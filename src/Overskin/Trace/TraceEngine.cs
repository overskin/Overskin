using System.Windows;
using System.Windows.Media;
#if !OVERSKIN_LINUX
using System.Windows.Media.Imaging;
#endif

namespace Overskin;

/// <summary>How a picture is turned into pen strokes.</summary>
public enum TraceMode
{
    /// <summary>The outer edge of every dark shape.</summary>
    Outline,
    /// <summary>One stroke down the middle of every dark line. Best for line art and text.</summary>
    Lines,
    /// <summary>Edges found by contrast rather than darkness. Best for photos.</summary>
    Sketch,
    /// <summary>Parallel strokes across dark areas, optionally cross-hatched.</summary>
    Hatch,
    /// <summary>Hatching that gets denser in darker tones.</summary>
    Shade,
    /// <summary>Fills each region with the nearest colour from a game's palette.</summary>
    Colour,
    /// <summary>
    /// Paints like Gartic Phone allows: big blobs with the biggest brush first, then smaller
    /// brushes only where it's still wrong, mixing shades with the game's opacity levels. No bucket.
    /// </summary>
    Gartic,
}

public sealed class TraceOptions
{
    public TraceMode Mode { get; init; } = TraceMode.Outline;
    /// <summary>0–255. Darker than this counts as ink. In Sketch mode: edge sensitivity.</summary>
    public int Threshold { get; init; } = 128;
    public bool Invert { get; init; }
    /// <summary>Smoothing radius in pixels applied before thresholding (0 = none).</summary>
    public int Blur { get; init; }
    /// <summary>Working size relative to the source; 1.0 = source size.</summary>
    public double Scale { get; init; } = 1.0;
    /// <summary>Strokes shorter than this many pixels are dropped as specks.</summary>
    public int MinPathLength { get; init; } = 8;
    /// <summary>Ramer-Douglas-Peucker tolerance in pixels.</summary>
    public double Tolerance { get; init; } = 1.0;
    /// <summary>Gap between hatch lines in pixels.</summary>
    public double Spacing { get; init; } = 4;
    /// <summary>Hatch direction in degrees (0 = horizontal).</summary>
    public double Angle { get; init; }
    public bool Crosshatch { get; init; }
    /// <summary>Number of tones in Shade mode.</summary>
    public int Levels { get; init; } = 3;
    /// <summary>Also trace the outline of filled regions.</summary>
    public bool OutlineToo { get; init; }
    /// <summary>Colour mode: the colours the target program offers. Skipped ones are left blank.</summary>
    public IReadOnlyList<(Color Color, bool Skip)> Palette { get; init; } = Array.Empty<(Color, bool)>();
    /// <summary>Gartic: the game's brush diameters in working pixels, smallest first.</summary>
    public IReadOnlyList<double> BrushSizes { get; init; } = Array.Empty<double>();
    /// <summary>Gartic: how many opacity levels the game has (1 = always full).</summary>
    public int OpacityLevels { get; init; } = 1;
    /// <summary>Gartic: how wrong (colour distance) an area may stay before a smaller brush fixes it. Lower = more detail, more strokes.</summary>
    public double Tolerance2 { get; init; } = 28;
}

/// <summary>A prepared picture: greyscale plus colour, both already flattened onto white.</summary>
public sealed class TraceSource
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required byte[] Gray { get; init; }
    /// <summary>R, G, B for every pixel.</summary>
    public required byte[] Rgb { get; init; }
}

public sealed class TraceResult
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    /// <summary>Strokes in working-pixel coordinates. Each one is drawn with the button held down.</summary>
    public required List<Point[]> Paths { get; init; }
    /// <summary>Colour mode: the palette index of each path. Null otherwise.</summary>
    public List<int>? ColourIndex { get; init; }
    /// <summary>Colour mode: the palette colours, for previews.</summary>
    public Color[]? Colours { get; init; }
    /// <summary>Faint picture of what was traced, premultiplied BGRA, Width × Height.</summary>
    public required byte[] Backdrop { get; init; }
    public int PointCount { get; init; }
    /// <summary>Hatching: preview with hairlines so the density, not a solid blob, is what shows.</summary>
    public bool ThinPreview { get; init; }
    /// <summary>Why nothing came out, when that is the case.</summary>
    public string? Message { get; init; }
    /// <summary>Gartic: which brush (0 = smallest) each path uses.</summary>
    public List<int>? SizeIndex { get; init; }
    /// <summary>Gartic: which opacity level (1 = faintest … levels = full) each path uses.</summary>
    public List<int>? OpacityIndex { get; init; }
    /// <summary>Gartic: what the canvas should look like when it's done, premultiplied BGRA, Width × Height.</summary>
    public byte[]? Painted { get; init; }
}

/// <summary>Turns a picture into pen strokes.</summary>
public static class TraceEngine
{
    private const int MaxSourceSide = 2400;
    private const int MaxWorkSide = 2000;

    // ===================================================================================
    //  preparation
    // ===================================================================================

#if !OVERSKIN_LINUX
    /// <summary>Flattens any bitmap onto white and splits it into grey and colour. Call on the UI thread.</summary>
    public static TraceSource Prepare(BitmapSource source)
    {
        BitmapSource src = source;
        int longest = Math.Max(src.PixelWidth, src.PixelHeight);
        if (longest > MaxSourceSide)
        {
            double k = (double)MaxSourceSide / longest;
            src = new TransformedBitmap(src, new ScaleTransform(k, k));
        }

        var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
        byte[] px = new byte[stride * h];
        bgra.CopyPixels(px, stride, 0);
        return PrepareBgra(px, w, h);
    }
#endif

    /// <summary>The same from straight BGRA pixels (already no bigger than <see cref="MaxSourceSide"/>).</summary>
    public static TraceSource PrepareBgra(byte[] px, int w, int h)
    {
        byte[] gray = new byte[w * h];
        byte[] rgb = new byte[w * h * 3];
        for (int i = 0, p = 0; i < gray.Length; i++, p += 4)
        {
            int a = px[p + 3];
            // transparent areas read as paper
            int b = (px[p] * a + 255 * (255 - a)) / 255;
            int g = (px[p + 1] * a + 255 * (255 - a)) / 255;
            int r = (px[p + 2] * a + 255 * (255 - a)) / 255;
            rgb[i * 3] = (byte)r;
            rgb[i * 3 + 1] = (byte)g;
            rgb[i * 3 + 2] = (byte)b;
            gray[i] = (byte)((b * 29 + g * 150 + r * 77) >> 8);
        }
        return new TraceSource { Width = w, Height = h, Gray = gray, Rgb = rgb };
    }

    /// <summary>Otsu's method: the threshold that best splits the picture into two tones.</summary>
    public static int AutoThreshold(TraceSource source)
    {
        var hist = new long[256];
        foreach (byte v in source.Gray) hist[v]++;
        long total = source.Gray.Length;
        double sumAll = 0;
        for (int i = 0; i < 256; i++) sumAll += i * (double)hist[i];

        // the best split is often a flat plateau (always, for pure black-and-white art): take its middle
        double sumB = 0, best = -1;
        long wB = 0;
        int first = 128, last = 128;
        for (int t = 0; t < 256; t++)
        {
            wB += hist[t];
            if (wB == 0) continue;
            long wF = total - wB;
            if (wF == 0) break;
            sumB += t * (double)hist[t];
            double mB = sumB / wB, mF = (sumAll - sumB) / wF;
            double between = (double)wB * wF * (mB - mF) * (mB - mF);
            if (between > best * (1 + 1e-9))
            {
                best = between;
                first = last = t;
            }
            else if (between >= best * (1 - 1e-9))
            {
                last = t;
            }
        }
        return Math.Clamp((first + last) / 2 + 1, 1, 254);
    }

    // ===================================================================================
    //  tracing
    // ===================================================================================

    /// <summary>Pure computation; safe to run on a worker thread.</summary>
    public static TraceResult Trace(TraceSource source, TraceOptions o, CancellationToken ct)
    {
        double scale = Math.Clamp(o.Scale, 0.02, 8.0);
        int w = Math.Max(1, (int)Math.Round(source.Width * scale));
        int h = Math.Max(1, (int)Math.Round(source.Height * scale));
        int longest = Math.Max(w, h);
        if (longest > MaxWorkSide)
        {
            double k = (double)MaxWorkSide / longest;
            w = Math.Max(1, (int)(w * k));
            h = Math.Max(1, (int)(h * k));
        }

        if (o.Mode == TraceMode.Colour) return TraceColour(source, o, w, h, ct);
        if (o.Mode == TraceMode.Gartic) return Painter.Paint(source, o, w, h, ct);

        byte[] gray = Resample(source.Gray, source.Width, source.Height, w, h);
        if (o.Blur > 0) gray = Blur(gray, w, h, o.Blur);
        if (o.Invert && o.Mode != TraceMode.Sketch)
            for (int i = 0; i < gray.Length; i++) gray[i] = (byte)(255 - gray[i]);
        ct.ThrowIfCancellationRequested();

        bool[] mask = o.Mode == TraceMode.Sketch ? EdgeMask(gray, w, h, o.Threshold) : DarkMask(gray, o.Threshold);
        var paths = new List<Point[]>();

        switch (o.Mode)
        {
            case TraceMode.Outline:
                paths = OrderNearest(Outlines(mask, w, h, o, ct), ct);
                break;

            case TraceMode.Lines:
            case TraceMode.Sketch:
                paths = OrderNearest(Centrelines(mask, w, h, o, ct), ct);
                break;

            case TraceMode.Hatch:
                paths.AddRange(Hatch(mask, w, h, o.Spacing, o.Angle, MinRun(o), ct));
                if (o.Crosshatch) paths.AddRange(Hatch(mask, w, h, o.Spacing, o.Angle + 90, MinRun(o), ct));
                if (o.OutlineToo) paths.AddRange(OrderNearest(Outlines(mask, w, h, o, ct), ct));
                break;

            case TraceMode.Shade:
            {
                // each darker tone adds another direction of hatching on top
                double[] angles = { 45, 135, 0, 90, 22.5 };
                int levels = Math.Clamp(o.Levels, 2, 5);
                for (int k = 0; k < levels; k++)
                {
                    int t = (int)Math.Round(o.Threshold * (levels - k) / (double)levels);
                    bool[] tone = k == 0 ? mask : DarkMask(gray, t);
                    paths.AddRange(Hatch(tone, w, h, o.Spacing, angles[k], MinRun(o), ct));
                }
                if (o.OutlineToo) paths.AddRange(OrderNearest(Outlines(mask, w, h, o, ct), ct));
                break;
            }
        }

        string? message = paths.Count > 0 ? null : o.Mode switch
        {
            TraceMode.Sketch => "No edges found. Raise the sensitivity.",
            _ => "Nothing to draw. Try another threshold, or Invert.",
        };

        return new TraceResult
        {
            Width = w,
            Height = h,
            Paths = paths,
            Backdrop = MaskBackdrop(mask),
            PointCount = CountPoints(paths),
            ThinPreview = o.Mode is TraceMode.Hatch or TraceMode.Shade,
            Message = message,
        };
    }

    private static int MinRun(TraceOptions o) => Math.Max(1, o.MinPathLength / 4);

    private static int CountPoints(List<Point[]> paths)
    {
        int n = 0;
        foreach (var p in paths) n += p.Length;
        return n;
    }

    private static bool[] DarkMask(byte[] gray, int threshold)
    {
        var mask = new bool[gray.Length];
        for (int i = 0; i < mask.Length; i++) mask[i] = gray[i] < threshold;
        return mask;
    }

    /// <summary>Sobel edge strength above a cut-off set by <paramref name="sensitivity"/> (higher = more edges).</summary>
    private static bool[] EdgeMask(byte[] g, int w, int h, int sensitivity)
    {
        var mask = new bool[w * h];
        double cut = 4 + (255 - Math.Clamp(sensitivity, 0, 255)) * 0.35;
        double cut2 = cut * cut * 16; // compare squared, before dividing by 4
        for (int y = 1; y < h - 1; y++)
        {
            int r0 = (y - 1) * w, r1 = y * w, r2 = (y + 1) * w;
            for (int x = 1; x < w - 1; x++)
            {
                int gx = g[r0 + x + 1] + 2 * g[r1 + x + 1] + g[r2 + x + 1] - g[r0 + x - 1] - 2 * g[r1 + x - 1] - g[r2 + x - 1];
                int gy = g[r2 + x - 1] + 2 * g[r2 + x] + g[r2 + x + 1] - g[r0 + x - 1] - 2 * g[r0 + x] - g[r0 + x + 1];
                mask[r1 + x] = (double)gx * gx + (double)gy * gy >= cut2;
            }
        }
        return mask;
    }

    // ===================================================================================
    //  colour
    // ===================================================================================

    private static TraceResult TraceColour(TraceSource source, TraceOptions o, int w, int h, CancellationToken ct)
    {
        var palette = o.Palette;
        var colours = palette.Select(p => p.Color).ToArray();
        if (palette.Count == 0 || palette.All(p => p.Skip))
        {
            return new TraceResult
            {
                Width = w, Height = h, Paths = new(), ColourIndex = new(), Colours = colours,
                Backdrop = new byte[w * h * 4],
                Message = palette.Count == 0
                    ? "Pick the game's colours first: Pick from screen, then click each swatch."
                    : "Every colour is turned off. Turn some back on.",
            };
        }

        // resample and smooth each channel
        var channels = new byte[3][];
        for (int c = 0; c < 3; c++)
        {
            var plane = new byte[source.Width * source.Height];
            for (int i = 0; i < plane.Length; i++) plane[i] = source.Rgb[i * 3 + c];
            plane = Resample(plane, source.Width, source.Height, w, h);
            if (o.Blur > 0) plane = Blur(plane, w, h, o.Blur);
            channels[c] = plane;
        }
        ct.ThrowIfCancellationRequested();

        // nearest palette colour for every pixel (weighted RGB, close to how people see difference)
        var labels = new int[w * h];
        for (int i = 0; i < labels.Length; i++)
        {
            int r = channels[0][i], g = channels[1][i], b = channels[2][i];
            int best = 0;
            double bestD = double.MaxValue;
            for (int k = 0; k < colours.Length; k++)
            {
                var pc = colours[k];
                double rm = (r + pc.R) / 2.0;
                double dr = r - pc.R, dg = g - pc.G, db = b - pc.B;
                double d = (2 + rm / 256) * dr * dr + 4 * dg * dg + (2 + (255 - rm) / 256) * db * db;
                if (d < bestD) { bestD = d; best = k; }
            }
            labels[i] = best;
        }
        ct.ThrowIfCancellationRequested();

        // lightest colours first, so darker detail lands on top
        var order = Enumerable.Range(0, colours.Length)
            .Where(k => !palette[k].Skip)
            .OrderByDescending(k => Luma(colours[k]))
            .ToList();

        var paths = new List<Point[]>();
        var index = new List<int>();
        foreach (int k in order)
        {
            ct.ThrowIfCancellationRequested();
            var mask = new bool[w * h];
            bool any = false;
            for (int i = 0; i < mask.Length; i++)
                if (labels[i] == k) { mask[i] = true; any = true; }
            if (!any) continue;

            var mine = new List<Point[]>();
            mine.AddRange(Hatch(mask, w, h, o.Spacing, o.Angle, MinRun(o), ct));
            if (o.Crosshatch) mine.AddRange(Hatch(mask, w, h, o.Spacing, o.Angle + 90, MinRun(o), ct));
            if (o.OutlineToo) mine.AddRange(OrderNearest(Outlines(mask, w, h, o, ct), ct));
            paths.AddRange(mine);
            index.AddRange(Enumerable.Repeat(k, mine.Count));
        }

        // backdrop: the quantised picture, faint
        var backdrop = new byte[w * h * 4];
        const int alpha = 70;
        for (int i = 0; i < labels.Length; i++)
        {
            int k = labels[i];
            if (palette[k].Skip) continue;
            var c = colours[k];
            int p = i * 4;
            backdrop[p] = (byte)(c.B * alpha / 255);
            backdrop[p + 1] = (byte)(c.G * alpha / 255);
            backdrop[p + 2] = (byte)(c.R * alpha / 255);
            backdrop[p + 3] = alpha;
        }

        return new TraceResult
        {
            Width = w,
            Height = h,
            Paths = paths,
            ColourIndex = index,
            Colours = colours,
            Backdrop = backdrop,
            PointCount = CountPoints(paths),
            Message = paths.Count > 0 ? null : "Nothing to draw with these colours.",
        };
    }

    public static double Luma(Color c) => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;

    // ===================================================================================
    //  resampling and smoothing
    // ===================================================================================

    internal static byte[] Resample(byte[] s, int sw, int sh, int w, int h)
    {
        if (w == sw && h == sh) return (byte[])s.Clone();

        byte[] dst = new byte[w * h];
        double sx = (double)sw / w, sy = (double)sh / h;

        if (sx > 1 || sy > 1)
        {
            // shrinking: average the block of source pixels each output pixel covers
            for (int y = 0; y < h; y++)
            {
                int y0 = (int)(y * sy), y1 = Math.Min(sh, Math.Max(y0 + 1, (int)((y + 1) * sy)));
                for (int x = 0; x < w; x++)
                {
                    int x0 = (int)(x * sx), x1 = Math.Min(sw, Math.Max(x0 + 1, (int)((x + 1) * sx)));
                    int sum = 0;
                    for (int yy = y0; yy < y1; yy++)
                    {
                        int row = yy * sw;
                        for (int xx = x0; xx < x1; xx++) sum += s[row + xx];
                    }
                    dst[y * w + x] = (byte)(sum / ((y1 - y0) * (x1 - x0)));
                }
            }
        }
        else
        {
            // enlarging: bilinear, so edges stay smooth instead of blocky
            for (int y = 0; y < h; y++)
            {
                double fy = Math.Clamp((y + 0.5) * sy - 0.5, 0, sh - 1);
                int y0 = (int)fy, y1 = Math.Min(sh - 1, y0 + 1);
                double ty = fy - y0;
                for (int x = 0; x < w; x++)
                {
                    double fx = Math.Clamp((x + 0.5) * sx - 0.5, 0, sw - 1);
                    int x0 = (int)fx, x1 = Math.Min(sw - 1, x0 + 1);
                    double tx = fx - x0;
                    double top = s[y0 * sw + x0] * (1 - tx) + s[y0 * sw + x1] * tx;
                    double bottom = s[y1 * sw + x0] * (1 - tx) + s[y1 * sw + x1] * tx;
                    dst[y * w + x] = (byte)(top * (1 - ty) + bottom * ty + 0.5);
                }
            }
        }
        return dst;
    }

    /// <summary>Two passes of a separable box blur: close to a Gaussian, and fast at any radius.</summary>
    internal static byte[] Blur(byte[] src, int w, int h, int radius)
    {
        var a = (byte[])src.Clone();
        var b = new byte[a.Length];
        for (int pass = 0; pass < 2; pass++)
        {
            BoxH(a, b, w, h, radius);
            BoxV(b, a, w, h, radius);
        }
        return a;
    }

    private static void BoxH(byte[] s, byte[] d, int w, int h, int r)
    {
        for (int y = 0; y < h; y++)
        {
            int row = y * w, sum = 0, n = 0;
            for (int x = -r; x < w; x++)
            {
                int add = x + r, rem = x - r - 1;
                if (add < w) { sum += s[row + add]; n++; }
                if (rem >= 0) { sum -= s[row + rem]; n--; }
                if (x >= 0) d[row + x] = (byte)(sum / n);
            }
        }
    }

    private static void BoxV(byte[] s, byte[] d, int w, int h, int r)
    {
        for (int x = 0; x < w; x++)
        {
            int sum = 0, n = 0;
            for (int y = -r; y < h; y++)
            {
                int add = y + r, rem = y - r - 1;
                if (add < h) { sum += s[add * w + x]; n++; }
                if (rem >= 0) { sum -= s[rem * w + x]; n--; }
                if (y >= 0) d[y * w + x] = (byte)(sum / n);
            }
        }
    }

    // ===================================================================================
    //  outlines: boundary following
    // ===================================================================================

    private static readonly int[] Dx = { 1, 0, -1, 0 };
    private static readonly int[] Dy = { 0, 1, 0, -1 };

    private static List<Point[]> Outlines(bool[] mask, int w, int h, TraceOptions o, CancellationToken ct)
    {
        var loops = Contours(mask, w, h, o.MinPathLength, ct);
        var result = new List<Point[]>(loops.Count);
        foreach (var loop in loops)
        {
            ct.ThrowIfCancellationRequested();
            result.Add(SimplifyClosed(loop, o.Tolerance));
        }
        return result;
    }

    /// <summary>
    /// Follows the boundary between ink and paper along pixel edges, keeping ink on the right.
    /// Produces one closed loop per outer edge and per hole.
    /// </summary>
    private static List<Point[]> Contours(bool[] m, int w, int h, int minLength, CancellationToken ct)
    {
        int vw = w + 1, vh = h + 1;
        // per corner: bit d set = an unvisited boundary edge leaves this corner in direction d
        byte[] edges = new byte[vw * vh];

        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                if (!m[row + x]) continue;
                if (y == 0 || !m[row - w + x]) edges[y * vw + x] |= 1;
                if (x == w - 1 || !m[row + x + 1]) edges[y * vw + x + 1] |= 2;
                if (y == h - 1 || !m[row + w + x]) edges[(y + 1) * vw + x + 1] |= 4;
                if (x == 0 || !m[row + x - 1]) edges[(y + 1) * vw + x] |= 8;
            }
        }

        var result = new List<Point[]>();
        var pts = new List<Point>(256);

        for (int vy = 0; vy < vh; vy++)
        {
            if ((vy & 63) == 0) ct.ThrowIfCancellationRequested();
            for (int vx = 0; vx < vw; vx++)
            {
                while (edges[vy * vw + vx] != 0)
                {
                    byte bits = edges[vy * vw + vx];
                    int startDir = (bits & 1) != 0 ? 0 : (bits & 2) != 0 ? 1 : (bits & 4) != 0 ? 2 : 3;

                    pts.Clear();
                    pts.Add(new Point(vx, vy));
                    int cx = vx, cy = vy, dir = startDir;
                    double length = 0;

                    while (true)
                    {
                        edges[cy * vw + cx] &= (byte)~(1 << dir);
                        cx += Dx[dir];
                        cy += Dy[dir];
                        length += 1;

                        bool atStart = cx == vx && cy == vy;
                        byte here = edges[cy * vw + cx];
                        int next = -1;
                        bool closed = false;

                        // where two ink pixels touch only at a corner, turning right keeps blobs apart
                        int right = (dir + 1) & 3, left = (dir + 3) & 3;
                        for (int k = 0; k < 3; k++)
                        {
                            int cand = k == 0 ? right : k == 1 ? dir : left;
                            if (atStart && cand == startDir) { closed = true; break; }
                            if ((here & (1 << cand)) != 0) { next = cand; break; }
                        }

                        if (closed || next < 0) break;
                        if (next != dir) pts.Add(new Point(cx, cy));
                        dir = next;
                    }

                    if (length >= minLength && pts.Count >= 3)
                    {
                        pts.Add(pts[0]);
                        result.Add(pts.ToArray());
                    }
                }
            }
        }
        return result;
    }

    // ===================================================================================
    //  centrelines: thinning and skeleton walking
    // ===================================================================================

    private static List<Point[]> Centrelines(bool[] mask, int w, int h, TraceOptions o, CancellationToken ct)
    {
        if (w < 3 || h < 3) return new();
        bool[] skel = Thin(mask, w, h, ct);
        RemoveStairs(skel, w, h);
        var raw = JoinStraight(WalkSkeleton(skel, w, h, 2, ct), ct);
        raw.RemoveAll(p => Length(p) < o.MinPathLength);

        var result = new List<Point[]>(raw.Count);
        foreach (var path in raw)
        {
            bool closed = path.Length > 3 && path[0] == path[^1];
            result.Add(closed ? SimplifyClosed(path, o.Tolerance) : SimplifyOpen(path, o.Tolerance));
        }
        return result;
    }

    private static double Length(Point[] p)
    {
        double len = 0;
        for (int i = 1; i < p.Length; i++) len += (p[i] - p[i - 1]).Length;
        return len;
    }

    /// <summary>
    /// Where strokes meet at a crossing, carries on into whichever one continues in the same
    /// direction, so a plus sign is two strokes rather than four.
    /// </summary>
    internal static List<Point[]> JoinStraight(List<Point[]> paths, CancellationToken ct)
    {
        const double reach = 3.2;   // px between the ends that may be joined
        const double minCos = 0.80; // within about 37 degrees of straight on
        const int cell = 4;

        var alive = new bool[paths.Count];
        var lists = new List<Point>[paths.Count];
        for (int i = 0; i < paths.Count; i++)
        {
            alive[i] = paths[i].Length >= 2 && paths[i][0] != paths[i][^1]; // closed loops stay as they are
            lists[i] = new List<Point>(paths[i]);
        }

        // spatial index of stroke ends
        var grid = new Dictionary<(int, int), List<int>>();
        (int, int) Key(Point p) => ((int)Math.Floor(p.X / cell), (int)Math.Floor(p.Y / cell));
        void Index(int i)
        {
            foreach (var end in new[] { lists[i][0], lists[i][^1] })
            {
                var key = Key(end);
                if (!grid.TryGetValue(key, out var bucket)) grid[key] = bucket = new List<int>();
                bucket.Add(i);
            }
        }
        for (int i = 0; i < paths.Count; i++) if (alive[i]) Index(i);

        // direction of travel leaving a stroke through one of its ends, measured over a few pixels
        static Vector Outward(List<Point> pts, bool atEnd)
        {
            int n = pts.Count, k = Math.Min(6, n - 1);
            var v = atEnd ? pts[n - 1] - pts[n - 1 - k] : pts[0] - pts[k];
            if (v.Length > 1e-9) v.Normalize();
            return v;
        }

        for (int i = 0; i < paths.Count; i++)
        {
            if (!alive[i]) continue;
            if ((i & 255) == 0) ct.ThrowIfCancellationRequested();

            for (int side = 0; side < 2; side++)
            {
                if (side == 1) lists[i].Reverse(); // second pass extends from the other end
                while (true)
                {
                    var tip = lists[i][^1];
                    var dir = Outward(lists[i], atEnd: true);
                    var (kx, ky) = Key(tip);

                    int best = -1;
                    bool bestFromStart = true;
                    double bestScore = double.MinValue;
                    for (int gx = kx - 1; gx <= kx + 1; gx++)
                    {
                        for (int gy = ky - 1; gy <= ky + 1; gy++)
                        {
                            if (!grid.TryGetValue((gx, gy), out var bucket)) continue;
                            foreach (int j in bucket)
                            {
                                if (j == i || !alive[j]) continue;
                                var other = lists[j];
                                for (int e = 0; e < 2; e++)
                                {
                                    bool fromStart = e == 0;
                                    var end = fromStart ? other[0] : other[^1];
                                    double gap = (end - tip).Length;
                                    if (gap > reach) continue;
                                    // entering the other stroke through this end, we travel opposite to its outward direction
                                    var onward = -Outward(other, atEnd: !fromStart);
                                    double cos = dir.X * onward.X + dir.Y * onward.Y;
                                    if (cos < minCos) continue;
                                    double score = cos - gap * 0.05;
                                    if (score > bestScore) { bestScore = score; best = j; bestFromStart = fromStart; }
                                }
                            }
                        }
                    }

                    if (best < 0) break;
                    var add = lists[best];
                    if (!bestFromStart) add.Reverse();
                    lists[i].AddRange(add);
                    alive[best] = false;
                    Index(i); // the new far end becomes findable
                }
            }
        }

        var result = new List<Point[]>();
        for (int i = 0; i < paths.Count; i++)
        {
            bool closedLoop = paths[i].Length >= 2 && paths[i][0] == paths[i][^1];
            if (alive[i] || closedLoop) result.Add(lists[i].ToArray());
        }
        return result;
    }

    /// <summary>Zhang-Suen thinning: peels shapes down to one-pixel-wide lines.</summary>
    internal static bool[] Thin(bool[] src, int w, int h, CancellationToken ct)
    {
        var m = (bool[])src.Clone();
        for (int x = 0; x < w; x++) { m[x] = false; m[(h - 1) * w + x] = false; }
        for (int y = 0; y < h; y++) { m[y * w] = false; m[y * w + w - 1] = false; }

        var fg = new List<int>();
        for (int i = 0; i < m.Length; i++) if (m[i]) fg.Add(i);
        var del = new List<int>();

        bool changed = true;
        while (changed)
        {
            ct.ThrowIfCancellationRequested();
            changed = false;
            for (int step = 0; step < 2; step++)
            {
                del.Clear();
                foreach (int p in fg)
                {
                    if (!m[p]) continue;
                    bool p2 = m[p - w], p3 = m[p - w + 1], p4 = m[p + 1], p5 = m[p + w + 1];
                    bool p6 = m[p + w], p7 = m[p + w - 1], p8 = m[p - 1], p9 = m[p - w - 1];

                    int b = (p2 ? 1 : 0) + (p3 ? 1 : 0) + (p4 ? 1 : 0) + (p5 ? 1 : 0)
                          + (p6 ? 1 : 0) + (p7 ? 1 : 0) + (p8 ? 1 : 0) + (p9 ? 1 : 0);
                    if (b < 2 || b > 6) continue;

                    int a = (!p2 && p3 ? 1 : 0) + (!p3 && p4 ? 1 : 0) + (!p4 && p5 ? 1 : 0) + (!p5 && p6 ? 1 : 0)
                          + (!p6 && p7 ? 1 : 0) + (!p7 && p8 ? 1 : 0) + (!p8 && p9 ? 1 : 0) + (!p9 && p2 ? 1 : 0);
                    if (a != 1) continue;

                    if (step == 0 ? (p2 && p4 && p6) || (p4 && p6 && p8) : (p2 && p4 && p8) || (p2 && p6 && p8)) continue;
                    del.Add(p);
                }
                foreach (int d in del) m[d] = false;
                if (del.Count > 0) changed = true;
            }
            fg.RemoveAll(i => !m[i]);
        }
        return m;
    }

    /// <summary>
    /// Removes corner pixels whose two straight neighbours already touch diagonally, so every
    /// line is cleanly one pixel wide and corners don't look like branches.
    /// </summary>
    internal static void RemoveStairs(bool[] m, int w, int h)
    {
        for (int y = 1; y < h - 1; y++)
        {
            for (int x = 1; x < w - 1; x++)
            {
                int p = y * w + x;
                if (!m[p]) continue;
                bool n = m[p - w], e = m[p + 1], s = m[p + w], wst = m[p - 1];
                bool ne = m[p - w + 1], se = m[p + w + 1], sw = m[p + w - 1], nw = m[p - w - 1];
                if ((n && e && !s && !wst && !sw) || (e && s && !n && !wst && !nw) ||
                    (s && wst && !n && !e && !ne) || (wst && n && !e && !s && !se))
                    m[p] = false;
            }
        }
    }

    internal static List<Point[]> WalkSkeleton(bool[] sk, int w, int h, int minLength, CancellationToken ct)
    {
        // straight neighbours first, so a walk never skips a pixel by cutting a corner
        int[] offs = { -w, 1, w, -1, -w + 1, w + 1, w - 1, -w - 1 };
        var deg = new byte[sk.Length];
        var all = new List<int>();
        var ends = new List<int>();
        for (int p = 0; p < sk.Length; p++)
        {
            if (!sk[p]) continue;
            int x = p % w, y = p / w;
            if (x == 0 || y == 0 || x == w - 1 || y == h - 1) { sk[p] = false; continue; }
            all.Add(p);
        }
        foreach (int p in all)
        {
            byte d = 0;
            foreach (int off in offs) if (sk[p + off]) d++;
            deg[p] = d;
            if (d <= 1) ends.Add(p);
        }

        var visited = new bool[sk.Length];
        var result = new List<Point[]>();

        List<int> Walk(int start, int exclude)
        {
            var list = new List<int> { start };
            int cur = start, prev = exclude;
            while (true)
            {
                int next = -1;
                foreach (int off in offs)
                {
                    int q = cur + off;
                    if (sk[q] && !visited[q]) { next = q; break; }
                }
                if (next < 0)
                {
                    // close a loop, or join a branch point that another stroke already passed through
                    foreach (int off in offs)
                    {
                        int q = cur + off;
                        if (q == prev || !sk[q] || !visited[q]) continue;
                        if ((q == start && list.Count > 3) || (deg[q] >= 3 && !list.Contains(q)))
                        {
                            list.Add(q);
                            break;
                        }
                    }
                    break;
                }
                visited[next] = true;
                list.Add(next);
                prev = cur;
                cur = next;
                if (deg[next] >= 3) break;
            }
            return list;
        }

        void Take(int start)
        {
            if (visited[start]) return;
            visited[start] = true;
            var forward = Walk(start, -1);
            var back = Walk(start, forward.Count > 1 ? forward[1] : -1);
            back.Reverse();
            back.AddRange(forward.Skip(1));

            double length = 0;
            var pts = new Point[back.Count];
            for (int i = 0; i < back.Count; i++)
            {
                pts[i] = new Point(back[i] % w + 0.5, back[i] / w + 0.5);
                if (i > 0) length += (pts[i] - pts[i - 1]).Length;
            }
            if (back.Count >= 2 && length >= minLength) result.Add(pts);
            else if (back.Count == 1 && minLength <= 1) result.Add(new[] { pts[0], pts[0] }); // a lone dot
        }

        foreach (int p in ends) Take(p);
        for (int i = 0; i < all.Count; i++)
        {
            if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
            Take(all[i]);
        }
        return result;
    }

    // ===================================================================================
    //  simplification
    // ===================================================================================

    internal static Point[] SimplifyOpen(Point[] pts, double tolerance)
    {
        if (tolerance <= 0 || pts.Length <= 2) return pts;
        bool[] keep = new bool[pts.Length];
        keep[0] = keep[^1] = true;
        Rdp(pts, 0, pts.Length - 1, tolerance, keep);
        return Kept(pts, keep);
    }

    /// <summary>Ramer-Douglas-Peucker on a closed loop (first point repeated at the end).</summary>
    private static Point[] SimplifyClosed(Point[] loop, double tolerance)
    {
        if (tolerance <= 0 || loop.Length <= 4) return loop;

        int far = 0;
        double farDist = -1;
        for (int i = 1; i < loop.Length - 1; i++)
        {
            double d = (loop[i] - loop[0]).LengthSquared;
            if (d > farDist) { farDist = d; far = i; }
        }

        bool[] keep = new bool[loop.Length];
        keep[0] = keep[far] = keep[^1] = true;
        Rdp(loop, 0, far, tolerance, keep);
        Rdp(loop, far, loop.Length - 1, tolerance, keep);
        return Kept(loop, keep);
    }

    private static Point[] Kept(Point[] pts, bool[] keep)
    {
        int count = 0;
        foreach (bool k in keep) if (k) count++;
        var result = new Point[count];
        for (int i = 0, j = 0; i < pts.Length; i++)
            if (keep[i]) result[j++] = pts[i];
        return result;
    }

    private static void Rdp(Point[] pts, int first, int last, double tolerance, bool[] keep)
    {
        var stack = new Stack<(int, int)>();
        stack.Push((first, last));
        double tol2 = tolerance * tolerance;

        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            if (b <= a + 1) continue;

            double ax = pts[a].X, ay = pts[a].Y;
            double dx = pts[b].X - ax, dy = pts[b].Y - ay;
            double len2 = dx * dx + dy * dy;

            int index = -1;
            double max = tol2;
            for (int i = a + 1; i < b; i++)
            {
                double px = pts[i].X - ax, py = pts[i].Y - ay;
                double d2;
                if (len2 < 1e-12)
                {
                    d2 = px * px + py * py;
                }
                else
                {
                    double t = Math.Clamp((px * dx + py * dy) / len2, 0, 1);
                    double ex = px - t * dx, ey = py - t * dy;
                    d2 = ex * ex + ey * ey;
                }
                if (d2 > max) { max = d2; index = i; }
            }

            if (index < 0) continue;
            keep[index] = true;
            stack.Push((a, index));
            stack.Push((index, b));
        }
    }

    // ===================================================================================
    //  ordering
    // ===================================================================================

    /// <summary>
    /// Greedy nearest-neighbour: always go to the closest undrawn stroke, entering open strokes
    /// from whichever end is nearer.
    /// </summary>
    private static List<Point[]> OrderNearest(List<Point[]> paths, CancellationToken ct)
    {
        int n = paths.Count;
        if (n < 2 || n > 20000) return paths;

        var ordered = new List<Point[]>(n);
        bool[] used = new bool[n];
        Point pen = new(0, 0);

        for (int step = 0; step < n; step++)
        {
            if ((step & 255) == 0) ct.ThrowIfCancellationRequested();

            int best = -1;
            bool reverse = false;
            double bestDist = double.MaxValue;
            for (int i = 0; i < n; i++)
            {
                if (used[i]) continue;
                var p = paths[i];
                double d0 = (p[0] - pen).LengthSquared;
                if (d0 < bestDist) { bestDist = d0; best = i; reverse = false; }
                if (p[0] != p[^1])
                {
                    double d1 = (p[^1] - pen).LengthSquared;
                    if (d1 < bestDist) { bestDist = d1; best = i; reverse = true; }
                }
            }

            used[best] = true;
            var chosen = paths[best];
            if (reverse)
            {
                chosen = (Point[])chosen.Clone();
                Array.Reverse(chosen);
            }
            ordered.Add(chosen);
            pen = chosen[^1];
        }
        return ordered;
    }

    /// <summary>
    /// For every pixel in the mask, roughly how far it is from the nearest pixel outside it
    /// (or off the picture), by a two-pass chamfer transform. Pixels outside are 0.
    /// </summary>
    internal static float[] InsideDistance(bool[] m, int w, int h)
    {
        const float Straight = 1f, Diagonal = 1.4142f;
        var d = new float[w * h];
        for (int i = 0; i < d.Length; i++) d[i] = m[i] ? float.MaxValue : 0;

        // off the picture counts as outside
        float At(int x, int y) => x < 0 || y < 0 || x >= w || y >= h ? 0 : d[y * w + x];

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (d[i] == 0) continue;
                float v = d[i];
                v = Math.Min(v, At(x - 1, y) + Straight);
                v = Math.Min(v, At(x, y - 1) + Straight);
                v = Math.Min(v, At(x - 1, y - 1) + Diagonal);
                v = Math.Min(v, At(x + 1, y - 1) + Diagonal);
                d[i] = v;
            }
        }
        for (int y = h - 1; y >= 0; y--)
        {
            for (int x = w - 1; x >= 0; x--)
            {
                int i = y * w + x;
                if (d[i] == 0) continue;
                float v = d[i];
                v = Math.Min(v, At(x + 1, y) + Straight);
                v = Math.Min(v, At(x, y + 1) + Straight);
                v = Math.Min(v, At(x + 1, y + 1) + Diagonal);
                v = Math.Min(v, At(x - 1, y + 1) + Diagonal);
                d[i] = v;
            }
        }
        return d;
    }

    // ===================================================================================
    //  hatching
    // ===================================================================================

    /// <summary>
    /// Parallel strokes across the mask at any angle, alternating direction line by line.
    /// Neighbouring strokes are joined into one zig-zag wherever the join stays inside the
    /// shape, which saves a pen lift each time.
    /// </summary>
    private static List<Point[]> Hatch(bool[] m, int w, int h, double spacing, double angleDeg, int minRun, CancellationToken ct)
    {
        spacing = Math.Max(1, spacing);
        double a = angleDeg * Math.PI / 180;
        double dx = Math.Cos(a), dy = Math.Sin(a);
        double nx = -dy, ny = dx;

        double nMin = double.MaxValue, nMax = double.MinValue, tMin = double.MaxValue, tMax = double.MinValue;
        foreach (var (cx, cy) in new[] { (0.0, 0.0), (w, 0.0), (0.0, h), (w, (double)h) })
        {
            double nv = cx * nx + cy * ny, tv = cx * dx + cy * dy;
            nMin = Math.Min(nMin, nv); nMax = Math.Max(nMax, nv);
            tMin = Math.Min(tMin, tv); tMax = Math.Max(tMax, tv);
        }

        bool Inside(double x, double y)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) return false;
            return m[(int)y * w + (int)x];
        }

        var strokes = new List<Point[]>();
        var line = new List<Point[]>();
        bool forward = true;
        int lineNo = 0;

        for (double off = nMin + spacing / 2; off < nMax; off += spacing)
        {
            if ((lineNo++ & 31) == 0) ct.ThrowIfCancellationRequested();
            line.Clear();

            double runStart = double.NaN, runLast = 0;
            for (double t = tMin; t <= tMax + 1; t += 1)
            {
                double x = off * nx + t * dx, y = off * ny + t * dy;
                if (Inside(x, y))
                {
                    if (double.IsNaN(runStart)) runStart = t;
                    runLast = t;
                }
                else if (!double.IsNaN(runStart))
                {
                    AddRun(runStart, runLast);
                    runStart = double.NaN;
                }
            }
            if (!double.IsNaN(runStart)) AddRun(runStart, runLast);
            if (line.Count == 0) continue;

            if (forward)
            {
                strokes.AddRange(line);
            }
            else
            {
                for (int i = line.Count - 1; i >= 0; i--) strokes.Add(new[] { line[i][1], line[i][0] });
            }
            forward = !forward;

            void AddRun(double t0, double t1)
            {
                if (t1 - t0 + 1 < minRun) return;
                double e = t1 + 0.999; // to the far edge of the last pixel
                line.Add(new[]
                {
                    new Point(off * nx + t0 * dx, off * ny + t0 * dy),
                    new Point(off * nx + e * dx, off * ny + e * dy),
                });
            }
        }

        return JoinStrokes(strokes, m, w, h, spacing * 1.9);
    }

    /// <summary>Chains each stroke onto the previous one when the hop between them is short and inside the shape.</summary>
    private static List<Point[]> JoinStrokes(List<Point[]> strokes, bool[] m, int w, int h, double maxGap)
    {
        var result = new List<Point[]>();
        List<Point>? current = null;

        bool HopInside(Point p, Point q)
        {
            double len = (q - p).Length;
            int steps = Math.Max(2, (int)(len * 2));
            for (int i = 1; i < steps; i++)
            {
                double t = 0.12 + 0.76 * i / steps; // ignore the very ends, which sit on pixel edges
                double x = p.X + (q.X - p.X) * t, y = p.Y + (q.Y - p.Y) * t;
                int ix = Math.Clamp((int)x, 0, w - 1), iy = Math.Clamp((int)y, 0, h - 1);
                if (!m[iy * w + ix]) return false;
            }
            return true;
        }

        foreach (var s in strokes)
        {
            if (current != null)
            {
                var last = current[^1];
                if ((s[0] - last).Length <= maxGap && HopInside(last, s[0]))
                {
                    current.AddRange(s);
                    continue;
                }
                result.Add(current.ToArray());
            }
            current = new List<Point>(s);
        }
        if (current != null) result.Add(current.ToArray());
        return result;
    }

    // ===================================================================================
    //  preview helpers
    // ===================================================================================

    private static byte[] MaskBackdrop(bool[] mask, byte alpha = 34)
    {
        var px = new byte[mask.Length * 4];
        for (int i = 0, p = 0; i < mask.Length; i++, p += 4)
        {
            if (!mask[i]) continue;
            px[p] = px[p + 1] = px[p + 2] = alpha; // premultiplied white
            px[p + 3] = alpha;
        }
        return px;
    }

#if !OVERSKIN_LINUX
    /// <summary>Frozen geometry of the paths, in working coordinates. With a colour, only that colour's paths.</summary>
    public static Geometry BuildGeometry(TraceResult result, int colour = -1)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            for (int i = 0; i < result.Paths.Count; i++)
            {
                if (colour >= 0 && result.ColourIndex != null && result.ColourIndex[i] != colour) continue;
                var path = result.Paths[i];
                if (path.Length == 0) continue;
                if (path.Length == 1)
                {
                    // a bucket click: a small plus where it lands
                    double r = Math.Max(2, Math.Min(result.Width, result.Height) / 120.0);
                    g.BeginFigure(new Point(path[0].X - r, path[0].Y), false, false);
                    g.LineTo(new Point(path[0].X + r, path[0].Y), true, false);
                    g.BeginFigure(new Point(path[0].X, path[0].Y - r), false, false);
                    g.LineTo(new Point(path[0].X, path[0].Y + r), true, false);
                    continue;
                }
                g.BeginFigure(path[0], false, false);
                for (int j = 1; j < path.Length; j++) g.LineTo(path[j], true, false);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    public static BitmapSource BuildBackdropBitmap(TraceResult result)
    {
        var bmp = BitmapSource.Create(result.Width, result.Height, 96, 96, PixelFormats.Pbgra32, null, result.Backdrop, result.Width * 4);
        bmp.Freeze();
        return bmp;
    }
#endif
}
