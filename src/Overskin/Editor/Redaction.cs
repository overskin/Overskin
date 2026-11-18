using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Overskin;

/// <summary>How a hidden area looks.</summary>
public enum HideMode { Blur, Pixelate, Mosaic }

/// <summary>
/// Hiding parts of a picture: blurred, pixelated or as mosaic tiles, and finding the private
/// details worth hiding (emails, phone numbers, card numbers, IP addresses, keys) with the
/// built-in text reader.
/// </summary>
public static class Redaction
{
    /// <summary>The area hidden, as a picture exactly the area's size.</summary>
    public static BitmapSource? Render(BitmapSource image, Rect area, HideMode mode, double strength)
    {
        int x = Math.Clamp((int)Math.Floor(area.X), 0, image.PixelWidth - 1);
        int y = Math.Clamp((int)Math.Floor(area.Y), 0, image.PixelHeight - 1);
        int w = Math.Clamp((int)Math.Ceiling(area.Right) - x, 1, image.PixelWidth - x);
        int h = Math.Clamp((int)Math.Ceiling(area.Bottom) - y, 1, image.PixelHeight - y);
        if (w < 2 || h < 2) return null;

        var src = ImageLoader.Normalize(image);
        int sw = src.PixelWidth, sh = src.PixelHeight;
        var all = new byte[sw * sh * 4];
        src.CopyPixels(all, sw * 4, 0);

        var px = new byte[w * h * 4];
        for (int r = 0; r < h; r++) Buffer.BlockCopy(all, ((y + r) * sw + x) * 4, px, r * w * 4, w * 4);

        int block = (int)Math.Max(3, Math.Round(2 + strength * 3)); // slider 1..12 -> 5..38 px
        switch (mode)
        {
            case HideMode.Pixelate: Blocks(px, w, h, block, grout: false); break;
            case HideMode.Mosaic: Blocks(px, w, h, Math.Max(6, block), grout: true); break;
            default:
                // blur from a little outside the area, so its edges fade into what's around
                int radius = Math.Max(2, block / 2 + 1);
                int ex = Math.Max(0, x - radius), ey = Math.Max(0, y - radius);
                int ew = Math.Min(sw, x + w + radius) - ex, eh = Math.Min(sh, y + h + radius) - ey;
                var big = new byte[ew * eh * 4];
                for (int r = 0; r < eh; r++) Buffer.BlockCopy(all, ((ey + r) * sw + ex) * 4, big, r * ew * 4, ew * 4);
                for (int pass = 0; pass < 3; pass++) BoxBlur(big, ew, eh, radius);
                for (int r = 0; r < h; r++) Buffer.BlockCopy(big, ((y - ey + r) * ew + (x - ex)) * 4, px, r * w * 4, w * 4);
                break;
        }
        for (int i = 3; i < px.Length; i += 4) px[i] = 255;
        var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        result.Freeze();
        return result;
    }

    /// <summary>Burns hidden areas straight into a copy of the picture.</summary>
    public static BitmapSource Apply(BitmapSource image, IEnumerable<Rect> areas, HideMode mode, double strength)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(image, new Rect(0, 0, image.PixelWidth, image.PixelHeight));
            foreach (var a in areas)
            {
                var r = Rect.Intersect(a, new Rect(0, 0, image.PixelWidth, image.PixelHeight));
                if (r.IsEmpty) continue;
                r = new Rect(Math.Floor(r.X), Math.Floor(r.Y), Math.Ceiling(r.Width), Math.Ceiling(r.Height));
                if (Render(image, r, mode, strength) is { } hidden) dc.DrawImage(hidden, r);
            }
        }
        var rtb = new RenderTargetBitmap(image.PixelWidth, image.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        return ImageLoader.Normalize(rtb);
    }

    private static void Blocks(byte[] px, int w, int h, int block, bool grout)
    {
        for (int by = 0; by < h; by += block)
        {
            for (int bx = 0; bx < w; bx += block)
            {
                int bw = Math.Min(block, w - bx), bh = Math.Min(block, h - by);
                long b = 0, g = 0, r = 0;
                for (int yy = by; yy < by + bh; yy++)
                    for (int xx = bx; xx < bx + bw; xx++)
                    {
                        int i = (yy * w + xx) * 4;
                        b += px[i]; g += px[i + 1]; r += px[i + 2];
                    }
                int n = bw * bh;
                byte cb = (byte)(b / n), cg = (byte)(g / n), cr = (byte)(r / n);
                // mosaic: each tile a touch lighter or darker, with a darker line between tiles
                if (grout)
                {
                    int jitter = ((bx / block * 73856093) ^ (by / block * 19349663)) % 15 - 7;
                    cb = (byte)Math.Clamp(cb + jitter, 0, 255); cg = (byte)Math.Clamp(cg + jitter, 0, 255); cr = (byte)Math.Clamp(cr + jitter, 0, 255);
                }
                for (int yy = by; yy < by + bh; yy++)
                    for (int xx = bx; xx < bx + bw; xx++)
                    {
                        int i = (yy * w + xx) * 4;
                        bool line = grout && (xx == bx || yy == by);
                        px[i] = line ? (byte)(cb * 0.62) : cb;
                        px[i + 1] = line ? (byte)(cg * 0.62) : cg;
                        px[i + 2] = line ? (byte)(cr * 0.62) : cr;
                    }
            }
        }
    }

    /// <summary>One horizontal and one vertical running-average pass.</summary>
    private static void BoxBlur(byte[] px, int w, int h, int radius)
    {
        var tmp = new byte[px.Length];
        for (int pass = 0; pass < 2; pass++)
        {
            bool horizontal = pass == 0;
            int lines = horizontal ? h : w, length = horizontal ? w : h;
            byte[] from = horizontal ? px : tmp, to = horizontal ? tmp : px;
            for (int l = 0; l < lines; l++)
            {
                int Index(int k) => horizontal ? (l * w + k) * 4 : (k * w + l) * 4;
                for (int c = 0; c < 3; c++)
                {
                    int sum = 0;
                    for (int k = -radius; k <= radius; k++) sum += from[Index(Math.Clamp(k, 0, length - 1)) + c];
                    for (int k = 0; k < length; k++)
                    {
                        to[Index(k) + c] = (byte)(sum / (2 * radius + 1));
                        sum += from[Index(Math.Min(length - 1, k + radius + 1)) + c] - from[Index(Math.Max(0, k - radius)) + c];
                    }
                }
            }
        }
    }

    // ---- finding private details ------------------------------------------------------------

    private static readonly (string Kind, Regex Pattern)[] Patterns =
    {
        ("email", new Regex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.Compiled)),
        ("card number", new Regex(@"\b(?:\d[ \-]?){13,19}\b", RegexOptions.Compiled)),
        ("phone number", new Regex(@"(?:\+\d{1,3}[ .\-]?)?(?:\(\d{2,4}\)[ .\-]?|\d{2,4}[ .\-])\d{3,4}[ .\-]?\d{3,4}\b", RegexOptions.Compiled)),
        ("IP address", new Regex(@"\b(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)\b", RegexOptions.Compiled)),
        ("IP address", new Regex(@"\b(?:[0-9A-Fa-f]{1,4}:){4,7}[0-9A-Fa-f]{1,4}\b", RegexOptions.Compiled)),
        ("key", new Regex(@"\b(?:sk|pk|ghp|gho|xox[bap]|AKIA|AIza)[A-Za-z0-9_\-]{12,}\b", RegexOptions.Compiled)),
        ("key", new Regex(@"\b(?=[A-Za-z0-9_\-]*\d)(?=[A-Za-z0-9_\-]*[A-Za-z])[A-Za-z0-9_\-]{32,}\b", RegexOptions.Compiled)),
    };

    /// <summary>
    /// Areas of the picture that hold private details, and what each one is. Empty when there
    /// are none; null when Windows has no text-reading language installed.
    /// </summary>
    public static async Task<List<(Rect Area, string Kind)>?> FindPrivateAsync(BitmapSource image)
    {
        var lines = await TextRecognizer.RecognizeLinesAsync(image);
        if (lines == null) return null;
        return FindPrivate(lines);
    }

    public static List<(Rect Area, string Kind)> FindPrivate(List<List<OcrWord>> lines)
    {
        var found = new List<(Rect, string)>();
        foreach (var words in lines)
        {
            // the line as text, remembering where each word starts
            var starts = new List<int>();
            var text = new System.Text.StringBuilder();
            foreach (var w in words)
            {
                if (text.Length > 0) text.Append(' ');
                starts.Add(text.Length);
                text.Append(w.Text);
            }
            string line = text.ToString();
            var taken = new bool[line.Length];

            foreach (var (kind, pattern) in Patterns)
            {
                foreach (Match m in pattern.Matches(line))
                {
                    if (kind == "card number" && !Luhn(m.Value)) continue;
                    if (kind == "phone number" && m.Value.Count(char.IsDigit) < 7) continue;
                    if (Enumerable.Range(m.Index, m.Length).Any(i => taken[i])) continue;
                    for (int i = m.Index; i < m.Index + m.Length; i++) taken[i] = true;

                    Rect area = Rect.Empty;
                    for (int i = 0; i < words.Count; i++)
                    {
                        int s = starts[i], e = s + words[i].Text.Length;
                        if (e > m.Index && s < m.Index + m.Length) area.Union(words[i].Bounds);
                    }
                    if (area.IsEmpty) continue;
                    double pad = Math.Max(2, area.Height * 0.18);
                    area.Inflate(pad, pad);
                    found.Add((area, kind));
                }
            }
        }
        return found;
    }

    private static bool Luhn(string digits)
    {
        var d = digits.Where(char.IsDigit).Select(c => c - '0').ToList();
        if (d.Count < 13 || d.Count > 19) return false;
        int sum = 0;
        for (int i = 0; i < d.Count; i++)
        {
            int v = d[d.Count - 1 - i];
            if (i % 2 == 1) { v *= 2; if (v > 9) v -= 9; }
            sum += v;
        }
        return sum % 10 == 0;
    }

    /// <summary>"2 emails and 1 phone number".</summary>
    public static string Describe(IEnumerable<string> kinds)
    {
        var parts = kinds.GroupBy(k => k).Select(g => g.Count() == 1 ? $"1 {g.Key}" : $"{g.Count()} {g.Key}{(g.Key.EndsWith("s") ? "es" : "s")}").ToList();
        return parts.Count <= 1 ? parts.FirstOrDefault() ?? "nothing" : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];
    }
}
