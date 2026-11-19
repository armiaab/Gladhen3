using Gladhen3.Models;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Compression;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Gladhen3.Services;

public static class PdfService
{
    private const long InFlightBudgetBytes = 384L * 1024 * 1024;

    /// <summary>Most bytes any one stream is inflated to; beyond it the stream is left alone.</summary>
    /// <remarks>Deflate exceeds 1000:1 on repetitive input, so a small hostile stream can claim gigabytes.</remarks>
    internal const long MaxInflatedStreamBytes = 1L << 30;

    /// <summary>Most pixels a decode may produce: 16384², a gigabyte as BGRA.</summary>
    internal const long MaxDecodedPixels = 16384L * 16384;

    internal static int GetRasterDpi() => AppSettings.Current.ImageCompression switch
    {
        PdfImageCompression.Low => 300,
        PdfImageCompression.Medium => 150,
        PdfImageCompression.High => 96,
        _ => 0
    };

    internal static double GetJpegQuality() => AppSettings.Current.ImageCompression switch
    {
        PdfImageCompression.Low => 0.85,
        PdfImageCompression.Medium => 0.70,
        PdfImageCompression.High => 0.50,
        _ => 0.92
    };

    private static int LanesFor(long worstBytesPerImage)
    {
        if (worstBytesPerImage <= 0) return 1;
        return Math.Clamp((int)(InFlightBudgetBytes / worstBytesPerImage), 1, Environment.ProcessorCount);
    }

    /// <summary>Writes <paramref name="items"/> to <paramref name="outputPath"/>.</summary>
    /// <param name="settings">Settings pinned for the whole build; the current ones if null.</param>
    public static PdfBuildResult CreatePdfFromDocuments(List<DocumentItem> items, string outputPath, AppSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        if (items.Any(i => i.Type == DocumentType.SectionBreak))
            throw new ArgumentException("Section dividers must be removed before building a PDF.", nameof(items));

        using var pin = AppSettings.Pin(settings ?? AppSettings.Current);

        EnsureFileIsWritable(outputPath);
        return BuildPdf(items, outputPath);
    }

    private static PdfBuildResult BuildPdf(List<DocumentItem> items, string outputPath)
    {
        var level = AppSettings.Current.ImageCompression;
        var compress = level != PdfImageCompression.None;
        var useCustomPageSize = AppSettings.Current.PaperSize != PdfPaperSize.Automatic;
        var dpi = GetRasterDpi();
        var quality = GetJpegQuality();

        Log.Information("PDF build start: mode={Mode} codec={Codec} quality={Q}% dpi={Dpi} customPageSize={Custom} items={Count}",
            level, compress ? AppSettings.Current.ImageCodec : "-", Math.Round(quality * 100), dpi, useCustomPageSize, items.Count);

        using var codec = compress ? OpenSelectedCodec(outputPath) : null;

        var inPlaceSource = WholeDocumentSource(items, useCustomPageSize);
        if (inPlaceSource != null && CompressInPlace(inPlaceSource, outputPath, items, codec, dpi, quality, out var inPlaceFailures))
            return new PdfBuildResult(items.Count, [], inPlaceFailures);

        using var output = new PdfDocument();
        output.Options.CompressContentStreams = false; // done in TerminateFlateStreams, correctly
        output.Options.NoCompression = false;
        output.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;
        output.Options.EnableCcittCompressionForBilevelImages = true;

        var sourceDocs = new Dictionary<string, PdfDocument>(StringComparer.OrdinalIgnoreCase);
        var sourceForms = new Dictionary<string, XPdfForm>(StringComparer.OrdinalIgnoreCase);
        var alreadyOptimised = new HashSet<int>();

        // Where each source page landed, for re-pointing bookmarks. Keyed by source and page
        // number, since object ids can repeat across documents.
        var importedPages = new Dictionary<(string Source, int PageNumber), ImportedPage>();
        var imagePagesAdded = 0;
        var pagesAdded = 0;
        var skipped = new List<string>();

        var pageLongInches = useCustomPageSize ? MaxPageLongEdgeInches() : 11.0;
        var cap = dpi > 0 ? (int)Math.Ceiling(pageLongInches * dpi) : 0;
        var assumedSourcePx = Math.Max((long)cap * cap, 12_000_000L);
        var lanes = compress
            ? LanesFor((assumedSourcePx * 4) + ((long)cap * cap * 8))
            : LanesFor(assumedSourcePx * 8);
        var block = compress ? Math.Max(lanes * 4, 32) : 32;

        // Larger than the worker count: the bounded channel keeps several encodes going while
        // the lane count caps decoded-image memory.

        try
        {
            for (var start = 0; start < items.Count; start += block)
            {
                var end = Math.Min(items.Count, start + block);

                var encoded = EmptyEncoded();
                var paths = new List<string>();
                for (var k = start; k < end; k++)
                {
                    if (items[k].Type != DocumentType.Image) continue;
                    if (!compress && CanPlaceDirectly(items[k].FilePath)) continue;
                    if (!paths.Contains(items[k].FilePath, StringComparer.OrdinalIgnoreCase))
                        paths.Add(items[k].FilePath);
                }
                if (paths.Count > 0)
                    encoded = PreEncodeImages(paths, cap, quality, lanes, codec);

                for (var k = start; k < end; k++)
                {
                    var item = items[k];
                    try
                    {
                        if (item.Type == DocumentType.Image)
                        {
                            if (AddImagePage(output, item.FilePath, encoded, useCustomPageSize, item.Rotation))
                                alreadyOptimised.Add(output.PageCount - 1);
                            imagePagesAdded++;
                            pagesAdded++;
                        }
                        else if (item.Type == DocumentType.BlankPage)
                        {
                            AddBlankPage(output, useCustomPageSize, item.Rotation);
                            pagesAdded++;
                        }
                        else if (item.Type == DocumentType.PdfPage)
                        {
                            var src = item.SourcePdfPath ?? item.FilePath;
                            if (string.IsNullOrEmpty(src))
                            {
                                skipped.Add(item.FileName);
                                continue;
                            }

                            if (!sourceDocs.TryGetValue(src, out var srcDoc))
                            {
                                srcDoc = PdfReader.Open(src, PdfDocumentOpenMode.Import);
                                sourceDocs[src] = srcDoc;
                            }

                            var idx = item.PageNumber - 1;
                            if (idx < 0 || idx >= srcDoc.PageCount)
                            {
                                Log.Warning("Page {Page} is outside {Path} ({Count} pages)", item.PageNumber, src, srcDoc.PageCount);
                                skipped.Add(item.FileName);
                                continue;
                            }

                            // If a page was added twice, bookmarks point at the later copy.
                            importedPages[(src, item.PageNumber)] =
                                AddPdfPage(output, srcDoc.Pages[idx], useCustomPageSize, src, item.PageNumber, item.Rotation, sourceForms);
                            pagesAdded++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Failed to add item to PDF: {Path}", item.FilePath);
                        skipped.Add(item.FileName);
                    }
                }

                encoded.Clear();
            }

            // Inside the try: both read the source documents, which the finally closes.
            CopyOutlines(output, sourceDocs, importedPages);
            CopyDocumentInfo(output, sourceDocs, imagePagesAdded == 0 && sourceDocs.Count == 1);
        }
        finally
        {
            DisposeAll(sourceForms.Values);
            DisposeAll(sourceDocs.Values);
        }

        if (pagesAdded == 0)
            throw new PdfOperationException(PdfFailureReason.NoPages, "None of the selected items could be turned into a page.") { Path = outputPath };

        var codecFailures = codec != null ? CompressDocumentImages(output, dpi, quality, codec, alreadyOptimised) : 0;

        SaveDocument(output, outputPath);

        var finalSize = new FileInfo(outputPath).Length;
        Log.Information("PDF saved: {Path} pages={Pages} skipped={Skipped} codecFailures={CodecFailures} size={Size}B",
            outputPath, pagesAdded, skipped.Count, codecFailures, finalSize);

        return new PdfBuildResult(pagesAdded, skipped, codecFailures);
    }

    /// <summary>Opens the compression method chosen in Settings.</summary>
    /// <exception cref="PdfOperationException">It is a plugin that will not load.</exception>
    private static IImageCodec OpenSelectedCodec(string? outputPath)
    {
        var id = AppSettings.Current.ImageCodec;
        try
        {
            return ImageCodecs.Open(id);
        }
        catch (CodecException ex)
        {
            // Never silently fall back to JPEG for a method the user chose.
            throw new PdfOperationException(PdfFailureReason.CodecUnavailable, $"The compression method '{id}' could not be loaded.", ex)
            {
                Path = outputPath,
                CodecReason = ex.Reason
            };
        }
    }

    /// <summary>Writes beside <paramref name="outputPath"/>, then replaces it.</summary>
    /// <remarks>
    /// A failed save never leaves a truncated file, which matters when saving over the only
    /// copy. File.Replace also keeps the original's creation time and permissions.
    /// </remarks>
    private static void SaveDocument(PdfDocument document, string outputPath)
    {
        TerminateFlateStreams(document);

        var fullPath = Path.GetFullPath(outputPath);
        var temporary = Path.Combine(Path.GetDirectoryName(fullPath)!, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            document.Save(temporary);

            if (File.Exists(fullPath))
                File.Replace(temporary, fullPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else
                File.Move(temporary, fullPath);
        }
        catch (IOException ex)
        {
            throw new PdfOperationException(PdfFailureReason.FileInUse, $"'{Path.GetFileName(outputPath)}' could not be written.", ex) { Path = outputPath };
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new PdfOperationException(PdfFailureReason.AccessDenied, $"'{Path.GetFileName(outputPath)}' could not be written.", ex) { Path = outputPath };
        }
        finally
        {
            DeleteIfPresent(temporary);
        }
    }

    /// <summary>Deletes a leftover temp file without masking the original failure.</summary>
    private static void DeleteIfPresent(string path)
    {
        if (!File.Exists(path)) return;

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Could not remove the temporary file {Path}", path);
        }
    }

    /// <summary>Appends the zlib Adler-32 trailer that PDFsharp omits from Flate streams.</summary>
    /// <remarks>
    /// PDFsharp 6.2.4 writes the header and deflate data but not the trailer. Strict readers
    /// (MuPDF) report "premature end of data" once per stream. Appending four bytes keeps
    /// PDFsharp's compression as is; streams that already end correctly are untouched.
    /// </remarks>
    private static void TerminateFlateStreams(PdfDocument document)
    {
        var repaired = 0;
        var compressed = 0;

        foreach (var obj in document.Internals.GetAllObjects())
        {
            if (obj is not PdfDictionary dict || dict.Stream == null) continue;

            // Unencoded: PDFsharp would deflate it during Save without the trailer, so compress it
            // here instead (this is why CompressContentStreams is off).
            if (!dict.Elements.ContainsKey("/Filter"))
            {
                // XMP stays uncompressed by convention.
                if (dict.Elements.GetString("/Type") == "/Metadata") continue;

                var plain = dict.Stream.Value;
                if (plain is not { Length: > 0 }) continue;

                byte[] deflated;
                try
                {
                    deflated = ZlibCompress(plain);
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Leaving a {Length}-byte stream uncompressed", plain.Length);
                    continue;
                }

                // Short streams grow when wrapped in zlib.
                if (deflated.Length >= plain.Length) continue;

                dict.Stream.Value = deflated;
                dict.Elements.SetName("/Filter", "/FlateDecode");
                dict.Elements.SetInteger("/Length", deflated.Length);
                compressed++;
                continue;
            }

            // A single /FlateDecode only; filter chains are left alone.
            if (dict.Elements.GetName("/Filter") != "/FlateDecode") continue;

            var raw = dict.Stream.Value;
            if (raw is not { Length: > 2 }) continue;

            // zlib header: method 8 (deflate), and the two bytes a multiple of 31.
            if ((raw[0] & 0x0F) != 8 || (((raw[0] << 8) | raw[1]) % 31) != 0) continue;

            uint checksum;
            try
            {
                // Checksum while streaming; the payload is never held in memory.
                if (!TryAdler32OfInflated(raw, MaxInflatedStreamBytes, out checksum))
                {
                    Log.Warning("Leaving a {Length}-byte flate stream alone: it inflates past {Limit} bytes", raw.Length, MaxInflatedStreamBytes);
                    continue;
                }
            }
            // Anything that is not plain zlib over complete deflate data is left as is.
            catch (Exception ex)
            {
                Log.Debug(ex, "Leaving a {Length}-byte flate stream alone", raw.Length);
                continue;
            }

            // Already terminated.
            if (raw.Length >= 6 &&
                raw[^4] == (byte)(checksum >> 24) && raw[^3] == (byte)(checksum >> 16) &&
                raw[^2] == (byte)(checksum >> 8) && raw[^1] == (byte)checksum)
            {
                continue;
            }

            var terminated = new byte[raw.Length + 4];
            System.Buffer.BlockCopy(raw, 0, terminated, 0, raw.Length);
            terminated[raw.Length] = (byte)(checksum >> 24);
            terminated[raw.Length + 1] = (byte)(checksum >> 16);
            terminated[raw.Length + 2] = (byte)(checksum >> 8);
            terminated[raw.Length + 3] = (byte)checksum;

            dict.Stream.Value = terminated;
            dict.Elements.SetInteger("/Length", terminated.Length);
            repaired++;
        }

        if (repaired > 0 || compressed > 0)
            Log.Debug("Streams finalised: {Compressed} compressed, {Repaired} terminated", compressed, repaired);
    }

    internal static byte[] ZlibCompress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(data, 0, data.Length);

        return output.ToArray();
    }

    /// <summary>Inflates the deflate data after a two-byte zlib header, passing each chunk to <paramref name="sink"/>.</summary>
    /// <returns>The inflated length, or -1 once it passes <paramref name="limit"/>.</returns>
    internal static long InflateBounded(byte[] zlib, long limit, ChunkSink? sink)
    {
        using var input = new MemoryStream(zlib, 2, zlib.Length - 2, writable: false);
        using var inflate = new DeflateStream(input, CompressionMode.Decompress);

        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long total = 0;
            int read;
            while ((read = inflate.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > limit) return -1;
                sink?.Invoke(buffer.AsSpan(0, read));
            }
            return total;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Receives inflated bytes a chunk at a time.</summary>
    internal delegate void ChunkSink(ReadOnlySpan<byte> chunk);

    /// <summary>The Adler-32 of a zlib stream's payload, or false if it inflates past <paramref name="limit"/>.</summary>
    internal static bool TryAdler32OfInflated(byte[] zlib, long limit, out uint checksum)
    {
        var adler = new Adler32State();
        var length = InflateBounded(zlib, limit, adler.Update);
        checksum = adler.Value;
        return length >= 0;
    }

    /// <summary>Adler-32 of uncompressed bytes (RFC 1950 trailer).</summary>
    internal static uint Adler32(byte[] data)
    {
        var adler = new Adler32State();
        adler.Update(data);
        return adler.Value;
    }

    /// <summary>Adler-32, fed a chunk at a time.</summary>
    private sealed class Adler32State
    {
        private const uint Modulus = 65521;
        // Longest run before the accumulators could overflow.
        private const int MaxRun = 5552;

        private uint _a = 1, _b;

        public uint Value => (_b << 16) | _a;

        public void Update(ReadOnlySpan<byte> data)
        {
            uint a = _a, b = _b;
            var offset = 0;

            while (offset < data.Length)
            {
                var run = Math.Min(MaxRun, data.Length - offset);
                for (var i = 0; i < run; i++)
                {
                    a += data[offset + i];
                    b += a;
                }
                a %= Modulus;
                b %= Modulus;
                offset += run;
            }

            _a = a;
            _b = b;
        }
    }

    private static void DisposeAll<T>(IEnumerable<T> disposables) where T : IDisposable
    {
        foreach (var item in disposables)
        {
            try
            {
                item.Dispose();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Ignoring a failure disposing {Type} during cleanup", typeof(T).Name);
            }
        }
    }

    private static Dictionary<string, PreEncodedImage> EmptyEncoded()
        => new(StringComparer.OrdinalIgnoreCase);

    /// <summary>An image file re-encoded into something PDFsharp can place.</summary>
    /// <param name="Final">
    /// False for a lossless stand-in, used when the chosen codec's output is not a file
    /// PDFsharp reads; the document-wide pass encodes it for real.
    /// </param>
    private sealed record PreEncodedImage(byte[] Bytes, bool Final);

    private const long PdfOverheadBytes = 2048;

    private const int MaxSizeSamples = 3;

    private const long SampleParseLimitBytes = 256L * 1024 * 1024;

    public static long EstimateOutputSize(IReadOnlyList<DocumentItem> items, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0) return 0;

        return EstimateSectionSizes([items], null, cancellationToken)[0];
    }

    public static IReadOnlyList<long> EstimateSectionSizes(
        IReadOnlyList<IReadOnlyList<DocumentItem>> sections,
        AppSettings? settings = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sections);
        if (sections.Count == 0) return [];

        using var pin = AppSettings.Pin(settings ?? AppSettings.Current);

        var compress = AppSettings.Current.ImageCompression != PdfImageCompression.None;
        var dpi = GetRasterDpi();
        var quality = GetJpegQuality();
        var useCustomPageSize = AppSettings.Current.PaperSize != PdfPaperSize.Automatic;
        var pageLongInches = useCustomPageSize ? MaxPageLongEdgeInches() : 11.0;
        var cap = dpi > 0 ? (int)Math.Ceiling(pageLongInches * dpi) : 0;

        var imagePaths = new List<string>();
        var pdfPages = new Dictionary<string, (SortedSet<int> Pages, int TotalPages)>(StringComparer.OrdinalIgnoreCase);

        foreach (var section in sections)
        {
            if (section == null) continue;

            foreach (var item in section)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (item.Type == DocumentType.Image)
                {
                    if (!imagePaths.Contains(item.FilePath, StringComparer.OrdinalIgnoreCase))
                        imagePaths.Add(item.FilePath);
                }
                else if (item.Type == DocumentType.PdfPage)
                {
                    var src = item.SourcePdfPath ?? item.FilePath;
                    if (string.IsNullOrEmpty(src)) continue;

                    if (!pdfPages.TryGetValue(src, out var group))
                    {
                        group = ([], Math.Max(1, item.TotalPages));
                        pdfPages[src] = group;
                    }
                    group.Pages.Add(item.PageNumber);
                }
            }
        }

        // Measure with the method that will be used; that is what the estimate is for.
        using var codec = compress ? OpenSelectedCodec(null) : null;

        var imageRatio = codec != null ? MeasureImageRatio(imagePaths, cap, quality, codec, cancellationToken) : 1.0;

        var profiles = new Dictionary<string, SourceProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, group) in pdfPages)
            profiles[path] = ProfileSource(path, group.Pages, group.TotalPages, codec, dpi, quality, cancellationToken);

        var results = new long[sections.Count];
        for (var i = 0; i < sections.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var section = sections[i];
            if (section == null || section.Count == 0)
            {
                results[i] = 0;
                continue;
            }

            var total = PdfOverheadBytes;
            long imageBytes = 0;
            var seenImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pagesBySource = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in section)
            {
                if (item.Type == DocumentType.Image)
                {
                    if (seenImages.Add(item.FilePath))
                        imageBytes += FileLengthOrZero(item.FilePath);
                }
                else if (item.Type == DocumentType.PdfPage)
                {
                    var src = item.SourcePdfPath ?? item.FilePath;
                    if (string.IsNullOrEmpty(src)) continue;

                    if (!pagesBySource.TryGetValue(src, out var pages))
                    {
                        pages = [];
                        pagesBySource[src] = pages;
                    }
                    pages.Add(item.PageNumber);
                }
            }

            total += (long)(imageBytes * imageRatio);

            foreach (var (path, pages) in pagesBySource)
            {
                if (profiles.TryGetValue(path, out var profile))
                    total += EstimateFromProfile(profile, pages, compress);
            }

            results[i] = total;
        }

        return results;
    }

    private static long FileLengthOrZero(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return 0;

        try
        {
            return info.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug(ex, "Skipping {Path} while estimating: its size could not be read", path);
            return 0;
        }
    }

    private static double MeasureImageRatio(List<string> paths, int cap, double quality, IImageCodec codec, CancellationToken cancellationToken)
    {
        if (paths.Count == 0) return 1.0;

        var sizes = new List<(string Path, long Length)>(paths.Count);
        foreach (var path in paths)
        {
            var length = FileLengthOrZero(path);
            if (length > 0) sizes.Add((path, length));
        }

        double ratioSum = 0;
        var sampled = 0;
        foreach (var entry in sizes.OrderByDescending(e => e.Length).Take(MaxSizeSamples))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var encodedLength = FinalEncodedLength(entry.Path, cap, quality, codec);
                if (encodedLength > 0)
                {
                    ratioSum += Math.Min(1.0, (double)encodedLength / entry.Length);
                    sampled++;
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Could not sample {Path} while estimating", entry.Path);
            }
        }

        return sampled > 0 ? ratioSum / sampled : 1.0;
    }

    private sealed class SourceProfile
    {
        public long FileLength { get; init; }
        public int TotalPages { get; init; }
        public double ImageRatio { get; init; } = 1.0;
        public Dictionary<int, long> PageImageBytes { get; init; } = [];
        public bool Measured { get; init; }
    }

    private static SourceProfile ProfileSource(
        string path,
        IReadOnlyCollection<int> pageNumbers,
        int totalPages,
        IImageCodec? codec,
        int dpi,
        double quality,
        CancellationToken cancellationToken)
    {
        var fileLength = FileLengthOrZero(path);
        var unmeasured = new SourceProfile { FileLength = fileLength, TotalPages = totalPages };

        if (fileLength == 0 || codec == null) return unmeasured;

        if (fileLength > SampleParseLimitBytes)
        {
            Log.Debug("Estimating {Path} from a rule of thumb: {Size}B is past the parse limit", path, fileLength);
            return unmeasured;
        }

        try
        {
            using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);

            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var jobs = new List<ImageJob>();
            var pageImageBytes = new Dictionary<int, long>();

            foreach (var pageNumber in pageNumbers)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var index = pageNumber - 1;
                if (index < 0 || index >= doc.PageCount) continue;

                var page = doc.Pages[index];
                var longInches = Math.Max(page.Width.Inch, page.Height.Inch);
                if (longInches <= 0) longInches = 11.0;

                var before = jobs.Count;
                CollectImages(page.Elements.GetDictionary("/Resources") ?? page.Resources, longInches, seen, jobs, 0);

                long added = 0;
                for (var j = before; j < jobs.Count; j++) added += jobs[j].OriginalLength;
                pageImageBytes[pageNumber] = added;
            }

            long sampledBefore = 0, sampledAfter = 0;
            foreach (var job in jobs.OrderByDescending(j => j.OriginalLength).Take(MaxSizeSamples))
            {
                cancellationToken.ThrowIfCancellationRequested();

                job.Prepare();
                try
                {
                    job.Recompress(dpi, quality, codec);
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Could not sample an image while estimating {Path}", path);
                }

                sampledBefore += job.OriginalLength;
                sampledAfter += job.Encoded is { Data.Length: > 0 } encoded && encoded.Data.Length < job.OriginalLength
                    ? encoded.Data.Length
                    : job.OriginalLength;
                job.Release();
            }

            var profile = new SourceProfile
            {
                FileLength = fileLength,
                TotalPages = totalPages,
                ImageRatio = sampledBefore > 0 ? (double)sampledAfter / sampledBefore : 1.0,
                PageImageBytes = pageImageBytes,
                Measured = true
            };

            if (fileLength > 32L * 1024 * 1024)
            {
                doc.Dispose();
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
                GC.WaitForPendingFinalizers();
            }

            return profile;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Debug(ex, "Falling back to a rule of thumb for {Path}", path);
            return unmeasured;
        }
    }

    private static long EstimateFromProfile(SourceProfile profile, List<int> pageNumbers, bool compress)
    {
        if (profile.FileLength == 0 || pageNumbers.Count == 0) return 0;

        var selectedFraction = profile.TotalPages > 0
            ? Math.Min(1.0, (double)pageNumbers.Count / profile.TotalPages)
            : 1.0;
        var selectedBytes = profile.FileLength * selectedFraction;

        if (!compress) return (long)selectedBytes;
        if (!profile.Measured) return (long)(selectedBytes * FallbackCompressionRatio());

        long imageBytes = 0;
        var counted = new HashSet<int>();
        foreach (var pageNumber in pageNumbers)
        {
            if (counted.Add(pageNumber) && profile.PageImageBytes.TryGetValue(pageNumber, out var bytes))
                imageBytes += bytes;
        }

        if (imageBytes == 0)
        {
            return (long)selectedBytes;
        }

        var nonImageBytes = Math.Max(0, selectedBytes - imageBytes);
        return (long)(nonImageBytes + (imageBytes * profile.ImageRatio));
    }

    private static double FallbackCompressionRatio() => AppSettings.Current.ImageCompression switch
    {
        PdfImageCompression.Low => 0.60,
        PdfImageCompression.Medium => 0.30,
        PdfImageCompression.High => 0.15,
        _ => 1.0
    };

    private static string? WholeDocumentSource(List<DocumentItem> items, bool useCustomPageSize)
    {
        if (useCustomPageSize || AppSettings.Current.Orientation != PdfPaperOrientation.Automatic) return null;
        if (items.Count == 0) return null;

        string? src = null;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.Type != DocumentType.PdfPage) return null;
            if (item.PageNumber != i + 1) return null;

            var path = item.SourcePdfPath ?? item.FilePath;
            if (string.IsNullOrEmpty(path)) return null;

            if (src == null) src = path;
            else if (!string.Equals(src, path, StringComparison.OrdinalIgnoreCase)) return null;
        }
        return src;
    }

    private static bool CompressInPlace(
        string sourcePath,
        string outputPath,
        List<DocumentItem> items,
        IImageCodec? codec,
        int dpi,
        double quality,
        out int codecFailures)
    {
        codecFailures = 0;

        PdfDocument? doc = null;
        try
        {
            try
            {
                doc = PdfReader.Open(sourcePath, PdfDocumentOpenMode.Modify);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "In-place rewrite unavailable for {Path}; importing pages instead", sourcePath);
                return false;
            }

            if (doc.PageCount != items.Count) return false;

            doc.Options.CompressContentStreams = false; // done in TerminateFlateStreams, correctly
            doc.Options.NoCompression = false;
            doc.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;
            doc.Options.EnableCcittCompressionForBilevelImages = true;

            // A turn is just a /Rotate entry, so annotations, links and forms come through intact.
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i].Rotation != 0) TurnPage(doc.Pages[i], items[i].Rotation);
            }

            if (codec != null)
                codecFailures = CompressDocumentImages(doc, dpi, quality, codec);

            var pages = doc.PageCount;

            SaveDocument(doc, outputPath);

            Log.Information("PDF saved (in place): {Path} pages={Pages} size={Size}B",
                outputPath, pages, new FileInfo(outputPath).Length);
            return true;
        }
        finally
        {
            if (doc is not null) DisposeAll([doc]);
        }
    }

    /// <returns>True when the image went in already in its final, compressed form.</returns>
    private static bool AddImagePage(PdfDocument output, string imagePath, Dictionary<string, PreEncodedImage> encoded, bool useCustomPageSize, int rotation)
    {
        XImage image;
        MemoryStream? owned = null;
        var final = false;

        // Re-encoded images are already upright; an original goes in as stored.
        var stored = ExifTurn.None;
        if (encoded.TryGetValue(imagePath, out var pre) && pre.Bytes.Length > 0)
        {
            owned = new MemoryStream(pre.Bytes, writable: false);
            image = XImage.FromStream(owned);
            final = pre.Final;
        }
        else
        {
            image = XImage.FromFile(imagePath);
            stored = ExifTurn.Read(imagePath);
        }

        try
        {
            var imgW = image.PixelWidth;
            var imgH = image.PixelHeight;
            var frame = PageFrame.For(imgW, imgH, DocumentItem.NormaliseRotation(stored.Turn + rotation), useCustomPageSize);

            var page = output.AddPage();
            frame.Size(page);

            using (var gfx = XGraphics.FromPdfPage(page))
            {
                var (left, top, drawW, drawH, _) = frame.Fit(imgW, imgH, useCustomPageSize);
                if (stored.Mirror) gfx.ScaleAtTransform(-1, 1, left + (drawW / 2), top);
                gfx.DrawImage(image, left, top, drawW, drawH);
            }

            frame.Turn(page);
            return final;
        }
        finally
        {
            image.Dispose();
            owned?.Dispose();
        }
    }

    /// <summary>Adds an empty page sized and turned like the one before it.</summary>
    /// <remarks>With a paper size set in Settings, every page is that size anyway.</remarks>
    private static void AddBlankPage(PdfDocument output, bool useCustomPageSize, int rotation)
    {
        double width, height;
        var turn = rotation;

        if (useCustomPageSize)
        {
            (width, height) = GetCustomPageSizePoints(ResolveTargetLandscape(false));
        }
        else if (output.PageCount > 0)
        {
            var previous = output.Pages[output.PageCount - 1];
            width = previous.Width.Point;
            height = previous.Height.Point;
            turn += SourceRotation(previous);
        }
        else
        {
            var (w, h) = PageDimensions.A4;
            (width, height) = ResolveTargetLandscape(false) ? (h, w) : (w, h);
        }

        var page = output.AddPage();
        page.Width = XUnit.FromPoint(width);
        page.Height = XUnit.FromPoint(height);

        turn = DocumentItem.NormaliseRotation(turn);
        if (turn != 0) page.Elements.SetInteger("/Rotate", turn);
    }

    /// <summary>Adds one source page to <paramref name="output"/>.</summary>
    /// <returns>The new page and how the source's coordinate space was mapped onto it.</returns>
    private static ImportedPage AddPdfPage(PdfDocument output, PdfPage sourcePage, bool useCustomPageSize, string sourcePath, int pageNumber, int rotation, Dictionary<string, XPdfForm> formCache)
    {
        if (!useCustomPageSize && AppSettings.Current.Orientation == PdfPaperOrientation.Automatic)
        {
            // Imported whole: its coordinate space is untouched, and turning it is one /Rotate entry.
            var imported = output.AddPage(sourcePage);
            if (rotation != 0) TurnPage(imported, rotation);
            return new ImportedPage(imported, PageTransform.Identity);
        }

        // Lay out as seen (own /Rotate plus the user's turn), draw unturned in the page's own frame,
        // and let the new /Rotate turn it, so coordinates stay in the source's user space where
        // bookmark destinations live.
        var srcW = sourcePage.Width.Point;
        var srcH = sourcePage.Height.Point;
        var turn = DocumentItem.NormaliseRotation(SourceRotation(sourcePage) + rotation);
        var frame = PageFrame.For(srcW, srcH, turn, useCustomPageSize);

        var page = output.AddPage();
        frame.Size(page);

        if (!formCache.TryGetValue(sourcePath, out var form))
        {
            form = XPdfForm.FromFile(sourcePath);
            formCache[sourcePath] = form;
        }
        form.PageNumber = pageNumber;

        var (left, top, drawW, drawH, scale) = frame.Fit(srcW, srcH, useCustomPageSize);
        using (var gfx = XGraphics.FromPdfPage(page))
            gfx.DrawImage(form, left, top, drawW, drawH);

        if (SourceRotation(sourcePage) != 0) ClearFormRotation(page);
        frame.Turn(page);

        // Content was scaled and centred, so destinations move with it. XGraphics measures from the
        // top, PDF from the bottom, hence the flip.
        return new ImportedPage(page, new PageTransform(scale, left, frame.BoxHeight - top - drawH));
    }

    // Page rotation.

    /// <summary>How a photo's EXIF tag says to stand it up: mirrored or not, then turned clockwise.</summary>
    /// <remarks>
    /// Re-encoded photos are decoded upright; a photo placed as stored must be turned to match.
    /// Turning the page leaves the JPEG bytes alone.
    /// </remarks>
    private readonly record struct ExifTurn(int Turn, bool Mirror)
    {
        public static ExifTurn None => default;

        /// <summary>The eight EXIF values in order: 1 upright, 2 mirrored, 3 upside down, and so on.</summary>
        public static ExifTurn FromTag(ushort tag) => tag switch
        {
            2 => new(0, true),
            3 => new(180, false),
            4 => new(180, true),
            5 => new(270, true),
            6 => new(90, false),
            7 => new(90, true),
            8 => new(270, false),
            _ => None
        };

        /// <summary>JPEG only: the other directly placed formats carry no orientation WIC reads.</summary>
        public static ExifTurn Read(string path)
        {
            if (Path.GetExtension(path).ToLowerInvariant() is not (".jpg" or ".jpeg")) return None;

            using var fs = File.OpenRead(path);
            using var ras = fs.AsRandomAccessStream();
            var decoder = BitmapDecoder.CreateAsync(ras).GetAwaiter().GetResult();
            var found = decoder.BitmapProperties.GetPropertiesAsync([OrientationProperty]).GetAwaiter().GetResult();
            return found.TryGetValue(OrientationProperty, out var value) && value.Value is ushort tag ? FromTag(tag) : None;
        }

        private const string OrientationProperty = "System.Photo.Orientation";
    }

    /// <summary>The page's own /Rotate as 0, 90, 180 or 270.</summary>
    /// <remarks>PDFsharp pushes inherited /Rotate onto each page; non-quarter turns are ignored, as readers do.</remarks>
    private static int SourceRotation(PdfPage page)
    {
        var degrees = page.Elements.GetInteger("/Rotate");
        return degrees % 90 == 0 ? DocumentItem.NormaliseRotation(degrees) : 0;
    }

    /// <summary>Turns a page clockwise by <paramref name="degrees"/>, on top of its current turn.</summary>
    private static void TurnPage(PdfPage page, int degrees)
        => page.Elements.SetInteger("/Rotate", DocumentItem.NormaliseRotation(SourceRotation(page) + degrees));

    /// <summary>Removes the /Matrix turn PDFsharp builds into a form drawn from a rotated page.</summary>
    /// <remarks>
    /// <see cref="XPdfForm"/> applies the source /Rotate in the matrix but reports the unturned
    /// size, so content lands half off the page. Without it the form matches an unrotated
    /// page, and <see cref="AddPdfPage"/> turns the result with the new page's /Rotate.
    /// </remarks>
    private static void ClearFormRotation(PdfPage page)
    {
        var xobjects = page.Resources.Elements.GetDictionary("/XObject");
        if (xobjects == null) return;

        foreach (var key in xobjects.Elements.Keys)
        {
            if (xobjects.Elements.GetDictionary(key) is { } xobject && ReadName(xobject, "/Subtype") == "/Form")
                xobject.Elements.Remove("/Matrix");
        }
    }

    /// <summary>The page box for some content: sized as it will be seen, stored as it is drawn.</summary>
    /// <remarks>
    /// Orientation and paper size are decided for the displayed page; the box is then swapped
    /// back by the quarter turn so content is drawn unturned and /Rotate turns the page.
    /// </remarks>
    private readonly record struct PageFrame(double BoxWidth, double BoxHeight, int Rotation)
    {
        public static PageFrame For(double contentWidth, double contentHeight, int turn, bool useCustomPageSize)
        {
            var quarterTurn = turn is 90 or 270;
            var (shownW, shownH) = quarterTurn ? (contentHeight, contentWidth) : (contentWidth, contentHeight);

            var sourceLandscape = shownW > shownH;
            var targetLandscape = ResolveTargetLandscape(sourceLandscape);

            var (pageW, pageH) = useCustomPageSize
                ? GetCustomPageSizePoints(targetLandscape)
                : GetAutoPageSizePoints(shownW, shownH, sourceLandscape, targetLandscape);

            return quarterTurn ? new PageFrame(pageH, pageW, turn) : new PageFrame(pageW, pageH, turn);
        }

        public void Size(PdfPage page)
        {
            page.Width = XUnit.FromPoint(BoxWidth);
            page.Height = XUnit.FromPoint(BoxHeight);
        }

        /// <summary>Applied after drawing, so PDFsharp never sees it while drawing.</summary>
        public void Turn(PdfPage page)
        {
            if (Rotation != 0) page.Elements.SetInteger("/Rotate", Rotation);
        }

        /// <summary>Scales content into the margins and centres it, in XGraphics coordinates.</summary>
        public (double Left, double Top, double Width, double Height, double Scale) Fit(double contentWidth, double contentHeight, bool useCustomPageSize)
        {
            var (left, top, right, bottom) = useCustomPageSize ? BoxMargins() : (0d, 0d, 0d, 0d);
            var availW = Math.Max(1d, BoxWidth - left - right);
            var availH = Math.Max(1d, BoxHeight - top - bottom);
            var scale = Math.Min(availW / contentWidth, availH / contentHeight);
            var drawW = contentWidth * scale;
            var drawH = contentHeight * scale;
            return (left + ((availW - drawW) / 2d), top + ((availH - drawH) / 2d), drawW, drawH, scale);
        }

        /// <summary>The margins (set for the page as seen) moved onto the sides of the box as drawn.</summary>
        /// <remarks>/Rotate 90 shows the box's left side at the top, and so on round.</remarks>
        private (double Left, double Top, double Right, double Bottom) BoxMargins()
        {
            var (l, t, r, b) = AppSettings.Current.GetMarginsInPoints();
            return Rotation switch
            {
                90 => (t, r, b, l),
                180 => (r, b, l, t),
                270 => (b, l, t, r),
                _ => (l, t, r, b)
            };
        }
    }

    // Bookmarks and document identity.

    /// <summary>How a source page's coordinate space maps onto its new page.</summary>
    /// <remarks>Non-identity only when the page was redrawn at another size.</remarks>
    private readonly record struct PageTransform(double Scale, double OffsetX, double OffsetY)
    {
        public static PageTransform Identity => new(1d, 0d, 0d);

        public double MapX(double x) => OffsetX + (x * Scale);

        public double MapY(double y) => OffsetY + (y * Scale);
    }

    /// <summary>A page in the output, and how its source was mapped.</summary>
    private readonly record struct ImportedPage(PdfPage Page, PageTransform Transform);

    /// <summary>Guards against cyclic or absurdly deep outline trees.</summary>
    private const int MaxOutlineDepth = 32;

    /// <summary>Copies every source's bookmarks onto the output.</summary>
    /// <remarks>
    /// Bookmarks live on the catalog, not the pages, so an assembled document has none unless
    /// copied. Destinations are re-pointed at the pages present; the rest are dropped.
    /// </remarks>
    private static void CopyOutlines(
        PdfDocument output,
        Dictionary<string, PdfDocument> sourceDocs,
        Dictionary<(string Source, int PageNumber), ImportedPage> importedPages)
    {
        var copied = 0;

        foreach (var (path, sourceDoc) in sourceDocs)
        {
            PdfOutlineCollection roots;
            try
            {
                roots = sourceDoc.Outlines;
            }
            // One unreadable outline must not cost the other sources theirs.
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not read the bookmarks of {Path}", path);
                continue;
            }

            if (roots.Count == 0) continue;

            // Destinations name page objects; map them back to page numbers.
            var pageNumbers = new Dictionary<PdfObjectID, int>();
            for (var i = 0; i < sourceDoc.PageCount; i++)
            {
                var reference = sourceDoc.Pages[i].Reference;
                if (reference != null) pageNumbers[reference.ObjectID] = i + 1;
            }

            copied += CopyOutlineLevel(roots, output.Outlines, path, pageNumbers, importedPages, 0);
        }

        if (copied > 0) Log.Information("Bookmarks carried over: {Count}", copied);
    }

    private static int CopyOutlineLevel(
        PdfOutlineCollection source,
        PdfOutlineCollection target,
        string sourcePath,
        Dictionary<PdfObjectID, int> pageNumbers,
        Dictionary<(string Source, int PageNumber), ImportedPage> importedPages,
        int depth)
    {
        if (depth >= MaxOutlineDepth)
        {
            Log.Warning("Stopped at bookmark depth {Depth} in {Path}: deeper than any real outline", depth, sourcePath);
            return 0;
        }

        var copied = 0;

        foreach (var entry in source)
        {
            var destination = ResolveOutlineTarget(entry, sourcePath, pageNumbers, importedPages);

            if (destination is not { } imported)
            {
                // Target page not in this output: lift its children to this level instead of losing them.
                copied += CopyOutlineLevel(entry.Outlines, target, sourcePath, pageNumbers, importedPages, depth + 1);
                continue;
            }

            var copy = target.Add(entry.Title ?? string.Empty, imported.Page, entry.Opened, entry.Style);
            if (!entry.TextColor.IsEmpty) copy.TextColor = entry.TextColor;

            ApplyDestination(copy, entry, imported.Transform);
            copied++;

            copied += CopyOutlineLevel(entry.Outlines, copy.Outlines, sourcePath, pageNumbers, importedPages, depth + 1);
        }

        return copied;
    }

    private static ImportedPage? ResolveOutlineTarget(
        PdfOutline entry,
        string sourcePath,
        Dictionary<PdfObjectID, int> pageNumbers,
        Dictionary<(string Source, int PageNumber), ImportedPage> importedPages)
    {
        PdfPage? destinationPage;
        try
        {
            destinationPage = entry.DestinationPage;
        }
        // An unresolvable destination drops just that bookmark.
        catch (Exception ex)
        {
            Log.Debug(ex, "Bookmark '{Title}' in {Path} has no destination this can follow", entry.Title, sourcePath);
            return null;
        }

        if (destinationPage?.Reference == null) return null;
        if (!pageNumbers.TryGetValue(destinationPage.Reference.ObjectID, out var pageNumber)) return null;

        return importedPages.TryGetValue((sourcePath, pageNumber), out var imported) ? imported : null;
    }

    /// <summary>Copies the destination, moving its coordinates with the page.</summary>
    /// <remarks>Only the fields the destination type defines are written.</remarks>
    private static void ApplyDestination(PdfOutline copy, PdfOutline source, PageTransform transform)
    {
        copy.PageDestinationType = source.PageDestinationType;

        switch (source.PageDestinationType)
        {
            case PdfPageDestinationType.Xyz:
                if (source.Left.HasValue) copy.Left = transform.MapX(source.Left.Value);
                if (source.Top.HasValue) copy.Top = transform.MapY(source.Top.Value);
                copy.Zoom = source.Zoom;
                break;

            case PdfPageDestinationType.FitH:
            case PdfPageDestinationType.FitBH:
                if (source.Top.HasValue) copy.Top = transform.MapY(source.Top.Value);
                break;

            case PdfPageDestinationType.FitV:
            case PdfPageDestinationType.FitBV:
                if (source.Left.HasValue) copy.Left = transform.MapX(source.Left.Value);
                break;

            case PdfPageDestinationType.FitR:
                if (source.Left.HasValue) copy.Left = transform.MapX(source.Left.Value);
                if (source.Top.HasValue) copy.Top = transform.MapY(source.Top.Value);
                copy.Right = transform.MapX(source.Right);
                copy.Bottom = transform.MapY(source.Bottom);
                break;

            default:
                // Fit and FitB carry no coordinates.
                break;
        }
    }

    /// <summary>Copies the source's document information when every page came from one PDF.</summary>
    /// <remarks>A merge has no single title, and viewers show Info.Title instead of the file name.</remarks>
    private static void CopyDocumentInfo(PdfDocument output, Dictionary<string, PdfDocument> sourceDocs, bool singleSource)
    {
        output.Info.Creator = "Gladhen3";

        if (!singleSource) return;

        try
        {
            var source = sourceDocs.Values.First().Info;

            if (!string.IsNullOrEmpty(source.Title)) output.Info.Title = source.Title;
            if (!string.IsNullOrEmpty(source.Author)) output.Info.Author = source.Author;
            if (!string.IsNullOrEmpty(source.Subject)) output.Info.Subject = source.Subject;
            if (!string.IsNullOrEmpty(source.Keywords)) output.Info.Keywords = source.Keywords;

            // Creator is the app that made the original content; the producer is recorded separately.
            if (!string.IsNullOrEmpty(source.Creator)) output.Info.Creator = source.Creator;
        }
        // Info is optional and often malformed; losing it must not fail the save.
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not carry the document information across");
        }
    }

    /// <returns>How many images the codec itself failed on; each was kept as it was.</returns>
    internal static int CompressDocumentImages(PdfDocument doc, int targetDpi, double quality, IImageCodec codec, HashSet<int>? skipPages = null)
    {
        if (targetDpi <= 0) return 0;

        var jobs = new List<ImageJob>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);

        for (var i = 0; i < doc.PageCount; i++)
        {
            if (skipPages != null && skipPages.Contains(i)) continue;

            var page = doc.Pages[i];
            var pageLongInches = Math.Max(page.Width.Inch, page.Height.Inch);
            if (pageLongInches <= 0) pageLongInches = 11.0;

            var res = page.Elements.GetDictionary("/Resources") ?? page.Resources;
            CollectImages(res, pageLongInches, seen, jobs, 0);
        }

        if (jobs.Count == 0)
        {
            Log.Information("Embedded image recompression: no eligible images found");
            return 0;
        }

        var worst = 0L;
        foreach (var j in jobs) worst = Math.Max(worst, j.EstimateBytes(targetDpi));
        var lanes = LanesFor(worst);
        var orderedJobs = jobs
            .OrderByDescending(j => j.EstimateBytes(targetDpi))
            .ToList();

        Log.Information("Embedded image recompression: {Count} candidate images, {Lanes} lanes", orderedJobs.Count, lanes);

        long before = 0, after = 0;
        var replaced = 0;
        var codecFailures = 0;

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
        GC.WaitForPendingFinalizers();

        // PDFsharp is not thread-safe and reading a stream may touch document tables, so only
        // that read is serialised; decode and encode stay parallel.
        var documentGate = new object();

        Parallel.For(0, orderedJobs.Count, new ParallelOptions { MaxDegreeOfParallelism = lanes }, i =>
        {
            var job = orderedJobs[i];
            lock (documentGate) job.Prepare();

            // A codec failure is counted and reported (the user chose it); an undecodable image is
            // only logged, since any codec would keep it as is.
            try
            {
                job.Recompress(targetDpi, quality, codec);
            }
            catch (CodecException ex)
            {
                Interlocked.Increment(ref codecFailures);
                Log.Warning(ex, "Codec failed on image {Index} of {Total} ({Reason}); keeping the original", i + 1, orderedJobs.Count, ex.Reason);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Leaving image {Index} of {Total} at its original size", i + 1, orderedJobs.Count);
            }
        });

        foreach (var job in orderedJobs)
        {
            before += job.OriginalLength;
            if (job.Encoded == null || job.Encoded.Data.Length >= job.OriginalLength * 0.98)
            {
                after += job.OriginalLength;
            }
            else
            {
                try
                {
                    job.Apply();
                    after += job.Encoded.Data.Length;
                    replaced++;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Could not replace one image stream; keeping the original");
                    after += job.OriginalLength;
                }
            }
            job.Release();
        }

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
        GC.WaitForPendingFinalizers();

        Log.Information("Embedded image recompression: {Replaced}/{Total} replaced, {Failed} codec failures, image bytes {Before}B -> {After}B ({Ratio:P0})",
            replaced, jobs.Count, codecFailures, before, after, before > 0 ? (double)after / before : 1d);

        return codecFailures;
    }

    private static void CollectImages(PdfDictionary? resources, double pageLongInches, HashSet<object> seen, List<ImageJob> jobs, int depth)
    {
        if (resources == null || depth > 12) return;

        var xobjects = resources.Elements.GetDictionary("/XObject");
        if (xobjects == null) return;

        foreach (var key in xobjects.Elements.Keys.ToList())
        {
            var dict = xobjects.Elements.GetDictionary(key);
            if (dict == null || !seen.Add(dict)) continue;

            var subtype = ReadName(dict, "/Subtype");

            if (subtype == "/Image")
            {
                if (ImageJob.TryCreate(dict, pageLongInches, out var job) && job != null)
                    jobs.Add(job);
            }
            else if (subtype == "/Form")
            {
                CollectImages(dict.Elements.GetDictionary("/Resources"), pageLongInches, seen, jobs, depth + 1);
            }
        }
    }

    internal static string? ReadName(PdfDictionary dict, string key) => dict.Elements.GetValue(key) switch
    {
        PdfName name => name.Value,
        PdfNameObject nameObject => nameObject.Value,
        _ => null
    };

    private sealed class ImageJob
    {
        private PdfDictionary _dict = null!;
        private byte[]? _raw;
        private bool _rawIsJpeg;
        private int _width;
        private int _height;
        private int _components;
        private double _pageLongInches;

        public int OriginalLength { get; private set; }
        public EncodedImage? Encoded { get; private set; }

        public static bool TryCreate(PdfDictionary dict, double pageLongInches, out ImageJob? job)
        {
            job = null;
            if (dict.Stream == null) return false;

            if (dict.Elements.ContainsKey("/ImageMask") && dict.Elements.GetBoolean("/ImageMask")) return false;

            var w = dict.Elements.GetInteger("/Width");
            var h = dict.Elements.GetInteger("/Height");
            if (w <= 0 || h <= 0) return false;

            // Decoding holds every sample; beyond this the image is kept.
            if ((long)w * h > MaxDecodedPixels)
            {
                Log.Warning("Leaving a {Width}x{Height} image as-is: over the {Limit}-pixel limit", w, h, MaxDecodedPixels);
                return false;
            }

            var length = dict.Stream.Value?.Length ?? 0;
            if (length == 0) return false;

            var filter = FilterName(dict);
            bool isJpeg;
            var components = 0;

            if (filter == "/DCTDecode")
            {
                isJpeg = true;
            }
            else if (filter == "/FlateDecode")
            {
                if (dict.Elements.GetInteger("/BitsPerComponent") != 8) return false;
                components = ColorSpaceName(dict) switch { "/DeviceRGB" => 3, "/DeviceGray" => 1, _ => 0 };
                if (components == 0) return false;
                isJpeg = false;
            }
            else
            {
                return false;
            }

            job = new ImageJob
            {
                _dict = dict,
                _rawIsJpeg = isJpeg,
                _width = w,
                _height = h,
                _components = components,
                _pageLongInches = pageLongInches,
                OriginalLength = length
            };
            return true;
        }

        private int TargetLongEdge(int targetDpi)
        {
            var cap = (int)Math.Ceiling(_pageLongInches * targetDpi);
            return Math.Min(cap, Math.Max(_width, _height));
        }

        public long EstimateBytes(int targetDpi)
        {
            var longEdge = Math.Max(_width, _height);
            if (longEdge <= 0) return 0;
            var scale = (double)TargetLongEdge(targetDpi) / longEdge;
            var targetPx = (long)Math.Ceiling(_width * scale) * (long)Math.Ceiling(_height * scale);
            return ((long)_width * _height * 4) + (targetPx * 4 * 2);
        }

        public void Prepare()
        {
            try
            {
                if (_rawIsJpeg)
                {
                    _raw = _dict.Stream!.Value;
                    return;
                }

                // PDFsharp inflates without a limit, so measure first: the samples /Width and /Height
                // describe, plus a predictor byte per row.
                var expected = ((long)_width * _components + 1) * _height;
                var encoded = _dict.Stream!.Value;
                if (encoded is not { Length: > 2 } || InflateBounded(encoded, expected, null) < 0)
                {
                    Log.Warning("Leaving a {Width}x{Height} image as-is: its stream is larger than the image", _width, _height);
                    _raw = null;
                    return;
                }

                _raw = _dict.Stream!.UnfilteredValue;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Leaving a {Width}x{Height} image as-is: its stream could not be read", _width, _height);
                _raw = null;
            }
        }

        /// <exception cref="CodecException">The codec failed; decoding failures surface as themselves.</exception>
        public void Recompress(int targetDpi, double quality, IImageCodec codec)
        {
            if (_raw == null) return;

            try
            {
                var target = TargetLongEdge(targetDpi);

                using var bitmap = _rawIsJpeg
                    ? WicDecodeScaled(_raw, target)
                    : RawToBitmap(_raw, _width, _height, _components, target);
                if (bitmap == null) return;

                var encoded = codec.Encode(bitmap, quality);
                if (encoded is not { Data.Length: > 0 }) return;

                Encoded = encoded;
            }
            finally
            {
                _raw = null;
            }
        }

        /// <remarks>
        /// Every sample-describing entry is rewritten and stale ones removed; a leftover
        /// /ColorSpace or /Decode would render wrong colours.
        /// </remarks>
        public void Apply()
        {
            var encoded = Encoded!;
            var e = _dict.Elements;
            _dict.Stream!.Value = encoded.Data;
            e.SetName("/Filter", encoded.Filter);
            e.SetInteger("/Width", encoded.Width);
            e.SetInteger("/Height", encoded.Height);
            e.SetInteger("/BitsPerComponent", encoded.BitsPerComponent);
            e.SetName("/ColorSpace", encoded.Components == 1 ? "/DeviceGray" : "/DeviceRGB");
            e.SetInteger("/Length", encoded.Data.Length);
            e.Remove("/DecodeParms");
            e.Remove("/Decode");
            e.Remove("/ColorTransform");

            if (encoded.Predictor > 0)
            {
                var parms = new PdfDictionary();
                parms.Elements.SetInteger("/Predictor", encoded.Predictor);
                parms.Elements.SetInteger("/Colors", encoded.Components);
                parms.Elements.SetInteger("/BitsPerComponent", encoded.BitsPerComponent);
                parms.Elements.SetInteger("/Columns", encoded.Width);
                e["/DecodeParms"] = parms;
            }
        }

        public void Release()
        {
            _raw = null;
            Encoded = null;
        }
    }

    private static string FilterName(PdfDictionary dict)
    {
        var chain = dict.Elements.GetArray("/Filter");
        if (chain is { Elements.Count: > 0 })
            return chain.Elements[^1] is PdfName last ? last.Value : string.Empty;

        return dict.Elements["/Filter"] is null ? string.Empty : dict.Elements.GetName("/Filter");
    }

    private static string ColorSpaceName(PdfDictionary dict)
    {
        if (dict.Elements.GetArray("/ColorSpace") is not null) return string.Empty;

        return dict.Elements["/ColorSpace"] is null ? string.Empty : dict.Elements.GetName("/ColorSpace");
    }


    /// <param name="codec">The chosen compression method, or null to convert losslessly at full size.</param>
    private static Dictionary<string, PreEncodedImage> PreEncodeImages(List<string> paths, int cap, double quality, int lanes, IImageCodec? codec)
    {
        var results = new ConcurrentDictionary<string, PreEncodedImage>(StringComparer.OrdinalIgnoreCase);
        var workerCount = Math.Min(paths.Count, Math.Max(1, lanes));
        var orderedPaths = paths
            .OrderByDescending(path =>
            {
                try { return new FileInfo(path).Length; }
                catch { return 0L; }
            })
            .ToList();

        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(Math.Max(workerCount * 2, 16))
        {
            SingleWriter = true,
            SingleReader = false,
            FullMode = BoundedChannelFullMode.Wait
        });

        var workers = new Task[workerCount];
        for (var w = 0; w < workerCount; w++)
        {
            workers[w] = Task.Run(async () =>
            {
                await foreach (var path in channel.Reader.ReadAllAsync())
                {
                    try
                    {
                        var bytes = EncodeSourceImage(path, cap, quality, codec, out var final);
                        if (bytes == null || bytes.Length == 0) continue;

                        var original = new FileInfo(path).Length;

                        // A format PDFsharp cannot read must be converted, however large the result.
                        if (bytes.Length < original || !CanPlaceDirectly(path))
                        {
                            results[path] = new PreEncodedImage(bytes, final);
                            Log.Information("Pre-encoded {Name}: {Src}B -> {Dst}B ({Ratio:P0}){Stage}",
                                Path.GetFileName(path), original, bytes.Length, (double)bytes.Length / original, final ? "" : ", final encoding to follow");
                        }
                        else
                        {
                            Log.Information("Pre-encode skipped (would grow) {Name}: {Src}B -> {Dst}B",
                                Path.GetFileName(path), original, bytes.Length);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Using {Name} as-is: it could not be re-encoded", Path.GetFileName(path));
                    }
                }
            });
        }

        foreach (var path in orderedPaths)
            channel.Writer.WriteAsync(path).AsTask().GetAwaiter().GetResult();

        channel.Writer.Complete();
        Task.WaitAll(workers);

        return new Dictionary<string, PreEncodedImage>(results, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Formats <see cref="XImage.FromFile(string)"/> can read.</summary>
    private static bool CanPlaceDirectly(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".bmp";

    /// <param name="final">False for a lossless stand-in the document-wide pass still encodes with <paramref name="codec"/>.</param>
    private static byte[]? EncodeSourceImage(string path, int maxLongEdge, double quality, IImageCodec? codec, out bool final)
    {
        final = true;

        using var fs = File.OpenRead(path);
        using var ras = fs.AsRandomAccessStream();
        var decoder = BitmapDecoder.CreateAsync(ras).GetAwaiter().GetResult();

        var hasAlpha = decoder.BitmapAlphaMode != BitmapAlphaMode.Ignore;
        var alphaMode = hasAlpha ? BitmapAlphaMode.Premultiplied : BitmapAlphaMode.Ignore;

        using var bitmap = DecodeScaled(decoder, maxLongEdge, alphaMode);
        if (bitmap == null) return null;

        // Images with alpha stay PNG: JPEG would turn transparency black.
        if (hasAlpha || codec == null) return Wic.EncodePng(bitmap);

        if (codec.ProducesJpegFile) return codec.Encode(bitmap, quality)?.Data;

        final = false;
        return Wic.EncodePng(bitmap);
    }

    /// <summary>Bytes an image file will occupy in the PDF after encoding.</summary>
    private static long FinalEncodedLength(string path, int maxLongEdge, double quality, IImageCodec codec)
    {
        using var fs = File.OpenRead(path);
        using var ras = fs.AsRandomAccessStream();
        var decoder = BitmapDecoder.CreateAsync(ras).GetAwaiter().GetResult();

        var hasAlpha = decoder.BitmapAlphaMode != BitmapAlphaMode.Ignore;
        using var bitmap = DecodeScaled(decoder, maxLongEdge, hasAlpha ? BitmapAlphaMode.Premultiplied : BitmapAlphaMode.Ignore);
        if (bitmap == null) return 0;

        if (hasAlpha) return Wic.EncodePng(bitmap).LongLength;

        // A declined image stays as it was.
        return codec.Encode(bitmap, quality)?.Data.LongLength ?? fs.Length;
    }

    private static SoftwareBitmap? DecodeScaled(BitmapDecoder decoder, int maxLongEdge, BitmapAlphaMode alphaMode,
        ExifOrientationMode exif = ExifOrientationMode.RespectExifOrientation)
    {
        var transform = new BitmapTransform();

        var w = (int)decoder.PixelWidth;
        var h = (int)decoder.PixelHeight;
        long outW = w, outH = h;
        var longEdge = Math.Max(w, h);
        if (maxLongEdge > 0 && longEdge > maxLongEdge)
        {
            var scale = (double)maxLongEdge / longEdge;
            transform.ScaledWidth = (uint)Math.Max(1, (int)Math.Round(w * scale));
            transform.ScaledHeight = (uint)Math.Max(1, (int)Math.Round(h * scale));
            transform.InterpolationMode = BitmapInterpolationMode.Fant;
            outW = transform.ScaledWidth;
            outH = transform.ScaledHeight;
        }

        // WIC scales while decoding, so only the output must fit.
        if (outW * outH > MaxDecodedPixels)
            throw new InvalidDataException($"A {outW}x{outH} decode is over the {MaxDecodedPixels}-pixel limit.");

        return decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            alphaMode,
            transform,
            exif,
            ColorManagementMode.ColorManageToSRgb).GetAwaiter().GetResult();
    }

    /// <summary>Decodes a JPEG stream from inside a PDF.</summary>
    /// <remarks>EXIF orientation is ignored, as PDF readers ignore it.</remarks>
    private static SoftwareBitmap? WicDecodeScaled(byte[] data, int maxLongEdge)
    {
        using var ras = new InMemoryRandomAccessStream();
        Wic.WriteAll(ras, data);
        var decoder = BitmapDecoder.CreateAsync(ras).GetAwaiter().GetResult();
        return DecodeScaled(decoder, maxLongEdge, BitmapAlphaMode.Ignore, ExifOrientationMode.IgnoreExifOrientation);
    }

    private static SoftwareBitmap? RawToBitmap(byte[] samples, int width, int height, int components, int maxLongEdge)
    {
        // Require exactly the samples the dictionary describes (PDFsharp undoes predictors);
        // anything else would shear the picture.
        if (samples == null || samples.LongLength != (long)width * height * components) return null;

        var dw = width;
        var dh = height;
        var longEdge = Math.Max(width, height);
        if (maxLongEdge > 0 && longEdge > maxLongEdge)
        {
            var scale = (double)maxLongEdge / longEdge;
            dw = Math.Max(1, (int)Math.Round(width * scale));
            dh = Math.Max(1, (int)Math.Round(height * scale));
        }

        var bgra = new byte[(long)dw * dh * 4];
        var srcRow = (long)width * components;
        var di = 0;

        for (var y = 0; y < dh; y++)
        {
            var y0 = (int)((long)y * height / dh);
            var y1 = Math.Max(y0 + 1, (int)(((long)y + 1) * height / dh));

            for (var x = 0; x < dw; x++)
            {
                var x0 = (int)((long)x * width / dw);
                var x1 = Math.Max(x0 + 1, (int)(((long)x + 1) * width / dw));

                int sr = 0, sg = 0, sb = 0, n = 0;
                for (var sy = y0; sy < y1; sy++)
                {
                    var rowBase = sy * srcRow;
                    for (var sx = x0; sx < x1; sx++)
                    {
                        var si = rowBase + ((long)sx * components);
                        if (components == 3)
                        {
                            sr += samples[si];
                            sg += samples[si + 1];
                            sb += samples[si + 2];
                        }
                        else
                        {
                            var g = samples[si];
                            sr += g; sg += g; sb += g;
                        }
                        n++;
                    }
                }

                bgra[di++] = (byte)(sb / n);
                bgra[di++] = (byte)(sg / n);
                bgra[di++] = (byte)(sr / n);
                bgra[di++] = 255;
            }
        }

        var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, dw, dh, BitmapAlphaMode.Ignore);
        bitmap.CopyFromBuffer(bgra.AsBuffer());
        return bitmap;
    }

    private static bool ResolveTargetLandscape(bool sourceLandscape) => AppSettings.Current.Orientation switch
    {
        PdfPaperOrientation.Portrait => false,
        PdfPaperOrientation.Landscape => true,
        _ => sourceLandscape
    };

    private static (double W, double H) GetAutoPageSizePoints(double srcW, double srcH, bool sourceLandscape, bool targetLandscape)
        => targetLandscape == sourceLandscape ? (srcW, srcH) : (srcH, srcW);

    private static (double W, double H) GetCustomPageSizePoints(bool landscape)
    {
        var (w, h) = GetPageDimensionsInPortrait(AppSettings.Current.PaperSize);
        return landscape ? (h, w) : (w, h);
    }

    private static class PageDimensions
    {
        public static readonly (double W, double H) A4 = (210.0 * 72.0 / 25.4, 297.0 * 72.0 / 25.4);
        public static readonly (double W, double H) Letter = (8.5 * 72.0, 11.0 * 72.0);
        public static readonly (double W, double H) Legal = (8.5 * 72.0, 14.0 * 72.0);
        public static readonly (double W, double H) A3 = (297.0 * 72.0 / 25.4, 420.0 * 72.0 / 25.4);
    }

    internal static (double Width, double Height) GetPageDimensionsInPortrait(PdfPaperSize size)
    {
        var (w, h) = size switch
        {
            PdfPaperSize.A4 => PageDimensions.A4,
            PdfPaperSize.Letter => PageDimensions.Letter,
            PdfPaperSize.Legal => PageDimensions.Legal,
            PdfPaperSize.A3 => PageDimensions.A3,
            PdfPaperSize.Custom => (AppSettings.Current.GetCustomWidthInPoints(), AppSettings.Current.GetCustomHeightInPoints()),
            _ => PageDimensions.A4
        };
        return w > h ? (h, w) : (w, h);
    }

    private static double MaxPageLongEdgeInches()
    {
        var (w, h) = GetPageDimensionsInPortrait(AppSettings.Current.PaperSize);
        return Math.Max(w, h) / 72.0;
    }

    internal static int ComputeMaxLongEdgePixels(int srcWidthPx, int srcHeightPx, double srcDpi, int targetDpi, double? pageLongInches)
    {
        var srcLong = Math.Max(srcWidthPx, srcHeightPx);
        if (targetDpi <= 0 || srcLong <= 0) return Math.Max(1, srcLong);
        var longInches = pageLongInches ?? (srcLong / (srcDpi <= 1 ? 96.0 : srcDpi));
        var cap = (int)Math.Ceiling(longInches * targetDpi);
        return Math.Clamp(cap, 1, srcLong);
    }

    internal static void EnsureFileIsWritable(string filePath)
    {
        if (!File.Exists(filePath))
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                throw new PdfOperationException(PdfFailureReason.DirectoryNotFound, $"The folder '{dir}' does not exist.") { Path = filePath };
            }
            return;
        }

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new PdfOperationException(PdfFailureReason.FileInUse, $"'{Path.GetFileName(filePath)}' is open in another application.", ex) { Path = filePath };
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new PdfOperationException(PdfFailureReason.AccessDenied, $"'{Path.GetFileName(filePath)}' cannot be written to.", ex) { Path = filePath };
        }
    }
}
