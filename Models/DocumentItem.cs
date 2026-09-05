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

    /// <summary>
    /// A divider row that starts a new output PDF. It is not a page and is never handed to
    /// <c>PdfService</c>; the window cuts the list at these and builds one document per run.
    /// </summary>
    /// <remarks>
    /// Living in the same collection as the pages is deliberate: the built-in reorder of
    /// <c>ListViewBase</c> writes back by calling <c>Move</c> on a flat vector, so a divider
    /// that is an ordinary row can be dragged to move a split point, and a page dragged past
    /// one changes which file it lands in - both for free. Grouping the ItemsSource instead
    /// would have made the view a read-only projection and taken drag reorder with it.
    /// </remarks>
    SectionBreak
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
    private string _sectionName = string.Empty;
    private string _sectionSummary = string.Empty;
    private string _sectionEstimate = string.Empty;

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

    /// <summary>True for a divider row rather than a page.</summary>
    public bool IsSectionBreak => Type == DocumentType.SectionBreak;

    /// <summary>
    /// The output file name for the run of pages this divider starts, without an extension.
    /// </summary>
    /// <remarks>
    /// Bound two-way to the band's text box, so it changes under the user's cursor and has to
    /// notify. It is what the user typed, not what will be written: <see cref="PdfSection"/>
    /// sanitises and de-duplicates at save time, because a name that is legal in the box can
    /// still be illegal on disk.
    /// </remarks>
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

    /// <summary>How many pages fall into this section, as shown on the band.</summary>
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

    /// <summary>This section's own estimated output size, as shown on the band.</summary>
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

    /// <summary>
    /// The size shown against a row in list view.
    /// </summary>
    /// <remarks>
    /// <see cref="FileSize"/> is the size of the file the item came from, so every page of
    /// one PDF reports the same number - twelve rows of a 2.58 MB document read as 31 MB.
    /// Only a loose image is genuinely one file, so only an image gets a size.
    /// </remarks>
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