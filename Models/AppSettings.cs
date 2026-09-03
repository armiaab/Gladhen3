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

    /// <summary>
    /// Gets custom width in points (72 points = 1 inch)
    /// </summary>
    public double GetCustomWidthInPoints() => CustomSizeUnit switch
    {
        UnitSize.Millimetre => CustomWidth * MmToPoints,
        UnitSize.Inch => CustomWidth * InchToPoints,
        UnitSize.Point => CustomWidth,
        _ => CustomWidth * MmToPoints
    };

    /// <summary>
    /// Gets custom height in points (72 points = 1 inch)
    /// </summary>
    public double GetCustomHeightInPoints() => CustomSizeUnit switch
    {
        UnitSize.Millimetre => CustomHeight * MmToPoints,
        UnitSize.Inch => CustomHeight * InchToPoints,
        UnitSize.Point => CustomHeight,
        _ => CustomHeight * MmToPoints
    };

    /// <summary>
    /// Gets the unit name for display
    /// </summary>
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

    /// <summary>
    /// Loads persisted settings, falling back to defaults.
    /// </summary>
    /// <remarks>
    /// Continuing with defaults is the correct behaviour here: a missing or corrupt settings
    /// file must not stop the app from starting. Only the failures that mean exactly that are
    /// handled - anything else is a defect and is allowed to surface.
    /// </remarks>
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

    /// <summary>Writes the current settings.</summary>
    /// <remarks>
    /// Failures propagate. Silently discarding them meant a user could change a setting, see
    /// no error, and find it reverted on next launch.
    /// </remarks>
    /// <exception cref="IOException">The settings file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The settings file could not be written.</exception>
    public static async Task SaveAsync()
    {
        var folder = ApplicationData.Current.LocalFolder;
        var file = await folder.CreateFileAsync(SettingsFileName, CreationCollisionOption.ReplaceExisting);

        var json = JsonSerializer.Serialize(_current);
        await FileIO.WriteTextAsync(file, json);
    }
}
