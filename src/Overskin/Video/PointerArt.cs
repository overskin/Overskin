using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Overskin;

/// <summary>A pre-drawn picture of the pointer: premultiplied BGRA, and where its tip is.</summary>
public sealed record PointerSprite(byte[] Pixels, int Width, int Height, double HotX, double HotY);

/// <summary>
/// Vector pointers for recordings. They're drawn from shapes at the exact size each frame
/// needs, so they stay perfectly sharp however big the pointer is or however far the video
/// zooms in, unlike the small bitmap Windows keeps for its own pointer.
/// </summary>
public static class PointerArt
{
    public enum Shape { Arrow, Text }

    // drawn on a 32-unit grid; the arrow's tip is at (2, 2)
    private static readonly Geometry ArrowShape = Geometry.Parse(
        "M 2,2 L 2,24.5 L 7.4,19.4 L 11.1,27.6 L 15.0,25.9 L 11.4,17.8 L 18.8,17.8 Z");
    private static readonly Geometry TextShape = Geometry.Parse(
        "M 9,3 H 12 C 13.4,3 14.6,3.6 15,4.6 C 15.4,3.6 16.6,3 18,3 H 21 V 5 H 18 C 16.9,5 16,5.9 16,7 V 25 " +
        "C 16,26.1 16.9,27 18,27 H 21 V 29 H 18 C 16.6,29 15.4,28.4 15,27.4 C 14.6,28.4 13.4,29 12,29 H 9 V 27 H 12 " +
        "C 13.1,27 14,26.1 14,25 V 7 C 14,5.9 13.1,5 12,5 H 9 Z");

    /// <summary>Draws a pointer <paramref name="size"/> pixels tall. Call on the UI thread.</summary>
    public static PointerSprite Render(Shape shape, int size, bool dark)
    {
        var geometry = shape == Shape.Arrow ? ArrowShape : TextShape;
        double k = size / 32.0;
        Brush fill = dark ? new SolidColorBrush(Color.FromRgb(20, 20, 22)) : Brushes.White;
        Brush edge = dark ? Brushes.White : new SolidColorBrush(Color.FromRgb(20, 20, 22));
        double hotX = shape == Shape.Arrow ? 2 : 15, hotY = shape == Shape.Arrow ? 2 : 16;

        // a little room around it for the outline and the shadow
        int pad = (int)Math.Ceiling(4 * k) + 2;
        int w = (int)Math.Ceiling(24 * k) + 2 * pad, h = size + 2 * pad;

        var visual = new DrawingVisual
        {
            Effect = new DropShadowEffect { BlurRadius = 5 * k, ShadowDepth = 1.4 * k, Direction = 270, Opacity = 0.35, Color = Colors.Black },
        };
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(pad, pad));
            dc.PushTransform(new ScaleTransform(k, k));
            dc.DrawGeometry(fill, new Pen(edge, 1.5) { LineJoin = PenLineJoin.Round }, geometry);
            dc.Pop();
            dc.Pop();
        }
        var target = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        var pixels = new byte[w * h * 4];
        target.CopyPixels(pixels, w * 4, 0);
        return new PointerSprite(pixels, w, h, pad + hotX * k, pad + hotY * k);
    }

    /// <summary>Lays a sprite over a BGRA frame with its tip at (x, y).</summary>
    public static void Blend(byte[] frame, int frameW, int frameH, PointerSprite s, double x, double y)
    {
        int left = (int)Math.Round(x - s.HotX), top = (int)Math.Round(y - s.HotY);
        for (int sy = 0; sy < s.Height; sy++)
        {
            int fy = top + sy;
            if (fy < 0 || fy >= frameH) continue;
            for (int sx = 0; sx < s.Width; sx++)
            {
                int fx = left + sx;
                if (fx < 0 || fx >= frameW) continue;
                int si = (sy * s.Width + sx) * 4;
                int a = s.Pixels[si + 3];
                if (a == 0) continue;
                int fi = (fy * frameW + fx) * 4, keep = 255 - a;
                frame[fi] = (byte)(s.Pixels[si] + frame[fi] * keep / 255);
                frame[fi + 1] = (byte)(s.Pixels[si + 1] + frame[fi + 1] * keep / 255);
                frame[fi + 2] = (byte)(s.Pixels[si + 2] + frame[fi + 2] * keep / 255);
            }
        }
    }

    /// <summary>A smooth (anti-aliased) ring: white with a thin dark edge, faded by <paramref name="opacity"/>.</summary>
    public static void Ring(byte[] frame, int frameW, int frameH, double cx, double cy, double radius, double width, double opacity)
    {
        int x0 = Math.Max(0, (int)(cx - radius - width - 2)), x1 = Math.Min(frameW - 1, (int)(cx + radius + width + 2));
        int y0 = Math.Max(0, (int)(cy - radius - width - 2)), y1 = Math.Min(frameH - 1, (int)(cy + radius + width + 2));
        double half = width / 2;
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                double d = Math.Sqrt((x + 0.5 - cx) * (x + 0.5 - cx) + (y + 0.5 - cy) * (y + 0.5 - cy));
                double off = Math.Abs(d - radius);
                double white = Math.Clamp(half - off + 0.5, 0, 1);
                double shade = Math.Clamp(half + 1 - off + 0.5, 0, 1) - white;
                if (white <= 0 && shade <= 0) continue;
                int i = (y * frameW + x) * 4;
                for (int c = 0; c < 3; c++)
                {
                    double v = frame[i + c];
                    v += (255 - v) * white * opacity;
                    v += (20 - v) * shade * opacity * 0.6;
                    frame[i + c] = (byte)Math.Clamp(v, 0, 255);
                }
            }
        }
    }
}
