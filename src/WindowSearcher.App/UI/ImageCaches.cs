using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WindowSearcher.App.UI;

/// <summary>Decrypts and decodes previews off the UI thread at the size they're shown. Cleared when the launcher hides.</summary>
internal static class ThumbnailLoader
{
    private static readonly ConcurrentDictionary<(string, int), BitmapSource> Cache = new();

    public static BitmapSource? TryGetCached(string path, int decodeWidth) =>
        Cache.GetValueOrDefault((path, decodeWidth));

    public static Task<BitmapSource?> LoadAsync(string path, int decodeWidth) => Task.Run(() =>
    {
        if (Cache.TryGetValue((path, decodeWidth), out var cached)) return cached;
        try
        {
            var bytes = Services.SecureStore.Open(File.ReadAllBytes(path));
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            img.DecodePixelWidth = decodeWidth;
            img.StreamSource = new MemoryStream(bytes);
            img.EndInit();
            img.Freeze();
            Cache[(path, decodeWidth)] = img;
            return (BitmapSource?)img;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    });

    public static void Clear() => Cache.Clear();
}

/// <summary>Application icons by executable path. UI thread only.</summary>
internal static class IconCache
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Get(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return null;
        if (Cache.TryGetValue(exePath, out var icon)) return icon;
        try
        {
            using var ico = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            if (ico is not null)
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                icon = src;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            icon = null;
        }
        Cache[exePath] = icon;
        return icon;
    }
}
