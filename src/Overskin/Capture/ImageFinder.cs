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
                bounds = TakeSoftEdges(px, w, h, bounds, sides.Radius);
                if (!LooksLikeAPicture(px, w, bounds, sides.Radius)) continue;
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
                // starts about r down from the top and ends about r up from the bottom; it may be in
                // pieces where something sits right beside the picture, but they cover most of it
                int slack = 2 + r / 3, from0 = top.Pos + r, to0 = bottom.Pos - r;
                int first = int.MaxValue, last = int.MinValue, covered = 0;
                foreach (var s in columns[x])
                {
                    if (s.B < from0 - slack || s.A > to0 + slack) continue;
                    // a piece running on past the corner is something beside the picture lining up with it
                    first = Math.Min(first, Math.Max(s.A, from0));
                    last = Math.Max(last, Math.Min(s.B, to0));
                    covered += Math.Min(s.B, to0) - Math.Max(s.A, from0) + 1;
                }
                if (first != int.MaxValue && Math.Abs(first - from0) <= slack && Math.Abs(last - to0) <= slack && covered >= (to0 - from0 + 1) * 0.6)
                {
                    radius = r;
                    return x;
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
    /// mostly differs from it. A picture placed half a pixel off has a soft first line, a blend of
    /// the background and the picture; then the flat background is one line further out.
    /// </summary>
    private static List<Seg> Runs(byte[] px, int w, int h, int line, int outside, bool horizontal)
    {
        var runs = new List<Seg>();
        int n = horizontal ? w : h, limit = horizontal ? h : w;
        int further = outside + (outside - line);
        bool canSkip = further >= 0 && further < limit;
        int Index(int along, int across) => horizontal ? (across * w + along) * 4 : (along * w + across) * 4;
        bool Flat(int o, int p) =>
            Math.Abs(px[o] - px[p]) <= FlatTolerance && Math.Abs(px[o + 1] - px[p + 1]) <= FlatTolerance && Math.Abs(px[o + 2] - px[p + 2]) <= FlatTolerance;
        int Diff(int a, int b) => Math.Max(Math.Abs(px[a] - px[b]), Math.Max(Math.Abs(px[a + 1] - px[b + 1]), Math.Abs(px[a + 2] - px[b + 2])));

        int start = -1, lastHit = -1, hits = 0, gap = 0, rb = 0, rg = 0, rr = 0;
        void Close()
        {
            int length = lastHit - start + 1;
            if (start >= 0 && length >= MinSide / 2 && hits >= length * 0.55)
                runs.Add(new Seg(line, start, lastHit));
            start = -1;
            hits = 0;
            gap = 0;
        }
        for (int i = 1; i < n - 1; i++)
        {
            int s = Index(i, line);
            int o = Index(i, outside);
            bool flat = Flat(o, Index(i - 1, outside));
            if (!flat && canSkip)
            {
                // a soft first line: the background is one further out, and this line sits between the two
                int o2 = Index(i, further);
                if (Flat(o2, Index(i - 1, further)) && Diff(o, o2) <= Diff(s, o2) && Diff(s, o) <= Diff(s, o2))
                {
                    o = o2;
                    flat = true;
                }
            }
            if (start >= 0 && (!flat || Math.Abs(px[o] - rb) > 8 || Math.Abs(px[o + 1] - rg) > 8 || Math.Abs(px[o + 2] - rr) > 8))
                Close();
            if (!flat) continue;
            if (Diff(s, o) >= EdgeContrast)
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
            else if (start >= 0 && ++gap > 40)
            {
                Close();
            }
        }
        Close();
        return runs;
    }

    /// <summary>
    /// A picture placed half a pixel off has a soft line around it, part picture and part
    /// background; it's taken in too, so nothing of the picture is cut off.
    /// </summary>
    private static Int32Rect TakeSoftEdges(byte[] px, int w, int h, Int32Rect r, int radius)
    {
        int Diff(int a, int b) => Math.Max(Math.Abs(px[a] - px[b]), Math.Max(Math.Abs(px[a + 1] - px[b + 1]), Math.Abs(px[a + 2] - px[b + 2])));
        bool Soft(Func<int, int> line, Func<int, int> beyond, int from, int to)
        {
            int n = 0, soft = 0;
            for (int i = from; i < to; i++) { n++; if (Diff(line(i), beyond(i)) >= 6) soft++; }
            return n > 8 && soft >= n * 0.5;
        }
        int x0 = r.X, y0 = r.Y, x1 = r.X + r.Width - 1, y1 = r.Y + r.Height - 1, k = radius + 2;
        bool top = y0 >= 2 && Soft(x => ((y0 - 1) * w + x) * 4, x => ((y0 - 2) * w + x) * 4, x0 + k, x1 - k);
        bool bottom = y1 + 2 < h && Soft(x => ((y1 + 1) * w + x) * 4, x => ((y1 + 2) * w + x) * 4, x0 + k, x1 - k);
        bool left = x0 >= 2 && Soft(y => (y * w + x0 - 1) * 4, y => (y * w + x0 - 2) * 4, y0 + k, y1 - k);
        bool right = x1 + 2 < w && Soft(y => (y * w + x1 + 1) * 4, y => (y * w + x1 + 2) * 4, y0 + k, y1 - k);
        if (top) y0--;
        if (bottom) y1++;
        if (left) x0--;
        if (right) x1++;
        return new Int32Rect(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    /// <summary>
    /// A photo has texture and plenty of colours. A meme, a cartoon or a screenshot can be flat
    /// in places, but what's along its edges changes as you go round; a panel, a button or a box
    /// of text has the same plain colour all along the inside of its edges.
    /// </summary>
    private static bool LooksLikeAPicture(byte[] px, int w, Int32Rect r, int radius)
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
        if (samples == 0) return false;
        if (textured >= samples * 0.25 && colours.Count >= 48) return true;
        if (colours.Count < 8) return false;

        // how many sides change colour along their inside
        int skip = radius + 3, varied = 0;
        bool Varies(IEnumerable<int> offsets)
        {
            var list = offsets.ToList();
            if (list.Count < 8) return false;
            var counts = new Dictionary<int, int>();
            foreach (int i in list)
            {
                int key = (px[i] >> 4) | (px[i + 1] >> 4 << 4) | (px[i + 2] >> 4 << 8);
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
            return counts.Values.Max() <= list.Count * 0.75;
        }
        int top = r.Y + 2, bottom = r.Y + r.Height - 3, left = r.X + 2, right = r.X + r.Width - 3;
        var xs = Enumerable.Range(r.X + skip, Math.Max(0, r.Width - 2 * skip));
        var ys = Enumerable.Range(r.Y + skip, Math.Max(0, r.Height - 2 * skip));
        if (Varies(xs.Select(x => (top * w + x) * 4))) varied++;
        if (Varies(xs.Select(x => (bottom * w + x) * 4))) varied++;
        if (Varies(ys.Select(y => (y * w + left) * 4))) varied++;
        if (Varies(ys.Select(y => (y * w + right) * 4))) varied++;
        return varied >= 2;
    }

    private static bool Same(Int32Rect a, Int32Rect b) =>
        Math.Abs(a.X - b.X) <= 2 && Math.Abs(a.Y - b.Y) <= 2 && Math.Abs(a.Width - b.Width) <= 3 && Math.Abs(a.Height - b.Height) <= 3;

#if DEBUG
    /// <summary>For the self-test: the edges found near a rectangle's four sides.</summary>
    internal static string EdgesNear(byte[] px, int w, int h, Int32Rect r)
    {
        string Show(string name, IEnumerable<Seg> segs) => name + "[" + string.Join(" ", segs.Select(s => $"{s.Pos}:{s.A}-{s.B}")) + "]";
        var tops = Enumerable.Range(r.Y - 2, 5).SelectMany(y => Runs(px, w, h, y, y - 1, true)).Where(s => s.B > r.X && s.A < r.X + r.Width);
        var bottoms = Enumerable.Range(r.Y + r.Height - 3, 5).SelectMany(y => Runs(px, w, h, y, y + 1, true)).Where(s => s.B > r.X && s.A < r.X + r.Width);
        var lefts = Enumerable.Range(r.X - 2, 5).SelectMany(x => Runs(px, w, h, x, x - 1, false)).Where(s => s.B > r.Y && s.A < r.Y + r.Height);
        var rights = Enumerable.Range(r.X + r.Width - 3, 5).SelectMany(x => Runs(px, w, h, x, x + 1, false)).Where(s => s.B > r.Y && s.A < r.Y + r.Height);
        return Show("top", tops) + " " + Show("bottom", bottoms) + " " + Show("left", lefts) + " " + Show("right", rights);
    }
#endif

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
