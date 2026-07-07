using Gladhen3.Models;
using Gladhen3.Services;
using ImageMagick;
using PdfSharp.Pdf.IO;
using System;
using System.IO;
using Xunit;

namespace Gladhen3.Tests;

/// <summary>
/// Proves the compression feature actually shrinks output: the pixel-budget math, the
/// Magick.NET compressor in isolation, and full image→PDF runs whose files are measurably
/// smaller than both the uncompressed PDF and the source image.
/// </summary>
public class PdfCompressionTests : IDisposable
{
    private const double A4LongEdgeInches = 297.0 / 25.4; // ≈ 11.69"

    private readonly string _tmpDir = Path.Combine(Path.GetTempPath(), $"gladhen_comp_{Guid.NewGuid():N}");
    private readonly AppSettings _saved = AppSettings.Current;

    public PdfCompressionTests()
    {
        Directory.CreateDirectory(_tmpDir);
        AppSettings.ReplaceForTesting(new AppSettings());
    }

    public void Dispose()
    {
        AppSettings.ReplaceForTesting(_saved);
        try { Directory.Delete(_tmpDir, recursive: true); } catch { }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string TmpPath(string name) => Path.Combine(_tmpDir, name);

    /// <summary>
    /// Writes a high-entropy ("photo-like") image so JPEG can't trivially crush it — the
    /// source is genuinely large, making the size comparisons meaningful. Format follows the
    /// file extension.
    /// </summary>
    private string MakeNoisyImage(string name, uint width, uint height, double densityDpi = 0)
    {
        var path = TmpPath(name);
        using var image = new MagickImage(MagickColors.White, width, height);
        image.AddNoise(NoiseType.Gaussian);
        image.AddNoise(NoiseType.Gaussian);
        image.AddNoise(NoiseType.Impulse);
        if (densityDpi > 0)
            image.Density = new Density(densityDpi, densityDpi, DensityUnit.PixelsPerInch);
        image.Quality = 92; // "original quality"
        image.Write(path);
        return path;
    }

    private static DocumentItem ImageItem(string path) => new()
    {
        Type     = DocumentType.Image,
        FilePath = path,
        FileName = Path.GetFileName(path)
    };

    private static int CountPages(string pdfPath)
    {
        using var doc = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Import);
        return doc.PageCount;
    }

    private static long Len(string path) => new FileInfo(path).Length;

    private static (uint W, uint H) Dimensions(string path)
    {
        using var img = new MagickImage(path);
        return (img.Width, img.Height);
    }

    // ── Pixel-budget math (pure, no I/O) ──────────────────────────────────────

    [Fact]
    public void ComputeMaxLongEdgePixels_FixedPage_DownsamplesLargeImage()
    {
        // A4 long edge at 96 DPI ≈ 1123 px — far below a 4000 px source.
        var cap = PdfService.ComputeMaxLongEdgePixels(4000, 3000, 300, 96, A4LongEdgeInches);
        Assert.True(cap < 4000);
        Assert.InRange(cap, 1110, 1130);
    }

    [Fact]
    public void ComputeMaxLongEdgePixels_NeverUpscales()
    {
        // Tiny image, generous page/DPI: budget is clamped to the source size.
        var cap = PdfService.ComputeMaxLongEdgePixels(500, 400, 96, 150, A4LongEdgeInches);
        Assert.Equal(500, cap);
    }

    [Fact]
    public void ComputeMaxLongEdgePixels_Automatic_UsesImagePhysicalSize()
    {
        // Automatic (pageLongInches = null): 3000 px @ 300 DPI = 10", 10" × 96 DPI = 960 px.
        var cap = PdfService.ComputeMaxLongEdgePixels(3000, 2000, 300, 96, null);
        Assert.True(cap < 3000);
        Assert.InRange(cap, 950, 970);
    }

    [Fact]
    public void ComputeMaxLongEdgePixels_Automatic_LowDpiImage_NotDownsampled()
    {
        // A 96 DPI image rendered at its own size is already at the 96 DPI target → no shrink.
        var cap = PdfService.ComputeMaxLongEdgePixels(3000, 2000, 96, 96, null);
        Assert.Equal(3000, cap);
    }

    [Fact]
    public void ComputeMaxLongEdgePixels_HigherDpiAllowsMorePixels()
    {
        var at150 = PdfService.ComputeMaxLongEdgePixels(8000, 6000, 600, 150, A4LongEdgeInches);
        var at96  = PdfService.ComputeMaxLongEdgePixels(8000, 6000, 600, 96,  A4LongEdgeInches);
        Assert.True(at150 > at96);
        Assert.True(at150 < 8000);
    }

    // ── Magick compressor in isolation ────────────────────────────────────────

    [Fact]
    public void CompressImageWithMagick_FixedPageHigh_ShrinksBytesAndResolution()
    {
        var src = MakeNoisyImage("big.jpg", 3000, 2400);
        AppSettings.ReplaceForTesting(new AppSettings
        {
            PaperSize        = PdfPaperSize.A4,
            ImageCompression = PdfImageCompression.High
        });

        var dst = TmpPath("big_compressed.jpg");
        PdfService.CompressImageWithMagick(src, dst, useJpeg: true);

        Assert.True(Len(dst) < Len(src), $"compressed {Len(dst)} should be < source {Len(src)}");
        var (w, h) = Dimensions(dst);
        Assert.True(Math.Max(w, h) <= 1130, $"long edge {Math.Max(w, h)} should be downsampled to the A4/96 DPI budget");
    }

    [Fact]
    public void CompressImageWithMagick_None_PreservesResolution()
    {
        var src = MakeNoisyImage("orig.jpg", 1600, 1200);
        AppSettings.ReplaceForTesting(new AppSettings
        {
            PaperSize        = PdfPaperSize.A4,
            ImageCompression = PdfImageCompression.None
        });

        var dst = TmpPath("orig_none.jpg");
        PdfService.CompressImageWithMagick(src, dst, useJpeg: true);

        var (w, h) = Dimensions(dst);
        Assert.Equal((uint)1600, w);
        Assert.Equal((uint)1200, h);
    }

    // ── End-to-end image → PDF ────────────────────────────────────────────────

    [Fact]
    public void CreatePdf_A4_HighCompression_SmallerThanUncompressedAndSource()
    {
        var src = MakeNoisyImage("photo.jpg", 3000, 2400);

        AppSettings.ReplaceForTesting(new AppSettings
        {
            PaperSize        = PdfPaperSize.A4,
            ImageCompression = PdfImageCompression.None
        });
        var noneOut = TmpPath("a4_none.pdf");
        new PdfService().CreatePdfFromDocuments([ImageItem(src)], noneOut);

        AppSettings.ReplaceForTesting(new AppSettings
        {
            PaperSize        = PdfPaperSize.A4,
            ImageCompression = PdfImageCompression.High
        });
        var highOut = TmpPath("a4_high.pdf");
        new PdfService().CreatePdfFromDocuments([ImageItem(src)], highOut);

        Assert.Equal(1, CountPages(highOut));
        Assert.True(Len(highOut) < Len(noneOut), $"high {Len(highOut)} should be < none {Len(noneOut)}");
        Assert.True(Len(highOut) < Len(src),     $"high {Len(highOut)} should be < source image {Len(src)}");
    }

    [Fact]
    public void CreatePdf_Automatic_HighCompression_SmallerThanUncompressed()
    {
        // 300 DPI source so automatic mode (page == image's physical size) also downsamples.
        var src = MakeNoisyImage("auto.jpg", 3000, 2400, densityDpi: 300);

        AppSettings.ReplaceForTesting(new AppSettings
        {
            PaperSize        = PdfPaperSize.Automatic,
            ImageCompression = PdfImageCompression.None
        });
        var noneOut = TmpPath("auto_none.pdf");
        new PdfService().CreatePdfFromDocuments([ImageItem(src)], noneOut);

        AppSettings.ReplaceForTesting(new AppSettings
        {
            PaperSize        = PdfPaperSize.Automatic,
            ImageCompression = PdfImageCompression.High
        });
        var highOut = TmpPath("auto_high.pdf");
        new PdfService().CreatePdfFromDocuments([ImageItem(src)], highOut);

        Assert.Equal(1, CountPages(highOut));
        Assert.True(Len(highOut) < Len(noneOut), $"high {Len(highOut)} should be < none {Len(noneOut)}");
    }

    // ── PDF-page inputs (merge/re-save of existing PDFs) ─────────────────────
    //
    // Regression: pages imported via XPdfForm copy their embedded images byte-for-byte,
    // so compression used to have zero effect when the inputs were PDFs rather than
    // image files — output size == input size exactly.

    private static DocumentItem PdfPageItem(string path, int page, int total) => new()
    {
        Type          = DocumentType.PdfPage,
        FilePath      = path,
        SourcePdfPath = path,
        FileName      = Path.GetFileName(path),
        PageNumber    = page,
        TotalPages    = total
    };

    [Fact]
    public void CreatePdf_FromPdfPages_Automatic_High_ShrinksEmbeddedImages()
    {
        // Build an uncompressed "scanned" PDF (one big embedded JPEG) as the input.
        var src = MakeNoisyImage("scan.jpg", 3000, 2400, densityDpi: 300);
        AppSettings.ReplaceForTesting(new AppSettings
        {
            PaperSize        = PdfPaperSize.Automatic,
            ImageCompression = PdfImageCompression.None
        });
        var scannedPdf = TmpPath("scanned.pdf");
        new PdfService().CreatePdfFromDocuments([ImageItem(src)], scannedPdf);

        // Re-save that PDF's page with High compression.
        AppSettings.ReplaceForTesting(new AppSettings
        {
            PaperSize        = PdfPaperSize.Automatic,
            ImageCompression = PdfImageCompression.High
        });
        var outPdf = TmpPath("scanned_high.pdf");
        new PdfService().CreatePdfFromDocuments([PdfPageItem(scannedPdf, 1, 1)], outPdf);

        Assert.Equal(1, CountPages(outPdf));
        Assert.True(Len(outPdf) < Len(scannedPdf) / 2,
            $"out {Len(outPdf)} should be far smaller than input pdf {Len(scannedPdf)}");
    }

    [Fact]
    public void CreatePdf_FromPdfPages_FixedPage_High_ShrinksEmbeddedImages()
    {
        var src = MakeNoisyImage("scan2.jpg", 3000, 2400, densityDpi: 300);
        AppSettings.ReplaceForTesting(new AppSettings
        {
            PaperSize        = PdfPaperSize.Automatic,
            ImageCompression = PdfImageCompression.None
        });
        var scannedPdf = TmpPath("scanned2.pdf");
        new PdfService().CreatePdfFromDocuments([ImageItem(src)], scannedPdf);

        AppSettings.ReplaceForTesting(new AppSettings
        {
            PaperSize        = PdfPaperSize.A4,
            ImageCompression = PdfImageCompression.High
        });
        var outPdf = TmpPath("scanned2_a4_high.pdf");
        new PdfService().CreatePdfFromDocuments([PdfPageItem(scannedPdf, 1, 1)], outPdf);

        Assert.Equal(1, CountPages(outPdf));
        Assert.True(Len(outPdf) < Len(scannedPdf) / 2,
            $"out {Len(outPdf)} should be far smaller than input pdf {Len(scannedPdf)}");
    }

    [Fact]
    public void CreatePdf_FromPdfPages_NoCompression_DoesNotRecompress()
    {
        var src = MakeNoisyImage("scan3.jpg", 1600, 1200);
        AppSettings.ReplaceForTesting(new AppSettings
        {
            PaperSize        = PdfPaperSize.Automatic,
            ImageCompression = PdfImageCompression.None
        });
        var scannedPdf = TmpPath("scanned3.pdf");
        new PdfService().CreatePdfFromDocuments([ImageItem(src)], scannedPdf);

        var outPdf = TmpPath("scanned3_none.pdf");
        new PdfService().CreatePdfFromDocuments([PdfPageItem(scannedPdf, 1, 1)], outPdf);

        // Fast merge path: size stays in the same ballpark (no recompression, no inflation).
        Assert.Equal(1, CountPages(outPdf));
        Assert.InRange(Len(outPdf), Len(scannedPdf) / 2, Len(scannedPdf) * 2);
    }

    [Fact]
    public void CreatePdf_MoreCompression_ProducesSmallerFile()
    {
        var src = MakeNoisyImage("levels.jpg", 3000, 2400);

        long Run(PdfImageCompression level)
        {
            AppSettings.ReplaceForTesting(new AppSettings
            {
                PaperSize        = PdfPaperSize.A4,
                ImageCompression = level
            });
            var outPath = TmpPath($"level_{level}.pdf");
            new PdfService().CreatePdfFromDocuments([ImageItem(src)], outPath);
            return Len(outPath);
        }

        var low    = Run(PdfImageCompression.Low);
        var medium = Run(PdfImageCompression.Medium);
        var high   = Run(PdfImageCompression.High);

        Assert.True(medium < low,  $"medium {medium} should be < low {low}");
        Assert.True(high   < medium, $"high {high} should be < medium {medium}");
    }
}
