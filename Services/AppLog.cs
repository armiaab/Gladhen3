using System;
using System.IO;
using System.Runtime.InteropServices;
using Serilog;
using Serilog.Events;
using Windows.ApplicationModel;
using Windows.Storage;

namespace Gladhen3.Services;

/// <summary>Owns the application log: location, level and retention.</summary>
/// <remarks>The single definition of the log directory, shared with <see cref="FileService"/>.</remarks>
public static class AppLog
{
    private const string FileNameTemplate = "gladhen3-.log";

    private const long MaxFileBytes = 8L * 1024 * 1024;
    private const int RetainedFiles = 7;

    private const string OutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}";

    /// <summary>Why the log could not be opened, if it could not; the app then runs without one.</summary>
    public static Exception? InitializationFailure { get; private set; }

    public static string Directory { get; } =
        Path.Combine(ApplicationData.Current.LocalFolder.Path, "Logs");

    /// <summary>Configures the logger and records the running build.</summary>
    public static void Initialize()
    {
#if DEBUG
        const LogEventLevel minimumLevel = LogEventLevel.Debug;
#else
        const LogEventLevel minimumLevel = LogEventLevel.Information;
#endif

        try
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Is(minimumLevel)
                .WriteTo.File(
                    Path.Combine(Directory, FileNameTemplate),
                    rollingInterval: RollingInterval.Day,
                    rollOnFileSizeLimit: true,
                    fileSizeLimitBytes: MaxFileBytes,
                    retainedFileCountLimit: RetainedFiles,
                    outputTemplate: OutputTemplate)
                .CreateLogger();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Recorded, not shown: no window exists yet, and an ownerless MessageDialog throws in a
            // desktop app. The main window reports it later.
            InitializationFailure = ex;
            return;
        }

        var version = Package.Current.Id.Version;
        Log.Information(
            "Gladhen3 {Version} started ({Architecture}, {Runtime}, {OS})",
            $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}",
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription);
    }

    /// <summary>Flushes and closes the log.</summary>
    /// <remarks>Synchronous: an async flush races process exit and loses the last entries.</remarks>
    public static void Shutdown() => Log.CloseAndFlush();
}
