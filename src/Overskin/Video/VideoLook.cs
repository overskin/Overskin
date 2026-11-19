using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Overskin;

/// <summary>
/// Puts the recording on a backdrop, like Beautify does for screenshots: the screen shrinks into a
/// card with rounded corners and a soft shadow on a gradient. The backdrop is drawn once; each
/// frame only copies the screen into it.
/// </summary>
internal sealed class VideoBackdrop
{
    public int Width { get; }
    public int Height { get; }
    public int X { get; }
    public int Y { get; }
    public int InnerWidth { get; }
    public int InnerHeight { get; }

    private readonly byte[] _back;  // backdrop and shadow, with the card black (premultiplied)
    private readonly byte[] _cover; // how much of each pixel the card covers, 0 to 255
    private readonly int _radius;

    private VideoBackdrop(int w, int h, int x, int y, int iw, int ih, int radius, byte[] back, byte[] cover)
    {
        Width = w; Height = h; X = x; Y = y; InnerWidth = iw; InnerHeight = ih;
        _radius = radius; _back = back; _cover = cover;
    }

    /// <summary>Draws the backdrop for a <paramref name="w"/> × <paramref name="h"/> video; null for "None". Call on the UI thread.</summary>
    public static VideoBackdrop? Create(string name, int w, int h)
    {
        if (string.IsNullOrEmpty(name) || name == "None" || !Beautify.Backgrounds.Any(b => b.Name == name)) return null;
        int pad = Math.Max(8, (int)Math.Round(Math.Min(w, h) * 0.06)) & ~1;
        int iw = (w - 2 * pad) & ~1, ih = (h - 2 * pad) & ~1;
        if (iw < 32 || ih < 32) return null;
        int x = (w - iw) / 2, y = (h - ih) / 2;
        int radius = Math.Max(4, (int)Math.Round(Math.Min(w, h) * 0.018));

        byte[] Draw(bool mask)
        {
            var card = new Border
            {
                Width = iw, Height = ih,
                CornerRadius = new CornerRadius(radius),
                Background = mask ? Brushes.White : Brushes.Black,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(x, y, 0, 0),
                Effect = mask ? null : new DropShadowEffect
                {
                    BlurRadius = Math.Max(10, pad * 0.9), ShadowDepth = Math.Max(2, pad * 0.18), Direction = 270,
                    Opacity = 0.42, Color = Colors.Black, RenderingBias = RenderingBias.Quality,
                },
            };
            var root = new Grid { Width = w, Height = h, Background = mask ? Brushes.Black : Beautify.BackgroundBrush(name), SnapsToDevicePixels = true };
            root.Children.Add(card);
            root.Measure(new Size(w, h));
            root.Arrange(new Rect(0, 0, w, h));
            root.UpdateLayout();
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(root);
            var px = new byte[w * h * 4];
            rtb.CopyPixels(px, w * 4, 0);
            return px;
        }

        var back = Draw(false);
        var maskPx = Draw(true);
        var cover = new byte[iw * ih];
        for (int yy = 0; yy < ih; yy++)
            for (int xx = 0; xx < iw; xx++)
                cover[yy * iw + xx] = maskPx[((y + yy) * w + x + xx) * 4 + 1];
        return new VideoBackdrop(w, h, x, y, iw, ih, radius, back, cover);
    }

    /// <summary>The finished frame: the screen (InnerWidth × InnerHeight) laid into the backdrop.</summary>
    public byte[] Compose(byte[] inner)
    {
        var frame = (byte[])_back.Clone();
        int band = _radius + 2;
        for (int yy = 0; yy < InnerHeight; yy++)
        {
            int src = yy * InnerWidth * 4, dst = ((Y + yy) * Width + X) * 4;
            bool cornerRow = yy < band || yy >= InnerHeight - band;
            if (!cornerRow)
            {
                Buffer.BlockCopy(inner, src, frame, dst, InnerWidth * 4);
                continue;
            }
            for (int xx = 0; xx < InnerWidth; xx++)
            {
                if (xx == band && InnerWidth - 2 * band > 0)
                {
                    // the middle of a corner row is fully covered too
                    int run = InnerWidth - 2 * band;
                    Buffer.BlockCopy(inner, src + xx * 4, frame, dst + xx * 4, run * 4);
                    xx += run - 1;
                    continue;
                }
                int c = _cover[yy * InnerWidth + xx];
                if (c == 0) continue;
                int i = xx * 4;
                // premultiplied "over": the backdrop already has the card's corner as black
                for (int ch = 0; ch < 3; ch++)
                    frame[dst + i + ch] = (byte)Math.Min(255, frame[dst + i + ch] + inner[src + i + ch] * c / 255);
            }
        }
        return frame;
    }
}

/// <summary>
/// Shows keyboard shortcuts in the recording (Ctrl + C, Alt + Tab, Enter …) as a label near the
/// bottom, so viewers can follow along. Only shortcuts and a few special keys are shown, never
/// ordinary typing, so nothing you type (such as a password) ends up in the video.
/// </summary>
internal sealed class KeyCaps : IDisposable
{
    private readonly record struct Shown(double Time, PointerSprite Picture);

    private readonly Dispatcher _ui;
    private readonly Func<double> _now;
    private readonly int _height;
    private readonly List<Shown> _shown = new();
    private readonly object _gate = new();
    private readonly Thread? _thread;
    private volatile bool _stop;

    /// <summary>How long a label stays, in seconds, including fading out.</summary>
    public const double Hold = 1.6;

    public KeyCaps(Dispatcher ui, Func<double> now, int videoHeight, bool start = true)
    {
        _ui = ui;
        _now = now;
        _height = videoHeight;
        if (!start) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "Overskin shortcut keys" };
        _thread.Start();
    }

    private static bool Held(int vk) => (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;

    private void Run()
    {
        var down = new bool[256];
        while (!_stop)
        {
            for (int vk = 0x08; vk < 0xFF; vk++)
            {
                if (vk is >= 0x10 and <= 0x12 || vk is 0x5B or 0x5C || vk is >= 0xA0 and <= 0xA5) continue; // modifiers themselves
                bool now = Held(vk);
                if (now && !down[vk])
                {
                    string? label = Label(vk, Held(0x11), Held(0x12), Held(0x10), Held(0x5B) || Held(0x5C));
                    if (label != null) Press(label);
                }
                down[vk] = now;
            }
            Thread.Sleep(8);
        }
    }

    /// <summary>The label for a key pressed with these modifiers, or null for ordinary typing.</summary>
    public static string? Label(int vk, bool ctrl, bool alt, bool shift, bool win)
    {
        string? name = vk switch
        {
            0x08 => "Backspace", 0x09 => "Tab", 0x0D => "Enter", 0x1B => "Esc", 0x20 => "Space",
            0x21 => "Page Up", 0x22 => "Page Down", 0x23 => "End", 0x24 => "Home",
            0x25 => "←", 0x26 => "↑", 0x27 => "→", 0x28 => "↓", 0x2C => "Print Screen", 0x2D => "Insert", 0x2E => "Delete",
            >= 0x30 and <= 0x39 => ((char)vk).ToString(),
            >= 0x41 and <= 0x5A => ((char)vk).ToString(),
            >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
            0xBA => ";", 0xBB => "=", 0xBC => ",", 0xBD => "-", 0xBE => ".", 0xBF => "/", 0xC0 => "`",
            0xDB => "[", 0xDC => "\\", 0xDD => "]", 0xDE => "'",
            _ => null,
        };
        if (name == null) return null;
        bool special = vk is 0x09 or 0x0D or 0x1B or 0x2E or (>= 0x70 and <= 0x87);
        // plain letters, numbers, Space, Backspace and arrows (with or without Shift) are typing: never shown
        if (!ctrl && !alt && !win && !special) return null;
        var parts = new List<string>();
        if (ctrl) parts.Add("Ctrl");
        if (alt) parts.Add("Alt");
        if (win) parts.Add("Win");
        if (shift) parts.Add("Shift");
        parts.Add(name);
        return string.Join(" + ", parts);
    }

    /// <summary>Shows a label from now (the tracking thread does this; tests call it directly).</summary>
    public void Press(string label)
    {
        double time = _now();
        _ui.BeginInvoke(() =>
        {
            var picture = Render(label, _height);
            lock (_gate)
            {
                _shown.Add(new Shown(time, picture));
                if (_shown.Count > 20) _shown.RemoveAt(0);
            }
        });
    }

    /// <summary>Draws the label as a dark rounded pill. Call on the UI thread.</summary>
    public static PointerSprite Render(string label, int videoHeight)
    {
        double size = Math.Clamp(videoHeight * 0.03, 13, 64);
        var text = new FormattedText(label, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), size, Brushes.White, 1.0);
        double padX = size * 0.8, padY = size * 0.45;
        int w = (int)Math.Ceiling(text.Width + 2 * padX), h = (int)Math.Ceiling(text.Height + 2 * padY);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(215, 24, 24, 27)), new Pen(new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), 1),
                new Rect(0.5, 0.5, w - 1, h - 1), h / 2.0, h / 2.0);
            dc.DrawText(text, new Point(padX, padY));
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var px = new byte[w * h * 4];
        rtb.CopyPixels(px, w * 4, 0);
        return new PointerSprite(px, w, h, w / 2.0, h);
    }

    /// <summary>Draws the label showing at <paramref name="time"/> (if any) near the bottom of the frame, fading out.</summary>
    public void Draw(byte[] frame, int w, int h, double time)
    {
        Shown? current = null;
        lock (_gate)
            for (int i = _shown.Count - 1; i >= 0; i--)
                if (_shown[i].Time <= time) { current = _shown[i]; break; }
        if (current is not { } s || time - s.Time > Hold) return;
        double age = time - s.Time;
        double fade = age < Hold - 0.35 ? 1 : (Hold - age) / 0.35;
        double rise = age < 0.12 ? (1 - age / 0.12) * h * 0.01 : 0;
        Blend(frame, w, h, s.Picture, w / 2.0, h - h * 0.1 + rise, fade);
    }

    private static void Blend(byte[] frame, int fw, int fh, PointerSprite s, double x, double y, double opacity)
    {
        int left = (int)Math.Round(x - s.HotX), top = (int)Math.Round(y - s.HotY);
        int k = (int)Math.Round(Math.Clamp(opacity, 0, 1) * 256);
        for (int sy = 0; sy < s.Height; sy++)
        {
            int fy = top + sy;
            if (fy < 0 || fy >= fh) continue;
            for (int sx = 0; sx < s.Width; sx++)
            {
                int fx = left + sx;
                if (fx < 0 || fx >= fw) continue;
                int si = (sy * s.Width + sx) * 4;
                int a = s.Pixels[si + 3] * k >> 8;
                if (a == 0) continue;
                int fi = (fy * fw + fx) * 4, keep = 255 - a;
                for (int c = 0; c < 3; c++)
                    frame[fi + c] = (byte)Math.Min(255, (s.Pixels[si + c] * k >> 8) + frame[fi + c] * keep / 255);
            }
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread?.Join(300);
    }
}
