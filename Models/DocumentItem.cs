#if !UNIT_TEST
using Microsoft.UI.Xaml.Media.Imaging;
#endif
using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace Gladhen3.Models;

public enum DocumentType
{
    Image,
    PdfPage,
    SectionBreak,

    /// <summary>An empty page the user inserted. It has no source file.</summary>
    BlankPage
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
    private int _rotation;

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

    /// <summary>
    /// The clockwise turn the user has given this page: 0, 90, 180 or 270 degrees.
    /// </summary>
    /// <remarks>
    /// Added on top of whatever rotation a source PDF page already carries, so a page that
    /// arrived sideways and was turned upright reads 90 here, not its absolute orientation.
    /// </remarks>
    public int Rotation
    {
        get => _rotation;
        set
        {
            var normalised = NormaliseRotation(value);
            if (_rotation == normalised) return;

            _rotation = normalised;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ThumbnailAngle));
        }
    }

    /// <summary>
    /// <see cref="Rotation"/> as the double a RotateTransform's Angle takes; a classic binding
    /// will not convert an int to it on its own.
    /// </summary>
    /// <remarks>
    /// Deliberately not animated. Item containers are recycled as the list scrolls, and an
    /// animated turn would spin every recycled thumbnail into its new angle.
    /// </remarks>
    public double ThumbnailAngle => _rotation;

    /// <summary>Folds any whole number of quarter turns into 0, 90, 180 or 270.</summary>
    /// <remarks>PDF allows negative /Rotate values, and nothing but quarter turns.</remarks>
    public static int NormaliseRotation(int degrees)
    {
        if (degrees % 90 != 0)
            throw new ArgumentOutOfRangeException(nameof(degrees), degrees, "Pages can only be turned in quarter turns.");

        return ((degrees % 360) + 360) % 360;
    }

    /// <summary>Another row for the same page, which the user can then turn or move on its own.</summary>
    public DocumentItem Clone() => new()
    {
        FileName = FileName,
        FilePath = FilePath,
        Type = Type,
        PageNumber = PageNumber,
        TotalPages = TotalPages,
        FileSize = FileSize,
        SourcePdfPath = SourcePdfPath,
        _thumbnail = _thumbnail,
        _rotation = _rotation
    };

    public int PageNumber { get; set; } = 1;
    public int TotalPages { get; set; } = 1;
    public string FileSize { get; set; } = string.Empty;
    public string? SourcePdfPath { get; set; }

    public string FileExtension => _fileExtension ??= Path.GetExtension(FilePath).ToLowerInvariant();

    public string TypeIcon => Type switch
    {
        DocumentType.PdfPage => "",
        DocumentType.BlankPage => "",
        _ => ""
    };

    public string DisplayName => _displayName ??= ComputeDisplayName();
    public string PageInfo => _pageInfo ??= ComputePageInfo();

    public string SizeInfo => Type == DocumentType.Image ? FileSize : string.Empty;

    // Parsed once: an 800-page book formats these 1600 times as its tiles appear.
    private static CompositeFormat _displayNameFormat = CompositeFormat.Parse("{0} (Page {1}/{2})");
    private static CompositeFormat _pageInfoFormat = CompositeFormat.Parse("Page {0} of {1}");

    /// <summary>
    /// How a PDF page names itself in a tooltip or to a screen reader: file name, page number,
    /// page count. Set from the UI's resources at start-up; the model cannot reach them itself.
    /// </summary>
    public static string DisplayNameFormat
    {
        get => _displayNameFormat.Format;
        set => _displayNameFormat = CompositeFormat.Parse(value);
    }

    /// <summary>The line under a PDF page's thumbnail: page number, page count.</summary>
    public static string PageInfoFormat
    {
        get => _pageInfoFormat.Format;
        set => _pageInfoFormat = CompositeFormat.Parse(value);
    }

    private string ComputeDisplayName() =>
        Type == DocumentType.PdfPage && TotalPages > 1
            ? string.Format(CultureInfo.CurrentCulture, _displayNameFormat, FileName, PageNumber, TotalPages)
            : FileName;

    private string ComputePageInfo() =>
        Type == DocumentType.PdfPage && TotalPages > 1
            ? string.Format(CultureInfo.CurrentCulture, _pageInfoFormat, PageNumber, TotalPages)
            : string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}