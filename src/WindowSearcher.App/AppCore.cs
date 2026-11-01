using System.IO;
using System.Windows.Interop;
using System.Windows.Threading;
using WindowSearcher.App.Capture;
using WindowSearcher.App.Extraction;
using WindowSearcher.App.Native;
using WindowSearcher.App.Search;
using WindowSearcher.App.Services;
using WindowSearcher.App.Tracking;
using WindowSearcher.App.UI;
using WindowSearcher.Core;

namespace WindowSearcher.App;

/// <summary>Wires tracking, extraction, search and UI together. Lives on the UI thread.</summary>
internal sealed class AppCore : IDisposable
{
    private const int TabRefreshDelayMs = 1500;

    private volatile AppSettings _settings;
    private readonly InteractiveUia _interactive = new();
    private readonly HotkeyManager _hotkey;
    private readonly TrayIcon _tray;
    private readonly FileSystemWatcher _settingsWatcher;
    private readonly DispatcherTimer _settingsDebounce;
    private readonly DispatcherTimer _purgeTimer;

    public AppCore(bool background)
    {
        _settings = AppSettings.Load(AppPaths.Settings);
        UiaReader.PageFreeBrowserReads = _settings.ReadTabsWithoutPages;
        using (var index = new SearchIndex(AppPaths.Database, SecureStore.DatabaseKey)) Docs.Load(index.GetSummaries());

        Tracker = new WindowTracker();
        Search = new SearchService(Tracker, Docs, AppPaths.Database, () => _settings);
        Extraction = new ExtractionService(() => _settings, Dispatcher.CurrentDispatcher, OnExtractionResult, OnSlowApp);
        Seed = new SeedService(Extraction, () => _settings, Dispatcher.CurrentDispatcher);
        Launcher = new LauncherWindow(this);
        new WindowInteropHelper(Launcher).EnsureHandle();

        Tracker.Added += OnWindowAdded;
        Tracker.Focused += w => Schedule(w, JobKind.Foreground, _settings.CaptureDelayMs);
        Tracker.TitleChanged += OnTitleChanged;
        Tracker.Closed += OnWindowsClosed;
        Tracker.Start();
        RemoveDocsOfClosedWindows();
        RemoveExcludedDocs();
        StartupSweep();

        _hotkey = new HotkeyManager(() => Launcher.Toggle());
        _tray = new TrayIcon(this);
        Seed.ProgressChanged += () => _tray.SetStatus($"Scanning tabs {Seed.Done}/{Seed.Total}…");
        Seed.Completed += OnSeedCompleted;
        var hotkeyOk = _hotkey.Register(_settings.Hotkey);
        if (!hotkeyOk)
            _tray.ShowBalloon("Hotkey unavailable",
                $"{_settings.Hotkey} is invalid or used by another app. Change \"Hotkey\" in Settings from the tray menu.");
        else if (!background)
            _tray.ShowBalloon("Windows Window Search is running",
                $"Press {_settings.Hotkey} to search your windows and tabs. Previews and page text are collected as you use them.");

        _settingsDebounce = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background,
            (_, _) => ReloadSettings(), Dispatcher.CurrentDispatcher);
        _settingsDebounce.Stop();
        _settingsWatcher = new FileSystemWatcher(AppPaths.Root, Path.GetFileName(AppPaths.Settings))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        _settingsWatcher.Changed += (_, _) => Launcher.Dispatcher.BeginInvoke(() =>
        {
            _settingsDebounce.Stop();
            _settingsDebounce.Start();
        });

        _purgeTimer = new DispatcherTimer(TimeSpan.FromMinutes(30), DispatcherPriority.Background,
            (_, _) => PurgeOldTabs(), Dispatcher.CurrentDispatcher);
        _purgeTimer.Start();
    }

    public AppSettings Settings => _settings;
    public WindowTracker Tracker { get; }
    public DocCache Docs { get; } = new();
    public SearchService Search { get; }
    public ExtractionService Extraction { get; }
    public SeedService Seed { get; }
    public LauncherWindow Launcher { get; }

    public async Task ActivateAsync(ResultItem item)
    {
        switch (item.Kind)
        {
            case ResultKind.ClosedTab:
                if (SearchService.IsWebUrl(item.Url)) WindowActivator.OpenUrl(item.Url!);
                break;
            case ResultKind.Tab:
                if (!item.IsActiveTab)
                {
                    var select = _interactive.Run(r => r.SelectTab(item.Hwnd, item.Title, item.TabIndex));
                    var finished = await Task.WhenAny(select, Task.Delay(1500));
                    if (finished != select || await select < 0)
                        Log.Info("Could not select a tab");
                }
                WindowActivator.Activate(item.Hwnd);
                break;
            default:
                WindowActivator.Activate(item.Hwnd);
                break;
        }
    }

    /// <summary>Private windows and excluded apps are listed by title only: no screenshots, no text.</summary>
    private bool IsSensitive(TrackedWindow w) =>
        (w.IsPrivate && !_settings.IndexPrivateWindows) || _settings.IsProcessExcluded(w.ProcessName);

    public void TogglePrivateWindows()
    {
        _settings.IndexPrivateWindows = !_settings.IndexPrivateWindows;
        _settings.Save(AppPaths.Settings);
        _tray.UpdateState();
        if (!_settings.IndexPrivateWindows) return;
        // Pick up what's showing in InPrivate windows right away.
        var i = 0;
        foreach (var w in Tracker.Windows.Where(w => w.IsPrivate))
            Schedule(w, JobKind.Sweep, 300 + i++ * 200);
    }

    private SettingsWindow? _settingsWindow;
    private AboutWindow? _aboutWindow;

    /// <summary>Opens the settings window, or brings it forward if it's already open.</summary>
    public void OpenSettings()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(this);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        _settingsWindow.Activate();
    }

    public void OpenAbout()
    {
        if (_aboutWindow is null)
        {
            _aboutWindow = new AboutWindow();
            _aboutWindow.Closed += (_, _) => _aboutWindow = null;
            _aboutWindow.Show();
        }
        _aboutWindow.Activate();
    }

    /// <summary>Opens the user guide (Help\index.html next to the exe) in the default browser.</summary>
    public void OpenHelp()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Help", "index.html");
        try
        {
            if (!File.Exists(path)) throw new FileNotFoundException("The user guide is missing", path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Could not open {path}", ex);
            _tray.ShowBalloon("Help unavailable", "The user guide could not be opened. See the log for details.");
        }
    }

    public void TogglePause()
    {
        _settings.IndexingPaused = !_settings.IndexingPaused;
        _settings.Save(AppPaths.Settings);
        _tray.UpdateState();
    }

    public void ClearIndex()
    {
        Docs.Clear();
        Extraction.EnqueueMaintenance(index =>
        {
            index.Clear();
            foreach (var f in Directory.EnumerateFiles(AppPaths.Thumbs)) WindowCapture.TryDelete(f);
        });
        Extraction.ResetState();
        Log.Info("Index cleared by user");
    }

    private void Schedule(TrackedWindow w, JobKind kind, int delayMs)
    {
        Extraction.Schedule(new ExtractionJob(
            w.Hwnd, w.Pid, w.ProcessName, w.Title, w.IsBrowser,
            Sensitive: IsSensitive(w),
            kind, DateTime.UtcNow.AddMilliseconds(delayMs)));
    }

    private void OnWindowAdded(TrackedWindow w)
    {
        if (w.IsBrowser) Schedule(w, JobKind.TabsOnly, 1000);
    }

    private void OnTitleChanged(TrackedWindow w)
    {
        // The tab scan is flipping this window's tabs; those aren't views by the user.
        if (Seed.IsSeeding(w.Hwnd)) return;
        // In front: tab switch or navigation, so capture and re-read. Behind: just refresh the tab list.
        if (Win32.GetForegroundWindow() == w.Hwnd) Schedule(w, JobKind.Foreground, _settings.CaptureDelayMs);
        else if (w.IsBrowser) Schedule(w, JobKind.TabsOnly, TabRefreshDelayMs);
    }

    private void OnExtractionResult(ExtractionResult r)
    {
        if (r.Doc is not null) Docs.Upsert(r.Doc);
        if (r.Tabs is null || !Tracker.TryGet(r.Hwnd, out var w)) return;
        ApplyTabs(w, r.Tabs);
    }

    private static void ApplyTabs(TrackedWindow w, IReadOnlyList<TabSnapshot> tabs) =>
        w.Tabs = tabs
            .Select((t, i) => new TrackedTab { Title = t.Title, RawName = t.RawName, Index = i, IsActive = t.IsActive })
            .ToList();

    /// <summary>
    /// Browser tabs with no saved text or no preview yet, grouped by window (minimized windows last).
    /// </summary>
    private List<SeedWindowPlan> BuildSeedPlan() =>
        Tracker.Windows
            .Where(w => w.IsBrowser && !IsSensitive(w) && w.Tabs.Count > 0)
            .OrderBy(w => w.IsMinimized)
            .ThenBy(w => w.ZOrder)
            .Select(w => new SeedWindowPlan(w.Hwnd, w.Pid, w.ProcessName, w.Tabs
                .Where(t => !IsBlankTab(t.Title) && !Seed.IsIgnored(w.Hwnd, t.Title) &&
                            Docs.FindTab(t.Title, w.Hwnd) is not { Thumb: not null })
                .Select(t => new SeedTab(t.Title, t.Index))
                .ToList()))
            .Where(p => p.Tabs.Count > 0)
            .ToList();

    private static bool IsBlankTab(string title) =>
        title.Length == 0 || title.Equals("New tab", StringComparison.OrdinalIgnoreCase) ||
        title.Equals("New InPrivate tab", StringComparison.OrdinalIgnoreCase);

    /// <summary>Tabs missing text or a preview, from the last known tab lists (cheap; for the launcher's status bar).</summary>
    public int CountUnscannedTabs() => BuildSeedPlan().Sum(p => p.Tabs.Count);

    /// <summary>Asks for confirmation, then switches through every unindexed Edge tab to index it.</summary>
    public async void StartSeed()
    {
        try
        {
            if (Seed.IsRunning)
            {
                _tray.ShowBalloon("Already scanning", $"{Seed.Done} of {Seed.Total} tabs done.");
                return;
            }
            if (_settings.IndexingPaused)
            {
                _tray.ShowBalloon("Indexing is paused", "Resume indexing from the tray menu to scan tabs.");
                return;
            }

            // Refresh tab lists first so the plan matches what's open right now.
            foreach (var w in Tracker.Windows.Where(w => w.IsBrowser && !IsSensitive(w)).ToList())
            {
                try
                {
                    var hwnd = w.Hwnd;
                    var caption = w.Title;
                    ApplyTabs(w, await _interactive.Run(r => r.ReadBrowser(hwnd, caption).Tabs));
                }
                catch (Exception ex)
                {
                    Log.Error("Could not read a browser tab strip", ex);
                }
            }

            var plan = BuildSeedPlan();
            var total = plan.Sum(p => p.Tabs.Count);
            if (total == 0)
            {
                _tray.ShowBalloon("Nothing to scan", "Every open Edge tab already has saved text and a preview.");
                return;
            }

            var parallel = Math.Min(plan.Count, Math.Clamp(_settings.SeedMaxWindows, 1, 8));
            var seconds = (int)Math.Ceiling(total * 0.8 / parallel) + 3;
            var duration = seconds < 90 ? $"{seconds} seconds" : $"{Math.Ceiling(seconds / 60.0)} minutes";
            var answer = System.Windows.Forms.MessageBox.Show(
                $"Scan {total} tab(s) in {plan.Count} Edge window(s) that have no saved text or preview yet?\n\n" +
                $"Edge will switch through these tabs for about {duration}, then return each window to the tab " +
                "you had open. Minimized windows are shown briefly (without taking focus) so their tabs get a " +
                "preview, then minimized again. Sleeping tabs wake up and use more memory until Edge puts them " +
                "back to sleep.\n\n" +
                "After you click OK, leave the keyboard and mouse alone: any input stops the scan and restores your tabs.",
                "Windows Window Search: scan tabs",
                System.Windows.Forms.MessageBoxButtons.OKCancel, System.Windows.Forms.MessageBoxIcon.Information);
            if (answer != System.Windows.Forms.DialogResult.OK) return;

            Seed.Start(plan);
            _tray.SetStatus($"Scanning tabs 0/{total}…");
        }
        catch (Exception ex)
        {
            Log.Error("Could not start the tab scan", ex);
        }
    }

    private void OnSeedCompleted(SeedSummary s)
    {
        _tray.SetStatus(null);
        var what = $"Indexed {s.Indexed} of {s.Total} tabs in {s.Elapsed.TotalSeconds:F0} s" +
                   (s.Failed > 0 ? $", {s.Failed} didn't load" : string.Empty) +
                   (s.Skipped > 0 ? $", {s.Skipped} skipped" : string.Empty) + ".";
        _tray.ShowBalloon(
            s.StopReason is null ? "Tab scan finished" : "Tab scan stopped",
            s.StopReason is null ? what : $"Stopped by {s.StopReason}. {what} Your tabs were restored.");
        // Pages may have changed their titles while reloading; refresh the tab lists.
        foreach (var hwnd in s.Windows)
            if (Tracker.TryGet(hwnd, out var w)) Schedule(w, JobKind.TabsOnly, 500);
    }

    /// <summary>Remember apps with slow UI Automation across restarts (settings are editable by the user).</summary>
    private void OnSlowApp(string process)
    {
        var s = _settings;
        if (s.IsOcrOnly(process)) return;
        // Swap in a new list rather than mutating: the worker thread may be reading the old one.
        s.OcrOnlyProcesses = [.. s.OcrOnlyProcesses, process];
        s.Save(AppPaths.Settings);
    }

    private static readonly TimeSpan StartupPassLimit = TimeSpan.FromMinutes(3);
    private StartupProgress? _startup;
    private DateTime _startupBegan;

    /// <summary>Raised on the UI thread as the startup pass progresses and once when it finishes.</summary>
    public event Action? StartupProgressChanged;

    /// <summary>Progress of the startup pass over already-open windows, or null once it's finished.</summary>
    public (int Done, int Total)? StartupStatus
    {
        get
        {
            if (_startup is not { IsFinished: false } s) return null;
            if (DateTime.UtcNow - _startupBegan > StartupPassLimit)
            {
                Log.Info($"Startup pass still going after {StartupPassLimit.TotalMinutes:F0} minutes ({s.Done} of {s.Total}); no longer showing its progress");
                s.Abandon();
                return null;
            }
            return (s.Done, s.Total);
        }
    }

    /// <summary>
    /// One-time pass at startup so windows opened before the app have text and (if visible) a preview.
    /// Browser tab lists come first: they take well under a second per window and make every tab (and the
    /// tab-scan link) show up in the search window within seconds. Each browser window's full read follows
    /// its tab list (OnJobDone); other windows are read one by one in the meantime.
    /// </summary>
    private void StartupSweep()
    {
        if (_settings.IndexingPaused) return;
        var windows = Tracker.Windows.OrderBy(w => w.ZOrder).ToList();
        if (windows.Count == 0) return;
        _startup = new StartupProgress(
            windows.Select(w => w.Hwnd.ToInt64()),
            windows.Where(w => w.IsBrowser).Select(w => w.Hwnd.ToInt64()));
        _startupBegan = DateTime.UtcNow;
        Extraction.JobDone += OnJobDone;

        var i = 0;
        foreach (var w in windows.Where(w => w.IsBrowser))
            Schedule(w, JobKind.TabsOnly, 300 + i++ * 50);
        var j = 0;
        foreach (var w in windows.Where(w => !w.IsBrowser))
            Schedule(w, JobKind.Sweep, 1500 + j++ * 200);
    }

    private void OnJobDone(IntPtr hwnd, JobKind kind)
    {
        if (_startup is not { } s || _startupEnded) return;
        if (s.JobDone(hwnd.ToInt64(), kind == JobKind.TabsOnly))
        {
            if (Tracker.TryGet(hwnd, out var w)) Schedule(w, JobKind.Sweep, 200);
            else s.Forget(hwnd.ToInt64());
        }
        AfterStartupProgress();
    }

    private bool _startupEnded;

    private void AfterStartupProgress()
    {
        if (_startup is { IsFinished: true } s && !_startupEnded)
        {
            _startupEnded = true;
            Extraction.JobDone -= OnJobDone;
            Log.Info($"Startup pass done: {s.Total} windows in {(DateTime.UtcNow - _startupBegan).TotalSeconds:F1}s");
        }
        StartupProgressChanged?.Invoke();
    }

    private void OnWindowsClosed(IReadOnlyList<TrackedWindow> closed)
    {
        // Window documents can't be reopened, so they go right away. Browser tab documents stay
        // (they can be found and reopened) until the retention purge.
        var keys = new List<string>();
        foreach (var w in closed)
        {
            _startup?.Forget(w.Hwnd.ToInt64());
            keys.Add(w.WindowDocKey);
            var tabPrefix = $"tab:{w.Hwnd}:";
            keys.AddRange(Docs.All.Where(d => d.Key.StartsWith(tabPrefix, StringComparison.Ordinal)).Select(d => d.Key));
        }
        DeleteDocs(keys);
        if (_startup is not null && !_startupEnded && closed.Count > 0) AfterStartupProgress();
    }

    private void RemoveDocsOfClosedWindows()
    {
        var liveWindowKeys = Tracker.Windows.Select(w => w.WindowDocKey).ToHashSet();
        var liveHwnds = Tracker.Windows.Select(w => w.Hwnd.ToInt64()).ToHashSet();
        var stale = Docs.All
            .Where(d => (d.Kind == DocKind.Window && !liveWindowKeys.Contains(d.Key)) ||
                        (d.Key.StartsWith("tab:", StringComparison.Ordinal) && !liveHwnds.Contains(d.Hwnd)))
            .Select(d => d.Key)
            .ToList();
        DeleteDocs(stale);
    }

    /// <summary>Deletes what's already saved for sites and apps that are excluded now.</summary>
    private void RemoveExcludedDocs()
    {
        var settings = _settings;
        var keys = Docs.All.Where(d => settings.IsUrlExcluded(d.Url)).Select(d => d.Key).ToList();
        keys.AddRange(Tracker.Windows.Where(w => settings.IsProcessExcluded(w.ProcessName)).Select(w => w.WindowDocKey));
        DeleteDocs(keys);
    }

    private void PurgeOldTabs()
    {
        var cutoff = DateTimeOffset.Now.AddDays(-Math.Max(1, _settings.RetentionDays));
        var liveTitles = Tracker.Windows.SelectMany(w => w.Tabs).Select(t => TitleCleaner.Key(t.Title)).ToHashSet();
        var old = Docs.All
            .Where(d => d.Kind == DocKind.Tab && d.LastSeen < cutoff && !liveTitles.Contains(TitleCleaner.Key(d.Title)))
            .Select(d => d.Key)
            .ToList();
        DeleteDocs(old);
        RemoveDocsOfClosedWindows();
    }

    private void DeleteDocs(List<string> keys)
    {
        if (keys.Count == 0) return;
        Docs.Remove(keys);
        Extraction.EnqueueMaintenance(index =>
        {
            foreach (var thumb in index.Delete(keys)) WindowCapture.TryDelete(thumb);
        });
    }

    public void ReloadSettings()
    {
        _settingsDebounce.Stop();
        var previousHotkey = _settings.Hotkey;
        _settings = AppSettings.Load(AppPaths.Settings);
        UiaReader.PageFreeBrowserReads = _settings.ReadTabsWithoutPages;
        RemoveExcludedDocs();
        if (_settings.Hotkey != previousHotkey && !_hotkey.Register(_settings.Hotkey))
        {
            _tray.ShowBalloon("Hotkey unavailable", $"{_settings.Hotkey} is invalid or already in use. Keeping {previousHotkey}.");
            _hotkey.Register(previousHotkey);
        }
        _tray.UpdateState();
        Log.Info("Settings reloaded");
    }

    public void Dispose()
    {
        Seed.Stop("app exit");
        _purgeTimer.Stop();
        _settingsWatcher.Dispose();
        _hotkey.Dispose();
        _tray.Dispose();
        Tracker.Dispose();
        Extraction.Dispose();
        _interactive.Dispose();
        Search.Dispose();
    }
}
