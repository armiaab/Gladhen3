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
