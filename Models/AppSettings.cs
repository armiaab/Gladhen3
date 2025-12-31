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

    /// <summary>
    /// Custom page width in the selected unit (default: millimeters)
    /// </summary>
    public double CustomWidth { get; set; } = 210; // Default A4 width in mm

    /// <summary>
    /// Custom page height in the selected unit (default: millimeters)
    /// </summary>
    public double CustomHeight { get; set; } = 297; // Default A4 height in mm

    /// <summary>
    /// Unit for custom page size: 0 = millimeters, 1 = inches, 2 = points
    /// </summary>
    public int CustomSizeUnit { get; set; } = 0; // Default: millimeters

    private const string SettingsFileName = "appSettings.json";
    private static AppSettings _current = new();

    public static AppSettings Current => _current;

    /// <summary>
    /// Gets the margin in points (72 points = 1 inch)
    /// </summary>
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
    public double GetCustomWidthInPoints()
    {
        return CustomSizeUnit switch
        {
            0 => CustomWidth * 72.0 / 25.4, // mm to points
            1 => CustomWidth * 72.0,         // inches to points
            2 => CustomWidth,    // already in points
            _ => CustomWidth * 72.0 / 25.4
        };
    }

    /// <summary>
    /// Gets custom height in points (72 points = 1 inch)
    /// </summary>
    public double GetCustomHeightInPoints()
    {
        return CustomSizeUnit switch
        {
            0 => CustomHeight * 72.0 / 25.4, // mm to points
            1 => CustomHeight * 72.0,         // inches to points
            2 => CustomHeight,// already in points
            _ => CustomHeight * 72.0 / 25.4
        };
    }

    /// <summary>
    /// Gets the unit name for display
    /// </summary>
    public string GetUnitName()
    {
        return CustomSizeUnit switch
        {
            0 => "mm",
            1 => "in",
            2 => "pt",
            _ => "mm"
        };
    }

    public static async Task LoadAsync()
    {
        try
        {
            var folder = ApplicationData.Current.LocalFolder;
            var file = await folder.TryGetItemAsync(SettingsFileName) as StorageFile;

            if (file != null)
            {
                var json = await FileIO.ReadTextAsync(file);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null)
                    _current = loaded;
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Logger.Error(ex, "Failed to load settings");
        }
    }

    public static async Task SaveAsync()
    {
        try
        {
            var folder = ApplicationData.Current.LocalFolder;
            var file = await folder.CreateFileAsync(SettingsFileName, CreationCollisionOption.ReplaceExisting);

            var json = JsonSerializer.Serialize(_current);
            await FileIO.WriteTextAsync(file, json);
        }
        catch (Exception ex)
        {
            Serilog.Log.Logger.Error(ex, "Failed to save settings");
        }
    }
}
