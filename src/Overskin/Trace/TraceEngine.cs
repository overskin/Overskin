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
    /// <summary>
    /// Outline and Colour modes: outline big areas, then fill them with one click of the
    /// program's paint bucket instead of hatching them. Needs the brush and bucket picked.
    /// </summary>
    public bool BucketFill { get; init; }
    /// <summary>Milliseconds per drawn point at the chosen speed; used to pick the quicker way to cover each area.</summary>
    public double PointMs { get; init; } = 8;
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
    /// <summary>Bucket fill: true for paths that are one click with the bucket. Null without bucket fill.</summary>
    public List<bool>? IsFill { get; init; }
    public int FillCount { get; init; }
    /// <summary>Faint picture of what was traced, premultiplied BGRA, Width × Height.</summary>
    public required byte[] Backdrop { get; init; }
    public int PointCount { get; init; }
    /// <summary>Hatching: preview with hairlines so the density, not a solid blob, is what shows.</summary>
    public bool ThinPreview { get; init; }
    /// <summary>Why nothing came out, when that is the case.</summary>
    public string? Message { get; init; }
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
                if (o.BucketFill)
                {
                    // every outline is already drawn, so each big shape only needs its inside clicked
                    var seeds = PlanFill(mask, w, h, o.Spacing, ct).Seeds;
                    var fills = new List<bool>(Enumerable.Repeat(false, paths.Count));
                    foreach (var s in seeds)
                    {
                        paths.Add(new[] { s });
                        fills.Add(true);
                    }
                    return new TraceResult
                    {
                        Width = w, Height = h, Paths = paths, IsFill = fills, FillCount = seeds.Count,
                        Backdrop = MaskBackdrop(mask, 90), PointCount = CountPoints(paths),
                        Message = paths.Count > 0 ? null : "Nothing to draw. Try another threshold, or Invert.",
                    };
                }
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

        // fast mode: soft edges between two colours come out as one- or two-pixel slivers of
        // some in-between colour, each costing a stroke but invisible at brush size; a
        // majority vote over each pixel's neighbours folds them into the colours either side
        if (o.BucketFill) labels = Majority(Majority(labels, w, h, colours.Length), w, h, colours.Length);

        // lightest colours first, so darker detail lands on top
        var order = Enumerable.Range(0, colours.Length)
            .Where(k => !palette[k].Skip)
            .OrderByDescending(k => Luma(colours[k]))
            .ToList();

        var paths = new List<Point[]>();
        var index = new List<int>();
        List<bool>? isFill = o.BucketFill ? new() : null;
        var seedsByColour = new List<(int Colour, List<Point> Seeds)>();
        foreach (int k in order)
        {
            ct.ThrowIfCancellationRequested();
            var mask = new bool[w * h];
            bool any = false;
            for (int i = 0; i < mask.Length; i++)
                if (labels[i] == k) { mask[i] = true; any = true; }
            if (!any) continue;

            var mine = new List<Point[]>();
            if (o.BucketFill)
            {
                // big areas: their whole border (holes too) now, one bucket click each later;
                // areas too thin to fill are hatched as usual
                var (loops, seeds, brushMask, lines) = ChooseFill(mask, w, h, o, ct);
                mine.AddRange(Hatch(brushMask, w, h, o.Spacing, o.Angle, MinRun(o), ct));
                if (o.OutlineToo) mine.AddRange(Outlines(brushMask, w, h, o, ct));
                mine.AddRange(lines);
                mine.AddRange(loops);
                mine = OrderNearest(mine, ct);
                if (seeds.Count > 0) seedsByColour.Add((k, seeds));
            }
            else
            {
                mine.AddRange(Hatch(mask, w, h, o.Spacing, o.Angle, MinRun(o), ct));
                if (o.Crosshatch) mine.AddRange(Hatch(mask, w, h, o.Spacing, o.Angle + 90, MinRun(o), ct));
                if (o.OutlineToo) mine.AddRange(OrderNearest(Outlines(mask, w, h, o, ct), ct));
            }
            paths.AddRange(mine);
            index.AddRange(Enumerable.Repeat(k, mine.Count));
            isFill?.AddRange(Enumerable.Repeat(false, mine.Count));
        }

        // then the bucket, once every border is down: a fill can't leak past its own outline
        int fillCount = 0;
        foreach (var (k, seeds) in seedsByColour)
        {
            foreach (var s in seeds)
            {
                paths.Add(new[] { s });
                index.Add(k);
                isFill!.Add(true);
                fillCount++;
            }
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
            IsFill = isFill,
            FillCount = fillCount,
            Backdrop = backdrop,
            PointCount = CountPoints(paths),
            Message = paths.Count > 0 ? null : "Nothing to draw with these colours.",
        };
    }

    public static double Luma(Color c) => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;

    /// <summary>Each pixel takes the most common label among itself and its 8 neighbours (ties keep it).</summary>
    private static int[] Majority(int[] labels, int w, int h, int count)
    {
        var result = new int[labels.Length];
        var votes = new int[count];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x, own = labels[i];
                Array.Clear(votes);
                for (int ny = Math.Max(0, y - 1); ny <= Math.Min(h - 1, y + 1); ny++)
                    for (int nx = Math.Max(0, x - 1); nx <= Math.Min(w - 1, x + 1); nx++)
                        votes[labels[ny * w + nx]]++;
                int best = own;
                for (int k = 0; k < count; k++)
                    if (votes[k] > votes[best]) best = k;
                result[i] = best;
            }
        }
        return result;
    }

    // ===================================================================================
    //  resampling and smoothing
    // ===================================================================================

    private static byte[] Resample(byte[] s, int sw, int sh, int w, int h)
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
    private static byte[] Blur(byte[] src, int w, int h, int radius)
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
    private static List<Point[]> JoinStraight(List<Point[]> paths, CancellationToken ct)
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
    private static bool[] Thin(bool[] src, int w, int h, CancellationToken ct)
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
    private static void RemoveStairs(bool[] m, int w, int h)
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

    private static List<Point[]> WalkSkeleton(bool[] sk, int w, int h, int minLength, CancellationToken ct)
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

    private static Point[] SimplifyOpen(Point[] pts, double tolerance)
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

    // ===================================================================================
    //  bucket fill planning
    // ===================================================================================

    /// <summary>
    /// Splits a mask into areas the program's paint bucket can fill and areas too thin for it.
    /// Once a brush of <paramref name="brush"/> pixels has gone round an area's border, whatever
    /// is still blank inside is one or more "islands"; each island needs one bucket click, at
    /// its deepest point so the click can't land on the outline. Areas with no island left
    /// inside are thin, and are better drawn with the brush.
    /// </summary>
    private static (bool[] Fillable, bool[] Thin, List<Point> Seeds, int[] Component, List<int> SeedComponent, float[] Distance, List<(int Component, List<int> Pixels, int Deepest)> Slivers)
        PlanFill(bool[] mask, int w, int h, double brush, CancellationToken ct)
    {
        int n = w * h;
        float[] dist = InsideDistance(mask, w, h);
        ct.ThrowIfCancellationRequested();

        // the outline is centred on the border, so it paints about half a brush inwards
        // ("reach"); whatever is deeper than that stays blank until the bucket. Clicks go a
        // little deeper still ("cover"), clear of the brush's soft edge.
        double reach = Math.Max(1, brush) / 2 + 0.5;
        double cover = Math.Max(1, brush) / 2 + 1.5;
        const int MinIsland = 4;

        int[] component = Label(mask, w, h);
        var fillableComponent = new HashSet<int>();
        var seeds = new List<Point>();
        var seedComponent = new List<int>();
        var unseeded = new List<(int Component, List<int> Pixels, int Deepest)>();
        var seen = new bool[n];
        var queue = new Queue<int>();
        var island = new List<int>();

        for (int start = 0; start < n; start++)
        {
            if (seen[start] || dist[start] <= reach) continue;
            if ((seeds.Count & 63) == 0) ct.ThrowIfCancellationRequested();

            // flood one blank island, remembering its deepest pixel
            island.Clear();
            int deepest = start;
            seen[start] = true;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                island.Add(i);
                if (dist[i] > dist[deepest]) deepest = i;
                int x = i % w, y = i / w;
                if (x > 0) Visit(i - 1);
                if (x < w - 1) Visit(i + 1);
                if (y > 0) Visit(i - w);
                if (y < h - 1) Visit(i + w);
            }

            if (island.Count >= MinIsland && dist[deepest] > cover)
            {
                fillableComponent.Add(component[deepest]);
                seeds.Add(new Point(deepest % w + 0.5, deepest / w + 0.5));
                seedComponent.Add(component[deepest]);
            }
            else
            {
                // too narrow to click safely: if the area is filled, the brush must cover it
                unseeded.Add((component[deepest], new List<int>(island), deepest));
            }
        }

        void Visit(int j)
        {
            if (seen[j] || dist[j] <= reach) return;
            seen[j] = true;
            queue.Enqueue(j);
        }

        var fillable = new bool[n];
        var thin = new bool[n];
        for (int i = 0; i < n; i++)
        {
            if (!mask[i]) continue;
            if (fillableComponent.Contains(component[i])) fillable[i] = true;
            else thin[i] = true;
        }
        var slivers = unseeded.Where(s => fillableComponent.Contains(s.Component)).ToList();
        return (fillable, thin, seeds, component, seedComponent, dist, slivers);
    }

    /// <summary>
    /// Brush strokes for the blank slivers a filled area's outline pinches off: a dot for a
    /// tiny one, a line down the middle of a longer one.
    /// </summary>
    private static List<Point[]> SliverStrokes(List<(int Component, List<int> Pixels, int Deepest)> slivers, int w, int h, TraceOptions o, CancellationToken ct)
    {
        var strokes = new List<Point[]>();
        bool[]? longOnes = null;
        foreach (var s in slivers)
        {
            if (s.Pixels.Count <= 6)
            {
                var p = new Point(s.Deepest % w + 0.5, s.Deepest / w + 0.5);
                strokes.Add(new[] { p, p });
                continue;
            }
            longOnes ??= new bool[w * h];
            foreach (int i in s.Pixels) longOnes[i] = true;
        }
        if (longOnes != null)
            strokes.AddRange(Centrelines(longOnes, w, h, new TraceOptions { MinPathLength = 1, Tolerance = o.Tolerance }, ct));
        return strokes;
    }

    /// <summary>
    /// Bucket mode, per area: outline-and-fill or plain hatching, whichever takes less time.
    /// Outlining wins for big smooth areas; hatching can win for small or very ragged ones,
    /// whose borders cost more points than a few hatch lines. Returns the outline loops and
    /// bucket clicks to draw, and the mask of everything left for the brush.
    /// </summary>
    private static (List<Point[]> Loops, List<Point> Seeds, bool[] Brush, List<Point[]> Lines) ChooseFill(bool[] mask, int w, int h, TraceOptions o, CancellationToken ct)
    {
        var plan = PlanFill(mask, w, h, o.Spacing, ct);
        var component = plan.Component;
        var (brushOnly, lines) = SplitLines(plan.Thin, plan.Component, plan.Distance, w, h, o, ct);
        if (plan.Seeds.Count == 0) return (new(), new(), brushOnly, lines);

        // timings, matching MouseDrawer: a point is one refresh, a pen lift a few short waits,
        // a bucket click a lift plus the wait for the flood
        double point = Math.Max(0.5, o.PointMs);
        double lift = 4 * Math.Clamp(point / 2, 2, 10);
        double click = lift + 45;

        // outlines of every fillable area, each loop credited to the area on its inner side
        var loops = new List<Point[]>();
        var loopComponent = new List<int>();
        var outlineCost = new Dictionary<int, double>();
        foreach (var raw in Contours(plan.Fillable, w, h, 4, ct))
        {
            int c = LoopOwner(raw, component, w, h);
            var loop = SimplifyClosed(raw, o.Tolerance);
            loops.Add(loop);
            loopComponent.Add(c);
            outlineCost[c] = outlineCost.GetValueOrDefault(c) + loop.Length * point + lift;
        }
        var clicks = new Dictionary<int, int>();
        foreach (int c in plan.SeedComponent)
        {
            outlineCost[c] = outlineCost.GetValueOrDefault(c) + click;
            clicks[c] = clicks.GetValueOrDefault(c) + 1;
        }

        // hatching the same areas: two points per run on each hatch row; neighbouring rows
        // mostly join into one zig-zag, so count a lift for about every third run
        var hatchCost = new Dictionary<int, double>();
        double spacing = Math.Max(1, o.Spacing);
        for (double row = spacing / 2; row < h; row += spacing)
        {
            int y = (int)row, start = y * w, previous = 0;
            for (int x = 0; x < w; x++)
            {
                int c = plan.Fillable[start + x] ? component[start + x] : 0;
                if (c != 0 && c != previous) hatchCost[c] = hatchCost.GetValueOrDefault(c) + 2 * point + lift / 3;
                previous = c;
            }
        }
        ct.ThrowIfCancellationRequested();

        // with outlines on, hatched areas get the same outline on top, so only the fill click
        // is weighed against the hatch lines
        var hatchInstead = new HashSet<int>();
        foreach (var (c, cost) in outlineCost)
        {
            double hatch = hatchCost.GetValueOrDefault(c, double.MaxValue);
            double fill = o.OutlineToo ? clicks.GetValueOrDefault(c) * click : cost;
            if (fill >= hatch) hatchInstead.Add(c);
        }

        var brush = brushOnly;
        if (hatchInstead.Count > 0)
        {
            brush = (bool[])brushOnly.Clone();
            for (int i = 0; i < brush.Length; i++)
                if (plan.Fillable[i] && hatchInstead.Contains(component[i])) brush[i] = true;
        }

        var keptLoops = new List<Point[]>();
        for (int i = 0; i < loops.Count; i++)
            if (!hatchInstead.Contains(loopComponent[i])) keptLoops.Add(loops[i]);
        var keptSeeds = new List<Point>();
        for (int s = 0; s < plan.Seeds.Count; s++)
            if (!hatchInstead.Contains(plan.SeedComponent[s])) keptSeeds.Add(plan.Seeds[s]);
        var slivers = plan.Slivers.Where(s => !hatchInstead.Contains(s.Component)).ToList();
        if (slivers.Count > 0) lines = lines.Concat(SliverStrokes(slivers, w, h, o, ct)).ToList();
        return (keptLoops, keptSeeds, brush, lines);
    }

    /// <summary>
    /// Areas no wider than the brush (lines, lettering, the outlines in cartoons) are drawn
    /// as one stroke down their middle instead of being hatched across, which would take a
    /// pen lift for every hatch row they cross. Tiny specks stay with the hatching.
    /// Returns what is left for hatching, and the centrelines.
    /// </summary>
    private static (bool[] Remaining, List<Point[]> Lines) SplitLines(bool[] thin, int[] component, float[] dist, int w, int h, TraceOptions o, CancellationToken ct)
    {
        double brush = Math.Max(1, o.Spacing);
        double maxHalfWidth = brush / 2 + 0.5; // one stroke down the middle covers the whole width
        double minSpan = Math.Max(4, brush * 2); // smaller than this is a speck

        var widest = new Dictionary<int, float>();
        var box = new Dictionary<int, (int X0, int Y0, int X1, int Y1)>();
        for (int i = 0; i < thin.Length; i++)
        {
            if (!thin[i]) continue;
            int c = component[i], x = i % w, y = i / w;
            widest[c] = Math.Max(widest.GetValueOrDefault(c), dist[i]);
            box[c] = box.TryGetValue(c, out var b)
                ? (Math.Min(b.X0, x), Math.Min(b.Y0, y), Math.Max(b.X1, x), Math.Max(b.Y1, y))
                : (x, y, x, y);
        }

        var lineAreas = new HashSet<int>();
        foreach (var (c, half) in widest)
        {
            var b = box[c];
            if (half <= maxHalfWidth && Math.Max(b.X1 - b.X0, b.Y1 - b.Y0) + 1 >= minSpan) lineAreas.Add(c);
        }
        if (lineAreas.Count == 0) return (thin, new());

        var rest = (bool[])thin.Clone();
        var lineMask = new bool[thin.Length];
        for (int i = 0; i < thin.Length; i++)
        {
            if (thin[i] && lineAreas.Contains(component[i]))
            {
                lineMask[i] = true;
                rest[i] = false;
            }
        }
        ct.ThrowIfCancellationRequested();
        var lines = Centrelines(lineMask, w, h, new TraceOptions { MinPathLength = 2, Tolerance = o.Tolerance }, ct);
        return (rest, lines);
    }

    /// <summary>
    /// The area a contour loop belongs to: the pixel just inside its first edge. Loops keep
    /// ink on the right, and points are corners, so the direction of the first edge says
    /// which pixel that is.
    /// </summary>
    private static int LoopOwner(Point[] loop, int[] component, int w, int h)
    {
        int vx = (int)loop[0].X, vy = (int)loop[0].Y;
        double dx = loop[1].X - loop[0].X, dy = loop[1].Y - loop[0].Y;
        (int x, int y) = dx > 0 ? (vx, vy) : dy > 0 ? (vx - 1, vy) : dx < 0 ? (vx - 1, vy - 1) : (vx, vy - 1);
        x = Math.Clamp(x, 0, w - 1);
        y = Math.Clamp(y, 0, h - 1);
        return component[y * w + x];
    }

    /// <summary>
    /// For every pixel in the mask, roughly how far it is from the nearest pixel outside it
    /// (or off the picture), by a two-pass chamfer transform. Pixels outside are 0.
    /// </summary>
    private static float[] InsideDistance(bool[] m, int w, int h)
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

    /// <summary>Numbers the 4-connected areas of a mask (1, 2, 3 ...); 0 outside it.</summary>
    private static int[] Label(bool[] m, int w, int h)
    {
        var labels = new int[w * h];
        var stack = new Stack<int>();
        int count = 0;
        for (int start = 0; start < labels.Length; start++)
        {
            if (!m[start] || labels[start] != 0) continue;
            int id = ++count;
            labels[start] = id;
            stack.Push(start);
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                int x = i % w, y = i / w;
                if (x > 0 && m[i - 1] && labels[i - 1] == 0) { labels[i - 1] = id; stack.Push(i - 1); }
                if (x < w - 1 && m[i + 1] && labels[i + 1] == 0) { labels[i + 1] = id; stack.Push(i + 1); }
                if (y > 0 && m[i - w] && labels[i - w] == 0) { labels[i - w] = id; stack.Push(i - w); }
                if (y < h - 1 && m[i + w] && labels[i + w] == 0) { labels[i + w] = id; stack.Push(i + w); }
            }
        }
        return labels;
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
