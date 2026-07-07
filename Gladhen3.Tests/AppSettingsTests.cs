using Gladhen3.Models;
using Xunit;

namespace Gladhen3.Tests;

public class AppSettingsTests
{
    private static AppSettings Make(
        PdfPageMargin margin = PdfPageMargin.None,
        PdfPaperSize size = PdfPaperSize.Automatic,
        int unit = 0,
        double w = 210, double h = 297,
        PdfImageCompression compression = PdfImageCompression.None) =>
        new()
        {
            Margin = margin,
            PaperSize = size,
            CustomSizeUnit = unit,
            CustomWidth = w,
            CustomHeight = h,
            ImageCompression = compression
        };

    // ── Margin ────────────────────────────────────────────────────────────────

    [Fact] public void Margin_None_Returns0() =>
        Assert.Equal(0, Make(PdfPageMargin.None).GetMarginInPoints());

    [Fact] public void Margin_Narrow_Returns18() =>
        Assert.Equal(18, Make(PdfPageMargin.Narrow).GetMarginInPoints());

    [Fact] public void Margin_Normal_Returns36() =>
        Assert.Equal(36, Make(PdfPageMargin.Normal).GetMarginInPoints());

    [Fact] public void Margin_Wide_Returns72() =>
        Assert.Equal(72, Make(PdfPageMargin.Wide).GetMarginInPoints());

    [Fact] public void Margin_ExtraWide_Returns108() =>
        Assert.Equal(108, Make(PdfPageMargin.ExtraWide).GetMarginInPoints());

    // ── Custom size – millimetres ─────────────────────────────────────────────

    [Fact]
    public void CustomWidth_Mm_ConvertsCorrectly()
    {
        // 25.4 mm = 1 inch = 72 pt
        var s = Make(unit: 0, w: 25.4, h: 25.4);
        Assert.Equal(72.0, s.GetCustomWidthInPoints(), precision: 3);
    }

    [Fact]
    public void CustomHeight_Mm_ConvertsCorrectly()
    {
        var s = Make(unit: 0, w: 25.4, h: 25.4);
        Assert.Equal(72.0, s.GetCustomHeightInPoints(), precision: 3);
    }

    [Fact]
    public void CustomWidth_A4_Mm()
    {
        // A4 width: 210 mm → 595.28 pt
        var s = Make(unit: 0, w: 210, h: 297);
        Assert.Equal(595.28, s.GetCustomWidthInPoints(), precision: 1);
    }

    // ── Custom size – inches ──────────────────────────────────────────────────

    [Fact]
    public void CustomWidth_Inches_ConvertsCorrectly()
    {
        // 1 inch = 72 pt
        var s = Make(unit: 1, w: 1.0, h: 1.0);
        Assert.Equal(72.0, s.GetCustomWidthInPoints(), precision: 3);
    }

    [Fact]
    public void CustomHeight_Inches_ConvertsCorrectly()
    {
        var s = Make(unit: 1, w: 8.5, h: 11.0);
        Assert.Equal(792.0, s.GetCustomHeightInPoints(), precision: 2);
    }

    // ── Custom size – points ──────────────────────────────────────────────────

    [Fact]
    public void CustomWidth_Points_IsIdentity()
    {
        var s = Make(unit: 2, w: 595, h: 842);
        Assert.Equal(595.0, s.GetCustomWidthInPoints());
    }

    [Fact]
    public void CustomHeight_Points_IsIdentity()
    {
        var s = Make(unit: 2, w: 595, h: 842);
        Assert.Equal(842.0, s.GetCustomHeightInPoints());
    }

    // ── Unit name ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, "mm")]
    [InlineData(1, "in")]
    [InlineData(2, "pt")]
    public void GetUnitName_ReturnsCorrectLabel(int unit, string expected)
    {
        var s = Make(unit: unit);
        Assert.Equal(expected, s.GetUnitName());
    }

    // ── Defaults ──────────────────────────────────────────────────────────────

    [Fact]
    public void DefaultSettings_HaveExpectedValues()
    {
        var s = new AppSettings();
        Assert.Equal(PdfPaperSize.Automatic, s.PaperSize);
        Assert.Equal(PdfPaperOrientation.Automatic, s.Orientation);
        Assert.Equal(PdfPageMargin.None, s.Margin);
        Assert.Equal(PdfImageCompression.None, s.ImageCompression);
        Assert.Equal(0, s.CustomSizeUnit);
        Assert.Equal(210, s.CustomWidth);
        Assert.Equal(297, s.CustomHeight);
    }

    // ── Test seam ─────────────────────────────────────────────────────────────

    [Fact]
    public void ReplaceForTesting_UpdatesCurrent()
    {
        var original = AppSettings.Current;
        var custom = new AppSettings { PaperSize = PdfPaperSize.A3 };
        AppSettings.ReplaceForTesting(custom);
        Assert.Equal(PdfPaperSize.A3, AppSettings.Current.PaperSize);
        AppSettings.ReplaceForTesting(original); // restore
    }
}
