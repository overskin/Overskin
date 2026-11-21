#if DEBUG
using System.Windows;
using System.Windows.Media;

namespace Overskin;

/// <summary>Self-tests for the smooth cursor, video backdrop and shortcut keys, Gartic strokes, and the tray menu.</summary>
public partial class App
{
    private static void PassTests(List<string> log)
    {
        static string F(double v) => v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

        // ---- smooth cursor: no lag mid-move, shakes gone, eases in and out, exact on clicks ----
        try
        {
            var track = new CursorTrack(new Int32Rect(0, 0, 1000, 1000), () => 0, start: false);
            // rest at 100 for 0.5 s, glide to 500 by 0.9 s with a ±3 px shake up and down, rest, click at 1.2 s,
            // then move away the instant after a click at 1.5 s
            double X(double t) => t < 0.5 ? 100 : t < 0.9 ? 100 + (t - 0.5) / 0.4 * 400 : t < 1.5 ? 500 : Math.Min(700, 500 + (t - 1.5) / 0.2 * 200);
            int k = 0;
            for (double t = 0; t <= 2.2; t += 0.004, k++)
                track.Add(new CursorTrack.Sample(t, X(t), 300 + (t > 0.5 && t < 0.9 ? (k % 2 == 0 ? 3 : -3) : 0), (t >= 1.2 && t < 1.25) || (t >= 1.5 && t < 1.55), (IntPtr)1, true));
            double sigma = SmoothCursorPainter.Sigma(45), ahead = 3 * sigma;
            var mid = track.SmoothAt(0.7, sigma, ahead);
            double lag = Math.Abs(mid.X - X(0.7));
            double shake = 0;
            for (double t = 0.6; t <= 0.8; t += 0.01) shake = Math.Max(shake, Math.Abs(track.SmoothAt(t, sigma, ahead).Y - 300));
            // the bell curve looks both ways, so the ease-in starts just before the real move: one sigma before it
            double v0 = (track.SmoothAt(0.5 - sigma + 0.01, sigma, ahead).X - track.SmoothAt(0.5 - sigma - 0.01, sigma, ahead).X) / 0.02;
            double vm = (track.SmoothAt(0.71, sigma, ahead).X - track.SmoothAt(0.69, sigma, ahead).X) / 0.02;
            var atClick = track.SmoothAt(1.5, sigma, ahead);
            var clicks = track.ClicksBetween(0, 3);
            log.Add($"smooth cursor: sigma={F(sigma)}s lag mid-move={F(lag)}px (expect <1) shake left={F(shake)}px of 3 (expect <0.5) " +
                    $"start speed {v0:0} vs cruising {vm:0} px/s (expect start well under half) on click off by {F(Math.Abs(atClick.X - 500))}px (expect <1.5) " +
                    $"clicks={clicks.Count} at {string.Join(",", clicks.Select(c => F(c.Time)))} (expect 2 at 1.20,1.50)");

            // motion blur: the same sprite, still vs swept 40 px, spreads wider and fainter
            var dot = new byte[10 * 10 * 4];
            for (int i = 0; i < 100; i++) { dot[i * 4] = dot[i * 4 + 1] = dot[i * 4 + 2] = 255; dot[i * 4 + 3] = 255; }
            var sprite = new PointerSprite(dot, 10, 10, 0, 0);
            (int Width, int Peak) Measure(IReadOnlyList<Point> path)
            {
                var frame = new byte[100 * 30 * 4];
                SmoothCursorPainter.Paint(frame, 100, 30, sprite, path);
                int width = 0, peak = 0;
                for (int x = 0; x < 100; x++)
                {
                    int v = frame[(15 * 100 + x) * 4];
                    if (v > 8) width++;
                    peak = Math.Max(peak, v);
                }
                return (width, peak);
            }
            var still = Measure(SmoothCursorPainter.ShutterPath(_ => new Point(40, 10), 1, 1 / 30.0, 0.5));
            var swept = Measure(SmoothCursorPainter.ShutterPath(t => new Point(40 + (t - 1) * 30 * 80, 10), 1, 1 / 30.0, 0.5));
            var noBlur = Measure(SmoothCursorPainter.ShutterPath(t => new Point(40 + (t - 1) * 30 * 80, 10), 1, 1 / 30.0, 0));
            log.Add($"motion blur: still width={still.Width} peak={still.Peak} swept width={swept.Width} peak={swept.Peak} blur off width={noBlur.Width} " +
                    "(expect still 10/255, swept about 50 and fainter, blur off 10)");

            // a Windows pointer turned into a sprite (the hand, which has no vector version)
            var painter = new SmoothCursorPainter(null);
            var hand = NativeMethods.LoadCursor(IntPtr.Zero, (IntPtr)32649);
            var handSprite = painter.SpriteFor(hand, 32);
            int solid = handSprite == null ? 0 : Enumerable.Range(0, handSprite.Width * handSprite.Height).Count(i => handSprite.Pixels[i * 4 + 3] > 200);
            log.Add($"windows pointer sprite: made={handSprite != null} solid pixels={solid} hotspot={F(handSprite?.HotX ?? -1)},{F(handSprite?.HotY ?? -1)} (expect True, >40, inside 0..32)");
        }
        catch (Exception ex) { log.Add("smooth cursor: FAILED " + ex); }

        // ---- video backdrop and shortcut keys ----
        try
        {
            var look = VideoBackdrop.Create("Ocean", 640, 360)!;
            var inner = new byte[look.InnerWidth * look.InnerHeight * 4];
            for (int i = 0; i < inner.Length; i += 4) { inner[i] = 10; inner[i + 1] = 200; inner[i + 2] = 30; inner[i + 3] = 255; }
            var frame = look.Compose(inner);
            string Px(int x, int y) { int i = (y * 640 + x) * 4; return $"{frame[i + 2]},{frame[i + 1]},{frame[i]}"; }
            string corner = Px(look.X, look.Y), centre = Px(320, 180), edge = Px(2, 2);
            log.Add($"video backdrop: card {look.InnerWidth}x{look.InnerHeight} at {look.X},{look.Y} centre={centre} (expect 30,200,10) " +
                    $"outside={edge} (expect the gradient, blue-ish) card corner={corner} (expect backdrop, rounded off) none={VideoBackdrop.Create("None", 640, 360) == null}");
            if (Environment.GetEnvironmentVariable("OVERSKIN_TEST_OUT") is { Length: > 0 } dir)
                SaveBgra(frame, 640, 360, System.IO.Path.Combine(dir, "video-backdrop.png"));

            var labels = new[]
            {
                KeyCaps.Label(0x43, true, false, false, false), KeyCaps.Label(0x41, false, false, false, false),
                KeyCaps.Label(0x41, false, false, true, false), KeyCaps.Label(0x0D, false, false, false, false),
                KeyCaps.Label(0x09, false, true, false, false), KeyCaps.Label(0x53, false, false, true, true),
            };
            log.Add($"shortcut labels: [{string.Join(" | ", labels.Select(l => l ?? "-"))}] (expect Ctrl + C | - | - | Enter | Alt + Tab | Win + Shift + S)");

            double now = 0;
            var keys = new KeyCaps(Current.Dispatcher, () => now, 360, start: false);
            keys.Press("Ctrl + C");
            DoEvents();
            var shown = new byte[640 * 360 * 4];
            keys.Draw(shown, 640, 360, 0.3);
            var gone = new byte[640 * 360 * 4];
            keys.Draw(gone, 640, 360, KeyCaps.Hold + 0.1);
            int lit = 0, litLater = 0;
            for (int i = 0; i < shown.Length; i += 4) { if (shown[i] + shown[i + 1] + shown[i + 2] > 300) lit++; if (gone[i] + gone[i + 1] + gone[i + 2] > 300) litLater++; }
            log.Add($"shortcut keys on video: lit pixels at 0.3 s={lit} (expect >30) after {KeyCaps.Hold + 0.1:0.0} s={litLater} (expect 0)");
            if (Environment.GetEnvironmentVariable("OVERSKIN_TEST_OUT") is { Length: > 0 } dir2)
            {
                var both = look.Compose(inner);
                keys.Draw(both, 640, 360, 0.3);
                SaveBgra(both, 640, 360, System.IO.Path.Combine(dir2, "video-keys.png"));
            }
        }
        catch (Exception ex) { log.Add("video look: FAILED " + ex); }

        // ---- Gartic: outlines first, continuous rounded strokes, no dots ----
        try
        {
            int w = 240, h = 180;
            var px = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double d = Math.Sqrt((x - 120) * (x - 120) + (y - 90) * (y - 90));
                    byte rr = 255, g = 255, b = 255;
                    if (d < 60) { rr = 230; g = 40; b = 40; }            // red face
                    if (Math.Abs(d - 60) < 2) { rr = g = b = 15; }        // black outline
                    if (Math.Abs(x - 100) < 2 && Math.Abs(y - 75) < 8) { rr = g = b = 15; } // eyes
                    if (Math.Abs(x - 140) < 2 && Math.Abs(y - 75) < 8) { rr = g = b = 15; }
                    int i = (y * w + x) * 4;
                    px[i] = b; px[i + 1] = g; px[i + 2] = rr; px[i + 3] = 255;
                }
            var src = TraceEngine.PrepareBgra(px, w, h);
            var palette = new List<(Color, bool)> { (Colors.White, true), (Color.FromRgb(0, 0, 0), false), (Color.FromRgb(230, 40, 40), false), (Color.FromRgb(40, 90, 230), false) };
            var sizes = new List<double> { 2, 4, 8, 14, 24 };
            var o = new TraceOptions { Mode = TraceMode.Gartic, Palette = palette, BrushSizes = sizes, OpacityLevels = GarticSetup.OpacityLevels, Blur = 0 };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = TraceEngine.Trace(src, o, default);
            sw.Stop();
            double Len(Point[] p) { double s = 0; for (int i = 1; i < p.Length; i++) s += (p[i] - p[i - 1]).Length; return s; }
            int firstOutline = Enumerable.Range(0, r.Paths.Count).TakeWhile(i => r.ColourIndex![i] == 1).Count();
            int dots = r.Paths.Count(p => p.Length < 2 || Len(p) < 1);
            int sharp = 0, turns = 0;
            foreach (var p in r.Paths)
                for (int i = 1; i + 1 < p.Length; i++)
                {
                    var a = p[i] - p[i - 1];
                    var b2 = p[i + 1] - p[i];
                    if (a.Length < 1e-6 || b2.Length < 1e-6) continue;
                    double turn = Math.Abs(Vector.AngleBetween(a, b2));
                    turns++;
                    if (turn > 100) sharp++;
                }
            double error = Painter.MeanError(r.Painted!, src, r.Width, r.Height);
            log.Add($"gartic strokes: {r.Paths.Count} strokes in {sw.ElapsedMilliseconds} ms, first {firstOutline} are black outlines (expect several) " +
                    $"dots={dots} (expect 0) sharp turns={sharp} of {turns} (expect 0) error={F(error)} (expect under 20)");
            if (Environment.GetEnvironmentVariable("OVERSKIN_TEST_OUT") is { Length: > 0 } dir)
                SaveBgra(r.Painted!, r.Width, r.Height, System.IO.Path.Combine(dir, "gartic-painted.png"));

            // the hand: eased speed, a small wobble, exact ends
            var line = new[] { new Point(0, 0), new Point(300, 0) };
            var hand = HumanStroke.Path(line, 18, 0.9, new Random(3));
            double first = (hand[1] - hand[0]).Length, last = (hand[^1] - hand[^2]).Length, most = 0, wobble = 0;
            for (int i = 1; i < hand.Length; i++) most = Math.Max(most, (hand[i] - hand[i - 1]).Length);
            foreach (var p in hand) wobble = Math.Max(wobble, Math.Abs(p.Y));
            log.Add($"gartic hand: {hand.Length} points, first step {F(first)} last {F(last)} fastest {F(most)} px (expect small, small, about 18) " +
                    $"wobble {F(wobble)} px (expect 0.2 to 0.9) ends {hand[0]} {hand[^1]} (expect 0,0 300,0) travel steps={HumanStroke.Travel(new Point(0, 0), new Point(200, 0), 18).Length}");
        }
        catch (Exception ex) { log.Add("gartic strokes: FAILED " + ex); }

        // ---- pictures inside a screenshot: a chat full of text, an avatar, a panel, and two sent pictures ----
        try
        {
            int w = 2560, h = 1440;
            var visual = new DrawingVisual();
            var photoRect = new Rect(400, 300, 520, 360);   // rounded, 8 px corners
            var squareRect = new Rect(400, 760, 300, 200);  // square corners
            var memeRect = new Rect(1500.4, 700.6, 420, 380); // half a pixel off (soft edges), rounded, a white caption bar on top, flat cartoon colours
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x31, 0x33, 0x38)), null, new Rect(0, 0, w, h));
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x2B, 0x2D, 0x31)), null, new Rect(0, 0, 240, h)); // sidebar
                var face = new Typeface("Segoe UI");
                for (int line = 0; line < 30; line++)
                {
                    var text = new FormattedText("Somebody wrote a message here, with a few more words to fill the line " + line,
                        System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 15, Brushes.Gainsboro, 1.0);
                    dc.DrawText(text, new Point(line < 6 ? 400 : 1100, 40 + line * 40));
                }
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x58, 0x65, 0xF2)), null, new Point(340, 320), 20, 20); // avatar
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x38, 0x3A, 0x40)), null, new Rect(260, 1340, 2200, 52), 8, 8); // message box
                dc.DrawText(new FormattedText("Message #general", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 15, Brushes.Gray, 1.0), new Point(290, 1355));
                // a "photo": smooth colour gradients plus fine noise, as a picture has
                var rnd = new Random(7);
                var bits = new byte[520 * 360 * 4];
                for (int y = 0; y < 360; y++)
                    for (int x = 0; x < 520; x++)
                    {
                        int i = (y * 520 + x) * 4;
                        bits[i] = (byte)Math.Clamp(60 + x / 4 + rnd.Next(-12, 12), 0, 255);
                        bits[i + 1] = (byte)Math.Clamp(90 + y / 3 + rnd.Next(-12, 12), 0, 255);
                        bits[i + 2] = (byte)Math.Clamp(200 - x / 5 + rnd.Next(-12, 12), 0, 255);
                        bits[i + 3] = 255;
                    }
                var photo = System.Windows.Media.Imaging.BitmapSource.Create(520, 360, 96, 96, PixelFormats.Bgra32, null, bits, 520 * 4);
                dc.PushClip(new RectangleGeometry(photoRect, 8, 8));
                dc.DrawImage(photo, photoRect);
                dc.Pop();
                dc.DrawImage(photo, squareRect);
                // a meme: white caption with black words, then a flat cartoon (sky, grass, sun)
                dc.PushClip(new RectangleGeometry(memeRect, 8, 8));
                dc.DrawRectangle(Brushes.White, null, new Rect(memeRect.X, memeRect.Y, memeRect.Width, 90));
                dc.DrawText(new FormattedText("when the code works first try", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 26, Brushes.Black, 1.0), new Point(memeRect.X + 30, memeRect.Y + 28));
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(120, 190, 240)), null, new Rect(memeRect.X, memeRect.Y + 90, memeRect.Width, 180));
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(80, 170, 70)), null, new Rect(memeRect.X, memeRect.Y + 270, memeRect.Width, 110));
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(250, 210, 60)), null, new Point(memeRect.X + 330, memeRect.Y + 150), 34, 34);
                dc.Pop();
            }
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            var screen = new byte[w * h * 4];
            rtb.CopyPixels(screen, w * 4, 0);
            ImageFinder.Find(screen, w, h); // warm up
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var found = ImageFinder.Find(screen, w, h);
            sw.Stop();
            string Show(FoundImage f) => $"{f.Bounds.X},{f.Bounds.Y} {f.Bounds.Width}x{f.Bounds.Height} r{f.Radius}";
            var onPhoto = ImageFinder.At(found, 600, 500);
            var onSquare = ImageFinder.At(found, 500, 850);
            var onText = ImageFinder.At(found, 1300, 300);
            var onMeme = ImageFinder.At(found, 1700, 900);
            log.Add($"pictures in a screenshot: {found.Count} found in {sw.ElapsedMilliseconds} ms on {w}x{h} [{string.Join(" | ", found.Select(Show))}] " +
                    $"(expect 3: 400,300 520x360 r8, 400,760 300x200 r0, 1500,700 421x381 r8) meme={(onMeme is { } m ? Show(m) : "none")} hover photo={(onPhoto is { } a ? Show(a) : "none")} " +
                    $"square={(onSquare is { } b ? Show(b) : "none")} text={(onText is { } c ? Show(c) : "none")} (expect none on text)");
            if (Environment.GetEnvironmentVariable("OVERSKIN_TEST_OUT") is { Length: > 0 } dir)
                SaveBgra(screen, w, h, System.IO.Path.Combine(dir, "pictures-screen.png"));

            // through the capture screen itself (never shown): hovering the photo targets exactly the photo
            var frozen = System.Windows.Media.Imaging.BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, screen, w * 4);
            var overlay = CaptureOverlay.ForTest(frozen, new NativeMethods.RECT { Left = 0, Top = 0, Right = w, Bottom = h });
            var hit = overlay.PictureTargetForTest(600, 500);
            var miss = overlay.PictureTargetForTest(1300, 300);
            overlay.Close();
            var (skippedOverlays, firstWindow) = CaptureOverlay.WindowsForTest();
            log.Add($"window picking: {skippedOverlays} see-through overlay windows skipped on this desktop; the top window that can be picked is {firstWindow}");
            log.Add($"capture screen snaps to pictures: found={hit.Count} on photo -> {hit.Target.X},{hit.Target.Y} {hit.Target.Width}x{hit.Target.Height} picture={hit.Picture} " +
                    $"(expect 400,300 520x360 True) on text picture={miss.Picture} (expect False)");
        }
        catch (Exception ex) { log.Add("pictures in a screenshot: FAILED " + ex); }

        // ---- scrolling capture with things moving on the page: an animation, a caret, a header shadow, noise ----
        try
        {
            int w = 600, h = 400, pageH = 2600, header = 40;
            var rng = new Random(11);
            var page = new byte[w * pageH * 4];
            for (int y = 0; y < pageH; y++)
            {
                bool blank = y % 18 < 6;
                for (int x = 0; x < w; x++)
                {
                    byte v = blank ? (byte)255 : (byte)(rng.Next(5) == 0 ? 30 : 245);
                    int o = (y * w + x) * 4;
                    page[o] = v; page[o + 1] = v; page[o + 2] = (byte)(v ^ (y & 0x1F)); page[o + 3] = 255;
                }
            }
            int frameNo = 0;
            byte[] View(int offset)
            {
                frameNo++;
                var f = new byte[w * h * 4];
                for (int y = 0; y < h; y++)
                {
                    if (y < header)
                    {
                        // a sticky header that gets a shadow line once the page has scrolled
                        byte hv = (byte)(offset > 0 && y == header - 1 ? 150 : 200);
                        for (int x = 0; x < w; x++) { int o = (y * w + x) * 4; f[o] = hv; f[o + 1] = 60; f[o + 2] = 90; f[o + 3] = 255; }
                        continue;
                    }
                    int src = Math.Min(pageH - 1, offset + y - header);
                    Buffer.BlockCopy(page, src * w * 4, f, y * w * 4, w * 4);
                }
                // an animation that stays in place on screen (like a floating chat bubble or spinner): changes every frame
                for (int y = h - 90; y < h - 20; y++)
                    for (int x = w - 140; x < w - 40; x++) { int o = (y * w + x) * 4; f[o] = (byte)(frameNo * 37 + x); f[o + 1] = (byte)(frameNo * 91 + y); f[o + 2] = 128; }
                // a blinking caret in the page
                if (frameNo % 2 == 0) for (int y = 120; y < 140; y++) { int o = (y * w + 300) * 4; f[o] = f[o + 1] = f[o + 2] = 0; }
                // a little noise, as from a resampled smooth scroll
                for (int k = 0; k < 400; k++) { int o = (rng.Next(header, h) * w + rng.Next(w)) * 4; f[o] = (byte)Math.Clamp(f[o] + rng.Next(-6, 7), 0, 255); }
                return f;
            }
            int maxOffset = pageH - (h - header);
            var stitcher = new ScrollStitcher(View(0), w, h);
            int off = 0, steps = 0;
            string results = "";
            while (steps++ < 60)
            {
                off = Math.Min(maxOffset, off + 211);
                var r1 = stitcher.Add(View(off));
                results += r1.ToString()[0];
                if (r1 == ScrollStitcher.Step.Lost) { results += $"(score {stitcher.LastScore:0.00} at {stitcher.LastBest}, {stitcher.LastNote})"; break; }
                if (r1 != ScrollStitcher.Step.Moved && off == maxOffset) break;
            }
            var stitched = stitcher.Result();
            var px = new byte[stitched.PixelWidth * stitched.PixelHeight * 4];
            stitched.CopyPixels(px, stitched.PixelWidth * 4, 0);
            // page rows checked away from the animation's columns and the caret
            int wrong = 0, checkedRows = 0;
            for (int y = header; y < Math.Min(stitched.PixelHeight - 100, header + pageH); y++)
            {
                checkedRows++;
                int bad = 0;
                for (int x = 30; x < w - 160; x++)
                {
                    if (x == 300) continue;
                    int o = (y * w + x) * 4, sIdx = ((y - header) * w + x) * 4;
                    if (Math.Abs(px[o + 2] - page[sIdx + 2]) > 8) bad++;
                }
                if (bad > 3) wrong++;
            }
            log.Add($"scroll with things moving: steps={results} height={stitched.PixelHeight} (expect about {header + pageH}) rows out of place={wrong} of {checkedRows} (expect 0)");
        }
        catch (Exception ex) { log.Add("scroll with things moving: FAILED " + ex); }

        // ---- moving things inside a screenshot, and swapping a picture ----
        try
        {
            int w = 900, h = 520;
            var dv = new DrawingVisual();
            var photoBox = new Rect(480, 120, 320, 220);
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x31, 0x33, 0x38)), null, new Rect(0, 0, w, h));
                var ft = new FormattedText("Move me", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 28, Brushes.White, 1.0);
                dc.DrawText(ft, new Point(60, 100));
                var rnd = new Random(3);
                var bits = new byte[320 * 220 * 4];
                for (int i = 0; i < bits.Length; i += 4) { bits[i] = (byte)rnd.Next(40, 120); bits[i + 1] = (byte)rnd.Next(120, 220); bits[i + 2] = (byte)rnd.Next(60, 200); bits[i + 3] = 255; }
                dc.PushClip(new RectangleGeometry(photoBox, 8, 8));
                dc.DrawImage(System.Windows.Media.Imaging.BitmapSource.Create(320, 220, 96, 96, PixelFormats.Bgra32, null, bits, 320 * 4), photoBox);
                dc.Pop();
            }
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            var shot = ImageLoader.Normalize(rtb);
            var ed = new EditorWindow(shot, null, "move", new EditorHost { SaveNew = _ => null, Pin = (_, _) => { }, Trace = _ => { } })
            { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
            ed.Show();
            DoEvents();
            byte[] Px(System.Windows.Media.Imaging.BitmapSource b) { var c = new System.Windows.Media.Imaging.FormatConvertedBitmap(b, PixelFormats.Bgra32, null, 0); var a2 = new byte[c.PixelWidth * c.PixelHeight * 4]; c.CopyPixels(a2, c.PixelWidth * 4, 0); return a2; }
            string At(byte[] a2, int x, int y) { int i = (y * w + x) * 4; return $"{a2[i + 2]},{a2[i + 1]},{a2[i]}"; }

            // a word on the plain background: lifted with its background taken out, the gap filled flat
            void Wait(Task t) { var until = DateTime.UtcNow.AddSeconds(10); while (!t.IsCompleted && DateTime.UtcNow < until) { DoEvents(); Thread.Sleep(10); } }
            Wait(ed.LiftForTest(new Int32Rect(50, 95, 140, 50), keyOut: true, 0));
            var piece = ed.ItemsForTest.LastOrDefault(i => i.Kind == AnnotationKind.Piece);
            var after = Px(ed.BaseForTest);
            int leftover = 0;
            for (int y = 95; y < 145; y++) for (int x = 50; x < 190; x++) { int i = (y * w + x) * 4; if (Math.Abs(after[i + 2] - 0x31) > 6) leftover++; }
            var pp = piece?.Pixels is { } pb ? Px(pb) : Array.Empty<byte>();
            int clear = 0, solid = 0;
            for (int i = 3; i < pp.Length; i += 4) { if (pp[i] == 0) clear++; else if (pp[i] > 230) solid++; }
            string pieceBox = piece == null ? "none" : $"{piece.Points[0].X:0},{piece.Points[0].Y:0} {piece.Points[1].X - piece.Points[0].X:0}x{piece.Points[1].Y - piece.Points[0].Y:0}";
            log.Add($"move a word: piece={pieceBox} (expect about 60,105 and the word's size) see-through pixels={clear} solid={solid} (expect both many) " +
                    $"left in the gap={leftover} (expect 0)");

            // the picture: found, swapped for a plain red one with the same corners, then lifted whole
            int found = ed.PicturesForTest();
            var red = System.Windows.Media.Imaging.BitmapSource.Create(40, 30, 96, 96, PixelFormats.Bgra32, null, Enumerable.Range(0, 40 * 30).SelectMany(_ => new byte[] { 30, 30, 220, 255 }).ToArray(), 160);
            ed.ReplacePictureForTest(0, red);
            var swapped = Px(ed.BaseForTest);
            string middle = At(swapped, 640, 230), corner = At(swapped, 481, 121), outside = At(swapped, 470, 230);
            Wait(ed.LiftForTest(new Int32Rect(480, 120, 320, 220), keyOut: false, 8));
            var lifted = ed.ItemsForTest.LastOrDefault(i => i.Kind == AnnotationKind.Piece);
            var gap = At(Px(ed.BaseForTest), 640, 230);
            ed.CloseForTest();
            log.Add($"swap a picture: found={found} (expect 1) middle={middle} (expect 220,30,30) rounded corner={corner} (expect the background 49,51,56 or close) outside={outside} (expect 49,51,56); " +
                    $"lifted it: {lifted?.Pixels?.PixelWidth}x{lifted?.Pixels?.PixelHeight} (expect 320x220) gap now {gap} (expect 49,51,56)");
        }
        catch (Exception ex) { log.Add("move things: FAILED " + ex); }

        // ---- video editing: idle parts found and cut, a label shown over the video at its time ----
        try
        {
            string src = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Overskin-edit-src.mp4");
            string dst = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Overskin-edit-out.mp4");
            // 8 s: a moving bar for 2 s, 4 s of nothing changing, then moving again
            {
                int w = 320, h = 180, fps = 30;
                var queue = new System.Collections.Concurrent.BlockingCollection<VideoFrame>(8);
                var encode = Task.Run(() => VideoEncoder.EncodeAsync(src, w, h, fps, queue));
                for (int i = 0; i < fps * 8; i++)
                {
                    var f = new byte[w * h * 4];
                    double t = i / (double)fps;
                    int bar = t < 2 ? i * 4 % w : t < 6 ? 2 * fps * 4 % w : i * 4 % w;
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                        {
                            int o = (y * w + x) * 4;
                            bool on = Math.Abs(x - bar) < 20;
                            f[o] = (byte)(on ? 250 : 40); f[o + 1] = (byte)(on ? 250 : 120); f[o + 2] = (byte)(on ? 250 : 40); f[o + 3] = 255;
                        }
                    queue.Add(new VideoFrame(f, TimeSpan.FromSeconds(t)));
                }
                queue.CompleteAdding();
                encode.GetAwaiter().GetResult();
            }
            var idle = Task.Run(() => VideoEdit.FindIdleAsync(src)).GetAwaiter().GetResult();
            var keep = VideoEdit.Keep(0, 8, idle);
            var label = new VideoOverlay { Kind = VideoOverlay.Kinds.Text, Text = "Click Save", A = new Point(160, 90), Start = 0.5, End = 1.5 };
            Task.Run(() => VideoEdit.RenderAsync(src, dst, keep, new[] { label }, 180)).GetAwaiter().GetResult();
            double length = Task.Run(() => VideoTrim.DurationAsync(dst)).GetAwaiter().GetResult().TotalSeconds;
            string during = length > 1.0 ? VideoPixel(dst, 1.0) : "-", after = length > 1.8 ? VideoPixel(dst, 1.8) : "-";
            log.Add($"video edit: idle parts=[{string.Join(" ", idle.Select(i => $"{i.Start:0.0}-{i.End:0.0}"))}] (expect one, about 2.4-5.6) " +
                    $"saved length={length:0.0}s (expect about {8 - idle.Sum(i => i.End - i.Start):0.0}) middle at 1.0 s={during} (expect dark: the label) at 1.8 s={after} (expect the picture: green-grey r40g120b40)");

            // the window, with a cut and a label, drawn for a look
            var win = new VideoEditorWindow(src, _ => { }) { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
            win.Show();
            Pump(1200);
            win.AddCutForTest(2.4, 5.4);
            Pump(200);
            log.Add($"video editor window: cuts={win.CutCountForTest} (expect 1)");
            SaveWindow(win, "video-editor.png");
            win.Close();
        }
        catch (Exception ex) { log.Add("video edit: FAILED " + ex); }

        // ---- capture screen on several monitors: a mirror shows its part of the shifted canvas ----
        try
        {
            var canvas = new System.Windows.Controls.Canvas { Width = 1000, Height = 100, Background = Brushes.Black };
            var block = new System.Windows.Shapes.Rectangle { Width = 100, Height = 100, Fill = Brushes.Lime };
            System.Windows.Controls.Canvas.SetLeft(block, 600);
            canvas.Children.Add(block);
            var host = new System.Windows.Controls.Canvas { Width = 300, Height = 100, ClipToBounds = true };
            System.Windows.Controls.Canvas.SetLeft(canvas, -500);
            host.Children.Add(canvas);
            var win = new Window { Content = host, Width = 300, Height = 100, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            win.Show();
            DoEvents();
            string Sample(Visual source, double x)
            {
                var mirror = new System.Windows.Shapes.Rectangle
                {
                    Width = 100, Height = 100,
                    Fill = new VisualBrush(source) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(x, 0, 100, 100), Stretch = Stretch.Fill },
                };
                mirror.Measure(new Size(100, 100));
                mirror.Arrange(new Rect(0, 0, 100, 100));
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(100, 100, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(mirror);
                var px = new byte[4];
                rtb.CopyPixels(new Int32Rect(50, 50, 1, 1), px, 4, 0);
                return $"{px[2]},{px[1]},{px[0]}";
            }
            string a600 = Sample(canvas, 600), a100 = Sample(canvas, 100);
            // the other way: the canvas at the origin, the host moved instead
            System.Windows.Controls.Canvas.SetLeft(canvas, 0);
            host.RenderTransform = new TranslateTransform(-500, 0);
            DoEvents();
            string b600 = Sample(canvas, 600);
            win.Close();
            log.Add($"monitor mirror: canvas offset by Left: viewbox 600 -> {a600}, viewbox 100 -> {a100}; host moved instead: viewbox 600 -> {b600} (expect lime 0,255,0 somewhere)");
        }
        catch (Exception ex) { log.Add("monitor mirror: FAILED " + ex); }

        // ---- the viewer: pictures, a GIF and a video; arrow keys, zoom, rotate ----
        try
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Overskin-viewer-test");
            System.IO.Directory.CreateDirectory(dir);
            var files = new List<string?>();
            for (int k = 0; k < 3; k++)
            {
                int w = 400 + k * 100, h = 300;
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb((byte)(60 + k * 60), 90, 200), Color.FromRgb(240, 180, (byte)(40 + k * 50)), 30), null, new Rect(0, 0, w, h));
                    dc.DrawText(new FormattedText($"Picture {k + 1}", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 40, Brushes.White, 1.0), new Point(30, 30));
                }
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                string f = System.IO.Path.Combine(dir, $"picture {k + 1}.png");
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                using (var fs = System.IO.File.Create(f)) enc.Save(fs);
                files.Add(f);
            }
            string video = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Overskin-edit-out.mp4");
            if (System.IO.File.Exists(video)) files.Add(video);
            var host = new ViewerHost { Edit = (_, _, _) => { }, EditVideo = _ => { }, EditGif = _ => { }, Pin = (_, _, _) => { }, ShowInFolder = _ => { }, Changed = () => { } };
            var viewer = new ViewerWindow(files, 1, host) { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
            viewer.Show();
            Pump(600);
            string first = viewer.TitleForTest;
            double fitScale = viewer.ScaleForTest;
            viewer.ZoomForTest(2);
            Pump(300);
            double zoomed = viewer.ScaleForTest;
            SaveWindow(viewer, "viewer.png");
            viewer.GoForTest(1);
            Pump(500);
            string second = viewer.TitleForTest;
            var turnTask = viewer.RotateForTest(90);
            var until = DateTime.UtcNow.AddSeconds(5);
            while (!turnTask.IsCompleted && DateTime.UtcNow < until) { DoEvents(); Thread.Sleep(10); }
            var saved = ImageLoader.LoadFile(files[2]!);
            viewer.GoForTest(1);
            Pump(800);
            string third = viewer.TitleForTest;
            SaveWindow(viewer, "viewer-video.png");
            viewer.Close();
            log.Add($"viewer: opened on {first} at {fitScale * 100:0}% (expect picture 2.png, 100%) zoomed to {zoomed * 100:0}% (expect 200%) " +
                    $"next {second} (expect picture 3.png) rotated file is {saved.PixelWidth}x{saved.PixelHeight} (expect 300x600) then {third} (expect the video)");
        }
        catch (Exception ex) { log.Add("viewer: FAILED " + ex); }

        // ---- size limit: a big, noisy screenshot always fits, as PNG and as JPG ----
        try
        {
            int w = 2400, h = 1600;
            var rnd = new Random(1);
            var px = new byte[w * h * 4];
            for (int i = 0; i < px.Length; i += 4) { px[i] = (byte)rnd.Next(256); px[i + 1] = (byte)(i / 4 % w / 10); px[i + 2] = (byte)rnd.Next(256); px[i + 3] = 255; }
            var big = System.Windows.Media.Imaging.BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
            long limit = 2 * 1024 * 1024;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var png = ImageFiles.Fit(big, false, 90, limit);
            long pngMs = sw.ElapsedMilliseconds;
            var jpg = ImageFiles.Fit(big, true, 90, limit);
            log.Add($"size limit 2 MB: png {png.Length / 1024} KB in {pngMs} ms, jpg {jpg.Length / 1024} KB in {sw.ElapsedMilliseconds - pngMs} ms (expect both at most 2048 KB)");
        }
        catch (Exception ex) { log.Add("size limit: FAILED " + ex); }

        // ---- mockup frames: browser, laptop, phone, and your own frame picture ----
        try
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(70, 120, 230), Color.FromRgb(160, 90, 220), 30), null, new Rect(0, 0, 800, 500));
                dc.DrawText(new FormattedText("Screenshot", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 60, Brushes.White, 1.0), new Point(40, 40));
            }
            var shotRtb = new System.Windows.Media.Imaging.RenderTargetBitmap(800, 500, 96, 96, PixelFormats.Pbgra32);
            shotRtb.Render(dv);
            // a home-made frame: a grey slab with a see-through rounded screen
            var fv = new DrawingVisual();
            using (var dc = fv.RenderOpen())
            {
                var outer = new RectangleGeometry(new Rect(20, 20, 560, 360), 30, 30);
                var hole = new RectangleGeometry(new Rect(60, 60, 480, 280), 12, 12);
                dc.DrawGeometry(Brushes.DimGray, null, new CombinedGeometry(GeometryCombineMode.Exclude, outer, hole));
            }
            var frameRtb = new System.Windows.Media.Imaging.RenderTargetBitmap(600, 400, 96, 96, PixelFormats.Pbgra32);
            frameRtb.Render(fv);
            string framePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Overskin-test-frame.png");
            var fe = new System.Windows.Media.Imaging.PngBitmapEncoder();
            fe.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(frameRtb));
            using (var f = System.IO.File.Create(framePath)) fe.Save(f);
            var screen = Mockup.FindScreen(frameRtb);
            var sizes = new List<string>();
            foreach (var name in Mockup.Frames.Skip(1))
            {
                var made = Beautify.Render(shotRtb, new BeautifyOptions { Background = "Ocean", Padding = 60, Frame = name, FramePath = framePath });
                sizes.Add($"{name} {made.PixelWidth}x{made.PixelHeight}");
                if (Environment.GetEnvironmentVariable("OVERSKIN_TEST_OUT") is { Length: > 0 } dir)
                {
                    var px = new byte[made.PixelWidth * made.PixelHeight * 4];
                    new System.Windows.Media.Imaging.FormatConvertedBitmap(made, PixelFormats.Bgra32, null, 0).CopyPixels(px, made.PixelWidth * 4, 0);
                    SaveBgra(px, made.PixelWidth, made.PixelHeight, System.IO.Path.Combine(dir, $"mockup-{name}.png"));
                }
            }
            log.Add($"mockups: [{string.Join(" | ", sizes)}] custom frame's screen found at {screen} (expect about 60,60,480,280)");
        }
        catch (Exception ex) { log.Add("mockups: FAILED " + ex); }

        // ---- a small copy of a video: under the size asked for ----
        try
        {
            string src = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Overskin-noisy.mp4");
            string dst = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Overskin-noisy-small.mp4");
            int w = 960, h = 540, fps = 30;
            var queue = new System.Collections.Concurrent.BlockingCollection<VideoFrame>(8);
            var encode = Task.Run(() => VideoEncoder.EncodeAsync(src, w, h, fps, queue));
            var rnd = new Random(2);
            for (int i = 0; i < fps * 5; i++)
            {
                var f = new byte[w * h * 4];
                rnd.NextBytes(f);
                queue.Add(new VideoFrame(f, TimeSpan.FromSeconds(i / (double)fps)));
            }
            queue.CompleteAdding();
            encode.GetAwaiter().GetResult();
            long before = new System.IO.FileInfo(src).Length, limit = 1024 * 1024;
            Task.Run(() => VideoEdit.RenderAsync(src, dst, new[] { (0.0, 5.0) }, Array.Empty<VideoOverlay>(), h, null, limit)).GetAwaiter().GetResult();
            long after = new System.IO.FileInfo(dst).Length;
            log.Add($"small video copy: {before / 1024} KB -> {after / 1024} KB (expect at most 1024 KB) passes: {string.Join(", ", VideoEdit.LastPasses.Select(b => b / 1024 + " KB"))}");
        }
        catch (Exception ex) { log.Add("small video copy: FAILED " + ex); }

        // ---- editable zooms: the zoomed video made from an unzoomed recording and its zoom track ----
        try
        {
            string raw = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Overskin-raw.mp4");
            string zoomed = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Overskin-zoomed.mp4");
            int w = 640, h = 360, fps = 30;
            var queue = new System.Collections.Concurrent.BlockingCollection<VideoFrame>(8);
            var encode = Task.Run(() => VideoEncoder.EncodeAsync(raw, w, h, fps, queue));
            for (int i = 0; i < fps * 5; i++)
            {
                var f = new byte[w * h * 4];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int o = (y * w + x) * 4;
                        bool right = x >= w / 2, bottom = y >= h / 2;
                        (byte r, byte g, byte b) c = !right && !bottom ? ((byte)230, (byte)30, (byte)30) : right && !bottom ? ((byte)30, (byte)230, (byte)30) : !right ? ((byte)30, (byte)30, (byte)230) : ((byte)240, (byte)240, (byte)240);
                        f[o] = c.b; f[o + 1] = c.g; f[o + 2] = c.r; f[o + 3] = 255;
                    }
                queue.Add(new VideoFrame(f, TimeSpan.FromSeconds(i / (double)fps)));
            }
            queue.CompleteAdding();
            encode.GetAwaiter().GetResult();
            var track = new ZoomTrack { Width = w, Height = h, Fps = fps, PanRate = 30 };
            for (int i = 0; i <= 50; i++) track.Pointer.Add(new[] { i * 0.1f, 480f, 90f });
            track.Zooms.Add(new ZoomSegment { Start = 1, End = 3.5, Zoom = 2.5 });
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Task.Run(() => ZoomTrack.RenderAsync(raw, zoomed, track, true)).GetAwaiter().GetResult();
            double length = Task.Run(() => VideoTrim.DurationAsync(zoomed)).GetAwaiter().GetResult().TotalSeconds;
            // the editor finds the zoom track of a video recorded with editable zooms
            string video = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Overskin-zoomable.mp4");
            System.IO.File.Copy(zoomed, video, true);
            System.IO.File.Copy(raw, ZoomTrack.RawPath(video), true);
            track.Save(video);
            var ed = new VideoEditorWindow(video, _ => { }) { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
            ed.Show();
            Pump(800);
            SaveWindow(ed, "video-editor-zooms.png");
            ed.Close();
            ZoomTrack.DeleteFor(video);
            log.Add($"editable zooms in the editor: track found={ZoomTrack.Load(video) == null} after removing (expect True)");
            log.Add($"editable zooms: made {length:0.0} s in {sw.ElapsedMilliseconds} ms (expect about 5.0) middle before={VideoPixel(zoomed, 0.3)} while zoomed={VideoPixel(zoomed, 2.5)} (expect a mix, then green)");
        }
        catch (Exception ex) { log.Add("editable zooms: FAILED " + ex); }

        // ---- tray menu: labels, and every submenu placed the same way ----
        try
        {
            var main = (MainWindow)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
            typeof(MainWindow).GetField("_settings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(main, new AppSettings());
            var headers = main.TrayHeadersForTest();
            var odd = headers.Where(h => h.Length > 0 && char.IsLower(h[0])).ToList();
            var named = new Workflow { Name = "text", CopyText = true, Copy = false, Save = false };
            named.Normalize();
            var custom = new Workflow { Name = "send to the team chat", Capture = "Window", Pin = true };
            custom.Normalize();
            log.Add($"workflow named \"text\" shows as \"{named.Title}\" (expect Copy text from an area); " +
                    $"a real name shows as \"{custom.Title}\" (expect Send to the team chat); window and pin describes as \"{custom.Describe()}\"");
            log.Add($"tray labels: {headers.Count} [{string.Join(" | ", headers)}] lowercase={odd.Count} (expect 0)");

            var item = new System.Windows.Controls.MenuItem { Header = "Capture" };
            var host = new Window { Width = 200, Height = 100, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false, Content = item };
            host.Show();
            DoEvents();
            var shortList = TrayIcon.SubmenuPlacements(item, new Size(200, 120), new Size(190, 30));
            var longList = TrayIcon.SubmenuPlacements(item, new Size(200, 420), new Size(190, 30));
            host.Close();
            // the same edge lines up whatever the height: the submenu's bottom item with the item's bottom (or tops when it opens down)
            double a1 = shortList[0].Point.Y + 120, b1 = longList[0].Point.Y + 420, a2 = shortList[0].Point.Y, b2 = longList[0].Point.Y;
            bool consistent = Math.Abs(a1 - b1) < 0.01 || Math.Abs(a2 - b2) < 0.01;
            log.Add($"tray submenus: short y={F(shortList[0].Point.Y)} tall y={F(longList[0].Point.Y)} same edge lined up={consistent} right of item x={F(shortList[0].Point.X)} (expect True, x near 182)");
        }
        catch (Exception ex) { log.Add("tray menu: FAILED " + ex); }
    }

    private static void SaveBgra(byte[] px, int w, int h, string path)
    {
        var bmp = System.Windows.Media.Imaging.BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px.Select((v, i) => i % 4 == 3 ? (byte)255 : v).ToArray(), w * 4);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var f = System.IO.File.Create(path);
        enc.Save(f);
    }
}
#endif
