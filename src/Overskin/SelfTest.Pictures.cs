#if DEBUG
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Overskin;

/// <summary>
/// Self-test for finding pictures inside screenshots: a set of hard scenes (odd corners, shadows,
/// borders, low contrast, things right beside the picture, a picture inside a picture), each with
/// where the pictures really are, and things that must not be taken for pictures.
/// "Overskin.exe --selftest log --more --pictures".
/// </summary>
public partial class App
{
    private sealed record Scene(string Name, int W, int H, Action<DrawingContext> Draw, Rect[] Pictures, Point[] NotPictures);

    private static BitmapSource Photo(int w, int h, int seed, int brightness = 0, bool dark = false)
    {
        var rnd = new Random(seed);
        var bits = new byte[w * h * 4];
        // a few soft colour blobs over a gradient, with grain: like a photo
        var blobs = Enumerable.Range(0, 6).Select(_ => (X: rnd.NextDouble() * w, Y: rnd.NextDouble() * h, R: (0.2 + rnd.NextDouble() * 0.4) * Math.Max(w, h),
            C: (rnd.Next(256), rnd.Next(256), rnd.Next(256)))).ToArray();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double r = 90 + 80.0 * x / w, g = 110 + 60.0 * y / h, b = 150 - 50.0 * x / w;
                foreach (var bl in blobs)
                {
                    double d = Math.Exp(-((x - bl.X) * (x - bl.X) + (y - bl.Y) * (y - bl.Y)) / (bl.R * bl.R));
                    r += (bl.C.Item1 - r) * d * 0.7; g += (bl.C.Item2 - g) * d * 0.7; b += (bl.C.Item3 - b) * d * 0.7;
                }
                double k = dark ? 0.35 : 1;
                int i = (y * w + x) * 4;
                bits[i] = (byte)Math.Clamp(b * k + brightness + rnd.Next(-9, 10), 0, 255);
                bits[i + 1] = (byte)Math.Clamp(g * k + brightness + rnd.Next(-9, 10), 0, 255);
                bits[i + 2] = (byte)Math.Clamp(r * k + brightness + rnd.Next(-9, 10), 0, 255);
                bits[i + 3] = 255;
            }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bits, w * 4);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>A rectangle with its own radius at each corner (top-left, top-right, bottom-right, bottom-left).</summary>
    private static Geometry Corners(Rect r, double tl, double tr, double br, double bl)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(r.Left + tl, r.Top), true, true);
            c.LineTo(new Point(r.Right - tr, r.Top), true, false);
            if (tr > 0) c.ArcTo(new Point(r.Right, r.Top + tr), new Size(tr, tr), 0, false, SweepDirection.Clockwise, true, false);
            c.LineTo(new Point(r.Right, r.Bottom - br), true, false);
            if (br > 0) c.ArcTo(new Point(r.Right - br, r.Bottom), new Size(br, br), 0, false, SweepDirection.Clockwise, true, false);
            c.LineTo(new Point(r.Left + bl, r.Bottom), true, false);
            if (bl > 0) c.ArcTo(new Point(r.Left, r.Bottom - bl), new Size(bl, bl), 0, false, SweepDirection.Clockwise, true, false);
            c.LineTo(new Point(r.Left, r.Top + tl), true, false);
            if (tl > 0) c.ArcTo(new Point(r.Left + tl, r.Top), new Size(tl, tl), 0, false, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }

    /// <summary>A "continuous corner" (squircle) rectangle, as iOS and macOS draw them.</summary>
    private static Geometry Squircle(Rect r, double radius)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            const int n = 5; // superellipse exponent in the corners
            bool first = true;
            void Corner(double cx, double cy, double sx, double sy, double a0)
            {
                for (int i = 0; i <= 16; i++)
                {
                    double a = a0 + i / 16.0 * Math.PI / 2, ca = Math.Cos(a), sa = Math.Sin(a);
                    double x = cx + radius * 1.3 * Math.Sign(ca) * Math.Pow(Math.Abs(ca), 2.0 / n);
                    double y = cy + radius * 1.3 * Math.Sign(sa) * Math.Pow(Math.Abs(sa), 2.0 / n);
                    if (first) { c.BeginFigure(new Point(x, y), true, true); first = false; }
                    else c.LineTo(new Point(x, y), true, true);
                }
            }
            double k = radius * 1.3;
            Corner(r.Right - k, r.Bottom - k, 1, 1, 0);
            Corner(r.Left + k, r.Bottom - k, -1, 1, Math.PI / 2);
            Corner(r.Left + k, r.Top + k, -1, -1, Math.PI);
            Corner(r.Right - k, r.Top + k, 1, -1, Math.PI * 1.5);
        }
        g.Freeze();
        return g;
    }

    private static void Text(DrawingContext dc, string s, double x, double y, Brush b, double size = 15) =>
        dc.DrawText(new FormattedText(s, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, b, 1.0), new Point(x, y));

    private static void Clipped(DrawingContext dc, Geometry clip, ImageSource img, Rect r)
    {
        dc.PushClip(clip);
        dc.DrawImage(img, r);
        dc.Pop();
    }

    private static void Shadow(DrawingContext dc, Rect r, double radius, double blur, double dy, double strength)
    {
        for (int i = (int)blur; i >= 1; i--)
        {
            byte a = (byte)Math.Clamp(strength * 255 / blur * 0.6, 1, 255);
            var rr = new Rect(r.X - i, r.Y - i + dy, r.Width + 2 * i, r.Height + 2 * i);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(a, 0, 0, 0)), null, rr, radius + i, radius + i);
        }
    }

    private static Brush Hex(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

    private static List<Scene> PictureScenes()
    {
        var list = new List<Scene>();
        var chat = Hex("#313338");
        var light = Brushes.Gainsboro;

        // 1. a plain rounded picture in a dark chat
        {
            var p = new Rect(300, 120, 480, 320);
            list.Add(new Scene("chat rounded 8", 1100, 600, dc =>
            {
                dc.DrawRectangle(chat, null, new Rect(0, 0, 1100, 600));
                Text(dc, "Somebody posted a photo of the trip, look at this", 300, 80, light);
                Clipped(dc, new RectangleGeometry(p, 8, 8), Photo(480, 320, 1), p);
                Text(dc, "nice!! where is that", 300, 460, light);
            }, new[] { p }, new[] { new Point(400, 90), new Point(350, 470) }));
        }
        // 2. squircle corners (iOS / macOS style)
        {
            var p = new Rect(200, 100, 400, 300);
            list.Add(new Scene("squircle 22", 900, 520, dc =>
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 900, 520));
                Clipped(dc, Squircle(p, 22), Photo(400, 300, 2), p);
            }, new[] { p }, Array.Empty<Point>()));
        }
        // 3. mixed corners: a chat bubble picture with one small corner by the tail
        {
            var p = new Rect(260, 90, 420, 300);
            list.Add(new Scene("mixed corners 18/18/4/18", 900, 500, dc =>
            {
                dc.DrawRectangle(Hex("#ECE5DD"), null, new Rect(0, 0, 900, 500));
                Clipped(dc, Corners(p, 18, 18, 4, 18), Photo(420, 300, 3), p);
            }, new[] { p }, Array.Empty<Point>()));
        }
        // 4. a card: picture flush with the card's rounded top, square bottom, caption under it in the card
        {
            var card = new Rect(200, 80, 440, 420);
            var p = new Rect(200, 80, 440, 260);
            list.Add(new Scene("card top corners only", 900, 580, dc =>
            {
                dc.DrawRectangle(Hex("#F0F2F5"), null, new Rect(0, 0, 900, 580));
                dc.DrawRoundedRectangle(Brushes.White, new Pen(Hex("#DADDE1"), 1), card, 12, 12);
                Clipped(dc, Corners(p, 12, 12, 0, 0), Photo(440, 260, 4), p);
                Text(dc, "A headline about something interesting", 220, 360, Brushes.Black, 18);
                Text(dc, "example.com  ·  4 min read", 220, 400, Brushes.Gray, 13);
            }, new[] { p }, new[] { new Point(300, 370), new Point(260, 408) }));
        }
        // 5. a 1-pixel border, 16 px corners, on black (Twitter / X)
        {
            var p = new Rect(150, 120, 500, 340);
            list.Add(new Scene("border 1px r16 on black", 850, 560, dc =>
            {
                dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, 850, 560));
                Text(dc, "Look what I made this weekend", 150, 80, Brushes.WhiteSmoke, 16);
                Clipped(dc, new RectangleGeometry(p, 16, 16), Photo(500, 340, 5), p);
                dc.DrawRoundedRectangle(null, new Pen(Hex("#2F3336"), 1), new Rect(p.X + 0.5, p.Y + 0.5, p.Width - 1, p.Height - 1), 16, 16);
            }, new[] { p }, new[] { new Point(250, 90) }));
        }
        // 6. a soft drop shadow on a white page
        {
            var p = new Rect(220, 120, 420, 300);
            list.Add(new Scene("drop shadow r12", 880, 560, dc =>
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 880, 560));
                Shadow(dc, p, 12, 14, 4, 0.18);
                Clipped(dc, new RectangleGeometry(p, 12, 12), Photo(420, 300, 6), p);
            }, new[] { p }, Array.Empty<Point>()));
        }
        // 7. a bright sky along the top edge, on a white page (weak top edge)
        {
            var p = new Rect(200, 100, 460, 300);
            list.Add(new Scene("bright top on white", 860, 500, dc =>
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 860, 500));
                var photo = Photo(460, 300, 7);
                Clipped(dc, new RectangleGeometry(p, 6, 6), photo, p);
                // a hazy, near-white sky fading into the photo
                var sky = new LinearGradientBrush(Color.FromRgb(236, 240, 246), Color.FromArgb(0, 236, 240, 246), 90);
                dc.PushClip(new RectangleGeometry(p, 6, 6));
                dc.DrawRectangle(sky, null, new Rect(p.X, p.Y, p.Width, 120));
                dc.Pop();
            }, new[] { p }, Array.Empty<Point>()));
        }
        // 8. a dark night photo in a dark chat
        {
            var p = new Rect(260, 90, 480, 320);
            list.Add(new Scene("dark photo on dark", 1000, 520, dc =>
            {
                dc.DrawRectangle(chat, null, new Rect(0, 0, 1000, 520));
                Clipped(dc, new RectangleGeometry(p, 8, 8), Photo(480, 320, 8, dark: true), p);
            }, new[] { p }, Array.Empty<Point>()));
        }
        // 9. crowded: avatar right beside it, name above, reactions just below, text to the right
        {
            var p = new Rect(260, 110, 380, 260);
            list.Add(new Scene("crowded chat", 1000, 520, dc =>
            {
                dc.DrawRectangle(chat, null, new Rect(0, 0, 1000, 520));
                dc.DrawEllipse(Hex("#5865F2"), null, new Point(225, 98), 20, 20);
                Text(dc, "finn  Today at 4:12 PM", 260, 84, Brushes.White, 15);
                Clipped(dc, new RectangleGeometry(p, 8, 8), Photo(380, 260, 9), p);
                dc.DrawRoundedRectangle(Hex("#2B2D31"), new Pen(Hex("#5865F2"), 1), new Rect(260, 376, 52, 26), 8, 8);
                Text(dc, "😂 3", 270, 379, Brushes.White, 13);
                Text(dc, "edited", 648, 350, Brushes.Gray, 11);
            }, new[] { p }, new[] { new Point(300, 92), new Point(285, 389) }));
        }
        // 10. on a gradient wallpaper
        {
            var p = new Rect(250, 140, 420, 280);
            list.Add(new Scene("on gradient", 920, 560, dc =>
            {
                dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(70, 40, 120), Color.FromRgb(230, 120, 90), 35), null, new Rect(0, 0, 920, 560));
                Clipped(dc, new RectangleGeometry(p, 10, 10), Photo(420, 280, 10), p);
            }, new[] { p }, Array.Empty<Point>()));
        }
        // 11. big round corners, bigger than before (radius 56)
        {
            var p = new Rect(180, 80, 520, 380);
            list.Add(new Scene("big radius 56", 900, 540, dc =>
            {
                dc.DrawRectangle(Hex("#1E1F22"), null, new Rect(0, 0, 900, 540));
                Clipped(dc, new RectangleGeometry(p, 56, 56), Photo(520, 380, 11), p);
            }, new[] { p }, Array.Empty<Point>()));
        }
        // 12. placed off the pixel grid and scaled: soft edges everywhere
        {
            var p = new Rect(201.4, 99.6, 401.3, 287.7);
            list.Add(new Scene("fractional r10.5", 820, 480, dc =>
            {
                dc.DrawRectangle(Hex("#FAFAFA"), null, new Rect(0, 0, 820, 480));
                Clipped(dc, new RectangleGeometry(p, 10.5, 10.5), Photo(300, 215, 12), p);
            }, new[] { p }, Array.Empty<Point>()));
        }
        // 13. a gallery: four pictures 4 px apart
        {
            var ps = new[] { new Rect(150, 100, 300, 200), new Rect(454, 100, 300, 200), new Rect(150, 304, 300, 200), new Rect(454, 304, 300, 200) };
            list.Add(new Scene("gallery 4px gaps", 900, 600, dc =>
            {
                dc.DrawRectangle(chat, null, new Rect(0, 0, 900, 600));
                dc.PushClip(new RectangleGeometry(new Rect(150, 100, 604, 404), 8, 8));
                for (int i = 0; i < 4; i++) dc.DrawImage(Photo(300, 200, 20 + i), ps[i]);
                dc.Pop();
            }, ps, Array.Empty<Point>()));
        }
        // 14. a picture inside a picture: a screenshot of a web page with a photo on it, posted in a chat
        {
            var outer = new Rect(150, 80, 640, 420);
            var inner = new Rect(330, 190, 280, 180);
            list.Add(new Scene("picture in a picture", 940, 580, dc =>
            {
                dc.DrawRectangle(chat, null, new Rect(0, 0, 940, 580));
                dc.PushClip(new RectangleGeometry(outer, 8, 8));
                dc.DrawRectangle(Brushes.White, null, outer);
                dc.DrawRectangle(Hex("#DEE1E6"), null, new Rect(outer.X, outer.Y, outer.Width, 34)); // browser bar
                Text(dc, "https://example.com/gallery", outer.X + 60, outer.Y + 8, Brushes.DimGray, 13);
                Text(dc, "Our favourite places", outer.X + 180, outer.Y + 60, Brushes.Black, 24);
                Clipped(dc, new RectangleGeometry(inner, 12, 12), Photo(280, 180, 14), inner);
                Text(dc, "Taken last summer by the lake, early in the morning.", outer.X + 130, outer.Y + 310, Brushes.Gray, 13);
                dc.Pop();
            }, new[] { outer, inner }, Array.Empty<Point>()));
        }
        // 15. a video thumbnail with a play button and a duration badge in its corner
        {
            var p = new Rect(200, 90, 480, 270);
            list.Add(new Scene("video thumbnail", 880, 480, dc =>
            {
                dc.DrawRectangle(Hex("#0F0F0F"), null, new Rect(0, 0, 880, 480));
                Clipped(dc, new RectangleGeometry(p, 12, 12), Photo(480, 270, 15), p);
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)), null, new Point(440, 225), 34, 34);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(220, 0, 0, 0)), null, new Rect(p.Right - 54, p.Bottom - 28, 46, 20), 4, 4);
                Text(dc, "12:34", p.Right - 50, p.Bottom - 27, Brushes.White, 12);
                Text(dc, "How I built a cabin in 30 days", 200, 372, Brushes.White, 16);
            }, new[] { p }, new[] { new Point(300, 382) }));
        }
        // 16. hover buttons sitting over the picture's top-right corner, sticking out
        {
            var p = new Rect(260, 120, 420, 300);
            list.Add(new Scene("toolbar over corner", 900, 520, dc =>
            {
                dc.DrawRectangle(chat, null, new Rect(0, 0, 900, 520));
                Clipped(dc, new RectangleGeometry(p, 8, 8), Photo(420, 300, 16), p);
                dc.DrawRoundedRectangle(Hex("#2B2D31"), new Pen(Hex("#1E1F22"), 1), new Rect(p.Right - 110, p.Top - 16, 124, 32), 6, 6);
                Text(dc, "😀  ↩  ⋯", p.Right - 98, p.Top - 11, Brushes.White, 14);
            }, new[] { p }, Array.Empty<Point>()));
        }
        // 17. square corners, flat cartoon colours (a meme / illustration), light page
        {
            var p = new Rect(220, 100, 400, 360);
            list.Add(new Scene("flat illustration square", 860, 540, dc =>
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 860, 540));
                dc.DrawRectangle(Brushes.White, null, new Rect(p.X, p.Y, p.Width, 80));
                Text(dc, "me after one coffee", p.X + 80, p.Y + 22, Brushes.Black, 26);
                dc.DrawRectangle(Hex("#78BEF0"), null, new Rect(p.X, p.Y + 80, p.Width, 180));
                dc.DrawRectangle(Hex("#50AA46"), null, new Rect(p.X, p.Y + 260, p.Width, 100));
                dc.DrawEllipse(Hex("#FAD23C"), null, new Point(p.X + 300, p.Y + 150), 34, 34);
                dc.DrawRectangle(null, new Pen(Hex("#E3E3E3"), 1), new Rect(p.X + 0.5, p.Y + 0.5, p.Width - 1, p.Height - 1));
            }, new[] { p }, Array.Empty<Point>()));
        }
        // 18. not pictures: a code block, buttons, an input box, a sidebar, a paragraph
        {
            list.Add(new Scene("no pictures (UI)", 1000, 600, dc =>
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 1000, 600));
                dc.DrawRectangle(Hex("#F6F8FA"), null, new Rect(0, 0, 220, 600));
                for (int i = 0; i < 8; i++) Text(dc, "Menu item " + i, 24, 30 + i * 36, Brushes.DimGray);
                dc.DrawRoundedRectangle(Hex("#0D1117"), null, new Rect(280, 40, 600, 200), 6, 6);
                string[] code = { "public static void Main()", "{", "    var x = Compute(42);", "    Console.WriteLine($\"{x}\");", "}" };
                var cols = new[] { Hex("#FF7B72"), Hex("#D2A8FF"), Hex("#79C0FF"), Hex("#A5D6FF"), Hex("#E6EDF3") };
                for (int i = 0; i < code.Length; i++) Text(dc, code[i], 300, 60 + i * 30, cols[i], 16);
                dc.DrawRoundedRectangle(Hex("#2DA44E"), null, new Rect(280, 270, 140, 40), 6, 6);
                Text(dc, "Save changes", 300, 279, Brushes.White);
                dc.DrawRoundedRectangle(Brushes.White, new Pen(Hex("#D0D7DE"), 1), new Rect(440, 270, 440, 40), 6, 6);
                Text(dc, "Search…", 455, 279, Brushes.Gray);
                for (int i = 0; i < 6; i++) Text(dc, "This is a paragraph of ordinary text that goes on for a while, line " + i, 280, 340 + i * 26, Brushes.Black);
            }, Array.Empty<Rect>(), new[] { new Point(500, 120), new Point(330, 290), new Point(600, 290), new Point(100, 300), new Point(500, 400) }));
        }
        // 19. a picture with a caption bar overlaid along its bottom, partly transparent
        {
            var p = new Rect(200, 90, 460, 320);
            list.Add(new Scene("overlay caption bar", 860, 500, dc =>
            {
                dc.DrawRectangle(Hex("#18191A"), null, new Rect(0, 0, 860, 500));
                Clipped(dc, new RectangleGeometry(p, 10, 10), Photo(460, 320, 19), p);
                dc.PushClip(new RectangleGeometry(p, 10, 10));
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), null, new Rect(p.X, p.Bottom - 50, p.Width, 50));
                dc.Pop();
                Text(dc, "Sunset over the hills", p.X + 16, p.Bottom - 38, Brushes.White, 16);
            }, new[] { p }, Array.Empty<Point>()));
        }
        // 20. a circle avatar picture and a pill-shaped picture
        {
            var c = new Rect(200, 120, 160, 160);
            var pill = new Rect(450, 150, 300, 100);
            list.Add(new Scene("circle and pill", 900, 420, dc =>
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 900, 420));
                Clipped(dc, new EllipseGeometry(c), Photo(160, 160, 30), c);
                Clipped(dc, new RectangleGeometry(pill, 50, 50), Photo(300, 100, 31), pill);
            }, new[] { c, pill }, Array.Empty<Point>()));
        }
        // 21. small corner radius but asymmetric anti-aliasing: picture 1 px inside a 1 px light frame, then page
        {
            var p = new Rect(240, 110, 400, 280);
            list.Add(new Scene("white frame (polaroid)", 880, 520, dc =>
            {
                dc.DrawRectangle(Hex("#3A3B3C"), null, new Rect(0, 0, 880, 520));
                dc.DrawRectangle(Brushes.White, null, new Rect(p.X - 14, p.Y - 14, p.Width + 28, p.Height + 60));
                dc.DrawImage(Photo(400, 280, 21), p);
                Text(dc, "summer '24", p.X + 140, p.Bottom + 12, Brushes.DimGray, 18);
            }, new[] { p, new Rect(p.X - 14, p.Y - 14, p.Width + 28, p.Height + 60) }, Array.Empty<Point>()));
        }
        // 22. a screenshot of an app (light, no photo in it) posted in a dark chat
        {
            var p = new Rect(220, 100, 560, 360);
            list.Add(new Scene("posted app screenshot", 1000, 560, dc =>
            {
                dc.DrawRectangle(chat, null, new Rect(0, 0, 1000, 560));
                Text(dc, "why does it do this??", 220, 64, light);
                dc.PushClip(new RectangleGeometry(p, 8, 8));
                dc.DrawRectangle(Brushes.White, null, p);
                dc.DrawRectangle(Hex("#F3F3F3"), null, new Rect(p.X, p.Y, p.Width, 30));
                Text(dc, "Settings", p.X + 12, p.Y + 6, Brushes.Black, 13);
                dc.DrawRectangle(Hex("#EBEBEB"), null, new Rect(p.X, p.Y + 30, 140, p.Height - 30));
                for (int i = 0; i < 7; i++) Text(dc, new[] { "System", "Bluetooth", "Network", "Personalise", "Apps", "Accounts", "Time" }[i], p.X + 16, p.Y + 46 + i * 30, Brushes.DimGray, 13);
                dc.DrawRoundedRectangle(Hex("#DCE9F7"), null, new Rect(p.X + 6, p.Y + 40, 128, 26), 4, 4);
                Text(dc, "Display", p.X + 160, p.Y + 44, Brushes.Black, 22);
                for (int i = 0; i < 5; i++)
                {
                    dc.DrawRoundedRectangle(Hex("#FBFBFB"), new Pen(Hex("#E5E5E5"), 1), new Rect(p.X + 160, p.Y + 90 + i * 52, 380, 44), 4, 4);
                    Text(dc, "Option number " + (i + 1), p.X + 176, p.Y + 102 + i * 52, Brushes.Black, 13);
                    dc.DrawRoundedRectangle(Hex("#0067C0"), null, new Rect(p.X + 490, p.Y + 104 + i * 52, 36, 18), 9, 9);
                }
                dc.Pop();
            }, new[] { p }, new[] { new Point(260, 74) }));
        }
        // 23. a link embed in a chat (a panel, not a picture), with a small thumbnail in it
        {
            var embed = new Rect(220, 100, 520, 140);
            var thumb = new Rect(640, 116, 80, 80);
            list.Add(new Scene("link embed", 900, 340, dc =>
            {
                dc.DrawRectangle(chat, null, new Rect(0, 0, 900, 340));
                dc.DrawRoundedRectangle(Hex("#2B2D31"), null, embed, 4, 4);
                dc.DrawRectangle(Hex("#5865F2"), null, new Rect(embed.X, embed.Y, 4, embed.Height));
                Text(dc, "GitHub", 236, 114, Brushes.Gray, 12);
                Text(dc, "overskin/Overskin: Screenshots for Windows", 236, 134, Hex("#00A8FC"), 16);
                Text(dc, "A free, open-source screenshot and screen-recording", 236, 164, light, 14);
                Text(dc, "tool for Windows.", 236, 186, light, 14);
                Clipped(dc, new RectangleGeometry(thumb, 4, 4), Photo(80, 80, 23), thumb);
            }, new[] { thumb }, new[] { new Point(300, 200), new Point(260, 120) }));
        }
        // 24. on a noisy acrylic background (the Photos app, a frosted panel)
        {
            var p = new Rect(200, 90, 440, 300);
            list.Add(new Scene("noisy acrylic", 840, 480, dc =>
            {
                var rnd = new Random(24);
                var bg = new byte[840 * 480 * 4];
                for (int i = 0; i < bg.Length; i += 4) { int v = 40 + rnd.Next(-3, 4); bg[i] = (byte)(v + 6); bg[i + 1] = (byte)v; bg[i + 2] = (byte)(v + 2); bg[i + 3] = 255; }
                dc.DrawImage(BitmapSource.Create(840, 480, 96, 96, PixelFormats.Bgra32, null, bg, 840 * 4), new Rect(0, 0, 840, 480));
                Clipped(dc, new RectangleGeometry(p, 4, 4), Photo(440, 300, 24), p);
            }, new[] { p }, Array.Empty<Point>()));
        }
        // 25. right under a coloured header bar, no gap (a hero image on a web page)
        {
            var p = new Rect(0, 56, 900, 300);
            var q = new Rect(120, 400, 300, 180);
            list.Add(new Scene("hero under header", 900, 620, dc =>
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 900, 620));
                dc.DrawRectangle(Hex("#1F2937"), null, new Rect(0, 0, 900, 56));
                Text(dc, "Brand     Home     Blog     About", 24, 16, Brushes.White, 16);
                dc.DrawImage(Photo(900, 300, 25), new Rect(40, 56, 820, 300));
                Clipped(dc, new RectangleGeometry(q, 8, 8), Photo(300, 180, 26), q);
            }, new[] { new Rect(40, 56, 820, 300), q }, Array.Empty<Point>()));
        }
        // 26. small thumbnails in a grid (a file picker, an emoji / sticker picker)
        {
            var ps = new List<Rect>();
            for (int r = 0; r < 3; r++) for (int c = 0; c < 6; c++) ps.Add(new Rect(60 + c * 72, 60 + r * 72, 64, 64));
            list.Add(new Scene("thumbnail grid 64px", 540, 300, dc =>
            {
                dc.DrawRectangle(Hex("#202020"), null, new Rect(0, 0, 540, 300));
                for (int i = 0; i < ps.Count; i++) Clipped(dc, new RectangleGeometry(ps[i], 4, 4), Photo(64, 64, 40 + i), ps[i]);
            }, ps.ToArray(), Array.Empty<Point>()));
        }
        // 27. a masonry row: pictures of different heights, tops aligned, 6 px apart
        {
            var ps = new[] { new Rect(60, 60, 220, 300), new Rect(286, 60, 260, 180), new Rect(552, 60, 200, 240) };
            list.Add(new Scene("masonry", 820, 420, dc =>
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 820, 420));
                for (int i = 0; i < ps.Length; i++) Clipped(dc, new RectangleGeometry(ps[i], 16, 16), Photo((int)ps[i].Width, (int)ps[i].Height, 60 + i), ps[i]);
            }, ps, Array.Empty<Point>()));
        }
        // 28. a dark-mode screenshot posted in a dark chat: the edge is only a few shades off
        {
            var p = new Rect(200, 90, 560, 340);
            list.Add(new Scene("dark screenshot on dark", 960, 520, dc =>
            {
                dc.DrawRectangle(chat, null, new Rect(0, 0, 960, 520));
                dc.PushClip(new RectangleGeometry(p, 8, 8));
                dc.DrawRectangle(Hex("#1E1E1E"), null, p);
                dc.DrawRectangle(Hex("#252526"), null, new Rect(p.X, p.Y, 150, p.Height));
                dc.DrawRectangle(Hex("#3C3C3C"), null, new Rect(p.X, p.Y, p.Width, 28)); // VS Code's title bar
                string[] code = { "function hello() {", "  const name = 'world';", "  console.log(`hi ${name}`);", "}", "", "hello();" };
                var cols = new[] { Hex("#569CD6"), Hex("#9CDCFE"), Hex("#DCDCAA"), Hex("#D4D4D4"), Hex("#D4D4D4"), Hex("#DCDCAA") };
                for (int i = 0; i < code.Length; i++) Text(dc, code[i], p.X + 170, p.Y + 44 + i * 24, cols[i], 14);
                for (int i = 0; i < 6; i++) Text(dc, "file" + i + ".js", p.X + 14, p.Y + 40 + i * 22, Brushes.Silver, 12);
                dc.Pop();
            }, new[] { p }, Array.Empty<Point>()));
        }
        // 29. a light-mode chat with a photo of a white wall: very little edge anywhere
        {
            var p = new Rect(220, 100, 420, 300);
            list.Add(new Scene("white photo on white", 860, 500, dc =>
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 860, 500));
                Clipped(dc, new RectangleGeometry(p, 8, 8), Photo(420, 300, 29, brightness: 95), p);
            }, new[] { p }, Array.Empty<Point>()));
        }
        // 30. a JPEG-compressed screenshot of a page with a photo on it, posted in a chat
        {
            var outer = new Rect(150, 80, 600, 400);
            var inner = new Rect(300, 190, 300, 190);
            BitmapSource Jpeg()
            {
                var v = new DrawingVisual();
                using (var d = v.RenderOpen())
                {
                    d.DrawRectangle(Brushes.White, null, new Rect(0, 0, 600, 400));
                    Text(d, "Weekend photos", 200, 40, Brushes.Black, 26);
                    Clipped(d, new RectangleGeometry(new Rect(150, 110, 300, 190), 10, 10), Photo(300, 190, 30), new Rect(150, 110, 300, 190));
                    Text(d, "More pictures from the trip below.", 170, 320, Brushes.DimGray, 14);
                }
                var r = new RenderTargetBitmap(600, 400, 96, 96, PixelFormats.Pbgra32);
                r.Render(v);
                var enc = new JpegBitmapEncoder { QualityLevel = 55 };
                enc.Frames.Add(BitmapFrame.Create(r));
                var ms = new System.IO.MemoryStream();
                enc.Save(ms);
                ms.Position = 0;
                var dec = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                dec.Freeze();
                return dec;
            }
            var jpeg = Jpeg();
            list.Add(new Scene("jpeg screenshot", 900, 560, dc =>
            {
                dc.DrawRectangle(chat, null, new Rect(0, 0, 900, 560));
                Clipped(dc, new RectangleGeometry(outer, 8, 8), jpeg, outer);
            }, new[] { outer, inner }, Array.Empty<Point>()));
        }
        // 31. a spreadsheet: a grid of cells, some filled with colour
        {
            list.Add(new Scene("spreadsheet", 900, 520, dc =>
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 900, 520));
                var grid = new Pen(Hex("#E1E1E1"), 1);
                for (int c = 0; c <= 9; c++) dc.DrawLine(grid, new Point(40.5 + c * 90, 40), new Point(40.5 + c * 90, 500));
                for (int r = 0; r <= 20; r++) dc.DrawLine(grid, new Point(40, 40.5 + r * 23), new Point(850, 40.5 + r * 23));
                dc.DrawRectangle(Hex("#C6EFCE"), null, new Rect(221, 87, 179, 68));
                dc.DrawRectangle(Hex("#FFEB9C"), null, new Rect(491, 179, 89, 91));
                var rnd = new Random(31);
                for (int r = 0; r < 19; r++) for (int c = 0; c < 9; c++)
                    if (rnd.Next(3) > 0) Text(dc, rnd.Next(10, 9999).ToString(), 48 + c * 90, 43 + r * 23, Brushes.Black, 13);
            }, Array.Empty<Rect>(), new[] { new Point(300, 120), new Point(530, 220), new Point(100, 300) }));
        }
        // 32. image search results: square corners, 2 px apart, different widths, captions under them
        {
            var ps = new[] { new Rect(40, 60, 240, 180), new Rect(282, 60, 150, 180), new Rect(434, 60, 300, 180), new Rect(40, 270, 200, 160), new Rect(242, 270, 260, 160), new Rect(504, 270, 230, 160) };
            list.Add(new Scene("image search grid", 780, 470, dc =>
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 780, 470));
                for (int i = 0; i < ps.Length; i++)
                {
                    dc.DrawImage(Photo((int)ps[i].Width, (int)ps[i].Height, 70 + i), ps[i]);
                    Text(dc, "site" + i + ".com", ps[i].X + 2, ps[i].Bottom + 4, Brushes.DimGray, 11);
                }
            }, ps, Array.Empty<Point>()));
        }
        // 33. the desktop: a wallpaper photo, a window on it, and a photo inside the window
        {
            var win = new Rect(160, 90, 560, 380);
            var photo = new Rect(200, 170, 300, 200);
            list.Add(new Scene("desktop window", 960, 600, dc =>
            {
                dc.DrawImage(Photo(960, 600, 33), new Rect(0, 0, 960, 600));
                dc.DrawRoundedRectangle(Brushes.White, new Pen(Hex("#80808080"), 1), win, 8, 8);
                dc.DrawRectangle(Hex("#F3F3F3"), null, new Rect(win.X + 1, win.Y + 1, win.Width - 2, 32));
                Text(dc, "Photos", win.X + 12, win.Y + 8, Brushes.Black, 13);
                Clipped(dc, new RectangleGeometry(photo, 6, 6), Photo(300, 200, 34), photo);
                Text(dc, "IMG_2041.jpg  ·  4032 × 3024", 520, 180, Brushes.DimGray, 12);
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(230, 32, 32, 32)), null, new Rect(0, 552, 960, 48)); // taskbar
            }, new[] { photo }, new[] { new Point(480, 576) })); // the window itself may be snapped to: it's the window's own outline
        }
        // 34. a calendar: flat coloured event blocks with text, rounded
        {
            list.Add(new Scene("calendar", 900, 560, dc =>
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 900, 560));
                var line = new Pen(Hex("#E0E0E0"), 1);
                for (int d = 0; d <= 5; d++) dc.DrawLine(line, new Point(80.5 + d * 160, 20), new Point(80.5 + d * 160, 540));
                for (int hr = 0; hr <= 10; hr++) dc.DrawLine(line, new Point(80, 20.5 + hr * 52), new Point(880, 20.5 + hr * 52));
                var events = new[] { (0, 1, 2, "#039BE5"), (1, 3, 1, "#33B679"), (2, 0, 3, "#8E24AA"), (3, 5, 2, "#F4511E"), (4, 2, 1, "#F6BF26") };
                foreach (var (day, start, len, col) in events)
                {
                    var r = new Rect(84 + day * 160, 23 + start * 52, 150, len * 52 - 6);
                    dc.DrawRoundedRectangle(Hex(col), null, r, 4, 4);
                    Text(dc, "Meeting with the team", r.X + 6, r.Y + 4, Brushes.White, 12);
                    Text(dc, "10:00 – 11:00", r.X + 6, r.Y + 20, Brushes.White, 11);
                }
            }, Array.Empty<Rect>(), new[] { new Point(160, 100), new Point(400, 220), new Point(400, 80), new Point(560, 330) }));
        }
        // 35. a video grid: 16:9 thumbnails with round corners, titles and channel icons under them
        {
            var ps = new[] { new Rect(40, 40, 320, 180), new Rect(376, 40, 320, 180), new Rect(40, 310, 320, 180), new Rect(376, 310, 320, 180) };
            list.Add(new Scene("video grid", 740, 560, dc =>
            {
                dc.DrawRectangle(Hex("#0F0F0F"), null, new Rect(0, 0, 740, 560));
                for (int i = 0; i < ps.Length; i++)
                {
                    Clipped(dc, new RectangleGeometry(ps[i], 12, 12), Photo(320, 180, 80 + i), ps[i]);
                    dc.DrawEllipse(Hex("#FF5722"), null, new Point(ps[i].X + 18, ps[i].Bottom + 26), 18, 18);
                    Text(dc, "A video title that goes on", ps[i].X + 46, ps[i].Bottom + 12, Brushes.White, 14);
                    Text(dc, "Channel name · 1.2M views", ps[i].X + 46, ps[i].Bottom + 34, Brushes.Gray, 12);
                }
            }, ps, new[] { new Point(140, 238), new Point(476, 508) }));
        }
        // 36. cut (chamfered) corners
        {
            var p = new Rect(200, 100, 420, 280);
            list.Add(new Scene("chamfered corners", 820, 480, dc =>
            {
                dc.DrawRectangle(Hex("#202124"), null, new Rect(0, 0, 820, 480));
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    double k = 14;
                    c.BeginFigure(new Point(p.Left + k, p.Top), true, true);
                    c.LineTo(new Point(p.Right - k, p.Top), true, false); c.LineTo(new Point(p.Right, p.Top + k), true, false);
                    c.LineTo(new Point(p.Right, p.Bottom - k), true, false); c.LineTo(new Point(p.Right - k, p.Bottom), true, false);
                    c.LineTo(new Point(p.Left + k, p.Bottom), true, false); c.LineTo(new Point(p.Left, p.Bottom - k), true, false);
                    c.LineTo(new Point(p.Left, p.Top + k), true, false);
                }
                g.Freeze();
                Clipped(dc, g, Photo(420, 280, 36), p);
            }, new[] { p }, Array.Empty<Point>()));
        }
        // 37. stepped pixel corners, not smoothed (an old app, or zoomed with nearest-neighbour)
        {
            var p = new Rect(180, 90, 400, 300);
            list.Add(new Scene("stepped corners", 780, 480, dc =>
            {
                dc.DrawRectangle(Hex("#ECECEC"), null, new Rect(0, 0, 780, 480));
                var g = new GeometryGroup { FillRule = FillRule.Nonzero };
                int r = 12;
                g.Children.Add(new RectangleGeometry(new Rect(p.X + r, p.Y, p.Width - 2 * r, p.Height)));
                g.Children.Add(new RectangleGeometry(new Rect(p.X, p.Y + r, p.Width, p.Height - 2 * r)));
                for (int i = 0; i < r; i++)
                {
                    int inset = (int)Math.Ceiling(r - Math.Sqrt(r * r - (r - i - 0.5) * (r - i - 0.5)));
                    g.Children.Add(new RectangleGeometry(new Rect(p.X + inset, p.Y + i, p.Width - 2 * inset, 1)));
                    g.Children.Add(new RectangleGeometry(new Rect(p.X + inset, p.Bottom - i - 1, p.Width - 2 * inset, 1)));
                }
                g.Freeze();
                Clipped(dc, g, Photo(400, 300, 37), p); // whole pixels only: no smoothing at the steps
            }, new[] { p }, Array.Empty<Point>()));
        }
        // 38. oval corners: wider than tall
        {
            var p = new Rect(220, 110, 420, 280);
            list.Add(new Scene("elliptical corners 30x12", 860, 500, dc =>
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 860, 500));
                Clipped(dc, new RectangleGeometry(p, 30, 12), Photo(420, 280, 38), p);
            }, new[] { p }, Array.Empty<Point>()));
        }
        // 39. three rounded corners and one square (a Telegram message picture)
        {
            var p = new Rect(260, 90, 380, 300);
            list.Add(new Scene("one square corner", 860, 480, dc =>
            {
                dc.DrawRectangle(Hex("#17212B"), null, new Rect(0, 0, 860, 480));
                Clipped(dc, Corners(p, 24, 24, 0, 24), Photo(380, 300, 39), p);
                Text(dc, "21:04 ✓✓", p.Right - 70, p.Bottom - 24, Brushes.White, 12);
            }, new[] { p }, Array.Empty<Point>()));
        }
        // 40. display scaling at 150%: off the pixel grid, corners 12 px at 1.5x
        {
            var p = new Rect(300.75, 150.25, 450.5, 300.75);
            list.Add(new Scene("150 percent scaling", 1000, 560, dc =>
            {
                dc.DrawRectangle(chat, null, new Rect(0, 0, 1000, 560));
                Text(dc, "look at this", 300, 110, light, 22);
                Clipped(dc, new RectangleGeometry(p, 12, 12), Photo(300, 200, 40), p);
            }, new[] { p }, Array.Empty<Point>()));
        }
        // 41. a faint inside outline on the picture (iOS / macOS photo style), on white
        {
            var p = new Rect(200, 100, 420, 300);
            list.Add(new Scene("inner hairline", 820, 500, dc =>
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 820, 500));
                Clipped(dc, new RectangleGeometry(p, 14, 14), Photo(420, 300, 41, brightness: 40), p);
                dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(26, 0, 0, 0)), 1), new Rect(p.X + 0.5, p.Y + 0.5, p.Width - 1, p.Height - 1), 13.5, 13.5);
            }, new[] { p }, Array.Empty<Point>()));
        }
        // 42. a PNG with its own rounded corners and soft shadow, on a light grey page
        {
            var p = new Rect(240, 120, 400, 260);
            list.Add(new Scene("png with own shadow", 880, 520, dc =>
            {
                dc.DrawRectangle(Hex("#F5F5F7"), null, new Rect(0, 0, 880, 520));
                Shadow(dc, p, 18, 24, 10, 0.12);
                Clipped(dc, new RectangleGeometry(p, 18, 18), Photo(400, 260, 42), p);
            }, new[] { p }, Array.Empty<Point>()));
        }
        // 43. tiny radius, fractional, on a mid-grey page (Slack in light mode)
        {
            var p = new Rect(150.5, 90.5, 360, 240);
            list.Add(new Scene("small radius fractional", 700, 420, dc =>
            {
                dc.DrawRectangle(Hex("#F8F8F8"), null, new Rect(0, 0, 700, 420));
                Text(dc, "Here's the mock-up", 150, 60, Brushes.Black, 15);
                Clipped(dc, new RectangleGeometry(p, 2.5, 2.5), Photo(360, 240, 43), p);
                dc.DrawRoundedRectangle(null, new Pen(Hex("#DDDDDD"), 1), p, 2.5, 2.5);
            }, new[] { p }, Array.Empty<Point>()));
        }
        return list;
    }

    private static void PictureTests(List<string> log)
    {
        string? dir = Environment.GetEnvironmentVariable("OVERSKIN_TEST_OUT");
        int hit = 0, total = 0, wrong = 0, falseChecks = 0;
        long ms = 0;
        foreach (var scene in PictureScenes())
        {
            try
            {
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen()) scene.Draw(dc);
                var rtb = new RenderTargetBitmap(scene.W, scene.H, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(visual);
                var px = new byte[scene.W * scene.H * 4];
                rtb.CopyPixels(px, scene.W * 4, 0);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var found = ImageFinder.Find(px, scene.W, scene.H);
                ms += sw.ElapsedMilliseconds;

                var parts = new List<string>();
                foreach (var want in scene.Pictures)
                {
                    total++;
                    // what hovering inside it snaps to: a point well inside, away from anything nested
                    var spot = new Point(want.X + Math.Min(30, want.Width / 4), want.Y + want.Height - Math.Min(30, want.Height / 4));
                    var got = ImageFinder.At(found, (int)spot.X, (int)spot.Y);
                    bool ok = got is { } g && Math.Abs(g.Bounds.X - want.X) <= 3 && Math.Abs(g.Bounds.Y - want.Y) <= 3
                              && Math.Abs(g.Bounds.X + g.Bounds.Width - want.Right) <= 3 && Math.Abs(g.Bounds.Y + g.Bounds.Height - want.Bottom) <= 3;
                    if (ok) hit++;
                    parts.Add((ok ? "ok " : "MISS ") + $"{want.X:0},{want.Y:0} {want.Width:0}x{want.Height:0}" +
                              (ok ? "" : $" got {(got is { } g2 ? $"{g2.Bounds.X},{g2.Bounds.Y} {g2.Bounds.Width}x{g2.Bounds.Height}" : "nothing")}"));
                    if (!ok && Environment.GetEnvironmentVariable("OVERSKIN_EDGES") == "1")
                        parts.Add("edges " + ImageFinder.EdgesNear(px, scene.W, scene.H, new Int32Rect((int)want.X, (int)want.Y, (int)want.Width, (int)want.Height)));
                }
                foreach (var not in scene.NotPictures)
                {
                    falseChecks++;
                    var got = ImageFinder.At(found, (int)not.X, (int)not.Y);
                    bool inExpected = scene.Pictures.Any(p => p.Contains(not));
                    if (got != null && !inExpected) { wrong++; parts.Add($"WRONG at {not.X:0},{not.Y:0}: {got.Value.Bounds.X},{got.Value.Bounds.Y} {got.Value.Bounds.Width}x{got.Value.Bounds.Height}"); }
                }
                if (Environment.GetEnvironmentVariable("OVERSKIN_PROBE") is { Length: > 0 } probe && probe.Split(',') is { Length: 5 } pr && pr[0] == scene.Name.Split(' ')[0])
                    parts.Add("probe " + ImageFinder.EdgesNear(px, scene.W, scene.H, new Int32Rect(int.Parse(pr[1]), int.Parse(pr[2]), int.Parse(pr[3]), int.Parse(pr[4]))));
                if (ImageFinder.LastPlain.Count > 0) parts.Add("plain " + string.Join(" ", ImageFinder.LastPlain.Select(r => $"{r.X},{r.Y} {r.Width}x{r.Height}")));
                log.Add($"pictures / {scene.Name}: " + string.Join("; ", parts) + $"   (found {found.Count}: " +
                        string.Join(" ", found.Select(f => $"{f.Bounds.X},{f.Bounds.Y} {f.Bounds.Width}x{f.Bounds.Height} r{f.Radius}")) + ")");
                if (dir != null)
                {
                    var bmp = BitmapSource.Create(scene.W, scene.H, 96, 96, PixelFormats.Bgra32, null, px, scene.W * 4);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(bmp));
                    using var f = System.IO.File.Create(System.IO.Path.Combine(dir, "scene-" + string.Join("", scene.Name.Where(char.IsLetterOrDigit)) + ".png"));
                    enc.Save(f);
                }
            }
            catch (Exception ex) { log.Add($"pictures / {scene.Name}: FAILED {ex}"); }
        }
        log.Insert(0, $"pictures: {hit}/{total} found exactly, {wrong} of {falseChecks} non-pictures snapped to, {ms} ms in all");

        // real screenshots in a folder: what's found in each, drawn on a copy to look at
        if (Environment.GetEnvironmentVariable("OVERSKIN_PICTURE_FILES") is { Length: > 0 } folder && dir != null)
            foreach (var file in System.IO.Directory.GetFiles(folder, "*.png"))
            {
                try
                {
                    var src = new FormatConvertedBitmap(BitmapFrame.Create(new Uri(file), BitmapCreateOptions.None, BitmapCacheOption.OnLoad), PixelFormats.Bgra32, null, 0);
                    int fw = src.PixelWidth, fh = src.PixelHeight;
                    var fpx = new byte[fw * fh * 4];
                    src.CopyPixels(fpx, fw * 4, 0);
                    var sw2 = System.Diagnostics.Stopwatch.StartNew();
                    var found = ImageFinder.Find(fpx, fw, fh);
                    log.Add($"file {System.IO.Path.GetFileName(file)} {fw}x{fh}: {found.Count} in {sw2.ElapsedMilliseconds} ms: " +
                            string.Join(" ", found.Select(f => $"{f.Bounds.X},{f.Bounds.Y} {f.Bounds.Width}x{f.Bounds.Height} r{f.Radius}")));
                    var v = new DrawingVisual();
                    using (var d = v.RenderOpen())
                    {
                        d.DrawImage(src, new Rect(0, 0, fw, fh));
                        var pen = new Pen(Brushes.Lime, 4);
                        foreach (var f in found) d.DrawRectangle(null, pen, new Rect(f.Bounds.X, f.Bounds.Y, f.Bounds.Width, f.Bounds.Height));
                    }
                    var r = new RenderTargetBitmap(fw, fh, 96, 96, PixelFormats.Pbgra32);
                    r.Render(v);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(r));
                    using var fs = System.IO.File.Create(System.IO.Path.Combine(dir, "file-" + System.IO.Path.GetFileName(file)));
                    enc.Save(fs);
                }
                catch (Exception ex) { log.Add($"file {file}: FAILED {ex.Message}"); }
            }
    }
}
#endif
