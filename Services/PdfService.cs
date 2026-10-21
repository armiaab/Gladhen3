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
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Gladhen3.Services;

public class PdfService
{
    private const long InFlightBudgetBytes = 384L * 1024 * 1024;

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

    public PdfBuildResult CreatePdfFromDocuments(List<DocumentItem> items, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        if (items.Any(i => i.Type == DocumentType.SectionBreak))
            throw new ArgumentException("Section dividers must be removed before building a PDF.", nameof(items));

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

        // Where each source page ended up, so that bookmarks can be re-pointed at it once every
        // page is in. Keyed by source and page number rather than by page object, because two
        // different documents can hand out the same object id.
        var importedPages = new Dictionary<(string Source, int PageNumber), ImportedPage>();
        var imagePagesAdded = 0;
        var pagesAdded = 0;
        var skipped = new List<string>();

        var pageLongInches = useCustomPageSize ? MaxPageLongEdgeInches() : 11.0;
        var cap = dpi > 0 ? (int)Math.Ceiling(pageLongInches * dpi) : 0;
        var assumedSourcePx = Math.Max((long)cap * cap, 12_000_000L);
        var block = compress ? LanesFor((assumedSourcePx * 4) + ((long)cap * cap * 8)) : 32;

        // Without compression the only images converted are the ones PDFsharp cannot read, at
        // full size and losslessly, but they are still budgeted: a batch of 32 TIFF scans
        // decoded at once is exactly the memory spike the lane budget exists to stop.
        var lanes = compress ? block : LanesFor(assumedSourcePx * 8);

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

                            // Last one wins if the same page was added twice: a bookmark can only
                            // point at one of them, and the later copy is the one further down.
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

            // Inside the try on purpose: both of these read the source documents, and the
            // finally below closes them.
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
            // Not quietly replaced with JPEG: the user picked this method, and a file compressed
            // some other way would look like it worked.
            throw new PdfOperationException(PdfFailureReason.CodecUnavailable, $"The compression method '{id}' could not be loaded.", ex)
            {
                Path = outputPath,
                CodecReason = ex.Reason
            };
        }
    }

    /// <summary>
    /// Writes the document beside <paramref name="outputPath"/> and only then replaces it.
    /// </summary>
    /// <remarks>
    /// Saving straight over the destination meant a save that failed part-way - a full disk, a
    /// codec that faulted - left a truncated file where the old one had been. That matters most
    /// for the quickest edit there is, rotating a page and saving over the original, where the
    /// file being replaced is the only copy of the document. <see cref="File.Replace(string, string, string?, bool)"/>
    /// also keeps the original's creation time and permissions, so the result is the same file,
    /// edited, rather than a new one with the old name.
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

    /// <summary>Removes a leftover temporary file without letting the cleanup hide why it was left over.</summary>
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

    /// <summary>
    /// Terminates the flate streams PDFsharp leaves unfinished.
    /// </summary>
    /// <remarks>
    /// PDFsharp 6.2.4 writes a zlib header and a complete deflate payload, then omits the
    /// four-byte Adler-32 that ends a zlib stream. Readers that inflate raw deflate and stop at
    /// its end marker - poppler, pdfium, Acrobat - never notice. Readers that expect the whole
    /// zlib stream the PDF specification asks for report "premature end of data in flate
    /// filter", once per stream: on an 859-page book that is 1718 complaints, which is what
    /// MuPDF-based viewers print. Every file the library writes is affected, including ones it
    /// authors from nothing, and 6.2.4 is the newest release.
    ///
    /// The four bytes are appended here rather than the stream re-compressed, so PDFsharp's own
    /// compression is preserved exactly and the file grows by four bytes per stream. A stream
    /// that already ends correctly is left untouched, which is how anything imported unchanged
    /// from a source document comes through.
    /// </remarks>
    private static void TerminateFlateStreams(PdfDocument document)
    {
        var repaired = 0;
        var compressed = 0;

        foreach (var obj in document.Internals.GetAllObjects())
        {
            if (obj is not PdfDictionary dict || dict.Stream == null) continue;

            // Not yet encoded. PDFsharp would deflate this during Save - after this pass, and
            // without terminating it - so it is compressed here instead, where it can be done
            // correctly. CompressContentStreams is off for exactly this reason.
            if (!dict.Elements.ContainsKey("/Filter"))
            {
                // XMP is conventionally left readable so tools can find it without a codec.
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

                // On a short stream the zlib wrapper costs more than it saves, and PDFsharp
                // compressed these anyway - which is how a 40-byte page became 66 broken bytes.
                if (deflated.Length >= plain.Length) continue;

                dict.Stream.Value = deflated;
                dict.Elements.SetName("/Filter", "/FlateDecode");
                dict.Elements.SetInteger("/Length", deflated.Length);
                compressed++;
                continue;
            }

            // A single /FlateDecode only. A filter chain is someone else's arrangement and the
            // arithmetic below does not describe it.
            if (dict.Elements.GetName("/Filter") != "/FlateDecode") continue;

            var raw = dict.Stream.Value;
            if (raw is not { Length: > 2 }) continue;

            // zlib header: low nibble 8 (deflate) and the two bytes a multiple of 31.
            if ((raw[0] & 0x0F) != 8 || (((raw[0] << 8) | raw[1]) % 31) != 0) continue;

            byte[] payload;
            try
            {
                payload = InflateRawDeflate(raw);
            }
            // Per-item isolation: anything that is not a plain zlib header over a complete
            // deflate payload is left exactly as it was.
            catch (Exception ex)
            {
                Log.Debug(ex, "Leaving a {Length}-byte flate stream alone", raw.Length);
                continue;
            }

            var checksum = Adler32(payload);

            // Already terminated - the last four bytes are the checksum the payload implies.
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

    /// <summary>Inflates the deflate payload sitting behind a two-byte zlib header.</summary>
    private static byte[] InflateRawDeflate(byte[] zlib)
    {
        using var input = new MemoryStream(zlib, 2, zlib.Length - 2, writable: false);
        using var inflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(zlib.Length * 4);
        inflate.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>Adler-32 over the uncompressed bytes, as RFC 1950 requires at the stream's end.</summary>
    private static uint Adler32(byte[] data)
    {
        const uint Modulus = 65521;
        // Largest run that cannot overflow the accumulators before the modulo is applied.
        const int MaxRun = 5552;

        uint a = 1, b = 0;
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

        return (b << 16) | a;
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

    /// <summary>An image file decoded and re-encoded into something PDFsharp can place.</summary>
    /// <param name="Final">
    /// True when these are the bytes the PDF will keep. False for a lossless stand-in, used when
    /// the chosen codec's output is not a file PDFsharp can read; the document-wide pass then
    /// encodes it for real.
    /// </param>
    private sealed record PreEncodedImage(byte[] Bytes, bool Final);

    private const long PdfOverheadBytes = 2048;

    private const int MaxSizeSamples = 3;

    private const long SampleParseLimitBytes = 256L * 1024 * 1024;

    public static long EstimateOutputSize(IReadOnlyList<DocumentItem> items, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0) return 0;

        return EstimateSectionSizes([items], cancellationToken)[0];
    }

    public static IReadOnlyList<long> EstimateSectionSizes(
        IReadOnlyList<IReadOnlyList<DocumentItem>> sections,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sections);
        if (sections.Count == 0) return [];

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

        // Measured with the method that will actually be used: the whole point of the estimate
        // is to show what switching it does.
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

            // Turning a page is a single /Rotate entry, so it costs this path nothing: the whole
            // file - annotations, links, forms - comes through exactly as it was.
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
        if (encoded.TryGetValue(imagePath, out var pre) && pre.Bytes.Length > 0)
        {
            owned = new MemoryStream(pre.Bytes, writable: false);
            image = XImage.FromStream(owned);
            final = pre.Final;
        }
        else
        {
            image = XImage.FromFile(imagePath);
        }

        try
        {
            var imgW = image.PixelWidth;
            var imgH = image.PixelHeight;
            var frame = PageFrame.For(imgW, imgH, rotation, useCustomPageSize);

            var page = output.AddPage();
            frame.Size(page);

            using (var gfx = XGraphics.FromPdfPage(page))
            {
                var (left, top, drawW, drawH, _) = frame.Fit(imgW, imgH, useCustomPageSize);
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

    /// <summary>Adds an empty page, the same size as the one before it.</summary>
    /// <remarks>
    /// Matching the previous page is what makes an inserted blank useful - padding a chapter to
    /// start on a right-hand page, a separator sheet for duplex printing - and it is turned the
    /// same way, so it does not appear as the one portrait page in a landscape deck. With a paper
    /// size chosen in Settings every page is that size anyway.
    /// </remarks>
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
    /// <returns>The page it became, and how its coordinate space moved to get there.</returns>
    private static ImportedPage AddPdfPage(PdfDocument output, PdfPage sourcePage, bool useCustomPageSize, string sourcePath, int pageNumber, int rotation, Dictionary<string, XPdfForm> formCache)
    {
        if (!useCustomPageSize && AppSettings.Current.Orientation == PdfPaperOrientation.Automatic)
        {
            // Imported whole, so its own coordinate space comes with it untouched - and turning
            // it is one entry on the page, which moves nothing a bookmark points at.
            var imported = output.AddPage(sourcePage);
            if (rotation != 0) TurnPage(imported, rotation);
            return new ImportedPage(imported, PageTransform.Identity);
        }

        // The page is laid out the way it will be seen - its own /Rotate plus the user's turn -
        // and then drawn unturned, in its own frame, with the new page's /Rotate doing the
        // turning. That keeps every coordinate in the source page's user space, which is where
        // bookmark destinations are written.
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

        // The content was scaled and centred, so anything addressing this page by coordinate has
        // to move with it. XGraphics measures "top" down from the top edge while PDF user space
        // counts up from the bottom, hence the flip on the vertical offset.
        return new ImportedPage(page, new PageTransform(scale, left, frame.BoxHeight - top - drawH));
    }

    // ---------------------------------------------------------------------------- page rotation

    /// <summary>The page's own /Rotate, as 0, 90, 180 or 270.</summary>
    /// <remarks>
    /// PDFsharp pushes a /Rotate inherited from the page tree down onto each page when it
    /// opens a file, so reading the page's own entry is enough. Anything that is not a quarter
    /// turn is ignored, as readers ignore it.
    /// </remarks>
    private static int SourceRotation(PdfPage page)
    {
        var degrees = page.Elements.GetInteger("/Rotate");
        return degrees % 90 == 0 ? DocumentItem.NormaliseRotation(degrees) : 0;
    }

    /// <summary>Turns a page clockwise by <paramref name="degrees"/>, on top of any turn it already has.</summary>
    private static void TurnPage(PdfPage page, int degrees)
        => page.Elements.SetInteger("/Rotate", DocumentItem.NormaliseRotation(SourceRotation(page) + degrees));

    /// <summary>
    /// Removes the turn PDFsharp builds into a form drawn from a rotated page.
    /// </summary>
    /// <remarks>
    /// <see cref="XPdfForm"/> gives the form a /Matrix that applies the source page's /Rotate,
    /// but still reports the page's unturned width and height, so the drawn content lands half
    /// off the new page. A /Rotate 90 scan redrawn onto A4 came out blank. With the matrix gone
    /// the form is the page in its own frame - exactly what an unrotated page produces, since
    /// PDFsharp gives those no matrix at all - and <see cref="AddPdfPage"/> turns the result
    /// with the new page's /Rotate instead.
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

    /// <summary>
    /// The page box for a piece of content, worked out the way it will be seen and stored the
    /// way it is drawn.
    /// </summary>
    /// <remarks>
    /// Orientation and paper size are decided for the page as displayed, so that a sideways scan
    /// turned upright counts as portrait. The box itself is then swapped back by a quarter turn
    /// so the content can be drawn unturned, and /Rotate turns the whole page to how it was
    /// laid out. Scaling to fit commutes with turning by quarter turns, so the result is the
    /// same as turning the content first - without any coordinate leaving user space.
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

        /// <summary>Applied once drawing is finished, so nothing PDFsharp does while drawing sees it.</summary>
        public void Turn(PdfPage page)
        {
            if (Rotation != 0) page.Elements.SetInteger("/Rotate", Rotation);
        }

        /// <summary>Scales content to fit inside the margins and centres it there, in XGraphics coordinates.</summary>
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

        /// <summary>
        /// The margins as set - for the page as it is seen - moved onto the sides of the box it
        /// is drawn in, which /Rotate then turns.
        /// </summary>
        /// <remarks>
        /// /Rotate 90 turns the box clockwise, so its left side is seen at the top, its top on the
        /// right, and so on round. Without this a left margin on a turned page ended up along
        /// its top.
        /// </remarks>
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

    // ------------------------------------------------------- bookmarks and document identity

    /// <summary>
    /// How a source page's coordinate space was mapped onto the page that now holds it.
    /// </summary>
    /// <remarks>
    /// Only ever anything but <see cref="Identity"/> when the page was redrawn at a different
    /// size: the content is scaled and centred, so a destination written in the source page's
    /// coordinates has to move the same way or the bookmark lands on the right page in the
    /// wrong place. Importing a page whole leaves its space alone.
    /// </remarks>
    private readonly record struct PageTransform(double Scale, double OffsetX, double OffsetY)
    {
        public static PageTransform Identity => new(1d, 0d, 0d);

        public double MapX(double x) => OffsetX + (x * Scale);

        public double MapY(double y) => OffsetY + (y * Scale);
    }

    /// <summary>A page in the rebuilt document, and how its source got there.</summary>
    private readonly record struct ImportedPage(PdfPage Page, PageTransform Transform);

    /// <summary>A cyclic or absurd outline tree must not be able to recurse this into the stack.</summary>
    private const int MaxOutlineDepth = 32;

    /// <summary>
    /// Copies the bookmarks of every source document onto the rebuilt one.
    /// </summary>
    /// <remarks>
    /// Bookmarks hang off the document catalog rather than off any page, so a document
    /// assembled out of imported pages starts with none of them - for a textbook that is most
    /// of what made the file navigable, and it happened even when the user only changed the
    /// page size. Destinations are re-pointed at the pages that actually made it into this
    /// output, so a subset or one file of a split keeps the bookmarks that still have somewhere
    /// to go and drops only the ones that do not.
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
            // Per-source isolation: an unreadable outline in one file is not a reason to lose
            // the bookmarks of the others, or the pages that were just built.
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not read the bookmarks of {Path}", path);
                continue;
            }

            if (roots.Count == 0) continue;

            // A destination names a page object, but the output is keyed by page number, so the
            // source's object ids have to be turned back into numbers first.
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
                // The page it pointed at is not in this document. Its children may still have
                // somewhere to go, so they are lifted to this level rather than vanishing with
                // a parent that happened to land on a removed page.
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
        // Per-item isolation: a named destination the reader cannot resolve, or an entry whose
        // action does not go to a page at all. That one bookmark is dropped, not the rest.
        catch (Exception ex)
        {
            Log.Debug(ex, "Bookmark '{Title}' in {Path} has no destination this can follow", entry.Title, sourcePath);
            return null;
        }

        if (destinationPage?.Reference == null) return null;
        if (!pageNumbers.TryGetValue(destinationPage.Reference.ObjectID, out var pageNumber)) return null;

        return importedPages.TryGetValue((sourcePath, pageNumber), out var imported) ? imported : null;
    }

    /// <summary>
    /// Carries the destination across, moving whatever coordinates it carries with the page.
    /// </summary>
    /// <remarks>
    /// Only the fields the destination type actually defines are written: the others would put
    /// numbers into slots that type does not have. Most outlines use /Fit, which is the whole
    /// page and needs nothing.
    /// </remarks>
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
                // Fit and FitB take the whole page and carry no coordinates.
                break;
        }
    }

    /// <summary>
    /// Gives the rebuilt document the identity of the one it came from.
    /// </summary>
    /// <remarks>
    /// Only when every page came from a single PDF. A merge of three books has no one title,
    /// and a viewer shows Info.Title in its window in place of the file name, so inventing one
    /// is worse than leaving it empty - which is what the old fixed "Created with Gladhen3"
    /// title did to every file this app ever wrote.
    /// </remarks>
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

            // "Creator" means the application that made the original content, which is still
            // whatever produced the source. This file's own producer is recorded separately.
            if (!string.IsNullOrEmpty(source.Creator)) output.Info.Creator = source.Creator;
        }
        // The document information dictionary is optional and frequently malformed. Losing it
        // is cosmetic; failing the save over it would not be.
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

        Log.Information("Embedded image recompression: {Count} candidate images, {Lanes} at a time", jobs.Count, lanes);

        long before = 0, after = 0;
        var replaced = 0;
        var codecFailures = 0;

        for (var start = 0; start < jobs.Count; start += lanes)
        {
            var end = Math.Min(jobs.Count, start + lanes);

            for (var i = start; i < end; i++) jobs[i].Prepare();

            Parallel.For(start, end, new ParallelOptions { MaxDegreeOfParallelism = lanes }, i =>
            {
                // Per-item isolation, in two kinds. A codec that fails is counted, because the
                // user chose it and has to hear that it is not doing its job; an image that
                // cannot be decoded in the first place is only logged, as it always was - it
                // would be kept as it was by any codec.
                try
                {
                    jobs[i].Recompress(targetDpi, quality, codec);
                }
                catch (CodecException ex)
                {
                    Interlocked.Increment(ref codecFailures);
                    Log.Warning(ex, "Codec failed on image {Index} of {Total} ({Reason}); keeping the original", i + 1, jobs.Count, ex.Reason);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Leaving image {Index} of {Total} at its original size", i + 1, jobs.Count);
                }
            });

            for (var i = start; i < end; i++)
            {
                var job = jobs[i];
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
        }

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

    private static string? ReadName(PdfDictionary dict, string key) => dict.Elements.GetValue(key) switch
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
                _raw = _rawIsJpeg ? _dict.Stream!.Value : _dict.Stream!.UnfilteredValue;
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

            var target = TargetLongEdge(targetDpi);

            using var bitmap = _rawIsJpeg
                ? WicDecodeScaled(_raw, target)
                : RawToBitmap(_raw, _width, _height, _components, target);
            if (bitmap == null) return;

            var encoded = codec.Encode(bitmap, quality);
            if (encoded is not { Data.Length: > 0 }) return;

            Encoded = encoded;
        }

        /// <remarks>
        /// Every entry that describes the samples is rewritten, and the ones that described the
        /// old encoding are removed: a former CMYK or ICC image keeping its /ColorSpace, or an
        /// old /Decode array, renders in the wrong colours.
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
        Parallel.ForEach(paths, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, lanes) }, path =>
        {
            try
            {
                var bytes = EncodeSourceImage(path, cap, quality, codec, out var final);
                if (bytes == null || bytes.Length == 0) return;

                var original = new FileInfo(path).Length;

                // A format PDFsharp cannot read goes in converted however large that makes it:
                // the alternative is not a smaller page, it is no page at all.
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
        });

        return new Dictionary<string, PreEncodedImage>(results, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The image formats <see cref="XImage.FromFile(string)"/> reads. GIF, TIFF, WebP, HEIC and the rest throw.</summary>
    private static bool CanPlaceDirectly(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".bmp";

    /// <param name="final">
    /// False when the result is only a lossless stand-in that the document-wide pass still has
    /// to put through <paramref name="codec"/>.
    /// </param>
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

        // Transparency stays PNG whatever the codec: JPEG has no alpha, and flattening it turns
        // the transparent parts black.
        if (hasAlpha || codec == null) return Wic.EncodePng(bitmap);

        if (codec.ProducesJpegFile) return codec.Encode(bitmap, quality)?.Data;

        final = false;
        return Wic.EncodePng(bitmap);
    }

    /// <summary>How many bytes an image file will occupy in the PDF once the codec is done with it.</summary>
    private static long FinalEncodedLength(string path, int maxLongEdge, double quality, IImageCodec codec)
    {
        using var fs = File.OpenRead(path);
        using var ras = fs.AsRandomAccessStream();
        var decoder = BitmapDecoder.CreateAsync(ras).GetAwaiter().GetResult();

        var hasAlpha = decoder.BitmapAlphaMode != BitmapAlphaMode.Ignore;
        using var bitmap = DecodeScaled(decoder, maxLongEdge, hasAlpha ? BitmapAlphaMode.Premultiplied : BitmapAlphaMode.Ignore);
        if (bitmap == null) return 0;

        if (hasAlpha) return Wic.EncodePng(bitmap).LongLength;

        // A codec that declines an image leaves it as it was.
        return codec.Encode(bitmap, quality)?.Data.LongLength ?? fs.Length;
    }

    private static SoftwareBitmap? DecodeScaled(BitmapDecoder decoder, int maxLongEdge, BitmapAlphaMode alphaMode)
    {
        var transform = new BitmapTransform();

        var w = (int)decoder.PixelWidth;
        var h = (int)decoder.PixelHeight;
        var longEdge = Math.Max(w, h);
        if (maxLongEdge > 0 && longEdge > maxLongEdge)
        {
            var scale = (double)maxLongEdge / longEdge;
            transform.ScaledWidth = (uint)Math.Max(1, (int)Math.Round(w * scale));
            transform.ScaledHeight = (uint)Math.Max(1, (int)Math.Round(h * scale));
            transform.InterpolationMode = BitmapInterpolationMode.Fant;
        }

        return decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            alphaMode,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb).GetAwaiter().GetResult();
    }

    private static SoftwareBitmap? WicDecodeScaled(byte[] data, int maxLongEdge)
    {
        using var ras = new InMemoryRandomAccessStream();
        Wic.WriteAll(ras, data);
        var decoder = BitmapDecoder.CreateAsync(ras).GetAwaiter().GetResult();
        return DecodeScaled(decoder, maxLongEdge, BitmapAlphaMode.Ignore);
    }

    private static SoftwareBitmap? RawToBitmap(byte[] samples, int width, int height, int components, int maxLongEdge)
    {
        // Exactly the samples the dictionary describes, or the image is left alone. PDFsharp
        // undoes PNG predictors when it unfilters, so a well-formed stream always matches; one
        // that does not is in some arrangement this code does not know, and reading it as rows
        // of pixels anyway would shear the picture rather than shrink it.
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
