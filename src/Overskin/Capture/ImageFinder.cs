using System.Windows;

namespace Overskin;

/// <summary>A picture found inside a screenshot: where it is, and how round its corners are.</summary>
public readonly record struct FoundImage(Int32Rect Bounds, int Radius);

/// <summary>
/// Finds the pictures inside a screenshot (a photo someone sent in Discord, an image on a web
/// page, a video thumbnail), so the capture screen can snap to one the way it snaps to a window.
///
/// A picture shows up as four long straight edges where a flat background (one colour, pixel
/// after pixel) meets something else, lined up into a rectangle. Rounded corners only shorten
/// those edges, by the same amount on every side, which is how they're recognised. What's
/// inside must look like a picture (lots of colours and texture), not a flat panel or a box of
/// text. The whole screen takes a few tens of milliseconds, on a background thread.
/// </summary>
public static class ImageFinder
{
    private const int MinSide = 36;     // smaller than this isn't worth snapping to
    private const int MaxRadius = 40;   // the roundest corner recognised
    private const int FlatTolerance = 3;
    private const int EdgeContrast = 14;

    private readonly record struct Seg(int Pos, int A, int B)
    {
        public int Length => B - A + 1;
    }

    /// <summary>The pictures in a BGRA screenshot, in its pixel coordinates.</summary>
    public static List<FoundImage> Find(byte[] px, int w, int h, CancellationToken ct = default)
    {
        var found = new List<FoundImage>();
        if (w < MinSide + 2 || h < MinSide + 2) return found;

        // the four kinds of edge, each with the flat background on a different side
        var tops = new List<Seg>[h];
        var bottoms = new List<Seg>[h];
        var lefts = new List<Seg>[w];
        var rights = new List<Seg>[w];
        Parallel.For(1, h - 1, new ParallelOptions { CancellationToken = ct }, y =>
        {
            tops[y] = Runs(px, w, h, y, y - 1, horizontal: true);
            bottoms[y] = Runs(px, w, h, y, y + 1, horizontal: true);
        });
        Parallel.For(1, w - 1, new ParallelOptions { CancellationToken = ct }, x =>
        {
            lefts[x] = Runs(px, w, h, x, x - 1, horizontal: false);
            rights[x] = Runs(px, w, h, x, x + 1, horizontal: false);
        });
        ct.ThrowIfCancellationRequested();

        // bottoms by where they start, to pair with tops quickly
        var bottomsByStart = new Dictionary<int, List<Seg>>();
        for (int y = 1; y < h - 1; y++)
            foreach (var s in bottoms[y])
            {
                if (!bottomsByStart.TryGetValue(s.A, out var list)) bottomsByStart[s.A] = list = new();
                list.Add(s);
            }

        for (int y = 1; y < h - 1; y++)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var top in tops[y])
            {
                // the nearest bottom below with the same ends (rounded corners shorten both the same)
                Seg? bottom = null;
                for (int a = top.A - 2; a <= top.A + 2; a++)
                {
                    if (!bottomsByStart.TryGetValue(a, out var list)) continue;
                    foreach (var b in list)
                        if (b.Pos >= top.Pos + MinSide - 1 && Math.Abs(b.B - top.B) <= 2 && (bottom == null || b.Pos < bottom.Value.Pos)
                            && Sides(lefts, rights, top, b, w) != null)
                            bottom = b;
                }
                if (bottom is not { } bot) continue;
                if (Sides(lefts, rights, top, bot, w) is not { } sides) continue;
                var bounds = new Int32Rect(sides.Left, top.Pos, sides.Right - sides.Left + 1, bot.Pos - top.Pos + 1);
                if (bounds.Width < MinSide || bounds.Height < MinSide) continue;
                if (!LooksLikeAPicture(px, w, bounds)) continue;
                if (found.Any(f => Same(f.Bounds, bounds))) continue;
                found.Add(new FoundImage(bounds, sides.Radius));
            }
        }
        return found;
    }

    /// <summary>
    /// The left and right edges that close a top and bottom into a rectangle: just outside their
    /// ends by the corner radius, and starting and ending that same radius in from the top and
    /// bottom, so the corners are round (or square, radius 0) and alike.
    /// </summary>
    private static (int Left, int Right, int Radius)? Sides(List<Seg>[] lefts, List<Seg>[] rights, Seg top, Seg bottom, int w)
    {
        int height = bottom.Pos - top.Pos + 1;
        int? Find(List<Seg>[] columns, int from, int step, out int radius)
        {
            radius = -1;
            for (int r = 0; r <= MaxRadius; r++)
            {
                int x = from + step * r;
                if (x <= 0 || x >= w - 1 || columns[x] == null) break;
                foreach (var s in columns[x])
                {
                    // starts about r down from the top and ends about r up from the bottom
                    int slack = 2 + r / 3;
                    if (Math.Abs(s.A - (top.Pos + r)) <= slack && Math.Abs(s.B - (bottom.Pos - r)) <= slack && s.Length >= (height - 2 * r) * 0.8)
                    {
                        radius = r;
                        return x;
                    }
                }
            }
            return null;
        }
        if (Find(lefts, top.A, -1, out int rl) is not { } left) return null;
        if (Find(rights, top.B, 1, out int rr) is not { } right) return null;
        if (Math.Abs(rl - rr) > 2 + Math.Max(rl, rr) / 4) return null;
        // the straight edges start where a round corner first covers a pixel enough to show,
        // about sqrt(R) short of the true radius R
        int seen = Math.Max(rl, rr);
        int radius = seen == 0 ? 0 : (int)Math.Round(Math.Pow((1 + Math.Sqrt(1 + 4.0 * seen)) / 2, 2) + 1);
        return (left, right, radius);
    }

    /// <summary>
    /// The edges along one row (or column) <paramref name="line"/>, with the flat background on
    /// <paramref name="outside"/>: stretches where the outside is one even colour and the inside
    /// mostly differs from it.
    /// </summary>
    private static List<Seg> Runs(byte[] px, int w, int h, int line, int outside, bool horizontal)
    {
        var runs = new List<Seg>();
        int n = horizontal ? w : h;
        int Index(int along, int across) => horizontal ? (across * w + along) * 4 : (along * w + across) * 4;

        int start = -1, lastHit = -1, hits = 0, gap = 0, rb = 0, rg = 0, rr = 0;
        void Close()
        {
            if (start >= 0 && lastHit - start + 1 >= MinSide - 2 * MaxRadius / 2 && lastHit - start + 1 >= MinSide / 2 && hits >= (lastHit - start + 1) * 0.7)
                runs.Add(new Seg(line, start, lastHit));
            start = -1;
            hits = 0;
            gap = 0;
        }
        for (int i = 1; i < n - 1; i++)
        {
            int o = Index(i, outside), p = Index(i - 1, outside), s = Index(i, line);
            bool flat = Math.Abs(px[o] - px[p]) <= FlatTolerance && Math.Abs(px[o + 1] - px[p + 1]) <= FlatTolerance && Math.Abs(px[o + 2] - px[p + 2]) <= FlatTolerance;
            if (start >= 0 && (!flat || Math.Abs(px[o] - rb) > 8 || Math.Abs(px[o + 1] - rg) > 8 || Math.Abs(px[o + 2] - rr) > 8))
                Close();
            if (!flat) continue;
            int diff = Math.Max(Math.Abs(px[s] - px[o]), Math.Max(Math.Abs(px[s + 1] - px[o + 1]), Math.Abs(px[s + 2] - px[o + 2])));
            if (diff >= EdgeContrast)
            {
                if (start < 0)
                {
                    start = i;
                    rb = px[o]; rg = px[o + 1]; rr = px[o + 2];
                }
                lastHit = i;
                hits++;
                gap = 0;
            }
            else if (start >= 0 && ++gap > 24)
            {
                Close();
            }
        }
        Close();
        return runs;
    }

    /// <summary>
    /// A picture has texture and plenty of colours; a panel, a button or a block of text has big
    /// flat areas and few colours.
    /// </summary>
    private static bool LooksLikeAPicture(byte[] px, int w, Int32Rect r)
    {
        int step = Math.Max(1, (int)Math.Sqrt(r.Width * (double)r.Height / 6000));
        int samples = 0, textured = 0;
        var colours = new HashSet<int>();
        for (int y = r.Y + 1; y < r.Y + r.Height - 1; y += step)
            for (int x = r.X + 1; x < r.X + r.Width - 1; x += step)
            {
                int i = (y * w + x) * 4, j = i - 4;
                samples++;
                if (Math.Abs(px[i] - px[j]) + Math.Abs(px[i + 1] - px[j + 1]) + Math.Abs(px[i + 2] - px[j + 2]) > 6) textured++;
                colours.Add((px[i] >> 3) | (px[i + 1] >> 3 << 5) | (px[i + 2] >> 3 << 10));
            }
        return samples > 0 && textured >= samples * 0.25 && colours.Count >= 48;
    }

    private static bool Same(Int32Rect a, Int32Rect b) =>
        Math.Abs(a.X - b.X) <= 2 && Math.Abs(a.Y - b.Y) <= 2 && Math.Abs(a.Width - b.Width) <= 3 && Math.Abs(a.Height - b.Height) <= 3;

    /// <summary>The smallest found picture under a point, or null.</summary>
    public static FoundImage? At(IReadOnlyList<FoundImage> found, int x, int y)
    {
        FoundImage? best = null;
        foreach (var f in found)
        {
            var b = f.Bounds;
            if (x < b.X || y < b.Y || x >= b.X + b.Width || y >= b.Y + b.Height) continue;
            if (best == null || (long)b.Width * b.Height < (long)best.Value.Bounds.Width * best.Value.Bounds.Height) best = f;
        }
        return best;
    }
}
