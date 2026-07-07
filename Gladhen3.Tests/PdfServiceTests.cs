using Gladhen3.Models;
using Gladhen3.Services;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using System;
using System.IO;
using Xunit;

namespace Gladhen3.Tests;

/// <summary>
/// Integration-style tests: create real PDF files, verify structure and properties.
/// WinRT image-conversion paths are stubbed out by the UNIT_TEST flag.
/// </summary>
public class PdfServiceTests : IDisposable
{
    private readonly string _tmpDir = Path.Combine(Path.GetTempPath(), $"gladhen_tests_{Guid.NewGuid():N}");
    private readonly AppSettings _saved = AppSettings.Current;

    public PdfServiceTests()
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

    private static string MakeSinglePagePdf(string path, double widthPt = 595, double heightPt = 842)
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Width  = new XUnit(widthPt);
        page.Height = new XUnit(heightPt);
        doc.Save(path);
        return path;
    }

    private static string MakeMultiPagePdf(string path, int pages)
    {
        using var doc = new PdfDocument();
        for (int i = 0; i < pages; i++)
        {
            var page = doc.AddPage();
            page.Width  = new XUnit(595);
            page.Height = new XUnit(842);
        }
        doc.Save(path);
        return path;
    }

    private static DocumentItem PdfPageItem(string pdfPath, int page, int total) => new()
    {
        Type          = DocumentType.PdfPage,
        FilePath      = pdfPath,
        SourcePdfPath = pdfPath,
        FileName      = Path.GetFileName(pdfPath),
        PageNumber    = page,
        TotalPages    = total
    };

    private static int CountPages(string pdfPath)
    {
        using var doc = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Import);
        return doc.PageCount;
    }

    // ── Page count ────────────────────────────────────────────────────────────

    [Fact]
    public void CreatePdf_SinglePdfPage_ProducesOnePage()
    {
        var src  = MakeSinglePagePdf(TmpPath("src.pdf"));
        var out_ = TmpPath("out.pdf");

        new PdfService().CreatePdfFromDocuments(
            [PdfPageItem(src, 1, 1)], out_);

        Assert.Equal(1, CountPages(out_));
    }

    [Fact]
    public void CreatePdf_ThreePages_ProducesThreePages()
    {
        var src  = MakeMultiPagePdf(TmpPath("src3.pdf"), 3);
        var out_ = TmpPath("out3.pdf");

        new PdfService().CreatePdfFromDocuments(
            [
                PdfPageItem(src, 1, 3),
                PdfPageItem(src, 2, 3),
                PdfPageItem(src, 3, 3)
            ], out_);

        Assert.Equal(3, CountPages(out_));
    }

    [Fact]
    public void CreatePdf_SubsetOfPages_ProducesCorrectCount()
    {
        var src  = MakeMultiPagePdf(TmpPath("src5.pdf"), 5);
        var out_ = TmpPath("out2.pdf");

        new PdfService().CreatePdfFromDocuments(
            [PdfPageItem(src, 2, 5), PdfPageItem(src, 4, 5)], out_);

        Assert.Equal(2, CountPages(out_));
    }

    // ── Custom page size ──────────────────────────────────────────────────────

    [Fact]
    public void CreatePdf_CustomA4_OutputPageIsA4Size()
    {
        AppSettings.ReplaceForTesting(new AppSettings
        {
            PaperSize = PdfPaperSize.A4,
            Orientation = PdfPaperOrientation.Portrait,
            Margin = PdfPageMargin.None
        });

        var src  = MakeSinglePagePdf(TmpPath("srcA4.pdf"));
        var out_ = TmpPath("outA4.pdf");

        new PdfService().CreatePdfFromDocuments([PdfPageItem(src, 1, 1)], out_);

        using var doc = PdfReader.Open(out_, PdfDocumentOpenMode.Import);
        var page = doc.Pages[0];
        Assert.Equal(595, (int)Math.Round(page.Width.Point));
        Assert.Equal(842, (int)Math.Round(page.Height.Point));
    }

    [Fact]
    public void CreatePdf_LandscapeLetter_WidthExceedsHeight()
    {
        AppSettings.ReplaceForTesting(new AppSettings
        {
            PaperSize   = PdfPaperSize.Letter,
            Orientation = PdfPaperOrientation.Landscape,
            Margin      = PdfPageMargin.None
        });

        var src  = MakeSinglePagePdf(TmpPath("srcLtr.pdf"));
        var out_ = TmpPath("outLtr.pdf");

        new PdfService().CreatePdfFromDocuments([PdfPageItem(src, 1, 1)], out_);

        using var doc = PdfReader.Open(out_, PdfDocumentOpenMode.Import);
        var page = doc.Pages[0];
        Assert.True(page.Width.Point > page.Height.Point);
    }

    // ── Automatic page size preserves original size ───────────────────────────

    [Fact]
    public void CreatePdf_Automatic_PreservesSourcePageDimensions()
    {
        AppSettings.ReplaceForTesting(new AppSettings
        {
            PaperSize   = PdfPaperSize.Automatic,
            Orientation = PdfPaperOrientation.Automatic
        });

        const double w = 720, h = 540; // 10" x 7.5" landscape
        var src  = MakeSinglePagePdf(TmpPath("srcAuto.pdf"), w, h);
        var out_ = TmpPath("outAuto.pdf");

        new PdfService().CreatePdfFromDocuments([PdfPageItem(src, 1, 1)], out_);

        using var doc = PdfReader.Open(out_, PdfDocumentOpenMode.Import);
        var page = doc.Pages[0];
        Assert.Equal((int)w, (int)Math.Round(page.Width.Point));
        Assert.Equal((int)h, (int)Math.Round(page.Height.Point));
    }

    // ── Error cases ───────────────────────────────────────────────────────────

    [Fact]
    public void CreatePdf_EmptyList_Throws()
    {
        var out_ = TmpPath("empty.pdf");
        Assert.Throws<InvalidOperationException>(() =>
            new PdfService().CreatePdfFromDocuments([], out_));
    }

    [Fact]
    public void EnsureFileIsWritable_NonExistentFile_DoesNotThrow()
    {
        var path = TmpPath("new_file.pdf");
        var ex = Record.Exception(() => PdfService.EnsureFileIsWritable(path));
        Assert.Null(ex);
    }

    [Fact]
    public void EnsureFileIsWritable_ExistingUnlockedFile_DoesNotThrow()
    {
        var path = TmpPath("unlocked.pdf");
        File.WriteAllText(path, "dummy");
        var ex = Record.Exception(() => PdfService.EnsureFileIsWritable(path));
        Assert.Null(ex);
    }

    [Fact]
    public void EnsureFileIsWritable_LockedFile_ThrowsIOException()
    {
        var path = TmpPath("locked.pdf");
        using var locked = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        Assert.Throws<IOException>(() => PdfService.EnsureFileIsWritable(path));
    }
}
