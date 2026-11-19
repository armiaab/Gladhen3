using Serilog;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Gladhen3.Services;

/// <summary>
/// How files reach a running instance: a <c>gladhen2:</c> URI from the Explorer menu, or a
/// length-prefixed message from a second launch over a named pipe.
/// </summary>
internal static class ActivationProtocol
{
    /// <summary>Largest activation message accepted over the pipe.</summary>
    public const int MaxMessageBytes = 1024 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Reads one length-prefixed message, or null if it is malformed.</summary>
    /// <remarks>A pipe read can return a partial frame, so header and body are read exactly.</remarks>
    /// <exception cref="EndOfStreamException">The sender went away mid-message.</exception>
    public static async Task<List<string>?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);

        var length = BitConverter.ToInt32(header, 0);

        // A launch with no files sends an empty message.
        if (length == 0) return [];

        if (length < 0 || length > MaxMessageBytes)
        {
            Log.Warning("Ignoring an activation message of {Length} bytes", length);
            return null;
        }

        var body = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            await stream.ReadExactlyAsync(body.AsMemory(0, length), cancellationToken);

            string message;
            try
            {
                message = StrictUtf8.GetString(body, 0, length);
            }
            catch (DecoderFallbackException)
            {
                Log.Warning("Ignoring an activation message that is not valid UTF-8");
                return null;
            }

            // '|' cannot appear in a Windows path, so it is a safe separator.
            return [.. message.Split('|', StringSplitOptions.RemoveEmptyEntries)];
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(body);
        }
    }

    /// <summary>Writes the message <see cref="ReadAsync"/> reads.</summary>
    /// <exception cref="ArgumentException">The paths do not fit in one message.</exception>
    public static void Write(Stream stream, IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(paths);

        var bytes = Encoding.UTF8.GetBytes(string.Join("|", paths));
        if (bytes.Length > MaxMessageBytes)
            throw new ArgumentException($"{paths.Count} paths make a {bytes.Length}-byte message; the limit is {MaxMessageBytes}.", nameof(paths));

        stream.Write(BitConverter.GetBytes(bytes.Length));
        stream.Write(bytes);
        stream.Flush();
    }

    /// <summary>The paths in the <c>files=</c> parameter of a raw (still escaped) query.</summary>
    /// <remarks>
    /// The shell extension encodes each path and joins them with bare commas, so split first
    /// and decode each part once. UnescapeDataString, not UrlDecode, which turns '+' into a space.
    /// </remarks>
    public static List<string> ParseFilesQuery(string rawQuery)
    {
        ArgumentNullException.ThrowIfNull(rawQuery);

        var paths = new List<string>();

        foreach (var pair in rawQuery.TrimStart('?').Split('&'))
        {
            if (!pair.StartsWith("files=", StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var encoded in pair["files=".Length..].Split(','))
            {
                if (encoded.Length == 0) continue;

                var decoded = Uri.UnescapeDataString(encoded);
                if (decoded.Length > 0) paths.Add(decoded);
            }
        }

        return paths;
    }

    /// <summary>The paths in a <c>gladhen2:</c> activation URI, or none if it is malformed.</summary>
    public static List<string> ParseUri(string uriString)
    {
        // Anything can invoke a registered protocol, so bad input is expected, not exceptional.
        if (!Uri.TryCreate(uriString, UriKind.Absolute, out var uri))
        {
            Log.Warning("Ignoring a malformed activation URI");
            return [];
        }

        return ParseFilesQuery(uri.GetComponents(UriComponents.Query, UriFormat.UriEscaped));
    }
}
