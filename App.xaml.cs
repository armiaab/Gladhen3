using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;


namespace Gladhen3;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
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
                    System.Diagnostics.Debug.WriteLine($"Error parsing URI: {ex.Message}");
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
