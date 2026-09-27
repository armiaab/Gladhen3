using System;
using System.Diagnostics;
using System.IO;
using Serilog;

namespace Gladhen3.Services;

public static class FileService
{
    public static void OpenLogDirectory()
    {
        var logDirectory = AppLog.Directory;

        Directory.CreateDirectory(logDirectory);

        Process.Start(new ProcessStartInfo
        {
            FileName = logDirectory,
            UseShellExecute = true
        })?.Dispose();

        Log.Information("Log directory opened: {Path}", logDirectory);
    }
}
