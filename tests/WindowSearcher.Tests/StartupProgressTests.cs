using WindowSearcher.Core;

namespace WindowSearcher.Tests;

public class StartupProgressTests
{
    // Windows 1 and 2 are browsers; 3 is another app.
    private static StartupProgress Create() => new([1, 2, 3], [1, 2]);

    [Fact]
    public void Browser_tab_list_read_asks_for_the_full_read_and_is_not_progress()
    {
        var p = Create();
        Assert.True(p.JobDone(1, tabsOnly: true));
        Assert.Equal(0, p.Done);
        Assert.Equal(3, p.Total);
    }

    [Fact]
    public void Finishes_after_every_full_read()
    {
        var p = Create();
        p.JobDone(1, tabsOnly: true);
        p.JobDone(2, tabsOnly: true);
        p.JobDone(3, tabsOnly: false);
        p.JobDone(1, tabsOnly: false);
        Assert.False(p.IsFinished);
        p.JobDone(2, tabsOnly: false);
        Assert.True(p.IsFinished);
        Assert.Equal(3, p.Done);
    }

    [Fact]
    public void Later_background_tab_refresh_does_not_count_as_the_full_read()
    {
        var p = Create();
        Assert.True(p.JobDone(1, tabsOnly: true));
        Assert.False(p.JobDone(1, tabsOnly: true));
        Assert.Equal(0, p.Done);
    }

    [Fact]
    public void Full_read_replacing_the_tab_list_read_counts_and_needs_nothing_more()
    {
        // The user focused the browser before its tab-list read ran: the foreground job reads everything.
        var p = Create();
        Assert.False(p.JobDone(1, tabsOnly: false));
        Assert.Equal(1, p.Done);
        Assert.False(p.JobDone(1, tabsOnly: true));
    }

    [Fact]
    public void Closed_windows_are_forgotten()
    {
        var p = Create();
        p.Forget(1);
        p.Forget(2);
        p.JobDone(3, tabsOnly: false);
        Assert.True(p.IsFinished);
    }

    [Fact]
    public void Browsers_not_in_the_window_list_are_ignored()
    {
        var p = new StartupProgress([3], [1, 2]);
        Assert.False(p.JobDone(1, tabsOnly: true));
        Assert.Equal(1, p.Total);
    }
}
