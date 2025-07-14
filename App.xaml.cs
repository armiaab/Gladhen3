using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;


namespace Gladhen3;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
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
