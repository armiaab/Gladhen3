using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Gladhen3.Services;

/// <summary>An image encoded for a PDF image XObject.</summary>
/// <param name="Filter">The PDF filter for <paramref name="Data"/>, with its slash.</param>
/// <param name="Components">1 for DeviceGray, 3 for DeviceRGB.</param>
/// <param name="Predictor">PNG predictor applied before Deflate, or 0 (built-in lossless codec only).</param>
internal sealed record EncodedImage(
    byte[] Data,
    string Filter,
    int Width,
    int Height,
    int Components,
    int BitsPerComponent,
    int Predictor = 0);

/// <summary>Turns decoded pixels into a PDF image stream.</summary>
/// <remarks>One instance per build or estimate, so a plugin DLL is never held between operations.</remarks>
internal interface IImageCodec : IDisposable
{
    /// <summary>True when the output is a complete JPEG file PDFsharp can place directly.</summary>
    bool ProducesJpegFile { get; }

    /// <summary>Encodes a <see cref="BitmapPixelFormat.Bgra8"/> bitmap.</summary>
    /// <returns>The encoded image, or null to keep the original.</returns>
    EncodedImage? Encode(SoftwareBitmap bitmap, double quality);
}

/// <summary>One entry in the list of installed codec plugins.</summary>
/// <param name="LoadFailure">Why the plugin cannot be used, or null if it can.</param>
public sealed record ImageCodecInfo(string Id, string FileName, string? Name, string? Filter, CodecFailureReason? LoadFailure);

/// <summary>Image encoders: JPEG (WIC) and lossless Deflate built in, plus user codec DLLs.</summary>
public static class ImageCodecs
{
    public const string JpegId = "jpeg";
    public const string LosslessId = "flate";
    public const string PluginPrefix = "plugin:";

    private static string? _pluginDirectory;

    /// <summary>Folder for installed codec DLLs, in the app's local data.</summary>
    public static string PluginDirectory
    {
        get => _pluginDirectory ??= Path.Combine(ApplicationData.Current.LocalFolder.Path, "Codecs");
        internal set => _pluginDirectory = value;
    }

    public static bool IsPlugin(string? id) => id?.StartsWith(PluginPrefix, StringComparison.Ordinal) == true;

    /// <summary>Opens the codec with this id; an unknown id means JPEG.</summary>
    /// <exception cref="CodecException">The id names a plugin that cannot be loaded.</exception>
    internal static IImageCodec Open(string? id)
    {
        if (IsPlugin(id)) return NativeImageCodec.Load(PluginPath(id!));

        return id == LosslessId ? new LosslessCodec() : new JpegCodec();
    }

    /// <summary>Lists every DLL in <see cref="PluginDirectory"/>, with the reason for any that will not load.</summary>
    public static IReadOnlyList<ImageCodecInfo> ListPlugins()
    {
        var found = new List<ImageCodecInfo>();
        if (!Directory.Exists(PluginDirectory)) return found;

        var files = Directory.GetFiles(PluginDirectory, "*.dll");
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        foreach (var path in files)
        {
            var fileName = Path.GetFileName(path);
            var id = PluginPrefix + fileName;

            // One broken DLL must not hide the others.
            try
            {
                using var codec = NativeImageCodec.Load(path);
                found.Add(new ImageCodecInfo(id, fileName, codec.Name, codec.Filter, null));
            }
            catch (CodecException ex)
            {
                Log.Warning(ex, "Codec plugin {File} is installed but unusable ({Reason})", fileName, ex.Reason);
                found.Add(new ImageCodecInfo(id, fileName, null, null, ex.Reason));
            }
        }

        return found;
    }

    /// <summary>Copies a codec DLL into <see cref="PluginDirectory"/> and checks that it works.</summary>
    /// <remarks>The copy is what gets loaded, so moving the original is harmless; an unusable copy is removed.</remarks>
    /// <exception cref="CodecException">The file is not a usable codec.</exception>
    public static ImageCodecInfo Install(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var fileName = Path.GetFileName(sourcePath);
        var target = Path.Combine(PluginDirectory, fileName);

        if (File.Exists(target))
            throw new CodecException(CodecFailureReason.AlreadyInstalled, $"A codec named '{fileName}' is already installed.") { Path = target };

        Directory.CreateDirectory(PluginDirectory);
        File.Copy(sourcePath, target);

        try
        {
            using var codec = NativeImageCodec.Load(target);
            Log.Information("Codec plugin installed: {File} '{Name}' ({Filter})", fileName, codec.Name, codec.Filter);
            return new ImageCodecInfo(PluginPrefix + fileName, fileName, codec.Name, codec.Filter, null);
        }
        catch (CodecException)
        {
            File.Delete(target);
            throw;
        }
    }

    /// <exception cref="CodecException">The DLL is in use and could not be deleted.</exception>
    public static void Uninstall(string id)
    {
        var path = PluginPath(id);

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still loaded by a running save or estimate.
            throw new CodecException(CodecFailureReason.InUse, $"'{Path.GetFileName(path)}' is in use.", ex) { Path = path };
        }

        Log.Information("Codec plugin removed: {File}", Path.GetFileName(path));
    }

    /// <summary>The DLL a plugin id refers to.</summary>
    /// <remarks>The id comes from an editable settings file, so only a bare file name in the plugin folder is accepted.</remarks>
    private static string PluginPath(string id)
    {
        var fileName = id[PluginPrefix.Length..];
        if (fileName.Length == 0 || Path.GetFileName(fileName) != fileName ||
            !fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            throw new CodecException(CodecFailureReason.NotFound, $"'{id}' does not name a codec plugin.");
        }

        return Path.Combine(PluginDirectory, fileName);
    }

    // Built-in codecs.

    /// <summary>Baseline JPEG through WIC.</summary>
    private sealed class JpegCodec : IImageCodec
    {
        public bool ProducesJpegFile => true;

        public EncodedImage? Encode(SoftwareBitmap bitmap, double quality)
        {
            var jpeg = Wic.EncodeJpeg(bitmap, quality);
            return jpeg.Length == 0
                ? null
                : new EncodedImage(jpeg, "/DCTDecode", bitmap.PixelWidth, bitmap.PixelHeight, 3, 8);
        }

        public void Dispose()
        {
        }
    }

    /// <summary>Lossless: Deflate over PNG-predicted rows. Only the resolution changes.</summary>
    /// <remarks>For screenshots and line art. All-grey images are stored as one channel.</remarks>
    private sealed class LosslessCodec : IImageCodec
    {
        /// <summary>PNG predictor 15: each row carries its own filter.</summary>
        private const int PngOptimum = 15;

        public bool ProducesJpegFile => false;

        public EncodedImage? Encode(SoftwareBitmap bitmap, double quality)
        {
            var width = bitmap.PixelWidth;
            var height = bitmap.PixelHeight;

            var bgra = new byte[(long)width * height * 4];
            bitmap.CopyToBuffer(bgra.AsBuffer());

            var components = IsGrey(bgra) ? 1 : 3;
            var samples = new byte[(long)width * height * components];
            for (long s = 0, d = 0; s < bgra.LongLength; s += 4)
            {
                if (components == 1)
                {
                    samples[d++] = bgra[s];
                }
                else
                {
                    samples[d++] = bgra[s + 2];
                    samples[d++] = bgra[s + 1];
                    samples[d++] = bgra[s];
                }
            }

            var predicted = PngPredictor.Apply(samples, width * components, height, components);
            return new EncodedImage(PdfService.ZlibCompress(predicted), "/FlateDecode", width, height, components, 8, PngOptimum);
        }

        private static bool IsGrey(byte[] bgra)
        {
            for (long i = 0; i < bgra.LongLength; i += 4)
            {
                if (bgra[i] != bgra[i + 1] || bgra[i] != bgra[i + 2]) return false;
            }
            return true;
        }

        public void Dispose()
        {
        }
    }
}

/// <summary>PNG row filters, as PDF's /Predictor 10-15 expects before Deflate.</summary>
internal static class PngPredictor
{
    /// <summary>Filters each row with the PNG filter leaving the smallest residue, prefixed with its number.</summary>
    /// <remarks>"Smallest" is the sum of signed residues, libpng's heuristic.</remarks>
    public static byte[] Apply(byte[] samples, int rowBytes, int height, int bytesPerPixel)
    {
        var output = new byte[(long)(rowBytes + 1) * height];
        var previous = new byte[rowBytes];
        var current = new byte[rowBytes];
        var candidates = new byte[5][];
        for (var f = 0; f < candidates.Length; f++) candidates[f] = new byte[rowBytes];

        for (var y = 0; y < height; y++)
        {
            Array.Copy(samples, (long)y * rowBytes, current, 0, rowBytes);

            var best = 0;
            var bestScore = long.MaxValue;
            for (var filter = 0; filter < 5; filter++)
            {
                var row = candidates[filter];
                long score = 0;
                for (var i = 0; i < rowBytes; i++)
                {
                    int x = current[i];
                    int a = i >= bytesPerPixel ? current[i - bytesPerPixel] : 0;
                    int b = previous[i];
                    int c = i >= bytesPerPixel ? previous[i - bytesPerPixel] : 0;

                    var value = (byte)(filter switch
                    {
                        0 => x,
                        1 => x - a,
                        2 => x - b,
                        3 => x - ((a + b) >> 1),
                        _ => x - Paeth(a, b, c)
                    });

                    row[i] = value;

                    // Widen before Abs: Math.Abs(sbyte.MinValue) throws, and 0x80 residues are common.
                    score += Math.Abs((int)(sbyte)value);
                }

                if (score < bestScore)
                {
                    bestScore = score;
                    best = filter;
                }
            }

            var at = (long)y * (rowBytes + 1);
            output[at] = (byte)best;
            Array.Copy(candidates[best], 0, output, at + 1, rowBytes);

            (previous, current) = (current, previous);
        }

        return output;
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }
}

/// <summary>WIC encode helpers shared by every codec.</summary>
internal static class Wic
{
    /// <remarks>Quality must be set when the encoder is created; setting it later changes nothing.</remarks>
    public static byte[] EncodeJpeg(SoftwareBitmap bitmap, double quality)
    {
        var props = new BitmapPropertySet
        {
            { "ImageQuality", new BitmapTypedValue((float)quality, PropertyType.Single) }
        };
        return Encode(BitmapEncoder.JpegEncoderId, props, bitmap);
    }

    public static byte[] EncodePng(SoftwareBitmap bitmap) => Encode(BitmapEncoder.PngEncoderId, null, bitmap);

    private static byte[] Encode(Guid encoderId, BitmapPropertySet? props, SoftwareBitmap bitmap)
    {
        using var ras = new InMemoryRandomAccessStream();
        var encoder = (props == null
            ? BitmapEncoder.CreateAsync(encoderId, ras)
            : BitmapEncoder.CreateAsync(encoderId, ras, props)).GetAwaiter().GetResult();

        encoder.SetSoftwareBitmap(bitmap);
        encoder.FlushAsync().GetAwaiter().GetResult();
        return ReadAll(ras);
    }

    public static void WriteAll(InMemoryRandomAccessStream stream, byte[] data)
    {
        stream.WriteAsync(data.AsBuffer()).AsTask().GetAwaiter().GetResult();
        stream.Seek(0);
    }

    public static byte[] ReadAll(InMemoryRandomAccessStream stream)
    {
        var size = (uint)stream.Size;
        var bytes = new byte[size];
        if (size == 0) return bytes;

        stream.Seek(0);
        stream.ReadAsync(bytes.AsBuffer(), size, InputStreamOptions.None).AsTask().GetAwaiter().GetResult();
        return bytes;
    }
}
