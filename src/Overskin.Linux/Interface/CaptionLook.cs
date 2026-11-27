using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Overskin.Linux.Core;

namespace Overskin.Linux.Interface;

/// <summary>Something drawn on a video for a while: text, an arrow, or a subtitle line.</summary>
public sealed class VideoOverlay
{
    public enum Kinds { Text, Caption, Arrow }
    public Kinds Kind { get; init; }
    public string Text { get; set; } = "";
    /// <summary>
    /// Where it goes, as fractions: text's centre and an arrow's tail, of the whole frame (so they
    /// stay on what they point at when it's cropped); a subtitle's bottom middle, of what's kept.
    /// </summary>
    public Point At { get; set; } = new(0.5, 0.92);
    /// <summary>An arrow's head, as a fraction of the whole frame.</summary>
    public Point To { get; set; }
    /// <summary>Seconds in the original video.</summary>
    public double Start { get; set; }
    public double End { get; set; }
    /// <summary>Subtitles: one of <see cref="CaptionLook.Looks"/>.</summary>
    public string Look { get; set; } = "Classic";
    /// <summary>Subtitles: "Line", "Light" (the word being said lights up) or "Build" (words appear as said).</summary>
    public string Words { get; set; } = "Line";
    /// <summary>Subtitles: the word being said, or −1.</summary>
    public int Highlight { get; set; } = -1;

    public VideoOverlay Clone() => (VideoOverlay)MemberwiseClone();
}

/// <summary>
/// How subtitles look, and how they follow the voice word by word (as on Windows): each word
/// lights up as it's said, or the line builds up a word at a time.
/// </summary>
public static class CaptionLook
{
    public static readonly (string Name, string Tip)[] Looks =
    {
        ("Classic", "White on a dark band, like TV subtitles"),
        ("Bold", "Big capitals with a thick black outline, like Shorts and TikTok"),
        ("Clean", "White words with a soft shadow, no band"),
        ("Boxed", "White on black, the word being said in a green box"),
    };

    public static readonly (string Name, string Label)[] WordModes = { ("Line", "Whole line"), ("Light", "Light up"), ("Build", "Build up") };

    public static List<(int Start, int Length)> Words(string text)
    {
        var words = new List<(int, int)>();
        int i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            int start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
            if (i > start) words.Add((start, i - start));
        }
        return words;
    }

    /// <summary>When each word of a line is said: the line's time shared out by the words' lengths.</summary>
    public static List<(double Start, double End)> WordTimes(string text, double start, double end)
    {
        var words = Words(text);
        var times = new List<(double, double)>();
        if (words.Count == 0) return times;
        double total = words.Sum(w => w.Length + 1.5);
        double t = start;
        foreach (var w in words)
        {
            double len = (end - start) * (w.Length + 1.5) / total;
            times.Add((t, t + len));
            t += len;
        }
        return times;
    }

    public static int WordAt(VideoOverlay caption, double now)
    {
        var times = WordTimes(caption.Text, caption.Start, caption.End);
        for (int i = times.Count - 1; i >= 0; i--)
            if (now >= times[i].Start) return i;
        return times.Count > 0 ? 0 : -1;
    }

    /// <summary>Subtitles split into one overlay per word for saving, each with that word lit.</summary>
    public static List<VideoOverlay> Expand(IEnumerable<VideoOverlay> overlays)
    {
        var result = new List<VideoOverlay>();
        foreach (var o in overlays)
        {
            if (o.Kind != VideoOverlay.Kinds.Caption || o.Words == "Line") { result.Add(o); continue; }
            var times = WordTimes(o.Text, o.Start, o.End);
            for (int i = 0; i < times.Count; i++)
            {
                var part = o.Clone();
                part.Start = times[i].Start;
                part.End = i == times.Count - 1 ? o.End : times[i + 1].Start;
                part.Highlight = i;
                result.Add(part);
            }
        }
        return result;
    }

    /// <summary>Draws an overlay onto a video frame <paramref name="w"/> × <paramref name="h"/> pixels.</summary>
    public static void Draw(DrawingContext dc, VideoOverlay o, int w, int h)
    {
        if (o.Kind == VideoOverlay.Kinds.Text) { DrawText(dc, o, w, h); return; }
        if (o.Kind == VideoOverlay.Kinds.Arrow) { DrawArrow(dc, o, w, h); return; }
        string look = Looks.Any(l => l.Name == o.Look) ? o.Look : "Classic";
        string text = o.Text.Length == 0 ? " " : look == "Bold" ? o.Text.ToUpper(CultureInfo.CurrentCulture) : o.Text;
        double size = look switch
        {
            "Bold" => Math.Clamp(h * 0.07, 18, 120),
            "Boxed" or "Clean" => Math.Clamp(h * 0.05, 15, 90),
            _ => Math.Clamp(h * 0.042, 13, 72),
        };
        var face = look == "Bold" ? FontChoices.Face("Anton")
            : new Typeface(new FontFamily("Inter"), FontStyle.Normal, look == "Classic" ? FontWeight.SemiBold : FontWeight.Bold);
        double padX = size * 0.5, padY = size * 0.22;
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, Brushes.White)
        {
            TextAlignment = TextAlignment.Center,
            MaxTextWidth = Math.Max(size * 4, w * (look == "Bold" ? 0.8 : 0.85) - 2 * padX),
        };
        double bw = ft.Width + 2 * padX, bh = ft.Height + 2 * padY;
        var anchor = new Point(o.At.X * w, o.At.Y * h);
        var bounds = new Rect(anchor.X - bw / 2, anchor.Y - bh, bw, bh);
        var origin = new Point(anchor.X - ft.MaxTextWidth / 2, bounds.Y + padY);

        var words = Words(text);
        int lit = o.Highlight;
        bool build = o.Words == "Build" && lit >= 0 && lit < words.Count;
        bool light = lit >= 0 && lit < words.Count && o.Words != "Line";

        if (look is "Classic" or "Boxed")
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(look == "Boxed" ? (byte)225 : (byte)190, 0, 0, 0)), null, bounds, size * 0.25, size * 0.25);

        // building up: only the words said so far, laid out where the whole line puts them
        using var clip = build && ft.BuildHighlightGeometry(origin, 0, words[lit].Start + words[lit].Length) is { } shown ? dc.PushGeometryClip(shown) : (IDisposable?)null;
        if (light && look == "Boxed" && ft.BuildHighlightGeometry(origin, words[lit].Start, words[lit].Length) is { } box)
        {
            var r = box.Bounds.Inflate(new Thickness(size * 0.12, size * 0.02));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)), null, r, size * 0.18, size * 0.18);
        }

        void Paint(IBrush fill)
        {
            var g = ft.BuildGeometry(origin);
            switch (look)
            {
                case "Bold":
                    if (g != null) { dc.DrawGeometry(null, new Pen(Brushes.Black, size * 0.16) { LineJoin = PenLineJoin.Round }, g); dc.DrawGeometry(fill, null, g); }
                    break;
                case "Clean":
                    if (g != null)
                    {
                        for (int k = 3; k >= 1; k--)
                            dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(46, 0, 0, 0)), size * 0.09 * k) { LineJoin = PenLineJoin.Round }, g);
                        dc.DrawGeometry(fill, null, g);
                    }
                    break;
                default:
                    if (g != null) dc.DrawGeometry(fill, null, g);
                    break;
            }
        }
        Paint(Brushes.White);
        if (light && look != "Boxed" && ft.BuildHighlightGeometry(origin, words[lit].Start, words[lit].Length) is { } word)
            using (dc.PushGeometryClip(word))
                Paint(new SolidColorBrush(look == "Classic" ? Color.FromRgb(0xFF, 0xD3, 0x4D) : Color.FromRgb(0xFF, 0xE8, 0x1A)));
    }

    /// <summary>Text placed on the video: bold white with a dark outline, centred on its point.</summary>
    /// <summary>A red arrow from its tail to its head, with a thin dark edge so it shows on red or busy backgrounds (as on Windows).</summary>
    private static void DrawArrow(DrawingContext dc, VideoOverlay o, int w, int h)
    {
        double thick = Math.Max(3, h * 0.008);
        var geometry = ArrowGeometry(new Point(o.At.X * w, o.At.Y * h), new Point(o.To.X * w, o.To.Y * h), thick);
        if (geometry == null) return;
        var red = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)), thick * 1.6) { LineJoin = PenLineJoin.Round, LineCap = PenLineCap.Round }, geometry);
        dc.DrawGeometry(red, new Pen(red, thick) { LineJoin = PenLineJoin.Round, LineCap = PenLineCap.Round }, geometry);
    }

    /// <summary>An arrow's outline: a shaft and a filled head at <paramref name="b"/>.</summary>
    public static Geometry? ArrowGeometry(Point a, Point b, double thick)
    {
        var d = b - a;
        double length = Math.Sqrt(d.X * d.X + d.Y * d.Y);
        if (length < 1) return null;
        var unit = new Point(d.X / length, d.Y / length);
        var normal = new Point(-unit.Y, unit.X);
        double head = Math.Min(length * 0.6, thick * 4 + 10);
        var baseCentre = b - unit * head;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            // the shaft, as a thin box up to the head
            var shaftEnd = b - unit * (head * 0.6);
            c.BeginFigure(a + normal * (thick / 2), true);
            c.LineTo(shaftEnd + normal * (thick / 2));
            c.LineTo(shaftEnd - normal * (thick / 2));
            c.LineTo(a - normal * (thick / 2));
            c.EndFigure(true);
            c.BeginFigure(b, true);
            c.LineTo(baseCentre + normal * (head * 0.55));
            c.LineTo(baseCentre - normal * (head * 0.55));
            c.EndFigure(true);
        }
        return g;
    }

    /// <summary>The box an overlay takes up on a frame <paramref name="w"/> × <paramref name="h"/>, for picking it up.</summary>
    public static Rect Bounds(VideoOverlay o, int w, int h)
    {
        switch (o.Kind)
        {
            case VideoOverlay.Kinds.Arrow:
            {
                double thick = Math.Max(3, h * 0.008) * 4 + 10;
                var a = new Point(o.At.X * w, o.At.Y * h);
                var b = new Point(o.To.X * w, o.To.Y * h);
                return new Rect(a, b).Normalize().Inflate(thick);
            }
            case VideoOverlay.Kinds.Text:
            {
                double size = Math.Clamp(h * 0.06, 14, 110);
                var ft = new FormattedText(o.Text.Length == 0 ? " " : o.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Inter"), FontStyle.Normal, FontWeight.Bold), size, Brushes.White) { TextAlignment = TextAlignment.Center, MaxTextWidth = w * 0.9 };
                return new Rect(o.At.X * w - ft.Width / 2, o.At.Y * h - ft.Height / 2, ft.Width, ft.Height).Inflate(size * 0.2);
            }
            default:
            {
                string look = Looks.Any(l => l.Name == o.Look) ? o.Look : "Classic";
                string text = o.Text.Length == 0 ? " " : look == "Bold" ? o.Text.ToUpper(CultureInfo.CurrentCulture) : o.Text;
                double size = look switch { "Bold" => Math.Clamp(h * 0.07, 18, 120), "Boxed" or "Clean" => Math.Clamp(h * 0.05, 15, 90), _ => Math.Clamp(h * 0.042, 13, 72) };
                var face = look == "Bold" ? FontChoices.Face("Anton") : new Typeface(new FontFamily("Inter"), FontStyle.Normal, look == "Classic" ? FontWeight.SemiBold : FontWeight.Bold);
                double padX = size * 0.5, padY = size * 0.22;
                var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, Brushes.White)
                {
                    TextAlignment = TextAlignment.Center, MaxTextWidth = Math.Max(size * 4, w * (look == "Bold" ? 0.8 : 0.85) - 2 * padX),
                };
                double bw = ft.Width + 2 * padX, bh = ft.Height + 2 * padY;
                return new Rect(o.At.X * w - bw / 2, o.At.Y * h - bh, bw, bh);
            }
        }
    }

    private static void DrawText(DrawingContext dc, VideoOverlay o, int w, int h)
    {
        double size = Math.Clamp(h * 0.06, 14, 110);
        var ft = new FormattedText(o.Text.Length == 0 ? " " : o.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Inter"), FontStyle.Normal, FontWeight.Bold), size, Brushes.White) { TextAlignment = TextAlignment.Center, MaxTextWidth = w * 0.9 };
        var origin = new Point(o.At.X * w - ft.MaxTextWidth / 2, o.At.Y * h - ft.Height / 2);
        if (ft.BuildGeometry(origin) is { } g)
        {
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)), size * 0.14) { LineJoin = PenLineJoin.Round }, g);
            dc.DrawGeometry(Brushes.White, null, g);
        }
    }
}
