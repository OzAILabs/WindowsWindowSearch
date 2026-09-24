using WindowSearcher.Core;

namespace WindowSearcher.Tests;

public class ScreenRectTests
{
    private static readonly ScreenRect Laptop = new(0, 0, 1366, 728);
    private static readonly ScreenRect RightScreen = new(1920, 0, 2560, 1400);

    [Fact]
    public void Default_size_is_capped_to_a_small_screen()
    {
        var r = ScreenRect.CenteredIn(Laptop, 1475, 875, 0.94);
        Assert.True(r.Width <= 1366 * 0.94 && r.Height <= 728 * 0.94);
        Assert.True(r.Left >= 0 && r.Right <= Laptop.Right && r.Top >= 0 && r.Bottom <= Laptop.Bottom);
    }

    [Fact]
    public void Default_size_is_kept_on_a_big_screen_and_centered()
    {
        var r = ScreenRect.CenteredIn(RightScreen, 1180, 700, 0.94);
        Assert.Equal(new ScreenRect(1920 + (2560 - 1180) / 2, (1400 - 700) / 3, 1180, 700), r);
    }

    [Fact]
    public void Saved_window_bigger_than_the_screen_shrinks_and_moves_inside()
    {
        var saved = new ScreenRect(200, 100, 2400, 1300);
        var r = saved.FitInside(Laptop, 640, 420);
        Assert.Equal(new ScreenRect(0, 0, 1366, 728), r);
    }

    [Fact]
    public void Saved_window_hanging_off_the_edge_moves_back_on_screen()
    {
        var saved = new ScreenRect(4200, 1200, 800, 600);
        var r = saved.FitInside(RightScreen, 640, 420);
        Assert.Equal(new ScreenRect(RightScreen.Right - 800, 1400 - 600, 800, 600), r);
    }

    [Fact]
    public void Saved_window_that_fits_is_unchanged()
    {
        var saved = new ScreenRect(2000, 50, 1000, 650);
        Assert.Equal(saved, saved.FitInside(RightScreen, 640, 420));
    }

    [Fact]
    public void Round_trips_through_json()
    {
        var saved = new ScreenRect(-1500, 40, 1100, 690);
        var json = System.Text.Json.JsonSerializer.Serialize(saved);
        Assert.Equal(saved, System.Text.Json.JsonSerializer.Deserialize<ScreenRect>(json));
    }

    [Fact]
    public void Minimum_size_is_applied_but_never_beyond_the_screen()
    {
        Assert.Equal(new ScreenRect(0, 0, 640, 420), new ScreenRect(0, 0, 100, 100).FitInside(Laptop, 640, 420));
        var tiny = new ScreenRect(0, 0, 500, 300);
        Assert.Equal(tiny, new ScreenRect(0, 0, 100, 100).FitInside(tiny, 640, 420));
    }
}
