using System.Text.Json;
using WindowSearcher.App.Services;
using WindowSearcher.Core;

namespace WindowSearcher.App.UI;

/// <summary>The search window's size and position (physical pixels) after the user last moved or resized it.</summary>
internal static class LauncherPlacement
{
    public static ScreenRect? Load()
    {
        try
        {
            if (!File.Exists(AppPaths.LauncherPlacement)) return null;
            var rect = JsonSerializer.Deserialize<ScreenRect>(File.ReadAllText(AppPaths.LauncherPlacement));
            return rect.Width > 0 && rect.Height > 0 ? rect : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static void Save(ScreenRect rect)
    {
        try
        {
            File.WriteAllText(AppPaths.LauncherPlacement, JsonSerializer.Serialize(rect));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Could not save the search window's position", ex);
        }
    }

    /// <summary>Back to the default: centered on the screen with the mouse pointer.</summary>
    public static void Clear()
    {
        try
        {
            File.Delete(AppPaths.LauncherPlacement);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Could not reset the search window's position", ex);
        }
    }
}
