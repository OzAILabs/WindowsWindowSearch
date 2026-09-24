using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace WindowSearcher.App.Services;

/// <summary>Notification-area icon and menu.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly AppCore _core;
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ToolStripMenuItem _open;
    private readonly WinForms.ToolStripMenuItem _pause;
    private readonly WinForms.ToolStripMenuItem _autostart;
    private readonly WinForms.ToolStripMenuItem _scan;
    private readonly WinForms.ToolStripMenuItem _private;
    private string? _status;

    public TrayIcon(AppCore core)
    {
        _core = core;
        var menu = new WinForms.ContextMenuStrip();
        _open = new WinForms.ToolStripMenuItem("Open search", null, (_, _) => _core.Launcher.ShowLauncher());
        _open.Font = new Font(_open.Font, FontStyle.Bold);
        menu.Items.Add(_open);
        menu.Items.Add(new WinForms.ToolStripMenuItem("Open grid view", null, (_, _) => _core.Launcher.ShowLauncher(grid: true)));
        _scan = new WinForms.ToolStripMenuItem("Scan Edge tabs missing text or preview…", null, (_, _) =>
        {
            if (_core.Seed.IsRunning) _core.Seed.Stop("you (tray menu)");
            else _core.StartSeed();
        });
        menu.Items.Add(_scan);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        _pause = new WinForms.ToolStripMenuItem("Pause indexing", null, (_, _) => _core.TogglePause());
        _autostart = new WinForms.ToolStripMenuItem("Start with Windows", null, (_, _) =>
        {
            Autostart.Set(!Autostart.IsEnabled);
            UpdateState();
        });
        _private = new WinForms.ToolStripMenuItem("Include InPrivate windows", null, (_, _) => _core.TogglePrivateWindows())
        {
            ToolTipText = "Capture and index InPrivate windows too (for example a second sign-in). Keeps a local record of them.",
        };
        menu.Items.Add(_pause);
        menu.Items.Add(_private);
        menu.Items.Add(_autostart);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(new WinForms.ToolStripMenuItem("Settings…", null, (_, _) => _core.OpenSettings()));
        menu.Items.Add(new WinForms.ToolStripMenuItem("Open log", null, (_, _) => OpenFile("notepad.exe", AppPaths.Log)));
        menu.Items.Add(new WinForms.ToolStripMenuItem("Open data folder", null, (_, _) => OpenFile("explorer.exe", AppPaths.Root)));
        menu.Items.Add(new WinForms.ToolStripMenuItem("Clear index and previews…", null, (_, _) => ConfirmClear()));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(new WinForms.ToolStripMenuItem("Help", null, (_, _) => _core.OpenHelp()));
        menu.Items.Add(new WinForms.ToolStripMenuItem("About Windows Window Search", null, (_, _) => _core.OpenAbout()));
        menu.Items.Add(new WinForms.ToolStripMenuItem("Exit", null, (_, _) => System.Windows.Application.Current.Shutdown()));
        menu.Opening += (_, _) => UpdateState();

        _icon = new WinForms.NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "Windows Window Search",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) _core.Launcher.Toggle();
        };
        UpdateState();
    }

    public void UpdateState()
    {
        _open.Text = $"Open search\t{_core.Settings.Hotkey}";
        _pause.Checked = _core.Settings.IndexingPaused;
        _autostart.Checked = Autostart.IsEnabled;
        _private.Checked = _core.Settings.IndexPrivateWindows;
        _scan.Text = _core.Seed.IsRunning
            ? $"Stop tab scan ({_core.Seed.Done}/{_core.Seed.Total})"
            : "Scan Edge tabs missing text or preview…";
        _icon.Text = _status ?? (_core.Settings.IndexingPaused ? "Windows Window Search (indexing paused)" : "Windows Window Search");
    }

    /// <summary>Temporary tooltip status (e.g. scan progress); null restores the normal tooltip.</summary>
    public void SetStatus(string? status)
    {
        _status = status is null ? null : $"Windows Window Search: {status}";
        UpdateState();
    }

    public void ShowBalloon(string title, string text) =>
        _icon.ShowBalloonTip(5000, title, text, WinForms.ToolTipIcon.Info);

    private void ConfirmClear()
    {
        var answer = WinForms.MessageBox.Show(
            "Delete all indexed text and saved previews? Open windows will be re-indexed as you use them.",
            "Windows Window Search", WinForms.MessageBoxButtons.OKCancel, WinForms.MessageBoxIcon.Question);
        if (answer == WinForms.DialogResult.OK) _core.ClearIndex();
    }

    private static void OpenFile(string exe, string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(exe) { ArgumentList = { path }, UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Log.Error($"Could not open {path}", ex);
        }
    }

    /// <summary>Magnifier glyph on an accent-colored rounded square.</summary>
    private static Icon CreateIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var bg = new SolidBrush(Color.FromArgb(0, 103, 192));
            using var path = new GraphicsPath();
            path.AddArc(1, 1, 10, 10, 180, 90);
            path.AddArc(21, 1, 10, 10, 270, 90);
            path.AddArc(21, 21, 10, 10, 0, 90);
            path.AddArc(1, 21, 10, 10, 90, 90);
            path.CloseFigure();
            g.FillPath(bg, path);
            using var pen = new Pen(Color.White, 3.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawEllipse(pen, 7, 7, 13, 13);
            g.DrawLine(pen, 18.5f, 18.5f, 25, 25);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}

/// <summary>Per-user "run at sign-in" entry. Off unless the user turns it on from the tray menu.</summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WindowSearcher";

    private static string Command => $"\"{Environment.ProcessPath}\" --background";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string v && string.Equals(v, Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key.SetValue(ValueName, Command);
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Error("Could not change the start-with-Windows setting", ex);
        }
    }
}
