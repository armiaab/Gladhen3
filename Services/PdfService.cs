using Gladhen3.Models;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Serilog;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;

namespace Gladhen3.Services;

public class PdfService
{
    // Cache for XImage instances to avoid repeated loading of the same image
    private readonly Dictionary<string, XImage> _imageCache = new(StringComparer.OrdinalIgnoreCase);

    // Cache for XPdfForm instances to avoid repeated loading of the same PDF
    private readonly Dictionary<string, XPdfForm> _pdfFormCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates a PDF from a list of DocumentItems (images and PDF pages) in their current order
    /// </summary>
    public void CreatePdfFromDocuments(List<DocumentItem> items, string outputPath)
    {
        var tempFiles = new List<string>();
        var useCustomPageSize = AppSettings.Current.PaperSize != PdfPaperSize.Automatic;

        try
        {
            if (useCustomPageSize)
            {
                // When custom page size is set, we need to re-render all pages
                CreatePdfWithCustomPageSize(items, outputPath);
            }
            else
            {
                // Automatic mode: use original sizes, just merge
                CreatePdfWithAutomaticPageSize(items, outputPath, tempFiles);
            }

            Log.Information("PDF created with {PageCount} pages: {OutputPath}", items.Count, outputPath);
        }
        finally
        {
            // Clean up temp files
            foreach (var tempFile in tempFiles)
            {
                try { File.Delete(tempFile); }
                catch { /* Ignore cleanup errors */ }
            }

            // Clear caches to free memory
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
                CreateSingleImagePdfOptimized(item.FilePath, tempPath);
                tempFiles.Add(tempPath);
                pageList.Add((tempPath, 0));
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

        MergePagesOptimized(pageList, outputPath);
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

        try
        {
            foreach (var item in items)
            {
                if (item.Type == DocumentType.Image)
                {
                    // Add image with custom page size - reuse XImage if same file
                    AddImageToDocumentOptimized(outputDocument, item.FilePath);
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
                    }
                }
            }
        }
        finally
        {
            currentInputDoc?.Dispose();
        }

        outputDocument.Save(outputPath);
    }

    /// <summary>
    /// Adds an image to the document with XImage caching for reuse
 /// </summary>
    private void AddImageToDocumentOptimized(PdfDocument document, string imagePath)
    {
        var page = document.AddPage();

        // Get or create cached XImage
        var xImage = GetOrCreateXImage(imagePath);
        var imageWidthPt = xImage.PointWidth;
        var imageHeightPt = xImage.PointHeight;

        // Set the specified page size
     SetPageSize(page, AppSettings.Current.PaperSize);

 // Determine orientation
        var isImageLandscape = imageWidthPt > imageHeightPt;
        var usePortrait = AppSettings.Current.Orientation switch
     {
     PdfPaperOrientation.Portrait => true,
            PdfPaperOrientation.Landscape => false,
     _ => !isImageLandscape // Automatic: match image orientation
        };

        // Swap page dimensions if needed for landscape
  if (!usePortrait)
        {
          (page.Width, page.Height) = (page.Height, page.Width);
     }

        using var gfx = XGraphics.FromPdfPage(page);

        // Get margin and calculate drawing area
        var marginPt = AppSettings.Current.GetMarginInPoints();
        var availableWidth = page.Width.Point - (marginPt * 2);
        var availableHeight = page.Height.Point - (marginPt * 2);

    // Calculate scale to fit within available area while maintaining aspect ratio
        var scaleX = availableWidth / imageWidthPt;
  var scaleY = availableHeight / imageHeightPt;
        var scale = Math.Min(Math.Min(scaleX, scaleY), 1.0); // Don't scale up

        var drawWidth = imageWidthPt * scale;
 var drawHeight = imageHeightPt * scale;

    // Center the image on the page
        var x = (page.Width.Point - drawWidth) / 2;
        var y = (page.Height.Point - drawHeight) / 2;

        gfx.DrawImage(xImage, x, y, drawWidth, drawHeight);
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

    // Create new page with custom size
        var newPage = document.AddPage();
        SetPageSize(newPage, AppSettings.Current.PaperSize);

  // Determine orientation based on source page or setting
        var isSourceLandscape = sourceWidth > sourceHeight;
        var usePortrait = AppSettings.Current.Orientation switch
        {
    PdfPaperOrientation.Portrait => true,
  PdfPaperOrientation.Landscape => false,
            _ => !isSourceLandscape // Automatic: match source orientation
        };

        // Swap page dimensions if needed for landscape
        if (!usePortrait)
     {
        (newPage.Width, newPage.Height) = (newPage.Height, newPage.Width);
    }

        using var gfx = XGraphics.FromPdfPage(newPage);

        // Get margin and calculate drawing area
      var marginPt = AppSettings.Current.GetMarginInPoints();
      var availableWidth = newPage.Width.Point - (marginPt * 2);
        var availableHeight = newPage.Height.Point - (marginPt * 2);

        // Calculate scale to fit within available area while maintaining aspect ratio
        var scaleX = availableWidth / sourceWidth;
    var scaleY = availableHeight / sourceHeight;
        var scale = Math.Min(Math.Min(scaleX, scaleY), 1.0); // Don't scale up

        var drawWidth = sourceWidth * scale;
var drawHeight = sourceHeight * scale;

        // Center the content on the page
        var x = (newPage.Width.Point - drawWidth) / 2;
 var y = (newPage.Height.Point - drawHeight) / 2;

gfx.DrawImage(form, x, y, drawWidth, drawHeight);
  }

    /// <summary>
    /// Gets a cached XImage or creates a new one
    /// </summary>
    private XImage GetOrCreateXImage(string imagePath)
    {
        if (_imageCache.TryGetValue(imagePath, out var cachedImage))
        {
            return cachedImage;
 }

        var xImage = XImage.FromFile(imagePath);
        _imageCache[imagePath] = xImage;
      return xImage;
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
    /// Creates a single image PDF with optimized memory usage
    /// </summary>
    private static void CreateSingleImagePdfOptimized(string imagePath, string outputPath)
    {
      using var document = new PdfDocument();
     var page = document.AddPage();

        // Load image using PDFsharp's XImage
        using var xImage = XImage.FromFile(imagePath);

   // Get image dimensions in points (PDFsharp uses 72 DPI internally)
        var imageWidthPt = xImage.PointWidth;
        var imageHeightPt = xImage.PointHeight;

      // Use image's natural size in points (Automatic mode)
      page.Width = new XUnit(imageWidthPt);
        page.Height = new XUnit(imageHeightPt);

    using var gfx = XGraphics.FromPdfPage(page);
        gfx.DrawImage(xImage, 0, 0, imageWidthPt, imageHeightPt);

        document.Save(outputPath);
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

      outputDocument.Save(outputPath);
    }

    private static void SetPageSize(PdfPage page, PdfPaperSize size)
    {
  switch (size)
        {
   case PdfPaperSize.A4:
      page.Width = XUnit.FromMillimeter(210);
      page.Height = XUnit.FromMillimeter(297);
                break;
     case PdfPaperSize.Letter:
      page.Width = XUnit.FromInch(8.5);
       page.Height = XUnit.FromInch(11);
       break;
      case PdfPaperSize.Legal:
                page.Width = XUnit.FromInch(8.5);
    page.Height = XUnit.FromInch(14);
         break;
            case PdfPaperSize.A3:
         page.Width = XUnit.FromMillimeter(297);
      page.Height = XUnit.FromMillimeter(420);
break;
        default:
         page.Width = XUnit.FromMillimeter(210);
  page.Height = XUnit.FromMillimeter(297);
          break;
        }
    }
}