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

/// <summary>A codec in a user-installed DLL, using the C interface in CodecSdk/gladhen_codec.h.</summary>
/// <remarks>
/// A C interface so codecs can be built from existing C libraries without .NET. The plugin
/// runs in-process, so a crash in it ends the app; its output is validated before use,
/// since a malformed stream would only fail later in someone's PDF reader.
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

    /// <summary>Filters a plugin may produce: those every PDF reader decodes.</summary>
    /// <remarks>No CCITTFax (needs decode parameters the interface cannot carry), no LZW (no gain over Flate).</remarks>
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

    /// <summary>Serialises calls into a plugin that is not declared thread-safe.</summary>
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

    /// <summary>The plugin's display name.</summary>
    public string Name { get; }

    /// <summary>The PDF filter of its output, with the leading slash.</summary>
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
            // Not a codec; release it now.
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

        // ABI 1 always passes 8-bit RGB, tightly packed, top-down.
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

    /// <summary>Copies out the plugin's output, rejecting anything that is not this image.</summary>
    private EncodedImage Validate(NativeOutput output, int width, int height)
    {
        if (output.Data == null || output.Size == 0)
            throw Invalid("returned no data");

        // Far above any real encoder's output, far below what copying could not survive.
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
                CheckHeaderSize(Jbig2PageSize(data), width, height, "JBIG2");
                break;

            case "/JPXDecode":
                CheckJpxHeader(data, width, height, channels);
                break;

            case "/FlateDecode":
                CheckLength(InflatedLength(data, expected), expected);
                break;

            case "/RunLengthDecode":
                CheckLength(RunLengthDecodedLength(data, expected), expected);
                break;

            default:
                throw Invalid($"uses {Filter}, which this app cannot check");
        }

        return new EncodedImage(data, Filter, width, height, channels, bitsPerComponent);
    }

    // Windows cannot decode JPEG 2000 or JBIG2, so those are checked against their headers only.

    private void CheckHeaderSize((long Width, long Height)? found, int width, int height, string format)
    {
        if (found is not { } size) throw Invalid($"returned data that is not a {format} stream");
        if (size.Width != width || size.Height != height)
            throw Invalid($"returned a {size.Width}x{size.Height} {format} image for a {width}x{height} image");
    }

    private void CheckJpxHeader(byte[] data, int width, int height, int channels)
    {
        var header = JpxHeader(data) ?? throw Invalid("returned data that is not JPEG 2000");
        CheckHeaderSize((header.Width, header.Height), width, height, "JPEG 2000");
        if (header.Components != channels)
            throw Invalid($"returned a {header.Components}-component JPEG 2000 image for {channels} channels");
    }

    private static uint ReadUInt32BigEndian(byte[] data, int offset)
        => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));

    private static ushort ReadUInt16BigEndian(byte[] data, int offset)
        => System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));

    /// <summary>Size and component count from a JP2 ihdr box or a raw codestream's SIZ marker.</summary>
    internal static (long Width, long Height, int Components)? JpxHeader(byte[] data)
    {
        // Raw codestream: SOC, SIZ; Xsiz, Ysiz, XOsiz, YOsiz follow Lsiz and Rsiz; Csiz after the tile fields.
        if (data.Length >= 40 && data[0] == 0xFF && data[1] == 0x4F && data[2] == 0xFF && data[3] == 0x51)
        {
            long xs = ReadUInt32BigEndian(data, 8), ys = ReadUInt32BigEndian(data, 12);
            long xo = ReadUInt32BigEndian(data, 16), yo = ReadUInt32BigEndian(data, 20);
            if (xo >= xs || yo >= ys) return null;
            return (xs - xo, ys - yo, ReadUInt16BigEndian(data, 38));
        }

        // JP2 file: boxes; jp2h contains ihdr (height, width, components).
        var offset = 0;
        while (offset + 8 <= data.Length)
        {
            long length = ReadUInt32BigEndian(data, offset);
            var type = ReadUInt32BigEndian(data, offset + 4);
            var body = offset + 8;

            if (length == 1)
            {
                if (offset + 16 > data.Length) return null;
                length = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offset + 8, 8));
                body = offset + 16;
            }
            else if (length == 0)
            {
                length = data.Length - offset;
            }

            if (length < body - offset || offset + length > data.Length) return null;

            if (type == 0x6A703268) // 'jp2h': a superbox, so step into it
            {
                offset = body;
                continue;
            }

            if (type == 0x69686472) // 'ihdr'
            {
                if (body + 10 > data.Length) return null;
                return (ReadUInt32BigEndian(data, body + 4), ReadUInt32BigEndian(data, body), ReadUInt16BigEndian(data, body + 8));
            }

            offset += (int)length;
        }

        return null;
    }

    /// <summary>The page size from the page-information segment opening an embedded JBIG2 stream.</summary>
    internal static (long Width, long Height)? Jbig2PageSize(byte[] data)
    {
        if (data.Length < 11) return null;

        // Bytes 0-3: segment number.
        var flags = data[4];
        var type = flags & 0x3F;
        var longPageAssociation = (flags & 0x40) != 0;

        // Page information: type 48, no referred-to segments.
        if (type != 48 || data[5] >> 5 != 0) return null;

        var offset = 6 + (longPageAssociation ? 4 : 1) + 4;
        if (offset + 8 > data.Length) return null;

        return (ReadUInt32BigEndian(data, offset), ReadUInt32BigEndian(data, offset + 4));
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

    /// <summary>Inflates at most one byte past what the image needs.</summary>
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

    /// <summary>The RunLengthDecode output length, stopping just past what the image needs; -1 if a run overruns the input.</summary>
    private static long RunLengthDecodedLength(byte[] data, long expected)
    {
        long total = 0;
        var i = 0;

        while (i < data.Length && total <= expected)
        {
            int length = data[i++];
            if (length == 128) return total; // end of data

            // Literal run: length + 1 bytes. Repeat run: one byte, 257 - length times.
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

    /// <summary>One line of plain text: control characters collapse to single spaces.</summary>
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
