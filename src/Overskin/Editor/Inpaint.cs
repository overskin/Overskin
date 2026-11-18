using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Overskin;

/// <summary>
/// Smart erase: paints over the masked pixels with what surrounds them, so text, icons or
/// people vanish from flat or gently shaded backgrounds (most of a screenshot). The gap is filled
/// smoothly from its edges, solved on a coarse copy first and then refined, which keeps big
/// areas fast.
/// </summary>
public static class Inpaint
{
    /// <summary>A copy of <paramref name="image"/> with every masked pixel (mask[y * width + x]) filled in.</summary>
    public static BitmapSource Fill(BitmapSource image, bool[] mask)
    {
        var src = ImageLoader.Normalize(image);
        int w = src.PixelWidth, h = src.PixelHeight;
        var px = new byte[w * h * 4];
        src.CopyPixels(px, w * 4, 0);

        // only work on the box around the mask, with a few known pixels around it
        int x0 = w, y0 = h, x1 = -1, y1 = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (mask[y * w + x]) { x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y); }
        if (x1 < 0) return src;
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
        if (!known.Any(k => k)) return src; // nothing to fill from

        for (int c = 0; c < 3; c++) Solve(values[c], known, rw, rh);

        for (int y = 0; y < rh; y++)
            for (int x = 0; x < rw; x++)
            {
                int i = y * rw + x;
                if (known[i]) continue;
                int s = ((y + y0) * w + x + x0) * 4;
                for (int c = 0; c < 3; c++) px[s + c] = (byte)Math.Clamp(Math.Round(values[c][i]), 0, 255);
                px[s + 3] = 255;
            }
        var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        result.Freeze();
        return result;
    }

    /// <summary>Fills unknown values so each is the average of its neighbours (a smooth membrane).</summary>
    private static void Solve(float[] v, bool[] known, int w, int h)
    {
        if (w > 24 || h > 24)
        {
            // a half-size version first: its answer is a good starting guess here
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
            // tiny: start from the average of what's known and relax for longer
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
        {
            for (int y = 0; y < h; y++)
            {
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
        Dot(points[0]);
        for (int i = 1; i < points.Count; i++)
        {
            var a = points[i - 1];
            var b = points[i];
            int steps = Math.Max(1, (int)((b - a).Length / Math.Max(1, radius / 3)));
            for (int s = 1; s <= steps; s++) Dot(a + (b - a) * (s / (double)steps));
        }
        return mask;
    }
}
