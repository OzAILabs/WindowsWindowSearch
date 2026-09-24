using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace WindowSearcher.App.Extraction;

/// <summary>Built-in Windows OCR (offline) for windows that expose no text through UI Automation.</summary>
internal sealed class OcrService
{
    private readonly OcrEngine? _engine = OcrEngine.TryCreateFromUserProfileLanguages();

    public bool IsAvailable => _engine is not null;

    public string? Recognize(Bitmap source)
    {
        if (_engine is null) return null;

        var max = Math.Min((int)OcrEngine.MaxImageDimension, 2600);
        var scale = Math.Min(1.0, Math.Min((double)max / source.Width, (double)max / source.Height));
        using var bmp = new Bitmap((int)(source.Width * scale), (int)(source.Height * scale), PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp)) g.DrawImage(source, 0, 0, bmp.Width, bmp.Height);

        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        byte[] pixels;
        try
        {
            var rowBytes = bmp.Width * 4;
            pixels = new byte[rowBytes * bmp.Height];
            for (var y = 0; y < bmp.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * rowBytes, rowBytes);
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        using var softwareBitmap = SoftwareBitmap.CreateCopyFromBuffer(
            pixels.AsBuffer(), BitmapPixelFormat.Bgra8, bmp.Width, bmp.Height, BitmapAlphaMode.Premultiplied);
        var result = _engine.RecognizeAsync(softwareBitmap).AsTask().GetAwaiter().GetResult();
        return string.Join('\n', result.Lines.Select(l => l.Text));
    }
}
