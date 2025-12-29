using Serilog;
using System;
using System.Diagnostics;
using System.IO;
using Windows.Storage;

namespace Gladhen3.Services;

public static class FileService
{
    public static void OpenLogDirectory()
    {
        var logDirectory = Path.Combine(ApplicationData.Current.LocalFolder.Path, "Logs");

        try
        {
            if (Directory.Exists(logDirectory))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = logDirectory,
                    UseShellExecute = true
                });
                Log.Information("Log directory opened: {Path}", logDirectory);
            }
            else
            {
                Directory.CreateDirectory(logDirectory);
                Process.Start(new ProcessStartInfo
                {
                    FileName = logDirectory,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to open log directory: {Path}", logDirectory);
        }
    }
}