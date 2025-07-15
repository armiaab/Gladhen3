using Serilog;
using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;

namespace Gladhen3.Services;

public static class FileService
{
    public static readonly string[] ValidImageExtensions = [".jpg", ".jpeg", ".png", ".bmp"];

    public static bool IsImageFile(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLower();
        return Array.Exists(ValidImageExtensions, ext => ext == extension);
    }

    public static async Task<StorageFile?> OpenFileWithTempCopyAsync(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return null;

            var fileName = string.Concat(Path.GetFileNameWithoutExtension(filePath), "_",
                Guid.NewGuid().ToString().AsSpan(0, 8), Path.GetExtension(filePath));

            var tempPath = Path.Combine(Path.GetTempPath(), fileName);

            File.Copy(filePath, tempPath, true);

            return await StorageFile.GetFileFromPathAsync(tempPath);
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, $"Error creating temporary copy of file: {filePath}");
            return null;
        }
    }

    public static void OpenLogDirectory()
    {
        var logDirectory = Path.Combine(ApplicationData.Current.LocalFolder.Path, "Logs");
        try
        {
            if (Directory.Exists(logDirectory))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = logDirectory,
                    UseShellExecute = true
                });
                Log.Information("Log directory opened: {Path}", logDirectory);
            }
            else
            {
                Log.Warning("Log directory does not exist: {Path}", logDirectory);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to open log directory: {Path}", logDirectory);
        }
    }
}