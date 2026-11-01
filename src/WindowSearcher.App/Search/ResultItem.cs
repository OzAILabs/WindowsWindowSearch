using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using WindowSearcher.App.UI;

namespace WindowSearcher.App.Search;

internal enum ResultKind
{
    Window,
    Tab,
    ClosedTab,
}

/// <summary>One row in the launcher: a window, a browser tab, or a recently closed tab.</summary>
internal sealed class ResultItem : INotifyPropertyChanged
{
    private const int GridThumbWidth = 320;
    private ImageSource? _thumbnail;
    private bool _thumbRequested;

    public required ResultKind Kind { get; init; }
    public IntPtr Hwnd { get; init; }
    public required string Title { get; init; }
    public required string ProcessName { get; init; }
    public string? ExePath { get; init; }
    public string? Url { get; init; }
    public string? DocKey { get; init; }
    public string? ThumbPath { get; init; }
    public DateTimeOffset LastSeen { get; init; } = DateTimeOffset.MinValue;
    public int ZOrder { get; init; }
    public int TabIndex { get; init; }
    public bool IsActiveTab { get; init; }
    public bool IsMinimized { get; init; }
    public string? Snippet { get; set; }
    public double Score { get; set; }
    public ImageSource? Icon { get; init; }

    public string AppName => FriendlyAppName(ProcessName);

    public bool HasSnippet => !string.IsNullOrEmpty(Snippet);

    /// <summary>Not open any more: what's shown is a saved copy, and Enter only opens the address again.</summary>
    public bool IsClosed => Kind == ResultKind.ClosedTab;

    public string Subtitle
    {
        get
        {
            var parts = new List<string>(4) { AppName };
            var host = Host(Url);
            if (host is not null) parts.Add(host);
            if (IsClosed) parts.Add("not open");
            else if (IsMinimized) parts.Add("minimized");
            parts.Add(Age());
            return string.Join("  ·  ", parts);
        }
    }

    /// <summary>Grid-sized preview, decoded off the UI thread on first use.</summary>
    public ImageSource? Thumbnail
    {
        get
        {
            if (_thumbnail is null && !_thumbRequested && ThumbPath is not null)
            {
                _thumbRequested = true;
                _thumbnail = ThumbnailLoader.TryGetCached(ThumbPath, GridThumbWidth);
                if (_thumbnail is null)
                {
                    var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                    ThumbnailLoader.LoadAsync(ThumbPath, GridThumbWidth).ContinueWith(t =>
                    {
                        if (t.Result is null) return;
                        dispatcher.BeginInvoke(() =>
                        {
                            _thumbnail = t.Result;
                            OnPropertyChanged(nameof(Thumbnail));
                        });
                    }, TaskScheduler.Default);
                }
            }
            return _thumbnail;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private string Age()
    {
        if (LastSeen == DateTimeOffset.MinValue || LastSeen.ToUnixTimeMilliseconds() <= 0)
            return Kind == ResultKind.Tab && DocKey is not null ? "scanned, not viewed" : "not viewed yet";
        var age = DateTimeOffset.Now - LastSeen;
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} min ago";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours} h ago";
        return $"{(int)age.TotalDays} d ago";
    }

    private static string? Host(string? url) =>
        url is not null && Uri.TryCreate(url, UriKind.Absolute, out var u) && !string.IsNullOrEmpty(u.Host)
        && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp)
            ? u.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? u.Host[4..] : u.Host
            : null;

    public static string FriendlyAppName(string process) => process.ToLowerInvariant() switch
    {
        "msedge" => "Edge",
        "chrome" => "Chrome",
        "outlook" or "olk" => "Outlook",
        "winword" => "Word",
        "excel" => "Excel",
        "powerpnt" => "PowerPoint",
        "onenote" => "OneNote",
        "ms-teams" or "teams" => "Teams",
        "code" => "VS Code",
        "explorer" => "File Explorer",
        "windowsterminal" => "Terminal",
        "notepad" => "Notepad",
        "acrord32" or "acrobat" => "Acrobat",
        _ => process,
    };
}
