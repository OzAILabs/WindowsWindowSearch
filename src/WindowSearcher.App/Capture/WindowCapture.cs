using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;
using WindowSearcher.App.Native;
using WindowSearcher.App.Services;

namespace WindowSearcher.App.Capture;

/// <summary>
/// Window screenshots via GDI. Runs on the background worker; uses System.Drawing so no WPF
/// objects are created off the UI thread.
/// </summary>
internal static class WindowCapture
{
    private const int ThumbMaxWidth = 960;
    private const int ThumbMaxHeight = 720;
    private static readonly ImageCodecInfo JpegCodec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    /// <summary>
    /// Copies the window's area from the screen. Only valid while the window is in the foreground;
    /// it never talks to the target app, so a hung app can't block us.
    /// </summary>
    public static Bitmap? FromScreen(IntPtr hwnd)
    {
        var r = Win32.GetVisibleBounds(hwnd);
        if (r.Width < 16 || r.Height < 16) return null;
        var screenDc = Win32.GetDC(IntPtr.Zero);
        var memDc = Win32.CreateCompatibleDC(screenDc);
        var hbm = Win32.CreateCompatibleBitmap(screenDc, r.Width, r.Height);
        var old = Win32.SelectObject(memDc, hbm);
        try
        {
            if (!Win32.BitBlt(memDc, 0, 0, r.Width, r.Height, screenDc, r.Left, r.Top, Win32.SRCCOPY)) return null;
            Win32.SelectObject(memDc, old);
            old = IntPtr.Zero;
            return Image.FromHbitmap(hbm);
        }
        finally
        {
            if (old != IntPtr.Zero) Win32.SelectObject(memDc, old);
            Win32.DeleteObject(hbm);
            Win32.DeleteDC(memDc);
            Win32.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>
    /// Asks the window to render itself (works for covered windows, not minimized ones).
    /// Browsers that stopped painting an occluded window return a blank image, which callers drop.
    /// </summary>
    public static Bitmap? WithPrintWindow(IntPtr hwnd)
    {
        if (!Win32.GetWindowRect(hwnd, out var wr) || wr.Width < 16 || wr.Height < 16) return null;
        var vb = Win32.GetVisibleBounds(hwnd);
        var screenDc = Win32.GetDC(IntPtr.Zero);
        var memDc = Win32.CreateCompatibleDC(screenDc);
        var hbm = Win32.CreateCompatibleBitmap(screenDc, wr.Width, wr.Height);
        var old = Win32.SelectObject(memDc, hbm);
        try
        {
            if (!Win32.PrintWindow(hwnd, memDc, Win32.PW_RENDERFULLCONTENT)) return null;
            Win32.SelectObject(memDc, old);
            old = IntPtr.Zero;
            using var full = Image.FromHbitmap(hbm);
            // Trim the invisible resize border that GetWindowRect includes.
            var crop = Rectangle.Intersect(
                new Rectangle(vb.Left - wr.Left, vb.Top - wr.Top, vb.Width, vb.Height),
                new Rectangle(0, 0, full.Width, full.Height));
            if (crop.Width < 16 || crop.Height < 16) return null;
            return full.Clone(crop, PixelFormat.Format32bppRgb);
        }
        finally
        {
            if (old != IntPtr.Zero) Win32.SelectObject(memDc, old);
            Win32.DeleteObject(hbm);
            Win32.DeleteDC(memDc);
            Win32.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>True when the image is (nearly) a single color, e.g. an unpainted browser window.</summary>
    public static bool IsBlank(Bitmap bmp)
    {
        const int grid = 24;
        int minR = 255, minG = 255, minB = 255, maxR = 0, maxG = 0, maxB = 0;
        for (var gy = 0; gy < grid; gy++)
        {
            var y = (int)((gy + 0.5) * bmp.Height / grid);
            for (var gx = 0; gx < grid; gx++)
            {
                var x = (int)((gx + 0.5) * bmp.Width / grid);
                var c = bmp.GetPixel(x, y);
                minR = Math.Min(minR, c.R); maxR = Math.Max(maxR, c.R);
                minG = Math.Min(minG, c.G); maxG = Math.Max(maxG, c.G);
                minB = Math.Min(minB, c.B); maxB = Math.Max(maxB, c.B);
            }
        }
        return maxR - minR < 10 && maxG - minG < 10 && maxB - minB < 10;
    }

    /// <summary>Saves a downscaled, encrypted JPEG and returns its path. File names are unique per capture.</summary>
    public static string SaveThumbnail(Bitmap bmp, string docKey)
    {
        var scale = Math.Min(1.0, Math.Min((double)ThumbMaxWidth / bmp.Width, (double)ThumbMaxHeight / bmp.Height));
        var w = Math.Max(1, (int)(bmp.Width * scale));
        var h = Math.Max(1, (int)(bmp.Height * scale));
        using var thumb = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(thumb))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(bmp, 0, 0, w, h);
        }
        var name = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(docKey)))[..16];
        var path = Path.Combine(AppPaths.Thumbs, $"{name}_{DateTime.UtcNow.Ticks:x}.bin");
        using var args = new EncoderParameters(1);
        args.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 72L);
        using var jpeg = new MemoryStream();
        thumb.Save(jpeg, JpegCodec, args);
        File.WriteAllBytes(path, SecureStore.Seal(jpeg.ToArray()));
        return path;
    }

    public static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            // Only ever delete inside our own thumbnail folder.
            if (Path.GetDirectoryName(Path.GetFullPath(path))?.Equals(Path.GetFullPath(AppPaths.Thumbs).TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase) == true)
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
