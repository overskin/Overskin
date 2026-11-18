using System.Windows;
using System.Windows.Controls;
using Overskin.Shared;
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

    /// <summary>What auto-hide looks for; chosen from the Auto-hide button's right-click menu.</summary>
    public static PrivateKinds Kinds { get; set; } = PrivateKinds.Default;

    /// <summary>Raised when <see cref="Kinds"/> is changed from a menu, so it can be saved.</summary>
    public static event Action? KindsChanged;

    /// <summary>
    /// Areas of the picture that hold private details, and what each one is. Empty when there
    /// are none; null when Windows has no text-reading language installed.
    /// </summary>
    public static async Task<List<(Rect Area, string Kind)>?> FindPrivateAsync(BitmapSource image)
    {
        // Windows' text reader skips some lines outright, such as a card number full of 1s (to it,
        // they look like a barcode). Reading the picture again larger, and again stretched sideways,
        // catches those; everything found is put together.
        var lines = await TextRecognizer.RecognizeLinesAsync(image);
        if (lines == null) return null;
        var all = new List<List<OcrWord>>(lines);
        foreach (var (scale, stretch) in new (double?, double)[] { (2, 1), (1, 2) })
        {
            try
            {
                if (await TextRecognizer.RecognizeLinesAsync(image, scale, stretch) is { } more) all.AddRange(more);
            }
            catch
            {
                // the extra readings only add; the first one stands on its own
            }
        }
        return Merge(FindPrivate(all));
    }

    /// <summary>One entry per detail when several readings found the same thing, covering all of them.</summary>
    private static List<(Rect Area, string Kind)> Merge(List<(Rect Area, string Kind)> found)
    {
        var merged = new List<(Rect Area, string Kind)>();
        foreach (var f in found)
        {
            int same = merged.FindIndex(m => Overlap(m.Area, f.Area) > 0.4);
            if (same < 0) { merged.Add(f); continue; }
            var area = merged[same].Area;
            area.Union(f.Area);
            merged[same] = (area, merged[same].Kind);
        }
        return merged;
    }

    private static double Overlap(Rect a, Rect b)
    {
        var i = Rect.Intersect(a, b);
        if (i.IsEmpty) return 0;
        return i.Width * i.Height / Math.Max(1, Math.Min(a.Width * a.Height, b.Width * b.Height));
    }

    public static List<(Rect Area, string Kind)> FindPrivate(List<List<OcrWord>> lines, PrivateKinds? kinds = null)
    {
        var words = lines.Select(l => (IReadOnlyList<TextWord>)l.Select(w => new TextWord(w.Text, w.Bounds.X, w.Bounds.Y, w.Bounds.Width, w.Bounds.Height)).ToList()).ToList();
        return PrivateDetails.Find(words, kinds ?? Kinds).Select(d => (new Rect(d.X, d.Y, d.Width, d.Height), d.Kind)).ToList();
    }

    /// <summary>"2 emails and 1 phone number".</summary>
    public static string Describe(IEnumerable<string> kinds) => PrivateDetails.Describe(kinds);

    /// <summary>A right-click menu that switches each kind of detail on or off.</summary>
    public static ContextMenu KindsMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "Hide these:", IsEnabled = false });
        foreach (var (kind, label) in PrivateDetails.Choices)
        {
            var item = new MenuItem { Header = label, IsCheckable = true, StaysOpenOnClick = true };
            item.Click += (_, _) =>
            {
                Kinds = item.IsChecked ? Kinds | kind : Kinds & ~kind;
                KindsChanged?.Invoke();
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var all = new MenuItem { Header = "Everything" };
        all.Click += (_, _) => { Kinds = PrivateKinds.All; KindsChanged?.Invoke(); };
        menu.Items.Add(all);
        var reset = new MenuItem { Header = "Back to the defaults" };
        reset.Click += (_, _) => { Kinds = PrivateKinds.Default; KindsChanged?.Invoke(); };
        menu.Items.Add(reset);
        // ticks reflect the current choice each time it opens
        menu.Opened += (_, _) =>
        {
            int i = 1;
            foreach (var (kind, _) in PrivateDetails.Choices) ((MenuItem)menu.Items[i++]).IsChecked = (Kinds & kind) != 0;
        };
        return menu;
    }
}
