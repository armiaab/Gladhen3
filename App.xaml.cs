using Microsoft.UI.Xaml;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Windows.Storage;

namespace Gladhen3;

public partial class App : Application
{
    private const string MutexName = "Gladhen3_SingleInstance_Mutex";
    private const string PipeName = "Gladhen3_IPC_Pipe";

    private static Mutex? _mutex;
    private static CancellationTokenSource? _pipeServerCts;
    private MainWindow? _mainWindow;

    public static App? Instance { get; private set; }

 public App()
    {
        Instance = this;
        InitializeComponent();
      InitializeLogger();
    }

    private static void InitializeLogger()
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
  System.Diagnostics.Debug.WriteLine($"Failed to initialize logger: {ex.Message}");
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
 var filePaths = new List<string>();
 ParseCommandLine(filePaths);

        // Try to acquire the mutex
        _mutex = new Mutex(true, MutexName, out bool createdNew);

    if (!createdNew)
 {
            // Another instance is running - send files to it and exit
 Log.Information("Another instance is running, sending files to existing instance");
    SendFilesToExistingInstance(filePaths);
   Environment.Exit(0);
            return;
        }

     // This is the first instance - start the pipe server
   StartPipeServer();

        _mainWindow = filePaths.Count > 0 ? new MainWindow(filePaths) : new MainWindow();
  _mainWindow.Activate();
    }

    private static void SendFilesToExistingInstance(List<string> filePaths)
    {
        try
        {
    using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(3000); // 3 second timeout

  var message = string.Join("|", filePaths);
       var bytes = Encoding.UTF8.GetBytes(message);

// Write length first, then data
            var lengthBytes = BitConverter.GetBytes(bytes.Length);
    client.Write(lengthBytes, 0, lengthBytes.Length);
      client.Write(bytes, 0, bytes.Length);
            client.Flush();

            Log.Information("Sent {Count} file(s) to existing instance", filePaths.Count);
   }
        catch (Exception ex)
        {
  Log.Error(ex, "Failed to send files to existing instance");
   }
    }

    private void StartPipeServer()
    {
        _pipeServerCts = new CancellationTokenSource();
      _ = RunPipeServerAsync(_pipeServerCts.Token);
    }

    private async Task RunPipeServerAsync(CancellationToken cancellationToken)
    {
        Log.Information("Starting IPC pipe server");

        while (!cancellationToken.IsCancellationRequested)
  {
         try
            {
     await using var server = new NamedPipeServerStream(
               PipeName,
        PipeDirection.In,
  NamedPipeServerStream.MaxAllowedServerInstances,
    PipeTransmissionMode.Byte,
       PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(cancellationToken);

    if (cancellationToken.IsCancellationRequested)
        break;

      // Read length
     var lengthBuffer = new byte[4];
         var bytesRead = await server.ReadAsync(lengthBuffer.AsMemory(0, 4), cancellationToken);

              if (bytesRead < 4)
            continue;

         var messageLength = BitConverter.ToInt32(lengthBuffer, 0);

         if (messageLength <= 0 || messageLength > 1024 * 1024) // Max 1MB
          continue;

     // Read message
   var messageBuffer = new byte[messageLength];
      bytesRead = await server.ReadAsync(messageBuffer.AsMemory(0, messageLength), cancellationToken);

       if (bytesRead < messageLength)
          continue;

     var message = Encoding.UTF8.GetString(messageBuffer);
      var receivedPaths = message.Split('|', StringSplitOptions.RemoveEmptyEntries)
            .Where(File.Exists)
     .ToList();

                if (receivedPaths.Count > 0)
                {
        Log.Information("Received {Count} file(s) from another instance", receivedPaths.Count);

 // Dispatch to UI thread
   _mainWindow?.DispatcherQueue.TryEnqueue(() =>
           {
         AddFilesToMainWindow(receivedPaths);
       BringWindowToFront();
     });
    }
      }
       catch (OperationCanceledException)
      {
          break;
   }
      catch (Exception ex)
  {
    Log.Error(ex, "Error in pipe server");
  await Task.Delay(100, cancellationToken);
            }
    }

        Log.Information("IPC pipe server stopped");
    }

    private void AddFilesToMainWindow(List<string> filePaths)
    {
        if (_mainWindow == null) return;

    try
   {
            _mainWindow.AddFilesFromPaths(filePaths);
  }
        catch (Exception ex)
    {
     Log.Error(ex, "Error adding files from another instance");
 }
    }

    private void BringWindowToFront()
    {
        if (_mainWindow == null) return;

        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_mainWindow);

    // Restore if minimized and bring to front
            ShowWindow(hwnd, SW_RESTORE);
       SetForegroundWindow(hwnd);
        }
        catch (Exception ex)
        {
  Log.Error(ex, "Error bringing window to front");
   }
    }

    private static void ParseCommandLine(List<string> filePaths)
    {
     var cmdArgs = Environment.GetCommandLineArgs();

  foreach (var arg in cmdArgs)
        {
            if (arg.StartsWith("gladhen2:", StringComparison.OrdinalIgnoreCase))
            {
   ParseGladhenUri(arg, filePaths);
            }
     else if (arg != cmdArgs[0] && !arg.StartsWith('-') && File.Exists(arg))
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
       var query = HttpUtility.ParseQueryString(uri.Query);
            var files = query["files"];

      if (string.IsNullOrEmpty(files))
    return;

      foreach (var encodedPath in files.Split(','))
         {
      var decodedPath = HttpUtility.UrlDecode(encodedPath);
   if (!string.IsNullOrEmpty(decodedPath) && File.Exists(decodedPath))
          filePaths.Add(decodedPath);
            }
     }
        catch (Exception ex)
  {
            Log.Error(ex, "Failed to parse URI: {Uri}", uriString);
        }
    }

    /// <summary>
    /// Cleanup on app shutdown
    /// </summary>
    public static void Cleanup()
    {
      _pipeServerCts?.Cancel();
        _pipeServerCts?.Dispose();
     _mutex?.ReleaseMutex();
        _mutex?.Dispose();
    }

  // Native methods
    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
 private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
