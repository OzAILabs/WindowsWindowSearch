using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowSearcher.Core;

public sealed class AppSettings
{
    /// <summary>Global hotkey, e.g. "Ctrl+Alt+Space" or "Win+Shift+F".</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+Space";

    /// <summary>Process names (without .exe) that are listed by title only: no screenshots, no text.</summary>
    public List<string> ExcludedProcesses { get; set; } = ["KeePass", "KeePassXC", "1Password", "Bitwarden"];

    /// <summary>
    /// Apps read only through screenshots + OCR because their UI Automation is slow. The app adds
    /// entries here automatically when it detects a slow app; you can add or remove names too.
    /// </summary>
    public List<string> OcrOnlyProcesses { get; set; } = [];

    /// <summary>
    /// Also capture and index InPrivate/Incognito windows (e.g. when they're used to sign in with a
    /// second account). Off by default because it keeps a local record of private browsing.
    /// </summary>
    public bool IndexPrivateWindows { get; set; }

    /// <summary>
    /// Domains indexed from a screenshot + OCR instead of reading the page (subdomains included).
    /// Reading a page switches on Edge's accessibility support for it, and some pages react badly
    /// (admin portals's app list flickers at certain window sizes).
    /// </summary>
    public List<string> ScreenshotOnlyDomains { get; set; } = ["admin.example.com"];

    /// <summary>
    /// true (default, since 1.0.5): read Edge's tabs and address bar without touching web pages; a page
    /// is only read when its text is due. false: the 1.0.4 method, one search over the whole window.
    /// </summary>
    public bool ReadTabsWithoutPages { get; set; } = true;

    /// <summary>Domains whose pages are never captured or indexed (subdomains included).</summary>
    public List<string> ExcludedDomains { get; set; } = [];

    /// <summary>Days to keep text and previews of closed browser tabs (so they can be found and reopened).</summary>
    public int RetentionDays { get; set; } = 7;

    /// <summary>Run OCR on the screenshot when a window exposes too little text through UI Automation.</summary>
    public bool OcrEnabled { get; set; } = true;

    /// <summary>Upper bound of text stored per window or tab.</summary>
    public int MaxTextChars { get; set; } = 150_000;

    /// <summary>Delay after a focus or tab change before capturing, so quick Alt-Tabbing is skipped.</summary>
    public int CaptureDelayMs { get; set; } = 700;

    /// <summary>Re-read text of an unchanged window/tab at most this often.</summary>
    public int ReindexAfterSeconds { get; set; } = 120;

    /// <summary>Refresh the screenshot of an unchanged window/tab at most this often.</summary>
    public int RecaptureAfterSeconds { get; set; } = 20;

    public bool IndexingPaused { get; set; }

    /// <summary>"Scan unviewed tabs": Edge windows processed at the same time.</summary>
    public int SeedMaxWindows { get; set; } = 4;

    /// <summary>"Scan unviewed tabs": tabs woken ahead of time across all windows (limits Edge's memory spike).</summary>
    public int SeedMaxLoadingTabs { get; set; } = 8;

    /// <summary>"Scan unviewed tabs": how long to wait for one tab's page to finish loading.</summary>
    public int SeedTabTimeoutMs { get; set; } = 8000;

    /// <summary>
    /// "Scan unviewed tabs": briefly restore minimized browser windows (without taking focus) so their
    /// tabs get a preview, then minimize them again. Edge draws nothing while a window is minimized.
    /// </summary>
    public bool SeedRestoreMinimized { get; set; } = true;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions) ?? new AppSettings();
        }
        catch (JsonException)
        {
            // Fall through to defaults; the broken file is left for the user to fix.
            return new AppSettings();
        }
        var defaults = new AppSettings();
        defaults.Save(path);
        return defaults;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    public bool IsProcessExcluded(string processName) =>
        ExcludedProcesses.Any(p => string.Equals(p, processName, StringComparison.OrdinalIgnoreCase));

    public bool IsOcrOnly(string processName) =>
        OcrOnlyProcesses.Any(p => string.Equals(p, processName, StringComparison.OrdinalIgnoreCase));

    public bool IsUrlExcluded(string? url) => MatchesDomain(url, ExcludedDomains);

    public bool IsScreenshotOnly(string? url) => MatchesDomain(url, ScreenshotOnlyDomains);

    private static bool MatchesDomain(string? url, List<string> domains)
    {
        if (string.IsNullOrEmpty(url) || domains.Count == 0) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var host = uri.Host;
        return domains.Any(d =>
        {
            var domain = d.Trim().TrimStart('.');
            return domain.Length > 0 &&
                   (host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
                    host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));
        });
    }
}
