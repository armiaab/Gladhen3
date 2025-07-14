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

    private const string SettingsFileName = "appSettings.json";
    private static AppSettings _current = new();

    public static AppSettings Current => _current;

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

public enum PdfPaperSize
{
    Automatic,
    A4,
    Letter,
    Legal,
    A3
}

public enum PdfPaperOrientation
{
    Automatic,
    Portrait,
    Landscape
}