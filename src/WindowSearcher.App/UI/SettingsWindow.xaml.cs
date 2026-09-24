using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WindowSearcher.App.Services;
using WindowSearcher.Core;

namespace WindowSearcher.App.UI;

/// <summary>Edits settings.json through a form. Saving writes the file; the app picks it up immediately.</summary>
internal partial class SettingsWindow : Window
{
    private readonly AppCore _core;

    public SettingsWindow(AppCore core)
    {
        _core = core;
        InitializeComponent();
        Fill(core.Settings, Autostart.IsEnabled);
    }

    private void Fill(AppSettings s, bool startWithWindows)
    {
        HotkeyBox.Text = s.Hotkey;
        StartWithWindowsBox.IsChecked = startWithWindows;
        PausedBox.IsChecked = s.IndexingPaused;

        PrivateWindowsBox.IsChecked = s.IndexPrivateWindows;
        RetentionBox.Text = s.RetentionDays.ToString();
        ExcludedAppsBox.Text = Lines(s.ExcludedProcesses);
        ExcludedSitesBox.Text = Lines(s.ExcludedDomains);

        OcrBox.IsChecked = s.OcrEnabled;
        ReadTabsWithoutPagesBox.IsChecked = s.ReadTabsWithoutPages;
        ScreenshotOnlySitesBox.Text = Lines(s.ScreenshotOnlyDomains);
        OcrOnlyAppsBox.Text = Lines(s.OcrOnlyProcesses);

        SeedWindowsBox.Text = s.SeedMaxWindows.ToString();
        SeedLoadingBox.Text = s.SeedMaxLoadingTabs.ToString();
        SeedTimeoutBox.Text = (s.SeedTabTimeoutMs / 1000).ToString();
        SeedRestoreMinimizedBox.IsChecked = s.SeedRestoreMinimized;

        CaptureDelayBox.Text = s.CaptureDelayMs.ToString();
        ReindexBox.Text = s.ReindexAfterSeconds.ToString();
        RecaptureBox.Text = s.RecaptureAfterSeconds.ToString();
        MaxTextBox.Text = s.MaxTextChars.ToString();
        ErrorText.Text = string.Empty;
    }

    /// <summary>Builds settings from the form, or returns null and shows what's wrong.</summary>
    private AppSettings? Read()
    {
        var errors = new List<string>();
        int Number(TextBox box, string name, int min, int max)
        {
            if (int.TryParse(box.Text.Trim(), out var n) && n >= min && n <= max) return n;
            errors.Add($"{name}: {min}–{max}");
            return min;
        }

        var hotkey = HotkeyBox.Text.Trim();
        if (!HotkeyManager.TryParse(hotkey, out _, out _)) errors.Add("Shortcut needs a key plus Ctrl, Alt, Shift or Win");

        var s = new AppSettings
        {
            Hotkey = hotkey,
            IndexingPaused = PausedBox.IsChecked == true,
            IndexPrivateWindows = PrivateWindowsBox.IsChecked == true,
            RetentionDays = Number(RetentionBox, "Days to keep closed tabs", 1, 365),
            ExcludedProcesses = List(ExcludedAppsBox.Text, stripExe: true),
            ExcludedDomains = List(ExcludedSitesBox.Text),
            OcrEnabled = OcrBox.IsChecked == true,
            ReadTabsWithoutPages = ReadTabsWithoutPagesBox.IsChecked == true,
            ScreenshotOnlyDomains = List(ScreenshotOnlySitesBox.Text),
            OcrOnlyProcesses = List(OcrOnlyAppsBox.Text, stripExe: true),
            SeedMaxWindows = Number(SeedWindowsBox, "Edge windows at once", 1, 8),
            SeedMaxLoadingTabs = Number(SeedLoadingBox, "Tabs loading at once", 1, 32),
            SeedTabTimeoutMs = Number(SeedTimeoutBox, "Seconds per tab", 1, 60) * 1000,
            SeedRestoreMinimized = SeedRestoreMinimizedBox.IsChecked == true,
            CaptureDelayMs = Number(CaptureDelayBox, "Wait after switching (ms)", 0, 5000),
            ReindexAfterSeconds = Number(ReindexBox, "Re-read interval (s)", 10, 3600),
            RecaptureAfterSeconds = Number(RecaptureBox, "Preview refresh (s)", 5, 3600),
            MaxTextChars = Number(MaxTextBox, "Most text kept", 1000, 1_000_000),
        };

        ErrorText.Text = errors.Count == 0 ? string.Empty : "Check: " + string.Join("; ", errors);
        return errors.Count == 0 ? s : null;
    }

    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.Tab)
            return;

        var parts = new List<string>();
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows) || Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) parts.Add("Win");
        if (parts.Count == 0)
        {
            ErrorText.Text = "The shortcut needs Ctrl, Alt, Shift or Win plus a key.";
            return;
        }
        parts.Add(key.ToString());
        HotkeyBox.Text = string.Join('+', parts);
        ErrorText.Text = string.Empty;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var settings = Read();
        if (settings is null) return;
        try
        {
            settings.Save(AppPaths.Settings);
            Autostart.Set(StartWithWindowsBox.IsChecked == true);
            _core.ReloadSettings();
            Close();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorText.Text = $"Couldn't save: {ex.Message}";
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private void OnResetClick(object sender, RoutedEventArgs e) =>
        Fill(new AppSettings(), StartWithWindowsBox.IsChecked == true); // shown only; nothing changes until Save

    private void OnOpenFileClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { AppPaths.Settings }, UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Log.Error("Could not open the settings file", ex);
        }
    }

    private static string Lines(IEnumerable<string> items) => string.Join(Environment.NewLine, items);

    private static List<string> List(string text, bool stripExe = false) =>
        text.Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => stripExe && x.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? x[..^4] : x)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
