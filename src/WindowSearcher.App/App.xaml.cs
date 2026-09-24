using System.Windows;
using WindowSearcher.App.Services;
using WindowSearcher.App.UI;

namespace WindowSearcher.App;

public partial class App : Application
{
    private const string MutexName = @"Local\WindowSearcher.SingleInstance";
    private const string ShowEventName = @"Local\WindowSearcher.Show";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private AppCore? _core;
    private volatile bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Second launch (e.g. from the Start menu) just opens the launcher of the running instance,
        // optionally with "--grid" and/or "--query <text>".
        _mutex = new Mutex(true, MutexName, out var isFirst);
        var background = e.Args.Any(a => string.Equals(a, "--background", StringComparison.OrdinalIgnoreCase));
        if (!isFirst && background)
        {
            // Start-at-login while already running: nothing to do.
            _mutex.Dispose();
            _mutex = null;
            Shutdown();
            return;
        }
        if (!isFirst)
        {
            try
            {
                AppPaths.Ensure();
                File.WriteAllLines(AppPaths.Command, e.Args);
                // This process was just started by the user, so it may take the foreground;
                // hand that right to the running instance so its launcher gets keyboard focus.
                Native.Win32.AllowSetForegroundWindow(Native.Win32.ASFW_ANY);
                EventWaitHandle.OpenExisting(ShowEventName).Set();
            }
            catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or IOException) { }
            _mutex.Dispose();
            _mutex = null;
            Shutdown();
            return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

        AppPaths.Ensure();
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled UI exception", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Unhandled exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        Theme.Apply(Resources);
        _core = new AppCore(background);

        new Thread(() =>
        {
            while (_showEvent.WaitOne() && !_exiting)
            {
                var args = ReadCommandArgs();
                Dispatcher.BeginInvoke(() => RunCommand(args));
            }
        }) { IsBackground = true, Name = "ShowSignal" }.Start();

        Log.Info($"Started (pid {Environment.ProcessId})");
    }

    private static string[] ReadCommandArgs()
    {
        try
        {
            if (!File.Exists(AppPaths.Command)) return [];
            var args = File.ReadAllLines(AppPaths.Command);
            File.Delete(AppPaths.Command);
            return args;
        }
        catch (IOException)
        {
            return [];
        }
    }

    /// <summary>A second launch's arguments: --settings, --about, or the launcher (--grid, --query text).</summary>
    private void RunCommand(string[] args)
    {
        if (_core is null) return;
        bool Has(string flag) => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
        if (Has("--settings")) { _core.OpenSettings(); return; }
        if (Has("--about")) { _core.OpenAbout(); return; }
        var q = Array.FindIndex(args, a => a.Equals("--query", StringComparison.OrdinalIgnoreCase));
        var query = q >= 0 && q + 1 < args.Length ? string.Join(' ', args[(q + 1)..]) : null;
        _core.Launcher.ShowLauncher(Has("--grid"), query);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        _showEvent?.Set();
        _core?.Dispose();
        if (_mutex is not null)
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
        Log.Info("Exited");
        base.OnExit(e);
    }
}
