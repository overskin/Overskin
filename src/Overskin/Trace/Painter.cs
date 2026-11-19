using System.Windows;
using System.Windows.Media;

namespace Overskin;

/// <summary>
/// Paints a picture the way a person would in Gartic Phone: no bucket, just the game's five brush
/// sizes and its opacity levels. It sketches the picture's dark outlines first, then colours in
/// from the biggest brush to the smallest, never painting over the outlines. Strokes are
/// continuous and rounded (no dots). Each pass lays
/// rows of strokes over the areas that are still wrong, choosing for each area the colour and
/// opacity that bring the canvas closest to the picture (red at low opacity over white gives
/// pink, so far more shades are possible than the palette has). Every stroke is painted onto a
/// simulated canvas as it is planned, so later, smaller brushes correct exactly what the bigger
/// ones left, and the preview shows what the game will end up with.
/// </summary>
public static class Painter
{
    /// <summary>A brush, a colour and an opacity: one setting of the game's controls.</summary>
    private readonly record struct Combo(int Colour, int Level, float R, float G, float B, float A, double Luma);

    /// <summary>Brush diameters to use before the real ones have been measured: a guess from the canvas size.</summary>
    public static double[] DefaultSizes(int w, int h)
    {
        double side = Math.Min(w, h);
        return new[] { 0.006, 0.014, 0.028, 0.05, 0.085 }.Select(k => Math.Max(1.5, side * k)).ToArray();
    }

    public static TraceResult Paint(TraceSource src, TraceOptions o, int w, int h, CancellationToken ct)
    {
        var palette = o.Palette;
        var colours = palette.Select(p => p.Color).ToArray();
        var usable = Enumerable.Range(0, palette.Count).Where(k => !palette[k].Skip).ToList();
        if (usable.Count == 0)
        {
            return new TraceResult
            {
                Width = w, Height = h, Paths = new(), ColourIndex = new(), Colours = colours, Backdrop = new byte[w * h * 4],
                Message = palette.Count == 0
                    ? "Pick the game's colours first: Pick from screen, then click each swatch."
                    : "Every colour is turned off. Turn some back on.",
            };
        }

        var sizes = (o.BrushSizes.Count > 0 ? o.BrushSizes.ToArray() : DefaultSizes(w, h)).Select(s => Math.Max(1.0, s)).ToArray();
        int levels = Math.Clamp(o.OpacityLevels, 1, 20);
        double tolerance = Math.Max(2, o.Tolerance2);

        // the picture, at working size
        var target = new byte[3][];
        for (int c = 0; c < 3; c++)
        {
            var plane = new byte[src.Width * src.Height];
            for (int i = 0; i < plane.Length; i++) plane[i] = src.Rgb[i * 3 + c];
            target[c] = TraceEngine.Resample(plane, src.Width, src.Height, w, h);
        }
        ct.ThrowIfCancellationRequested();

        // the game's canvas starts white
        var canvas = new float[3][];
        for (int c = 0; c < 3; c++) canvas[c] = Enumerable.Repeat(255f, w * h).ToArray();

        // every colour at every opacity
        var combos = new List<Combo>();
        foreach (int k in usable)
        {
            var col = colours[k];
            for (int j = 1; j <= levels; j++)
                combos.Add(new Combo(k, j, col.R, col.G, col.B, j / (float)levels, TraceEngine.Luma(col)));
        }

        var paths = new List<Point[]>();
        var colourIndex = new List<int>();
        var sizeIndex = new List<int>();
        var opacityIndex = new List<int>();
        var stamp = new int[w * h];
        int strokeId = 0;

        // ---- outlines first: the picture's thin dark lines, as smooth strokes with a small brush ----
        // what the fills keep off: the middle half of each outline, so colour comes right up to the
        // line (at most nibbling its edge) instead of leaving a white gap beside it
        var core = new int[w * h];
        var scratch = new float[3][] { new float[w * h], new float[w * h], new float[w * h] };
        foreach (var (path, comboIndex, si) in OutlineStrokes(target, w, h, sizes, combos, levels, ct))
        {
            Simulate(canvas, stamp, ++strokeId, w, h, path, sizes[si] / 2, combos[comboIndex]);
            Simulate(scratch, core, strokeId, w, h, path, Math.Max(0.75, sizes[si] / 4), combos[comboIndex]);
            paths.Add(path);
            colourIndex.Add(combos[comboIndex].Colour);
            sizeIndex.Add(si);
            opacityIndex.Add(combos[comboIndex].Level);
        }
        var protect = new bool[w * h];
        for (int i = 0; i < protect.Length; i++) protect[i] = core[i] != 0;
        var guard = new Integral(protect, w, h);
        double smallest = sizes.Min();

        // biggest brush first: it blocks the picture in, the smaller ones only fix what's still off
        foreach (int si in Enumerable.Range(0, sizes.Length).OrderByDescending(i => sizes[i]))
        {
            ct.ThrowIfCancellationRequested();
            double d = sizes[si], step = Math.Max(1, d * 0.92);
            // a small brush takes many strokes to cover anything, so it only fixes what is clearly wrong
            double layerTolerance = tolerance * Math.Sqrt(sizes.Max() / d);
            // compare against the picture softened to this brush's scale, so a big brush isn't asked for detail it can't give
            int soften = (int)Math.Round(d / 3);
            var reference = soften > 0 ? target.Select(p => TraceEngine.Blur(p, w, h, soften)).ToArray() : target;

            int rows = (int)Math.Ceiling(h / step), cols = (int)Math.Ceiling(w / step);
            var choice = new int[rows * cols];
            Array.Fill(choice, -1);
            for (int r = 0; r < rows; r++)
            {
                int y0 = (int)(r * step), y1 = Math.Min(h, (int)((r + 1) * step));
                if (y1 <= y0) continue;
                for (int c = 0; c < cols; c++)
                {
                    int x0 = (int)(c * step), x1 = Math.Min(w, (int)((c + 1) * step));
                    if (x1 <= x0) continue;
                {
                    // the outlines are already right: a stroke here would paint over them (a stroke
                    // along this row covers about the cell itself)
                    double reach = 0;
                    if (guard.Any(Math.Max(0, (int)(x0 - reach)), Math.Max(0, (int)(y0 - reach)), Math.Min(w, (int)Math.Ceiling(x1 + reach)), Math.Min(h, (int)Math.Ceiling(y1 + reach))))
                        continue;
                    // keep the neighbour's setting when it's nearly as good: longer strokes, fewer ends showing
                    int left = c > 0 ? choice[r * cols + c - 1] : -1, above = r > 0 ? choice[(r - 1) * cols + c] : -1;
                    choice[r * cols + c] = Choose(canvas, reference, w, x0, y0, x1, y1, combos, layerTolerance, left, above);
                }
                }
            }

            // a lone cell or two would be a dot: leave those to a smaller brush (or out, for the smallest)
            DropSpecks(choice, rows, cols, d <= smallest + 1e-6 ? 2 : 3);
            // rows of same-setting cells become strokes, joined into zig-zags where they line up,
            // with the turns rounded off like a hand going back and forth
            var planned = Strokes(choice, rows, cols, step, d, w, h)
                .Select(x => (Path: Dash(RoundCorners(x.Path, step * 0.5), d, w), x.Combo)).ToList();
            // lighter settings first, so darker detail lands on top
            planned.Sort((a, b) =>
            {
                var ca = combos[a.Combo];
                var cb = combos[b.Combo];
                int byLuma = cb.Luma.CompareTo(ca.Luma);
                return byLuma != 0 ? byLuma : ca.Level.CompareTo(cb.Level);
            });
            foreach (var (path, comboIndex) in planned)
            {
                var combo = combos[comboIndex];
                Simulate(canvas, stamp, ++strokeId, w, h, path, d / 2, combo);
                paths.Add(path);
                colourIndex.Add(combo.Colour);
                sizeIndex.Add(si);
                opacityIndex.Add(combo.Level);
            }
        }

        // what the game should end up showing
        var painted = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            painted[i * 4] = (byte)Math.Clamp(canvas[2][i] + 0.5f, 0, 255);
            painted[i * 4 + 1] = (byte)Math.Clamp(canvas[1][i] + 0.5f, 0, 255);
            painted[i * 4 + 2] = (byte)Math.Clamp(canvas[0][i] + 0.5f, 0, 255);
            painted[i * 4 + 3] = 255;
        }

        return new TraceResult
        {
            Width = w,
            Height = h,
            Paths = paths,
            ColourIndex = colourIndex,
            SizeIndex = sizeIndex,
            OpacityIndex = opacityIndex,
            Colours = colours,
            Backdrop = painted,
            Painted = painted,
            PointCount = paths.Sum(p => p.Length),
            Message = paths.Count > 0 ? null : "Nothing to paint: the picture already matches a blank canvas.",
        };
    }

    /// <summary>
    /// The best setting for one cell, or -1 to leave it: only worth painting when the cell is
    /// clearly wrong and a flat coat of some colour and opacity makes it clearly better.
    /// </summary>
    private static int Choose(float[][] canvas, byte[][] reference, int w, int x0, int y0, int x1, int y1, List<Combo> combos, double tolerance, int left = -1, int above = -1)
    {
        int n = (x1 - x0) * (y1 - y0);
        double cr = 0, cg = 0, cb = 0, rr = 0, rg = 0, rb = 0, wrong = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = y * w + x;
                float pr = canvas[0][i], pg = canvas[1][i], pb = canvas[2][i];
                byte qr = reference[0][i], qg = reference[1][i], qb = reference[2][i];
                cr += pr; cg += pg; cb += pb;
                rr += qr; rg += qg; rb += qb;
                wrong += Distance(pr, pg, pb, qr, qg, qb);
            }
        wrong /= n;
        if (wrong < tolerance) return -1;
        cr /= n; cg /= n; cb /= n; rr /= n; rg /= n; rb /= n;

        // how much the picture varies inside the cell: no flat coat can fix that part
        double texture = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = y * w + x;
                texture += Distance(reference[0][i], reference[1][i], reference[2][i], rr, rg, rb);
            }
        texture /= n;

        double After(int k)
        {
            var c = combos[k];
            double a = c.A;
            double nr = cr + (c.R - cr) * a, ng = cg + (c.G - cg) * a, nb = cb + (c.B - cb) * a;
            return Distance(nr, ng, nb, rr, rg, rb);
        }
        int best = -1;
        double bestD = double.MaxValue;
        for (int k = 0; k < combos.Count; k++)
        {
            double dist = After(k);
            if (dist < bestD) { bestD = dist; best = k; }
        }
        // a neighbour's setting that's nearly as good wins: it joins that stroke instead of starting a new one
        double slack = tolerance * 0.35;
        foreach (int k in new[] { left, above })
        {
            if (k < 0 || k == best) continue;
            double dist = After(k);
            if (dist <= bestD + slack) { best = k; bestD = dist; break; }
        }
        double after = bestD + texture * 0.8;
        return wrong - after > tolerance * 0.3 ? best : -1;
    }

    /// <summary>"Redmean" weighted RGB distance: close to how different two colours look.</summary>
    internal static double Distance(double r1, double g1, double b1, double r2, double g2, double b2)
    {
        double rm = (r1 + r2) / 2, dr = r1 - r2, dg = g1 - g2, db = b1 - b2;
        return Math.Sqrt((2 + rm / 256) * dr * dr + 4 * dg * dg + (2 + (255 - rm) / 256) * db * db);
    }

    /// <summary>
    /// Turns the grid of choices into strokes: each run of same-setting cells in a row is a
    /// straight stroke along the row's middle (rows sit one brush apart, so they touch without
    /// overlapping), and runs in the next row whose ends line up continue the same stroke as a
    /// zig-zag, which saves lifting the pen.
    /// </summary>
    private static List<(Point[] Path, int Combo)> Strokes(int[] choice, int rows, int cols, double step, double d, int w, int h)
    {
        var runs = new List<(int C0, int C1, int Combo)>[rows];
        for (int r = 0; r < rows; r++)
        {
            runs[r] = new();
            int c = 0;
            while (c < cols)
            {
                int k = choice[r * cols + c];
                if (k < 0) { c++; continue; }
                int start = c;
                while (c < cols && choice[r * cols + c] == k) c++;
                runs[r].Add((start, c - 1, k));
            }
        }

        double radius = d / 2;
        (double A, double B) Ends((int C0, int C1, int Combo) run)
        {
            double a = run.C0 * step + radius, b = (run.C1 + 1) * step - radius;
            if (b < a) a = b = (run.C0 * step + (run.C1 + 1) * step) / 2;
            return (Math.Clamp(a, 0, w - 1), Math.Clamp(b, 0, w - 1));
        }
        double RowY(int r) => Math.Clamp(r * step + step / 2, 0, h - 1);

        var used = runs.Select(list => new bool[list.Count]).ToArray();
        var strokes = new List<(Point[], int)>();
        for (int r = 0; r < rows; r++)
        {
            for (int i = 0; i < runs[r].Count; i++)
            {
                if (used[r][i]) continue;
                used[r][i] = true;
                var run = runs[r][i];
                var (a, b) = Ends(run);
                var points = new List<Point> { new(a, RowY(r)) };
                if (b > a) points.Add(new Point(b, RowY(r)));
                double penX = b;
                bool rightward = true;
                int row = r;
                while (row + 1 < rows)
                {
                    // the next row's run of the same setting whose near end is under the pen
                    int found = -1;
                    for (int j = 0; j < runs[row + 1].Count; j++)
                    {
                        if (used[row + 1][j] || runs[row + 1][j].Combo != run.Combo) continue;
                        var (na, nb) = Ends(runs[row + 1][j]);
                        double near = rightward ? nb : na;
                        if (Math.Abs(near - penX) <= step) { found = j; break; }
                    }
                    if (found < 0) break;
                    row++;
                    used[row][found] = true;
                    var (fa, fb) = Ends(runs[row][found]);
                    rightward = !rightward;
                    // came in at the right end: go back leftwards, and the other way round
                    if (!rightward) { points.Add(new Point(fb, RowY(row))); if (fa < fb) points.Add(new Point(fa, RowY(row))); penX = fa; }
                    else { points.Add(new Point(fa, RowY(row))); if (fb > fa) points.Add(new Point(fb, RowY(row))); penX = fb; }
                }
                strokes.Add((points.ToArray(), run.Combo));
            }
        }
        return strokes;
    }

    /// <summary>
    /// The picture's thin dark lines (a cartoon's outlines) as smooth strokes, with the brush that
    /// matches their thickness and the colour closest to theirs. Nothing for photos or pictures
    /// without line art, or when the palette has no dark colour.
    /// </summary>
    private static List<(Point[] Path, int Combo, int Size)> OutlineStrokes(byte[][] target, int w, int h, double[] sizes, List<Combo> combos, int levels, CancellationToken ct)
    {
        var found = new List<(Point[] Path, int Combo, int Size)>();
        var full = Enumerable.Range(0, combos.Count).Where(k => combos[k].Level == levels && combos[k].Luma < 110).ToList();
        if (full.Count == 0) return found;

        var dark = new bool[w * h];
        int darkCount = 0;
        for (int i = 0; i < dark.Length; i++)
        {
            double luma = 0.299 * target[0][i] + 0.587 * target[1][i] + 0.114 * target[2][i];
            if (luma < 85) { dark[i] = true; darkCount++; }
        }
        // mostly dark (a photo at night, a black poster): no line art to speak of
        if (darkCount == 0 || darkCount > dark.Length * 0.35) return found;

        var ordered = Enumerable.Range(0, sizes.Length).OrderBy(i => sizes[i]).ToArray();
        // lines no thicker than the third brush; anything thicker is an area, coloured in later
        double maxHalf = sizes[ordered[Math.Min(2, ordered.Length - 1)]] * 0.75;
        var inside = TraceEngine.InsideDistance(dark, w, h);
        var skeleton = TraceEngine.Thin(dark, w, h, ct);
        var widths = new List<float>();
        for (int i = 0; i < skeleton.Length; i++)
        {
            if (!skeleton[i]) continue;
            if (inside[i] > maxHalf) skeleton[i] = false;
            else widths.Add(inside[i]);
        }
        if (widths.Count == 0) return found;
        widths.Sort();
        double thickness = 2 * widths[widths.Count / 2];
        int brush = ordered[0];
        foreach (int si in ordered)
            if (sizes[si] <= thickness * 1.15) brush = si;

        // the colour the lines really are
        double r = 0, g = 0, b = 0;
        int n = 0;
        for (int i = 0; i < skeleton.Length; i++)
            if (skeleton[i]) { r += target[0][i]; g += target[1][i]; b += target[2][i]; n++; }
        r /= n; g /= n; b /= n;
        int combo = full.OrderBy(k => Distance(combos[k].R, combos[k].G, combos[k].B, r, g, b)).First();

        double minLength = Math.Max(5, sizes[brush] * 2);
        TraceEngine.RemoveStairs(skeleton, w, h);
        foreach (var raw in TraceEngine.JoinStraight(TraceEngine.WalkSkeleton(skeleton, w, h, 2, ct), ct))
        {
            var smooth = Smooth(raw, 2);
            var simple = TraceEngine.SimplifyOpen(smooth, 0.7);
            var curve = RoundCorners(simple, Math.Max(2, sizes[brush]));
            if (Length(curve) >= minLength) found.Add((curve, combo, brush));
        }
        // nearest next, so the pen hops a short way between lines
        return found.Count < 2 ? found : Nearest(found);
    }

    private static List<(Point[] Path, int Combo, int Size)> Nearest(List<(Point[] Path, int Combo, int Size)> strokes)
    {
        var left = new List<(Point[] Path, int Combo, int Size)>(strokes);
        var order = new List<(Point[] Path, int Combo, int Size)>();
        var at = new Point(0, 0);
        while (left.Count > 0)
        {
            int best = 0;
            bool flip = false;
            double bestD = double.MaxValue;
            for (int i = 0; i < left.Count; i++)
            {
                var p = left[i].Path;
                double a = (p[0] - at).LengthSquared, z = (p[^1] - at).LengthSquared;
                if (a < bestD) { bestD = a; best = i; flip = false; }
                if (z < bestD) { bestD = z; best = i; flip = true; }
            }
            var s = left[best];
            left.RemoveAt(best);
            var path = flip ? Enumerable.Reverse(s.Path).ToArray() : s.Path;
            order.Add((path, s.Combo, s.Size));
            at = path[^1];
        }
        return order;
    }

    /// <summary>Averages each point with its neighbours (ends stay put), taking the pixel stairs out of a traced line.</summary>
    private static Point[] Smooth(Point[] p, int radius)
    {
        if (p.Length < 3) return p;
        var o = new Point[p.Length];
        for (int i = 0; i < p.Length; i++)
        {
            int r = Math.Min(radius, Math.Min(i, p.Length - 1 - i));
            double x = 0, y = 0;
            for (int j = i - r; j <= i + r; j++) { x += p[j].X; y += p[j].Y; }
            o[i] = new Point(x / (2 * r + 1), y / (2 * r + 1));
        }
        return o;
    }

    /// <summary>
    /// Rounds every corner of a polyline into a curve (a quadratic Bézier through the corner, up to
    /// <paramref name="cut"/> pixels each side), so the pen sweeps round instead of stopping dead.
    /// </summary>
    internal static Point[] RoundCorners(Point[] p, double cut)
    {
        if (p.Length < 3 || cut <= 0) return p;
        var o = new List<Point> { p[0] };
        for (int i = 1; i < p.Length - 1; i++)
        {
            Point a = p[i - 1], b = p[i], c = p[i + 1];
            Vector ab = a - b, cb = c - b;
            double la = ab.Length, lc = cb.Length;
            if (la < 1e-6 || lc < 1e-6) continue;
            double ca = Math.Min(cut, la / 2), cc = Math.Min(cut, lc / 2);
            Point p0 = b + ab / la * ca, p2 = b + cb / lc * cc;
            const int steps = 5;
            for (int k = 0; k <= steps; k++)
            {
                double t = k / (double)steps, u = 1 - t;
                o.Add(new Point(u * u * p0.X + 2 * u * t * b.X + t * t * p2.X, u * u * p0.Y + 2 * u * t * b.Y + t * t * p2.Y));
            }
        }
        o.Add(p[^1]);
        return o.ToArray();
    }

    /// <summary>A stroke that would be a single dab becomes a short dash, the way a hand makes a tiny stroke rather than a dot.</summary>
    private static Point[] Dash(Point[] p, double d, int w)
    {
        if (p.Length > 1 && Length(p) >= 1) return p;
        double half = Math.Max(0.75, d * 0.3);
        var c = p[0];
        return new[] { new Point(Math.Max(0, c.X - half), c.Y), new Point(Math.Min(w - 1, c.X + half), c.Y) };
    }

    private static double Length(Point[] p)
    {
        double sum = 0;
        for (int i = 1; i < p.Length; i++) sum += (p[i] - p[i - 1]).Length;
        return sum;
    }

    /// <summary>Clears groups of same-setting cells smaller than <paramref name="min"/> cells.</summary>
    private static void DropSpecks(int[] choice, int rows, int cols, int min)
    {
        var seen = new bool[choice.Length];
        var stack = new Stack<int>();
        var group = new List<int>();
        for (int start = 0; start < choice.Length; start++)
        {
            if (seen[start] || choice[start] < 0) continue;
            int k = choice[start];
            group.Clear();
            stack.Push(start);
            seen[start] = true;
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                group.Add(i);
                int r = i / cols, c = i % cols;
                if (c > 0 && !seen[i - 1] && choice[i - 1] == k) { seen[i - 1] = true; stack.Push(i - 1); }
                if (c < cols - 1 && !seen[i + 1] && choice[i + 1] == k) { seen[i + 1] = true; stack.Push(i + 1); }
                if (r > 0 && !seen[i - cols] && choice[i - cols] == k) { seen[i - cols] = true; stack.Push(i - cols); }
                if (r < rows - 1 && !seen[i + cols] && choice[i + cols] == k) { seen[i + cols] = true; stack.Push(i + cols); }
            }
            if (group.Count < min)
                foreach (int i in group) choice[i] = -1;
        }
    }

    /// <summary>A summed-area table of a mask, for "is anything set in this box" in constant time.</summary>
    private sealed class Integral
    {
        private readonly int[] _sum;
        private readonly int _w;

        public Integral(bool[] m, int w, int h)
        {
            _w = w + 1;
            _sum = new int[(w + 1) * (h + 1)];
            for (int y = 0; y < h; y++)
            {
                int row = 0;
                for (int x = 0; x < w; x++)
                {
                    if (m[y * w + x]) row++;
                    _sum[(y + 1) * _w + x + 1] = _sum[y * _w + x + 1] + row;
                }
            }
        }

        /// <summary>True when any pixel in [x0, x1) × [y0, y1) is set.</summary>
        public bool Any(int x0, int y0, int x1, int y1) =>
            x1 > x0 && y1 > y0 && _sum[y1 * _w + x1] - _sum[y0 * _w + x1] - _sum[y1 * _w + x0] + _sum[y0 * _w + x0] > 0;
    }

    /// <summary>
    /// Paints one stroke onto the simulated canvas: a round brush of <paramref name="radius"/>
    /// along the path. Like the game, a stroke's opacity applies once, even where it crosses itself.
    /// </summary>
    private static void Simulate(float[][] canvas, int[] stamp, int id, int w, int h, Point[] path, double radius, Combo combo)
    {
        double r2 = radius * radius;
        float a = combo.A, keep = 1 - a;
        void Cover(Point p, Point q)
        {
            int xMin = Math.Max(0, (int)Math.Floor(Math.Min(p.X, q.X) - radius)), xMax = Math.Min(w - 1, (int)Math.Ceiling(Math.Max(p.X, q.X) + radius));
            int yMin = Math.Max(0, (int)Math.Floor(Math.Min(p.Y, q.Y) - radius)), yMax = Math.Min(h - 1, (int)Math.Ceiling(Math.Max(p.Y, q.Y) + radius));
            double dx = q.X - p.X, dy = q.Y - p.Y, len2 = dx * dx + dy * dy;
            for (int y = yMin; y <= yMax; y++)
            {
                double cy = y + 0.5;
                for (int x = xMin; x <= xMax; x++)
                {
                    double cx = x + 0.5;
                    double t = len2 > 0 ? Math.Clamp(((cx - p.X) * dx + (cy - p.Y) * dy) / len2, 0, 1) : 0;
                    double ex = p.X + t * dx - cx, ey = p.Y + t * dy - cy;
                    if (ex * ex + ey * ey > r2) continue;
                    int i = y * w + x;
                    if (stamp[i] == id) continue;
                    stamp[i] = id;
                    canvas[0][i] = canvas[0][i] * keep + combo.R * a;
                    canvas[1][i] = canvas[1][i] * keep + combo.G * a;
                    canvas[2][i] = canvas[2][i] * keep + combo.B * a;
                }
            }
        }
        if (path.Length == 1) Cover(path[0], path[0]);
        for (int i = 1; i < path.Length; i++) Cover(path[i - 1], path[i]);
    }

    /// <summary>How far, on average, a painted canvas is from the picture (for tests and the status line).</summary>
    public static double MeanError(byte[] paintedBgra, TraceSource src, int w, int h)
    {
        var planes = new byte[3][];
        for (int c = 0; c < 3; c++)
        {
            var plane = new byte[src.Width * src.Height];
            for (int i = 0; i < plane.Length; i++) plane[i] = src.Rgb[i * 3 + c];
            planes[c] = TraceEngine.Resample(plane, src.Width, src.Height, w, h);
        }
        double sum = 0;
        for (int i = 0; i < w * h; i++)
            sum += Distance(paintedBgra[i * 4 + 2], paintedBgra[i * 4 + 1], paintedBgra[i * 4], planes[0][i], planes[1][i], planes[2][i]);
        return sum / (w * h);
    }
}
