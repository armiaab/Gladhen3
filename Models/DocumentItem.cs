#if !UNIT_TEST
using Microsoft.UI.Xaml.Media.Imaging;
#endif
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace Gladhen3.Models;

public enum DocumentType
{
    Image,
    PdfPage
}

public class DocumentItem : INotifyPropertyChanged
{
#if !UNIT_TEST
    private BitmapImage? _thumbnail;
#else
    private object? _thumbnail;
#endif
    private string? _fileExtension;
    private string? _displayName;
    private string? _pageInfo;

    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public DocumentType Type { get; set; }

#if !UNIT_TEST
    public BitmapImage? Thumbnail
#else
    public object? Thumbnail
#endif
    {
        get => _thumbnail;
        set
        {
            if (_thumbnail != value)
            {
                _thumbnail = value;
                OnPropertyChanged();
            }
        }
    }

    public int PageNumber { get; set; } = 1;
    public int TotalPages { get; set; } = 1;
    public string FileSize { get; set; } = string.Empty;
    public string? SourcePdfPath { get; set; }

    // Cached: FilePath never changes after construction.
    public string FileExtension => _fileExtension ??= Path.GetExtension(FilePath).ToLowerInvariant();

    // Segoe MDL2 Assets glyphs (EA90 = PDF, EB9F = Photo).
    public string TypeIcon => Type == DocumentType.PdfPage ? "" : "";

    // Cached: FileName / PageNumber / TotalPages are set once at construction.
    public string DisplayName => _displayName ??= ComputeDisplayName();
    public string PageInfo    => _pageInfo    ??= ComputePageInfo();

    private string ComputeDisplayName() =>
        Type == DocumentType.PdfPage && TotalPages > 1
            ? $"{FileName} (Page {PageNumber}/{TotalPages})"
            : FileName;

    private string ComputePageInfo() =>
        Type == DocumentType.PdfPage && TotalPages > 1
            ? $"Page {PageNumber} of {TotalPages}"
            : string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}