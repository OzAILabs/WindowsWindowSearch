using System.Diagnostics;
using System.Drawing;
using System.Windows.Threading;
using WindowSearcher.App.Capture;
using WindowSearcher.App.Native;
using WindowSearcher.App.Services;
using WindowSearcher.Core;

namespace WindowSearcher.App.Extraction;

internal enum JobKind
{
    /// <summary>User focused the window (or switched tabs). Screen capture + text; runs only if still in front.</summary>
    Foreground,

    /// <summary>One-time startup pass: PrintWindow capture + text, doesn't count as "viewed".</summary>
    Sweep,

    /// <summary>Browser tab list refresh only (background title change).</summary>
    TabsOnly,
}

internal sealed record ExtractionJob(
    IntPtr Hwnd,
    int Pid,
    string ProcessName,
    string Caption,
    bool IsBrowser,
    bool Sensitive,
    JobKind Kind,
    DateTime DueUtc);

internal sealed record ExtractionResult(
    IntPtr Hwnd,
    IReadOnlyList<TabSnapshot>? Tabs,
    DocSummary? Doc);

/// <summary>
/// Single background thread that does all capture, UI Automation and indexing work, so the UI
/// thread never waits on another app. Jobs are debounced per window.
/// </summary>
internal sealed class ExtractionService : IDisposable
{
    private sealed class WindowState
    {
        public string Signature = string.Empty;
        public DateTime LastText = DateTime.MinValue;
        public DateTime LastCapture = DateTime.MinValue;
        public int Timeouts;
    }

    private static readonly TimeSpan UiaTimeout = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan TextBudget = TimeSpan.FromMilliseconds(500);
    private const int UIA_E_TIMEOUT = unchecked((int)0x80131505);

    // Apps whose UI Automation tree is slow and yields little text (seen with classic Outlook/Word).
    // UIA work runs on the target app's UI thread, so after two strikes we switch that app to OCR only.
    private const long SlowTextMs = 800;
    private const long VerySlowTextMs = 2500;
    private readonly Dictionary<string, int> _slowStrikes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _ocrOnly = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _gate = new();
    private readonly Dictionary<IntPtr, ExtractionJob> _pending = new();
    private readonly Queue<Action<SearchIndex>> _maintenance = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly Dictionary<IntPtr, WindowState> _state = new();
    private readonly Func<AppSettings> _settings;
    private readonly Dispatcher _ui;
    private readonly Action<ExtractionResult> _onResult;
    private readonly Action<string> _onSlowApp;
    private readonly Thread _thread;
    private bool _stopping;

    public ExtractionService(Func<AppSettings> settings, Dispatcher ui, Action<ExtractionResult> onResult, Action<string> onSlowApp)
    {
        _settings = settings;
        _ui = ui;
        _onResult = onResult;
        _onSlowApp = onSlowApp;
        _thread = new Thread(Run) { IsBackground = true, Name = "Extraction", Priority = ThreadPriority.BelowNormal };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    /// <summary>A job finished (whatever it found, even nothing); raised on the UI thread.</summary>
    public event Action<IntPtr, JobKind>? JobDone;

    public void Schedule(ExtractionJob job)
    {
        lock (_gate)
        {
            if (_pending.TryGetValue(job.Hwnd, out var existing))
            {
                // Foreground work wins and restarts the debounce; otherwise keep the stronger job.
                if (job.Kind != JobKind.Foreground && existing.Kind != JobKind.TabsOnly)
                    job = existing with { Caption = job.Caption };
            }
            _pending[job.Hwnd] = job;
        }
        _signal.Set();
    }

    public void EnqueueMaintenance(Action<SearchIndex> action)
    {
        lock (_gate) _maintenance.Enqueue(action);
        _signal.Set();
    }

    /// <summary>Indexes a page read elsewhere (tab scan), keeping this thread the only index writer.</summary>
    public void IndexExternal(IntPtr hwnd, DocRecord doc) => EnqueueMaintenance(index =>
    {
        var result = index.Upsert(doc);
        WindowCapture.TryDelete(result.PreviousThumb);
        Post(new ExtractionResult(hwnd, null, result.Summary));
    });

    /// <summary>Forget per-window skip state (e.g. after clearing the index).</summary>
    public void ResetState() => EnqueueMaintenance(_ => _state.Clear());

    private void Run()
    {
        using var reader = new UiaReader(UiaTimeout);
        using var index = new SearchIndex(AppPaths.Database, SecureStore.DatabaseKey);
        var ocr = new OcrService();
        if (!ocr.IsAvailable) Log.Info("OCR unavailable: no OCR language installed for the user profile.");

        while (true)
        {
            Action<SearchIndex>? maintenance = null;
            ExtractionJob? job = null;
            var wait = Timeout.InfiniteTimeSpan;
            lock (_gate)
            {
                if (_stopping) return;
                if (_maintenance.Count > 0)
                {
                    maintenance = _maintenance.Dequeue();
                }
                else if (_pending.Count > 0)
                {
                    var next = _pending.Values.MinBy(j => j.DueUtc)!;
                    var now = DateTime.UtcNow;
                    if (next.DueUtc <= now)
                    {
                        job = next;
                        _pending.Remove(next.Hwnd);
                    }
                    else
                    {
                        wait = next.DueUtc - now;
                    }
                }
            }

            if (maintenance is not null)
            {
                try { maintenance(index); }
                catch (Exception ex) { Log.Error("Maintenance failed", ex); }
                continue;
            }
            if (job is null)
            {
                _signal.WaitOne(wait);
                continue;
            }

            try
            {
                Execute(job, reader, index, ocr);
            }
            catch (Exception ex)
            {
                Log.Error($"Extraction failed for {job.ProcessName}", ex);
                if (IsTimeout(ex)) StateFor(job.Hwnd).Timeouts++;
            }
            finally
            {
                // After the job's result (posted first), so listeners see its tabs and text.
                var done = job;
                _ui.BeginInvoke(() => JobDone?.Invoke(done.Hwnd, done.Kind));
            }
        }
    }

    private WindowState StateFor(IntPtr hwnd)
    {
        if (!_state.TryGetValue(hwnd, out var s)) _state[hwnd] = s = new WindowState();
        return s;
    }

    private void Execute(ExtractionJob job, UiaReader reader, SearchIndex index, OcrService ocr)
    {
        var hwnd = job.Hwnd;
        if (!Win32.IsWindow(hwnd))
        {
            _state.Remove(hwnd);
            return;
        }
        // A foreground job only matters if the user is still looking at that window.
        if (job.Kind == JobKind.Foreground && Win32.GetForegroundWindow() != hwnd) return;
        if (Win32.IsHungAppWindow(hwnd))
        {
            Log.Info($"Skipped hung window {job.ProcessName}");
            return;
        }

        var settings = _settings();
        if (settings.IndexingPaused) return; // paused: don't touch other apps at all
        var state = StateFor(hwnd);
        if (state.Timeouts >= 3) return; // keeps timing out: leave this app alone
        var sw = Stopwatch.StartNew();
        var nowUtc = DateTime.UtcNow;

        // 1. Identify what is showing (browser: tab strip + address bar).
        BrowserSnapshot? browser = null;
        if (job.IsBrowser) browser = reader.ReadBrowser(hwnd, job.Caption);
        var uiaMs = sw.ElapsedMilliseconds;

        if (job.Kind == JobKind.TabsOnly)
        {
            Post(new ExtractionResult(hwnd, browser?.Tabs, null));
            return;
        }

        string key, title;
        DocKind kind;
        string? url = null;
        if (browser is { Tabs.Count: > 0 })
        {
            title = browser.ActiveTitle ?? job.Caption;
            url = browser.Url;
            kind = DocKind.Tab;
            key = url is not null ? $"url:{url}" : $"tab:{hwnd}:{title}";
        }
        else
        {
            title = job.Caption;
            kind = DocKind.Window;
            key = $"win:{hwnd}:{job.Pid}";
        }

        // A tab whose address can't be read could be on an excluded site, so it waits until it can be.
        var unknownAddress = kind == DocKind.Tab && url is null;
        var sensitive = job.Sensitive || unknownAddress || settings.IsUrlExcluded(url);
        if (sensitive)
        {
            Post(new ExtractionResult(hwnd, browser?.Tabs, null));
            return;
        }

        var signature = $"{key}|{title}";
        var changed = signature != state.Signature;
        var wantText = job.Kind == JobKind.Sweep || changed ||
                       nowUtc - state.LastText > TimeSpan.FromSeconds(settings.ReindexAfterSeconds);
        // Screenshot-only sites get their text from OCR, so they need a fresh capture whenever text is due.
        var screenshotOnly = browser is not null && settings.IsScreenshotOnly(url);
        var wantCapture = job.Kind == JobKind.Sweep || changed || (screenshotOnly && wantText) ||
                          nowUtc - state.LastCapture > TimeSpan.FromSeconds(settings.RecaptureAfterSeconds);
        state.Signature = signature;

        // 2. Text through UI Automation (skipped for apps known to be slow; OCR covers them).
        string? text = null;
        if (wantText)
        {
            try
            {
                if (browser is not null)
                {
                    // Only here does the app reach into the page (Edge then enables accessibility for it).
                    var document = screenshotOnly ? null : reader.GetPageDocument(browser);
                    text = document is null ? string.Empty : reader.ReadDocumentText(document, settings.MaxTextChars, TextBudget);
                }
                else if (!_ocrOnly.Contains(job.ProcessName) && !settings.IsOcrOnly(job.ProcessName))
                    text = reader.ReadGenericText(hwnd, settings.MaxTextChars, TextBudget);
                else
                    text = string.Empty;
            }
            catch (Exception ex) when (IsTimeout(ex))
            {
                // Keep going: the screenshot and OCR can still describe the window.
                text = string.Empty;
                state.Timeouts++;
                AddSlowStrike(job.ProcessName, "timed out", severe: true);
            }
            state.LastText = nowUtc;
        }
        var textMs = sw.ElapsedMilliseconds - uiaMs;
        if (browser is null && textMs > SlowTextMs && (text?.Length ?? 0) < 500)
            AddSlowStrike(job.ProcessName, $"took {textMs}ms for {text?.Length ?? 0} chars", severe: textMs > VerySlowTextMs);

        // 3. Screenshot.
        Bitmap? bmp = null;
        string? thumb = null;
        long captureMs = 0, ocrMs = 0;
        try
        {
            if (wantCapture && !Win32.IsIconic(hwnd) && !Win32.IsCloaked(hwnd))
            {
                var t0 = sw.ElapsedMilliseconds;
                bmp = job.Kind == JobKind.Foreground
                    ? Win32.GetForegroundWindow() == hwnd ? WindowCapture.FromScreen(hwnd) : null
                    : WindowCapture.WithPrintWindow(hwnd);
                if (bmp is not null && WindowCapture.IsBlank(bmp))
                {
                    bmp.Dispose();
                    bmp = null;
                }
                if (bmp is not null)
                {
                    thumb = WindowCapture.SaveThumbnail(bmp, key);
                    state.LastCapture = nowUtc;
                }
                captureMs = sw.ElapsedMilliseconds - t0;
            }

            // 4. OCR fallback for apps that expose (almost) no text.
            if (wantText && settings.OcrEnabled && ocr.IsAvailable && bmp is not null && (text?.Length ?? 0) < 200)
            {
                var t0 = sw.ElapsedMilliseconds;
                var ocrText = ocr.Recognize(bmp);
                if (!string.IsNullOrWhiteSpace(ocrText))
                    text = Ranker.CleanText($"{text} {ocrText}", settings.MaxTextChars);
                ocrMs = sw.ElapsedMilliseconds - t0;
            }
        }
        finally
        {
            bmp?.Dispose();
        }

        // 5. Index. A null body/thumb keeps what is stored.
        var result = index.Upsert(new DocRecord(
            key, kind, hwnd.ToInt64(), job.Pid, job.ProcessName, title, url,
            wantText ? text ?? string.Empty : null,
            thumb,
            job.Kind == JobKind.Foreground ? DateTimeOffset.Now : null));
        WindowCapture.TryDelete(result.PreviousThumb);

        Log.Info($"{job.Kind} {job.ProcessName} total={sw.ElapsedMilliseconds}ms uia={uiaMs} text={textMs} " +
                 $"capture={captureMs} ocr={ocrMs} chars={(wantText ? text?.Length ?? 0 : -1)} " +
                 $"tabs={browser?.Tabs.Count ?? 0} reindexed={result.ContentChanged}");

        Post(new ExtractionResult(hwnd, browser?.Tabs, result.Summary));
    }

    private void AddSlowStrike(string process, string reason, bool severe)
    {
        var strikes = _slowStrikes[process] = _slowStrikes.GetValueOrDefault(process) + (severe ? 2 : 1);
        if (strikes < 2 || !_ocrOnly.Add(process)) return;
        Log.Info($"UI Automation is slow for {process} ({reason}); using screenshots + OCR only for this app.");
        _ui.BeginInvoke(_onSlowApp, process);
    }

    private static bool IsTimeout(Exception ex) =>
        ex is TimeoutException || ex.HResult == UIA_E_TIMEOUT || ex.InnerException?.HResult == UIA_E_TIMEOUT;

    private void Post(ExtractionResult result) => _ui.BeginInvoke(_onResult, result);

    public void Dispose()
    {
        lock (_gate) _stopping = true;
        _signal.Set();
        _thread.Join(TimeSpan.FromSeconds(2));
    }
}
