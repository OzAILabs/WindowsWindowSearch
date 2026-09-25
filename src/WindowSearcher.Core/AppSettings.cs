using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowSearcher.Core;

public sealed class AppSettings
{
    /// <summary>Settings format of this release. Older files are brought up to date once when loaded.</summary>
    public const int CurrentVersion = 2;

    /// <summary>Settings format the file was written with (missing in files from 1.0.12 and earlier).</summary>
    public int SettingsVersion { get; set; } = CurrentVersion;

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
    /// Domains indexed from a screenshot + OCR instead of reading the page (subdomains included), for
    /// sites that react badly when read: reading a page switches on Edge's accessibility support for it.
    /// Empty by default. Up to 1.0.12 it held admin.example.com, whose app list sometimes flickers,
    /// but that flicker also happens without this app, and reading the page indexes all of it.
    /// </summary>
    public List<string> ScreenshotOnlyDomains { get; set; } = [];

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
            {
                var json = File.ReadAllText(path);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
                if (settings.Migrate(FileVersion(json))) settings.TrySave(path);
                return settings;
            }
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

    private void TrySave(string path)
    {
        try
        {
            Save(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The update is applied in memory anyway and saved with the next change.
        }
    }

    /// <summary>The file's SettingsVersion; 1 when it has none (written by 1.0.12 or earlier).</summary>
    private static int FileVersion(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
        return doc.RootElement.ValueKind == JsonValueKind.Object &&
               doc.RootElement.TryGetProperty(nameof(SettingsVersion), out var v) &&
               v.TryGetInt32(out var version)
            ? version
            : 1;
    }

    /// <summary>Updates settings written by an older release. Returns true if the file should be rewritten.</summary>
    private bool Migrate(int fromVersion)
    {
        if (fromVersion >= CurrentVersion) return false;
        // 2 (1.0.13): admin portal pages are read instead of OCR'd. Only a list that still holds exactly the old
        // default is cleared; one the user edited is left as it is.
        if (fromVersion < 2 && ScreenshotOnlyDomains.Count == 1 &&
            string.Equals(ScreenshotOnlyDomains[0].Trim(), "admin.example.com", StringComparison.OrdinalIgnoreCase))
            ScreenshotOnlyDomains = [];
        SettingsVersion = CurrentVersion;
        return true;
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
