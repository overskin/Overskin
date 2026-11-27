using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Overskin.Linux.Core;
using Overskin.Linux.Interface;

namespace Overskin.Linux.Platform;

/// <summary>A picture to lay over frames: premultiplied BGRA, and the point that goes on the spot (a pointer's tip).</summary>
public sealed record Sprite(byte[] Pixels, int Width, int Height, double HotX, double HotY);

/// <summary>A shortcut pressed during a recording, to show as a label (seconds into the recording).</summary>
public sealed class KeyPress
{
    public double T { get; set; }
    public string Label { get; set; } = "";
}

/// <summary>
/// How recordings are finished (as on Windows): a sharp pointer drawn smoothly (white or black, any
/// size, motion-blurred when it moves fast), rings where it clicks, shortcuts shown as labels, and
/// the screen laid on a backdrop. All drawn into the frames after recording.
/// </summary>
public static class RecordingLook
{
    // drawn on a 32-unit grid; the arrow's tip is at (2, 2)
    private const string ArrowShape = "M 2,2 L 2,24.5 L 7.4,19.4 L 11.1,27.6 L 15.0,25.9 L 11.4,17.8 L 18.8,17.8 Z";

    public static readonly string[] PointerStyles = { "White", "Black" };

    /// <summary>The arrow pointer <paramref name="size"/> pixels tall, with an outline and a soft shadow.</summary>
    public static Sprite Pointer(int size, bool dark)
    {
        double k = size / 32.0;
        IBrush fill = dark ? new SolidColorBrush(Color.FromRgb(20, 20, 22)) : Brushes.White;
        IBrush edge = dark ? Brushes.White : new SolidColorBrush(Color.FromRgb(20, 20, 22));
        int pad = (int)Math.Ceiling(4 * k) + 2;
        int w = (int)Math.Ceiling(24 * k) + 2 * pad, h = size + 2 * pad;
        var geometry = StreamGeometry.Parse(ArrowShape);
        var bmp = Imaging.Render(new PixelSize(w, h), ctx =>
        {
            using (ctx.PushTransform(Matrix.CreateScale(k, k) * Matrix.CreateTranslation(pad, pad)))
            {
                // the shadow: the same shape a little lower, soft and faint
                using (ctx.PushTransform(Matrix.CreateTranslation(0, 1.4)))
                using (ctx.PushOpacity(0.3))
                    ctx.DrawGeometry(Brushes.Black, new Pen(Brushes.Black, 2.5) { LineJoin = PenLineJoin.Round }, geometry);
                ctx.DrawGeometry(fill, new Pen(edge, 1.5) { LineJoin = PenLineJoin.Round }, geometry);
            }
        });
        return new Sprite(Imaging.Pixels(bmp, out _), w, h, pad + 2 * k, pad + 2 * k);
    }

    /// <summary>Lays a sprite over a BGRA frame with its hot spot at (x, y), faded by <paramref name="opacity"/>.</summary>
    public static void Blend(byte[] frame, int fw, int fh, Sprite s, double x, double y, double opacity = 1)
    {
        int left = (int)Math.Round(x - s.HotX), top = (int)Math.Round(y - s.HotY);
        int k = (int)Math.Round(Math.Clamp(opacity, 0, 1) * 256);
        if (k <= 0) return;
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

    /// <summary>A smooth ring: white with a thin dark edge, faded by <paramref name="opacity"/>.</summary>
    public static void Ring(byte[] frame, int fw, int fh, double cx, double cy, double radius, double width, double opacity)
    {
        int x0 = Math.Max(0, (int)(cx - radius - width - 2)), x1 = Math.Min(fw - 1, (int)(cx + radius + width + 2));
        int y0 = Math.Max(0, (int)(cy - radius - width - 2)), y1 = Math.Min(fh - 1, (int)(cy + radius + width + 2));
        double half = width / 2;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                double d = Math.Sqrt((x + 0.5 - cx) * (x + 0.5 - cx) + (y + 0.5 - cy) * (y + 0.5 - cy));
                double off = Math.Abs(d - radius);
                double white = Math.Clamp(half - off + 0.5, 0, 1);
                double shade = Math.Clamp(half + 1 - off + 0.5, 0, 1) - white;
                if (white <= 0 && shade <= 0) continue;
                int i = (y * fw + x) * 4;
                for (int c = 0; c < 3; c++)
                {
                    double v = frame[i + c];
                    v += (255 - v) * white * opacity;
                    v += (20 - v) * shade * opacity * 0.6;
                    frame[i + c] = (byte)Math.Clamp(v, 0, 255);
                }
            }
    }

    /// <summary>A shortcut's label as a dark rounded pill, its hot spot the middle of its bottom edge.</summary>
    public static Sprite KeyLabel(string label, int videoHeight)
    {
        double size = Math.Clamp(videoHeight * 0.03, 13, 64);
        var text = new FormattedText(label, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Inter"), FontStyle.Normal, FontWeight.SemiBold), size, Brushes.White);
        double padX = size * 0.8, padY = size * 0.45;
        int w = (int)Math.Ceiling(text.Width + 2 * padX), h = (int)Math.Ceiling(text.Height + 2 * padY);
        var bmp = Imaging.Render(new PixelSize(w, h), ctx =>
        {
            ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(215, 24, 24, 27)), new Pen(new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), 1),
                new Rect(0.5, 0.5, w - 1, h - 1), h / 2.0, h / 2.0);
            ctx.DrawText(text, new Point(padX, padY));
        });
        return new Sprite(Imaging.Pixels(bmp, out _), w, h, w / 2.0, h);
    }

    /// <summary>How long a shortcut's label stays, in seconds, including fading out.</summary>
    public const double KeyHold = 1.6;

    /// <summary>Draws the label showing at <paramref name="t"/> (if any) near the bottom, rising in and fading out.</summary>
    public static void DrawKey(byte[] frame, int w, int h, IReadOnlyList<KeyPress> keys, IReadOnlyDictionary<string, Sprite> labels, double t)
    {
        KeyPress? current = null;
        for (int i = keys.Count - 1; i >= 0; i--)
            if (keys[i].T <= t) { current = keys[i]; break; }
        if (current == null || t - current.T > KeyHold || !labels.TryGetValue(current.Label, out var s)) return;
        double age = t - current.T;
        double fade = age < KeyHold - 0.35 ? 1 : (KeyHold - age) / 0.35;
        double rise = age < 0.12 ? (1 - age / 0.12) * h * 0.01 : 0;
        Blend(frame, w, h, s, w / 2.0, h - h * 0.1 + rise, fade);
    }
}

/// <summary>
/// The recording on a backdrop (as on Windows), like Beautify does for screenshots: the screen
/// shrinks into a card with rounded corners and a soft shadow on a gradient. The backdrop is
/// drawn once; each frame only copies the screen into it.
/// </summary>
public sealed class VideoBackdrop
{
    public int Width { get; }
    public int Height { get; }
    public int X { get; }
    public int Y { get; }
    public int InnerWidth { get; }
    public int InnerHeight { get; }
    private readonly byte[] _back;  // the backdrop and shadow, the card black
    private readonly byte[] _cover; // how much of each pixel of the card is card, 0 to 255

    private VideoBackdrop(int w, int h, int x, int y, int iw, int ih, byte[] back, byte[] cover)
    {
        Width = w; Height = h; X = x; Y = y; InnerWidth = iw; InnerHeight = ih;
        _back = back;
        _cover = cover;
    }

    /// <summary>The backdrop for a <paramref name="w"/> × <paramref name="h"/> video; null for "None".</summary>
    public static VideoBackdrop? Create(string name, int w, int h)
    {
        if (string.IsNullOrEmpty(name) || name == "None" || Beautify.BackgroundBrush(name) is not { } brush) return null;
        int pad = Math.Max(8, (int)Math.Round(Math.Min(w, h) * 0.06)) & ~1;
        int iw = (w - 2 * pad) & ~1, ih = (h - 2 * pad) & ~1;
        if (iw < 32 || ih < 32) return null;
        int x = (w - iw) / 2, y = (h - ih) / 2;
        int radius = Math.Max(4, (int)Math.Round(Math.Min(w, h) * 0.018));
        var card = new RoundedRect(new Rect(x, y, iw, ih), radius);
        var back = Imaging.Pixels(Imaging.Render(new PixelSize(w, h), ctx =>
        {
            ctx.FillRectangle(brush, new Rect(0, 0, w, h));
            ctx.DrawRectangle(Brushes.Black, null, card, new BoxShadows(new BoxShadow
            {
                Blur = Math.Max(10, pad * 0.9), OffsetY = Math.Max(2, pad * 0.18), Color = Color.FromArgb(107, 0, 0, 0),
            }));
        }), out _);
        var mask = Imaging.Pixels(Imaging.Render(new PixelSize(w, h), ctx => ctx.DrawRectangle(Brushes.White, null, card)), out _);
        var cover = new byte[iw * ih];
        for (int yy = 0; yy < ih; yy++)
            for (int xx = 0; xx < iw; xx++)
                cover[yy * iw + xx] = mask[((y + yy) * w + x + xx) * 4 + 3];
        return new VideoBackdrop(w, h, x, y, iw, ih, back, cover);
    }

    /// <summary>The finished frame: the screen (InnerWidth × InnerHeight) laid into the backdrop.</summary>
    public byte[] Compose(byte[] inner)
    {
        var frame = (byte[])_back.Clone();
        for (int yy = 0; yy < InnerHeight; yy++)
        {
            int src = yy * InnerWidth * 4, dst = ((Y + yy) * Width + X) * 4;
            for (int xx = 0; xx < InnerWidth; xx++)
            {
                int a = _cover[yy * InnerWidth + xx];
                if (a == 0) continue;
                int s = src + xx * 4, d = dst + xx * 4;
                if (a == 255) { frame[d] = inner[s]; frame[d + 1] = inner[s + 1]; frame[d + 2] = inner[s + 2]; frame[d + 3] = 255; continue; }
                for (int c = 0; c < 3; c++) frame[d + c] = (byte)((inner[s + c] * a + frame[d + c] * (255 - a)) / 255);
                frame[d + 3] = 255;
            }
        }
        return frame;
    }

    /// <summary>A whole frame made smaller to fit the card (smoothly).</summary>
    public byte[] Shrink(byte[] src, int w, int h)
    {
        var dst = new byte[InnerWidth * InnerHeight * 4];
        double kx = w / (double)InnerWidth, ky = h / (double)InnerHeight;
        Parallel.For(0, InnerHeight, y =>
        {
            double fy0 = y * ky, fy1 = (y + 1) * ky;
            int y0 = (int)fy0, y1 = Math.Min(h, (int)Math.Ceiling(fy1));
            for (int x = 0; x < InnerWidth; x++)
            {
                double fx0 = x * kx, fx1 = (x + 1) * kx;
                int x0 = (int)fx0, x1 = Math.Min(w, (int)Math.Ceiling(fx1));
                double b = 0, g = 0, r = 0, n = 0;
                for (int sy = y0; sy < y1; sy++)
                    for (int sx = x0; sx < x1; sx++)
                    {
                        int o = (sy * w + sx) * 4;
                        b += src[o]; g += src[o + 1]; r += src[o + 2]; n++;
                    }
                int d = (y * InnerWidth + x) * 4;
                if (n > 0) { dst[d] = (byte)(b / n); dst[d + 1] = (byte)(g / n); dst[d + 2] = (byte)(r / n); }
                dst[d + 3] = 255;
            }
        });
        return dst;
    }
}

/// <summary>
/// Shortcuts pressed while recording on a Mac (⌘ C, ⌥ ⇥, Return…), read the way Windows does it:
/// which keys are down, checked many times a second. Only shortcuts and a few special keys are
/// kept, never ordinary typing, so nothing you type (such as a password) ends up in the video.
/// macOS asks once for Input Monitoring.
/// </summary>
public sealed class MacKeys : IDisposable
{
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const int CombinedSession = 0;

    [DllImport(CoreGraphics)] private static extern bool CGEventSourceKeyState(int state, ushort key);
    [DllImport(CoreGraphics)] private static extern ulong CGEventSourceFlagsState(int state);
    [DllImport(CoreGraphics)] private static extern bool CGPreflightListenEventAccess();
    [DllImport(CoreGraphics)] private static extern bool CGRequestListenEventAccess();

    private readonly List<KeyPress> _pressed = new();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly Thread _thread;
    private volatile bool _stop;
    private readonly double _offset;

    /// <summary>Whether Overskin may read the keys (asking when it may not, once).</summary>
    public static bool Allowed(bool ask)
    {
        if (!Mac.IsMac) return false;
        try { return CGPreflightListenEventAccess() || (ask && CGRequestListenEventAccess()); }
        catch { return false; }
    }

    /// <param name="offset">Seconds already recorded when this starts.</param>
    public MacKeys(double offset = 0)
    {
        _offset = offset;
        _thread = new Thread(Run) { IsBackground = true, Name = "Overskin shortcut keys" };
        _thread.Start();
    }

    public List<KeyPress> Pressed { get { lock (_pressed) return _pressed.ToList(); } }

    private void Run()
    {
        var down = new bool[128];
        while (!_stop)
        {
            ulong flags = CGEventSourceFlagsState(CombinedSession);
            bool shift = (flags & 0x20000) != 0, ctrl = (flags & 0x40000) != 0, alt = (flags & 0x80000) != 0, cmd = (flags & 0x100000) != 0;
            for (ushort key = 0; key < 128; key++)
            {
                if (key is >= 54 and <= 63) continue; // the modifiers themselves
                bool now = CGEventSourceKeyState(CombinedSession, key);
                if (now && !down[key] && Label(key, ctrl, alt, shift, cmd) is { } label)
                    lock (_pressed) _pressed.Add(new KeyPress { T = _offset + _clock.Elapsed.TotalSeconds, Label = label });
                down[key] = now;
            }
            Thread.Sleep(8);
        }
    }

    private static readonly Dictionary<int, string> Names = new()
    {
        [0] = "A", [1] = "S", [2] = "D", [3] = "F", [4] = "H", [5] = "G", [6] = "Z", [7] = "X", [8] = "C", [9] = "V", [11] = "B", [12] = "Q", [13] = "W",
        [14] = "E", [15] = "R", [16] = "Y", [17] = "T", [18] = "1", [19] = "2", [20] = "3", [21] = "4", [22] = "6", [23] = "5", [24] = "=", [25] = "9",
        [26] = "7", [27] = "-", [28] = "8", [29] = "0", [30] = "]", [31] = "O", [32] = "U", [33] = "[", [34] = "I", [35] = "P", [36] = "Return", [37] = "L",
        [38] = "J", [39] = "'", [40] = "K", [41] = ";", [42] = "\\", [43] = ",", [44] = "/", [45] = "N", [46] = "M", [47] = ".", [48] = "Tab", [49] = "Space",
        [50] = "`", [51] = "Delete", [53] = "Esc", [96] = "F5", [97] = "F6", [98] = "F7", [99] = "F3", [100] = "F8", [101] = "F9", [103] = "F11",
        [109] = "F10", [111] = "F12", [115] = "Home", [116] = "Page Up", [117] = "Forward Delete", [118] = "F4", [119] = "End", [120] = "F2",
        [121] = "Page Down", [122] = "F1", [123] = "←", [124] = "→", [125] = "↓", [126] = "↑",
    };

    /// <summary>The label for a key pressed with these modifiers, or null for ordinary typing.</summary>
    public static string? Label(int key, bool ctrl, bool alt, bool shift, bool cmd)
    {
        if (!Names.TryGetValue(key, out var name)) return null;
        bool special = key is 48 or 36 or 53 or 117 || name.StartsWith('F') && name.Length <= 3 && name.Length >= 2 && char.IsDigit(name[1]);
        // plain letters, numbers, Space, Delete and arrows (with or without Shift) are typing: never shown
        if (!ctrl && !alt && !cmd && !special) return null;
        var parts = new List<string>();
        if (ctrl) parts.Add("⌃");
        if (alt) parts.Add("⌥");
        if (shift) parts.Add("⇧");
        if (cmd) parts.Add("⌘");
        parts.Add(name);
        return string.Join(" ", parts);
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(300);
    }
}
