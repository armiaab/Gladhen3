using Microsoft.UI.Xaml;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;


namespace Gladhen3;

public partial class App : Application
{
    private MainWindow? _window;
    public App()
    {
        InitializeComponent();

        Task.Run(InitializeFullLogger);
    }

    private void InitializeFullLogger()
    {
        try
        {
            var logFilePath = Path.Combine(
                ApplicationData.Current.LocalFolder.Path,
                "Logs",
                "gladhen3-.log");

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File(logFilePath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();

            Log.Information("Application started");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize full logger");
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var filePaths = new List<string>();

        ParseCommandLine(filePaths);

        _window = filePaths.Count != 0 ? new MainWindow(filePaths) : new MainWindow();
        _window.Activate();
    }

    private void ParseCommandLine(List<string> filePaths)
    {
        var cmdArgs = Environment.GetCommandLineArgs();

        foreach (var arg in cmdArgs)
        {
            if (arg.StartsWith("gladhen2:", StringComparison.OrdinalIgnoreCase))
            {
                ParseGladhenUri(arg, filePaths);
            }
            else if (arg != cmdArgs[0])
            {
                filePaths.Add(arg);
            }
        }
    }

    private static void ParseGladhenUri(string uriString, List<string> filePaths)
    {
        try
        {
            var uri = new Uri(uriString);
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var files = query["files"];

            if (string.IsNullOrEmpty(files))
                return;

            var encodedPaths = files.Split(',');
            foreach (var encodedPath in encodedPaths)
            {
                var decodedPath = System.Web.HttpUtility.UrlDecode(encodedPath);
                if (!string.IsNullOrEmpty(decodedPath))
                    filePaths.Add(decodedPath);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to parse gladhen2 URI: {Uri}", uriString);
        }
    }
}
