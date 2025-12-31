using Gladhen3.Models;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Gladhen3.Services;

public class PdfService
{
    // Cache for XImage instances to avoid repeated loading of the same image
    private readonly Dictionary<string, XImage> _imageCache = new(StringComparer.OrdinalIgnoreCase);

    // Cache for XPdfForm instances to avoid repeated loading of the same PDF
    private readonly Dictionary<string, XPdfForm> _pdfFormCache = new(StringComparer.OrdinalIgnoreCase);

    // Temp files created during conversion that need cleanup
    private readonly List<string> _convertedImageTempFiles = new();

    /// <summary>
    /// Creates a PDF from a list of DocumentItems (images and PDF pages) in their current order
    /// </summary>
    public void CreatePdfFromDocuments(List<DocumentItem> items, string outputPath)
    {
        var tempFiles = new List<string>();
        var useCustomPageSize = AppSettings.Current.PaperSize != PdfPaperSize.Automatic;

        try
        {
            // Ensure output path is writable before starting
            try
            {
                EnsureFileIsWritable(outputPath);
            }
            catch (IOException ex)
            {
                Log.Warning(ex, "Output file is locked or not writable: {Path}", outputPath);
                throw new IOException($"Cannot save PDF to '{Path.GetFileName(outputPath)}' because it is open in another application. Close it and try again.", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Warning(ex, "Access denied to output file: {Path}", outputPath);
                throw new UnauthorizedAccessException($"Access denied saving PDF to '{Path.GetFileName(outputPath)}'. Check permissions or choose another location.", ex);
            }

            try
            {
                if (useCustomPageSize)
                {
                    CreatePdfWithCustomPageSize(items, outputPath);
                }
                else
                {
                    CreatePdfWithAutomaticPageSize(items, outputPath, tempFiles);
                }

                Log.Information("PDF created with {PageCount} pages: {OutputPath}", items.Count, outputPath);
            }
            catch (IOException ex)
            {
                Log.Warning(ex, "I/O error while creating PDF: {Path}", outputPath);
                throw new IOException($"Cannot save PDF to '{Path.GetFileName(outputPath)}' because it is open in another application or an I/O error occurred. Close other apps and try again.", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Warning(ex, "Access denied while creating PDF: {Path}", outputPath);
                throw new UnauthorizedAccessException($"Access denied saving PDF to '{Path.GetFileName(outputPath)}'. Check permissions or choose another location.", ex);
            }
        }
        finally
        {
            foreach (var tempFile in tempFiles)
            {
                try { File.Delete(tempFile); }
                catch { /* Ignore cleanup errors */ }
            }

            foreach (var tempFile in _convertedImageTempFiles)
            {
                try { File.Delete(tempFile); }
                catch { /* Ignore cleanup errors */ }
            }
            _convertedImageTempFiles.Clear();

            ClearCaches();
        }
    }

    /// <summary>
    /// Creates PDF with automatic page size (original sizes)
    /// </summary>
    private void CreatePdfWithAutomaticPageSize(List<DocumentItem> items, string outputPath, List<string> tempFiles)
    {
        var pageList = new List<(string PdfPath, int PageIndex)>(items.Count);

        foreach (var item in items)
        {
            if (item.Type == DocumentType.Image)
            {
                var tempPath = Path.Combine(Path.GetTempPath(), $"gladhen_{Guid.NewGuid():N}.pdf");

                try
                {
                    CreateSingleImagePdfWithFallback(item.FilePath, tempPath);
                    tempFiles.Add(tempPath);
                    pageList.Add((tempPath, 0));
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to create PDF from image: {Path}", item.FilePath);
                    // Continue with other items
                }
            }
            else if (item.Type == DocumentType.PdfPage)
            {
                var sourcePath = item.SourcePdfPath ?? item.FilePath;
                if (!string.IsNullOrEmpty(sourcePath))
                {
                    pageList.Add((sourcePath, item.PageNumber - 1));
                }
            }
        }

        if (pageList.Count > 0)
        {
            MergePagesOptimized(pageList, outputPath);
        }
        else
        {
            throw new InvalidOperationException("No pages could be processed. Please check the image formats.");
        }
    }

    /// <summary>
    /// Creates a PDF with all pages resized to the custom page size with XImage reuse
    /// </summary>
    private void CreatePdfWithCustomPageSize(List<DocumentItem> items, string outputPath)
    {
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
                        // Add image with custom page size - reuse XImage if same file
                        AddImageToDocumentWithFallback(outputDocument, item.FilePath);
                        pagesAdded++;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Failed to add image to PDF: {Path}", item.FilePath);
                        // Continue with other items
                    }
                }
                else if (item.Type == DocumentType.PdfPage)
                {
                    var sourcePath = item.SourcePdfPath ?? item.FilePath;
                    if (string.IsNullOrEmpty(sourcePath)) continue;

                    // Load source PDF if different from current
                    if (sourcePath != currentPdfPath)
                    {
                        currentInputDoc?.Dispose();
                        currentInputDoc = PdfReader.Open(sourcePath, PdfDocumentOpenMode.Import);
                        currentPdfPath = sourcePath;
                    }

                    if (currentInputDoc != null && item.PageNumber > 0 && item.PageNumber <= currentInputDoc.PageCount)
                    {
                        // Re-render PDF page to custom size with XPdfForm reuse
                        AddPdfPageToDocumentOptimized(outputDocument, sourcePath, item.PageNumber - 1);
                        pagesAdded++;
                    }
                }
            }
        }
        finally
        {
            currentInputDoc?.Dispose();
        }

        if (pagesAdded == 0)
        {
            throw new InvalidOperationException("No pages could be processed. Please check the image formats.");
        }

        try
        {
            outputDocument.Save(outputPath);
        }
        catch (IOException ex)
        {
            Log.Warning(ex, "Failed to save PDF (I/O): {Path}", outputPath);
            throw new IOException($"Cannot save PDF to '{Path.GetFileName(outputPath)}' because it is open in another application. Close it and try again.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warning(ex, "Failed to save PDF (access denied): {Path}", outputPath);
            throw new UnauthorizedAccessException($"Access denied saving PDF to '{Path.GetFileName(outputPath)}'. Check permissions or choose another location.", ex);
        }
    }

    /// <summary>
    /// Creates a single image PDF with fallback for unsupported formats
    /// Handles orientation setting even in automatic page size mode
    /// </summary>
    private void CreateSingleImagePdfWithFallback(string imagePath, string outputPath)
    {
        using var document = new PdfDocument();
        var page = document.AddPage();

        // Try to load image with fallback conversion
        using var xImage = LoadImageWithFallback(imagePath);

        // Get image dimensions in points (PDFsharp uses72 DPI internally)
        var imageWidthPt = xImage.PointWidth;
        var imageHeightPt = xImage.PointHeight;

        // Use image's natural size in points (Automatic page size mode)
        page.Width = new XUnit(imageWidthPt);
        page.Height = new XUnit(imageHeightPt);

        // Apply orientation setting even in automatic mode
        var isImageLandscape = imageWidthPt > imageHeightPt;
        var currentPageIsLandscape = page.Width.Point > page.Height.Point;

        // Determine target orientation
        var targetLandscape = AppSettings.Current.Orientation switch
        {
            PdfPaperOrientation.Portrait => false,
            PdfPaperOrientation.Landscape => true,
            _ => isImageLandscape // Automatic: keep image's natural orientation
        };

        // Swap page dimensions if orientation doesn't match
        if (targetLandscape != currentPageIsLandscape)
        {
            (page.Width, page.Height) = (page.Height, page.Width);
        }

        using var gfx = XGraphics.FromPdfPage(page);

        // Calculate how to fit the image in the page (which may now be rotated)
        var pageWidth = page.Width.Point;
        var pageHeight = page.Height.Point;

        // Calculate scale to fit
        var scaleX = pageWidth / imageWidthPt;
        var scaleY = pageHeight / imageHeightPt;
        var scale = Math.Min(scaleX, scaleY);

        var drawWidth = imageWidthPt * scale;
        var drawHeight = imageHeightPt * scale;

        // Center the image
        var x = (pageWidth - drawWidth) / 2;
        var y = (pageHeight - drawHeight) / 2;

        gfx.DrawImage(xImage, x, y, drawWidth, drawHeight);

        try
        {
            document.Save(outputPath);
        }
        catch (IOException ex)
        {
            Log.Warning(ex, "Failed to save single-image PDF (I/O): {Path}", outputPath);
            throw new IOException($"Cannot save PDF to '{Path.GetFileName(outputPath)}' because it is open in another application. Close it and try again.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warning(ex, "Failed to save single-image PDF (access denied): {Path}", outputPath);
            throw new UnauthorizedAccessException($"Access denied saving PDF to '{Path.GetFileName(outputPath)}'. Check permissions or choose another location.", ex);
        }
    }

    /// <summary>
    /// Adds an image to the document with fallback for unsupported formats
    /// </summary>
    private void AddImageToDocumentWithFallback(PdfDocument document, string imagePath)
    {
        var page = document.AddPage();

        // Get or create cached XImage with fallback
        var xImage = GetOrCreateXImageWithFallback(imagePath);
        var imageWidthPt = xImage.PointWidth;
        var imageHeightPt = xImage.PointHeight;

        // Get the base page dimensions (always get as portrait first - shorter dimension as width)
        var (baseWidth, baseHeight) = GetPageDimensionsInPortrait(AppSettings.Current.PaperSize);

        // Determine if we should use landscape orientation
        var isImageLandscape = imageWidthPt > imageHeightPt;
        var useLandscape = AppSettings.Current.Orientation switch
        {
            PdfPaperOrientation.Portrait => false,
            PdfPaperOrientation.Landscape => true,
            _ => isImageLandscape // Automatic: match image orientation
        };

        // Set page dimensions based on orientation
        if (useLandscape)
        {
            // Landscape: width > height
            page.Width = new XUnit(baseHeight); // Swap: longer dimension becomes width
            page.Height = new XUnit(baseWidth); // Swap: shorter dimension becomes height
        }
        else
        {
            // Portrait: height > width
            page.Width = new XUnit(baseWidth);
            page.Height = new XUnit(baseHeight);
        }

        using var gfx = XGraphics.FromPdfPage(page);

        // Get margin and calculate drawing area
        var marginPt = AppSettings.Current.GetMarginInPoints();
        var availableWidth = page.Width.Point - (marginPt * 2);
        var availableHeight = page.Height.Point - (marginPt * 2);

        // Calculate scale to fit within available area while maintaining aspect ratio
        var scaleX = availableWidth / imageWidthPt;
        var scaleY = availableHeight / imageHeightPt;
        var scale = Math.Min(scaleX, scaleY);

        // Don't scale up beyond original size
        scale = Math.Min(scale, 1.0);

        var drawWidth = imageWidthPt * scale;
        var drawHeight = imageHeightPt * scale;

        // Center the image on the page
        var x = (page.Width.Point - drawWidth) / 2;
        var y = (page.Height.Point - drawHeight) / 2;

        gfx.DrawImage(xImage, x, y, drawWidth, drawHeight);
    }

    /// <summary>
    /// Loads an image with fallback conversion for unsupported formats
    /// </summary>
    private XImage LoadImageWithFallback(string imagePath)
    {
        // First, try direct loading
        try
        {
            return XImage.FromFile(imagePath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Direct image load failed for: {Path}, attempting conversion", imagePath);
        }

        // Convert to PNG/JPEG using Windows Imaging Component
        var convertedPath = ConvertImageToCompatibleFormat(imagePath);
        if (convertedPath != null)
        {
            try
            {
                return XImage.FromFile(convertedPath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to load converted image: {Path}", convertedPath);
            }
        }

        throw new InvalidOperationException($"Unable to load or convert image: {imagePath}");
    }

    /// <summary>
    /// Gets a cached XImage or creates a new one with fallback conversion
    /// </summary>
    private XImage GetOrCreateXImageWithFallback(string imagePath)
    {
        if (_imageCache.TryGetValue(imagePath, out var cachedImage))
        {
            return cachedImage;
        }

        var xImage = LoadImageWithFallback(imagePath);
        _imageCache[imagePath] = xImage;
        return xImage;
    }

    /// <summary>
    /// Converts an image to a compatible format (JPEG or PNG) using Windows Imaging Component (WIC)
    /// Supports TIFF, WebP, HEIC, and other formats that PDFsharp doesn't handle well
    /// </summary>
    private string? ConvertImageToCompatibleFormat(string imagePath)
    {
        try
        {
            // Use JPEG for photos (better compression), PNG for images with transparency
            var ext = Path.GetExtension(imagePath).ToLowerInvariant();
            var useJpeg = ext is ".jpg" or ".jpeg" or ".heic" or ".heif" or ".cr2" or ".nef" or ".arw" or ".dng" or ".raw";

            var outputExt = useJpeg ? ".jpg" : ".png";
            var tempPath = Path.Combine(Path.GetTempPath(), $"gladhen_conv_{Guid.NewGuid():N}{outputExt}");

            // Use synchronous wait for the async conversion
            var task = ConvertImageAsync(imagePath, tempPath, useJpeg);
            task.GetAwaiter().GetResult();

            _convertedImageTempFiles.Add(tempPath);
            Log.Information("Converted image to {Format}: {Source} -> {Dest}", outputExt.ToUpper(), imagePath, tempPath);
            return tempPath;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to convert image: {Path}", imagePath);
            return null;
        }
    }

    /// <summary>
    /// Async implementation of image conversion using WIC
    /// </summary>
    private static async Task ConvertImageAsync(string sourcePath, string destPath, bool useJpeg)
    {
        // Get the source file
        var file = await StorageFile.GetFileFromPathAsync(sourcePath);

        using var sourceStream = await file.OpenReadAsync();

        // Decode the image using WIC (handles most formats including TIFF, WebP, HEIC, etc.)
        var decoder = await BitmapDecoder.CreateAsync(sourceStream);

        // Get the software bitmap - use Bgra8 for PNG (supports alpha), Bgra8 for JPEG too
        var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
               BitmapAlphaMode.Premultiplied);

        try
        {
            // Write to the temp file using FileStream (more reliable than StorageFile for temp paths)
            using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var outputStream = fileStream.AsRandomAccessStream();

            // Encode as JPEG or PNG
            var encoderId = useJpeg ? BitmapEncoder.JpegEncoderId : BitmapEncoder.PngEncoderId;
            var encoder = await BitmapEncoder.CreateAsync(encoderId, outputStream);

            encoder.SetSoftwareBitmap(softwareBitmap);

            // For JPEG, set quality
            if (useJpeg)
            {
                encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
                var properties = new BitmapPropertySet
     {
       { "ImageQuality", new BitmapTypedValue(0.92, Windows.Foundation.PropertyType.Single) }
                };
                await encoder.BitmapProperties.SetPropertiesAsync(properties);
            }

            await encoder.FlushAsync();
        }
        finally
        {
            softwareBitmap.Dispose();
        }
    }

    /// <summary>
    /// Adds a PDF page to the document with XPdfForm caching for reuse
    /// </summary>
    private void AddPdfPageToDocumentOptimized(PdfDocument document, string sourcePath, int pageIndex)
    {
        // Get or create cached XPdfForm
        var form = GetOrCreateXPdfForm(sourcePath);
        form.PageIndex = pageIndex;

        var sourceWidth = form.PointWidth;
        var sourceHeight = form.PointHeight;

        // Create new page
        var newPage = document.AddPage();

        // Get the base page dimensions (always get as portrait first - shorter dimension as width)
        var (baseWidth, baseHeight) = GetPageDimensionsInPortrait(AppSettings.Current.PaperSize);

        // Determine if we should use landscape orientation
        var isSourceLandscape = sourceWidth > sourceHeight;
        var useLandscape = AppSettings.Current.Orientation switch
        {
            PdfPaperOrientation.Portrait => false,
            PdfPaperOrientation.Landscape => true,
            _ => isSourceLandscape // Automatic: match source orientation
        };

        // Set page dimensions based on orientation
        if (useLandscape)
        {
            // Landscape: width > height
            newPage.Width = new XUnit(baseHeight); // Swap: longer dimension becomes width
            newPage.Height = new XUnit(baseWidth); // Swap: shorter dimension becomes height
        }
        else
        {
            // Portrait: height > width
            newPage.Width = new XUnit(baseWidth);
            newPage.Height = new XUnit(baseHeight);
        }

        using var gfx = XGraphics.FromPdfPage(newPage);

        // Get margin and calculate drawing area
        var marginPt = AppSettings.Current.GetMarginInPoints();
        var availableWidth = newPage.Width.Point - (marginPt * 2);
        var availableHeight = newPage.Height.Point - (marginPt * 2);

        // Calculate scale to fit within available area while maintaining aspect ratio
        var scaleX = availableWidth / sourceWidth;
        var scaleY = availableHeight / sourceHeight;
        var scale = Math.Min(scaleX, scaleY);

        // Don't scale up beyond original size
        scale = Math.Min(scale, 1.0);

        var drawWidth = sourceWidth * scale;
        var drawHeight = sourceHeight * scale;

        // Center the content on the page
        var x = (newPage.Width.Point - drawWidth) / 2;
        var y = (newPage.Height.Point - drawHeight) / 2;

        gfx.DrawImage(form, x, y, drawWidth, drawHeight);
    }

    /// <summary>
    /// Gets a cached XPdfForm or creates a new one
    /// </summary>
    private XPdfForm GetOrCreateXPdfForm(string pdfPath)
    {
        if (_pdfFormCache.TryGetValue(pdfPath, out var cachedForm))
        {
            return cachedForm;
        }

        var form = XPdfForm.FromFile(pdfPath);
        _pdfFormCache[pdfPath] = form;
        return form;
    }

    /// <summary>
    /// Clears all caches and disposes cached objects
    /// </summary>
    private void ClearCaches()
    {
        foreach (var image in _imageCache.Values)
        {
            try { image.Dispose(); }
            catch { /* Ignore disposal errors */ }
        }
        _imageCache.Clear();

        foreach (var form in _pdfFormCache.Values)
        {
            try { form.Dispose(); }
            catch { /* Ignore disposal errors */ }
        }
        _pdfFormCache.Clear();
    }

    /// <summary>
    /// Merges PDF pages with optimized document handling
    /// </summary>
    private static void MergePagesOptimized(List<(string PdfPath, int PageIndex)> pageList, string outputPath)
    {
        using var outputDocument = new PdfDocument();
        outputDocument.Info.Title = "Created with Gladhen3";

        // Cache for open PDF documents to avoid reopening same file
        var documentCache = new Dictionary<string, PdfDocument>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var (pdfPath, pageIndex) in pageList)
            {
                // Get or open PDF document
                if (!documentCache.TryGetValue(pdfPath, out var inputDoc))
                {
                    inputDoc = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Import);
                    documentCache[pdfPath] = inputDoc;
                }

                if (pageIndex >= 0 && pageIndex < inputDoc.PageCount)
                {
                    outputDocument.AddPage(inputDoc.Pages[pageIndex]);
                }
            }

            try
            {
                outputDocument.Save(outputPath);
            }
            catch (IOException ex)
            {
                Log.Warning(ex, "Failed to save merged PDF (I/O): {Path}", outputPath);
                throw new IOException($"Cannot save PDF to '{Path.GetFileName(outputPath)}' because it is open in another application. Close it and try again.", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Warning(ex, "Failed to save merged PDF (access denied): {Path}", outputPath);
                throw new UnauthorizedAccessException($"Access denied saving PDF to '{Path.GetFileName(outputPath)}'. Check permissions or choose another location.", ex);
            }
        }
        finally
        {
            // Dispose all cached documents
            foreach (var doc in documentCache.Values)
            {
                try { doc.Dispose(); }
                catch { /* Ignore disposal errors */ }
            }
        }
    }

    /// <summary>
    /// Gets page dimensions in portrait orientation (width &lt; height).
    /// Returns (shorterDimension, longerDimension) so caller can swap for landscape.
    /// </summary>
    private static (double Width, double Height) GetPageDimensionsInPortrait(PdfPaperSize size)
    {
        double width, height;

        switch (size)
        {
            case PdfPaperSize.A4:
                width = XUnit.FromMillimeter(210).Point;
                height = XUnit.FromMillimeter(297).Point;
                break;
            case PdfPaperSize.Letter:
                width = XUnit.FromInch(8.5).Point;
                height = XUnit.FromInch(11).Point;
                break;
            case PdfPaperSize.Legal:
                width = XUnit.FromInch(8.5).Point;
                height = XUnit.FromInch(14).Point;
                break;
            case PdfPaperSize.A3:
                width = XUnit.FromMillimeter(297).Point;
                height = XUnit.FromMillimeter(420).Point;
                break;
            case PdfPaperSize.Custom:
                width = AppSettings.Current.GetCustomWidthInPoints();
                height = AppSettings.Current.GetCustomHeightInPoints();
                break;
            default:
                width = XUnit.FromMillimeter(210).Point;
                height = XUnit.FromMillimeter(297).Point;
                break;
        }

        // Ensure we always return portrait orientation (width < height)
        // This normalizes user input so orientation setting works correctly
        if (width > height)
        {
            return (height, width); // Swap to make it portrait
        }

        return (width, height);
    }

    /// <summary>
    /// Ensures the output file path is writable. If the file exists and is locked,
    /// throws a user-friendly exception.
    /// </summary>
    private static void EnsureFileIsWritable(string filePath)
    {
        if (!File.Exists(filePath))
        {
            // File doesn't exist, check if directory is writable
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                throw new InvalidOperationException($"Directory does not exist: {directory}");
            }
            return;
        }

        // File exists, try to open it for writing to check if it's locked
        try
        {
            using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Write,
                FileShare.None);
        }
        catch (IOException ex)
        {
            Log.Warning(ex, "File is locked: {Path}", filePath);
            throw new IOException(
 $"Cannot save to '{Path.GetFileName(filePath)}' because it is currently open in another application.\n\n" +
 "Please close the file in the other application and try again.",
 ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warning(ex, "Access denied to file: {Path}", filePath);
            throw new UnauthorizedAccessException(
 $"Cannot save to '{Path.GetFileName(filePath)}' because access is denied.\n\n" +
 "Please check file permissions or choose a different location.",
 ex);
        }
    }
}