using Gladhen3.Models;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;

namespace Gladhen3.Services;

public class PdfService
{
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
                var pageList = new List<(string PdfPath, int PageIndex)>();

                foreach (var item in items)
                {
                    if (item.Type == DocumentType.Image)
                    {
                        var tempPath = Path.Combine(Path.GetTempPath(), $"gladhen_{Guid.NewGuid()}.pdf");
                        CreateSingleImagePdf(item.FilePath, tempPath);
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

                MergePages(pageList, outputPath);
            }

            Log.Information("PDF created with {PageCount} pages: {OutputPath}", items.Count, outputPath);
        }
        finally
        {
            foreach (var tempFile in tempFiles)
            {
                try { File.Delete(tempFile); }
                catch { /* Ignore cleanup errors */ }
            }
        }
    }

    /// <summary>
    /// Creates a PDF with all pages resized to the custom page size
    /// </summary>
    private static void CreatePdfWithCustomPageSize(List<DocumentItem> items, string outputPath)
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
                    // Add image with custom page size
                    AddImageToDocument(outputDocument, item.FilePath);
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
                        // Re-render PDF page to custom size
                        AddPdfPageToDocument(outputDocument, currentInputDoc, item.PageNumber - 1);
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
    /// Adds an image to the document with custom page size settings
    /// </summary>
    private static void AddImageToDocument(PdfDocument document, string imagePath)
    {
        var page = document.AddPage();

        using var xImage = XImage.FromFile(imagePath);
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

        // Get margin from settings
        var marginPt = AppSettings.Current.GetMarginInPoints();
        var availableWidth = page.Width.Point - (marginPt * 2);
        var availableHeight = page.Height.Point - (marginPt * 2);

        // Calculate scale to fit within available area while maintaining aspect ratio
        var scaleX = availableWidth / imageWidthPt;
        var scaleY = availableHeight / imageHeightPt;
        var scale = Math.Min(scaleX, scaleY);

        // Don't scale up if image is smaller than available area
        scale = Math.Min(scale, 1.0);

        var drawWidth = imageWidthPt * scale;
        var drawHeight = imageHeightPt * scale;

        // Center the image on the page
        var x = (page.Width.Point - drawWidth) / 2;
        var y = (page.Height.Point - drawHeight) / 2;

        gfx.DrawImage(xImage, x, y, drawWidth, drawHeight);
    }

    /// <summary>
    /// Adds a PDF page to the document, re-rendered to custom page size
    /// </summary>
    private static void AddPdfPageToDocument(PdfDocument document, PdfDocument sourceDoc, int pageIndex)
    {
        var sourcePage = sourceDoc.Pages[pageIndex];
        var sourceWidth = sourcePage.Width.Point;
        var sourceHeight = sourcePage.Height.Point;

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

        // Get margin from settings
        var marginPt = AppSettings.Current.GetMarginInPoints();
        var availableWidth = newPage.Width.Point - (marginPt * 2);
        var availableHeight = newPage.Height.Point - (marginPt * 2);

        // We need to import the page first to get a form we can draw
        // Create a temporary document to extract the page as a form
        using var tempDoc = new PdfDocument();
        var importedPage = tempDoc.AddPage(sourcePage);

        // Create XPdfForm from the source file and page
        var sourcePath = sourceDoc.FullPath;
        if (!string.IsNullOrEmpty(sourcePath))
        {
            using var form = XPdfForm.FromFile(sourcePath);
            form.PageIndex = pageIndex;

            // Calculate scale to fit within available area while maintaining aspect ratio
            var scaleX = availableWidth / sourceWidth;
            var scaleY = availableHeight / sourceHeight;
            var scale = Math.Min(scaleX, scaleY);

            // Don't scale up if source is smaller than available area
            scale = Math.Min(scale, 1.0);

            var drawWidth = sourceWidth * scale;
            var drawHeight = sourceHeight * scale;

            // Center the content on the page
            var x = (newPage.Width.Point - drawWidth) / 2;
            var y = (newPage.Height.Point - drawHeight) / 2;

            gfx.DrawImage(form, x, y, drawWidth, drawHeight);
        }
    }

    private static void CreateSingleImagePdf(string imagePath, string outputPath)
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

    private static void MergePages(List<(string PdfPath, int PageIndex)> pageList, string outputPath)
    {
        using var outputDocument = new PdfDocument();
        outputDocument.Info.Title = "Created with Gladhen3";

        var currentPdfPath = string.Empty;
        PdfDocument? currentInputDoc = null;

        try
        {
            foreach (var (pdfPath, pageIndex) in pageList)
            {
                if (pdfPath != currentPdfPath)
                {
                    currentInputDoc?.Dispose();
                    currentInputDoc = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Import);
                    currentPdfPath = pdfPath;
                }

                if (currentInputDoc != null && pageIndex >= 0 && pageIndex < currentInputDoc.PageCount)
                    outputDocument.AddPage(currentInputDoc.Pages[pageIndex]);
            }
        }
        finally
        {
            currentInputDoc?.Dispose();
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