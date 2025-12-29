using Gladhen3.Models;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Serilog;
using System;
using System.Collections.Generic;
using System.Drawing;
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

        try
        {
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
            Log.Information("PDF created with {PageCount} pages: {OutputPath}", pageList.Count, outputPath);
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

    private static void CreateSingleImagePdf(string imagePath, string outputPath)
    {
        using var document = new PdfDocument();
        var page = document.AddPage();
        using var bitmap = new Bitmap(imagePath);

        var imageWidth = bitmap.Width;
        var imageHeight = bitmap.Height;

        if (AppSettings.Current.PaperSize == PdfPaperSize.Automatic)
        {
            var pointWidth = imageWidth * 72.0 / 96.0;
            var pointHeight = imageHeight * 72.0 / 96.0;
            page.Width = new XUnit(pointWidth);
            page.Height = new XUnit(pointHeight);

            using var gfx = XGraphics.FromPdfPage(page);
            using var xImage = XImage.FromFile(imagePath);
            gfx.DrawImage(xImage, 0, 0, pointWidth, pointHeight);
        }
        else
        {
            SetPageSize(page, AppSettings.Current.PaperSize);

            var usePortrait = AppSettings.Current.Orientation == PdfPaperOrientation.Portrait ||
                (AppSettings.Current.Orientation == PdfPaperOrientation.Automatic && imageHeight > imageWidth);

            if (!usePortrait)
            {
                var temp = page.Height.Point;
                page.Height = page.Width;
                page.Width = new XUnit(temp);
            }

            using var gfx = XGraphics.FromPdfPage(page);
            using var xImage = XImage.FromFile(imagePath);

            var scaleX = page.Width.Point / imageWidth;
            var scaleY = page.Height.Point / imageHeight;
            var scale = Math.Min(scaleX, scaleY);

            var width = imageWidth * scale;
            var height = imageHeight * scale;
            var x = (page.Width.Point - width) / 2;
            var y = (page.Height.Point - height) / 2;

            gfx.DrawImage(xImage, x, y, width, height);
        }

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