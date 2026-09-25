namespace WindowSearcher.Core;

/// <summary>
/// Progress of the one-time pass over the windows that were open when the app started. Browser windows get a
/// quick tab-list read first, then every window gets its full read. Windows are identified by handle.
/// Any finished full job counts, because a newer job (the user focusing the window) can replace a queued one.
/// </summary>
public sealed class StartupProgress
{
    private readonly HashSet<long> _tabsFirst;
    private readonly HashSet<long> _pending;

    public StartupProgress(IEnumerable<long> windows, IEnumerable<long> browsers)
    {
        _pending = [.. windows];
        _tabsFirst = [.. browsers.Where(_pending.Contains)];
        Total = _pending.Count;
    }

    public int Total { get; }
    public int Done => Total - _pending.Count;
    public bool IsFinished => _pending.Count == 0;

    /// <summary>
    /// Records a finished job for a window. Returns true when that was the window's startup tab-list read and it
    /// still needs its full read. Other tab-list refreshes (tabs changing in the background) don't count.
    /// </summary>
    public bool JobDone(long window, bool tabsOnly)
    {
        if (tabsOnly) return _tabsFirst.Remove(window) && _pending.Contains(window);
        _tabsFirst.Remove(window);
        _pending.Remove(window);
        return false;
    }

    /// <summary>The window closed (or can't be read), so it no longer counts.</summary>
    public void Forget(long window)
    {
        _tabsFirst.Remove(window);
        _pending.Remove(window);
    }

    /// <summary>Stops tracking, e.g. when the pass takes unreasonably long.</summary>
    public void Abandon()
    {
        _tabsFirst.Clear();
        _pending.Clear();
    }
}
