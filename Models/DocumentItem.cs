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
    PdfPage,

    SectionBreak
}

public class DocumentItem : INotifyPropertyChanged
{
    private BitmapImage? _thumbnail;
    private string? _fileExtension;
    private string? _displayName;
    private string? _pageInfo;
    private string _sectionName = string.Empty;
    private string _sectionSummary = string.Empty;
    private string _sectionEstimate = string.Empty;

    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public DocumentType Type { get; set; }

    public BitmapImage? Thumbnail
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

    public bool IsSectionBreak => Type == DocumentType.SectionBreak;

    public string SectionName
    {
        get => _sectionName;
        set
        {
            var incoming = value ?? string.Empty;
            if (_sectionName == incoming) return;
            _sectionName = incoming;
            OnPropertyChanged();
        }
    }

    public string SectionSummary
    {
        get => _sectionSummary;
        set
        {
            if (_sectionSummary == value) return;
            _sectionSummary = value;
            OnPropertyChanged();
        }
    }

    public string SectionEstimate
    {
        get => _sectionEstimate;
        set
        {
            if (_sectionEstimate == value) return;
            _sectionEstimate = value;
            OnPropertyChanged();
        }
    }

    public int PageNumber { get; set; } = 1;
    public int TotalPages { get; set; } = 1;
    public string FileSize { get; set; } = string.Empty;
    public string? SourcePdfPath { get; set; }

    public string FileExtension => _fileExtension ??= Path.GetExtension(FilePath).ToLowerInvariant();

    public string TypeIcon => Type == DocumentType.PdfPage ? "" : "";

    public string DisplayName => _displayName ??= ComputeDisplayName();
    public string PageInfo => _pageInfo ??= ComputePageInfo();

    public string SizeInfo => Type == DocumentType.Image ? FileSize : string.Empty;

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