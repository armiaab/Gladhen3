using Gladhen3.Models;
using Gladhen3.Services;
using Xunit;

namespace Gladhen3.Tests;

/// <summary>
/// Verifies GetPageDimensionsInPortrait and GetJpegQuality/GetRasterDpi stay in sync
/// with the enum values documented in PdfImageCompression and AppSettings.
/// </summary>
public class PageDimensionTests
{
    // ── Standard sizes ────────────────────────────────────────────────────────

    [Fact]
    public void A4_IsPortrait_WidthLessThanHeight()
    {
        var (w, h) = PdfService.GetPageDimensionsInPortrait(PdfPaperSize.A4);
        Assert.True(w < h, $"A4 portrait width ({w}) should be less than height ({h})");
    }

    [Fact]
    public void A4_Approx595x842()
    {
        var (w, h) = PdfService.GetPageDimensionsInPortrait(PdfPaperSize.A4);
        Assert.Equal(595, (int)System.Math.Round(w));
        Assert.Equal(842, (int)System.Math.Round(h));
    }

    [Fact]
    public void Letter_Approx612x792()
    {
        var (w, h) = PdfService.GetPageDimensionsInPortrait(PdfPaperSize.Letter);
        Assert.Equal(612, (int)System.Math.Round(w));
        Assert.Equal(792, (int)System.Math.Round(h));
    }

    [Fact]
    public void Legal_Approx612x1008()
    {
        var (w, h) = PdfService.GetPageDimensionsInPortrait(PdfPaperSize.Legal);
        Assert.Equal(612, (int)System.Math.Round(w));
        Assert.Equal(1008, (int)System.Math.Round(h));
    }

    [Fact]
    public void A3_IsPortrait_WidthLessThanHeight()
    {
        var (w, h) = PdfService.GetPageDimensionsInPortrait(PdfPaperSize.A3);
        Assert.True(w < h);
    }

    [Fact]
    public void A3_IsApproximatelyDoubleA4Height()
    {
        var (_, a4h) = PdfService.GetPageDimensionsInPortrait(PdfPaperSize.A4);
        var (a3w, _) = PdfService.GetPageDimensionsInPortrait(PdfPaperSize.A3);
        // A3 width ≈ A4 height
        Assert.Equal((int)System.Math.Round(a4h), (int)System.Math.Round(a3w));
    }

    [Fact]
    public void Custom_UsesAppSettingsValues()
    {
        var original = AppSettings.Current;
        AppSettings.ReplaceForTesting(new AppSettings
        {
            PaperSize      = PdfPaperSize.Custom,
            CustomSizeUnit = 1, // inches
            CustomWidth    = 5.0,
            CustomHeight   = 7.0
        });
        try
        {
            var (w, h) = PdfService.GetPageDimensionsInPortrait(PdfPaperSize.Custom);
            Assert.Equal(360, (int)System.Math.Round(w)); // 5 in × 72
            Assert.Equal(504, (int)System.Math.Round(h)); // 7 in × 72
        }
        finally
        {
            AppSettings.ReplaceForTesting(original);
        }
    }

    // ── JPEG quality ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(PdfImageCompression.None,   0.92)]
    [InlineData(PdfImageCompression.Low,    0.85)]
    [InlineData(PdfImageCompression.Medium, 0.65)]
    [InlineData(PdfImageCompression.High,   0.40)]
    public void GetJpegQuality_ReturnsExpectedValue(PdfImageCompression level, double expected)
    {
        var original = AppSettings.Current;
        AppSettings.ReplaceForTesting(new AppSettings { ImageCompression = level });
        try
        {
            Assert.Equal(expected, PdfService.GetJpegQuality(), 2);
        }
        finally
        {
            AppSettings.ReplaceForTesting(original);
        }
    }

    // ── Raster DPI ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(PdfImageCompression.Low,    150u)]
    [InlineData(PdfImageCompression.Medium, 120u)]
    [InlineData(PdfImageCompression.High,    96u)]
    public void GetRasterDpi_ReturnsExpectedDpi(PdfImageCompression level, uint expectedDpi)
    {
        var original = AppSettings.Current;
        AppSettings.ReplaceForTesting(new AppSettings { ImageCompression = level });
        try
        {
            Assert.Equal(expectedDpi, PdfService.GetRasterDpi());
        }
        finally
        {
            AppSettings.ReplaceForTesting(original);
        }
    }

    [Fact]
    public void LowerCompression_MeansHigherQuality()
    {
        var original = AppSettings.Current;
        try
        {
            AppSettings.ReplaceForTesting(new AppSettings { ImageCompression = PdfImageCompression.Low });
            var lowQ = PdfService.GetJpegQuality();

            AppSettings.ReplaceForTesting(new AppSettings { ImageCompression = PdfImageCompression.High });
            var highQ = PdfService.GetJpegQuality();

            Assert.True(lowQ > highQ);
        }
        finally
        {
            AppSettings.ReplaceForTesting(original);
        }
    }

    [Fact]
    public void LowerCompression_MeansHigherDpi()
    {
        var original = AppSettings.Current;
        try
        {
            AppSettings.ReplaceForTesting(new AppSettings { ImageCompression = PdfImageCompression.Low });
            var lowDpi = PdfService.GetRasterDpi();

            AppSettings.ReplaceForTesting(new AppSettings { ImageCompression = PdfImageCompression.High });
            var highDpi = PdfService.GetRasterDpi();

            Assert.True(lowDpi > highDpi);
        }
        finally
        {
            AppSettings.ReplaceForTesting(original);
        }
    }
}
