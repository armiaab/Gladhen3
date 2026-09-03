using System;
using System.Diagnostics;
using System.IO;
using Serilog;
using Windows.Storage;

namespace Gladhen3.Services;

public static class FileService
{
    /// <summary>Opens the folder the log files are written to in File Explorer.</summary>
    /// <remarks>
    /// Failures are not caught here. Previously they were logged and discarded, so pressing
    /// the button did nothing at all and the log entry explaining why was in the very folder
    /// that had failed to open. The caller decides what to tell the user.
    /// </remarks>
    /// <exception cref="IOException">The folder could not be created.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder could not be created.</exception>
    /// <exception cref="System.ComponentModel.Win32Exception">No handler could open the folder.</exception>
    public static void OpenLogDirectory()
    {
        var logDirectory = Path.Combine(ApplicationData.Current.LocalFolder.Path, "Logs");

        Directory.CreateDirectory(logDirectory);

        Process.Start(new ProcessStartInfo
        {
            FileName = logDirectory,
            UseShellExecute = true
        })?.Dispose();

        Log.Information("Log directory opened: {Path}", logDirectory);
    }
}
