using Microsoft.UI.Xaml;
using Serilog;
using System;
using System.Buffers;
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
        HookGlobalExceptionHandlers();
    }

    /// <summary>
    /// Last line of defence for exceptions nothing else caught.
    /// </summary>
    /// <remarks>
    /// There was no handler at all before this, so a failure on a background thread or inside
    /// an unawaited task ended the process with nothing written to the log to say why.
    /// Nothing here marks the exception handled: the process state is unknown at that point,
    /// and carrying on regardless is how corrupt output gets written. It fails, but it fails
    /// with a record of what happened.
    /// </remarks>
    private void HookGlobalExceptionHandlers()
    {
        UnhandledException += (_, e) =>
        {
            Log.Fatal(e.Exception, "Unhandled exception on the UI thread");
            Log.CloseAndFlush();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception (terminating={Terminating})", e.IsTerminating);
            Log.CloseAndFlush();
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            // Marked observed so it does not escalate on finalization, but it is still a bug.
            Log.Error(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
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
        // Nothing else can be done about a logger that will not start - there is nowhere to
        // report it to - and it must not stop the app from running.
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to initialize logger: {ex.Message}");
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var filePaths = new List<string>();
        ParseCommandLine(filePaths);

        _mutex = new Mutex(true, MutexName, out bool createdNew);

        if (!createdNew)
        {
            Log.Information("Another instance is running, sending files to existing instance");
            SendFilesToExistingInstance(filePaths);
            Environment.Exit(0);
            return;
        }

        StartPipeServer();

        _mainWindow = filePaths.Count > 0 ? new MainWindow(filePaths) : new MainWindow();
        _mainWindow.Activate();
    }

    private static void SendFilesToExistingInstance(List<string> filePaths)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);

            var message = string.Join("|", filePaths);
            var bytes = Encoding.UTF8.GetBytes(message);

            var lengthBytes = BitConverter.GetBytes(bytes.Length);
            client.Write(lengthBytes, 0, lengthBytes.Length);
            client.Write(bytes, 0, bytes.Length);
            client.Flush();

            Log.Information("Sent {Count} file(s) to existing instance", filePaths.Count);
        }
        // The other instance may be busy, shutting down, or gone between the mutex check and
        // now. This one is exiting either way, so there is nobody left to report to.
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            Log.Error(ex, "Could not hand {Count} file(s) to the running instance", filePaths.Count);
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

                var lengthBuffer = new byte[4];
                var bytesRead = await server.ReadAsync(lengthBuffer.AsMemory(0, 4), cancellationToken);
                if (bytesRead < 4) continue;

                var messageLength = BitConverter.ToInt32(lengthBuffer, 0);
                if (messageLength <= 0 || messageLength > 1024 * 1024) continue; // Max 1 MB

                var messageBuffer = ArrayPool<byte>.Shared.Rent(messageLength);
                try
                {
                    bytesRead = await server.ReadAsync(messageBuffer.AsMemory(0, messageLength), cancellationToken);
                    if (bytesRead < messageLength) continue;

                    var message = Encoding.UTF8.GetString(messageBuffer, 0, messageLength);
                    var receivedPaths = message.Split('|', StringSplitOptions.RemoveEmptyEntries)
                                               .Where(File.Exists)
                                               .ToList();

                    if (receivedPaths.Count > 0)
                    {
                        Log.Information("Received {Count} file(s) from another instance", receivedPaths.Count);
                        _mainWindow?.DispatcherQueue.TryEnqueue(() =>
                        {
                            AddFilesToMainWindow(receivedPaths);
                            BringWindowToFront();
                        });
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(messageBuffer);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            // A long-lived background loop: one bad message or a client that disconnects
            // mid-write must not take the listener down for the rest of the session.
            catch (Exception ex)
            {
                Log.Error(ex, "Error in pipe server; continuing to listen");
                await Task.Delay(100, cancellationToken);
            }
        }

        Log.Information("IPC pipe server stopped");
    }

    private void AddFilesToMainWindow(List<string> filePaths)
    {
        if (_mainWindow == null) return;

        // Queued onto the dispatcher, so there is no caller left to propagate to.
        try
        {
            _mainWindow.AddFilesFromPaths(filePaths);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not add {Count} file(s) sent by another instance", filePaths.Count);
        }
    }

    private void BringWindowToFront()
    {
        if (_mainWindow == null) return;

        // Purely cosmetic, and the window handle is gone if the window is already closing.
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_mainWindow);

            ShowWindow(hwnd, SW_RESTORE);
            SetForegroundWindow(hwnd);
        }
        catch (COMException ex)
        {
            Log.Warning(ex, "Could not bring the window to the front");
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
        // Anything can invoke a registered protocol, so malformed input is ordinary rather
        // than exceptional - it is tested for instead of being caught after the fact.
        if (!Uri.TryCreate(uriString, UriKind.Absolute, out var uri))
        {
            Log.Warning("Ignoring a malformed activation URI");
            return;
        }

        var files = HttpUtility.ParseQueryString(uri.Query)["files"];
        if (string.IsNullOrEmpty(files))
            return;

        foreach (var encodedPath in files.Split(','))
        {
            var decodedPath = HttpUtility.UrlDecode(encodedPath);
            if (!string.IsNullOrEmpty(decodedPath) && File.Exists(decodedPath))
                filePaths.Add(decodedPath);
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

    private const int SW_RESTORE = 9;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
