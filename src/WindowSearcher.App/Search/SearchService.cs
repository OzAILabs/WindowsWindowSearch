using WindowSearcher.App.Tracking;
using WindowSearcher.App.UI;
using WindowSearcher.Core;

namespace WindowSearcher.App.Search;

/// <summary>
/// Builds launcher results on the UI thread: live windows and tabs from the tracker, merged with
/// full-text hits from the index. Typical cost is a few milliseconds.
/// </summary>
internal sealed class SearchService : IDisposable
{
    private const int MaxResults = 150;
    private static readonly TimeSpan NeverSeenAge = TimeSpan.FromDays(30);

    private readonly WindowTracker _tracker;
    private readonly DocCache _docs;
    private readonly SearchIndex _index;
    private readonly Func<AppSettings> _settings;

    public SearchService(WindowTracker tracker, DocCache docs, string dbPath, Func<AppSettings> settings)
    {
        _tracker = tracker;
        _docs = docs;
        _settings = settings;
        _index = new SearchIndex(dbPath, Services.SecureStore.DatabaseKey);
    }

    /// <summary>Every open window and tab, most recently used first.</summary>
    public List<ResultItem> Recent() =>
        BuildLiveItems()
            .OrderByDescending(i => i.LastSeen)
            .ThenBy(i => i.ZOrder)
            .ThenBy(i => i.TabIndex)
            .ToList();

    public List<ResultItem> Search(string query)
    {
        var live = BuildLiveItems();
        var now = DateTimeOffset.Now;

        var liveByTitle = live.Where(i => i.Kind == ResultKind.Tab)
            .ToLookup(i => TitleCleaner.Key(i.Title));
        var liveByKey = live.Where(i => i.DocKey is not null)
            .GroupBy(i => i.DocKey!)
            .ToDictionary(g => g.Key, g => g.First());

        // Full-text hits mapped onto live items; unmatched tab hits become "closed tab" results.
        var content = new Dictionary<ResultItem, (double Score, string Snippet)>();
        var closed = new Dictionary<string, ResultItem>(StringComparer.OrdinalIgnoreCase);
        var fts = FtsQuery.Build(query);
        var hits = fts is null ? [] : _index.Search(fts, 200);
        for (var i = 0; i < hits.Count; i++)
        {
            var hit = hits[i];
            var score = Ranker.ContentScore(i, hits.Count);
            var target = liveByKey.GetValueOrDefault(hit.Key);
            if (target is null && hit.Kind == DocKind.Tab)
            {
                var sameTitle = liveByTitle[TitleCleaner.Key(hit.Title)].ToList();
                target = sameTitle.FirstOrDefault(t => t.Hwnd.ToInt64() == hit.Hwnd) ?? sameTitle.FirstOrDefault();
            }

            if (target is not null)
            {
                if (!content.TryGetValue(target, out var existing) || existing.Score < score)
                    content[target] = (score, hit.Snippet);
            }
            else if (hit.Kind == DocKind.Tab && IsWebUrl(hit.Url) && !closed.ContainsKey(hit.Url!))
            {
                closed[hit.Url!] = new ResultItem
                {
                    Kind = ResultKind.ClosedTab,
                    Title = hit.Title,
                    ProcessName = hit.Process,
                    Url = hit.Url,
                    DocKey = hit.Key,
                    ThumbPath = hit.Thumb,
                    LastSeen = hit.LastSeen,
                    Snippet = hit.Snippet,
                    Icon = null,
                    ZOrder = int.MaxValue,
                    Score = Ranker.Combine(0, score * 0.7, now - hit.LastSeen),
                };
            }
        }

        var results = new List<ResultItem>();
        foreach (var item in live)
        {
            var fuzzy = FuzzyMatcher.ScoreFields(query, item.Title, item.AppName + " " + item.ProcessName, item.Url);
            var (contentScore, snippet) = content.GetValueOrDefault(item);
            var age = item.LastSeen == DateTimeOffset.MinValue ? NeverSeenAge : now - item.LastSeen;
            item.Score = Ranker.Combine(fuzzy, contentScore, age);
            if (item.Score <= 0) continue;
            item.Snippet = snippet;
            results.Add(item);
        }
        results.AddRange(closed.Values);

        return results
            .OrderByDescending(i => i.Score)
            .ThenBy(i => i.ZOrder)
            .ThenBy(i => i.TabIndex)
            .Take(MaxResults)
            .ToList();
    }

    public int IndexedCount() => _docs.Count;

    private List<ResultItem> BuildLiveItems()
    {
        var items = new List<ResultItem>(256);
        var includePrivate = _settings().IndexPrivateWindows;
        foreach (var w in _tracker.Windows)
        {
            var icon = IconCache.Get(w.ExePath);
            if (w.Tabs.Count > 0)
            {
                foreach (var tab in w.Tabs)
                {
                    var doc = w.IsPrivate && !includePrivate ? null : _docs.FindTab(tab.Title, w.Hwnd);
                    var lastSeen = doc?.LastSeen ?? DateTimeOffset.MinValue;
                    if (tab.IsActive && w.LastFocused > lastSeen) lastSeen = w.LastFocused;
                    items.Add(new ResultItem
                    {
                        Kind = ResultKind.Tab,
                        Hwnd = w.Hwnd,
                        Title = tab.Title.Length > 0 ? tab.Title : tab.RawName,
                        ProcessName = w.ProcessName,
                        ExePath = w.ExePath,
                        Url = doc?.Url,
                        DocKey = doc?.Key,
                        ThumbPath = doc?.Thumb,
                        LastSeen = lastSeen,
                        ZOrder = w.ZOrder,
                        TabIndex = tab.Index,
                        IsActiveTab = tab.IsActive,
                        IsMinimized = w.IsMinimized,
                        Icon = icon,
                    });
                }
                continue;
            }

            var winDoc = _docs.Get(w.WindowDocKey);
            var seen = winDoc?.LastSeen ?? DateTimeOffset.MinValue;
            if (w.LastFocused > seen) seen = w.LastFocused;
            items.Add(new ResultItem
            {
                Kind = ResultKind.Window,
                Hwnd = w.Hwnd,
                Title = w.IsBrowser ? TitleCleaner.CleanBrowserWindowTitle(w.Title) : w.Title,
                ProcessName = w.ProcessName,
                ExePath = w.ExePath,
                DocKey = w.WindowDocKey,
                ThumbPath = winDoc?.Thumb,
                LastSeen = seen,
                ZOrder = w.ZOrder,
                IsMinimized = w.IsMinimized,
                Icon = icon,
            });
        }
        return items;
    }

    public static bool IsWebUrl(string? url) =>
        url is not null && Uri.TryCreate(url, UriKind.Absolute, out var u) &&
        (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);

    public void Dispose() => _index.Dispose();
}
