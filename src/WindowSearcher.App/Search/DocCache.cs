using WindowSearcher.Core;

namespace WindowSearcher.App.Search;

/// <summary>
/// UI-thread copy of the index metadata (no text): which preview and recency belong to which
/// window or tab. Lets the launcher open instantly without touching the database.
/// </summary>
internal sealed class DocCache
{
    private readonly Dictionary<string, DocSummary> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<DocSummary>> _tabsByTitle = new(StringComparer.Ordinal);

    public int Count => _byKey.Count;

    public IEnumerable<DocSummary> All => _byKey.Values;

    public void Load(IEnumerable<DocSummary> docs)
    {
        Clear();
        foreach (var d in docs) Upsert(d);
    }

    public void Upsert(DocSummary doc)
    {
        if (_byKey.TryGetValue(doc.Key, out var old)) RemoveFromTitleIndex(old);
        _byKey[doc.Key] = doc;
        if (doc.Kind == DocKind.Tab)
        {
            var k = TitleCleaner.Key(doc.Title);
            if (!_tabsByTitle.TryGetValue(k, out var list)) _tabsByTitle[k] = list = new List<DocSummary>(1);
            list.Add(doc);
        }
    }

    public void Remove(IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            if (!_byKey.Remove(key, out var old)) continue;
            RemoveFromTitleIndex(old);
        }
    }

    public DocSummary? Get(string key) => _byKey.GetValueOrDefault(key);

    /// <summary>Indexed page for a live tab: same window first, otherwise the most recently seen.</summary>
    public DocSummary? FindTab(string title, IntPtr hwnd)
    {
        if (!_tabsByTitle.TryGetValue(TitleCleaner.Key(title), out var list) || list.Count == 0) return null;
        var h = hwnd.ToInt64();
        DocSummary? best = null;
        foreach (var d in list)
        {
            if (best is null ||
                (d.Hwnd == h && best.Hwnd != h) ||
                ((d.Hwnd == h) == (best.Hwnd == h) && d.LastSeen > best.LastSeen))
                best = d;
        }
        return best;
    }

    public void Clear()
    {
        _byKey.Clear();
        _tabsByTitle.Clear();
    }

    private void RemoveFromTitleIndex(DocSummary doc)
    {
        if (doc.Kind != DocKind.Tab) return;
        var k = TitleCleaner.Key(doc.Title);
        if (!_tabsByTitle.TryGetValue(k, out var list)) return;
        list.RemoveAll(d => d.Key == doc.Key);
        if (list.Count == 0) _tabsByTitle.Remove(k);
    }
}
