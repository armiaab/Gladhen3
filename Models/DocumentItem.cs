using Microsoft.UI.Xaml.Media.Imaging;
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
    private BitmapImage? _thumbnail;

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

    public int PageNumber { get; set; } = 1;
    public int TotalPages { get; set; } = 1;
    public string FileSize { get; set; } = string.Empty;
    public string? SourcePdfPath { get; set; }

    public string FileExtension => Path.GetExtension(FilePath).ToLowerInvariant();

    public string TypeIcon => Type == DocumentType.PdfPage ? "\uEA90" : "\uEB9F";

    public string DisplayName => Type == DocumentType.PdfPage && TotalPages > 1
        ? $"{FileName} (Page {PageNumber}/{TotalPages})"
        : FileName;

    public string PageInfo => Type == DocumentType.PdfPage && TotalPages > 1
        ? $"Page {PageNumber} of {TotalPages}"
  : string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
