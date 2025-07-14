using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.UI.Xaml;
using Serilog;
using Windows.Storage;


namespace Gladhen3;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
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

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var cmdArgs = Environment.GetCommandLineArgs();
        var filePaths = new List<string>();

        foreach (var arg in cmdArgs)
        {
            if (arg.StartsWith("gladhen2:"))
            {
                try
                {
                    var uri = new Uri(arg);
                    var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                    var files = query["files"];
                    if (!string.IsNullOrEmpty(files))
                    {
                        var encodedPaths = files.Split(',');
                        foreach (var encodedPath in encodedPaths)
                        {
                            var decodedPath = System.Web.HttpUtility.UrlDecode(encodedPath);
                            if (!string.IsNullOrEmpty(decodedPath))
                            {
                                filePaths.Add(decodedPath);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to parse gladhen2 URI: {Uri}", arg);
                }
            }
            else if (arg != cmdArgs[0])
            {
                filePaths.Add(arg);
            }
        }

        _window = filePaths.Count != 0 ? new MainWindow(filePaths) : new MainWindow();
        _window.Activate();
    }
}
