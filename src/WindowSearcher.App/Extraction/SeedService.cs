using System.Diagnostics;
using System.Windows.Threading;
using WindowSearcher.App.Capture;
using WindowSearcher.App.Native;
using WindowSearcher.App.Search;
using WindowSearcher.App.Services;
using WindowSearcher.Core;

namespace WindowSearcher.App.Extraction;

internal sealed record SeedTab(string Title, int Index);

internal sealed record SeedWindowPlan(IntPtr Hwnd, int Pid, string ProcessName, IReadOnlyList<SeedTab> Tabs);

internal sealed record SeedSummary(
    int Indexed, int Failed, int Skipped, int Total, TimeSpan Elapsed, string? StopReason,
    IReadOnlyList<IntPtr> Windows);

/// <summary>
/// "Scan unviewed tabs": Edge only exposes the active tab's page, so this switches through the
/// unindexed tabs of each window, reads and captures each one, then restores the original tab.
/// Windows run in parallel (each on its own thread and UI Automation connection). Within a window
/// the next few tabs are woken ahead of time so their reloads overlap. Any keyboard or mouse input
/// stops the scan.
/// </summary>
internal sealed class SeedService
{
    private const int WakeDwellMs = 150;
    private const int PollMs = 300;
    private const int InputGraceMs = 1500;
    private const int RestoreSettleMs = 600;
    private const int CaptureSettleMs = 250;
    private const int ScreenshotOnlyLoadMs = 2500;
    private static readonly TimeSpan ReadBudget = TimeSpan.FromMilliseconds(800);

    private enum Outcome
    {
        Indexed,
        Failed,
        Skipped,
    }

    private readonly ExtractionService _extraction;
    private readonly Func<AppSettings> _settings;
    private readonly Dispatcher _ui;
    private readonly HashSet<IntPtr> _seeding = new(); // UI thread only
    private readonly HashSet<(IntPtr, string)> _ignored = new(); // UI thread only
    private readonly System.Collections.Concurrent.ConcurrentQueue<(IntPtr, string)> _unindexable = new();
    private CancellationTokenSource? _cts;
    private Timer? _inputWatch;
    private volatile string? _stopReason;
    private int _done;
    private int _total;

    public SeedService(ExtractionService extraction, Func<AppSettings> settings, Dispatcher ui)
    {
        _extraction = extraction;
        _settings = settings;
        _ui = ui;
    }

    /// <summary>Raised on the UI thread after each tab.</summary>
    public event Action? ProgressChanged;

    /// <summary>Raised on the UI thread when the scan ends (finished or stopped).</summary>
    public event Action<SeedSummary>? Completed;

    public bool IsRunning => _cts is not null;

    public int Done => Volatile.Read(ref _done);

    public int Total => _total;

    /// <summary>True while this window's tabs are being switched by the scan (UI thread).</summary>
    public bool IsSeeding(IntPtr hwnd) => _seeding.Contains(hwnd);

    /// <summary>
    /// Tabs an earlier scan found can't be indexed (edge:// pages, excluded domains). Left out of later
    /// scans so they don't keep the "Scan N tabs" count above zero (UI thread).
    /// </summary>
    public bool IsIgnored(IntPtr hwnd, string title) => _ignored.Contains((hwnd, TitleCleaner.Key(title)));

    public void Start(IReadOnlyList<SeedWindowPlan> plan)
    {
        if (IsRunning || plan.Count == 0) return;
        var settings = _settings();
        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;
        _stopReason = null;
        _done = 0;
        _total = plan.Sum(p => p.Tabs.Count);
        foreach (var p in plan) _seeding.Add(p.Hwnd);

        var parallel = Math.Clamp(settings.SeedMaxWindows, 1, 8);
        var maxLoading = Math.Max(1, settings.SeedMaxLoadingTabs);
        var lookahead = Math.Clamp(maxLoading / Math.Min(parallel, plan.Count), 1, 4);
        var windowGate = new SemaphoreSlim(parallel);
        var loadGate = new SemaphoreSlim(maxLoading);
        var sw = Stopwatch.StartNew();
        Log.Info($"Tab scan started: {_total} tabs in {plan.Count} windows, {parallel} windows at a time, " +
                 $"{lookahead} tabs woken ahead per window, at most {maxLoading} loading");

        // Stop on any keyboard/mouse input, after a short grace period for the click that started it.
        uint? baseline = null;
        _inputWatch = new Timer(_ =>
        {
            var tick = Win32.LastInputTick();
            if (baseline is null) baseline = tick;
            else if (tick != baseline) Stop("keyboard or mouse input");
        }, null, InputGraceMs, 250);

        var tasks = plan.Select(p => Task.Factory.StartNew(() =>
        {
            try
            {
                windowGate.Wait(ct);
            }
            catch (OperationCanceledException)
            {
                return (Indexed: 0, Failed: 0, Skipped: p.Tabs.Count);
            }
            try
            {
                return RunWindow(p, loadGate, lookahead, settings, ct);
            }
            finally
            {
                windowGate.Release();
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        Task.WhenAll(tasks).ContinueWith(_ =>
        {
            var results = tasks.Where(x => x.IsCompletedSuccessfully).Select(x => x.Result).ToList();
            var summary = new SeedSummary(
                results.Sum(r => r.Indexed), results.Sum(r => r.Failed), results.Sum(r => r.Skipped),
                _total, sw.Elapsed, _stopReason, plan.Select(p => p.Hwnd).ToList());
            _ui.BeginInvoke(() => Finish(cts, summary));
        }, TaskScheduler.Default);
    }

    public void Stop(string reason)
    {
        var cts = _cts;
        if (cts is null || cts.IsCancellationRequested) return;
        _stopReason ??= reason;
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void Finish(CancellationTokenSource cts, SeedSummary summary)
    {
        _inputWatch?.Dispose();
        _inputWatch = null;
        _seeding.Clear();
        while (_unindexable.TryDequeue(out var tab)) _ignored.Add(tab);
        _cts = null;
        cts.Dispose();
        Log.Info($"Tab scan {(summary.StopReason is null ? "finished" : $"stopped ({summary.StopReason})")}: " +
                 $"indexed={summary.Indexed} failed={summary.Failed} skipped={summary.Skipped} of {summary.Total} " +
                 $"in {summary.Elapsed.TotalSeconds:F1}s");
        Completed?.Invoke(summary);
    }

    private (int Indexed, int Failed, int Skipped) RunWindow(
        SeedWindowPlan plan, SemaphoreSlim loadGate, int lookahead, AppSettings settings, CancellationToken ct)
    {
        var hwnd = plan.Hwnd;
        var tabs = plan.Tabs;
        var n = tabs.Count;
        var held = new bool[n]; // tabs woken ahead that still hold a loading slot
        int indexed = 0, failed = 0, skipped = 0;
        string? originalTitle = null;
        var originalIndex = -1;
        var sw = Stopwatch.StartNew();
        Win32.WINDOWPLACEMENT? minimizedPlacement = null;

        using var reader = new UiaReader(TimeSpan.FromMilliseconds(2000));
        var ocr = new OcrService();
        try
        {
            // Edge draws nothing while minimized, so show the window (without focus) to get previews.
            if (settings.SeedRestoreMinimized && Win32.IsIconic(hwnd) && !ct.IsCancellationRequested)
            {
                var placement = new Win32.WINDOWPLACEMENT { length = System.Runtime.InteropServices.Marshal.SizeOf<Win32.WINDOWPLACEMENT>() };
                if (Win32.GetWindowPlacement(hwnd, ref placement))
                {
                    minimizedPlacement = placement;
                    var show = placement;
                    show.showCmd = Win32.SW_SHOWNOACTIVATE;
                    Win32.SetWindowPlacement(hwnd, ref show);
                    ct.WaitHandle.WaitOne(RestoreSettleMs);
                }
            }

            var start = reader.ReadBrowser(hwnd, Win32.GetWindowText(hwnd));
            originalIndex = IndexOfActive(start.Tabs);
            originalTitle = originalIndex >= 0 ? start.Tabs[originalIndex].Title : null;
            // One page and no reachable tab strip: nothing to switch, just read what's showing.
            var singlePage = start.Tabs.Count <= 1;

            var nextWake = 1;
            for (var i = 0; i < n; i++)
            {
                if (ct.IsCancellationRequested || !Win32.IsWindow(hwnd)) break;
                var tab = tabs[i];

                // Start this tab loading, then wake the next few so their reloads overlap with this one.
                var selected = reader.SelectTab(hwnd, tab.Title, tab.Index);
                if (nextWake <= i) nextWake = i + 1;
                var wokeAny = false;
                while (nextWake < n && nextWake <= i + lookahead && !ct.IsCancellationRequested && loadGate.Wait(0))
                {
                    held[nextWake] = true;
                    if (reader.SelectTab(hwnd, tabs[nextWake].Title, tabs[nextWake].Index) >= 0)
                    {
                        wokeAny = true;
                        ct.WaitHandle.WaitOne(WakeDwellMs);
                    }
                    nextWake++;
                }
                if (wokeAny && !ct.IsCancellationRequested) selected = reader.SelectTab(hwnd, tab.Title, tab.Index);

                Outcome outcome;
                if (selected >= 0)
                {
                    outcome = ReadTab(reader, ocr, plan, selected, tab, settings, ct);
                }
                else if (singlePage)
                {
                    outcome = ReadTab(reader, ocr, plan, -1, tab, settings, ct);
                }
                else
                {
                    Log.Info($"Tab scan: tab {tab.Index} of window {hwnd} not found in the tab strip (closed or renamed)");
                    outcome = Outcome.Failed;
                }
                if (held[i])
                {
                    held[i] = false;
                    loadGate.Release();
                }
                if (ct.IsCancellationRequested && outcome == Outcome.Failed) break; // interrupted, not a real failure
                switch (outcome)
                {
                    case Outcome.Indexed: indexed++; break;
                    case Outcome.Failed: failed++; break;
                    default:
                        skipped++;
                        _unindexable.Enqueue((hwnd, TitleCleaner.Key(tab.Title)));
                        break;
                }
                Interlocked.Increment(ref _done);
                _ui.BeginInvoke(() => ProgressChanged?.Invoke());

                // Typically a minimized window whose pages never render: don't burn 8 s per tab.
                if (indexed == 0 && failed >= 2)
                {
                    Log.Info($"Tab scan: no page content in window {hwnd} (minimized?), skipping its remaining tabs");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Tab scan failed for window {hwnd}", ex);
        }
        finally
        {
            for (var j = 0; j < n; j++)
                if (held[j]) loadGate.Release();
            if (originalTitle is not null && Win32.IsWindow(hwnd))
            {
                try { reader.SelectTab(hwnd, originalTitle, originalIndex); }
                catch (Exception ex) { Log.Error($"Could not restore the original tab in window {hwnd}", ex); }
            }
            if (minimizedPlacement is { } saved && Win32.IsWindow(hwnd))
            {
                saved.showCmd = Win32.SW_SHOWMINNOACTIVE;
                Win32.SetWindowPlacement(hwnd, ref saved);
            }
        }

        var notDone = n - indexed - failed - skipped;
        Log.Info($"Tab scan window {hwnd}: indexed={indexed} failed={failed} skipped={skipped + notDone} " +
                 $"in {sw.Elapsed.TotalSeconds:F1}s ({(indexed + failed > 0 ? sw.ElapsedMilliseconds / (indexed + failed) : 0)}ms/tab)");
        return (indexed, failed, skipped + notDone);
    }

    /// <summary>
    /// Waits until the selected tab's page stops changing, then reads, captures and indexes it.
    /// <paramref name="tabIndex"/> -1 reads whatever page is showing (single-page window).
    /// </summary>
    private Outcome ReadTab(UiaReader reader, OcrService ocr, SeedWindowPlan plan, int tabIndex, SeedTab tab, AppSettings settings,
        CancellationToken ct)
    {
        var hwnd = plan.Hwnd;
        var sw = Stopwatch.StartNew();
        try
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(1000, settings.SeedTabTimeoutMs));
            BrowserSnapshot? ready = null, lastGood = null;
            FlaUI.Core.AutomationElements.AutomationElement? readyDoc = null, lastGoodDoc = null;
            var lastLength = -1;
            var screenshotOnly = false;
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                var snap = reader.ReadBrowser(hwnd, Win32.GetWindowText(hwnd));
                if (tabIndex < 0 || IndexOfActive(snap.Tabs) == tabIndex)
                {
                    // Screenshot-only sites: never reach into the page (see AppSettings.ScreenshotOnlyDomains).
                    if (settings.IsScreenshotOnly(snap.Url))
                    {
                        ready = snap;
                        screenshotOnly = true;
                        break;
                    }
                    var doc = reader.GetPageDocument(snap);
                    var length = doc is null ? 0 : reader.PeekTextLength(doc);
                    if (length > 0)
                    {
                        lastGood = snap;
                        lastGoodDoc = doc;
                        // Loaded when the text stops growing (small changes allowed for live counters/clocks).
                        if (lastLength > 0 && Math.Abs(length - lastLength) <= Math.Max(20, length / 50))
                        {
                            ready = snap;
                            readyDoc = doc;
                            break;
                        }
                    }
                    lastLength = length;
                }
                ct.WaitHandle.WaitOne(PollMs);
            }
            if (ready is null)
            {
                ready = lastGood;
                readyDoc = lastGoodDoc;
            }
            if (ready is null || (!screenshotOnly && readyDoc is null) || ct.IsCancellationRequested) return Outcome.Failed;

            var url = ready.Url;
            if (url is not null && !SearchService.IsWebUrl(url)) return Outcome.Skipped; // edge://, new tab, etc.
            // An unreadable address could be on an excluded site.
            if (url is null || settings.IsUrlExcluded(url)) return Outcome.Skipped;

            var title = ready.ActiveTitle ?? tab.Title;
            var key = $"url:{url}";
            var text = string.Empty;
            if (screenshotOnly)
                ct.WaitHandle.WaitOne(ScreenshotOnlyLoadMs); // no way to watch the page load without reading it
            else
                text = reader.ReadDocumentText(readyDoc!, settings.MaxTextChars, ReadBudget);

            string? thumb = null;
            for (var attempt = 0; attempt < 2 && thumb is null && !Win32.IsIconic(hwnd) && !ct.IsCancellationRequested; attempt++)
            {
                ct.WaitHandle.WaitOne(attempt == 0 ? CaptureSettleMs : 2 * CaptureSettleMs);
                using var bmp = WindowCapture.WithPrintWindow(hwnd);
                if (bmp is null || WindowCapture.IsBlank(bmp)) continue;
                thumb = WindowCapture.SaveThumbnail(bmp, key);
                if (screenshotOnly && settings.OcrEnabled && ocr.IsAvailable)
                    text = Ranker.CleanText(ocr.Recognize(bmp), settings.MaxTextChars);
            }
            if (screenshotOnly && thumb is null) return Outcome.Failed;

            _extraction.IndexExternal(hwnd, new DocRecord(
                key, DocKind.Tab, hwnd.ToInt64(), plan.Pid, plan.ProcessName, title, url, text, thumb, ViewedAt: null));
            Log.Info($"Tab scan: tab {tabIndex} of window {hwnd} in {sw.ElapsedMilliseconds}ms, chars={text.Length}, " +
                     $"preview={thumb is not null}{(screenshotOnly ? ", screenshot+OCR" : string.Empty)}");
            return Outcome.Indexed;
        }
        catch (Exception ex)
        {
            Log.Error($"Tab scan: tab {tabIndex} of window {hwnd} failed", ex);
            return Outcome.Failed;
        }
    }

    private static int IndexOfActive(IReadOnlyList<TabSnapshot> tabs)
    {
        for (var i = 0; i < tabs.Count; i++)
            if (tabs[i].IsActive) return i;
        return -1;
    }
}
