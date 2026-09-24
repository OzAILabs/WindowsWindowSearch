namespace WindowSearcher.App.Tracking;

internal sealed class TrackedTab
{
    public required string Title { get; init; }
    public required string RawName { get; init; }
    public required int Index { get; init; }
    public bool IsActive { get; set; }
}

/// <summary>Live state of one top-level window. Owned and mutated by the UI thread only.</summary>
internal sealed class TrackedWindow
{
    private static readonly HashSet<string> BrowserProcesses =
        new(StringComparer.OrdinalIgnoreCase) { "msedge", "chrome", "brave", "vivaldi", "opera" };

    public required IntPtr Hwnd { get; init; }
    public required int Pid { get; init; }
    public required string ProcessName { get; init; }
    public string? ExePath { get; init; }
    public required string ClassName { get; init; }
    public string Title { get; set; } = string.Empty;
    public int ZOrder { get; set; }
    public bool IsMinimized { get; set; }

    /// <summary>When the user last brought this window to the foreground (MinValue = not since startup).</summary>
    public DateTimeOffset LastFocused { get; set; } = DateTimeOffset.MinValue;

    /// <summary>Tabs from the browser tab strip; empty for non-browsers and browser app windows.</summary>
    public IReadOnlyList<TrackedTab> Tabs { get; set; } = [];

    public bool IsBrowser => ClassName == "Chrome_WidgetWin_1" && BrowserProcesses.Contains(ProcessName);

    public bool IsPrivate => IsBrowser && Core.TitleCleaner.IsPrivateBrowserCaption(Title);

    public string WindowDocKey => $"win:{Hwnd}:{Pid}";
}
