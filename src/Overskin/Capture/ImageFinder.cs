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
    private const int MaxRadius = 64;   // the roundest corner recognised
    private const int FlatTolerance = 3;
    private const int GrainTolerance = 7; // an acrylic or noisy background still counts as one colour
    private const int EdgeContrast = 8;

    private readonly record struct Seg(int Pos, int A, int B)
    {
        public int Length => B - A + 1;
    }

    /// <summary>The pictures in a BGRA screenshot, in its pixel coordinates.</summary>
    public static List<FoundImage> Find(byte[] px, int w, int h, CancellationToken ct = default)
    {
        var found = new List<FoundImage>();
#if DEBUG
        LastPlain.Clear();
#endif
        if (w < MinSide + 2 || h < MinSide + 2) return found;
        var sw = System.Diagnostics.Stopwatch.StartNew();

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

        // bottoms by where they start and end, to pair with tops quickly
        var bottomsByStart = new Dictionary<int, List<Seg>>();
        var bottomsByEnd = new Dictionary<int, List<Seg>>();
        for (int y = 1; y < h - 1; y++)
            foreach (var s in bottoms[y])
            {
                if (!bottomsByStart.TryGetValue(s.A, out var l1)) bottomsByStart[s.A] = l1 = new();
                l1.Add(s);
                if (!bottomsByEnd.TryGetValue(s.B, out var l2)) bottomsByEnd[s.B] = l2 = new();
                l2.Add(s);
            }

        var plain = new List<(Int32Rect Bounds, int Radius)>(); // rectangles that looked like panels, not pictures
        var candidates = new List<Seg>();
        var seen = new HashSet<long>();
        for (int y = 1; y < h - 1; y++)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var top in tops[y])
            {
                // bottoms below that line up with it; each corner may be rounded differently (or be
                // hidden under something), so the ends may differ by up to a corner's size
                candidates.Clear();
                seen.Clear();
                int reach = MaxRadius + 4;
                void Take(Dictionary<int, List<Seg>> index, int around)
                {
                    for (int k = around - reach; k <= around + reach; k++)
                    {
                        if (!index.TryGetValue(k, out var list)) continue;
                        foreach (var c in list)
                            if (c.Pos >= top.Pos + MinSide - 1 && seen.Add(((long)c.Pos << 32) | (uint)c.A)) candidates.Add(c);
                    }
                }
                Take(bottomsByStart, top.A);
                Take(bottomsByEnd, top.B);
                candidates.Sort((p, q) => p.Pos.CompareTo(q.Pos));
                foreach (var bot in candidates)
                {
                    // the two line up: either covers most of the other (both may be in pieces)
                    if (Cover(bottoms[bot.Pos], top.A, top.B).Share < 0.6 && Cover(tops[top.Pos], bot.A, bot.B).Share < 0.6) continue;
                    if (Rect(px, tops, bottoms, lefts, rights, top, bot, w) is not { } rect) continue;
                    var bounds = TakeSoftEdges(px, w, h, rect.Bounds, rect.Radius);
                    if (bounds.Width < MinSide || bounds.Height < MinSide) break;
                    if (found.Any(f => Same(f.Bounds, bounds)) || plain.Any(f => Same(f.Bounds, bounds))) break;
                    if (LooksLikeAPicture(px, w, bounds, rect.Radius)) found.Add(new FoundImage(bounds, rect.Radius));
                    else plain.Add((bounds, rect.Radius));
#if DEBUG
                    if (!LooksLikeAPicture(px, w, bounds, rect.Radius)) LastPlain.Add(bounds);
#endif
                    break; // the nearest bottom that closes a rectangle is this top's
                }
            }
        }
        // a plain-looking rectangle can still be a picture of something: a screenshot of a web page
        // with a photo on it, or of an app, posted in a chat. It holds a sizeable picture, or several
        // panels of its own and is a different colour from everything round it (a card on a page is
        // nearly the page's colour)
        var all = found.Select(f => f.Bounds).Concat(plain.Select(p => p.Bounds)).ToList();
        foreach (var (bounds, radius) in plain)
        {
            if (found.Any(f => Same(f.Bounds, bounds))) continue;
            long area = (long)bounds.Width * bounds.Height;
            bool holdsPicture = found.Any(f => Inside(f.Bounds, bounds, 6) && (long)f.Bounds.Width * f.Bounds.Height >= area * 0.12);
            // most of it is a picture, with a bar along one edge (a title bar, a caption strip)
            bool barAndPicture = found.Any(f => Inside(f.Bounds, bounds, -3) && SharedSides(f.Bounds, bounds) >= 3
                                                && (long)f.Bounds.Width * f.Bounds.Height >= area * 0.6);
            bool screenshot = bounds.Width >= 160 && bounds.Height >= 100
                              && all.Count(r => Inside(r, bounds, 4) && !Same(r, bounds)) >= 3
                              && ToneGap(px, w, h, bounds) >= 20;
            if (holdsPicture || barAndPicture || screenshot) found.Add(new FoundImage(bounds, radius));
        }
        found.RemoveAll(r => found.Any(o => !o.Equals(r) && Inside(r.Bounds, o.Bounds, -3) && SharedSides(r.Bounds, o.Bounds) >= 3
                                            && (long)o.Bounds.Width * o.Bounds.Height < (long)r.Bounds.Width * r.Bounds.Height * 1.5));
        EdgeMs = sw.ElapsedMilliseconds;
        FindRound(px, w, h, found, ct);
        RoundMs = sw.ElapsedMilliseconds - EdgeMs;
        return found;
    }

    // ---- round pictures: circles (profile pictures), pills and big round corners ----------------
    //
    // These have short straight edges or none, so they're found another way: everything that
    // isn't flat is grouped into blobs (in 4-pixel blocks, so it's quick), and each blob's outline
    // is checked against a rectangle with round corners of the size its corners show, all the way
    // round, from a square corner to a full circle.

    private const int Block = 4;

    /// <summary>For the self-test: how long the last search's two halves took.</summary>
    internal static long EdgeMs, RoundMs;
#if DEBUG
    internal static readonly List<Int32Rect> LastPlain = new();
#endif

    private static int Diff(byte[] px, int a, int b) =>
        Math.Max(Math.Abs(px[a] - px[b]), Math.Max(Math.Abs(px[a + 1] - px[b + 1]), Math.Abs(px[a + 2] - px[b + 2])));

    private static void FindRound(byte[] px, int w, int h, List<FoundImage> found, CancellationToken ct)
    {
        int bw = (w + Block - 1) / Block, bh = (h + Block - 1) / Block;
        var busy = new bool[bw * bh];
        Parallel.For(1, h - 1, new ParallelOptions { CancellationToken = ct }, y =>
        {
            int row = (y / Block) * bw, up = -w * 4;
            for (int x = 1; x < w; x++)
            {
                int i = (y * w + x) * 4;
                if (Diff(px, i, i - 4) > FlatTolerance || Diff(px, i, i + up) > FlatTolerance) busy[row + x / Block] = true;
            }
        });
        ct.ThrowIfCancellationRequested();

        var label = new int[bw * bh];
        var queue = new int[bw * bh];
        int next = 0;
        for (int start = 0; start < busy.Length; start++)
        {
            if (!busy[start] || label[start] != 0) continue;
            next++;
            int head = 0, tail = 0, count = 0;
            int bx0 = int.MaxValue, by0 = int.MaxValue, bx1 = -1, by1 = -1;
            queue[tail++] = start;
            label[start] = next;
            while (head < tail)
            {
                int c = queue[head++], cx = c % bw, cy = c / bw;
                count++;
                bx0 = Math.Min(bx0, cx); bx1 = Math.Max(bx1, cx); by0 = Math.Min(by0, cy); by1 = Math.Max(by1, cy);
                if (cx > 0 && busy[c - 1] && label[c - 1] == 0) { label[c - 1] = next; queue[tail++] = c - 1; }
                if (cx < bw - 1 && busy[c + 1] && label[c + 1] == 0) { label[c + 1] = next; queue[tail++] = c + 1; }
                if (cy > 0 && busy[c - bw] && label[c - bw] == 0) { label[c - bw] = next; queue[tail++] = c - bw; }
                if (cy < bh - 1 && busy[c + bw] && label[c + bw] == 0) { label[c + bw] = next; queue[tail++] = c + bw; }
            }
            int bwide = bx1 - bx0 + 1, btall = by1 - by0 + 1;
            if (bwide * Block < MinSide || btall * Block < MinSide) continue;
            if (bx0 == 0 || by0 == 0 || bx1 >= bw - 1 || by1 >= bh - 1) continue; // needs background all round
            if (count < bwide * btall * 0.6) continue; // a ring, a line of text, scattered bits
            ct.ThrowIfCancellationRequested();
            if (Outline(px, w, h, label, bw, next, bx0, by0, bx1, by1) is not { } picture) continue;
            if (found.Any(f => Same(f.Bounds, picture.Bounds))) continue;
            if (!LooksLikeAPicture(px, w, picture.Bounds, picture.Radius)) continue;
            found.Add(picture);
        }
    }

    /// <summary>The blob's exact edges, the size of its corners, and whether its outline really is that shape.</summary>
    private static FoundImage? Outline(byte[] px, int w, int h, int[] label, int bw, int id, int bx0, int by0, int bx1, int by1)
    {
        // exact edges: the outermost pixels that differ from their outer neighbour, inside this blob's blocks
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
        for (int y = Math.Max(1, by0 * Block); y < Math.Min(h - 1, (by1 + 1) * Block); y++)
            for (int x = Math.Max(1, bx0 * Block); x < Math.Min(w - 1, (bx1 + 1) * Block); x++)
            {
                if (label[(y / Block) * bw + x / Block] != id) continue;
                int i = (y * w + x) * 4;
                if (x < x0 && Diff(px, i, i - 4) > 1) x0 = x;
                if (x > x1 && Diff(px, i, i + 4) > 1) x1 = x;
                if (y < y0 && Diff(px, i, i - w * 4) > 1) y0 = y;
                if (y > y1 && Diff(px, i, i + w * 4) > 1) y1 = y;
            }
        if (x1 < x0 || y1 < y0) return null;
        int width = x1 - x0 + 1, height = y1 - y0 + 1;
        if (width < MinSide || height < MinSide || x0 < 5 || y0 < 5 || x1 > w - 6 || y1 > h - 6) return null;

        // each corner's roundness, from how far in along the diagonal the picture starts
        int limit = Math.Min(width, height) / 2;
        int Corner(int cx, int cy, int dx, int dy)
        {
            int bg = ((cy - dy * 2) * w + (cx - dx * 2)) * 4;
            for (int k = 0; k <= limit; k++)
            {
                int i = ((cy + dy * k) * w + (cx + dx * k)) * 4;
                if (Diff(px, i, bg) >= 24) return (int)Math.Round(k / (1 - Math.Sqrt(0.5)));
            }
            return -1;
        }
        var radii = new[] { Corner(x0, y0, 1, 1), Corner(x1, y0, -1, 1), Corner(x0, y1, 1, -1), Corner(x1, y1, -1, -1) };
        if (radii.Any(r => r < 0)) return null;
        Array.Sort(radii);
        if (radii[3] - radii[0] > 4 + radii[3] / 5) return null; // corners that don't match
        double radius = Math.Min(limit, (radii[1] + radii[2]) / 2.0);
        if (Math.Abs(width - height) <= 3 && radius >= limit * 0.85) radius = limit; // a circle

        // all the way round: flat just outside, something else just inside
        double left = x0, top = y0, right = x1 + 1, bottom = y1 + 1, rr = radius;
        int samples = 0, good = 0;
        void Probe(double ox, double oy, double nx, double ny)
        {
            int At(double d)
            {
                int x = (int)Math.Floor(ox + nx * d), y = (int)Math.Floor(oy + ny * d);
                return x < 0 || y < 0 || x >= w || y >= h ? -1 : (y * w + x) * 4;
            }
            int o1 = At(2.5), o2 = At(4), inner = At(-3);
            samples++;
            if (o1 < 0 || o2 < 0 || inner < 0) return;
            if (Diff(px, o1, o2) <= 6 && Diff(px, inner, o1) >= EdgeContrast) good++;
        }
        for (int i = 0; i < 24; i++)
        {
            double t = (i + 0.5) / 24;
            if (right - left - 2 * rr > 8)
            {
                double x = left + rr + t * (right - left - 2 * rr);
                Probe(x, top, 0, -1);
                Probe(x, bottom, 0, 1);
            }
            if (bottom - top - 2 * rr > 8)
            {
                double y = top + rr + t * (bottom - top - 2 * rr);
                Probe(left, y, -1, 0);
                Probe(right, y, 1, 0);
            }
            if (rr >= 6)
            {
                double a = t * Math.PI / 2, ca = Math.Cos(a), sa = Math.Sin(a);
                Probe(right - rr + rr * ca, bottom - rr + rr * sa, ca, sa);
                Probe(left + rr - rr * ca, bottom - rr + rr * sa, -ca, sa);
                Probe(left + rr - rr * ca, top + rr - rr * sa, -ca, -sa);
                Probe(right - rr + rr * ca, top + rr - rr * sa, ca, -sa);
            }
        }
        if (samples < 24 || good < samples * 0.8) return null;
        return new FoundImage(new Int32Rect(x0, y0, width, height), (int)Math.Round(radius));
    }


    /// <summary>How much of [from, to] the edges on one line cover (0 to 1), and where they start and end.</summary>
    private static (double Share, int First, int Last) Cover(List<Seg>? line, int from, int to)
    {
        if (line == null || to < from) return (0, int.MaxValue, int.MinValue);
        int covered = 0, first = int.MaxValue, last = int.MinValue;
        foreach (var s in line)
        {
            int a = Math.Max(s.A, from), b = Math.Min(s.B, to);
            if (b < a) continue;
            covered += b - a + 1;
            first = Math.Min(first, a);
            last = Math.Max(last, b);
        }
        return (covered / (double)(to - from + 1), first, last);
    }

    /// <summary>
    /// The rectangle a top and a bottom edge make with the best left and right edges near their
    /// ends. Each side has to show for a good part of its length, but a corner may be rounded
    /// differently from the others, or hidden under something (a toolbar, a badge) that sticks out.
    /// </summary>
    private static (Int32Rect Bounds, int Radius)? Rect(byte[] px, List<Seg>[] tops, List<Seg>[] bottoms, List<Seg>[] lefts, List<Seg>[] rights, Seg top, Seg bot, int w)
    {
        int y0 = top.Pos, y1 = bot.Pos;
        int innerL = Math.Min(top.A, bot.A), innerR = Math.Max(top.B, bot.B);

        (int X, double Share) Best(List<Seg>[] columns, int from, int inner, int step)
        {
            // the nearest edge out from the corners that covers most of the side (then the best of
            // the next few, for a border drawn as two lines); not something further out that lines up
            int bestX = -1, stopK = int.MaxValue;
            double best = 0;
            int startK = -Math.Abs(inner - from) - 2;
            for (int k = startK; k <= MaxRadius + 2 && k <= stopK; k++)
            {
                int x = from + step * k;
                if (x <= 0 || x >= w - 1) continue;
                double share = Cover(columns[x], y0, y1).Share;
                if (share > best + 0.02) { best = share; bestX = x; }
                if (best >= 0.6 && stopK == int.MaxValue) stopK = k + 3;
            }
            return (bestX, best);
        }
        // from where the later-starting edge starts out past the earlier one: a header's edge may run
        // the whole width above a picture that starts further in
        var (xl, sl) = Best(lefts, innerL, Math.Max(top.A, bot.A), -1);
        var (xr, sr) = Best(rights, innerR, Math.Min(top.B, bot.B), 1);
        if (xl < 0 || xr < 0 || xr - xl + 1 < MinSide) return null;
        var ct = Cover(tops[y0], xl, xr);
        var cb = Cover(bottoms[y1], xl, xr);
        var cl = Cover(lefts[xl], y0, y1);
        var cr = Cover(rights[xr], y0, y1);
        double[] shares = { ct.Share, cb.Share, sl, sr };
        if (shares.Min() < 0.4 || shares.Sum() < 2.7) return null;

        // a side that runs on well past a corner belongs to something bigger; then this corner is
        // only real if the other edge comes right up to that side (a picture flush in a card's corner)
        var rl = Reach(lefts[xl], y0, y1);
        var rr = Reach(rights[xr], y0, y1);
        const int runOn = 4;
        bool Open(int sideRunsOn, int gapToSide) => sideRunsOn > runOn && gapToSide > 3;
        if (Open(y0 - rl.First, ct.First - xl) || Open(y0 - rr.First, xr - ct.Last) ||
            Open(rl.Last - y1, cb.First - xl) || Open(rr.Last - y1, xr - cb.Last)) return null;
        // and the same the other way: a top or bottom running on past a side that stops short of it
        var rt = Reach(tops[y0], xl, xr);
        var rb = Reach(bottoms[y1], xl, xr);
        if (Open(xl - rt.First, cl.First - y0) || Open(rt.Last - xr, cr.First - y0) ||
            Open(xl - rb.First, y1 - cl.Last) || Open(rb.Last - xr, y1 - cr.Last)) return null;

        // sides running on past a top or bottom that is one flat colour along its inside: that's a
        // band inside something bigger (a caption strip, the page under a browser bar), not an edge
        int m = Math.Min((xr - xl) / 4, MaxRadius / 2 + 2);
        if ((y0 - rl.First > runOn || y0 - rr.First > runOn) && FlatAlong(px, w, y0 + 2, xl + m, xr - m)) return null;
        if ((rl.Last - y1 > runOn || rr.Last - y1 > runOn) && FlatAlong(px, w, y1 - 2, xl + m, xr - m)) return null;

        // each corner's roundness, from how far in its two edges start; a corner whose two edges
        // disagree is hidden under something, and doesn't count
        var radii = new List<int>();
        void Corner(int alongX, int alongY)
        {
            if (alongX < 0 || alongY < 0) return;
            if (Math.Abs(alongX - alongY) > 2 + Math.Max(alongX, alongY) / 3) return;
            int seen = Math.Max(alongX, alongY);
            radii.Add(seen == 0 ? 0 : (int)Math.Round(Math.Pow((1 + Math.Sqrt(1 + 4.0 * seen)) / 2, 2) + 1));
        }
        Corner(ct.First - xl, cl.First - y0);
        Corner(xr - ct.Last, cr.First - y0);
        Corner(cb.First - xl, y1 - cl.Last);
        Corner(xr - cb.Last, y1 - cr.Last);
        if (radii.Count < 2) return null;
        // the least round corner: a mask that size never cuts into the picture
        return (new Int32Rect(xl, y0, xr - xl + 1, y1 - y0 + 1), radii.Min());
    }

    /// <summary>Where the edges on a line that touch [from, to] really start and end, beyond it too.</summary>
    private static (int First, int Last) Reach(List<Seg>? line, int from, int to)
    {
        int first = from, last = to;
        if (line == null) return (first, last);
        foreach (var s in line)
        {
            if (s.B < from || s.A > to) continue;
            first = Math.Min(first, s.A);
            last = Math.Max(last, s.B);
        }
        return (first, last);
    }

    private static int SharedSides(Int32Rect a, Int32Rect b)
    {
        int n = 0;
        if (Math.Abs(a.X - b.X) <= 3) n++;
        if (Math.Abs(a.Y - b.Y) <= 3) n++;
        if (Math.Abs(a.X + a.Width - b.X - b.Width) <= 3) n++;
        if (Math.Abs(a.Y + a.Height - b.Y - b.Height) <= 3) n++;
        return n;
    }

    /// <summary>Whether a row is (nearly) one colour from x0 to x1.</summary>
    private static bool FlatAlong(byte[] px, int w, int y, int x0, int x1)
    {
        if (x1 - x0 < 8) return false;
        var counts = new Dictionary<int, int>();
        for (int x = x0; x <= x1; x++)
        {
            int i = (y * w + x) * 4;
            int key = (px[i] >> 4) | (px[i + 1] >> 4 << 4) | (px[i + 2] >> 4 << 8);
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        return counts.Values.Max() >= (x1 - x0 + 1) * 0.9;
    }

    /// <summary>
    /// How far apart the commonest colour just inside a rectangle and the commonest colour just
    /// outside it are.
    /// </summary>
    private static int ToneGap(byte[] px, int w, int h, Int32Rect r)
    {
        int Commonest(int inset)
        {
            var counts = new Dictionary<int, int>();
            void Add(int x, int y)
            {
                if (x < 0 || y < 0 || x >= w || y >= h) return;
                int i = (y * w + x) * 4;
                int key = (px[i] >> 3) | (px[i + 1] >> 3 << 5) | (px[i + 2] >> 3 << 10);
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
            int x0 = r.X + inset, y0 = r.Y + inset, x1 = r.X + r.Width - 1 - inset, y1 = r.Y + r.Height - 1 - inset;
            for (int x = x0; x <= x1; x += 2) { Add(x, y0); Add(x, y1); }
            for (int y = y0; y <= y1; y += 2) { Add(x0, y); Add(x1, y); }
            return counts.Count == 0 ? -1 : counts.MaxBy(kv => kv.Value).Key;
        }
        int a = Commonest(12), b = Commonest(-4);
        if (a < 0 || b < 0) return 0;
        int Ch(int key, int shift) => ((key >> shift) & 31) << 3;
        return Math.Max(Math.Abs(Ch(a, 0) - Ch(b, 0)), Math.Max(Math.Abs(Ch(a, 5) - Ch(b, 5)), Math.Abs(Ch(a, 10) - Ch(b, 10))));
    }

    private static bool Inside(Int32Rect inner, Int32Rect outer, int margin) =>
        inner.X >= outer.X + margin && inner.Y >= outer.Y + margin &&
        inner.X + inner.Width <= outer.X + outer.Width - margin && inner.Y + inner.Height <= outer.Y + outer.Height - margin;

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
            Math.Abs(px[o] - px[p]) <= GrainTolerance && Math.Abs(px[o + 1] - px[p + 1]) <= GrainTolerance && Math.Abs(px[o + 2] - px[p + 2]) <= GrainTolerance;
        int Diff(int a, int b) => Math.Max(Math.Abs(px[a] - px[b]), Math.Max(Math.Abs(px[a + 1] - px[b + 1]), Math.Abs(px[a + 2] - px[b + 2])));

        int start = -1, lastHit = -1, hits = 0, gap = 0, same = 0, rb = 0, rg = 0, rr = 0;
        void Close()
        {
            int length = lastHit - start + 1;
            if (start >= 0 && length >= MinSide / 2 && hits >= length * 0.55)
                runs.Add(new Seg(line, start, lastHit));
            start = -1;
            hits = 0;
            gap = 0;
            same = 0;
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
            // a gradient background drifts slowly along the edge: follow it, but a jump is another background
            if (start >= 0) { rb = px[o]; rg = px[o + 1]; rr = px[o + 2]; }
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
                same = 0;
            }
            else if (start >= 0 && (++gap > 40 || (Diff(s, o) <= GrainTolerance && ++same >= 3)))
            {
                // a gap, or a stretch where the line is the background itself: nothing is there
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
