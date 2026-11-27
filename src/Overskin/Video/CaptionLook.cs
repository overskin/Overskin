using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Overskin;

/// <summary>
/// How subtitles look, and how they follow the voice word by word: each word lights up as it's
/// said, or the line builds up a word at a time, like the captions on TikTok and Shorts.
/// </summary>
public static class CaptionLook
{
    /// <summary>The looks: the name shown, and what it is.</summary>
    public static readonly (string Name, string Tip)[] Looks =
    {
        ("Classic", "White on a dark band, like TV subtitles"),
        ("Bold", "Big capitals with a thick black outline, like Shorts and TikTok"),
        ("Clean", "White words with a soft shadow, no band"),
        ("Boxed", "White on black, the word being said in a green box"),
    };

    /// <summary>How the words follow the voice.</summary>
    public static readonly (string Name, string Label, string Tip)[] WordModes =
    {
        ("Line", "Whole line", "Each line shows whole for as long as it's said"),
        ("Light", "Light up", "The word being said lights up"),
        ("Build", "Build up", "Words appear one at a time as they're said"),
    };

    /// <summary>Where each word of the text starts, and how long it is.</summary>
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

    /// <summary>
    /// When each word of a line is said: the line's time shared out by the words' lengths
    /// (longer words take longer to say), each starting where the last ended.
    /// </summary>
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

    /// <summary>Which word is being said at <paramref name="now"/> (−1 before the first).</summary>
    public static int WordAt(VideoOverlay caption, double now)
    {
        var times = WordTimes(caption.Text, caption.Start, caption.End);
        for (int i = times.Count - 1; i >= 0; i--)
            if (now >= times[i].Start) return i;
        return times.Count > 0 ? 0 : -1;
    }

    /// <summary>
    /// Subtitles split into one overlay per word for saving, each showing the line with that
    /// word lit (or the line up to that word), for the time the word is said.
    /// </summary>
    public static List<VideoOverlay> Expand(IEnumerable<VideoOverlay> overlays)
    {
        var result = new List<VideoOverlay>();
        foreach (var o in overlays)
        {
            if (o.Kind != VideoOverlay.Kinds.Caption || o.Words == "Line")
            {
                result.Add(o);
                continue;
            }
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

    /// <summary>Draws a subtitle line; its bounds, in video pixels.</summary>
    internal static Rect Draw(DrawingContext dc, VideoOverlay o, int videoHeight, int videoWidth)
    {
        string look = Looks.Any(l => l.Name == o.Look) ? o.Look : "Classic";
        bool caps = look == "Bold";
        string text = o.Text.Length == 0 ? " " : caps ? o.Text.ToUpper(CultureInfo.CurrentCulture) : o.Text;
        double size = look switch
        {
            "Bold" => Math.Clamp(videoHeight * 0.07, 18, 120),
            "Boxed" => Math.Clamp(videoHeight * 0.05, 15, 90),
            "Clean" => Math.Clamp(videoHeight * 0.05, 15, 90),
            _ => Math.Clamp(videoHeight * 0.042, 13, 72),
        };
        var family = look == "Bold" ? FontLibrary.Find("Anton").Family : new FontFamily("Segoe UI");
        var weight = look is "Bold" ? FontWeights.Normal : look == "Classic" ? FontWeights.SemiBold : FontWeights.Bold;
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal), size, Brushes.White, 1.0)
        { TextAlignment = TextAlignment.Center };
        double padX = size * 0.5, padY = size * 0.22;
        if (videoWidth > 0) ft.MaxTextWidth = Math.Max(size * 4, videoWidth * (look == "Bold" ? 0.8 : 0.85) - 2 * padX);
        double w = ft.Width + 2 * padX, h = ft.Height + 2 * padY;
        var bounds = new Rect(o.A.X - w / 2, o.A.Y - h, w, h);
        double textLeft = ft.MaxTextWidth > 0 && ft.MaxTextWidth < double.MaxValue ? o.A.X - ft.MaxTextWidth / 2 : bounds.X + padX;
        var origin = new Point(textLeft, bounds.Y + padY);

        var words = Words(text);
        int lit = o.Highlight;
        bool build = o.Words == "Build" && lit >= 0;
        bool light = lit >= 0 && lit < words.Count && o.Words != "Line";

        // the band behind the words
        if (look is "Classic" or "Boxed")
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(look == "Boxed" ? (byte)225 : (byte)190, 0, 0, 0)), null, bounds, size * 0.25, size * 0.25);

        // building up: only the words said so far, laid out where the whole line puts them
        if (build && lit < words.Count)
        {
            int shown = words[lit].Start + words[lit].Length;
            dc.PushClip(ft.BuildHighlightGeometry(origin, 0, shown) ?? Geometry.Empty);
        }
        // the word being said, in a box behind it
        if (light && look == "Boxed" && ft.BuildHighlightGeometry(origin, words[lit].Start, words[lit].Length) is { } box)
        {
            var r = box.Bounds;
            r.Inflate(size * 0.12, size * 0.02);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)), null, r, size * 0.18, size * 0.18);
        }

        void Words_(Brush fill)
        {
            switch (look)
            {
                case "Bold":
                {
                    var g = ft.BuildGeometry(origin);
                    dc.DrawGeometry(null, new Pen(Brushes.Black, size * 0.16) { LineJoin = PenLineJoin.Round }, g);
                    dc.DrawGeometry(fill, null, g);
                    break;
                }
                case "Clean":
                {
                    var g = ft.BuildGeometry(origin);
                    // a soft shadow: the words a few times over, faint and spread
                    for (int k = 3; k >= 1; k--)
                        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(46, 0, 0, 0)), size * 0.09 * k) { LineJoin = PenLineJoin.Round }, g);
                    dc.DrawGeometry(fill, null, g);
                    break;
                }
                default:
                    ft.SetForegroundBrush(fill);
                    dc.DrawText(ft, origin);
                    break;
            }
        }
        Words_(Brushes.White);
        // the word being said, in colour
        if (light && look != "Boxed" && ft.BuildHighlightGeometry(origin, words[lit].Start, words[lit].Length) is { } word)
        {
            dc.PushClip(word);
            Words_(new SolidColorBrush(look == "Classic" ? Color.FromRgb(0xFF, 0xD3, 0x4D) : Color.FromRgb(0xFF, 0xE8, 0x1A)));
            dc.Pop();
        }
        if (build && lit < words.Count) dc.Pop();
        if (look == "Bold") bounds.Inflate(size * 0.1, size * 0.1);
        return bounds;
    }
}
