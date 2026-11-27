using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Overskin;

/// <summary>
/// Typing into a screenshot's text, like a document. The first time you type on a line, its font,
/// size, weight and colour are worked out by drawing the line's own words in each common font and
/// keeping the one that matches the picture best. New letters are drawn in that style and the
/// rest of the line moves along to make room. Works on text on a plain background.
/// </summary>
public partial class EditorWindow
{
    /// <summary>How a line of text looks: the font it matched, its size in pixels, ink and background colours, and where its baseline is.</summary>
    /// <param name="Edges">0 grey soft edges; 1 ClearType with red on the left of each pixel (RGB); 2 the other way round (BGR).</param>
    /// <param name="Display">Windows' pixel-snapped ("display") text, as most apps draw it, rather than WPF's smooth text.</param>
    /// <param name="Gamma">How dark the soft edges are: under 1 darker, over 1 lighter.</param>
    internal sealed record TextStyle(Typeface Face, string Name, double Size, Color Ink, Color Paper, double Baseline, double Score,
        int Edges = 0, bool Display = false, double Gamma = 1, int Filter = 0, double Saturation = 1, double YScale = 1, double LookScore = 1);

    private readonly Dictionary<int, TextStyle?> _lineStyles = new();

    /// <summary>How many of <see cref="CommonFonts"/> (from the start) are always tried.</summary>
    private const int CoreFonts = 12;

    private static readonly string[] CommonFonts =
    {
        "Segoe UI", "Segoe UI Variable Text", "Arial", "Calibri", "Cambria", "Georgia", "Times New Roman",
        "Verdana", "Tahoma", "Trebuchet MS", "Consolas", "Courier New",
        // used by apps and websites, when this PC has them
        "Helvetica", "Roboto", "Inter", "Open Sans", "Noto Sans", "Lato", "Source Sans Pro", "Ubuntu", "Montserrat", "Poppins",
        "Lucida Sans Unicode", "Bahnschrift", "Century Gothic", "Franklin Gothic Medium", "Candara", "Corbel",
        "Comic Sans MS", "Cascadia Code", "Cascadia Mono", "Lucida Console", "JetBrains Mono", "Fira Code",
    };

    private static List<(Typeface Face, string Name)>? _faces;

    /// <summary>The fonts to try, each in regular and bold (and Segoe UI semibold), that this PC has.</summary>
    private static List<(Typeface Face, string Name)> Faces()
    {
        if (_faces != null) return _faces;
        var installed = new HashSet<string>(Fonts.SystemFontFamilies.Select(f => f.Source), StringComparer.OrdinalIgnoreCase);
        var list = new List<(Typeface, string)>();
        foreach (var name in CommonFonts.Where(installed.Contains))
        {
            var family = new FontFamily(name);
            // the weights this font really has (not ones Windows would fake), from light to bold
            var real = family.FamilyTypefaces.Where(t => t.Style == FontStyles.Normal && t.Stretch == FontStretches.Normal)
                .Select(t => t.Weight.ToOpenTypeWeight()).ToHashSet();
            list.Add((new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), name));
            if (real.Contains(600) || name.StartsWith("Segoe UI"))
                list.Add((new Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), name + " Semibold"));
            if (real.Contains(500))
                list.Add((new Typeface(family, FontStyles.Normal, FontWeights.Medium, FontStretches.Normal), name + " Medium"));
            if (real.Contains(300) && !name.StartsWith("Segoe UI Variable"))
                list.Add((new Typeface(family, FontStyles.Normal, FontWeights.Light, FontStretches.Normal), name + " Light"));
            list.Add((new Typeface(family, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), name + " Bold"));
        }
        // the ones that come with Overskin, when this PC doesn't have its own copy
        foreach (var (name, weights) in FontLibrary.MatchingFonts)
        {
            if (installed.Contains(name)) continue;
            var family = FontLibrary.Bundled(name);
            foreach (var weight in weights)
                list.Add((new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal),
                    weight == FontWeights.Normal ? name : $"{name} {WeightName(weight)}"));
        }
        return _faces = list;
    }

    private static FormattedText Ft(string text, Typeface face, double size, Brush? brush = null, bool display = false) =>
        new(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, brush ?? Brushes.Black,
            null, display ? TextFormattingMode.Display : TextFormattingMode.Ideal, 1.0);

    /// <summary>
    /// Text drawn in black on nothing, as how much each pixel's red, green and blue are covered (0 to 1).
    /// With ClearType-style edges it's drawn three times as wide, one column per sub-pixel, and
    /// softened across neighbouring sub-pixels the way ClearType does, which gives the coloured fringes.
    /// </summary>
    private static float[] Coverage3(string text, Typeface face, double size, bool display, int edges, double gamma, Point origin, int w, int h,
        int filter = 0, double sat = 1, double yScale = 1) =>
        Shape(RawCoverage(text, face, size, display, edges, origin, w, h, yScale), w, h, edges, filter, gamma, sat);

    /// <summary>The text drawn as coverage: one value per pixel, or per sub-pixel (three across) for ClearType.</summary>
    private static float[] RawCoverage(string text, Typeface face, double size, bool display, int edges, Point origin, int w, int h, double yScale = 1)
    {
        var ft = Ft(text, face, size, null, display && edges == 0);
        int sx = edges == 0 ? 1 : 3;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // the height stretch keeps the baseline where it is
            if (sx != 1 || yScale != 1) dc.PushTransform(new ScaleTransform(sx, yScale, 0, origin.Y + ft.Baseline));
            dc.DrawText(ft, origin);
        }
        int rw = w * sx;
        var rtb = new RenderTargetBitmap(rw, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var px = new byte[rw * h * 4];
        rtb.CopyPixels(px, rw * 4, 0);
        var raw = new float[rw * h];
        for (int k = 0; k < raw.Length; k++) raw[k] = px[k * 4 + 3] / 255f;
        return raw;
    }

    // ClearType's filters across neighbouring sub-pixels: the usual soft one, a lighter one and a sharp one
    private static readonly float[][] Filters =
    {
        new[] { 1 / 9f, 2 / 9f, 3 / 9f, 2 / 9f, 1 / 9f },
        new[] { 0f, 1 / 3f, 1 / 3f, 1 / 3f, 0f },
        new[] { 0f, 1 / 4f, 2 / 4f, 1 / 4f, 0f },
    };

    /// <summary>
    /// Coverage per pixel as red, green and blue: for ClearType, the sub-pixels filtered, then the
    /// colours pushed apart by <paramref name="sat"/> (Windows' fringes can be strongly coloured);
    /// then the edges made darker or lighter by <paramref name="gamma"/>.
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
        var real = new List<(string Text, Int32Rect Crop, float[] Cover, Rect Ink, float[] Cover3)>();
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
            real.Add((_words[i].Text, new Int32Rect(cx0, cy0, cw, ch), cover, new Rect(l, t, r - l, bt - t), cover3));
        }
        if (real.Count == 0) return null;

        // ClearType: grey text with coloured edges. Then fonts are compared with coloured edges too,
        // or ClearType's softer strokes would pass for a bolder font. Which way round is tried on the first font.
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
        List<TextStyle> Rank(IEnumerable<(Typeface Face, string Name)> faces, int filter, double sat, double gamma)
        {
            var ranked = new List<TextStyle>();
            foreach (var (face, name) in faces)
            {
                double total = 0, sizeSum = 0, weight = 0, baseline = 0;
                bool ok = true;
                for (int s = 0; s < real.Count; s++)
                {
                    var (text, crop, cover, inkBox, _) = real[s];
                    var g100 = Ft(text, face, 100).BuildGeometry(new Point()).Bounds;
                    if (g100.IsEmpty || g100.Width < 1) { ok = false; break; }
                    double size0 = 100 * inkBox.Width / g100.Width;
                    // the height has to fit too, or it's not this font
                    if (Math.Abs(g100.Height * size0 / 100 - inkBox.Height) > Math.Max(2, inkBox.Height * 0.25)) { ok = false; break; }
                    double bestScore = double.MaxValue, bestSize = size0, bestBase = 0;
                    foreach (double size in new[] { size0, Math.Round(size0), Math.Round(size0 * 2) / 2 }.Distinct())
                    {
                        if (sub == -1)
                        {
                            double rgb = Compare(text, face, size, crop, cover, inkBox, real[s].Cover3, 1).Score;
                            double bgr = Compare(text, face, size, crop, cover, inkBox, real[s].Cover3, 2).Score;
                            sub = rgb <= bgr ? 1 : 2;
                        }
                        var (score, baseY) = Compare(text, face, size, crop, cover, inkBox, real[s].Cover3, Math.Max(0, sub), filter, sat, gamma);
                        if (score < bestScore) { bestScore = score; bestSize = size; bestBase = baseY; }
                    }
                    total += bestScore * text.Length;
                    sizeSum += bestSize * text.Length;
                    weight += text.Length;
                    if (s == 0) baseline = bestBase;
                }
                if (!ok || weight == 0) continue;
                ranked.Add(new TextStyle(face, name, sizeSum / weight, ink, paper, baseline, total / weight));
            }
            // near-ties go to the more common font (earlier in the list): Segoe UI over its Windows 11 twin
            // (the lean only applies among the usual fonts: the others all count as coming last of
            // those, or a long list would push a font at its end out of reach)
            var order = Faces().Select(f => f.Name).ToList();
            int coreCount = Faces().Count(f => CommonFonts.Take(CoreFonts).Contains(f.Face.FontFamily.Source, StringComparer.OrdinalIgnoreCase));
            double Biased(TextStyle t) => t.Score * (1 + 0.006 * Math.Min(coreCount, Math.Max(0, order.IndexOf(t.Name))) / 3);
            ranked.Sort((u, v) => Biased(u).CompareTo(Biased(v)));
            return ranked;
        }

        // The quick look: each font's words drawn at the size that makes them as wide as the picture's,
        // and how far their height is out. Only the closest are compared pixel by pixel, so trying
        // more fonts doesn't make matching slower.
        IEnumerable<(Typeface Face, string Name)> Likely(List<(Typeface Face, string Name)> all)
        {
            // the usual fonts are always compared; of the others, only the few whose words come out
            // closest to the right shape
            var core = new HashSet<string>(CommonFonts.Take(CoreFonts), StringComparer.OrdinalIgnoreCase);
            // one quick pixel comparison each, on the longest word at the size that fits its width
            var (text0, crop0, cover0, ink0, cover30) = real[0];
            var extras = all.Where(f => !core.Contains(f.Face.FontFamily.Source)).Select(f =>
            {
                var g = Ft(text0, f.Face, 100).BuildGeometry(new Point()).Bounds;
                if (g.IsEmpty || g.Width < 1) return (f, Off: double.MaxValue);
                double size0 = 100 * ink0.Width / g.Width;
                if (Math.Abs(g.Height * size0 / 100 - ink0.Height) > Math.Max(2, ink0.Height * 0.25)) return (f, Off: double.MaxValue);
                return (f, Off: Compare(text0, f.Face, size0, crop0, cover0, ink0, cover30).Score);
            }).Where(x => x.Off < double.MaxValue).OrderBy(x => x.Off).Take(5).Select(x => x.f).ToHashSet();
            return all.Where(f => core.Contains(f.Face.FontFamily.Source) || extras.Contains(f)); // in the usual order, which settles near-ties
        }

        // the size, finely: the ink's width runs a little wide from soft edges, so try just under it too
        TextStyle Fine(TextStyle best, int filter, double sat, double gamma)
        {
            double fineScore = double.MaxValue, fineSize = best.Size, fineBaseline = best.Baseline;
            for (double k = 0.9; k <= 1.031; k += 0.01)
            {
                double size = best.Size * k, sum = 0, weightSum = 0, firstBase = 0;
                for (int s = 0; s < real.Count; s++)
                {
                    var (score, baseY) = Compare(real[s].Text, best.Face, size, real[s].Crop, real[s].Cover, real[s].Ink, real[s].Cover3, Math.Max(0, sub), filter, sat, gamma, align: true);
                    sum += score * real[s].Text.Length;
                    weightSum += real[s].Text.Length;
                    if (s == 0) firstBase = baseY;
                }
                if (sum / weightSum < fineScore) { fineScore = sum / weightSum; fineSize = size; fineBaseline = firstBase; }
            }
            return best with { Size = fineSize, Baseline = fineBaseline, Score = fineScore };
        }

        TextStyle Look(TextStyle best)
        {
            // how the edges are drawn: grey or ClearType (either way round), smooth or pixel-snapped,
            // which ClearType filter, how strong its colours, lighter or darker, and whether Windows
            // snapped the line to whole pixels or nudged its height. Each is tried on the line's own
            // words and the closest kept. Only the drawing is redone per shape; the rest is arithmetic.
            double ys = real.Average(rr =>
            {
                var g = Ft(rr.Text, best.Face, best.Size).BuildGeometry(new Point()).Bounds;
                return g.Height > 0 ? rr.Ink.Height / g.Height : 1;
            });
            ys = Math.Clamp(ys, 0.92, 1.08);
            var yScales = Math.Abs(ys - 1) < 0.01 ? new[] { 1.0 } : new[] { 1.0, ys };
            var modes = new List<(int Edges, bool Display)> { (0, false), (0, true) };
            if (sub != 2) modes.Add((1, false));
            if (sub != 1) modes.Add((2, false));
            double bestLook = double.MaxValue;
            TextStyle chosen = best;
            foreach (var (edges, display) in modes)
                foreach (bool snap in new[] { false, true })
                    foreach (double yScale in yScales)
                    {
                        var raws = new List<(float[] Raw, double Baseline)>();
                        foreach (var rr in real)
                        {
                            var ft0 = Ft(rr.Text, best.Face, best.Size, null, display && edges == 0);
                            var g = ft0.BuildGeometry(new Point()).Bounds;
                            var origin = AlignOrigin(rr.Text, best.Face, best.Size, display && edges == 0, rr.Crop, rr.Cover, rr.Ink, yScale);
                            double baseAbs = rr.Crop.Y + origin.Y + ft0.Baseline;
                            if (snap) { origin.Y += Math.Round(baseAbs) - baseAbs; baseAbs = Math.Round(baseAbs); }
                            raws.Add((RawCoverage(rr.Text, best.Face, best.Size, display, edges, origin, rr.Crop.Width, rr.Crop.Height, yScale), baseAbs));
                        }
                        foreach (int filter in edges == 0 ? new[] { 0 } : new[] { 0, 1, 2 })
                            foreach (double sat in edges == 0 ? new[] { 1.0 } : new[] { 1.0, 1.3, 1.6, 2.0 })
                                foreach (double gamma in new[] { 0.7, 0.85, 1.0, 1.2, 1.45 })
                                {
                                    double diff = 0, sum = 0;
                                    for (int q = 0; q < real.Count; q++)
                                    {
                                        var a = real[q].Cover3;
                                        var b2 = Shape(raws[q].Raw, real[q].Crop.Width, real[q].Crop.Height, edges, filter, gamma, sat);
                                        for (int k = 0; k < a.Length; k++)
                                        {
                                            diff += Math.Abs(a[k] - b2[k]);
                                            sum += a[k] + b2[k];
                                        }
                                    }
                                    double score = sum <= 0 ? 1 : diff / sum;
                                    if (score < bestLook - 1e-9)
                                    {
                                        bestLook = score;
                                        chosen = best with
                                        {
                                            Edges = edges, Display = display, Gamma = gamma, Filter = filter, Saturation = sat,
                                            YScale = yScale, Baseline = raws[0].Baseline, LookScore = score,
                                        };
                                    }
                                }
                    }
            return chosen;
        }

        var tried = Likely(Faces()).ToList();
        var first = Rank(tried, 0, 1, 1);
        LastRankForTest = $"tried {tried.Count}: {string.Join(", ", tried.Where(f => !CommonFonts.Take(CoreFonts).Contains(f.Face.FontFamily.Source)).Select(f => f.Name))} | top: {string.Join(", ", first.Take(5).Select(t => $"{t.Name} {t.Score:0.000}"))}";
        if (first.Count == 0) return null;
        var result = Look(Fine(first[0], 0, 1, 1));
        // darker or more colourful edges make a font look bolder than it is, and a bolder font with
        // lighter edges can look much the same; so the closest few fonts are each judged with
        // their own best edges (same edge type, snapping and stretch; filter, colour and darkness
        // free), and the best pair wins
        TextStyle Judge(TextStyle cand)
        {
            var raws = new List<float[]>();
            double baseline = cand.Baseline;
            for (int q = 0; q < real.Count; q++)
            {
                var rr = real[q];
                var ft0 = Ft(rr.Text, cand.Face, cand.Size, null, result.Display && result.Edges == 0);
                var g = ft0.BuildGeometry(new Point()).Bounds;
                var origin = AlignOrigin(rr.Text, cand.Face, cand.Size, result.Display && result.Edges == 0, rr.Crop, rr.Cover, rr.Ink, result.YScale);
                double baseAbs = rr.Crop.Y + origin.Y + ft0.Baseline;
                if (Math.Abs(result.Baseline - Math.Round(result.Baseline)) < 1e-6) { origin.Y += Math.Round(baseAbs) - baseAbs; baseAbs = Math.Round(baseAbs); }
                if (q == 0) baseline = baseAbs;
                raws.Add(RawCoverage(rr.Text, cand.Face, cand.Size, result.Display, result.Edges, origin, rr.Crop.Width, rr.Crop.Height, result.YScale));
            }
            var judged = cand with { LookScore = double.MaxValue };
            foreach (int filter in result.Edges == 0 ? new[] { 0 } : new[] { 0, 1, 2 })
                foreach (double sat in result.Edges == 0 ? new[] { 1.0 } : new[] { 1.0, 1.3, 1.6, 2.0 })
                    foreach (double gamma in new[] { 0.6, 0.7, 0.85, 1.0, 1.2, 1.45 })
                    {
                        double diff = 0, sum = 0;
                        for (int q = 0; q < real.Count; q++)
                        {
                            var a = real[q].Cover3;
                            var b2 = Shape(raws[q], real[q].Crop.Width, real[q].Crop.Height, result.Edges, filter, gamma, sat);
                            for (int k = 0; k < a.Length; k++)
                            {
                                diff += Math.Abs(a[k] - b2[k]);
                                sum += a[k] + b2[k];
                            }
                        }
                        double score = sum <= 0 ? 1 : diff / sum;
                        if (score < judged.LookScore)
                            judged = cand with
                            {
                                Edges = result.Edges, Display = result.Display, YScale = result.YScale, Baseline = baseline,
                                Filter = filter, Saturation = sat, Gamma = gamma, LookScore = score,
                            };
                    }
            return judged;
        }
        if (result.Edges > 0)
        {
            foreach (var cand in first.Take(5).Skip(1))
            {
                var judged = Judge(Fine(cand, result.Filter, result.Saturation, result.Gamma));
                // a different font has to be clearly closer, not just a hair
                if (judged.LookScore < result.LookScore * 0.97) result = judged;
            }
        }
        return result;
    }

    /// <summary>
    /// Where to draw a word so its ink sits exactly on the picture's: first by the ink's box (to the
    /// pixel), then nudged by the difference in the ink's centre of mass (to a fraction of a pixel).
    /// Half a pixel out is enough to make the wrong edge settings look right.
    /// </summary>
    private static Point AlignOrigin(string text, Typeface face, double size, bool display, Int32Rect crop, float[] cover, Rect inkBox, double yScale = 1)
    {
        var ft = Ft(text, face, size, null, display);
        var g = ft.BuildGeometry(new Point()).Bounds;
        var o = new Point(inkBox.X + inkBox.Width / 2 - (g.X + g.Width / 2), inkBox.Y + inkBox.Height / 2 - (g.Y + g.Height / 2));
        var drawn = RawCoverage(text, face, size, display, 0, o, crop.Width, crop.Height, yScale);
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
        return new Point(o.X + Math.Clamp(real.X - mine.X, -1.5, 1.5), o.Y + Math.Clamp(real.Y - mine.Y, -1.5, 1.5));
    }

    /// <summary>How different the word drawn in this font is from the picture (0 the same, 1 nothing in common), and the baseline it implies.</summary>
    private static (double Score, double Baseline) Compare(string text, Typeface face, double size, Int32Rect crop, float[] cover, Rect inkBox,
        float[]? cover3 = null, int edges = 0, int filter = 0, double sat = 1, double gamma = 1, bool align = false)
    {
        var ft = Ft(text, face, size);
        var g = ft.BuildGeometry(new Point()).Bounds;
        // centre the drawn ink on the picture's ink (exactly, by the ink's centre of mass, when it matters)
        var origin = align ? AlignOrigin(text, face, size, false, crop, cover, inkBox)
            : new Point(inkBox.X + inkBox.Width / 2 - (g.X + g.Width / 2), inkBox.Y + inkBox.Height / 2 - (g.Y + g.Height / 2));
        double diff = 0, sum = 0;
        if (edges > 0 && cover3 != null)
        {
            var c3 = Coverage3(text, face, size, false, edges, gamma, origin, crop.Width, crop.Height, filter, sat);
            for (int k = 0; k < c3.Length; k++)
            {
                diff += Math.Abs(cover3[k] - c3[k]);
                sum += cover3[k] + c3[k];
            }
            return (sum <= 0 ? 1 : diff / sum, crop.Y + origin.Y + ft.Baseline);
        }
        var alpha = Coverage(ft, origin, crop.Width, crop.Height);
        if (gamma != 1)
            for (int k = 0; k < alpha.Length; k++) alpha[k] = (float)Math.Pow(alpha[k], gamma);
        for (int k = 0; k < cover.Length; k++)
        {
            diff += Math.Abs(cover[k] - alpha[k]);
            sum += cover[k] + alpha[k];
        }
        return (sum <= 0 ? 1 : diff / sum, crop.Y + origin.Y + ft.Baseline);
    }

    /// <summary>Text drawn in black on nothing, as how much each pixel is covered (0 to 1).</summary>
    private static float[] Coverage(FormattedText ft, Point origin, int w, int h)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawText(ft, origin);
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var px = new byte[w * h * 4];
        rtb.CopyPixels(px, w * 4, 0);
        var a = new float[w * h];
        for (int k = 0; k < a.Length; k++) a[k] = px[k * 4 + 3] / 255f;
        return a;
    }

    // ---- typing --------------------------------------------------------------------------------

    private readonly Queue<char> _typed = new();
    private bool _typing;
    private bool _warnedBusy;

    protected override void OnPreviewTextInput(TextCompositionEventArgs e)
    {
        base.OnPreviewTextInput(e);
        if (_textBox != null || _selected != null || !(HasTextCaret || HasTextSelection)) return;
        if (string.IsNullOrEmpty(e.Text) || e.Text.Any(char.IsControl)) return;
        e.Handled = true;
        foreach (char c in e.Text) _typed.Enqueue(c);
        _ = PumpTypingAsync();
    }

    /// <summary>A key typed straight from the keyboard handler (Space, which buttons would otherwise take).</summary>
    private void TypeKey(char c)
    {
        _typed.Enqueue(c);
        _ = PumpTypingAsync();
    }

    /// <summary>Types the waiting letters one after another, so fast typing isn't lost.</summary>
    private async Task PumpTypingAsync()
    {
        if (_typing) return;
        _typing = true;
        try
        {
            while (_typed.Count > 0) await TypeAsync(_typed.Dequeue());
        }
        finally { _typing = false; }
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
            if (!_warnedBusy) Toast.Show("Typing needs text on a plain background. This line is on a picture or a gradient.", true);
            _warnedBusy = true;
            return;
        }

        var brush = new SolidColorBrush(style.Ink);
        var ft = Ft(c.ToString(), style.Face, style.Size, brush, style.Display && style.Edges == 0);
        int shift = Math.Max(1, (int)Math.Round(ft.WidthIncludingTrailingWhitespace));
        double caretX = Edges(wi)[k];
        // just after a space typed at the end of a word: the cursor is out in the space, not on the word
        bool afterSpace = SpaceCaret is { } spaceX;
        if (afterSpace) caretX = SpaceCaret!.Value;

        // where the new letter's own space starts: half a letter gap out from the ink at either end of a word
        var n = Ft("n", style.Face, style.Size);
        double halfGap = Math.Max(0, (n.WidthIncludingTrailingWhitespace - n.BuildGeometry(new Point()).Bounds.Width) / 2);
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
            var cover = Coverage3(c.ToString(), style.Face, style.Size, style.Display, style.Edges, style.Gamma,
                new Point(pen - gx, style.Baseline - ft.Baseline - gy), gw, gh, style.Filter, style.Saturation, style.YScale);
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
                    // each colour blended on its own: that's what gives ClearType its coloured edges
                    px[o] = (byte)Math.Round(px[o] * (1 - ab) + style.Ink.B * ab);
                    px[o + 1] = (byte)Math.Round(px[o + 1] * (1 - ag) + style.Ink.G * ag);
                    px[o + 2] = (byte)Math.Round(px[o + 2] * (1 - ar) + style.Ink.R * ar);
                }
            }
        }
        var result = BitmapSource.Create(w, h, _base.DpiX, _base.DpiY, PixelFormats.Bgra32, null, px, w * 4);
        result.Freeze();

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
        _spaceAfter = char.IsWhiteSpace(c) && (afterSpace || k == len) ? (_base, _focus, caretX + shift) : null;
        if (_spaceAfter != null) ShowTextSelection();
    }

    /// <summary>A space typed at the end of a word leaves the cursor in that space (the picture, the word end it hangs off, and its x).</summary>
    private (BitmapSource Base, (int W, int K) Focus, double X)? _spaceAfter;

    /// <summary>Where the cursor is when it's out in a just-typed space; null otherwise.</summary>
    private double? SpaceCaret =>
        _spaceAfter is { } sp && sp.Base == _base && sp.Focus == _focus && !HasTextSelection ? sp.X : null;

    /// <summary>For the self-test: the style a line matched, and how long matching took.</summary>
    internal (TextStyle? Style, double Ms) MatchLineForTest(int word)
    {
        _lineStyles.Clear();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var style = StyleFor(_wordLine[word]);
        return (style, sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>For the self-test: a line of text with ClearType-style coloured edges, on a plain background.</summary>
    internal static BitmapSource ClearTypeForTest(string text, string font, double size, Color ink, Color paper, int edges, int w = 360, int h = 60,
        int filter = 0, double sat = 1, double gamma = 1)
    {
        var face = new Typeface(font);
        var cover = Coverage3(text, face, size, false, edges, gamma, new Point(14, 14), w, h, filter, sat);
        var px = new byte[w * h * 4];
        for (int k = 0; k < w * h; k++)
        {
            float r = cover[k * 3], g = cover[k * 3 + 1], b = cover[k * 3 + 2];
            px[k * 4] = (byte)Math.Round(paper.B * (1 - b) + ink.B * b);
            px[k * 4 + 1] = (byte)Math.Round(paper.G * (1 - g) + ink.G * g);
            px[k * 4 + 2] = (byte)Math.Round(paper.R * (1 - r) + ink.R * r);
            px[k * 4 + 3] = 255;
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>For the self-test: click at <paramref name="p"/> and type <paramref name="text"/>.</summary>
    internal async Task ClickAndTypeForTest(Point p, string text)
    {
        if (!TryBeginTextSelect(p)) return;
        EndTextSelect();
        foreach (char c in text) await TypeAsync(c);
    }
}
