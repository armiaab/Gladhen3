using System;
using System.IO;
using System.Text.Json;
using System.Threading;
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
    private static readonly AsyncLocal<AppSettings?> Pinned = new();

    /// <summary>The settings in force: a pinned snapshot inside a background operation, else the saved ones.</summary>
    /// <remarks>Copy-on-write: a published instance is never modified, only replaced.</remarks>
    public static AppSettings Current => Pinned.Value ?? _current;

    internal static void ReplaceForTesting(AppSettings settings) => _current = settings;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    /// <summary>
    /// Makes <paramref name="snapshot"/> what <see cref="Current"/> returns on this logical
    /// thread, and the tasks it starts, until the result is disposed.
    /// </summary>
    /// <remarks>Lets a save or estimate keep the values it started with while Settings changes.</remarks>
    public static IDisposable Pin(AppSettings snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var previous = Pinned.Value;
        Pinned.Value = snapshot;
        return new Unpin(previous);
    }

    private sealed class Unpin(AppSettings? previous) : IDisposable
    {
        public void Dispose() => Pinned.Value = previous;
    }

    /// <summary>The margins in points: a preset on all four sides, or the four custom values.</summary>
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

    /// <summary>Upper bound on the settings file; real ones are a few hundred bytes.</summary>
    private const long MaxSettingsFileBytes = 64 * 1024;

    /// <summary>Reads the saved settings. Called once, before the first window exists.</summary>
    /// <remarks>Synchronous so nothing can run on the defaults while the file is read.</remarks>
    public static void Load()
    {
        try
        {
            var path = Path.Combine(ApplicationData.Current.LocalFolder.Path, SettingsFileName);
            var info = new FileInfo(path);
            if (!info.Exists) return;

            if (info.Length > MaxSettingsFileBytes)
            {
                Serilog.Log.Logger.Warning("Settings file is {Bytes} bytes, over the {Max} limit; continuing with defaults", info.Length, MaxSettingsFileBytes);
                return;
            }

            var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
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

    /// <summary>Writes <paramref name="settings"/>, then makes it <see cref="Current"/>.</summary>
    /// <remarks>If the write fails, the in-memory settings stay as they were.</remarks>
    public static async Task SaveAsync(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var folder = ApplicationData.Current.LocalFolder;
        var file = await folder.CreateFileAsync(SettingsFileName, CreationCollisionOption.ReplaceExisting);

        var json = JsonSerializer.Serialize(settings);
        await FileIO.WriteTextAsync(file, json);

        _current = settings;
    }

    /// <summary>Saves a copy of the current settings with <paramref name="change"/> applied.</summary>
    public static Task UpdateAsync(Action<AppSettings> change)
    {
        var next = _current.Clone();
        change(next);
        return SaveAsync(next);
    }
}
