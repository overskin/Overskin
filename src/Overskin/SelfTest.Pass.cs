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
