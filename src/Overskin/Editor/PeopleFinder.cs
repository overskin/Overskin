using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;

namespace Overskin;

/// <summary>
/// Finds who is in a screenshot, for hiding: faces (with Windows' own face finder, on this PC),
/// and the names and profile pictures in chats such as Discord, Slack and Teams.
/// </summary>
public static partial class PeopleFinder
{
    // ---- faces -------------------------------------------------------------------------------

    /// <summary>
    /// Every face in the picture, a little bigger than the face itself so hair and chin are
    /// covered. Empty when there are none or this PC has no face finder.
    /// </summary>
    public static async Task<List<Rect>> FacesAsync(BitmapSource image)
    {
        var found = new List<Rect>();
        try
        {
            if (!Windows.Media.FaceAnalysis.FaceDetector.IsSupported) return found;
            var detector = await Windows.Media.FaceAnalysis.FaceDetector.CreateAsync();
            // small faces (profile pictures, a webcam in a corner) are found better enlarged
            double scale = Math.Clamp(1400.0 / Math.Max(image.PixelWidth, image.PixelHeight), 1, 2.5);
            using var gray = await GrayAsync(image, scale);
            detector.MinDetectableFaceSize = new Windows.Graphics.Imaging.BitmapSize { Width = 24, Height = 24 };
            var faces = await detector.DetectFacesAsync(gray);
            foreach (var f in faces)
            {
                var b = f.FaceBox;
                var r = new Rect(b.X / scale, b.Y / scale, b.Width / scale, b.Height / scale);
                // the box Windows gives is eyebrows to mouth; take in the forehead, hair and chin
                r = new Rect(r.X - r.Width * 0.18, r.Y - r.Height * 0.35, r.Width * 1.36, r.Height * 1.6);
                r.Intersect(new Rect(0, 0, image.PixelWidth, image.PixelHeight));
                if (!r.IsEmpty) found.Add(r);
            }
        }
        catch
        {
            // no face finder on this PC: nothing found
        }
        return found;
    }

    /// <summary>The picture, scaled, in the grey format the face finder reads.</summary>
    private static async Task<SoftwareBitmap> GrayAsync(BitmapSource image, double scale)
    {
        BitmapSource source = Math.Abs(scale - 1) > 0.01 ? new TransformedBitmap(image, new System.Windows.Media.ScaleTransform(scale, scale)) : image;
        using var memory = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
        encoder.Save(memory);
        memory.Position = 0;
        using var stream = memory.AsRandomAccessStream();
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
        using var colour = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        return SoftwareBitmap.Convert(colour, BitmapPixelFormat.Gray8);
    }

    // ---- names in chats ------------------------------------------------------------------------

    // a message's time beside the name: "Today at 3:45 PM", "Yesterday at 21:07", "11/24/2026 3:45 PM", "3:45 PM", "— 24/11/2026 09:12"
    [GeneratedRegex(@"^(?:[—–-]\s*)?(?:(?:today|yesterday)\s+at\s+|\d{1,2}[/.\-]\d{1,2}[/.\-]\d{2,4},?\s*)?\d{1,2}[:.]\d{2}(?:\s*[ap]\.?m\.?)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex TimePattern();

    [GeneratedRegex(@"^(?:[—–-]\s*)?(?:today|yesterday)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DayPattern();

    /// <summary>
    /// The names at the top of chat messages (a name with the message's time after it), each
    /// with the round profile picture to its left, and @mentions in the messages.
    /// </summary>
    public static List<(Rect Area, string Kind)> ChatNames(IReadOnlyList<IReadOnlyList<OcrWord>> lines, int width, int height)
    {
        var found = new List<(Rect, string)>();
        foreach (var raw in lines)
        {
            var line = raw.OrderBy(w => w.Bounds.Left).ToList();
            if (line.Count == 0) continue;

            // @mentions (not email addresses)
            foreach (var w in line)
                if (w.Text.Length > 2 && w.Text[0] == '@' && !w.Text[1..].Contains('@') && !w.Text[1..].Contains('.'))
                    found.Add((Pad(w.Bounds, 2), "name"));

            // a name followed by a time: the words before the time are the name
            for (int t = 1; t < line.Count && t <= 5; t++)
            {
                string rest = string.Join(" ", line.Skip(t).Take(5).Select(w => w.Text));
                if (!TimePattern().IsMatch(rest) && !DayPattern().IsMatch(rest)) continue;
                // the name sits at the start of the line, and the time follows close after
                var name = line.Take(t).ToList();
                if (name.Any(w => w.Text.Any(char.IsDigit) && w.Text.Any(c => c == ':'))) break;
                double gap = line[t].Bounds.Left - name[^1].Bounds.Right;
                double h = name.Max(w => w.Bounds.Height);
                if (gap > h * 6) break;
                var box = name.Select(w => w.Bounds).Aggregate(Rect.Union);
                found.Add((Pad(box, 2), "name"));
                // the profile picture: a square about two and a half lines high, a little to the left
                double s = Math.Max(h, 10) / 15.0;
                var face = new Rect(box.Left - 58 * s, box.Top - 6 * s, 46 * s, 46 * s);
                face.Intersect(new Rect(0, 0, width, height));
                if (!face.IsEmpty && face.Width > 10 * s && face.Height > 10 * s) found.Add((face, "profile picture"));
                break;
            }
        }
        return found;
    }

    private static Rect Pad(Rect r, double by)
    {
        r.Inflate(by, by);
        return r;
    }
}
