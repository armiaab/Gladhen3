using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Storage;

namespace Gladhen3.Models;

public class AppSettings
{
    public PdfPaperSize PaperSize { get; set; } = PdfPaperSize.Automatic;
    public PdfPaperOrientation Orientation { get; set; } = PdfPaperOrientation.Automatic;
    public PdfPageMargin Margin { get; set; } = PdfPageMargin.None;
    public PdfImageCompression ImageCompression { get; set; } = PdfImageCompression.None;

    /// <summary>
    /// Which encoder recompressed images go through: a built-in id, or "plugin:" and the file
    /// name of a codec DLL. See <see cref="Services.ImageCodecs"/>.
    /// </summary>
    public string ImageCodec { get; set; } = Services.ImageCodecs.JpegId;

    /// <summary>
    /// How normalize judges pictures of pages: the built-in model, "model:" and the file name of
    /// one the user added, or text recognition alone. See <see cref="Services.OrientationModels"/>.
    /// </summary>
    public string OrientationMethod { get; set; } = Services.OrientationModels.BuiltInId;

    /// <summary>Whether Windows' text recognition gets a say on pages the model cannot judge.</summary>
    public bool OrientationTextRecognitionFallback { get; set; } = true;
    public double CustomWidth { get; set; } = 210;
    public double CustomHeight { get; set; } = 297;
    public UnitSize CustomSizeUnit { get; set; } = UnitSize.Millimetre;

    private const string SettingsFileName = "appSettings.json";

    private const double MmToPoints = 72.0 / 25.4;
    private const double InchToPoints = 72.0;
    public UnitSize CustomMarginUnit { get; set; } = UnitSize.Millimetre;
    public double CustomMarginLeft { get; set; } = 0.5;
    public double CustomMarginRight { get; set; } = 0.5;
    public double CustomMarginTop { get; set; } = 0.5;
    public double CustomMarginBottom { get; set; } = 0.5;
    private static AppSettings _current = new();

    public static AppSettings Current => _current;

    internal static void ReplaceForTesting(AppSettings settings) => _current = settings;

    /// <summary>The margins in points, for the page as it is seen.</summary>
    /// <remarks>
    /// A preset puts the same margin on all four sides; Custom uses the four values as entered,
    /// in their unit. Custom used to fall through to a fixed half inch all round, so the four
    /// boxes in Settings changed nothing at all.
    /// </remarks>
    public (double Left, double Top, double Right, double Bottom) GetMarginsInPoints()
    {
        if (Margin != PdfPageMargin.Custom)
        {
            var all = GetMarginInPoints();
            return (all, all, all, all);
        }

        double ToPoints(double value) => Math.Max(0, CustomMarginUnit switch
        {
            UnitSize.Inch => value * InchToPoints,
            UnitSize.Point => value,
            _ => value * MmToPoints
        });

        return (ToPoints(CustomMarginLeft), ToPoints(CustomMarginTop), ToPoints(CustomMarginRight), ToPoints(CustomMarginBottom));
    }

    public double GetMarginInPoints()
    {
        return Margin switch
        {
            PdfPageMargin.None => 0,
            PdfPageMargin.Narrow => 18, // 0.25 inch
            PdfPageMargin.Normal => 36,      // 0.5 inch
            PdfPageMargin.Wide => 72,     // 1 inch
            PdfPageMargin.ExtraWide => 108,  // 1.5 inch
            _ => 36
        };
    }

    public double GetCustomWidthInPoints() => CustomSizeUnit switch
    {
        UnitSize.Millimetre => CustomWidth * MmToPoints,
        UnitSize.Inch => CustomWidth * InchToPoints,
        UnitSize.Point => CustomWidth,
        _ => CustomWidth * MmToPoints
    };

    public double GetCustomHeightInPoints() => CustomSizeUnit switch
    {
        UnitSize.Millimetre => CustomHeight * MmToPoints,
        UnitSize.Inch => CustomHeight * InchToPoints,
        UnitSize.Point => CustomHeight,
        _ => CustomHeight * MmToPoints
    };

    public string GetUnitName()
    {
        return CustomSizeUnit switch
        {
            UnitSize.Millimetre => "mm",
            UnitSize.Inch => "in",
            UnitSize.Point => "pt",
            _ => "mm"
        };
    }

    public static async Task LoadAsync()
    {
        try
        {
            var folder = ApplicationData.Current.LocalFolder;

            if (await folder.TryGetItemAsync(SettingsFileName) is not StorageFile file)
                return;

            var json = await FileIO.ReadTextAsync(file);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json);
            if (loaded != null)
                _current = loaded;
        }
        catch (JsonException ex)
        {
            Serilog.Log.Logger.Warning(ex, "Settings file is not valid JSON; continuing with defaults");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Logger.Warning(ex, "Settings file could not be read; continuing with defaults");
        }
    }

    public static async Task SaveAsync()
    {
        var folder = ApplicationData.Current.LocalFolder;
        var file = await folder.CreateFileAsync(SettingsFileName, CreationCollisionOption.ReplaceExisting);

        var json = JsonSerializer.Serialize(_current);
        await FileIO.WriteTextAsync(file, json);
    }
}
