using System.Diagnostics;
using System.Windows.Threading;
using WindowSearcher.App.Native;

namespace WindowSearcher.App.Tracking;

/// <summary>
/// Keeps the list of Alt-Tab style windows current. Event driven (focus, minimize, title change);
/// a cheap EnumWindows pass every few seconds catches windows opening and closing.
/// Runs entirely on the UI thread.
/// </summary>
internal sealed class WindowTracker : IDisposable
{
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
        "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland",
    };

    private readonly Dictionary<IntPtr, TrackedWindow> _windows = new();
    private readonly Dictionary<int, (string Name, string? Path)> _processes = new();
    private readonly List<IntPtr> _hooks = new();
    private readonly Win32.WinEventProc _winEventProc;
    private readonly DispatcherTimer _refreshTimer;
    private readonly int _ownPid = Environment.ProcessId;

    public event Action<TrackedWindow>? Added;
    public event Action<TrackedWindow>? Focused;
    public event Action<TrackedWindow>? TitleChanged;
    public event Action<IReadOnlyList<TrackedWindow>>? Closed;

    public WindowTracker()
    {
        _winEventProc = OnWinEvent;
        _refreshTimer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) => Refresh(),
            Dispatcher.CurrentDispatcher);
    }

    public IReadOnlyCollection<TrackedWindow> Windows => _windows.Values;

    public bool TryGet(IntPtr hwnd, out TrackedWindow window) => _windows.TryGetValue(hwnd, out window!);

    public void Start()
    {
        Refresh();
        const uint flags = Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS;
        _hooks.Add(Win32.SetWinEventHook(Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _winEventProc, 0, 0, flags));
        _hooks.Add(Win32.SetWinEventHook(Win32.EVENT_SYSTEM_MINIMIZESTART, Win32.EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, _winEventProc, 0, 0, flags));
        _hooks.Add(Win32.SetWinEventHook(Win32.EVENT_OBJECT_NAMECHANGE, Win32.EVENT_OBJECT_NAMECHANGE, IntPtr.Zero, _winEventProc, 0, 0, flags));
        _refreshTimer.Start();

        var fg = Win32.GetForegroundWindow();
        if (_windows.TryGetValue(fg, out var current)) current.LastFocused = DateTimeOffset.Now;
    }

    /// <summary>Re-enumerates top-level windows: adds new ones, drops closed ones, refreshes titles and Z-order.</summary>
    public void Refresh()
    {
        var seen = new List<IntPtr>(256);
        Win32.EnumWindows((h, _) =>
        {
            seen.Add(h);
            return true;
        }, IntPtr.Zero);

        var live = new HashSet<IntPtr>();
        var z = 0;
        foreach (var hwnd in seen)
        {
            if (_windows.TryGetValue(hwnd, out var existing))
            {
                if (!Win32.IsWindowVisible(hwnd)) continue;
                live.Add(hwnd);
                existing.ZOrder = z++;
                existing.IsMinimized = Win32.IsIconic(hwnd);
                UpdateTitle(existing);
                continue;
            }
            var created = TryCreate(hwnd);
            if (created is null) continue;
            created.ZOrder = z++;
            live.Add(hwnd);
            _windows[hwnd] = created;
            Added?.Invoke(created);
        }

        var closed = _windows.Values.Where(w => !live.Contains(w.Hwnd)).ToList();
        if (closed.Count == 0) return;
        foreach (var w in closed) _windows.Remove(w.Hwnd);
        var livePids = _windows.Values.Select(w => w.Pid).ToHashSet();
        foreach (var pid in _processes.Keys.Where(p => !livePids.Contains(p)).ToList()) _processes.Remove(pid);
        Closed?.Invoke(closed);
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero || idObject != Win32.OBJID_WINDOW || idChild != Win32.CHILDID_SELF) return;
        try
        {
            switch (eventType)
            {
                case Win32.EVENT_SYSTEM_MINIMIZEEND:
                    // Restoring isn't using: the tab scan restores windows without focus. When you restore
                    // one yourself, a foreground event follows.
                    if (_windows.TryGetValue(hwnd, out var restored)) restored.IsMinimized = false;
                    break;
                case Win32.EVENT_SYSTEM_FOREGROUND:
                    var w = GetOrAdd(hwnd) ?? GetOrAdd(Win32.GetAncestor(hwnd, 3 /* GA_ROOTOWNER */));
                    if (w is null) return;
                    w.IsMinimized = false;
                    w.LastFocused = DateTimeOffset.Now;
                    UpdateTitle(w);
                    Focused?.Invoke(w);
                    break;
                case Win32.EVENT_SYSTEM_MINIMIZESTART:
                    if (_windows.TryGetValue(hwnd, out var min)) min.IsMinimized = true;
                    break;
                case Win32.EVENT_OBJECT_NAMECHANGE:
                    if (_windows.TryGetValue(hwnd, out var named)) UpdateTitle(named);
                    break;
            }
        }
        catch (Exception ex)
        {
            Services.Log.Error("WinEvent handler failed", ex);
        }
    }

    private TrackedWindow? GetOrAdd(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        if (_windows.TryGetValue(hwnd, out var w)) return w;
        var created = TryCreate(hwnd);
        if (created is null) return null;
        _windows[hwnd] = created;
        Added?.Invoke(created);
        return created;
    }

    private void UpdateTitle(TrackedWindow w)
    {
        var title = Win32.GetWindowText(w.Hwnd);
        if (title.Length == 0 || title == w.Title) return;
        w.Title = title;
        TitleChanged?.Invoke(w);
    }

    private TrackedWindow? TryCreate(IntPtr hwnd)
    {
        if (!IsAltTabWindow(hwnd, out var className, out var title)) return null;
        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == _ownPid || pid == 0) return null;
#if DEBUG
        // tools\readme-screenshots: only the demo browser's windows exist, so nothing real can reach a screenshot.
        if (int.TryParse(Environment.GetEnvironmentVariable("WWS_DEMO_PID"), out var demoPid) && pid != demoPid) return null;
#endif

        // UWP apps live inside ApplicationFrameHost; the real process owns the CoreWindow child.
        if (className == "ApplicationFrameWindow")
        {
            var framePid = pid;
            Win32.EnumChildWindows(hwnd, (child, _) =>
            {
                Win32.GetWindowThreadProcessId(child, out var childPid);
                if (childPid != framePid) pid = childPid;
                return childPid == framePid;
            }, IntPtr.Zero);
        }

        var (name, path) = GetProcess(pid);
        return new TrackedWindow
        {
            Hwnd = hwnd,
            Pid = pid,
            ProcessName = name,
            ExePath = path,
            ClassName = className,
            Title = title,
            IsMinimized = Win32.IsIconic(hwnd),
        };
    }

    private (string Name, string? Path) GetProcess(int pid)
    {
        if (_processes.TryGetValue(pid, out var info)) return info;
        var path = Win32.GetProcessPath(pid);
        string name;
        if (path is not null)
        {
            name = System.IO.Path.GetFileNameWithoutExtension(path);
        }
        else
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                name = p.ProcessName;
            }
            catch (ArgumentException)
            {
                name = "unknown";
            }
        }
        info = (name, path);
        _processes[pid] = info;
        return info;
    }

    private static bool IsAltTabWindow(IntPtr hwnd, out string className, out string title)
    {
        className = string.Empty;
        title = string.Empty;
        if (!Win32.IsWindowVisible(hwnd)) return false;

        var style = (long)Win32.GetWindowLongPtr(hwnd, Win32.GWL_STYLE);
        if ((style & Win32.WS_CHILD) != 0) return false;
        var ex = (long)Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE);
        var appWindow = (ex & Win32.WS_EX_APPWINDOW) != 0;
        if (!appWindow)
        {
            if ((ex & Win32.WS_EX_TOOLWINDOW) != 0 || (ex & Win32.WS_EX_NOACTIVATE) != 0) return false;
            if (Win32.GetWindow(hwnd, Win32.GW_OWNER) != IntPtr.Zero) return false;
        }

        className = Win32.GetClassName(hwnd);
        if (ShellClasses.Contains(className)) return false;

        title = Win32.GetWindowText(hwnd);
        if (string.IsNullOrWhiteSpace(title)) return false;

        var bounds = Win32.GetVisibleBounds(hwnd);
        if (!Win32.IsIconic(hwnd) && (bounds.Width <= 1 || bounds.Height <= 1)) return false;

        // Cloaked windows are invisible (suspended UWP apps, shell tricks) unless they're
        // simply parked on another virtual desktop.
        if (Win32.IsCloaked(hwnd) && !VirtualDesktop.IsOnOtherDesktop(hwnd)) return false;
        return true;
    }

    public void Dispose()
    {
        _refreshTimer.Stop();
        foreach (var h in _hooks) Win32.UnhookWinEvent(h);
        _hooks.Clear();
    }
}
