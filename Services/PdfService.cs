using Gladhen3.Models;
using iText.IO.Image;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using PdfPageSize = iText.Kernel.Geom.PageSize;
using PdfRectangle = iText.Kernel.Geom.Rectangle;
using SkiaSharp;
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
#endif

namespace Gladhen3.Services;

public class PdfService
{
    private readonly ConcurrentBag<string> _convertedImageTempFiles = [];

    public void CreatePdfFromDocuments(List<DocumentItem> items, string outputPath)
    {
        var useCustomPageSize = AppSettings.Current.PaperSize != PdfPaperSize.Automatic;

        try
        {
            try { EnsureFileIsWritable(outputPath); }
            catch (IOException ex) { throw new IOException($"Cannot save PDF to '{Path.GetFileName(outputPath)}' because it is open in another application. Close it and try again.", ex); }
            catch (UnauthorizedAccessException ex) { throw new UnauthorizedAccessException($"Access denied saving PDF to '{Path.GetFileName(outputPath)}'. Check permissions or choose another location.", ex); }

            try
            {
                CreatePdfWithIText(items, outputPath, useCustomPageSize);
                Log.Information("PDF created with {PageCount} pages: {OutputPath}", items.Count, outputPath);
            }
            catch (IOException ex) { throw new IOException($"Cannot save PDF to '{Path.GetFileName(outputPath)}' because it is open in another application or an I/O error occurred. Close other apps and try again.", ex); }
            catch (UnauthorizedAccessException ex) { throw new UnauthorizedAccessException($"Access denied saving PDF to '{Path.GetFileName(outputPath)}'. Check permissions or choose another location.", ex); }
        }
        finally
        {
            foreach (var f in _convertedImageTempFiles)
                try { File.Delete(f); } catch { }
            _convertedImageTempFiles.Clear();
        }
    }

    private void CreatePdfWithIText(List<DocumentItem> items, string outputPath, bool useCustomPageSize)
    {
        var compressImages = AppSettings.Current.ImageCompression != PdfImageCompression.None;
        Log.Information("PDF compression mode: {CompressionMode}, Quality: {Quality}%, DPI: {DPI}",
            AppSettings.Current.ImageCompression, Math.Round(GetJpegQuality() * 100), GetRasterDpi());

        var imagePaths = items.Where(i => i.Type == DocumentType.Image).Select(i => i.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var convertedImages = compressImages && imagePaths.Count > 0
            ? PreConvertImagesAsync(imagePaths).GetAwaiter().GetResult()
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Log.Information("Image conversion: {ConvertedCount} of {TotalCount} images processed", convertedImages.Count, imagePaths.Count);

        var writerProps = new WriterProperties()
            .SetCompressionLevel(CompressionConstants.BEST_COMPRESSION)
            .SetFullCompressionMode(true);

        using var outputStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new PdfWriter(outputStream, writerProps);
        using var outputDocument = new PdfDocument(writer);
        outputDocument.GetDocumentInfo().SetTitle("Created with Gladhen3");

        var inputPdfCache = new Dictionary<string, PdfDocument>(StringComparer.OrdinalIgnoreCase);
        var pagesAdded = 0;

        try
        {
            foreach (var item in items)
            {
                if (item.Type == DocumentType.Image)
                {
                    try
                    {
                        var sourcePath = compressImages && convertedImages.TryGetValue(item.FilePath, out var convPath) ? convPath : item.FilePath;
                        AddImagePage(outputDocument, sourcePath, useCustomPageSize);
                        pagesAdded++;
                    }
                    catch (Exception ex) { Log.Error(ex, "Failed to add image to PDF: {Path}", item.FilePath); }
                }
                else if (item.Type == DocumentType.PdfPage)
                {
                    var sourcePath = item.SourcePdfPath ?? item.FilePath;
                    if (string.IsNullOrEmpty(sourcePath)) continue;

                    try
                    {
                        if (!inputPdfCache.TryGetValue(sourcePath, out var sourcePdf))
                        {
                            sourcePdf = new PdfDocument(new PdfReader(sourcePath));
                            inputPdfCache[sourcePath] = sourcePdf;
                        }

                        var sourcePageIndex = item.PageNumber - 1;
                        if (sourcePageIndex < 0 || sourcePageIndex >= sourcePdf.GetNumberOfPages()) continue;

                        AddPdfPage(outputDocument, sourcePdf.GetPage(item.PageNumber), useCustomPageSize);
                        pagesAdded++;
                    }
                    catch (Exception ex) { Log.Error(ex, "Failed to add PDF page: {Path}", sourcePath); }
                }
            }
        }
        finally
        {
            foreach (var sourcePdf in inputPdfCache.Values)
                try { sourcePdf.Close(); } catch { }
        }

        if (pagesAdded == 0) throw new InvalidOperationException("No pages could be processed. Please check the image formats.");

        if (AppSettings.Current.ImageCompression != PdfImageCompression.None)
        {
            Log.Information("Starting embedded PDF image recompression...");
            CompressEmbeddedPdfImages(outputDocument);
            Log.Information("Embedded image recompression complete");
        }
    }

    private void AddImagePage(PdfDocument outputDocument, string imagePath, bool useCustomPageSize)
    {
        var imageData = LoadImageDataWithFallback(imagePath);
        var imgWidth = imageData.GetWidth();
        var imgHeight = imageData.GetHeight();

        var sourceIsLandscape = imgWidth > imgHeight;
        var targetIsLandscape = ResolveTargetLandscape(sourceIsLandscape);
        var pageRectangle = useCustomPageSize ? GetCustomPageRectangle(targetIsLandscape) : GetAutoPageRectangle(imgWidth, imgHeight, sourceIsLandscape, targetIsLandscape);

        var page = outputDocument.AddNewPage(new PdfPageSize(pageRectangle));
        var canvas = new PdfCanvas(page);

        var margin = useCustomPageSize ? (float)AppSettings.Current.GetMarginInPoints() : 0f;
        var availableWidth = Math.Max(1f, pageRectangle.GetWidth() - (2f * margin));
        var availableHeight = Math.Max(1f, pageRectangle.GetHeight() - (2f * margin));
        var scale = Math.Min(Math.Min(availableWidth / imgWidth, availableHeight / imgHeight), 1f);
        var drawWidth = imgWidth * scale;
        var drawHeight = imgHeight * scale;

        var drawRect = new PdfRectangle((pageRectangle.GetWidth() - drawWidth) / 2f, (pageRectangle.GetHeight() - drawHeight) / 2f, drawWidth, drawHeight);
        canvas.AddImageFittedIntoRectangle(imageData, drawRect, false);
    }

    private void AddPdfPage(PdfDocument outputDocument, PdfPage sourcePage, bool useCustomPageSize)
    {
        var sourceRect = sourcePage.GetPageSizeWithRotation();
        var sourceWidth = sourceRect.GetWidth();
        var sourceHeight = sourceRect.GetHeight();

        var sourceIsLandscape = sourceWidth > sourceHeight;
        var targetIsLandscape = ResolveTargetLandscape(sourceIsLandscape);
        var pageRectangle = useCustomPageSize ? GetCustomPageRectangle(targetIsLandscape) : GetAutoPageRectangle(sourceWidth, sourceHeight, sourceIsLandscape, targetIsLandscape);

        var newPage = outputDocument.AddNewPage(new PdfPageSize(pageRectangle));
        var xObject = sourcePage.CopyAsFormXObject(outputDocument);
        var canvas = new PdfCanvas(newPage);

        var margin = useCustomPageSize ? (float)AppSettings.Current.GetMarginInPoints() : 0f;
        var availableWidth = Math.Max(1f, pageRectangle.GetWidth() - (2f * margin));
        var availableHeight = Math.Max(1f, pageRectangle.GetHeight() - (2f * margin));
        var scale = Math.Min(availableWidth / sourceWidth, availableHeight / sourceHeight);
        var drawWidth = sourceWidth * scale;
        var drawHeight = sourceHeight * scale;
        var drawX = (pageRectangle.GetWidth() - drawWidth) / 2f;
        var drawY = (pageRectangle.GetHeight() - drawHeight) / 2f;

        canvas.AddXObjectWithTransformationMatrix(xObject, drawWidth / sourceWidth, 0, 0, drawHeight / sourceHeight, drawX, drawY);
    }

    private static PdfRectangle GetAutoPageRectangle(float sourceWidth, float sourceHeight, bool sourceIsLandscape, bool targetIsLandscape)
        => targetIsLandscape == sourceIsLandscape ? new PdfRectangle(0, 0, sourceWidth, sourceHeight) : new PdfRectangle(0, 0, sourceHeight, sourceWidth);

    private static PdfRectangle GetCustomPageRectangle(bool useLandscape)
    {
        var (baseWidth, baseHeight) = GetPageDimensionsInPortrait(AppSettings.Current.PaperSize);
        return useLandscape ? new PdfRectangle(0, 0, (float)baseHeight, (float)baseWidth) : new PdfRectangle(0, 0, (float)baseWidth, (float)baseHeight);
    }

    private static bool ResolveTargetLandscape(bool sourceLandscape) => AppSettings.Current.Orientation switch
    {
        PdfPaperOrientation.Portrait => false,
        PdfPaperOrientation.Landscape => true,
        _ => sourceLandscape
    };

    private ImageData LoadImageDataWithFallback(string imagePath)
    {
        try { return ImageDataFactory.Create(imagePath); }
        catch (Exception ex) { Log.Warning(ex, "Direct image load failed for: {Path}, attempting conversion", imagePath); }

        var converted = ConvertImageToCompatibleFormat(imagePath);
        if (!string.IsNullOrEmpty(converted))
        {
            try { return ImageDataFactory.Create(converted); }
            catch (Exception ex) { Log.Error(ex, "Failed to load converted image: {Path}", converted); }
        }

        throw new InvalidOperationException($"Unable to load or convert image: {imagePath}");
    }

    internal static double GetJpegQuality() => AppSettings.Current.ImageCompression switch
    {
        PdfImageCompression.Low => 0.85,
        PdfImageCompression.Medium => 0.65,
        PdfImageCompression.High => 0.40,
        _ => 0.92
    };

    internal static uint GetRasterDpi() => AppSettings.Current.ImageCompression switch
    {
        PdfImageCompression.Low => 150,
        PdfImageCompression.Medium => 120,
        PdfImageCompression.High => 96,
        _ => 150
    };

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

    internal static void EnsureFileIsWritable(string filePath)
    {
        if (!File.Exists(filePath))
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                throw new InvalidOperationException($"Directory does not exist: {dir}");
            return;
        }

        try { using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.None); }
        catch (IOException ex) { throw new IOException($"Cannot save to ''{Path.GetFileName(filePath)}'' because it is currently open in another application.\n\nPlease close the file in the other application and try again.", ex); }
        catch (UnauthorizedAccessException ex) { throw new UnauthorizedAccessException($"Cannot save to ''{Path.GetFileName(filePath)}'' because access is denied.\n\nPlease check file permissions or choose a different location.", ex); }
    }

    private async Task<Dictionary<string, string>> PreConvertImagesAsync(IEnumerable<string> imagePaths)
    {
        var results = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pathList = imagePaths.ToList();
        Log.Information("Starting pre-conversion of {ImageCount} images", pathList.Count);

        await Task.WhenAll(pathList.Select(path => Task.Run(() =>
        {
            try
            {
                var tempPath = ConvertImageToTemp(path);
                _convertedImageTempFiles.Add(tempPath);
                results[path] = tempPath;
                var srcSize = new FileInfo(path).Length;
                var dstSize = new FileInfo(tempPath).Length;
                Log.Information("Pre-converted image: {Src} ({SrcSize}B) -> {Dst} ({DstSize}B, {Ratio:P0})", Path.GetFileName(path), srcSize, Path.GetFileName(tempPath), dstSize, (double)dstSize / srcSize);
            }
            catch (Exception ex) { Log.Error(ex, "Failed to pre-convert image: {Path}", path); }
        })));

        Log.Information("Pre-conversion complete: {SuccessCount}/{TotalCount} images converted", results.Count, pathList.Count);
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

    private string ConvertImageToTemp(string imagePath)
    {
        var useJpeg = ShouldUseJpeg(imagePath);
        var extension = useJpeg ? ".jpg" : ".png";
        var tempPath = Path.Combine(Path.GetTempPath(), $"gladhen_conv_{Guid.NewGuid():N}{extension}");
        ConvertImage(imagePath, tempPath, useJpeg);
        return tempPath;
    }

    internal static bool ShouldUseJpeg(string imagePath)
    {
        if (AppSettings.Current.ImageCompression != PdfImageCompression.None) return true;
        var ext = Path.GetExtension(imagePath).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".heic" or ".heif" or ".cr2" or ".nef" or ".arw" or ".dng" or ".raw";
    }

    private void ConvertImage(string sourcePath, string destPath, bool useJpeg)
    {
        try { CompressImageWithSkia(sourcePath, destPath, useJpeg); return; }
        catch (Exception ex) { Log.Warning(ex, "SkiaSharp conversion failed for {Path}; trying OS codec fallback", sourcePath); }
#if !UNIT_TEST
        ConvertImageWithWinRtAsync(sourcePath, destPath, useJpeg).GetAwaiter().GetResult();
#else
        throw new InvalidOperationException($"Unable to convert image: {sourcePath}");
#endif
    }

    internal static void CompressImageWithSkia(string sourcePath, string destPath, bool useJpeg)
    {
        var level = AppSettings.Current.ImageCompression;
        using var bitmap = OpenSourceImage(sourcePath, level, out var srcLong, out var srcDpi, out var targetLong);

        SKBitmap toEncode = bitmap;
        if (level != PdfImageCompression.None && targetLong < srcLong)
        {
            var loadedLong = Math.Max(bitmap.Width, bitmap.Height);
            if (targetLong < loadedLong)
            {
                var scale = (float)targetLong / loadedLong;
                var newWidth = (int)Math.Max(1, Math.Round(bitmap.Width * scale));
                var newHeight = (int)Math.Max(1, Math.Round(bitmap.Height * scale));
                toEncode = bitmap.Resize(new SKImageInfo(newWidth, newHeight), SKFilterQuality.High);
                Log.Information("Downsampled image: {W}x{H}px -> {NewW}x{NewH}px ({Scale:P0})", bitmap.Width, bitmap.Height, newWidth, newHeight, scale);
            }
        }

        using var image = SKImage.FromBitmap(toEncode);
        var format = useJpeg ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png;
        var quality = useJpeg ? (int)Math.Clamp(Math.Round(GetJpegQuality() * 100), 1, 100) : 100;
        using (var data = image.Encode(format, quality))
        using (var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write))
            data.SaveTo(fs);

        if (toEncode != bitmap) toEncode.Dispose();

        var srcInfo = new FileInfo(sourcePath);
        var destInfo = new FileInfo(destPath);
        if (destInfo.Length >= srcInfo.Length)
        {
            var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
            if (ext == ".jpg" || ext == ".jpeg" || ext == ".png")
            {
                File.Copy(sourcePath, destPath, true);
                Log.Information("Output larger than source, keeping original: {Src} ({SrcSize}B) was > {Dst} ({DstSize}B)", Path.GetFileName(sourcePath), srcInfo.Length, Path.GetFileName(destPath), destInfo.Length);
            }
        }
        else
            Log.Information("Compression reduced file: {Src} ({SrcSize}B) -> {Dst} ({DstSize}B, {Ratio:P0})", Path.GetFileName(sourcePath), srcInfo.Length, Path.GetFileName(destPath), destInfo.Length, (double)destInfo.Length / srcInfo.Length);
    }

    internal static int ComputeMaxLongEdgePixels(int srcWidthPx, int srcHeightPx, double srcDpi, int targetDpi, double? pageLongInches)
    {
        var srcLong = Math.Max(srcWidthPx, srcHeightPx);
        if (targetDpi <= 0 || srcLong <= 0) return Math.Max(1, srcLong);
        var longInches = pageLongInches ?? (srcLong / (srcDpi <= 1 ? 96.0 : srcDpi));
        var cap = (int)Math.Ceiling(longInches * targetDpi);
        return Math.Clamp(cap, 1, srcLong);
    }

    private static SKBitmap OpenSourceImage(string sourcePath, PdfImageCompression level, out int srcLong, out double srcDpi, out int targetLong)
    {
        if (level != PdfImageCompression.None)
        {
            double? pageLongInches = AppSettings.Current.PaperSize == PdfPaperSize.Automatic ? 11.0 : MaxPageLongEdgeInches();
            srcDpi = 96.0;
            var bitmap = SKBitmap.Decode(sourcePath) ?? throw new InvalidOperationException($"Unable to decode image: {sourcePath}");
            srcLong = (int)Math.Max(bitmap.Width, bitmap.Height);
            targetLong = ComputeMaxLongEdgePixels(bitmap.Width, bitmap.Height, srcDpi, (int)GetRasterDpi(), pageLongInches);
            Log.Information("Image compression: {Src} -> {SrcLong}px source long edge, target {TargetLong}px ({CompressionLevel})", Path.GetFileName(sourcePath), srcLong, targetLong, level);
            return bitmap;
        }

        srcLong = 0;
        srcDpi = 96.0;
        targetLong = 0;
        return SKBitmap.Decode(sourcePath) ?? throw new InvalidOperationException($"Unable to decode image: {sourcePath}");
    }

    private static double MaxPageLongEdgeInches()
    {
        var (w, h) = GetPageDimensionsInPortrait(AppSettings.Current.PaperSize);
        return Math.Max(w, h) / 72.0;
    }

#if !UNIT_TEST
    private static async Task ConvertImageWithWinRtAsync(string sourcePath, string destPath, bool useJpeg)
    {
        var file = await StorageFile.GetFileFromPathAsync(sourcePath);
        using var sourceStream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(sourceStream);
        var softwareBitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

        try
        {
            using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var ras = fs.AsRandomAccessStream();
            var encoderId = useJpeg ? BitmapEncoder.JpegEncoderId : BitmapEncoder.PngEncoderId;
            var encoder = await BitmapEncoder.CreateAsync(encoderId, ras);
            encoder.SetSoftwareBitmap(softwareBitmap);

            if (AppSettings.Current.ImageCompression != PdfImageCompression.None)
            {
                double? pageLongInches = AppSettings.Current.PaperSize == PdfPaperSize.Automatic ? null : MaxPageLongEdgeInches();
                var dpi = decoder.DpiX > 1 ? decoder.DpiX : 96.0;
                var srcLong = (int)Math.Max(decoder.PixelWidth, decoder.PixelHeight);
                var targetLong = ComputeMaxLongEdgePixels((int)decoder.PixelWidth, (int)decoder.PixelHeight, dpi, (int)GetRasterDpi(), pageLongInches);
                if (targetLong < srcLong)
                {
                    var scale = (double)targetLong / srcLong;
                    encoder.BitmapTransform.ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale));
                    encoder.BitmapTransform.ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale));
                }
                encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
            }

            if (useJpeg)
                await encoder.BitmapProperties.SetPropertiesAsync(new BitmapPropertySet { { "ImageQuality", new BitmapTypedValue(GetJpegQuality(), Windows.Foundation.PropertyType.Single) } });

            await encoder.FlushAsync();
        }
        finally { softwareBitmap.Dispose(); }
    }
#endif

    internal static void CompressEmbeddedPdfImages(PdfDocument doc)
    {
        if (AppSettings.Current.ImageCompression == PdfImageCompression.None) return;
        var quality = (uint)Math.Clamp((int)Math.Round(GetJpegQuality() * 100), 1, 100);

        try
        {
            Log.Information("Scanning {PageCount} pages for embedded images to recompress", doc.GetNumberOfPages());
            var recompressedCount = 0;
            for (int i = 1; i <= doc.GetNumberOfPages(); i++)
                recompressedCount += CompressPageXObjects(doc.GetPage(i), quality);
            Log.Information("Embedded image recompression: {Count} images recompressed", recompressedCount);
        }
        catch (Exception ex) { Log.Warning(ex, "Error compressing embedded PDF images: {Error}", ex.Message); }
    }

    private static int CompressPageXObjects(PdfPage page, uint quality)
    {
        var recompressed = 0;
        try
        {
            var resources = page.GetResources();
            if (resources == null) return 0;
            var xObjectDict = resources.GetResource(PdfName.XObject);
            if (xObjectDict == null || xObjectDict.IsEmpty()) return 0;

            foreach (var key in xObjectDict.KeySet())
            {
                try
                {
                    var objRef = xObjectDict.Get((PdfName)key);
                    if (objRef == null) continue;

                    PdfStream? xObject = null;
                    if (objRef.IsStream()) xObject = objRef as PdfStream;
                    else if (objRef.IsIndirectReference()) xObject = ((PdfIndirectReference)objRef).GetRefersTo() as PdfStream;

                    if (xObject == null) continue;
                    var subtype = xObject.Get(PdfName.Subtype);
                    if (subtype == null || !subtype.Equals(PdfName.Image)) continue;

                    if (TryRecompressImageStream(xObject, quality)) recompressed++;
                }
                catch (Exception ex) { Log.Warning(ex, "Error processing XObject {Key}", key); }
            }
        }
        catch (Exception ex) { Log.Warning(ex, "Error processing XObjects on page"); }
        return recompressed;
    }

    private static bool TryRecompressImageStream(PdfStream imageStream, uint quality)
    {
        try
        {
            if (imageStream == null) return false;
            var filter = imageStream.Get(PdfName.Filter);
            if (filter == null) return false;

            bool isJpeg = false;
            if (filter is PdfName filterName) isJpeg = filterName.Equals(PdfName.DCTDecode);
            else if (filter is PdfArray filterArray && filterArray.Size() > 0)
            {
                var first = filterArray.Get(0);
                if (first is PdfName firstName) isJpeg = firstName.Equals(PdfName.DCTDecode);
            }

            if (!isJpeg) return false;
            byte[] jpegData = imageStream.GetBytes(false);
            if (jpegData == null || jpegData.Length == 0) return false;

            using var bitmap = SKBitmap.Decode(jpegData);
            if (bitmap == null) return false;

            using var image = SKImage.FromBitmap(bitmap);
            using var reencoded = image.Encode(SKEncodedImageFormat.Jpeg, (int)quality);
            byte[] newBytes = reencoded.ToArray();

            if (newBytes.Length < jpegData.Length)
            {
                imageStream.SetData(newBytes);
                imageStream.Put(PdfName.Filter, PdfName.DCTDecode);
                Log.Information("Recompressed embedded JPEG: {OldSize}B -> {NewSize}B ({Ratio:P0})", jpegData.Length, newBytes.Length, (double)newBytes.Length / jpegData.Length);
                return true;
            }
        }
        catch (Exception ex) { Log.Warning(ex, "Failed to recompress image stream"); }
        return false;
    }
}
