using Gladhen3.Models;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Gladhen3.Services;

/// <summary>
/// PDF assembly + image compression.
///
/// Three deliberate design points:
///  * The PDF layer is PDFsharp (MIT), not iText (AGPL).
///  * The image layer is WIC (Windows.Graphics.Imaging), which ships in the OS, so there is
///    no native imaging DLL in the package at all.
///  * Peak memory is bounded by a budget rather than by page count. See InFlightBudgetBytes.
/// </summary>
public class PdfService
{
    /// <summary>
    /// Ceiling for image data being decoded/encoded at any one moment.
    ///
    /// Concurrency is derived from this instead of from ProcessorCount. Fanning out to one
    /// image per core meant a 12-core machine held twelve full-resolution bitmaps at once,
    /// and because every image in the document was also collected up front, peak working set
    /// grew linearly with the document: 1.85 GB for a 148 MB scan. That is an out-of-memory
    /// crash in the x86 package, which only has a 2 GB address space.
    /// </summary>
    private const long InFlightBudgetBytes = 384L * 1024 * 1024;

    // Preset ladder: target raster DPI + JPEG quality. Anything above the DPI cap for the
    // page an image is drawn on is simply wasted bytes.
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

    /// <summary>Number of images to work on at once, given the worst-case cost of one.</summary>
    private static int LanesFor(long worstBytesPerImage)
    {
        if (worstBytesPerImage <= 0) return 1;
        return Math.Clamp((int)(InFlightBudgetBytes / worstBytesPerImage), 1, Environment.ProcessorCount);
    }

    /// <summary>Builds a PDF at <paramref name="outputPath"/> from <paramref name="items"/>.</summary>
    /// <returns>What was written, including any items that had to be skipped.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="items"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="outputPath"/> is empty or blank.</exception>
    /// <exception cref="PdfOperationException">
    /// The PDF could not be written for a reason the user can act on; inspect
    /// <see cref="PdfOperationException.Reason"/> rather than the message.
    /// </exception>
    public PdfBuildResult CreatePdfFromDocuments(List<DocumentItem> items, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        EnsureFileIsWritable(outputPath);
        return BuildPdf(items, outputPath);
    }

    private PdfBuildResult BuildPdf(List<DocumentItem> items, string outputPath)
    {
        var level = AppSettings.Current.ImageCompression;
        var compress = level != PdfImageCompression.None;
        var useCustomPageSize = AppSettings.Current.PaperSize != PdfPaperSize.Automatic;
        var dpi = GetRasterDpi();
        var quality = GetJpegQuality();

        Log.Information("PDF build start: mode={Mode} quality={Q}% dpi={Dpi} customPageSize={Custom} items={Count}",
            level, Math.Round(quality * 100), dpi, useCustomPageSize, items.Count);

        var inPlaceSource = WholeDocumentSource(items, useCustomPageSize);
        if (inPlaceSource != null && CompressInPlace(inPlaceSource, outputPath, items.Count, compress, dpi, quality))
            return new PdfBuildResult(items.Count, []);

        using var output = new PdfDocument();
        output.Options.CompressContentStreams = true;
        output.Options.NoCompression = false;
        output.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;
        output.Options.EnableCcittCompressionForBilevelImages = true;
        output.Info.Title = "Created with Gladhen3";
        output.Info.Creator = "Gladhen3";

        var sourceDocs = new Dictionary<string, PdfDocument>(StringComparer.OrdinalIgnoreCase);
        var sourceForms = new Dictionary<string, XPdfForm>(StringComparer.OrdinalIgnoreCase);
        var alreadyOptimised = new HashSet<int>();
        var pagesAdded = 0;
        // Items that could not be turned into a page. Dropping these silently produced a PDF
        // with pages quietly missing, so they are reported back to the caller instead.
        var skipped = new List<string>();

        var pageLongInches = useCustomPageSize ? MaxPageLongEdgeInches() : 11.0;
        var cap = dpi > 0 ? (int)Math.Ceiling(pageLongInches * dpi) : 0;
        // Source dimensions are not known until each file is opened, so budget for a large-ish
        // camera image rather than assuming they are all small - a dozen concurrent 24 MP
        // decodes would undo the whole point of the budget.
        var assumedSourcePx = Math.Max((long)cap * cap, 12_000_000L);
        var block = compress ? LanesFor((assumedSourcePx * 4) + ((long)cap * cap * 8)) : 32;

        try
        {
            for (var start = 0; start < items.Count; start += block)
            {
                var end = Math.Min(items.Count, start + block);

                var encoded = EmptyEncoded();
                if (compress)
                {
                    var paths = new List<string>();
                    for (var k = start; k < end; k++)
                    {
                        if (items[k].Type != DocumentType.Image) continue;
                        if (!paths.Contains(items[k].FilePath, StringComparer.OrdinalIgnoreCase))
                            paths.Add(items[k].FilePath);
                    }
                    if (paths.Count > 0)
                        encoded = PreEncodeImages(paths, cap, quality, block);
                }

                for (var k = start; k < end; k++)
                {
                    var item = items[k];
                    try
                    {
                        if (item.Type == DocumentType.Image)
                        {
                            if (AddImagePage(output, item.FilePath, encoded, useCustomPageSize))
                                alreadyOptimised.Add(output.PageCount - 1);
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

                            AddPdfPage(output, srcDoc.Pages[idx], useCustomPageSize, src, item.PageNumber, sourceForms);
                            pagesAdded++;
                        }
                    }
                    // One unreadable or corrupt file must not abandon the whole document, so
                    // every failure mode is isolated here - but it is recorded, not swallowed.
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Failed to add item to PDF: {Path}", item.FilePath);
                        skipped.Add(item.FileName);
                    }
                }

                encoded.Clear();
            }
        }
        finally
        {
            DisposeAll(sourceForms.Values);
            DisposeAll(sourceDocs.Values);
        }

        if (pagesAdded == 0)
            throw new PdfOperationException(PdfFailureReason.NoPages, "None of the selected items could be turned into a page.") { Path = outputPath };

        if (compress)
            CompressDocumentImages(output, dpi, quality, alreadyOptimised);

        SaveDocument(output, outputPath);

        var finalSize = new FileInfo(outputPath).Length;
        Log.Information("PDF saved: {Path} pages={Pages} skipped={Skipped} size={Size}B",
            outputPath, pagesAdded, skipped.Count, finalSize);

        return new PdfBuildResult(pagesAdded, skipped);
    }

    /// <summary>
    /// Writes the document, translating the two failures the user can actually do something
    /// about. Anything else propagates unchanged so it is visible as a defect.
    /// </summary>
    private static void SaveDocument(PdfDocument document, string outputPath)
    {
        try
        {
            document.Save(outputPath);
        }
        catch (IOException ex)
        {
            throw new PdfOperationException(PdfFailureReason.FileInUse, $"'{Path.GetFileName(outputPath)}' could not be written.", ex) { Path = outputPath };
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new PdfOperationException(PdfFailureReason.AccessDenied, $"'{Path.GetFileName(outputPath)}' could not be written.", ex) { Path = outputPath };
        }
    }

    /// <summary>
    /// Disposes everything, reporting rather than hiding any failure. Cleanup runs while an
    /// exception may already be in flight, so it must not throw a second one over the top.
    /// </summary>
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

    private static Dictionary<string, byte[]> EmptyEncoded()
        => new(StringComparer.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- size estimation

    /// <summary>Roughly what a PDF costs beyond its page content: catalog, xref, trailer.</summary>
    private const long PdfOverheadBytes = 2048;

    /// <summary>How many images to push through the real codec when sizing a job.</summary>
    private const int MaxSizeSamples = 3;

    /// <summary>
    /// Past this, a source is sized from a rule of thumb instead of being parsed. Parsing
    /// costs the same memory as saving, and doing that on every settings change would undo
    /// the work that made this service fit in a small process.
    /// </summary>
    private const long SampleParseLimitBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Predicts the size of the file <see cref="CreatePdfFromDocuments"/> would produce.
    /// </summary>
    /// <remarks>
    /// The prediction comes from running a few of the user's own images through the real
    /// encoder rather than from a fixed per-preset ratio: how well a page compresses depends
    /// almost entirely on what is on it, and a table of constants would be confidently wrong
    /// for the scans this app exists to shrink. Sampling the largest images makes the answer
    /// track the bytes that actually matter.
    ///
    /// This is deliberately synchronous and cancellable - callers run it off the UI thread and
    /// cancel it when the selection changes again.
    /// </remarks>
    public static long EstimateOutputSize(IReadOnlyList<DocumentItem> items, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0) return 0;

        var compress = AppSettings.Current.ImageCompression != PdfImageCompression.None;
        var dpi = GetRasterDpi();
        var quality = GetJpegQuality();
        var useCustomPageSize = AppSettings.Current.PaperSize != PdfPaperSize.Automatic;
        var pageLongInches = useCustomPageSize ? MaxPageLongEdgeInches() : 11.0;
        var cap = dpi > 0 ? (int)Math.Ceiling(pageLongInches * dpi) : 0;

        var total = PdfOverheadBytes;

        var imagePaths = new List<string>();
        var pdfGroups = new Dictionary<string, (List<int> Pages, int TotalPages)>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
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

                if (!pdfGroups.TryGetValue(src, out var group))
                {
                    group = ([], Math.Max(1, item.TotalPages));
                    pdfGroups[src] = group;
                }
                group.Pages.Add(item.PageNumber);
            }
        }

        total += EstimateImages(imagePaths, compress, cap, quality, cancellationToken);

        foreach (var (path, group) in pdfGroups)
            total += EstimatePdfPages(path, group.Pages, group.TotalPages, compress, dpi, quality, cancellationToken);

        return total;
    }

    private static long EstimateImages(List<string> paths, bool compress, int cap, double quality, CancellationToken cancellationToken)
    {
        if (paths.Count == 0) return 0;

        var sizes = new List<(string Path, long Length)>(paths.Count);
        foreach (var path in paths)
        {
            try
            {
                var length = new FileInfo(path).Length;
                if (length > 0) sizes.Add((path, length));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Debug(ex, "Skipping {Path} while estimating", path);
            }
        }

        long total = 0;
        if (!compress)
        {
            foreach (var entry in sizes) total += entry.Length;
            return total;
        }

        // The biggest files dominate the total, so they are the ones worth sampling.
        double ratioSum = 0;
        var sampled = 0;
        foreach (var entry in sizes.OrderByDescending(e => e.Length).Take(MaxSizeSamples))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var encoded = EncodeSourceImage(entry.Path, cap, quality);
                if (encoded is { Length: > 0 })
                {
                    ratioSum += Math.Min(1.0, (double)encoded.Length / entry.Length);
                    sampled++;
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Could not sample {Path} while estimating", entry.Path);
            }
        }

        var ratio = sampled > 0 ? ratioSum / sampled : 1.0;
        foreach (var entry in sizes) total += (long)(entry.Length * ratio);
        return total;
    }

    private static long EstimatePdfPages(string path, List<int> pageNumbers, int totalPages, bool compress, int dpi, double quality, CancellationToken cancellationToken)
    {
        long fileLength;
        try
        {
            fileLength = new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug(ex, "Skipping {Path} while estimating", path);
            return 0;
        }

        var selectedFraction = totalPages > 0 ? Math.Min(1.0, (double)pageNumbers.Count / totalPages) : 1.0;
        var selectedBytes = fileLength * selectedFraction;

        if (!compress)
            return (long)selectedBytes;

        if (fileLength > SampleParseLimitBytes)
        {
            // Too big to parse just to answer a question the user has not committed to yet.
            Log.Debug("Estimating {Path} from a rule of thumb: {Size}B is past the parse limit", path, fileLength);
            return (long)(selectedBytes * FallbackCompressionRatio());
        }

        try
        {
            using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);

            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var jobs = new List<ImageJob>();

            foreach (var pageNumber in pageNumbers)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var index = pageNumber - 1;
                if (index < 0 || index >= doc.PageCount) continue;

                var page = doc.Pages[index];
                var longInches = Math.Max(page.Width.Inch, page.Height.Inch);
                if (longInches <= 0) longInches = 11.0;

                CollectImages(page.Elements.GetDictionary("/Resources") ?? page.Resources, longInches, seen, jobs, 0);
            }

            long imageBytes = 0;
            foreach (var job in jobs) imageBytes += job.OriginalLength;

            if (imageBytes == 0)
            {
                // Nothing here is an image, so compression has almost nothing to act on.
                return (long)selectedBytes;
            }

            long sampledBefore = 0, sampledAfter = 0;
            foreach (var job in jobs.OrderByDescending(j => j.OriginalLength).Take(MaxSizeSamples))
            {
                cancellationToken.ThrowIfCancellationRequested();

                job.Prepare();
                try
                {
                    job.Recompress(dpi, quality);
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Could not sample an image while estimating {Path}", path);
                }

                sampledBefore += job.OriginalLength;
                sampledAfter += job.NewBytes is { Length: > 0 } && job.NewBytes.Length < job.OriginalLength
                    ? job.NewBytes.Length
                    : job.OriginalLength;
                job.Release();
            }

            var ratio = sampledBefore > 0 ? (double)sampledAfter / sampledBefore : 1.0;

            // Text, fonts and structure survive compression essentially untouched; only the
            // image bytes move.
            var nonImageBytes = Math.Max(0, selectedBytes - imageBytes);
            var estimate = (long)(nonImageBytes + (imageBytes * ratio));

            if (fileLength > 32L * 1024 * 1024)
            {
                // A big source was just parsed only to answer a question. Hand the memory back
                // rather than leaving it resident behind an idle window.
                doc.Dispose();
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
                GC.WaitForPendingFinalizers();
            }

            return estimate;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // An estimate is a nicety. If the file will not open, fall back rather than
            // failing the whole calculation - saving will report the real problem.
            Log.Debug(ex, "Falling back to a rule of thumb for {Path}", path);
            return (long)(selectedBytes * FallbackCompressionRatio());
        }
    }

    /// <summary>
    /// Rough per-preset ratios, used only when a source is too large or too broken to sample.
    /// Deliberately conservative: a number that is too low reads as a broken promise.
    /// </summary>
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

    private static bool CompressInPlace(string sourcePath, string outputPath, int expectedPages, bool compress, int dpi, double quality)
    {
        PdfDocument? doc = null;
        try
        {
            // A probe, not an operation: encrypted, damaged or otherwise unusual files fail
            // here in whatever way PDFsharp sees fit, and every one of them simply means
            // "use the page-import path instead". The caller is not left without a route.
            try
            {
                doc = PdfReader.Open(sourcePath, PdfDocumentOpenMode.Modify);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "In-place rewrite unavailable for {Path}; importing pages instead", sourcePath);
                return false;
            }

            if (doc.PageCount != expectedPages) return false;

            doc.Options.CompressContentStreams = true;
            doc.Options.NoCompression = false;
            doc.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;
            doc.Options.EnableCcittCompressionForBilevelImages = true;

            if (compress)
                CompressDocumentImages(doc, dpi, quality);

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

    private static bool AddImagePage(PdfDocument output, string imagePath, Dictionary<string, byte[]> encoded, bool useCustomPageSize)
    {
        XImage image;
        MemoryStream? owned = null;
        var preEncoded = false;
        if (encoded.TryGetValue(imagePath, out var bytes) && bytes.Length > 0)
        {
            owned = new MemoryStream(bytes, writable: false);
            image = XImage.FromStream(owned);
            preEncoded = true;
        }
        else
        {
            image = XImage.FromFile(imagePath);
        }

        try
        {
            var imgW = image.PixelWidth;
            var imgH = image.PixelHeight;
            var sourceLandscape = imgW > imgH;
            var targetLandscape = ResolveTargetLandscape(sourceLandscape);

            var (pageW, pageH) = useCustomPageSize
                ? GetCustomPageSizePoints(targetLandscape)
                : GetAutoPageSizePoints(imgW, imgH, sourceLandscape, targetLandscape);

            var page = output.AddPage();
            page.Width = XUnit.FromPoint(pageW);
            page.Height = XUnit.FromPoint(pageH);

            using var gfx = XGraphics.FromPdfPage(page);

            var margin = useCustomPageSize ? AppSettings.Current.GetMarginInPoints() : 0d;
            var availW = Math.Max(1d, pageW - (2d * margin));
            var availH = Math.Max(1d, pageH - (2d * margin));
            var scale = Math.Min(availW / imgW, availH / imgH);
            var drawW = imgW * scale;
            var drawH = imgH * scale;

            gfx.DrawImage(image, (pageW - drawW) / 2d, (pageH - drawH) / 2d, drawW, drawH);
            return preEncoded;
        }
        finally
        {
            image.Dispose();
            owned?.Dispose();
        }
    }

    private static void AddPdfPage(PdfDocument output, PdfPage sourcePage, bool useCustomPageSize, string sourcePath, int pageNumber, Dictionary<string, XPdfForm> formCache)
    {
        if (!useCustomPageSize && AppSettings.Current.Orientation == PdfPaperOrientation.Automatic)
        {
            output.AddPage(sourcePage);
            return;
        }

        var srcW = sourcePage.Width.Point;
        var srcH = sourcePage.Height.Point;
        var sourceLandscape = srcW > srcH;
        var targetLandscape = ResolveTargetLandscape(sourceLandscape);

        var (pageW, pageH) = useCustomPageSize
            ? GetCustomPageSizePoints(targetLandscape)
            : GetAutoPageSizePoints(srcW, srcH, sourceLandscape, targetLandscape);

        var page = output.AddPage();
        page.Width = XUnit.FromPoint(pageW);
        page.Height = XUnit.FromPoint(pageH);

        if (!formCache.TryGetValue(sourcePath, out var form))
        {
            form = XPdfForm.FromFile(sourcePath);
            formCache[sourcePath] = form;
        }
        form.PageNumber = pageNumber;

        using var gfx = XGraphics.FromPdfPage(page);
        var margin = useCustomPageSize ? AppSettings.Current.GetMarginInPoints() : 0d;
        var availW = Math.Max(1d, pageW - (2d * margin));
        var availH = Math.Max(1d, pageH - (2d * margin));
        var scale = Math.Min(availW / srcW, availH / srcH);
        var drawW = srcW * scale;
        var drawH = srcH * scale;

        gfx.DrawImage(form, (pageW - drawW) / 2d, (pageH - drawH) / 2d, drawW, drawH);
    }

    internal static void CompressDocumentImages(PdfDocument doc, int targetDpi, double quality, HashSet<int>? skipPages = null)
    {
        if (targetDpi <= 0) return;

        // Collect references only - no pixel data is touched here. An XObject shared by N
        // pages must only be processed once.
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
            return;
        }

        var worst = 0L;
        foreach (var j in jobs) worst = Math.Max(worst, j.EstimateBytes(targetDpi));
        var lanes = LanesFor(worst);

        Log.Information("Embedded image recompression: {Count} candidate images, {Lanes} at a time", jobs.Count, lanes);

        long before = 0, after = 0;
        var replaced = 0;

        for (var start = 0; start < jobs.Count; start += lanes)
        {
            var end = Math.Min(jobs.Count, start + lanes);

            // Reading the stream (and inflating it, for FlateDecode) touches the PDFsharp
            // object model, so it stays on this thread; the workers only see byte arrays.
            for (var i = start; i < end; i++) jobs[i].Prepare();

            Parallel.For(start, end, new ParallelOptions { MaxDegreeOfParallelism = lanes }, i =>
            {
                // Images are independent, and a codec that cannot handle one of them says so
                // in whatever way it likes. Failing here just leaves that image untouched;
                // letting it escape would abandon a document that is otherwise fine.
                try
                {
                    jobs[i].Recompress(targetDpi, quality);
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
                // Require a real win. Swapping in a stream that is 0.1% smaller buys nothing
                // and costs a second generation of JPEG loss.
                if (job.NewBytes == null || job.NewBytes.Length >= job.OriginalLength * 0.98)
                {
                    after += job.OriginalLength;
                }
                else
                {
                    // One malformed image must not sink the whole save - the in-place path
                    // has no second attempt to fall back to.
                    try
                    {
                        job.Apply();
                        after += job.NewBytes.Length;
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

        Log.Information("Embedded image recompression: {Replaced}/{Total} replaced, image bytes {Before}B -> {After}B ({Ratio:P0})",
            replaced, jobs.Count, before, after, before > 0 ? (double)after / before : 1d);
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

            string subtype;
            try
            {
                subtype = dict.Elements.GetName("/Subtype");
            }
            catch (InvalidOperationException ex)
            {
                Log.Debug(ex, "Skipping XObject {Key} with a malformed /Subtype", key);
                continue;
            }

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

    private sealed class ImageJob
    {
        private PdfDictionary _dict = null!;
        private byte[]? _raw;
        private bool _rawIsJpeg;
        private int _width;
        private int _height;
        private int _components;
        private double _pageLongInches;
        private int _newWidth;
        private int _newHeight;

        public int OriginalLength { get; private set; }
        public byte[]? NewBytes { get; private set; }

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

        public void Recompress(int targetDpi, double quality)
        {
            if (_raw == null) return;

            var target = TargetLongEdge(targetDpi);

            using var bitmap = _rawIsJpeg
                ? WicDecodeScaled(_raw, target)
                : RawToBitmap(_raw, _width, _height, _components, target);
            if (bitmap == null) return;

            var jpeg = WicEncodeJpeg(bitmap, quality, out var nw, out var nh);
            if (jpeg == null || jpeg.Length == 0) return;

            NewBytes = jpeg;
            _newWidth = nw;
            _newHeight = nh;
        }

        public void Apply()
        {
            var e = _dict.Elements;
            _dict.Stream!.Value = NewBytes!;
            e.SetName("/Filter", "/DCTDecode");
            e.SetInteger("/Width", _newWidth);
            e.SetInteger("/Height", _newHeight);
            e.SetInteger("/BitsPerComponent", 8);
            e.SetName("/ColorSpace", "/DeviceRGB");
            e.SetInteger("/Length", NewBytes!.Length);
            e.Remove("/DecodeParms");
            e.Remove("/Decode");
            e.Remove("/ColorTransform");
        }

        public void Release()
        {
            _raw = null;
            NewBytes = null;
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


    private Dictionary<string, byte[]> PreEncodeImages(List<string> paths, int cap, double quality, int lanes)
    {
        var results = new ConcurrentDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        Parallel.ForEach(paths, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, lanes) }, path =>
        {
            try
            {
                var bytes = EncodeSourceImage(path, cap, quality);
                if (bytes == null || bytes.Length == 0) return;

                var original = new FileInfo(path).Length;
                if (bytes.Length < original)
                {
                    results[path] = bytes;
                    Log.Information("Pre-encoded {Name}: {Src}B -> {Dst}B ({Ratio:P0})",
                        Path.GetFileName(path), original, bytes.Length, (double)bytes.Length / original);
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

        return new Dictionary<string, byte[]>(results, StringComparer.OrdinalIgnoreCase);
    }

    private static byte[]? EncodeSourceImage(string path, int maxLongEdge, double quality)
    {
        using var fs = File.OpenRead(path);
        using var ras = fs.AsRandomAccessStream();
        var decoder = BitmapDecoder.CreateAsync(ras).GetAwaiter().GetResult();

        var hasAlpha = decoder.BitmapAlphaMode != BitmapAlphaMode.Ignore;
        var alphaMode = hasAlpha ? BitmapAlphaMode.Premultiplied : BitmapAlphaMode.Ignore;

        using var bitmap = DecodeScaled(decoder, maxLongEdge, alphaMode);
        if (bitmap == null) return null;

        return hasAlpha
            ? WicEncodePng(bitmap, out _, out _)
            : WicEncodeJpeg(bitmap, quality, out _, out _);
    }

    /// <summary>Decodes at (or just above) the requested long edge rather than at full
    /// resolution. For JPEG this lets WIC scale in the DCT domain, which is both far cheaper
    /// and far less memory than decoding everything and resampling afterwards.</summary>
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
        WriteAll(ras, data);
        var decoder = BitmapDecoder.CreateAsync(ras).GetAwaiter().GetResult();
        return DecodeScaled(decoder, maxLongEdge, BitmapAlphaMode.Ignore);
    }

    /// <summary>
    /// Wraps raw 8-bit grey or RGB samples (already Flate-decoded) as a BGRA bitmap,
    /// box-averaging down to the target long edge on the way. Building the bitmap at full
    /// resolution and scaling in the encoder allocated the full-size buffer for nothing.
    /// </summary>
    private static SoftwareBitmap? RawToBitmap(byte[] samples, int width, int height, int components, int maxLongEdge)
    {
        if (samples == null || samples.LongLength < (long)width * height * components) return null;

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

    private static byte[]? WicEncodeJpeg(SoftwareBitmap bitmap, double quality, out int outWidth, out int outHeight)
    {
        // ImageQuality only takes effect when handed to CreateAsync. Setting it afterwards
        // through BitmapProperties writes metadata and silently does not change encoding -
        // which is why the old quality slider barely moved the output size.
        var props = new BitmapPropertySet
        {
            { "ImageQuality", new BitmapTypedValue((float)quality, PropertyType.Single) }
        };
        return WicEncode(BitmapEncoder.JpegEncoderId, props, bitmap, out outWidth, out outHeight);
    }

    private static byte[]? WicEncodePng(SoftwareBitmap bitmap, out int outWidth, out int outHeight)
        => WicEncode(BitmapEncoder.PngEncoderId, null, bitmap, out outWidth, out outHeight);

    /// <summary>Encodes at the bitmap's own size; scaling already happened at decode time.</summary>
    private static byte[]? WicEncode(Guid encoderId, BitmapPropertySet? props, SoftwareBitmap bitmap, out int outWidth, out int outHeight)
    {
        outWidth = bitmap.PixelWidth;
        outHeight = bitmap.PixelHeight;

        using var ras = new InMemoryRandomAccessStream();
        var encoder = (props == null
            ? BitmapEncoder.CreateAsync(encoderId, ras)
            : BitmapEncoder.CreateAsync(encoderId, ras, props)).GetAwaiter().GetResult();

        encoder.SetSoftwareBitmap(bitmap);
        encoder.FlushAsync().GetAwaiter().GetResult();
        return ReadAll(ras);
    }

    private static void WriteAll(IRandomAccessStream stream, byte[] data)
    {
        // Straight buffer write; DataWriter would stage another copy of the whole image.
        stream.WriteAsync(data.AsBuffer()).AsTask().GetAwaiter().GetResult();
        stream.Seek(0);
    }

    private static byte[] ReadAll(IRandomAccessStream stream)
    {
        var size = (uint)stream.Size;
        var bytes = new byte[size];
        if (size == 0) return bytes;

        stream.Seek(0);
        stream.ReadAsync(bytes.AsBuffer(), size, InputStreamOptions.None).AsTask().GetAwaiter().GetResult();
        return bytes;
    }

    // ---------------------------------------------------------------- page geometry

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

    /// <summary>
    /// Fails early, before any work is done, if the destination cannot be written.
    /// </summary>
    /// <remarks>
    /// This is a courtesy check, not a guarantee: the file can still be locked between here
    /// and the save, which is why <see cref="SaveDocument"/> translates the same failures.
    /// </remarks>
    /// <exception cref="PdfOperationException">The destination is missing, locked, or denied.</exception>
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
