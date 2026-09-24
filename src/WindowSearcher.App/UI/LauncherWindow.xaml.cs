using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using WindowSearcher.App.Native;
using WindowSearcher.App.Search;

namespace WindowSearcher.App.UI;

internal partial class LauncherWindow : Window
{
    public const double TileWidth = 264;
    public const double TileImageHeight = 150;
    public const double TileTextWidth = 218;
    private const int PreviewDecodeWidth = 960;

    private readonly AppCore _core;
    private readonly DispatcherTimer _searchDebounce;
    private List<ResultItem> _results = [];
    private bool _gridMode;
    private bool _activating;
    private int _previewVersion;

    public LauncherWindow(AppCore core)
    {
        _core = core;
        InitializeComponent();
        _searchDebounce = new DispatcherTimer(TimeSpan.FromMilliseconds(60), DispatcherPriority.Input, (_, _) =>
        {
            _searchDebounce!.Stop();
            RunSearch();
        }, Dispatcher);
        _searchDebounce.Stop(); // this constructor starts the timer

        PreviewKeyDown += OnPreviewKeyDown;
        Deactivated += (_, _) =>
        {
            if (!_activating) HideLauncher();
        };
        SourceInitialized += (_, _) =>
        {
            // Rounded corners on Windows 11.
            var hwnd = new WindowInteropHelper(this).Handle;
            var round = 2;
            Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        };
    }

    public bool IsOpen => IsVisible;

    public void Toggle(bool grid = false)
    {
        if (IsVisible) HideLauncher();
        else ShowLauncher(grid);
    }

    public void ShowLauncher(bool grid = false, string? query = null)
    {
        var sw = Stopwatch.StartNew();
        _core.Tracker.Refresh();
        SetMode(grid);
        SearchBox.Text = query ?? string.Empty;
        SearchBox.CaretIndex = SearchBox.Text.Length;
        _searchDebounce.Stop();
        RunSearch();
        PositionOnActiveMonitor();
        Show();
        Activate();
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        Services.Log.Info($"Launcher shown in {sw.ElapsedMilliseconds}ms with {_results.Count} items");
    }

    public void HideLauncher()
    {
        if (!IsVisible) return;
        Hide();
        _results = [];
        ResultsList.ItemsSource = null;
        GridList.ItemsSource = null;
        PreviewImage.Source = null;
        ThumbnailLoader.Clear();
    }

    private void PositionOnActiveMonitor()
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        Win32.GetCursorPos(out var pt);
        var monitor = Win32.MonitorFromPoint(pt, Win32.MONITOR_DEFAULTTONEAREST);
        var info = new Win32.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFO>() };
        if (!Win32.GetMonitorInfo(monitor, ref info)) return;
        var work = info.rcWork;
        // Move onto the monitor first so WPF rescales for its DPI, then center using the real size.
        Win32.SetWindowPos(hwnd, IntPtr.Zero, work.Left, work.Top, 0, 0,
            Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
        Win32.GetWindowRect(hwnd, out var r);
        var x = work.Left + (work.Width - r.Width) / 2;
        var y = work.Top + Math.Max(0, (work.Height - r.Height) / 3);
        Win32.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void RunSearch()
    {
        var sw = Stopwatch.StartNew();
        var query = SearchBox.Text.Trim();
        _results = query.Length == 0 ? _core.Search.Recent() : _core.Search.Search(query);
        ResultsList.ItemsSource = _results;
        GridList.ItemsSource = _gridMode ? _results : null;
        if (_results.Count > 0) Select(0);
        else ShowPreview(null);

        var windows = _core.Tracker.Windows.Count;
        var tabs = _core.Tracker.Windows.Sum(w => w.Tabs.Count);
        var paused = _core.Settings.IndexingPaused ? "   ·   indexing paused" : string.Empty;
        StatusText.Text = query.Length == 0
            ? $"{windows} windows  ·  {tabs} tabs  ·  {_core.Search.IndexedCount()} indexed{paused}"
            : $"{_results.Count} results in {sw.ElapsedMilliseconds} ms{paused}";
        UpdateSeedButton(query.Length == 0);
    }

    private void UpdateSeedButton(bool idleView)
    {
        if (_core.Seed.IsRunning)
        {
            SeedButton.Content = $"Scanning tabs {_core.Seed.Done}/{_core.Seed.Total}… (click to stop)";
            SeedButton.Visibility = Visibility.Visible;
            return;
        }
        var unscanned = idleView && !_core.Settings.IndexingPaused ? _core.CountUnscannedTabs() : 0;
        SeedButton.Content = $"Scan {unscanned} tab{(unscanned == 1 ? "" : "s")} missing text or preview";
        SeedButton.Visibility = unscanned > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSeedClick(object sender, RoutedEventArgs e)
    {
        if (_core.Seed.IsRunning)
        {
            _core.Seed.Stop("you (launcher)");
            UpdateSeedButton(idleView: true);
            return;
        }
        HideLauncher();
        _core.StartSeed();
    }

    private void SetMode(bool grid)
    {
        _gridMode = grid;
        ListPanel.Visibility = grid ? Visibility.Collapsed : Visibility.Visible;
        GridList.Visibility = grid ? Visibility.Visible : Visibility.Collapsed;
        GridList.ItemsSource = grid ? _results : null;
        ModeLabel.Text = grid ? "Grid view" : "List view";
        if (grid && ResultsList.SelectedIndex >= 0) Select(ResultsList.SelectedIndex);
    }

    private int SelectedIndex => ResultsList.SelectedIndex;

    private void Select(int index)
    {
        if (_results.Count == 0) return;
        index = Math.Clamp(index, 0, _results.Count - 1);
        ResultsList.SelectedIndex = index;
        ResultsList.ScrollIntoView(_results[index]);
        if (_gridMode)
        {
            GridList.SelectedIndex = index;
            GridList.ScrollIntoView(_results[index]);
        }
    }

    private int GridColumns()
    {
        var usable = GridList.ActualWidth - GridList.Padding.Left - GridList.Padding.Right - SystemParameters.VerticalScrollBarWidth;
        return Math.Max(1, (int)(usable / (TileWidth + 12)));
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var step = _gridMode ? GridColumns() : 1;
        switch (e.Key)
        {
            case Key.Escape:
                HideLauncher();
                e.Handled = true;
                break;
            case Key.Enter:
                _ = ActivateAsync(SelectedIndex >= 0 && SelectedIndex < _results.Count ? _results[SelectedIndex] : null);
                e.Handled = true;
                break;
            case Key.Tab:
                SetMode(!_gridMode);
                e.Handled = true;
                break;
            case Key.Down:
                Select(SelectedIndex + step);
                e.Handled = true;
                break;
            case Key.Up:
                Select(SelectedIndex - step);
                e.Handled = true;
                break;
            case Key.Right when _gridMode:
                Select(SelectedIndex + 1);
                e.Handled = true;
                break;
            case Key.Left when _gridMode:
                Select(SelectedIndex - 1);
                e.Handled = true;
                break;
            case Key.PageDown:
                Select(SelectedIndex + step * 8);
                e.Handled = true;
                break;
            case Key.PageUp:
                Select(SelectedIndex - step * 8);
                e.Handled = true;
                break;
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ShowPreview(ResultsList.SelectedItem as ResultItem);

    private void ShowPreview(ResultItem? item)
    {
        var version = ++_previewVersion;
        PreviewImage.Source = null;
        PreviewTitle.Text = item?.Title ?? string.Empty;
        PreviewUrl.Text = item?.Url ?? item?.Subtitle ?? string.Empty;
        Highlight.SetText(PreviewSnippet, item?.Snippet);
        PreviewIcon.Source = item?.Icon;
        PreviewEmpty.Visibility = item is null ? Visibility.Collapsed : Visibility.Visible;
        if (item?.ThumbPath is null) return;

        var cached = ThumbnailLoader.TryGetCached(item.ThumbPath, PreviewDecodeWidth);
        if (cached is not null)
        {
            PreviewImage.Source = cached;
            PreviewEmpty.Visibility = Visibility.Collapsed;
            return;
        }
        ThumbnailLoader.LoadAsync(item.ThumbPath, PreviewDecodeWidth).ContinueWith(t =>
        {
            if (t.Result is null) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (version != _previewVersion) return;
                PreviewImage.Source = t.Result;
                PreviewEmpty.Visibility = Visibility.Collapsed;
            });
        }, TaskScheduler.Default);
    }

    private void OnItemActivated(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list) return;
        if (ItemsControl.ContainerFromElement(list, (DependencyObject)e.OriginalSource) is not ListBoxItem container) return;
        if (container.DataContext is ResultItem item) _ = ActivateAsync(item);
    }

    private async Task ActivateAsync(ResultItem? item)
    {
        if (item is null || _activating) return;
        _activating = true;
        try
        {
            await _core.ActivateAsync(item);
        }
        catch (Exception ex)
        {
            Services.Log.Error("Activation failed", ex);
        }
        finally
        {
            _activating = false;
            HideLauncher();
        }
    }
}
