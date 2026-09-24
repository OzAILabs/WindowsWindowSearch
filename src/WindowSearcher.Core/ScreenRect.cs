namespace WindowSearcher.Core;

/// <summary>A window or monitor rectangle in physical pixels.</summary>
public readonly record struct ScreenRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;

    /// <summary>
    /// Shrinks the rectangle to fit inside <paramref name="area"/> (but not below the minimum size, unless the
    /// area itself is smaller) and moves it inside, so a window saved on a bigger screen is fully visible.
    /// </summary>
    public ScreenRect FitInside(ScreenRect area, int minWidth = 0, int minHeight = 0)
    {
        var w = Math.Min(Math.Max(Width, minWidth), area.Width);
        var h = Math.Min(Math.Max(Height, minHeight), area.Height);
        var x = Math.Clamp(Left, area.Left, area.Right - w);
        var y = Math.Clamp(Top, area.Top, area.Bottom - h);
        return new ScreenRect(x, y, w, h);
    }

    /// <summary>
    /// A rectangle of the given size, capped at <paramref name="maxFraction"/> of the area, centered horizontally
    /// and a third of the way down.
    /// </summary>
    public static ScreenRect CenteredIn(ScreenRect area, int width, int height, double maxFraction)
    {
        var w = Math.Min(width, (int)(area.Width * maxFraction));
        var h = Math.Min(height, (int)(area.Height * maxFraction));
        return new ScreenRect(area.Left + (area.Width - w) / 2, area.Top + (area.Height - h) / 3, w, h);
    }
}
