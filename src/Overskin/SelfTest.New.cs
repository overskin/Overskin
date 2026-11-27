#if DEBUG
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Overskin;

/// <summary>
/// Self-tests for the fixes after the first try of 3.3: the Recent captures strip closing with Esc,
/// Spotlight, the AI models really being inside, the letter/number fixer, the swipe fade, the
/// phone page, the see-through window on Windows 10, and fine-tuning typed letters.
/// Run with --selftest log --more --new --no-mouse.
/// </summary>
public partial class App
{
    private static void NewFixTests(List<string> log)
    {
        void Pump(int ms)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += (_, _) => { t.Stop(); frame.Continue = false; };
            t.Start();
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }
        var host = new EditorHost { SaveNew = _ => null, Pin = (_, _) => { } };
        string dir = Path.Combine(Path.GetTempPath(), "overskin-newtests");
        Directory.CreateDirectory(dir);

        BitmapSource Grey(int w, int h, byte v) =>
            ImageLoader.Normalize(BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr24, null, Enumerable.Repeat(v, w * h * 3).ToArray(), w * 3));
        static byte Px(BitmapSource b, int x, int y)
        {
            var px = new byte[4];
            new FormatConvertedBitmap(b, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
            return px[1];
        }

        // ---- Recent captures: Esc closes it without the "can't close while closing" crash ----
        try
        {
            string file = Path.Combine(dir, "recent.png");
            ImageFiles.TrySave(Grey(64, 40, 120), file, out _);
            RecentStrip.Toggle(new[] { file }, _ => { });
            Pump(500);
            bool opened = RecentStrip.IsOpenForTest;
            string? error = null;
            try { RecentStrip.EscapeForTest(); Pump(300); }
            catch (Exception ex) { error = ex.Message; }
            // and the hotkey pressed again while it's open, then Esc
            RecentStrip.Toggle(new[] { file }, _ => { });
            Pump(300);
            try { RecentStrip.Toggle(new[] { file }, _ => { }); RecentStrip.EscapeForTest(); Pump(300); }
            catch (Exception ex) { error ??= ex.Message; }
            log.Add($"recent strip Esc: opened={opened} closed={!RecentStrip.IsOpenForTest} error={error ?? "none"} (expect True True none)");
        }
        catch (Exception ex) { log.Add("recent strip Esc: FAILED " + ex); }

        // ---- Spotlight: S picks it, a drag draws it, and everything else darkens ----
        try
        {
            var ed = new EditorWindow(Grey(200, 100, 200), null, "spot", host) { Left = -4000, Top = -4000, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
            ed.Show();
            Pump(400);
            ed.ToolKeyForTest(Key.S);
            string tool = ed.ToolForTest;
            ed.DragForTest(new Point(50, 25), new Point(150, 75));
            Pump(100);
            int marks = ed.MarkCountForTest;
            var shot = ed.Export();
            byte outside = Px(shot, 10, 10), inside = Px(shot, 100, 50);
            ed.CloseForTest();
            log.Add($"spotlight: key S tool={tool} (expect Spotlight) marks after drag={marks} (expect 1) outside={outside} (expect about 80) inside={inside} (expect 200)");
        }
        catch (Exception ex) { log.Add("spotlight: FAILED " + ex); }

        // ---- the AI models are inside, and work ----
        try { log.Add("ai: " + string.Join(" | ", AiModels.Check())); }
        catch (Exception ex) { log.Add("ai: FAILED " + ex); }

        // ---- letters and numbers Windows' reader mixes up ----
        try
        {
            var cases = new (string In, string Out)[]
            {
                ("HELL0", "HELLO"), ("w0rld", "world"), ("2O26", "2026"), ("1O", "10"), ("$1,2O0.5O", "$1,200.50"),
                ("B00K", "BOOK"), ("Wor1d", "World"), ("S0S", "SOS"),
                // left alone
                ("1st", "1st"), ("10am", "10am"), ("mp3", "mp3"), ("4K", "4K"), ("0x1F", "0x1F"), ("COVID-19", "COVID-19"),
                ("AB0C-1D2E", "AB0C-1D2E"), ("j0hn@mail.com", "j0hn@mail.com"), ("1080p", "1080p"), ("Hello", "Hello"), ("I", "I"), ("v2", "v2"),
            };
            var wrong = cases.Where(c => OcrFix.Word(c.In) != c.Out).Select(c => $"{c.In}->{OcrFix.Word(c.In)} (want {c.Out})").ToList();
            log.Add($"text fixes: {cases.Length - wrong.Count}/{cases.Length} right{(wrong.Count > 0 ? " WRONG: " + string.Join(", ", wrong) : "")} (expect all right)");
        }
        catch (Exception ex) { log.Add("text fixes: FAILED " + ex); }

        // ---- the box fits what's inside it, solid shapes included ----
        try
        {
            byte[] Page(Func<int, int, bool> ink)
            {
                var px = new byte[300 * 200 * 4];
                for (int y = 0; y < 200; y++)
                    for (int x = 0; x < 300; x++)
                    {
                        int i = (y * 300 + x) * 4;
                        bool on = ink(x, y);
                        px[i] = (byte)(on ? 200 : 240); px[i + 1] = (byte)(on ? 90 : 240); px[i + 2] = (byte)(on ? 40 : 240); px[i + 3] = 255;
                    }
                return px;
            }
            var solid = Page((x, y) => x >= 100 && x < 180 && y >= 60 && y < 110);
            var fitted = Tighten.Fit(solid, 300, 70, 30, 230, 150);
            var touching = Tighten.Fit(solid, 300, 120, 30, 230, 150);
            var empty = Tighten.Fit(Page((_, _) => false), 300, 70, 30, 230, 150);
            log.Add($"fit box: solid shape {fitted} (expect (100, 60, 180, 110)) touching the edge {touching} (expect (120, 60, 180, 110)) all background {empty} (expect (70, 30, 230, 150))");
        }
        catch (Exception ex) { log.Add("fit box: FAILED " + ex.Message); }

        // ---- swiping the preview left fades it all the way out ----
        log.Add($"preview left fade: at 0={CaptureNotice.LeftFade(0):0.00} 20={CaptureNotice.LeftFade(20):0.00} 80={CaptureNotice.LeftFade(80):0.00} 120={CaptureNotice.LeftFade(120):0.00} (expect 1.00 1.00 between 0.00)");

        // ---- the phone page offers Share and Save to Photos ----
        try
        {
            string file = Path.Combine(dir, "phone.png");
            ImageFiles.TrySave(Grey(64, 40, 90), file, out _);
            using var share = PhoneShare.Start(file);
            if (share == null) log.Add("phone page: no home network address here (skipped)");
            else
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                string page = http.GetStringAsync(share.Url).GetAwaiter().GetResult();
                var bytes = http.GetByteArrayAsync(share.Url + "/file").GetAwaiter().GetResult();
                log.Add($"phone page: share button={page.Contains("id=\"share\"")} save to photos={page.Contains("Save to Photos")} share sheet={page.Contains("navigator.share")} " +
                        $"file={bytes.Length == new FileInfo(file).Length} (expect True True True True)");
            }
        }
        catch (Exception ex) { log.Add("phone page: FAILED " + ex.Message); }

        // ---- the see-through window works on Windows 10 too ----
        log.Add($"see-through: supported={WindowLook.GlassSupported} windows 11={WindowLook.IsWindows11} build={Environment.OSVersion.Version.Build} (expect supported True from build 17134)");

        // ---- the see-through window's outline follows its rounded corners ----
        try
        {
            var pts = WindowLook.Outline(new Rect(14, 14, 400, 300), new CornerRadius(16, 3, 16, 3), 1);
            int minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X), minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
            bool cornerCut = !pts.Any(p => p.X == 14 && p.Y == 14); // the round top-left corner has no square point
            bool squareish = pts.Any(p => Math.Abs(p.X - 414) <= 3 && Math.Abs(p.Y - 14) <= 3);
            log.Add($"see-through outline: {pts.Length} points box {minX},{minY} to {maxX},{maxY} round corner cut={cornerCut} small corner kept={squareish} (expect 14,14 to 414,314 True True)");
        }
        catch (Exception ex) { log.Add("see-through outline: FAILED " + ex.Message); }

        // ---- the countdown runs and can be called off (shown off-screen) ----
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var run = RecordCountdown.Run(new Int32Rect(-5000, -5000, 600, 400), 1);
            while (!run.IsCompleted && sw.ElapsedMilliseconds < 5000) Pump(50);
            bool went = run.IsCompleted && run.Result;
            long ms = sw.ElapsedMilliseconds;
            var run2 = RecordCountdown.Run(new Int32Rect(-5000, -5000, 600, 400), 3);
            Pump(200);
            RecordCountdown.Cancel();
            while (!run2.IsCompleted && sw.ElapsedMilliseconds < 10000) Pump(50);
            log.Add($"countdown: one second finished={went} in {ms} ms (expect True, about 1000-1300), called off={run2.IsCompleted && !run2.Result} (expect True), default on={new AppSettings().CountdownFirst} (expect False)");
        }
        catch (Exception ex) { log.Add("countdown: FAILED " + ex); }

        // ---- typed letters: font, size and weight can be changed, and put back ----
        try
        {
            var face = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            FormattedText F(string t) => new(t, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 24, Brushes.Black, 1.0);
            var v = new DrawingVisual();
            using (var dc = v.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 320, 60));
                dc.DrawText(F("Hello there"), new Point(12, 12));
            }
            var rtb = new RenderTargetBitmap(320, 60, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(v);
            var hello = F("Hello").BuildGeometry(new Point(12, 12)).Bounds;
            var there = F("there").BuildGeometry(new Point(12 + F("Hello ").WidthIncludingTrailingWhitespace, 12)).Bounds;
            var ed = new EditorWindow(ImageLoader.Normalize(rtb), null, "tune", host) { Left = -4000, Top = -4000, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
            ed.Show();
            Pump(300);
            var words = new List<OcrWord> { new("Hello", hello), new("there", there) };
            ed.SelectWordsForTest(words, 0, 0, new[] { 0, 0 });
            var matched = ed.MatchLineForTest(0).Style;
            ed.TuneForTest("bigger");
            var bigger = ed.TypingStyleForTest(0);
            ed.TuneForTest("regular");
            ed.TuneForTest("georgia");
            var changed = ed.TypingStyleForTest(0);
            ed.TuneForTest("reset");
            var back = ed.TypingStyleForTest(0);
            ed.CloseForTest();
            log.Add($"type tuning: matched={matched?.Name} {matched?.Size:0.#} bigger={bigger?.Size:0.#} then {changed?.Name} " +
                    $"reset={back?.Name} {back?.Size:0.#} (expect a Segoe UI match, one step bigger, then Georgia, then the match again)");

            // a changed font redraws the selected word in it, rather than only what's typed next
            var ed2 = new EditorWindow(ImageLoader.Normalize(rtb), null, "restyle", host) { Left = -4000, Top = -4000, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
            ed2.Show();
            Pump(300);
            ed2.SelectWordsForTest(new List<OcrWord> { new("Hello", hello), new("there", there) }, 1, 1, new[] { 0, 0 });
            var before = ed2.BaseForTest;
            ed2.TuneForTest("georgia");
            Pump(2500);
            bool redrawn = !ReferenceEquals(before, ed2.BaseForTest);
            string text = ed2.TextForTest;
            ed2.CloseForTest();
            log.Add($"restyle selected: picture redrawn={redrawn} text=\"{text}\" (expect True, Hello there)");
        }
        catch (Exception ex) { log.Add("type tuning: FAILED " + ex); }

        // ---- deleting a word leaves nothing behind, even when the reader's boxes cut off its tops and tails ----
        try
        {
            var results = new List<string>();
            foreach (var (font, size, weight) in new[] { ("Segoe UI", 22.0, FontWeights.Normal), ("Georgia", 26.0, FontWeights.Normal), ("Arial", 18.0, FontWeights.Bold) })
            {
                var face = new Typeface(new FontFamily(font), FontStyles.Normal, weight, FontStretches.Normal);
                FormattedText F(string t) => new(t, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, Brushes.Black, 1.0);
                const int W = 420, H = 70;
                var v = new DrawingVisual();
                using (var dc = v.RenderOpen())
                {
                    dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, W, H));
                    dc.DrawText(F("Typography is jumpy"), new Point(10, 14));
                }
                var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(v);
                Rect Box(string before, string word)
                {
                    double x = 10 + F(before).WidthIncludingTrailingWhitespace;
                    var b = F(word).BuildGeometry(new Point(x, 14)).Bounds;
                    // like the reader: a box that misses the tops of tall letters and the tails
                    double cut = b.Height * 0.18;
                    return new Rect(b.X, b.Y + cut, b.Width, Math.Max(2, b.Height - 2 * cut));
                }
                var words = new List<OcrWord> { new("Typography", Box("", "Typography")), new("is", Box("Typography ", "is")), new("jumpy", Box("Typography is ", "jumpy")) };
                var ed = new EditorWindow(ImageLoader.Normalize(rtb), null, "lines", host) { Left = -4000, Top = -4000, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
                ed.Show();
                Pump(250);
                ed.SelectWordsForTest(words, 2, 2, new[] { 0, 0, 0 });
                var task = ed.DeleteSelectionForTest();
                while (!task.IsCompleted) Pump(30);
                var after = new FormatConvertedBitmap(ed.BaseForTest, PixelFormats.Bgra32, null, 0);
                var px = new byte[W * H * 4];
                after.CopyPixels(px, W * 4, 0);
                ed.CloseForTest();
                ImageFiles.TrySave(after, Path.Combine(dir, $"leftover-{font}.png"), out _);
                // everything right of "is" should be white again, top to bottom
                int from = (int)Math.Ceiling(F("Typography is").BuildGeometry(new Point(10, 14)).Bounds.Right) + 3, left = 0;
                for (int y = 0; y < H; y++)
                    for (int x = from; x < W; x++)
                        if (px[(y * W + x) * 4 + 1] < 200) left++;
                results.Add($"{font} {weight} {left} px");
            }
            log.Add($"leftover lines after deleting a word: {string.Join(", ", results)} (expect 0 px each)");
        }
        catch (Exception ex) { log.Add("leftover lines: FAILED " + ex); }

        // ---- the fonts that come with Overskin load in each weight, and text drawn in them is recognised ----
        try
        {
            var widths = new List<string>();
            foreach (var (name, weights) in FontLibrary.MatchingFonts)
                foreach (var wt in weights)
                {
                    var tf = new Typeface(FontLibrary.Bundled(name), FontStyles.Normal, wt, FontStretches.Normal);
                    bool real = tf.TryGetGlyphTypeface(out var gt) && gt.Weight == wt;
                    double width = new FormattedText("Settings", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, 40, Brushes.Black, 1.0).Width;
                    widths.Add($"{name} {wt}={width:0}{(real ? "" : " (NOT ITS OWN)")}");
                }
            var found = new List<string>();
            foreach (var (name, weight, size) in new[] { ("Inter", FontWeights.Normal, 16.0), ("Inter", FontWeights.SemiBold, 18.0), ("Roboto", FontWeights.Normal, 15.0), ("Roboto", FontWeights.Normal, 20.0), ("Roboto", FontWeights.Medium, 16.0), ("Roboto", FontWeights.Bold, 17.0) })
            {
                var face = new Typeface(FontLibrary.Bundled(name), FontStyles.Normal, weight, FontStretches.Normal);
                FormattedText F(string t) => new(t, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, Brushes.Black, 1.0);
                var v = new DrawingVisual();
                using (var dc = v.RenderOpen())
                {
                    dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 420, 60));
                    dc.DrawText(F("Settings and privacy"), new Point(10, 14));
                }
                var rtb = new RenderTargetBitmap(420, 60, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(v);
                var words = new List<OcrWord>();
                double x = 10;
                foreach (var word in new[] { "Settings", "and", "privacy" })
                {
                    words.Add(new(word, F(word).BuildGeometry(new Point(x, 14)).Bounds));
                    x += F(word + " ").WidthIncludingTrailingWhitespace;
                }
                var ed = new EditorWindow(ImageLoader.Normalize(rtb), null, "font", host) { Left = -4000, Top = -4000, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
                ed.Show();
                Pump(200);
                ed.SelectWordsForTest(words, 0, 0, new[] { 0, 0, 0 });
                var (style, ms) = ed.MatchLineForTest(0);
                ed.CloseForTest();
                found.Add($"{name} {weight} {size}->{style?.Name} {style?.Size:0.#} in {ms:0} ms [{EditorWindow.LastRankForTest}]");
            }
            log.Add($"bundled fonts: {string.Join(", ", widths)} (expect each weight wider than the last, none NOT ITS OWN); recognised: {string.Join("; ", found)} (expect each the same font and weight)");
        }
        catch (Exception ex) { log.Add("bundled fonts: FAILED " + ex); }

        // ---- a cut-out stays see-through: copied and saved with its clear background, shown on a checkerboard ----
        try
        {
            var px = new byte[40 * 30 * 4];
            for (int i = 0; i < px.Length; i += 4) { px[i] = 50; px[i + 1] = 90; px[i + 2] = 200; px[i + 3] = (byte)(i / 4 % 40 < 20 ? 255 : 0); }
            var clear = BitmapSource.Create(40, 30, 96, 96, PixelFormats.Bgra32, null, px, 160);
            clear.Freeze();
            bool seen = ImageFiles.HasSeeThrough(clear), solid = ImageFiles.HasSeeThrough(Grey(40, 30, 10));
            var ed = new EditorWindow(clear, null, "cut", host) { Left = -4000, Top = -4000, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
            ed.Show();
            Pump(300);
            var shot = ed.Export();
            var a = new byte[4];
            new FormatConvertedBitmap(shot, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(35, 15, 1, 1), a, 4, 0);
            bool checker = ed.CheckerShownForTest;
            ed.CloseForTest();
            string file = Path.Combine(dir, "cut.png");
            ImageFiles.TrySave(shot, file, out _);
            var back = ImageLoader.LoadFile(file);
            var b = new byte[4];
            new FormatConvertedBitmap(back, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(35, 15, 1, 1), b, 4, 0);
            // soft edges take the subject's colour, not the old background's
            var edge = new byte[5 * 1 * 4];
            for (int x = 0; x < 5; x++) { edge[x * 4] = 0; edge[x * 4 + 1] = 0; edge[x * 4 + 2] = 255; edge[x * 4 + 3] = 255; } // red subject
            edge[3 * 4] = 0; edge[3 * 4 + 1] = 255; edge[3 * 4 + 2] = 0; edge[3 * 4 + 3] = 120;                                // a green-tinged soft edge
            edge[4 * 4 + 3] = 0;                                                                                                    // fully clear
            AiModels.Defringe(edge, 5, 1);
            log.Add($"cut-out edges: soft edge colour R{edge[3 * 4 + 2]} G{edge[3 * 4 + 1]} alpha {edge[3 * 4 + 3]} clear pixel R{edge[4 * 4 + 2]} (expect R255 G0 alpha 120, clear R0)");
            log.Add($"cut-out background: see-through found={seen} in a solid one={solid} checkerboard shown={checker} exported alpha={a[3]} saved alpha={b[3]} (expect True False True 0 0)");
        }
        catch (Exception ex) { log.Add("cut-out background: FAILED " + ex); }
    }
}
#endif
