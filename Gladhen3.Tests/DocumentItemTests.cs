using Gladhen3.Models;
using Xunit;

namespace Gladhen3.Tests;

public class DocumentItemTests
{
    // ── TypeIcon ──────────────────────────────────────────────────────────────

    [Fact]
    public void TypeIcon_PdfPage_ReturnsPdfGlyph() =>
        Assert.Equal("", new DocumentItem { Type = DocumentType.PdfPage }.TypeIcon);

    [Fact]
    public void TypeIcon_Image_ReturnsPhotoGlyph() =>
        Assert.Equal("", new DocumentItem { Type = DocumentType.Image }.TypeIcon);

    // ── FileExtension ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(@"C:\docs\file.PDF",  ".pdf")]
    [InlineData(@"C:\pics\photo.JPG", ".jpg")]
    [InlineData(@"C:\pics\image.png", ".png")]
    public void FileExtension_IsLowercase(string path, string expected) =>
        Assert.Equal(expected, new DocumentItem { FilePath = path }.FileExtension);

    // ── DisplayName ───────────────────────────────────────────────────────────

    [Fact]
    public void DisplayName_SinglePage_IsJustFileName()
    {
        var item = new DocumentItem
        {
            Type = DocumentType.PdfPage,
            FileName = "doc.pdf",
            TotalPages = 1,
            PageNumber = 1
        };
        Assert.Equal("doc.pdf", item.DisplayName);
    }

    [Fact]
    public void DisplayName_MultiPage_IncludesPageNumber()
    {
        var item = new DocumentItem
        {
            Type = DocumentType.PdfPage,
            FileName = "doc.pdf",
            TotalPages = 5,
            PageNumber = 3
        };
        Assert.Equal("doc.pdf (Page 3/5)", item.DisplayName);
    }

    [Fact]
    public void DisplayName_Image_IsJustFileName()
    {
        var item = new DocumentItem
        {
            Type = DocumentType.Image,
            FileName = "photo.jpg",
            TotalPages = 1,
            PageNumber = 1
        };
        Assert.Equal("photo.jpg", item.DisplayName);
    }

    // ── PageInfo ──────────────────────────────────────────────────────────────

    [Fact]
    public void PageInfo_SinglePage_IsEmpty()
    {
        var item = new DocumentItem
        {
            Type = DocumentType.PdfPage,
            TotalPages = 1,
            PageNumber = 1
        };
        Assert.Equal(string.Empty, item.PageInfo);
    }

    [Fact]
    public void PageInfo_MultiPage_ShowsPageOf()
    {
        var item = new DocumentItem
        {
            Type = DocumentType.PdfPage,
            TotalPages = 10,
            PageNumber = 4
        };
        Assert.Equal("Page 4 of 10", item.PageInfo);
    }

    [Fact]
    public void PageInfo_Image_IsEmpty()
    {
        var item = new DocumentItem
        {
            Type = DocumentType.Image,
            TotalPages = 1,
            PageNumber = 1
        };
        Assert.Equal(string.Empty, item.PageInfo);
    }

    // ── INotifyPropertyChanged ────────────────────────────────────────────────

    [Fact]
    public void Thumbnail_Set_FiresPropertyChanged()
    {
        var item = new DocumentItem();
        string? raised = null;
        item.PropertyChanged += (_, e) => raised = e.PropertyName;

        item.Thumbnail = new object();

        Assert.Equal(nameof(DocumentItem.Thumbnail), raised);
    }

    [Fact]
    public void Thumbnail_SetSameValue_DoesNotFirePropertyChanged()
    {
        var item = new DocumentItem();
        var thumb = new object();
        item.Thumbnail = thumb;

        int count = 0;
        item.PropertyChanged += (_, _) => count++;
        item.Thumbnail = thumb; // same reference — no event expected

        Assert.Equal(0, count);
    }
}
