using System;
using System.Collections.Frozen;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Gladhen3.Services;

/// <summary>
/// A codec implemented by a DLL the user installed, speaking the C interface in
/// CodecSdk/gladhen_codec.h.
/// </summary>
/// <remarks>
/// <para>
/// A plain C interface rather than a .NET one so that a codec can be built from whatever the
/// algorithm already exists in - most image codecs worth having are C libraries - without the
/// author needing this app's assemblies, or .NET at all.
/// </para>
/// <para>
/// The plugin runs inside this process. A fault in its code ends the app and there is no
/// catching it; what can be checked is the data it returns, and every stream is checked before
/// it is allowed near a page, because a malformed one would not fail here - it would produce a
/// PDF that fails later, in someone else's reader.
/// </para>
/// </remarks>
internal sealed unsafe class NativeImageCodec : IImageCodec
{
    /// <summary>The interface version this app implements; GLADHEN_CODEC_ABI in the header.</summary>
    public const uint AbiVersion = 1;

    private const int ResultEncoded = 0;
    private const int ResultSkip = 1;

    private const uint FlagThreadSafe = 1;

    private const int MaxNameBytes = 256;
    private const int MaxNameLength = 64;

    /// <summary>The filters a plugin may produce: those every PDF reader can decode without help.</summary>
    /// <remarks>
    /// CCITTFaxDecode is absent because it needs decode parameters this interface has no way
    /// to carry; LZWDecode because nothing is gained by it over Flate.
    /// </remarks>
    private static readonly FrozenSet<string> SupportedFilters = new[]
    {
        "/DCTDecode", "/JPXDecode", "/FlateDecode", "/JBIG2Decode", "/RunLengthDecode"
    }.ToFrozenSet(StringComparer.Ordinal);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeImage
    {
        public uint Width;
        public uint Height;
        public uint Stride;
        public uint Channels;
        public byte* Pixels;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeOutput
    {
        public byte* Data;
        public nuint Size;
        public uint Channels;
        public uint BitsPerComponent;
    }

    private readonly nint _library;
    private readonly delegate* unmanaged[Cdecl]<NativeImage*, float, NativeOutput*, int> _encode;
    private readonly delegate* unmanaged[Cdecl]<byte*, void> _free;

    /// <summary>Serialises calls into a plugin that has not said it is safe to call concurrently.</summary>
    private readonly object? _gate;

    private bool _disposed;

    private NativeImageCodec(nint library, string path)
    {
        _library = library;

        var abi = (delegate* unmanaged[Cdecl]<uint>)Export(library, path, "gladhen_codec_abi");
        var version = abi();
        if (version != AbiVersion)
        {
            throw new CodecException(CodecFailureReason.UnsupportedVersion,
                $"'{Path.GetFileName(path)}' implements codec interface {version}; this app implements {AbiVersion}.") { Path = path };
        }

        var name = (delegate* unmanaged[Cdecl]<byte*>)Export(library, path, "gladhen_codec_name");
        var filter = (delegate* unmanaged[Cdecl]<byte*>)Export(library, path, "gladhen_codec_filter");
        _encode = (delegate* unmanaged[Cdecl]<NativeImage*, float, NativeOutput*, int>)Export(library, path, "gladhen_codec_encode");
        _free = (delegate* unmanaged[Cdecl]<byte*, void>)Export(library, path, "gladhen_codec_free");

        var declared = ReadUtf8(filter()).Trim();
        Filter = declared.StartsWith('/') ? declared : "/" + declared;
        if (!SupportedFilters.Contains(Filter))
        {
            throw new CodecException(CodecFailureReason.UnsupportedFilter,
                $"'{Path.GetFileName(path)}' produces {Filter}, which this app does not write.") { Path = path };
        }

        var displayName = Sanitise(ReadUtf8(name()));
        Name = displayName.Length > 0 ? displayName : Path.GetFileNameWithoutExtension(path);

        uint flags = 0;
        if (NativeLibrary.TryGetExport(library, "gladhen_codec_flags", out var flagsExport))
            flags = ((delegate* unmanaged[Cdecl]<uint>)flagsExport)();

        _gate = (flags & FlagThreadSafe) != 0 ? null : new object();
    }

    /// <summary>What the plugin calls itself, for the compression-method list.</summary>
    public string Name { get; }

    /// <summary>The PDF filter its output decodes with, with the leading slash.</summary>
    public string Filter { get; }

    public bool ProducesJpegFile => false;

    /// <exception cref="CodecException">The DLL is missing, built for another architecture, or is not a codec.</exception>
    public static NativeImageCodec Load(string path)
    {
        if (!File.Exists(path))
            throw new CodecException(CodecFailureReason.NotFound, $"'{Path.GetFileName(path)}' is no longer installed.") { Path = path };

        nint library;
        try
        {
            library = NativeLibrary.Load(path);
        }
        catch (BadImageFormatException ex)
        {
            throw new CodecException(CodecFailureReason.WrongArchitecture,
                $"'{Path.GetFileName(path)}' is not a {RuntimeInformation.ProcessArchitecture} DLL.", ex) { Path = path };
        }
        catch (DllNotFoundException ex)
        {
            throw new CodecException(CodecFailureReason.LoadFailed, $"Windows could not load '{Path.GetFileName(path)}'.", ex) { Path = path };
        }

        try
        {
            return new NativeImageCodec(library, path);
        }
        catch
        {
            // Not a codec after all; nothing will ever call into it, so let it go now.
            NativeLibrary.Free(library);
            throw;
        }
    }

    /// <exception cref="CodecException">The plugin reported an error or returned data that is not this image.</exception>
    public EncodedImage? Encode(SoftwareBitmap bitmap, double quality)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;

        // ABI 1 always hands over 8-bit RGB, tightly packed and top-down: one format for every
        // plugin to handle, whatever the source was.
        var bgra = new byte[(long)width * height * 4];
        bitmap.CopyToBuffer(bgra.AsBuffer());
        var rgb = new byte[(long)width * height * 3];
        for (long s = 0, d = 0; s < bgra.LongLength; s += 4)
        {
            rgb[d++] = bgra[s + 2];
            rgb[d++] = bgra[s + 1];
            rgb[d++] = bgra[s];
        }

        var output = default(NativeOutput);
        int result;
        fixed (byte* pixels = rgb)
        {
            var image = new NativeImage
            {
                Width = (uint)width,
                Height = (uint)height,
                Stride = (uint)width * 3,
                Channels = 3,
                Pixels = pixels
            };

            if (_gate == null)
            {
                result = _encode(&image, (float)quality, &output);
            }
            else
            {
                lock (_gate) result = _encode(&image, (float)quality, &output);
            }
        }

        try
        {
            if (result == ResultSkip) return null;
            if (result != ResultEncoded)
                throw new CodecException(CodecFailureReason.EncodeFailed, $"'{Name}' failed with code {result} on a {width}x{height} image.");

            return Validate(output, width, height);
        }
        finally
        {
            if (output.Data != null) _free(output.Data);
        }
    }

    /// <summary>Copies out what the plugin produced, refusing anything that does not decode to this image.</summary>
    private EncodedImage Validate(NativeOutput output, int width, int height)
    {
        if (output.Data == null || output.Size == 0)
            throw Invalid("returned no data");

        // Generous: well past anything a real encoder produces, and far short of what would
        // take the process down when it is copied.
        var ceiling = ((long)width * height * 3 * 2) + (1L << 20);
        if (output.Size > (nuint)ceiling)
            throw Invalid($"returned {output.Size} bytes for a {width}x{height} image");

        var channels = (int)output.Channels;
        var bitsPerComponent = (int)output.BitsPerComponent;
        if (channels is not (1 or 3))
            throw Invalid($"reported {channels} channels");
        if (bitsPerComponent is not (1 or 2 or 4 or 8))
            throw Invalid($"reported {bitsPerComponent} bits per component");

        var data = new ReadOnlySpan<byte>(output.Data, checked((int)output.Size)).ToArray();
        var rowBytes = (((long)width * channels * bitsPerComponent) + 7) / 8;
        var expected = rowBytes * height;

        switch (Filter)
        {
            case "/DCTDecode":
                if (bitsPerComponent != 8) throw Invalid("returned a JPEG that is not 8 bits per component");
                CheckJpegSize(data, width, height);
                break;

            case "/JBIG2Decode":
                if (channels != 1 || bitsPerComponent != 1) throw Invalid("returned JBIG2 data that is not 1-bit grey");
                break;

            case "/FlateDecode":
                CheckLength(InflatedLength(data, expected), expected);
                break;

            case "/RunLengthDecode":
                CheckLength(RunLengthDecodedLength(data, expected), expected);
                break;

            default:
                // JPXDecode: nothing on Windows decodes JPEG 2000 to check it against.
                break;
        }

        return new EncodedImage(data, Filter, width, height, channels, bitsPerComponent);
    }

    private void CheckLength(long actual, long expected)
    {
        if (actual != expected) throw Invalid($"returned {actual} bytes of samples where the image needs {expected}");
    }

    private void CheckJpegSize(byte[] jpeg, int width, int height)
    {
        BitmapDecoder decoder;
        using var stream = new InMemoryRandomAccessStream();
        Wic.WriteAll(stream, jpeg);
        try
        {
            decoder = BitmapDecoder.CreateAsync(BitmapDecoder.JpegDecoderId, stream).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
            throw new CodecException(CodecFailureReason.EncodeFailed, $"'{Name}' returned data that is not a JPEG.", ex);
        }

        if (decoder.PixelWidth != width || decoder.PixelHeight != height)
            throw Invalid($"returned a {decoder.PixelWidth}x{decoder.PixelHeight} JPEG for a {width}x{height} image");
    }

    /// <summary>Inflates at most one byte more than the image needs, so a runaway stream cannot exhaust memory.</summary>
    private long InflatedLength(byte[] zlib, long expected)
    {
        var buffer = new byte[Math.Min(expected + 1, 1 << 20)];
        long total = 0;

        try
        {
            using var inflate = new ZLibStream(new MemoryStream(zlib, writable: false), CompressionMode.Decompress);
            int read;
            while (total <= expected && (read = inflate.Read(buffer, 0, buffer.Length)) > 0)
                total += read;
        }
        catch (InvalidDataException ex)
        {
            throw new CodecException(CodecFailureReason.EncodeFailed, $"'{Name}' returned data that is not a zlib stream.", ex);
        }

        return total;
    }

    /// <summary>
    /// The length a RunLengthDecode stream expands to, stopping just past what the image needs;
    /// -1 if a run claims more bytes than the stream holds.
    /// </summary>
    private static long RunLengthDecodedLength(byte[] data, long expected)
    {
        long total = 0;
        var i = 0;

        while (i < data.Length && total <= expected)
        {
            int length = data[i++];
            if (length == 128) return total; // end of data

            // A literal run copies length + 1 bytes; a repeat run reads one and writes it
            // 257 - length times.
            var consumed = length < 128 ? length + 1 : 1;
            if (i + consumed > data.Length) return -1;

            total += length < 128 ? length + 1 : 257 - length;
            i += consumed;
        }

        return total;
    }

    private CodecException Invalid(string what)
        => new(CodecFailureReason.EncodeFailed, $"'{Name}' {what}.");

    private static nint Export(nint library, string path, string name)
    {
        if (NativeLibrary.TryGetExport(library, name, out var address)) return address;

        throw new CodecException(CodecFailureReason.NotACodec,
            $"'{Path.GetFileName(path)}' does not export {name}.") { Path = path };
    }

    private static string ReadUtf8(byte* text)
    {
        if (text == null) return string.Empty;

        var length = 0;
        while (length < MaxNameBytes && text[length] != 0) length++;
        return Encoding.UTF8.GetString(text, length);
    }

    /// <summary>One line of plain text: control characters become spaces, and runs of them one space.</summary>
    private static string Sanitise(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            var visible = char.IsControl(c) || char.IsWhiteSpace(c) ? ' ' : c;
            if (visible == ' ' && (builder.Length == 0 || builder[^1] == ' ')) continue;
            builder.Append(visible);
        }

        var clean = builder.ToString().TrimEnd();
        return clean.Length > MaxNameLength ? clean[..MaxNameLength].TrimEnd() : clean;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        NativeLibrary.Free(_library);
    }
}
