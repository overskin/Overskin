using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Overskin.Linux.Core;

namespace Overskin.Linux.Interface;

/// <summary>
/// Typing into a screenshot's text, like a document (as on Windows). The first time you type on a
/// line, its font, size, weight and colour are worked out by drawing the line's own words in each
/// common font and keeping the one that matches the picture best. New letters are drawn in that
/// style and the rest of the line moves along to make room. Works on text on a plain background.
/// </summary>
public sealed partial class EditorWindow
{
    /// <summary>How a line of text looks: the font it matched, its size in pixels, ink and background colours, and where its baseline is.</summary>
    /// <param name="Edges">0 grey soft edges (macOS and most Linux); 1 sub-pixel edges with red on the left of each pixel (RGB); 2 the other way round (BGR).</param>
    /// <param name="Gamma">How dark the soft edges are: under 1 darker, over 1 lighter.</param>
    internal sealed record TextStyle(Typeface Face, string Name, double Size, Color Ink, Color Paper, double Baseline, double Score,
        int Edges = 0, double Gamma = 1, int Filter = 0, double Saturation = 1, double YScale = 1, double LookScore = 1);

    private readonly Dictionary<int, TextStyle?> _lineStyles = new();

    /// <summary>What each line matched before it was changed here, for the reset button.</summary>
    private readonly Dictionary<int, TextStyle?> _matchedStyles = new();

    /// <summary>How many of <see cref="CommonFonts"/> (from the start) are always tried, when the computer has them.</summary>
    private const int CoreFonts = 14;

    private static readonly string[] CommonFonts =
    {
        // macOS's own (SF Pro is the system font), then the ones nearly every computer has
        "SF Pro", "SF Pro Text", "SF Pro Display", "Helvetica Neue", "Helvetica", "Arial", "Georgia", "Times New Roman", "Verdana",
        "Menlo", "SF Mono", "Courier New",
        // Linux desktops' own
        "Cantarell", "DejaVu Sans",
        // used by apps and websites, when this computer has them
        "Segoe UI", "Calibri", "Tahoma", "Trebuchet MS", "Lucida Grande", "Avenir", "Avenir Next", "Noto Sans", "Liberation Sans",
        "Ubuntu", "Open Sans", "Lato", "Source Sans Pro", "Montserrat", "Poppins", "Monaco", "DejaVu Sans Mono", "Liberation Mono",
        "Ubuntu Mono", "Consolas", "JetBrains Mono", "Fira Code", "Comic Sans MS",
    };

    /// <summary>Fonts that come with Overskin for matching (many apps and websites use them), with the weights included.</summary>
    private static readonly (string Name, FontWeight[] Weights)[] MatchingFonts =
    {
        ("Inter", new[] { FontWeight.Normal, FontWeight.Medium, FontWeight.SemiBold, FontWeight.Bold }),
        ("Roboto", new[] { FontWeight.Normal, FontWeight.Medium, FontWeight.Bold }),
    };

    private static FontFamily Bundled(string name) => new($"avares://overskin/Assets/Fonts#{name}");

    /// <summary>A font family's name as people know it ("Inter", not where it's kept).</summary>
    private static string NameOf(FontFamily family) => family.Name;

    private static List<(Typeface Face, string Name)>? _faces;
    private static HashSet<string>? _installed;

    private static HashSet<string> Installed()
    {
        if (_installed != null) return _installed;
        try { _installed = new HashSet<string>(FontManager.Current.SystemFonts.Select(f => f.Name), StringComparer.OrdinalIgnoreCase); }
        catch { _installed = new(StringComparer.OrdinalIgnoreCase); }
        return _installed;
    }

    /// <summary>Whether the font really has this weight (rather than one made up from another).</summary>
    private static bool HasWeight(FontFamily family, FontWeight weight)
    {
        try
        {
            return FontManager.Current.TryGetGlyphTypeface(new Typeface(family, FontStyle.Normal, weight), out var g)
                   && g.Weight == weight && string.Equals(g.FamilyName, family.Name, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>The fonts to try, each in regular and bold (and the in-between weights it really has), that this computer has.</summary>
    private static List<(Typeface Face, string Name)> Faces()
    {
        if (_faces != null) return _faces;
        var installed = Installed();
        var list = new List<(Typeface, string)>();
        foreach (var name in CommonFonts.Where(installed.Contains))
        {
            var family = new FontFamily(name);
            list.Add((new Typeface(family), name));
            if (HasWeight(family, FontWeight.SemiBold)) list.Add((new Typeface(family, FontStyle.Normal, FontWeight.SemiBold), name + " Semibold"));
            if (HasWeight(family, FontWeight.Medium)) list.Add((new Typeface(family, FontStyle.Normal, FontWeight.Medium), name + " Medium"));
            if (HasWeight(family, FontWeight.Light)) list.Add((new Typeface(family, FontStyle.Normal, FontWeight.Light), name + " Light"));
            list.Add((new Typeface(family, FontStyle.Normal, FontWeight.Bold), name + " Bold"));
        }
        // the ones that come with Overskin, when this computer doesn't have its own copy
        foreach (var (name, weights) in MatchingFonts)
        {
            if (installed.Contains(name)) continue;
            var family = Bundled(name);
            foreach (var weight in weights)
                list.Add((new Typeface(family, FontStyle.Normal, weight), weight == FontWeight.Normal ? name : $"{name} {WeightName(weight)}"));
        }
        return _faces = list;
    }

    private static bool IsCore(Typeface face) => CommonFonts.Take(CoreFonts).Contains(face.FontFamily.Name, StringComparer.OrdinalIgnoreCase);

    private static FormattedText Ft(string text, Typeface face, double size, IBrush? brush = null) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, brush ?? Brushes.Black);

    private static Rect InkOf(FormattedText ft, Point at) => ft.BuildGeometry(at)?.Bounds ?? default;

    /// <summary>
    /// Text drawn in black on nothing, as how much each pixel's red, green and blue are covered (0 to 1).
    /// With sub-pixel edges it's drawn three times as wide, one column per sub-pixel, and softened
    /// across neighbouring sub-pixels, which gives the coloured fringes.
    /// </summary>
    private static float[] Coverage3(string text, Typeface face, double size, int edges, double gamma, Point origin, int w, int h,
        int filter = 0, double sat = 1, double yScale = 1) =>
        Shape(RawCoverage(text, face, size, edges, origin, w, h, yScale), w, h, edges, filter, gamma, sat);

    /// <summary>
    /// The colours text is drawn in to see how much it covers: the line's own. Light text on a dark
    /// background is drawn heavier than dark on light (on a Mac and here alike), so drawing it in
    /// other colours would make the right font look the wrong weight.
    /// </summary>
    private static Color _drawInk = Colors.Black, _drawPaper = Colors.White;

    private static void DrawIn(Color ink, Color paper) => (_drawInk, _drawPaper) = (ink, paper);

    /// <summary>The text drawn as coverage: one value per pixel, or per sub-pixel (three across) for sub-pixel edges.</summary>
    private static float[] RawCoverage(string text, Typeface face, double size, int edges, Point origin, int w, int h, double yScale = 1)
    {
        int sx = edges == 0 ? 1 : 3;
        int rw = Math.Max(1, w * sx);
        // drawn on a solid background and read back from how far each pixel moved towards the ink:
        // text drawn on nothing (see-through) comes out much bolder than on a solid background,
        // which is how screenshots are
        var ink = _drawInk;
        var paper = _drawPaper;
        if (ColourGap(ink, paper) < 40) (ink, paper) = (Colors.Black, Colors.White);
        var ft = Ft(text, face, size, new SolidColorBrush(ink));
        var bmp = Imaging.Render(new PixelSize(rw, Math.Max(1, h)), ctx =>
        {
            ctx.FillRectangle(new SolidColorBrush(paper), new Rect(0, 0, rw, Math.Max(1, h)));
            if (sx != 1 || yScale != 1)
            {
                // the height stretch keeps the baseline where it is
                double by = origin.Y + ft.Baseline;
                using (ctx.PushTransform(Matrix.CreateTranslation(0, -by) * Matrix.CreateScale(sx, yScale) * Matrix.CreateTranslation(0, by)))
                    ctx.DrawText(ft, origin);
            }
            else ctx.DrawText(ft, origin);
        });
        var px = Imaging.Pixels(bmp, out _);
        var raw = new float[rw * h];
        // the colour (blue, green or red) where ink and background differ most tells best
        int ch = 0;
        double span = ink.B - paper.B;
        if (Math.Abs(ink.G - paper.G) > Math.Abs(span)) { ch = 1; span = ink.G - paper.G; }
        if (Math.Abs(ink.R - paper.R) > Math.Abs(span)) { ch = 2; span = ink.R - paper.R; }
        double from = ch == 0 ? paper.B : ch == 1 ? paper.G : paper.R;
        for (int k = 0; k < raw.Length; k++) raw[k] = (float)Math.Clamp((px[k * 4 + ch] - from) / span, 0, 1);
        return raw;
    }

    private static double ColourGap(Color a, Color b)
    {
        double db = a.B - b.B, dg = a.G - b.G, dr = a.R - b.R;
        return Math.Sqrt(db * db + dg * dg + dr * dr);
    }

    // sub-pixel filters across neighbouring sub-pixels: the usual soft one, a lighter one and a sharp one
    private static readonly float[][] Filters =
    {
        new[] { 1 / 9f, 2 / 9f, 3 / 9f, 2 / 9f, 1 / 9f },
        new[] { 0f, 1 / 3f, 1 / 3f, 1 / 3f, 0f },
        new[] { 0f, 1 / 4f, 2 / 4f, 1 / 4f, 0f },
    };

    /// <summary>
    /// Coverage per pixel as red, green and blue: for sub-pixel edges, the sub-pixels filtered, then
    /// the colours pushed apart by <paramref name="sat"/>; then the edges made darker or lighter by
    /// <paramref name="gamma"/>.
    /// </summary>
    private static float[] Shape(float[] raw, int w, int h, int edges, int filter, double gamma, double sat)
    {
        var o = new float[w * h * 3];
        float G(float a) => gamma == 1 ? a : (float)Math.Pow(Math.Clamp(a, 0, 1), gamma);
        if (edges == 0)
        {
            for (int k = 0; k < w * h; k++) o[k * 3] = o[k * 3 + 1] = o[k * 3 + 2] = G(raw[k]);
            return o;
        }
        var f = Filters[Math.Clamp(filter, 0, Filters.Length - 1)];
        int rw = w * 3;
        for (int y = 0; y < h; y++)
        {
            int row = y * rw;
            for (int x = 0; x < w; x++)
            {
                float r = 0, g = 0, b = 0;
                for (int c = 0; c < 3; c++)
                {
                    int s0 = 3 * x + c;
                    float v = 0;
                    for (int d = -2; d <= 2; d++)
                    {
                        int s = s0 + d;
                        if (s >= 0 && s < rw) v += f[d + 2] * raw[row + s];
                    }
                    int ch = edges == 1 ? c : 2 - c;
                    if (ch == 0) r = v; else if (ch == 1) g = v; else b = v;
                }
                if (sat != 1)
                {
                    float m = (r + g + b) / 3;
                    r = m + (float)sat * (r - m);
                    g = m + (float)sat * (g - m);
                    b = m + (float)sat * (b - m);
                }
                int k = (y * w + x) * 3;
                o[k] = G(Math.Clamp(r, 0, 1));
                o[k + 1] = G(Math.Clamp(g, 0, 1));
                o[k + 2] = G(Math.Clamp(b, 0, 1));
            }
        }
        return o;
    }

    /// <summary>The style of a line, worked out once (null when it's on a busy background).</summary>
    private TextStyle? StyleFor(int line)
    {
        if (_lineStyles.TryGetValue(line, out var known)) return known;
        var style = MatchStyle(line);
        _lineStyles[line] = style;
        return style;
    }

    /// <summary>For the self-test: which fonts the last match tried, and the closest few.</summary>
    internal static string LastRankForTest = "";

    private readonly record struct Sample(string Text, PixelRect Crop, float[] Cover, Rect Ink, float[] Cover3);

    private TextStyle? MatchStyle(int line)
    {
        var (lo, hi, y0, y1, right, left) = LineBand(line);
        var px = BasePixels();
        int w = _pxW;
        var bg = BackgroundOf(px, w, Math.Max(0, (int)left - 2), y0, Math.Min(w, right), y1);
        if (bg.Share < 0.5 || y1 <= y0) return null;

        double Dist(int o, double b, double g, double r)
        {
            double db = px[o] - b, dg = px[o + 1] - g, dr = px[o + 2] - r;
            return Math.Sqrt(db * db + dg * dg + dr * dr);
        }

        // the ink colour: the pixels furthest from the background, averaged
        var far = new List<(double D, int O)>();
        for (int y = y0; y < y1; y++)
            for (int x = Math.Max(0, (int)left); x < Math.Min(w, right); x++)
            {
                int o = (y * w + x) * 4;
                far.Add((Dist(o, bg.B, bg.G, bg.R), o));
            }
        far.Sort((p, q) => q.D.CompareTo(p.D));
        double contrast = far.Count == 0 ? 0 : far[Math.Min(far.Count - 1, far.Count / 40)].D;
        if (contrast < 40) return null;
        double sb = 0, sg = 0, sr = 0;
        int n = 0;
        // the ink colour from only the most solid pixels (the middle of the strokes): mixing in the
        // soft edges makes black come out grey, and then everything matched against it is off
        double solid = far[Math.Min(far.Count - 1, Math.Max(2, far.Count / 200))].D;
        foreach (var (d, o) in far)
        {
            if (d < solid * 0.96) break;
            sb += px[o]; sg += px[o + 1]; sr += px[o + 2]; n++;
        }
        var ink = Color.FromRgb((byte)(sr / n), (byte)(sg / n), (byte)(sb / n));
        double inkDist = Math.Max(1, Math.Sqrt(Math.Pow(ink.B - bg.B, 2) + Math.Pow(ink.G - bg.G, 2) + Math.Pow(ink.R - bg.R, 2)));

        // the line's longest words are what each font is tried on
        var samples = Enumerable.Range(lo, hi - lo + 1).Where(i => _words[i].Text.Length >= 2)
            .OrderByDescending(i => _words[i].Text.Length).Take(3).ToList();
        if (samples.Count == 0) samples = new List<int> { lo };

        // how much ink each sample's pixels hold (0 to 1), and where that ink is
        var real = new List<Sample>();
        foreach (int i in samples)
        {
            var b = _words[i].Bounds;
            double dx = 2 + b.Height * 0.15;
            int cx0 = Math.Max(0, (int)Math.Floor(b.Left - dx)), cx1 = Math.Min(w, (int)Math.Ceiling(b.Right + dx));
            int cy0 = y0, cy1 = y1;
            int cw = cx1 - cx0, ch = cy1 - cy0;
            if (cw < 3 || ch < 3) continue;
            var cover = new float[cw * ch];
            var cover3 = new float[cw * ch * 3];
            double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, bt = double.MinValue;
            for (int y = 0; y < ch; y++)
                for (int x = 0; x < cw; x++)
                {
                    int o = ((cy0 + y) * w + cx0 + x) * 4;
                    float c = (float)Math.Clamp(Dist(o, bg.B, bg.G, bg.R) / inkDist, 0, 1);
                    cover[y * cw + x] = c;
                    // each colour on its own, where the ink and background differ enough in it to tell
                    for (int k = 0; k < 3; k++)
                    {
                        double inkK = k == 0 ? ink.R : k == 1 ? ink.G : ink.B, bgK = k == 0 ? bg.R : k == 1 ? bg.G : bg.B;
                        int pxK = px[o + 2 - k];
                        double span = inkK - bgK;
                        cover3[(y * cw + x) * 3 + k] = Math.Abs(span) >= 40 ? (float)Math.Clamp((pxK - bgK) / span, 0, 1) : c;
                    }
                    if (c >= 0.4f) { l = Math.Min(l, x); t = Math.Min(t, y); r = Math.Max(r, x + 1); bt = Math.Max(bt, y + 1); }
                }
            if (r <= l) continue;
            real.Add(new Sample(_words[i].Text, new PixelRect(cx0, cy0, cw, ch), cover, new Rect(l, t, r - l, bt - t), cover3));
        }
        if (real.Count == 0) return null;

        // sub-pixel text: grey text with coloured edges. Then fonts are compared with coloured edges
        // too, or the softer strokes would pass for a bolder font. Which way round is tried on the first font.
        int sub = 0;
        bool greyText = Math.Max(Math.Abs(ink.R - ink.G), Math.Abs(ink.G - ink.B)) < 24 && Math.Max(Math.Abs(bg.R - bg.G), Math.Abs(bg.G - bg.B)) < 24;
        if (greyText)
        {
            int edgePx = 0, tinted = 0;
            foreach (var rr in real)
                for (int k = 0; k < rr.Cover.Length; k++)
                {
                    if (rr.Cover[k] < 0.1f || rr.Cover[k] > 0.9f) continue;
                    edgePx++;
                    float cr = rr.Cover3[k * 3], cb = rr.Cover3[k * 3 + 2];
                    if (Math.Abs(cr - cb) > 0.25f) tinted++;
                }
            if (edgePx > 0 && tinted > edgePx * 0.15) sub = -1; // decided below
        }

        // try every font: sized so the word's ink is as wide as in the picture, placed over it, compared pixel by pixel
        var paper = Color.FromRgb((byte)bg.R, (byte)bg.G, (byte)bg.B);
        DrawIn(ink, paper);
        try
        {
        var allFaces = Faces();
        var order = allFaces.Select(f => f.Name).ToList();
        int coreCount = allFaces.Count(f => IsCore(f.Face));
        double Biased(TextStyle t) => t.Score * (1 + 0.006 * Math.Min(coreCount, Math.Max(0, order.IndexOf(t.Name))) / 3);
        List<TextStyle> Rank(IEnumerable<(Typeface Face, string Name)> faces, int filter, double sat, double gamma)
        {
            var ranked = new List<TextStyle>();
            foreach (var (face, name) in faces)
            {
                double total = 0, sizeSum = 0, weight = 0, baseline = 0;
                bool ok = true;
                for (int s = 0; s < real.Count; s++)
                {
                    var rs = real[s];
                    var g100 = InkOf(Ft(rs.Text, face, 100), new Point());
                    if (g100.Width < 1) { ok = false; break; }
                    double size0 = 100 * rs.Ink.Width / g100.Width;
                    // the height has to fit too, or it's not this font
                    if (Math.Abs(g100.Height * size0 / 100 - rs.Ink.Height) > Math.Max(2, rs.Ink.Height * 0.25)) { ok = false; break; }
                    if (sub == -1)
                    {
                        double rgb = Compare(rs.Text, face, size0, rs.Crop, rs.Cover, rs.Ink, rs.Cover3, 1, align: true).Score;
                        double bgr = Compare(rs.Text, face, size0, rs.Crop, rs.Cover, rs.Ink, rs.Cover3, 2, align: true).Score;
                        sub = rgb <= bgr ? 1 : 2;
                    }
                    // placed exactly (text isn't drawn quite where it's asked, so placing it by its box alone
                    // can make the right font look further off than a wrong one); the size is refined later
                    var (bestScore, bestBase) = Compare(rs.Text, face, size0, rs.Crop, rs.Cover, rs.Ink, rs.Cover3, Math.Max(0, sub), filter, sat, gamma, align: true);
                    double bestSize = size0;
                    total += bestScore * rs.Text.Length;
                    sizeSum += bestSize * rs.Text.Length;
                    weight += rs.Text.Length;
                    if (s == 0) baseline = bestBase;
                }
                if (!ok || weight == 0) continue;
                ranked.Add(new TextStyle(face, name, sizeSum / weight, ink, paper, baseline, total / weight));
            }
            // near-ties go to the more common font (earlier in the list); the lean only applies among
            // the usual fonts, or a long list would push a font at its end out of reach
            ranked.Sort((u, v) => Biased(u).CompareTo(Biased(v)));
            return ranked;
        }

        // The quick look: the usual fonts are always compared; of the others, only the few whose
        // longest word comes out closest to the right shape, so more fonts don't make matching slower
        IEnumerable<(Typeface Face, string Name)> Likely(List<(Typeface Face, string Name)> all)
        {
            var r0 = real[0];
            var extras = all.Where(f => !IsCore(f.Face)).Select(f =>
            {
                var g = InkOf(Ft(r0.Text, f.Face, 100), new Point());
                if (g.Width < 1) return (f, Off: double.MaxValue);
                double size0 = 100 * r0.Ink.Width / g.Width;
                if (Math.Abs(g.Height * size0 / 100 - r0.Ink.Height) > Math.Max(2, r0.Ink.Height * 0.25)) return (f, Off: double.MaxValue);
                return (f, Off: Compare(r0.Text, f.Face, size0, r0.Crop, r0.Cover, r0.Ink, r0.Cover3).Score);
            }).Where(x => x.Off < double.MaxValue).OrderBy(x => x.Off).Take(5).Select(x => x.f).ToHashSet();
            return all.Where(f => IsCore(f.Face) || extras.Contains(f)); // in the usual order, which settles near-ties
        }

        // the size, finely: the ink's width runs a little wide from soft edges, so try just under it too
        TextStyle Fine(TextStyle best, int filter, double sat, double gamma)
        {
            (double Score, double Baseline) At(double size, bool align)
            {
                double sum = 0, weightSum = 0, firstBase = 0;
                for (int s = 0; s < real.Count; s++)
                {
                    var rs = real[s];
                    var (score, baseY) = Compare(rs.Text, best.Face, size, rs.Crop, rs.Cover, rs.Ink, rs.Cover3, Math.Max(0, sub), filter, sat, gamma, align);
                    sum += score * rs.Text.Length;
                    weightSum += rs.Text.Length;
                    if (s == 0) firstBase = baseY;
                }
                return (sum / weightSum, firstBase);
            }
            // every size compared as it comes, then the closest few placed exactly (which is slower)
            var sizes = new List<(double Size, double Score)>();
            for (double k = 0.9; k <= 1.031; k += 0.01) sizes.Add((best.Size * k, At(best.Size * k, false).Score));
            double fineScore = double.MaxValue, fineSize = best.Size, fineBaseline = best.Baseline;
            foreach (var (size, _) in sizes.OrderBy(x => x.Score).Take(3))
            {
                var (score, baseY) = At(size, true);
                if (score < fineScore) { fineScore = score; fineSize = size; fineBaseline = baseY; }
            }
            return best with { Size = fineSize, Baseline = fineBaseline, Score = fineScore };
        }

        // the drawing settings that make the typed letters look like the picture's: how the edges are
        // drawn (grey, or sub-pixel either way round), snapped to whole pixels or not, which filter,
        // how strong its colours, lighter or darker, and a nudge in height
        var placed = new Dictionary<(Typeface, double, int, double), Point>();
        (List<float[]> Raws, double Baseline) Draw(TextStyle st, int edges, bool snap, double yScale)
        {
            var raws = new List<float[]>();
            double first = st.Baseline;
            for (int q = 0; q < real.Count; q++)
            {
                var rr = real[q];
                var ft0 = Ft(rr.Text, st.Face, st.Size);
                if (!placed.TryGetValue((st.Face, st.Size, q, yScale), out var origin))
                    placed[(st.Face, st.Size, q, yScale)] = origin = AlignOrigin(rr.Text, st.Face, st.Size, rr.Crop, rr.Cover, rr.Ink, yScale);
                double baseAbs = rr.Crop.Y + origin.Y + ft0.Baseline;
                if (snap) { origin = new Point(origin.X, origin.Y + Math.Round(baseAbs) - baseAbs); baseAbs = Math.Round(baseAbs); }
                if (q == 0) first = baseAbs;
                raws.Add(RawCoverage(rr.Text, st.Face, st.Size, edges, origin, rr.Crop.Width, rr.Crop.Height, yScale));
            }
            return (raws, first);
        }

        double LookScore(List<float[]> raws, int edges, int filter, double gamma, double sat)
        {
            double diff = 0, sum = 0;
            for (int q = 0; q < real.Count; q++)
            {
                var a = real[q].Cover3;
                var b2 = Shape(raws[q], real[q].Crop.Width, real[q].Crop.Height, edges, filter, gamma, sat);
                for (int k = 0; k < a.Length; k++)
                {
                    diff += Math.Abs(a[k] - b2[k]);
                    sum += a[k] + b2[k];
                }
            }
            return sum <= 0 ? 1 : diff / sum;
        }

        TextStyle Look(TextStyle best)
        {
            double ys = real.Average(rr =>
            {
                var g = InkOf(Ft(rr.Text, best.Face, best.Size), new Point());
                return g.Height > 0 ? rr.Ink.Height / g.Height : 1;
            });
            ys = Math.Clamp(ys, 0.92, 1.08);
            var yScales = Math.Abs(ys - 1) < 0.01 ? new[] { 1.0 } : new[] { 1.0, ys };
            var modes = new List<int> { 0 };
            if (sub != 2) modes.Add(1);
            if (sub != 1) modes.Add(2);
            double bestLook = double.MaxValue;
            TextStyle chosen = best;
            foreach (int edges in modes)
                foreach (bool snap in new[] { false, true })
                    foreach (double yScale in yScales)
                    {
                        var (raws, baseline) = Draw(best, edges, snap, yScale);
                        foreach (int filter in edges == 0 ? new[] { 0 } : new[] { 0, 1, 2 })
                            foreach (double sat in edges == 0 ? new[] { 1.0 } : new[] { 1.0, 1.3, 1.6, 2.0 })
                                foreach (double gamma in new[] { 0.7, 0.85, 1.0, 1.2, 1.45 })
                                {
                                    double score = LookScore(raws, edges, filter, gamma, sat);
                                    if (score < bestLook - 1e-9)
                                    {
                                        bestLook = score;
                                        chosen = best with { Edges = edges, Gamma = gamma, Filter = filter, Saturation = sat, YScale = yScale, Baseline = baseline, LookScore = score };
                                    }
                                }
                    }
            return chosen;
        }

        var tried = Likely(allFaces).ToList();
        var first = Rank(tried, 0, 1, 1);
        LastRankForTest = $"tried {tried.Count}: {string.Join(", ", tried.Where(f => !IsCore(f.Face)).Select(f => f.Name))} | top: {string.Join(", ", first.Take(5).Select(t => $"{t.Name} {t.Score:0.000}"))}";
        if (first.Count == 0) return null;
        // the closest few each sized finely before one is picked: a font's size worked out from the
        // width alone can be a little out, which made the right font look worse than it is
        var sized = first.Take(3).Select(t => Fine(t, 0, 1, 1)).OrderBy(Biased).ToList();
        var result = Look(sized[0]);
        // darker or more colourful edges make a font look bolder than it is, and a bolder font with
        // lighter edges can look much the same; so the closest few fonts are each judged with their
        // own best edges (same edge type, snapping and stretch), and the best pair wins
        TextStyle Judge(TextStyle cand)
        {
            bool snapped = Math.Abs(result.Baseline - Math.Round(result.Baseline)) < 1e-6;
            var (raws, baseline) = Draw(cand, result.Edges, snapped, result.YScale);
            var judged = cand with { LookScore = double.MaxValue };
            foreach (int filter in result.Edges == 0 ? new[] { 0 } : new[] { 0, 1, 2 })
                foreach (double sat in result.Edges == 0 ? new[] { 1.0 } : new[] { 1.0, 1.3, 1.6, 2.0 })
                    foreach (double gamma in new[] { 0.6, 0.7, 0.85, 1.0, 1.2, 1.45 })
                    {
                        double score = LookScore(raws, result.Edges, filter, gamma, sat);
                        if (score < judged.LookScore)
                            judged = cand with { Edges = result.Edges, YScale = result.YScale, Baseline = baseline, Filter = filter, Saturation = sat, Gamma = gamma, LookScore = score };
                    }
            return judged;
        }
        if (result.Edges > 0)
        foreach (var cand in first.Take(5).Where(t => t.Name != sized[0].Name))
        {
            var judged = Judge(Fine(cand, result.Filter, result.Saturation, result.Gamma));
            // a different font has to be clearly closer, not just a hair
            if (judged.LookScore < result.LookScore * 0.97) result = judged;
        }
        return result;
        }
        finally { DrawIn(Colors.Black, Colors.White); }
    }

    /// <summary>
    /// Where to draw a word so its ink sits exactly on the picture's: first by the ink's box (to the
    /// pixel), then nudged by the difference in the ink's centre of mass (to a fraction of a pixel).
    /// </summary>
    private static Point AlignOrigin(string text, Typeface face, double size, PixelRect crop, float[] cover, Rect inkBox, double yScale = 1)
    {
        var g = InkOf(Ft(text, face, size), new Point());
        var o = new Point(inkBox.X + inkBox.Width / 2 - (g.X + g.Width / 2), inkBox.Y + inkBox.Height / 2 - (g.Y + g.Height / 2));
        var drawn = RawCoverage(text, face, size, 0, o, crop.Width, crop.Height, yScale);
        (double X, double Y, double M) Centre(float[] a)
        {
            double sx = 0, sy = 0, m = 0;
            for (int y = 0; y < crop.Height; y++)
                for (int x = 0; x < crop.Width; x++)
                {
                    float v = a[y * crop.Width + x];
                    sx += v * x; sy += v * y; m += v;
                }
            return m > 0 ? (sx / m, sy / m, m) : (0, 0, 0);
        }
        var real = Centre(cover);
        var mine = Centre(drawn);
        if (real.M <= 0 || mine.M <= 0) return o;
        var start = new Point(o.X + Math.Clamp(real.X - mine.X, -1.5, 1.5), o.Y + Math.Clamp(real.Y - mine.Y, -1.5, 1.5));

        // text isn't drawn exactly where it's asked to be (positions are rounded, and not evenly), so
        // the nudge is checked: a few places either side of it, and the closest kept
        double Off(Point at)
        {
            var a = RawCoverage(text, face, size, 0, at, crop.Width, crop.Height, yScale);
            double diff = 0, sum = 0;
            for (int k = 0; k < a.Length; k++) { diff += Math.Abs(cover[k] - a[k]); sum += cover[k] + a[k]; }
            return sum <= 0 ? 1 : diff / sum;
        }
        var best = o;
        double least = Off(o);
        for (int k = -3; k <= 3; k++)
        {
            var at = new Point(start.X + k * 0.25, start.Y);
            double v = Off(at);
            if (v < least - 1e-9) { least = v; best = at; }
        }
        foreach (double dy in new[] { -0.5, 0.5 })
        {
            var at = new Point(best.X, best.Y + dy);
            double v = Off(at);
            if (v < least - 1e-9) { least = v; best = at; }
        }
        return best;
    }

    /// <summary>How different the word drawn in this font is from the picture (0 the same, 1 nothing in common), and the baseline it implies.</summary>
    private static (double Score, double Baseline) Compare(string text, Typeface face, double size, PixelRect crop, float[] cover, Rect inkBox,
        float[]? cover3 = null, int edges = 0, int filter = 0, double sat = 1, double gamma = 1, bool align = false)
    {
        var ft = Ft(text, face, size);
        var g = InkOf(ft, new Point());
        // centre the drawn ink on the picture's ink (exactly, by the ink's centre of mass, when it matters)
        var origin = align ? AlignOrigin(text, face, size, crop, cover, inkBox)
            : new Point(inkBox.X + inkBox.Width / 2 - (g.X + g.Width / 2), inkBox.Y + inkBox.Height / 2 - (g.Y + g.Height / 2));
        double diff = 0, sum = 0;
        if (edges > 0 && cover3 != null)
        {
            var c3 = Coverage3(text, face, size, edges, gamma, origin, crop.Width, crop.Height, filter, sat);
            for (int k = 0; k < c3.Length; k++)
            {
                diff += Math.Abs(cover3[k] - c3[k]);
                sum += cover3[k] + c3[k];
            }
            return (sum <= 0 ? 1 : diff / sum, crop.Y + origin.Y + ft.Baseline);
        }
        var alpha = RawCoverage(text, face, size, 0, origin, crop.Width, crop.Height);
        if (gamma != 1)
            for (int k = 0; k < alpha.Length; k++) alpha[k] = (float)Math.Pow(alpha[k], gamma);
        for (int k = 0; k < cover.Length; k++)
        {
            diff += Math.Abs(cover[k] - alpha[k]);
            sum += cover[k] + alpha[k];
        }
        return (sum <= 0 ? 1 : diff / sum, crop.Y + origin.Y + ft.Baseline);
    }

    // ---- typing --------------------------------------------------------------------------------

    private readonly Queue<char> _typed = new();
    private bool _typingNow;
    private bool _warnedBusy;

    /// <summary>Letters typed while the text cursor (or a selection) is in the picture.</summary>
    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (_typing != null || _tool != Tool.Select || !(HasTextCaret || HasTextSelection)) return;
        if (FocusManager?.GetFocusedElement() is TextBox) return;
        if (string.IsNullOrEmpty(e.Text) || e.Text.Any(char.IsControl)) return;
        e.Handled = true;
        foreach (char c in e.Text) _typed.Enqueue(c);
        _ = PumpTypingAsync();
    }

    /// <summary>Types the waiting letters one after another, so fast typing isn't lost.</summary>
    private async Task PumpTypingAsync()
    {
        if (_typingNow) return;
        _typingNow = true;
        try
        {
            while (_typed.Count > 0) await TypeAsync(_typed.Dequeue());
        }
        finally { _typingNow = false; }
    }

    /// <summary>Types one letter at the cursor (replacing the selection, if there is one).</summary>
    private async Task TypeAsync(char c)
    {
        if (HasTextSelection) await DeleteTextAsync(back: false);
        if (!HasTextCaret) return;
        var (wi, k) = _focus;
        int line = _wordLine[wi];
        var style = StyleFor(line);
        if (style == null)
        {
            _typed.Clear();
            if (!_warnedBusy) _status.Text = "Typing needs text on a plain background. This line is on a picture or a gradient.";
            _warnedBusy = true;
            return;
        }

        var ft = Ft(c.ToString(), style.Face, style.Size);
        int shift = Math.Max(1, (int)Math.Round(ft.WidthIncludingTrailingWhitespace));
        double caretX = Edges(wi)[k];
        // just after a space typed at the end of a word: the cursor is out in the space, not on the word
        bool afterSpace = SpaceCaret is { } spaceX;
        if (afterSpace) caretX = SpaceCaret!.Value;

        // where the new letter's own space starts: half a letter gap out from the ink at either end of a word
        var nft = Ft("n", style.Face, style.Size);
        double halfGap = Math.Max(0, (nft.WidthIncludingTrailingWhitespace - InkOf(nft, new Point()).Width) / 2);
        int len = _words[wi].Text.Length;
        double pen = afterSpace ? caretX + halfGap : k == 0 && len > 0 ? caretX - halfGap : k == len ? caretX + halfGap : caretX;

        var (lo, hi, y0, y1, right, _) = LineBand(line);
        var src = BasePixels();
        int w = _pxW, h = _pxH;
        var px = (byte[])src.Clone();
        int ix = Math.Clamp((int)Math.Round(caretX), 0, w);
        if (!afterSpace && k > 0 && k < len)
            ix = Seam(src, w, y0, y1, caretX, 1, (style.Paper.B, style.Paper.G, style.Paper.R));
        int lineRight = Math.Clamp(right, ix, w);
        // the rest of the line moves right to make room, and the room is cleared to the background
        byte pb = style.Paper.B, pg = style.Paper.G, pr = style.Paper.R;
        for (int y = y0; y < y1; y++)
        {
            int row = y * w * 4;
            int count = Math.Min(lineRight, w - shift) - ix;
            if (count > 0) Array.Copy(src, row + ix * 4, px, row + (ix + shift) * 4, count * 4);
            for (int x = ix; x < Math.Min(w, ix + shift); x++)
            {
                int o = row + x * 4;
                px[o] = pb; px[o + 1] = pg; px[o + 2] = pr; px[o + 3] = 255;
            }
        }

        // the letter, drawn in the line's font and blended in with its ink colour
        if (!char.IsWhiteSpace(c))
        {
            int margin = (int)Math.Ceiling(style.Size * 0.5) + 2;
            int gx = (int)Math.Floor(pen) - margin, gy = (int)Math.Floor(style.Baseline - ft.Baseline) - margin;
            int gw = shift + 2 * margin, gh = (int)Math.Ceiling(ft.Height) + 2 * margin;
            DrawIn(style.Ink, style.Paper);
            float[] cover;
            try
            {
                cover = Coverage3(c.ToString(), style.Face, style.Size, style.Edges, style.Gamma,
                    new Point(pen - gx, style.Baseline - ft.Baseline - gy), gw, gh, style.Filter, style.Saturation, style.YScale);
            }
            finally { DrawIn(Colors.Black, Colors.White); }
            for (int y = 0; y < gh; y++)
            {
                int yy = gy + y;
                if (yy < 0 || yy >= h) continue;
                for (int x = 0; x < gw; x++)
                {
                    int xx = gx + x;
                    int k3 = (y * gw + x) * 3;
                    float ar = cover[k3], ag = cover[k3 + 1], ab = cover[k3 + 2];
                    if ((ar <= 0 && ag <= 0 && ab <= 0) || xx < 0 || xx >= w) continue;
                    int o = (yy * w + xx) * 4;
                    // each colour blended on its own: that's what gives sub-pixel text its coloured edges
                    px[o] = (byte)Math.Round(px[o] * (1 - ab) + style.Ink.B * ab);
                    px[o + 1] = (byte)Math.Round(px[o + 1] * (1 - ag) + style.Ink.G * ag);
                    px[o + 2] = (byte)Math.Round(px[o + 2] * (1 - ar) + style.Ink.R * ar);
                }
            }
        }
        var result = Imaging.FromPixels(px, new PixelSize(w, h));

        // the line's letters, with the new one in and the rest moved along
        var cells = new List<(char Ch, double L, double R, double T, double B)>();
        bool placed = char.IsWhiteSpace(c);
        double top = _words[wi].Bounds.Top, bottom = _words[wi].Bounds.Bottom;
        for (int i = lo; i <= hi; i++)
        {
            var e = Edges(i);
            var box = _words[i].Bounds;
            for (int j = 0; j < _words[i].Text.Length; j++)
            {
                if (!placed && (i, j) == (wi, k))
                {
                    cells.Add((c, caretX, caretX + shift, top, bottom));
                    placed = true;
                }
                double l = e[j], r = e[j + 1];
                if (l >= caretX - 0.01) { l += shift; r += shift; }
                cells.Add((_words[i].Text[j], l, r, box.Top, box.Bottom));
            }
            if (!placed && i == wi && k == _words[i].Text.Length)
            {
                cells.Add((c, caretX, caretX + shift, top, bottom));
                placed = true;
            }
        }
        CommitLine(lo, hi, line, cells, result, px, caretX + shift);
        _spaceAfter = char.IsWhiteSpace(c) && (afterSpace || k == len) ? (Base, _focus, caretX + shift) : null;
        if (_spaceAfter != null) ShowTextSelection();
        await Task.Yield();
    }

    /// <summary>A space typed at the end of a word leaves the cursor in that space (the picture, the word end it hangs off, and its x).</summary>
    private (Bitmap Base, (int W, int K) Focus, double X)? _spaceAfter;

    /// <summary>Where the cursor is when it's out in a just-typed space; null otherwise.</summary>
    private double? SpaceCaret =>
        _spaceAfter is { } sp && ReferenceEquals(sp.Base, Base) && sp.Focus == _focus && !HasTextSelection ? sp.X : null;

    // ---- the fine-tuning bar ---------------------------------------------------------------------
    //
    // While the text cursor is in the picture: the font, size and weight new letters are typed in.
    // They start as worked out from the picture; this is for when that's a little off (a touch too
    // bold, say). Each line keeps its own.

    private StackPanel? _typeBar;
    private Button? _typeFont, _typeWeight;
    private TextBlock? _typeSize;

    private int TuneLine => (HasTextCaret || HasTextSelection) && _focus.W < _wordLine.Count ? _wordLine[_focus.W] : -1;

    /// <summary>The bar itself, made once and shown at the bottom of the window while it applies.</summary>
    private Control TypeBar()
    {
        _typeFont = new Button { Content = "Matched to the picture", MinWidth = 150 };
        _typeFont.Click += (_, _) => FontMenu().Open(_typeFont);
        ToolTip.SetTip(_typeFont, "The font new letters are typed in");
        _typeWeight = new Button { Content = "auto" };
        _typeWeight.Click += (_, _) => WeightMenu().Open(_typeWeight);
        ToolTip.SetTip(_typeWeight, "How bold new letters are");
        _typeSize = new TextBlock { Text = "auto", VerticalAlignment = VerticalAlignment.Center, MinWidth = 52, TextAlignment = TextAlignment.Center };
        var smaller = new Button { Content = "−" };
        smaller.Click += (_, _) => Retune(st => st with { Size = Math.Max(4, st.Size - SizeStep(st.Size)) });
        ToolTip.SetTip(smaller, "Smaller");
        var bigger = new Button { Content = "+" };
        bigger.Click += (_, _) => Retune(st => st with { Size = Math.Min(400, st.Size + SizeStep(st.Size)) });
        ToolTip.SetTip(bigger, "Bigger");
        var reset = new Button { Content = "Reset" };
        reset.Click += (_, _) => ResetTune();
        ToolTip.SetTip(reset, "Back to what was matched from the picture");
        _typeBar = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6, IsVisible = false, VerticalAlignment = VerticalAlignment.Center,
            Children = { Ui.Label("Typing in", "dim"), _typeFont, _typeWeight, smaller, _typeSize, bigger, reset },
        };
        return _typeBar;
    }

    private void UpdateTypeBar()
    {
        if (_typeBar == null) return;
        int line = TuneLine;
        _typeBar.IsVisible = line >= 0 && _tool == Tool.Select;
        if (line < 0) return;
        if (_lineStyles.TryGetValue(line, out var s) && s != null)
        {
            _typeFont!.Content = NameOf(s.Face.FontFamily);
            _typeSize!.Text = $"{s.Size:0.#} px";
            _typeWeight!.Content = WeightName(s.Face.Weight);
        }
        else
        {
            _typeFont!.Content = "Matched to the picture";
            _typeSize!.Text = "auto";
            _typeWeight!.Content = "auto";
        }
    }

    private static string WeightName(FontWeight w) =>
        (int)w switch { <= 350 => "Light", <= 450 => "Regular", <= 550 => "Medium", <= 650 => "Semibold", _ => "Bold" };

    private static readonly (string Name, FontWeight Weight)[] Weights =
        { ("Light", FontWeight.Light), ("Regular", FontWeight.Normal), ("Medium", FontWeight.Medium), ("Semibold", FontWeight.SemiBold), ("Bold", FontWeight.Bold) };

    /// <summary>Half a pixel for small text, more for big.</summary>
    private static double SizeStep(double size) => size < 20 ? 0.5 : size < 60 ? 1 : 2;

    private static Typeface Face(FontFamily family, FontWeight weight) => new(family, FontStyle.Normal, weight);

    /// <summary>Changes the current line's style (working it out from the picture first, if need be).</summary>
    private void Retune(Func<TextStyle, TextStyle> change)
    {
        int line = TuneLine;
        if (line < 0) return;
        var s = StyleFor(line);
        if (s == null)
        {
            _status.Text = "Typing needs text on a plain background. This line is on a picture or a gradient.";
            return;
        }
        if (!_matchedStyles.ContainsKey(line)) _matchedStyles[line] = s;
        var changed = change(s);
        _lineStyles[line] = changed with { Name = NameOf(changed.Face.FontFamily) + (changed.Face.Weight == FontWeight.Normal ? "" : " " + WeightName(changed.Face.Weight)) };
        UpdateTypeBar();
        ShowTextHint();
        _canvas.Focus(); // typing carries on into the picture
        _ = RestyleAsync();
    }

    private void ResetTune()
    {
        int line = TuneLine;
        if (line < 0 || !_matchedStyles.Remove(line, out var matched)) return;
        _lineStyles[line] = matched;
        UpdateTypeBar();
        ShowTextHint();
        _canvas.Focus();
        _ = RestyleAsync();
    }

    private ContextMenu FontMenu()
    {
        var menu = new ContextMenu();
        var installed = Installed();
        string? current = _lineStyles.TryGetValue(TuneLine, out var s) && s != null ? NameOf(s.Face.FontFamily) : null;
        var choices = CommonFonts.Where(installed.Contains).Select(n => (Name: n, Family: new FontFamily(n)))
            .Concat(MatchingFonts.Where(f => !installed.Contains(f.Name)).Select(f => (f.Name, Family: Bundled(f.Name))))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, family) in choices)
        {
            var item = new MenuItem { Header = (name == current ? "✓ " : "") + name, FontFamily = family };
            item.Click += (_, _) => Retune(st => st with { Face = Face(family, st.Face.Weight) });
            menu.Items.Add(item);
        }
        return menu;
    }

    private ContextMenu WeightMenu()
    {
        var menu = new ContextMenu();
        string? current = _lineStyles.TryGetValue(TuneLine, out var s) && s != null ? WeightName(s.Face.Weight) : null;
        foreach (var (name, weight) in Weights)
        {
            var item = new MenuItem { Header = (name == current ? "✓ " : "") + name, FontWeight = weight };
            item.Click += (_, _) => Retune(st => st with { Face = Face(st.Face.FontFamily, weight) });
            menu.Items.Add(item);
        }
        return menu;
    }

    /// <summary>
    /// Letters already there take the new style too: the selected ones, or else the word the
    /// cursor is in. They're taken out and typed again in the new style, then selected again.
    /// </summary>
    private async Task RestyleAsync()
    {
        if (_typingNow || _textEditBusy) return;
        if (!HasTextSelection)
        {
            if (!HasTextCaret || SpaceCaret != null) return;
            int w = _focus.W;
            if (_words[w].Text.Length == 0) return;
            _anchor = (w, 0);
            _focus = (w, _words[w].Text.Length);
        }
        var parts = SelectedParts();
        if (parts.Count == 0) return;
        if (parts.Select(p => _wordLine[p.Word]).Distinct().Count() > 1)
        {
            _status.Text = "Restyling works on one line at a time. The new style is used for what you type next.";
            return;
        }
        string text = SelectedText();
        if (string.IsNullOrWhiteSpace(text)) return;
        // a whole word after another: taking it out closes the gap, so the space is typed back too
        var first = Compare(_anchor, _focus) <= 0 ? _anchor : _focus;
        bool wordStart = first.K == 0 && first.W > 0 && _wordLine[first.W - 1] == _wordLine[first.W];
        _typingNow = true;
        try
        {
            await DeleteTextAsync(back: false);
            if (!HasTextCaret) return;
            if (wordStart && _focus.K == _words[_focus.W].Text.Length && SpaceCaret == null) text = " " + text;
            int line = _wordLine[_focus.W];
            double startX = Edges(_focus.W)[_focus.K];
            foreach (char c in text) await TypeAsync(c);
            // select what was retyped (found by where it starts), so another change applies to it too
            if (HasTextCaret)
            {
                (int W, int K) best = _focus;
                double gap = double.MaxValue;
                for (int i = 0; i < _words.Count; i++)
                {
                    if (_wordLine[i] != line) continue;
                    var e = Edges(i);
                    for (int k = 0; k < e.Length; k++)
                        if (Math.Abs(e[k] - startX) < gap - 0.01 && Compare((i, k), _focus) < 0) { gap = Math.Abs(e[k] - startX); best = (i, k); }
                }
                // from the end of a word, the selection starts at the next one
                if (best.K == _words[best.W].Text.Length && best.W + 1 < _words.Count && _wordLine[best.W + 1] == line) best = (best.W + 1, 0);
                _anchor = best;
                ShowTextSelection();
            }
        }
        finally { _typingNow = false; }
        ShowTextHint();
    }

    // ---- for the self-test ------------------------------------------------------------------

    /// <summary>The style a line matched, and how long matching took.</summary>
    internal (TextStyle? Style, double Ms) MatchLineForTest(int word)
    {
        _lineStyles.Clear();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var style = StyleFor(_wordLine[word]);
        return (style, sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>Click at <paramref name="p"/> and type <paramref name="text"/>.</summary>
    internal async Task ClickAndTypeForTest(Point p, string text)
    {
        if (!TryBeginTextSelect(p)) return;
        EndTextSelect();
        foreach (char c in text) await TypeAsync(c);
    }

    internal async Task RestyleDoneForTest()
    {
        for (int i = 0; i < 400 && (_typingNow || _textEditBusy); i++) await Task.Delay(20);
    }

    internal string TextForTest => string.Join(" ", _words.Select(w => w.Text));
    internal TextStyle? TypingStyleForTest(int word) => _lineStyles.TryGetValue(_wordLine[word], out var s) ? s : null;
    internal bool TypeBarShownForTest => _typeBar?.IsVisible == true;

    /// <summary>The bar's buttons.</summary>
    internal void TuneForTest(string what)
    {
        switch (what)
        {
            case "bigger": Retune(st => st with { Size = Math.Min(400, st.Size + SizeStep(st.Size)) }); break;
            case "regular": Retune(st => st with { Face = Face(st.Face.FontFamily, FontWeight.Normal) }); break;
            case "roboto": Retune(st => st with { Face = Face(Bundled("Roboto"), st.Face.Weight) }); break;
            case "reset": ResetTune(); break;
        }
    }
}
