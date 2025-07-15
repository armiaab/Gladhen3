using Gladhen3.Models;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Serilog;
using System;
using System.Collections.Generic;
using System.Drawing;

namespace Gladhen3.Services;

public class PdfService
{
    public void ConvertImagesToPdf(List<ImageItem> images, string outputPath)
    {
        try
        {
            PdfDocument document = new();
            document.Info.Title = "Converted Images";

            foreach (var imageItem in images)
            {
                try
                {
                    using var bitmap = new Bitmap(imageItem.FilePath!);

                    var page = document.AddPage();

                    var imageWidth = bitmap.Width;
                    var imageHeight = bitmap.Height;

                    if (AppSettings.Current.PaperSize == PdfPaperSize.Automatic)
                    {
                        var pointWidth = imageWidth * 72.0 / 96.0;
                        var pointHeight = imageHeight * 72.0 / 96.0;

                        page.Width = new XUnit(pointWidth);
                        page.Height = new XUnit(pointHeight);

                        using XGraphics gfx = XGraphics.FromPdfPage(page);
                        using var xImage = XImage.FromFile(imageItem.FilePath!);
                        gfx.DrawImage(xImage, 0, 0, pointWidth, pointHeight);
                    }
                    else
                    {
                        SetPageSize(page, AppSettings.Current.PaperSize);

                        bool usePortrait = AppSettings.Current.Orientation == PdfPaperOrientation.Portrait ||
                                          (AppSettings.Current.Orientation == PdfPaperOrientation.Automatic &&
                                           imageHeight > imageWidth);

                        if (!usePortrait)
                        {
                            var temp = page.Height.Point;
                            page.Height = page.Width;
                            page.Width = new XUnit(temp);
                        }

                        using XGraphics gfx = XGraphics.FromPdfPage(page);
                        using var xImage = XImage.FromFile(imageItem.FilePath!);

                        double scaleX = page.Width.Point / imageWidth;
                        double scaleY = page.Height.Point / imageHeight;
                        double scale = Math.Min(scaleX, scaleY);

                        double width = imageWidth * scale;
                        double height = imageHeight * scale;
                        double x = (page.Width.Point - width) / 2;
                        double y = (page.Height.Point - height) / 2;

                        gfx.DrawImage(xImage, x, y, width, height);
                    }
                }
                catch (Exception ex)
                {
                    Log.Logger.Error(ex, $"Error processing image: {imageItem.FilePath}");
                }
            }

            document.Save(outputPath);
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, $"Error converting images to PDF: {outputPath}");
            throw;
        }
    }

    private void SetPageSize(PdfPage page, PdfPaperSize size)
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