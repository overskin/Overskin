using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Overskin.Linux.Core;

/// <summary>A picture as plain BGRA bytes, for pixel work.</summary>
public sealed class Px
{
    public int W { get; }
    public int H { get; }
    public byte[] D { get; }

    public Px(int w, int h, byte[]? data = null)
    {
        W = w; H = h;
        D = data ?? new byte[w * h * 4];
    }

    public static Px From(Bitmap b) => new(b.PixelSize.Width, b.PixelSize.Height, Imaging.Pixels(b, out _));
    public Bitmap ToBitmap() => Imaging.FromPixels(D, new PixelSize(W, H));

    public Px Crop(PixelRect r)
    {
        var p = new Px(r.Width, r.Height);
        for (int y = 0; y < r.Height; y++) Buffer.BlockCopy(D, ((r.Y + y) * W + r.X) * 4, p.D, y * r.Width * 4, r.Width * 4);
        return p;
    }
}

/// <summary>How a hidden area looks.</summary>
public enum HideMode { Blur, Pixelate, Mosaic }

/// <summary>Pixel effects shared by the editor: hiding, smart erase, turning and flipping.</summary>
public static class Effects
{
    // ---- hiding (blur, pixelate, mosaic) -----------------------------------------------------

    /// <summary>The area hidden, as a picture the area's size. <paramref name="strength"/> runs 1 to 12.</summary>
    public static Bitmap? Hide(Bitmap image, PixelRect area, HideMode mode, double strength)
    {
        area = area.Intersect(new PixelRect(image.PixelSize));
        if (area.Width < 2 || area.Height < 2) return null;
        var all = Px.From(image);
        var part = all.Crop(area);
        int block = (int)Math.Max(3, Math.Round(2 + strength * 3)); // 1..12 -> 5..38 px

        switch (mode)
        {
            case HideMode.Pixelate: Blocks(part, block, grout: false); break;
            case HideMode.Mosaic: Blocks(part, Math.Max(6, block), grout: true); break;
            default:
                // blur from a little outside the area, so its edges fade into what's around
                int radius = Math.Max(2, block / 2 + 1);
                var outer = new PixelRect(area.X - radius, area.Y - radius, area.Width + radius * 2, area.Height + radius * 2)
                    .Intersect(new PixelRect(image.PixelSize));
                var big = all.Crop(outer);
                for (int pass = 0; pass < 3; pass++) BoxBlur(big, radius);
                part = big.Crop(new PixelRect(area.X - outer.X, area.Y - outer.Y, area.Width, area.Height));
                break;
        }
        for (int i = 3; i < part.D.Length; i += 4) part.D[i] = 255;
        return part.ToBitmap();
    }

    private static void Blocks(Px p, int block, bool grout)
    {
        int w = p.W, h = p.H;
        var px = p.D;
        for (int by = 0; by < h; by += block)
        for (int bx = 0; bx < w; bx += block)
        {
            int bw = Math.Min(block, w - bx), bh = Math.Min(block, h - by);
            long b = 0, g = 0, r = 0;
            for (int yy = by; yy < by + bh; yy++)
            for (int xx = bx; xx < bx + bw; xx++)
            {
                int i = (yy * w + xx) * 4;
                b += px[i]; g += px[i + 1]; r += px[i + 2];
            }
            int n = bw * bh;
            byte cb = (byte)(b / n), cg = (byte)(g / n), cr = (byte)(r / n);
            if (grout)
            {
                // mosaic: each tile a touch lighter or darker, with a darker line between tiles
                int jitter = ((bx / block * 73856093) ^ (by / block * 19349663)) % 15 - 7;
                cb = (byte)Math.Clamp(cb + jitter, 0, 255); cg = (byte)Math.Clamp(cg + jitter, 0, 255); cr = (byte)Math.Clamp(cr + jitter, 0, 255);
            }
            for (int yy = by; yy < by + bh; yy++)
            for (int xx = bx; xx < bx + bw; xx++)
            {
                int i = (yy * w + xx) * 4;
                bool line = grout && (xx == bx || yy == by);
                px[i] = line ? (byte)(cb * 0.62) : cb;
                px[i + 1] = line ? (byte)(cg * 0.62) : cg;
                px[i + 2] = line ? (byte)(cr * 0.62) : cr;
            }
        }
    }

    private static void BoxBlur(Px p, int radius)
    {
        int w = p.W, h = p.H;
        var px = p.D;
        var tmp = new byte[px.Length];
        for (int pass = 0; pass < 2; pass++)
        {
            bool horizontal = pass == 0;
            int lines = horizontal ? h : w, length = horizontal ? w : h;
            byte[] from = horizontal ? px : tmp, to = horizontal ? tmp : px;
            for (int l = 0; l < lines; l++)
            {
                int Index(int k) => horizontal ? (l * w + k) * 4 : (k * w + l) * 4;
                for (int c = 0; c < 3; c++)
                {
                    int sum = 0;
                    for (int k = -radius; k <= radius; k++) sum += from[Index(Math.Clamp(k, 0, length - 1)) + c];
                    for (int k = 0; k < length; k++)
                    {
                        to[Index(k) + c] = (byte)(sum / (2 * radius + 1));
                        sum += from[Index(Math.Min(length - 1, k + radius + 1)) + c] - from[Index(Math.Max(0, k - radius)) + c];
                    }
                }
            }
        }
    }

    // ---- smart erase ---------------------------------------------------------------------------

    /// <summary>
    /// Paints over the masked pixels with what surrounds them, so text and icons vanish from flat
    /// or gently shaded backgrounds: solved on a coarse copy first, then refined.
    /// </summary>
    public static Bitmap Inpaint(Bitmap image, bool[] mask)
    {
        var src = Px.From(image);
        int w = src.W, h = src.H;
        var px = src.D;

        int x0 = w, y0 = h, x1 = -1, y1 = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (mask[y * w + x]) { x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y); }
        if (x1 < 0) return image;
        x0 = Math.Max(0, x0 - 4); y0 = Math.Max(0, y0 - 4); x1 = Math.Min(w - 1, x1 + 4); y1 = Math.Min(h - 1, y1 + 4);
        int rw = x1 - x0 + 1, rh = y1 - y0 + 1;

        var known = new bool[rw * rh];
        var values = new float[3][];
        for (int c = 0; c < 3; c++) values[c] = new float[rw * rh];
        for (int y = 0; y < rh; y++)
            for (int x = 0; x < rw; x++)
            {
                int i = y * rw + x, s = ((y + y0) * w + x + x0) * 4;
                known[i] = !mask[(y + y0) * w + x + x0];
                for (int c = 0; c < 3; c++) values[c][i] = px[s + c];
            }
        if (!known.Any(k => k)) return image;

        Parallel.For(0, 3, c => Solve(values[c], known, rw, rh));

        for (int y = 0; y < rh; y++)
            for (int x = 0; x < rw; x++)
            {
                int i = y * rw + x;
                if (known[i]) continue;
                int s = ((y + y0) * w + x + x0) * 4;
                for (int c = 0; c < 3; c++) px[s + c] = (byte)Math.Clamp(Math.Round(values[c][i]), 0, 255);
                px[s + 3] = 255;
            }
        return src.ToBitmap();
    }

    private static void Solve(float[] v, bool[] known, int w, int h)
    {
        if (w > 24 || h > 24)
        {
            int cw = (w + 1) / 2, ch = (h + 1) / 2;
            var cv = new float[cw * ch];
            var ck = new bool[cw * ch];
            for (int y = 0; y < ch; y++)
                for (int x = 0; x < cw; x++)
                {
                    float sum = 0;
                    int n = 0;
                    for (int dy = 0; dy < 2; dy++)
                        for (int dx = 0; dx < 2; dx++)
                        {
                            int fx = x * 2 + dx, fy = y * 2 + dy;
                            if (fx >= w || fy >= h || !known[fy * w + fx]) continue;
                            sum += v[fy * w + fx];
                            n++;
                        }
                    ck[y * cw + x] = n > 0;
                    cv[y * cw + x] = n > 0 ? sum / n : 0;
                }
            if (ck.Any(k => k)) Solve(cv, ck, cw, ch);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (!known[y * w + x]) v[y * w + x] = cv[(y / 2) * cw + x / 2];
            Relax(v, known, w, h, 40);
        }
        else
        {
            float mean = 0;
            int n = 0;
            for (int i = 0; i < v.Length; i++) if (known[i]) { mean += v[i]; n++; }
            mean = n > 0 ? mean / n : 0;
            for (int i = 0; i < v.Length; i++) if (!known[i]) v[i] = mean;
            Relax(v, known, w, h, 300);
        }
    }

    private static void Relax(float[] v, bool[] known, int w, int h, int passes)
    {
        for (int pass = 0; pass < passes; pass++)
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (known[i]) continue;
                    float sum = 0;
                    int n = 0;
                    if (x > 0) { sum += v[i - 1]; n++; }
                    if (x < w - 1) { sum += v[i + 1]; n++; }
                    if (y > 0) { sum += v[i - w]; n++; }
                    if (y < h - 1) { sum += v[i + w]; n++; }
                    v[i] = sum / n;
                }
    }

    /// <summary>A mask from a brush stroke: every pixel within <paramref name="radius"/> of the path.</summary>
    public static bool[] StrokeMask(IReadOnlyList<Point> points, double radius, int w, int h)
    {
        var mask = new bool[w * h];
        void Dot(Point c)
        {
            int xa = Math.Max(0, (int)(c.X - radius)), xb = Math.Min(w - 1, (int)(c.X + radius));
            int ya = Math.Max(0, (int)(c.Y - radius)), yb = Math.Min(h - 1, (int)(c.Y + radius));
            for (int y = ya; y <= yb; y++)
                for (int x = xa; x <= xb; x++)
                    if ((x + 0.5 - c.X) * (x + 0.5 - c.X) + (y + 0.5 - c.Y) * (y + 0.5 - c.Y) <= radius * radius) mask[y * w + x] = true;
        }
        if (points.Count == 0) return mask;
        Dot(points[0]);
        for (int i = 1; i < points.Count; i++)
        {
            var a = points[i - 1];
            var b = points[i];
            var d = b - a;
            double length = Math.Sqrt(d.X * d.X + d.Y * d.Y);
            int steps = Math.Max(1, (int)(length / Math.Max(1, radius / 3)));
            for (int s = 1; s <= steps; s++) Dot(a + d * (s / (double)steps));
        }
        return mask;
    }

    // ---- turning, flipping, resizing ----------------------------------------------------------

    public static Bitmap Rotate(Bitmap image, bool clockwise)
    {
        var s = Px.From(image);
        var r = new Px(s.H, s.W);
        for (int y = 0; y < s.H; y++)
            for (int x = 0; x < s.W; x++)
            {
                int nx = clockwise ? s.H - 1 - y : y, ny = clockwise ? x : s.W - 1 - x;
                Buffer.BlockCopy(s.D, (y * s.W + x) * 4, r.D, (ny * r.W + nx) * 4, 4);
            }
        return r.ToBitmap();
    }

    public static Bitmap Flip(Bitmap image, bool horizontal)
    {
        var s = Px.From(image);
        var r = new Px(s.W, s.H);
        for (int y = 0; y < s.H; y++)
            for (int x = 0; x < s.W; x++)
            {
                int nx = horizontal ? s.W - 1 - x : x, ny = horizontal ? y : s.H - 1 - y;
                Buffer.BlockCopy(s.D, (y * s.W + x) * 4, r.D, (ny * r.W + nx) * 4, 4);
            }
        return r.ToBitmap();
    }

    public static Bitmap Resize(Bitmap image, int width, int height) =>
        Imaging.Render(new PixelSize(Math.Max(1, width), Math.Max(1, height)), ctx =>
        {
            using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
                ctx.DrawImage(image, new Rect(image.Size), new Rect(0, 0, width, height));
        });
}

public sealed record OcrWord(string Text, Rect Bounds);
