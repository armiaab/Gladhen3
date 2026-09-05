using System;
using System.IO;
using System.Runtime.InteropServices;
using Serilog;
using Serilog.Events;
using Windows.ApplicationModel;
using Windows.Storage;

namespace Gladhen3.Services;

/// <summary>
/// Owns the application log: where it is written, at what level, and how much of it is kept.
/// </summary>
/// <remarks>
/// The directory used to be built independently here and in <see cref="FileService"/>, so the
/// "open log folder" button and the logger could disagree about where the log lives. There is
/// one definition now and both use it.
/// </remarks>
public static class AppLog
{
    private const string FileNameTemplate = "gladhen3-.log";

    private const long MaxFileBytes = 8L * 1024 * 1024;
    private const int RetainedFiles = 7;

    private const string OutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}";

    /// <summary>The folder log files are written to.</summary>
    public static string Directory { get; } =
        Path.Combine(ApplicationData.Current.LocalFolder.Path, "Logs");

    /// <summary>
    /// Configures the logger and records what is running, so a log a user sends back
    /// identifies its own build.
    /// </summary>
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

    /// <summary>
    /// Flushes and closes the log.
    /// </summary>
    /// <remarks>
    /// Synchronous on purpose: an async flush races process exit, and the entries explaining
    /// why the app is closing are exactly the ones worth not losing.
    /// </remarks>
    public static void Shutdown() => Log.CloseAndFlush();
}
