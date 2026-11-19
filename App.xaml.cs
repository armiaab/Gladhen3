using Microsoft.UI.Xaml;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Gladhen3;

public partial class App : Application
{
    private const string MutexName = "Gladhen3_SingleInstance_Mutex";
    private const string PipeName = "Gladhen3_IPC_Pipe";

    private static Mutex? _mutex;
    private static CancellationTokenSource? _pipeServerCts;
    private MainWindow? _mainWindow;
    private Microsoft.UI.Dispatching.DispatcherQueue? _uiQueue;

    /// <summary>Files from other launches, waiting their turn. UI thread only.</summary>
    private readonly Queue<List<string>> _pendingActivations = new();
    private bool _drainingActivations;

    public static App? Instance { get; private set; }

    public App()
    {
        Instance = this;
        InitializeComponent();
        Services.AppLog.Initialize();
        HookGlobalExceptionHandlers();
    }

    /// <summary>Logs anything nothing else caught.</summary>
    /// <remarks>
    /// Nothing is marked handled: the process state is unknown by then, and carrying on risks
    /// writing corrupt output.
    /// </remarks>
    private void HookGlobalExceptionHandlers()
    {
        UnhandledException += (_, e) =>
        {
            Log.Fatal(e.Exception, "Unhandled exception on the UI thread");
            Services.AppLog.Shutdown();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception (terminating={Terminating})", e.IsTerminating);
            Services.AppLog.Shutdown();
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            // Observed so it does not escalate on finalization; it is still a bug.
            Log.Error(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
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
            // Environment.Exit skips finalizers, so flush the log by hand.
            Services.AppLog.Shutdown();
            Environment.Exit(0);
            return;
        }

        // Loaded before the window so nothing runs on the defaults.
        Models.AppSettings.Load();

        // Until the window exists, received files wait in the queue.
        _uiQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        StartPipeServer();

        _mainWindow = filePaths.Count > 0 ? new MainWindow(filePaths) : new MainWindow();
        _mainWindow.Activate();
        _ = DrainActivationsAsync();
    }

    /// <summary>Queues files handed over by another launch. UI thread only.</summary>
    private void EnqueueActivation(List<string> paths)
    {
        _pendingActivations.Enqueue(paths);
        _ = DrainActivationsAsync();
    }

    /// <summary>Adds queued activations one batch at a time, after the start-up load.</summary>
    /// <remarks>Serialised so launches cannot interleave pages or undo steps. UI thread only.</remarks>
    private async Task DrainActivationsAsync()
    {
        if (_drainingActivations || _mainWindow == null) return;
        _drainingActivations = true;

        try
        {
            await _mainWindow.InitialLoad;

            while (_pendingActivations.TryDequeue(out var paths))
            {
                await _mainWindow.AddFilesFromPathsAsync(paths);
                BringWindowToFront();
            }
        }
        finally
        {
            _drainingActivations = false;
        }
    }

    private static void SendFilesToExistingInstance(List<string> filePaths)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);

            Services.ActivationProtocol.Write(client, filePaths);

            Log.Information("Sent {Count} file(s) to existing instance", filePaths.Count);
        }
        // The other instance may be busy or gone; this one is exiting either way.
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or ArgumentException)
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

                var paths = await Services.ActivationProtocol.ReadAsync(server, cancellationToken);
                if (paths == null) continue;

                var existing = paths.Where(File.Exists).ToList();
                if (existing.Count == 0) continue;

                Log.Information("Received {Count} file(s) from another instance", existing.Count);
                if (_uiQueue?.TryEnqueue(() => EnqueueActivation(existing)) != true)
                    Log.Warning("Could not queue {Count} received file(s); the window is shutting down", existing.Count);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            // One bad message must not stop the listener.
            catch (Exception ex)
            {
                Log.Error(ex, "Error in pipe server; continuing to listen");
                await Task.Delay(100, cancellationToken);
            }
        }

        Log.Information("IPC pipe server stopped");
    }

    private void BringWindowToFront()
    {
        if (_mainWindow == null) return;

        // Cosmetic; the handle is gone if the window is closing.
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
        => filePaths.AddRange(Services.ActivationProtocol.ParseUri(uriString).Where(File.Exists));

    /// <summary>Releases the pipe server and the single-instance mutex.</summary>
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
