using System.Text.RegularExpressions;

namespace WindowSearcher.Core;

/// <summary>
/// Normalizes browser window and tab titles so the same page matches across the
/// tab strip, the window caption and the index.
/// </summary>
public static partial class TitleCleaner
{
    // Status suffixes Edge/Chrome append to tab accessibility names,
    // e.g. "Log in | Helpdesk - Sleeping - Memory usage - 131 MB".
    [GeneratedRegex(
        @"\s-\s(?:Sleeping|Memory usage\s-\s[\d.,]+\s*[KMGT]?B|High memory usage(?:\s-\s[\d.,]+\s*[KMGT]?B)?|Audio playing|Playing audio|Muted|Tab muted|" +
        @"Network error|Crashed|Pinned|Pinned tab|Loading|Using (?:your )?(?:camera|microphone|location)|" +
        @"Recording|Sharing (?:your )?screen|Paused|Blocked|Snoozed|Frozen|Discarded)\s*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex TabStatusSuffix();

    // "<page> and 5 more pages - <profile> - Microsoft​ Edge" (Edge puts a zero-width space in its name).
    [GeneratedRegex(
        @"^(?<t>.*?)(?:\sand\s\d+\smore\spages?)?(?:\s-\s(?<p>[^-]*?))?\s-\s(?:Microsoft​?\s?Edge|Google Chrome|Brave|Vivaldi|Opera)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex BrowserWindowTitle();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>Strips tab status suffixes such as " - Sleeping - Memory usage - 131 MB".</summary>
    public static string CleanTabTitle(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var s = raw.Replace('​', ' ').Trim();
        while (true)
        {
            var next = TabStatusSuffix().Replace(s, string.Empty);
            if (next == s) break;
            s = next;
        }
        return Whitespace().Replace(s, " ").Trim();
    }

    /// <summary>Returns the active page title from a browser window caption, or the caption itself.</summary>
    public static string CleanBrowserWindowTitle(string? caption)
    {
        if (string.IsNullOrWhiteSpace(caption)) return string.Empty;
        var m = BrowserWindowTitle().Match(caption.Trim());
        var title = m.Success ? m.Groups["t"].Value : caption;
        return CleanTabTitle(title);
    }

    /// <summary>True when an Edge/Chrome caption marks a private window.</summary>
    public static bool IsPrivateBrowserCaption(string? caption) =>
        caption is not null &&
        (caption.Contains("InPrivate", StringComparison.OrdinalIgnoreCase) ||
         caption.Contains("(Incognito)", StringComparison.OrdinalIgnoreCase));

    // Unread counters sites put in front of the title: "(31) YouTube", "(2) Inbox", "(99+) Feed".
    [GeneratedRegex(@"^\(\d+\+?\)\s*")]
    private static partial Regex CountPrefix();

    /// <summary>
    /// Same page? Ignores case, whitespace and a leading unread counter, which changes while a
    /// page is open ("(30) YouTube" matches "(31) YouTube").
    /// </summary>
    public static bool TitlesMatch(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Key for dictionary lookups by title, with the same rules as <see cref="TitlesMatch"/>.</summary>
    public static string Key(string title) => Normalize(title).ToLowerInvariant();

    private static string Normalize(string s)
    {
        var t = Whitespace().Replace(s.Replace('​', ' '), " ").Trim();
        var stripped = CountPrefix().Replace(t, string.Empty);
        return stripped.Length > 0 ? stripped : t;
    }
}
