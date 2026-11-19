using System.Windows;
using System.Windows.Media;

namespace Overskin;

/// <summary>
/// Paints a picture the way a person would in Gartic Phone: no bucket, just the game's five brush
/// sizes and its opacity levels. It works from the biggest brush to the smallest. Each pass lays
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
                    // keep the neighbour's setting when it's nearly as good: longer strokes, fewer ends showing
                    int left = c > 0 ? choice[r * cols + c - 1] : -1, above = r > 0 ? choice[(r - 1) * cols + c] : -1;
                    choice[r * cols + c] = Choose(canvas, reference, w, x0, y0, x1, y1, combos, layerTolerance, left, above);
                }
                }
            }

            // rows of same-setting cells become strokes, joined into zig-zags where they line up
            var planned = Strokes(choice, rows, cols, step, d, w, h);
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
