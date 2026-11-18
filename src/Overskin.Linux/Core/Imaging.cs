using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Overskin.Linux.Core;

public static class Imaging
{
    private static readonly Vector Dpi = new(96, 96);

    public static PixelSize SizeOf(Bitmap b) => b.PixelSize;

    /// <summary>A copy of part of the picture, at full resolution.</summary>
    public static Bitmap Crop(Bitmap source, PixelRect area)
    {
        area = area.Intersect(new PixelRect(source.PixelSize));
        if (area.Width <= 0 || area.Height <= 0) throw new ArgumentException("empty area");

        var result = new WriteableBitmap(area.Size, Dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = result.Lock())
            source.CopyPixels(area, fb.Address, fb.RowBytes * area.Height, fb.RowBytes);
        return result;
    }

    public static byte[] Pixels(Bitmap source, out int stride)
    {
        stride = source.PixelSize.Width * 4;
        var bytes = new byte[stride * source.PixelSize.Height];
        unsafe
        {
            fixed (byte* p = bytes)
                source.CopyPixels(new PixelRect(source.PixelSize), (IntPtr)p, bytes.Length, stride);
        }
        return bytes;
    }

    public static Bitmap FromPixels(byte[] bytes, PixelSize size)
    {
        var result = new WriteableBitmap(size, Dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var fb = result.Lock();
        int stride = size.Width * 4;
        for (int y = 0; y < size.Height; y++)
            System.Runtime.InteropServices.Marshal.Copy(bytes, y * stride, fb.Address + y * fb.RowBytes, stride);
        return result;
    }

    /// <summary>Big blocks of averaged colour over an area, to hide what's under it.</summary>
    public static Bitmap Pixelate(Bitmap source, PixelRect area, int block)
    {
        var part = Crop(source, area);
        var px = Pixels(part, out int stride);
        int w = part.PixelSize.Width, h = part.PixelSize.Height;
        block = Math.Max(2, block);

        for (int by = 0; by < h; by += block)
        for (int bx = 0; bx < w; bx += block)
        {
            int x1 = Math.Min(w, bx + block), y1 = Math.Min(h, by + block);
            long b = 0, g = 0, r = 0, a = 0, n = 0;
            for (int y = by; y < y1; y++)
            for (int x = bx; x < x1; x++)
            {
                int i = y * stride + x * 4;
                b += px[i]; g += px[i + 1]; r += px[i + 2]; a += px[i + 3]; n++;
            }
            for (int y = by; y < y1; y++)
            for (int x = bx; x < x1; x++)
            {
                int i = y * stride + x * 4;
                px[i] = (byte)(b / n); px[i + 1] = (byte)(g / n); px[i + 2] = (byte)(r / n); px[i + 3] = (byte)(a / n);
            }
        }
        return FromPixels(px, part.PixelSize);
    }

    public static byte[] Png(Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms);
        return ms.ToArray();
    }

    public static void SavePng(Bitmap bitmap, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        bitmap.Save(path);
    }

    /// <summary>Draws something at the picture's own pixel size.</summary>
    public static Bitmap Render(PixelSize size, Action<DrawingContext> draw)
    {
        var rtb = new RenderTargetBitmap(size, Dpi);
        using (var ctx = rtb.CreateDrawingContext()) draw(ctx);
        return rtb;
    }

    public static Color ParseColour(string text, Color fallback) =>
        Color.TryParse(text, out var c) ? c : fallback;
}
