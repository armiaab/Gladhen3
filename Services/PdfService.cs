using Gladhen3.Models;
using ImageMagick;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
#if !UNIT_TEST
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
#endif

namespace Gladhen3.Services;

public class PdfService
{
    private readonly Dictionary<string, XImage>   _imageCache   = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, XPdfForm> _pdfFormCache = new(StringComparer.OrdinalIgnoreCase);
    // ConcurrentBag: Add is called from parallel pre-conversion tasks
    private readonly ConcurrentBag<string> _convertedImageTempFiles = new();

    // ── Public entry point ────────────────────────────────────────────────────

    public void CreatePdfFromDocuments(List<DocumentItem> items, string outputPath)
    {
        var tempFiles = new List<string>();
        var useCustomPageSize = AppSettings.Current.PaperSize != PdfPaperSize.Automatic;

        try
        {
            try { EnsureFileIsWritable(outputPath); }
            catch (IOException ex)
            {
                throw new IOException(
                    $"Cannot save PDF to '{Path.GetFileName(outputPath)}' because it is open in another application. Close it and try again.", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new UnauthorizedAccessException(
                    $"Access denied saving PDF to '{Path.GetFileName(outputPath)}'. Check permissions or choose another location.", ex);
            }

            try
            {
                if (useCustomPageSize)
                    CreatePdfWithCustomPageSize(items, outputPath);
                else
                    CreatePdfWithAutomaticPageSize(items, outputPath, tempFiles);

                Log.Information("PDF created with {PageCount} pages: {OutputPath}", items.Count, outputPath);
            }
            catch (IOException ex)
            {
                throw new IOException(
                    $"Cannot save PDF to '{Path.GetFileName(outputPath)}' because it is open in another application or an I/O error occurred. Close other apps and try again.", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new UnauthorizedAccessException(
                    $"Access denied saving PDF to '{Path.GetFileName(outputPath)}'. Check permissions or choose another location.", ex);
            }
        }
        finally
        {
            foreach (var f in tempFiles)
                try { File.Delete(f); } catch { }
            foreach (var f in _convertedImageTempFiles)
                try { File.Delete(f); } catch { }
            _convertedImageTempFiles.Clear();
            ClearCaches();
        }
    }

    // ── Automatic page size ───────────────────────────────────────────────────

    private void CreatePdfWithAutomaticPageSize(
        List<DocumentItem> items, string outputPath, List<string> tempFiles)
    {
        var compress = AppSettings.Current.ImageCompression != PdfImageCompression.None;

        if (!compress)
        {
            // Fast path: merge PDF pages directly; wrap images in single-page PDFs
            var pageList = new List<(string PdfPath, int PageIndex)>(items.Count);
            foreach (var item in items)
            {
                if (item.Type == DocumentType.Image)
                {
                    var tmpPath = Path.Combine(Path.GetTempPath(), $"gladhen_{Guid.NewGuid():N}.pdf");
                    try
                    {
                        CreateSingleImagePdfWithFallback(item.FilePath, tmpPath);
                        tempFiles.Add(tmpPath);
                        pageList.Add((tmpPath, 0));
                    }
                    catch (Exception ex) { Log.Error(ex, "Failed to create PDF from image: {Path}", item.FilePath); }
                }
                else if (item.Type == DocumentType.PdfPage)
                {
                    var src = item.SourcePdfPath ?? item.FilePath;
                    if (!string.IsNullOrEmpty(src))
                        pageList.Add((src, item.PageNumber - 1));
                }
            }
            if (pageList.Count == 0)
                throw new InvalidOperationException("No pages could be processed. Please check the image formats.");
            MergePagesOptimized(pageList, outputPath);
            return;
        }

        // Compression path
        // ① Pre-convert all source images to JPEG in parallel (reduces image file size)
        var imageKeys = items
            .Where(i => i.Type == DocumentType.Image)
            .Select(i => i.FilePath)
            .Distinct()
            .ToList();

        var convertedImages = imageKeys.Count > 0
            ? PreConvertImagesAsync(imageKeys).GetAwaiter().GetResult()
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // ② Build output PDF directly: images from pre-converted JPEGs, PDF pages re-rendered
        //    via XPdfForm (keeps vector text sharp). Embedded raster images inside those PDF
        //    pages are recompressed afterwards by CompressEmbeddedPdfImages.
        BuildCompressedAutoPdf(items, outputPath, convertedImages);
    }

    /// <summary>
    /// Compression variant of the automatic-size path.
    /// Images use pre-converted JPEGs; PDF pages are re-rendered via XPdfForm at their
    /// original dimensions so PDFsharp's deflate compression is applied to content streams.
    /// </summary>
    private void BuildCompressedAutoPdf(
        List<DocumentItem> items, string outputPath,
        Dictionary<string, string> convertedImages)
    {
        using var doc = new PdfDocument();
        doc.Info.Title = "Created with Gladhen3";
        var pagesAdded = 0;

        foreach (var item in items)
        {
            if (item.Type == DocumentType.Image)
            {
                try
                {
                    var src = convertedImages.GetValueOrDefault(item.FilePath, item.FilePath);
                    using var xImage = XImage.FromFile(src);
                    var wPt = xImage.PointWidth;
                    var hPt = xImage.PointHeight;

                    var isLandscape = wPt > hPt;
                    var useLandscape = AppSettings.Current.Orientation switch
                    {
                        PdfPaperOrientation.Portrait  => false,
                        PdfPaperOrientation.Landscape => true,
                        _                             => isLandscape
                    };

                    var page = doc.AddPage();
                    page.Width  = new XUnit(useLandscape == isLandscape ? wPt : hPt);
                    page.Height = new XUnit(useLandscape == isLandscape ? hPt : wPt);

                    using var gfx = XGraphics.FromPdfPage(page);
                    var s  = Math.Min(page.Width.Point / wPt, page.Height.Point / hPt);
                    var dw = wPt * s;
                    var dh = hPt * s;
                    gfx.DrawImage(xImage,
                        (page.Width.Point  - dw) / 2,
                        (page.Height.Point - dh) / 2, dw, dh);
                    pagesAdded++;
                }
                catch (Exception ex) { Log.Error(ex, "Failed to add image: {Path}", item.FilePath); }
            }
            else if (item.Type == DocumentType.PdfPage)
            {
                var src = item.SourcePdfPath ?? item.FilePath;
                if (string.IsNullOrEmpty(src)) continue;

                try
                {
                    var form = GetOrCreateXPdfForm(src);
                    form.PageIndex = item.PageNumber - 1;

                    var srcW = form.PointWidth;
                    var srcH = form.PointHeight;

                    var isLandscape = srcW > srcH;
                    var targetLandscape = AppSettings.Current.Orientation switch
                    {
                        PdfPaperOrientation.Portrait  => false,
                        PdfPaperOrientation.Landscape => true,
                        _                             => isLandscape
                    };

                    var page = doc.AddPage();
                    // Preserve original page dimensions (automatic mode)
                    page.Width  = new XUnit(targetLandscape == isLandscape ? srcW : srcH);
                    page.Height = new XUnit(targetLandscape == isLandscape ? srcH : srcW);

                    using var gfx = XGraphics.FromPdfPage(page);
                    var s  = Math.Min(page.Width.Point / srcW, page.Height.Point / srcH);
                    gfx.DrawImage(form,
                        (page.Width.Point  - srcW * s) / 2,
                        (page.Height.Point - srcH * s) / 2,
                        srcW * s, srcH * s);
                    pagesAdded++;
                }
                catch (Exception ex) { Log.Error(ex, "Failed to add PDF page: {Path}", src); }
            }
        }

        if (pagesAdded == 0)
            throw new InvalidOperationException("No pages could be processed. Please check the image formats.");
        CompressEmbeddedPdfImages(doc);
        doc.Save(outputPath);
    }

    // ── Custom page size ──────────────────────────────────────────────────────

    private void CreatePdfWithCustomPageSize(List<DocumentItem> items, string outputPath)
    {
        var compress = AppSettings.Current.ImageCompression != PdfImageCompression.None;

        // Pre-convert source images to JPEG in parallel when compression is on
        var convertedImages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (compress)
        {
            var imageKeys = items
                .Where(i => i.Type == DocumentType.Image)
                .Select(i => i.FilePath)
                .Distinct()
                .ToList();
            if (imageKeys.Count > 0)
                convertedImages = PreConvertImagesAsync(imageKeys).GetAwaiter().GetResult();
        }

        using var outputDocument = new PdfDocument();
        outputDocument.Info.Title = "Created with Gladhen3";

        var currentPdfPath = string.Empty;
        PdfDocument? currentInputDoc = null;
        var pagesAdded = 0;

        try
        {
            foreach (var item in items)
            {
                if (item.Type == DocumentType.Image)
                {
                    try
                    {
                        if (compress && convertedImages.TryGetValue(item.FilePath, out var convPath))
                            AddImageToDocumentFromPath(outputDocument, convPath);
                        else
                            AddImageToDocumentWithFallback(outputDocument, item.FilePath);
                        pagesAdded++;
                    }
                    catch (Exception ex) { Log.Error(ex, "Failed to add image to PDF: {Path}", item.FilePath); }
                }
                else if (item.Type == DocumentType.PdfPage)
                {
                    var sourcePath = item.SourcePdfPath ?? item.FilePath;
                    if (string.IsNullOrEmpty(sourcePath)) continue;

                    // PDF pages are always re-rendered via XPdfForm onto the target page size.
                    // When compression is on, embedded raster images are recompressed later
                    // by CompressEmbeddedPdfImages; vector text stays sharp with no rasterization.
                    if (sourcePath != currentPdfPath)
                    {
                        currentInputDoc?.Dispose();
                        currentInputDoc = PdfReader.Open(sourcePath, PdfDocumentOpenMode.Import);
                        currentPdfPath = sourcePath;
                    }

                    if (currentInputDoc != null && item.PageNumber > 0 && item.PageNumber <= currentInputDoc.PageCount)
                    {
                        AddPdfPageToDocumentOptimized(outputDocument, sourcePath, item.PageNumber - 1);
                        pagesAdded++;
                    }
                }
            }
        }
        finally { currentInputDoc?.Dispose(); }

        if (pagesAdded == 0)
            throw new InvalidOperationException("No pages could be processed. Please check the image formats.");

        if (compress)
            CompressEmbeddedPdfImages(outputDocument);

        try { outputDocument.Save(outputPath); }
        catch (IOException ex)
        {
            throw new IOException(
                $"Cannot save PDF to '{Path.GetFileName(outputPath)}' because it is open in another application. Close it and try again.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UnauthorizedAccessException(
                $"Access denied saving PDF to '{Path.GetFileName(outputPath)}'. Check permissions or choose another location.", ex);
        }
    }

    // ── Per-page drawing helpers ──────────────────────────────────────────────

    /// <summary>
    /// Adds an image file to the document using the instance cache with format fallback.
    /// Used in the no-compression path where the same source file may appear multiple times.
    /// </summary>
    private void AddImageToDocumentWithFallback(PdfDocument document, string imagePath)
        => AddImageToDocumentCore(document, GetOrCreateXImageWithFallback(imagePath));

    /// <summary>
    /// Adds an image to the document by loading directly from <paramref name="imagePath"/>.
    /// Used when the file is a pre-converted temp JPEG that won't be reused.
    /// </summary>
    private void AddImageToDocumentFromPath(PdfDocument document, string imagePath)
    {
        using var xImage = XImage.FromFile(imagePath);
        AddImageToDocumentCore(document, xImage);
    }

    /// <summary>
    /// Core image layout: places xImage on a new page sized to the current paper setting.
    /// Photos are never upscaled beyond 1× their natural size.
    /// </summary>
    private void AddImageToDocumentCore(PdfDocument document, XImage xImage)
    {
        var page = document.AddPage();
        var imgW = xImage.PointWidth;
        var imgH = xImage.PointHeight;

        var (baseW, baseH) = GetPageDimensionsInPortrait(AppSettings.Current.PaperSize);

        var useLandscape = AppSettings.Current.Orientation switch
        {
            PdfPaperOrientation.Portrait  => false,
            PdfPaperOrientation.Landscape => true,
            _                             => imgW > imgH
        };

        page.Width  = new XUnit(useLandscape ? baseH : baseW);
        page.Height = new XUnit(useLandscape ? baseW : baseH);

        using var gfx = XGraphics.FromPdfPage(page);

        var margin = AppSettings.Current.GetMarginInPoints();
        var availW = page.Width.Point  - margin * 2;
        var availH = page.Height.Point - margin * 2;
        var scale  = Math.Min(Math.Min(availW / imgW, availH / imgH), 1.0);
        var drawW  = imgW * scale;
        var drawH  = imgH * scale;

        gfx.DrawImage(xImage,
            (page.Width.Point  - drawW) / 2,
            (page.Height.Point - drawH) / 2,
            drawW, drawH);
    }

    /// <summary>
    /// Re-renders a PDF page via XPdfForm onto the target paper size.
    /// Vector content is resolution-independent and is never upscale-limited.
    /// </summary>
    private void AddPdfPageToDocumentOptimized(PdfDocument document, string sourcePath, int pageIndex)
    {
        var form = GetOrCreateXPdfForm(sourcePath);
        form.PageIndex = pageIndex;

        var srcW = form.PointWidth;
        var srcH = form.PointHeight;

        var (baseW, baseH) = GetPageDimensionsInPortrait(AppSettings.Current.PaperSize);

        var useLandscape = AppSettings.Current.Orientation switch
        {
            PdfPaperOrientation.Portrait  => false,
            PdfPaperOrientation.Landscape => true,
            _                             => srcW > srcH
        };

        var newPage = document.AddPage();
        newPage.Width  = new XUnit(useLandscape ? baseH : baseW);
        newPage.Height = new XUnit(useLandscape ? baseW : baseH);

        using var gfx = XGraphics.FromPdfPage(newPage);

        var margin = AppSettings.Current.GetMarginInPoints();
        var availW = newPage.Width.Point  - margin * 2;
        var availH = newPage.Height.Point - margin * 2;
        var scale  = Math.Min(availW / srcW, availH / srcH);
        var drawW  = srcW * scale;
        var drawH  = srcH * scale;

        gfx.DrawImage(form,
            (newPage.Width.Point  - drawW) / 2,
            (newPage.Height.Point - drawH) / 2,
            drawW, drawH);
    }

    // ── Image in automatic-size mode ──────────────────────────────────────────

    private void CreateSingleImagePdfWithFallback(string imagePath, string outputPath)
    {
        using var document = new PdfDocument();
        var page = document.AddPage();

        using var xImage = LoadImageWithFallback(imagePath);
        var imgW = xImage.PointWidth;
        var imgH = xImage.PointHeight;

        page.Width  = new XUnit(imgW);
        page.Height = new XUnit(imgH);

        var isImgLandscape     = imgW > imgH;
        var currentIsLandscape = page.Width.Point > page.Height.Point;
        var targetLandscape = AppSettings.Current.Orientation switch
        {
            PdfPaperOrientation.Portrait  => false,
            PdfPaperOrientation.Landscape => true,
            _                             => isImgLandscape
        };

        if (targetLandscape != currentIsLandscape)
            (page.Width, page.Height) = (page.Height, page.Width);

        using var gfx = XGraphics.FromPdfPage(page);
        var s  = Math.Min(page.Width.Point / imgW, page.Height.Point / imgH);
        gfx.DrawImage(xImage,
            (page.Width.Point  - imgW * s) / 2,
            (page.Height.Point - imgH * s) / 2,
            imgW * s, imgH * s);

        try { document.Save(outputPath); }
        catch (IOException ex)
        {
            throw new IOException(
                $"Cannot save PDF to '{Path.GetFileName(outputPath)}' because it is open in another application. Close it and try again.", ex);
        }
    }

    // ── Image loading & caching ───────────────────────────────────────────────

    private XImage LoadImageWithFallback(string imagePath)
    {
        if (AppSettings.Current.ImageCompression != PdfImageCompression.None)
        {
            var compressed = ConvertImageToCompatibleFormat(imagePath);
            if (compressed != null) return XImage.FromFile(compressed);
        }

        try { return XImage.FromFile(imagePath); }
        catch (Exception ex)
        {
            Log.Warning(ex, "Direct image load failed for: {Path}, attempting conversion", imagePath);
        }

        var converted = ConvertImageToCompatibleFormat(imagePath);
        if (converted != null)
        {
            try { return XImage.FromFile(converted); }
            catch (Exception ex) { Log.Error(ex, "Failed to load converted image: {Path}", converted); }
        }

        throw new InvalidOperationException($"Unable to load or convert image: {imagePath}");
    }

    private XImage GetOrCreateXImageWithFallback(string imagePath)
    {
        if (_imageCache.TryGetValue(imagePath, out var cached)) return cached;
        var xImage = LoadImageWithFallback(imagePath);
        _imageCache[imagePath] = xImage;
        return xImage;
    }

    private XPdfForm GetOrCreateXPdfForm(string pdfPath)
    {
        if (_pdfFormCache.TryGetValue(pdfPath, out var cached)) return cached;
        var form = XPdfForm.FromFile(pdfPath);
        _pdfFormCache[pdfPath] = form;
        return form;
    }

    private void ClearCaches()
    {
        foreach (var img  in _imageCache.Values)  try { img.Dispose();  } catch { }
        foreach (var form in _pdfFormCache.Values) try { form.Dispose(); } catch { }
        _imageCache.Clear();
        _pdfFormCache.Clear();
    }

    // ── Static helpers ────────────────────────────────────────────────────────

    internal static double GetJpegQuality() => AppSettings.Current.ImageCompression switch
    {
        PdfImageCompression.Low    => 0.85,
        PdfImageCompression.Medium => 0.65,
        PdfImageCompression.High   => 0.40,
        _                          => 0.92
    };

    internal static uint GetRasterDpi() => AppSettings.Current.ImageCompression switch
    {
        PdfImageCompression.Low    => 150,
        PdfImageCompression.Medium => 120,
        PdfImageCompression.High   => 96,
        _                          => 150
    };

    private static class PageDimensions
    {
        public static readonly (double W, double H) A4     = (XUnit.FromMillimeter(210).Point, XUnit.FromMillimeter(297).Point);
        public static readonly (double W, double H) Letter = (XUnit.FromInch(8.5).Point,       XUnit.FromInch(11).Point);
        public static readonly (double W, double H) Legal  = (XUnit.FromInch(8.5).Point,       XUnit.FromInch(14).Point);
        public static readonly (double W, double H) A3     = (XUnit.FromMillimeter(297).Point, XUnit.FromMillimeter(420).Point);
    }

    internal static (double Width, double Height) GetPageDimensionsInPortrait(PdfPaperSize size)
    {
        var (w, h) = size switch
        {
            PdfPaperSize.A4     => PageDimensions.A4,
            PdfPaperSize.Letter => PageDimensions.Letter,
            PdfPaperSize.Legal  => PageDimensions.Legal,
            PdfPaperSize.A3     => PageDimensions.A3,
            PdfPaperSize.Custom => (AppSettings.Current.GetCustomWidthInPoints(),
                                    AppSettings.Current.GetCustomHeightInPoints()),
            _                   => PageDimensions.A4
        };
        return w > h ? (h, w) : (w, h);
    }

    internal static void EnsureFileIsWritable(string filePath)
    {
        if (!File.Exists(filePath))
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                throw new InvalidOperationException($"Directory does not exist: {dir}");
            return;
        }

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new IOException(
                $"Cannot save to '{Path.GetFileName(filePath)}' because it is currently open in another application.\n\nPlease close the file in the other application and try again.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UnauthorizedAccessException(
                $"Cannot save to '{Path.GetFileName(filePath)}' because access is denied.\n\nPlease check file permissions or choose a different location.", ex);
        }
    }

    private static void MergePagesOptimized(List<(string PdfPath, int PageIndex)> pageList, string outputPath)
    {
        using var outputDocument = new PdfDocument();
        outputDocument.Info.Title = "Created with Gladhen3";

        var documentCache = new Dictionary<string, PdfDocument>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var (pdfPath, pageIndex) in pageList)
            {
                if (!documentCache.TryGetValue(pdfPath, out var inputDoc))
                {
                    inputDoc = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Import);
                    documentCache[pdfPath] = inputDoc;
                }

                if (pageIndex >= 0 && pageIndex < inputDoc.PageCount)
                    outputDocument.AddPage(inputDoc.Pages[pageIndex]);
            }

            try { outputDocument.Save(outputPath); }
            catch (IOException ex)
            {
                throw new IOException(
                    $"Cannot save PDF to '{Path.GetFileName(outputPath)}' because it is open in another application. Close it and try again.", ex);
            }
        }
        finally
        {
            foreach (var doc in documentCache.Values)
                try { doc.Dispose(); } catch { }
        }
    }

    // ── Embedded PDF image recompression ──────────────────────────────────────
    //
    // Pages imported from existing PDFs arrive as form XObjects whose raster images are
    // copied byte-for-byte, so without this pass a merged scan/photo PDF comes out exactly
    // the same size as it went in. This walks every image XObject in the finished document
    // and recompresses the JPEG-encoded ones (the dominant case for scans and photos):
    // downsample to the page-size pixel budget, re-encode at the configured quality, and
    // only ever swap the bytes in when the result is actually smaller.

    /// <summary>
    /// Recompresses JPEG (DCTDecode) image XObjects embedded in <paramref name="doc"/>.
    /// Non-JPEG images (Flate, CCITT, masks) and CMYK JPEGs are left untouched.
    /// </summary>
    internal static void CompressEmbeddedPdfImages(PdfDocument doc)
    {
        var quality = (uint)Math.Clamp((int)Math.Round(GetJpegQuality() * 100), 1, 100);

        // Budget: assume an image may span the largest page in the document full-bleed.
        double pageLongInches = 0;
        foreach (var page in doc.Pages)
            pageLongInches = Math.Max(pageLongInches, Math.Max(page.Width.Point, page.Height.Point) / 72.0);
        if (pageLongInches <= 0)
            pageLongInches = MaxPageLongEdgeInches();
        var maxLongEdge = (int)Math.Ceiling(pageLongInches * GetRasterDpi());

        foreach (var obj in doc.Internals.GetAllObjects())
        {
            if (obj is not PdfDictionary dict || dict.Stream is null) continue;
            if (dict.Elements.GetName("/Subtype") != "/Image") continue;
            if (!IsDctEncoded(dict)) continue;

            try
            {
                RecompressJpegXObject(dict, maxLongEdge, quality);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Skipping recompression of an embedded PDF image");
            }
        }
    }

    /// <summary>True when the image stream is plain JPEG data (single DCTDecode filter).</summary>
    private static bool IsDctEncoded(PdfDictionary dict)
    {
        var filter = dict.Elements["/Filter"];
        return filter switch
        {
            PdfName name => name.Value == "/DCTDecode",
            PdfArray { Elements.Count: 1 } arr => (arr.Elements[0] as PdfName)?.Value == "/DCTDecode",
            _ => false
        };
    }

    private static void RecompressJpegXObject(PdfDictionary dict, int maxLongEdge, uint quality)
    {
        var jpeg = dict.Stream.Value;
        using var image = new MagickImage(jpeg);

        // CMYK JPEGs need colour management to convert safely — not worth the risk here.
        if (image.ColorSpace == ColorSpace.CMYK) return;

        var srcLong = (int)Math.Max(image.Width, image.Height);
        if (srcLong > maxLongEdge)
            image.Thumbnail(new Percentage((double)maxLongEdge / srcLong * 100));

        image.Format  = MagickFormat.Jpeg;
        image.Quality = quality;
        image.Strip();
        var newBytes = image.ToByteArray();

        // Never inflate: keep the original bytes unless recompression actually helped.
        if (newBytes.Length >= jpeg.Length) return;

        var isGray = image.ColorSpace is ColorSpace.Gray or ColorSpace.LinearGray;
        dict.Stream.Value = newBytes;
        dict.Elements["/Width"]            = new PdfInteger((int)image.Width);
        dict.Elements["/Height"]           = new PdfInteger((int)image.Height);
        dict.Elements["/BitsPerComponent"] = new PdfInteger(8);
        dict.Elements["/ColorSpace"]       = new PdfName(isGray ? "/DeviceGray" : "/DeviceRGB");
        dict.Elements["/Filter"]           = new PdfName("/DCTDecode");
        dict.Elements.Remove("/DecodeParms");
        dict.Elements.Remove("/Decode");
    }

    // ── Image conversion & compression ────────────────────────────────────────
    //
    // Compression runs through Magick.NET (cross-platform and therefore unit-testable):
    // every image is downsampled to the target DPI for the active page size and re-encoded
    // as JPEG at the configured quality. That resolution reduction — not just the quality
    // drop — is what actually makes the output PDF smaller than the source images. Formats
    // Magick.NET can't decode (some camera RAW / HEIC) fall back to the OS WinRT codecs.

    /// <summary>
    /// Pre-converts/compresses images concurrently.
    /// Returns a map of original path → converted temp-file path.
    /// </summary>
    private async Task<Dictionary<string, string>> PreConvertImagesAsync(IEnumerable<string> imagePaths)
    {
        var results = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        await Task.WhenAll(imagePaths.Select(path => Task.Run(() =>
        {
            try
            {
                var tempPath = ConvertImageToTemp(path);
                _convertedImageTempFiles.Add(tempPath);
                results[path] = tempPath;
            }
            catch (Exception ex) { Log.Error(ex, "Failed to pre-convert image: {Path}", path); }
        })));

        return new Dictionary<string, string>(results, StringComparer.OrdinalIgnoreCase);
    }

    private string? ConvertImageToCompatibleFormat(string imagePath)
    {
        try
        {
            var tempPath = ConvertImageToTemp(imagePath);
            _convertedImageTempFiles.Add(tempPath);
            Log.Information("Converted image: {Src} -> {Dst}", imagePath, tempPath);
            return tempPath;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to convert image: {Path}", imagePath);
            return null;
        }
    }

    /// <summary>Chooses the output format, runs the compressor, returns the temp-file path.</summary>
    private string ConvertImageToTemp(string imagePath)
    {
        var useJpeg  = ShouldUseJpeg(imagePath);
        var tempPath = Path.Combine(Path.GetTempPath(), $"gladhen_conv_{Guid.NewGuid():N}{(useJpeg ? ".jpg" : ".png")}");
        ConvertImage(imagePath, tempPath, useJpeg);
        return tempPath;
    }

    /// <summary>
    /// JPEG for photos and for any image when compression is enabled; PNG only to losslessly
    /// normalise a non-photo format while compression is off.
    /// </summary>
    internal static bool ShouldUseJpeg(string imagePath)
    {
        if (AppSettings.Current.ImageCompression != PdfImageCompression.None) return true;
        var ext = Path.GetExtension(imagePath).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".heic" or ".heif"
                    or ".cr2" or ".nef" or ".arw" or ".dng" or ".raw";
    }

    /// <summary>Magick.NET compressor with a WinRT fallback for formats it can't decode.</summary>
    private void ConvertImage(string sourcePath, string destPath, bool useJpeg)
    {
        try
        {
            CompressImageWithMagick(sourcePath, destPath, useJpeg);
            return;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Magick.NET conversion failed for {Path}; trying OS codec fallback", sourcePath);
        }
#if !UNIT_TEST
        ConvertImageWithWinRtAsync(sourcePath, destPath, useJpeg).GetAwaiter().GetResult();
#else
        throw new InvalidOperationException($"Unable to convert image: {sourcePath}");
#endif
    }

    /// <summary>
    /// Downsamples to the target DPI for the active page size and re-encodes at the
    /// configured quality. This is the core of the "make the PDF thinner" feature.
    /// </summary>
    internal static void CompressImageWithMagick(string sourcePath, string destPath, bool useJpeg)
    {
        var level = AppSettings.Current.ImageCompression;
        using var image = OpenSourceImage(sourcePath, level, out var srcLong, out var srcDpi, out var targetLong);

        if (level != PdfImageCompression.None && targetLong < srcLong)
        {
            // The JPEG decode hint in OpenSourceImage may have already shrunk the decoded
            // pixels; Thumbnail() closes the remaining gap (its fast filter is fine here —
            // metadata is stripped below anyway).
            var loadedLong = (int)Math.Max(image.Width, image.Height);
            if (targetLong < loadedLong)
                image.Thumbnail(new Percentage((double)targetLong / loadedLong * 100));

            // Keep the on-page physical size unchanged by scaling the stored DPI with the
            // pixel count, so automatic-mode page dimensions don't shift when resolution drops.
            var newDpi = srcDpi * ((double)targetLong / srcLong);
            image.Density = new Density(newDpi, newDpi, DensityUnit.PixelsPerInch);
        }

        if (useJpeg)
        {
            if (image.HasAlpha)
            {
                image.BackgroundColor = MagickColors.White;
                image.Alpha(AlphaOption.Remove);
            }
            image.Format  = MagickFormat.Jpeg;
            image.Quality = (uint)Math.Clamp((int)Math.Round(GetJpegQuality() * 100), 1, 100);
        }
        else
        {
            image.Format = MagickFormat.Png;
        }

        image.Strip(); // drop EXIF/thumbnail/colour-profile metadata to save bytes
        image.Write(destPath);
    }

    /// <summary>
    /// Maximum long-edge pixel budget for a downsampled image. For a fixed page size this is
    /// (page long edge in inches × target DPI); for automatic size
    /// (<paramref name="pageLongInches"/> = null) it's the image's own physical long edge.
    /// Never exceeds the source long edge, so images are only ever shrunk, never enlarged.
    /// </summary>
    internal static int ComputeMaxLongEdgePixels(
        int srcWidthPx, int srcHeightPx, double srcDpi, int targetDpi, double? pageLongInches)
    {
        var srcLong = Math.Max(srcWidthPx, srcHeightPx);
        if (targetDpi <= 0 || srcLong <= 0) return Math.Max(1, srcLong);

        var longInches = pageLongInches ?? (srcLong / (srcDpi <= 1 ? 96.0 : srcDpi));
        var cap = (int)Math.Ceiling(longInches * targetDpi);
        return Math.Clamp(cap, 1, srcLong);
    }

    /// <summary>
    /// Opens the source image for compression. When downsampling is needed and the source
    /// is a JPEG, a "jpeg:size" decode hint lets libjpeg DCT-scale during decode (reading a
    /// fraction of the pixels) instead of decoding at full resolution and resizing afterwards.
    /// </summary>
    private static MagickImage OpenSourceImage(
        string sourcePath, PdfImageCompression level,
        out int srcLong, out double srcDpi, out int targetLong)
    {
        if (level != PdfImageCompression.None)
        {
            double? pageLongInches = AppSettings.Current.PaperSize == PdfPaperSize.Automatic
                ? null                       // automatic page == image's own physical size
                : MaxPageLongEdgeInches();

            try
            {
                var info = new MagickImageInfo(sourcePath); // header-only read
                srcDpi     = DpiFromDensity(info.Density);
                srcLong    = (int)Math.Max(info.Width, info.Height);
                targetLong = ComputeMaxLongEdgePixels(
                    (int)info.Width, (int)info.Height, srcDpi, (int)GetRasterDpi(), pageLongInches);

                if (targetLong < srcLong && info.Format == MagickFormat.Jpeg)
                {
                    // Ask for 2× the target so the final Thumbnail() pass has quality headroom.
                    var hint = Math.Min(srcLong, targetLong * 2);
                    var settings = new MagickReadSettings();
                    settings.SetDefine(MagickFormat.Jpeg, "size", $"{hint}x{hint}");
                    return new MagickImage(sourcePath, settings);
                }
                return new MagickImage(sourcePath);
            }
            catch
            {
                // Header ping failed (unusual format): fall through to a full decode.
            }

            var fullImage = new MagickImage(sourcePath);
            srcDpi     = GetImageDpi(fullImage);
            srcLong    = (int)Math.Max(fullImage.Width, fullImage.Height);
            targetLong = ComputeMaxLongEdgePixels(
                (int)fullImage.Width, (int)fullImage.Height, srcDpi, (int)GetRasterDpi(), pageLongInches);
            return fullImage;
        }

        srcLong    = 0;
        srcDpi     = 96.0;
        targetLong = 0;
        return new MagickImage(sourcePath);
    }

    private static double MaxPageLongEdgeInches()
    {
        var (w, h) = GetPageDimensionsInPortrait(AppSettings.Current.PaperSize);
        return Math.Max(w, h) / 72.0;
    }

    private static double GetImageDpi(MagickImage image) => DpiFromDensity(image.Density);

    private static double DpiFromDensity(Density? d)
    {
        if (d is null) return 96.0;
        var dpi = d.Units == DensityUnit.PixelsPerCentimeter ? d.X * 2.54 : d.X;
        return dpi > 1 ? dpi : 96.0;
    }

#if !UNIT_TEST
    /// <summary>
    /// Fallback for formats Magick.NET can't open (some RAW/HEIC): decode with the OS WinRT
    /// codecs, downsampling during encode to the same pixel budget.
    /// </summary>
    private static async Task ConvertImageWithWinRtAsync(string sourcePath, string destPath, bool useJpeg)
    {
        var file = await StorageFile.GetFileFromPathAsync(sourcePath);
        using var sourceStream = await file.OpenReadAsync();
        var decoder        = await BitmapDecoder.CreateAsync(sourceStream);
        var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

        try
        {
            using var fs  = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var ras = fs.AsRandomAccessStream();
            var encoderId = useJpeg ? BitmapEncoder.JpegEncoderId : BitmapEncoder.PngEncoderId;
            var encoder   = await BitmapEncoder.CreateAsync(encoderId, ras);
            encoder.SetSoftwareBitmap(softwareBitmap);

            if (AppSettings.Current.ImageCompression != PdfImageCompression.None)
            {
                double? pageLongInches = AppSettings.Current.PaperSize == PdfPaperSize.Automatic
                    ? null
                    : MaxPageLongEdgeInches();
                var dpi        = decoder.DpiX > 1 ? decoder.DpiX : 96.0;
                var srcLong    = (int)Math.Max(decoder.PixelWidth, decoder.PixelHeight);
                var targetLong = ComputeMaxLongEdgePixels(
                    (int)decoder.PixelWidth, (int)decoder.PixelHeight, dpi, (int)GetRasterDpi(), pageLongInches);
                if (targetLong < srcLong)
                {
                    var scale = (double)targetLong / srcLong;
                    encoder.BitmapTransform.ScaledWidth       = (uint)Math.Max(1, Math.Round(decoder.PixelWidth  * scale));
                    encoder.BitmapTransform.ScaledHeight      = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale));
                }
                encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
            }

            if (useJpeg)
            {
                await encoder.BitmapProperties.SetPropertiesAsync(new BitmapPropertySet
                {
                    { "ImageQuality", new BitmapTypedValue(GetJpegQuality(), Windows.Foundation.PropertyType.Single) }
                });
            }

            await encoder.FlushAsync();
        }
        finally { softwareBitmap.Dispose(); }
    }
#endif
}
