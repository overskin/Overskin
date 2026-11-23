#if DEBUG
using System.Windows;

namespace Overskin;

/// <summary>Self-tests for the 2.6 features: GIF trimming, video trimming, sound, faster capture,
/// webcam, watermarks, workflows, guide export and compare pins.</summary>
public partial class App
{
    /// <summary>Runs the dispatcher for a while, so timers tick.</summary>
    private static void Pump(int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { DoEvents(); Thread.Sleep(10); }
    }

    private static void MoreFeatureTests(List<string> log)
    {
        // ---- GIF trim: dragging a handle pauses playback, shows that edge, and never crosses ----
        try
        {
            var clip = new GifClip { Width = 40, Height = 30 };
            for (int i = 0; i < 30; i++) { clip.Frames.Add(new byte[40 * 30 * 4]); clip.DelaysMs.Add(40); }
            var w = new GifEditorWindow(clip, "test", new GifEditorHost { Folder = System.IO.Path.GetTempPath(), Saved = _ => { } })
            { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
            w.Show();
            Pump(150);
            bool playingAtStart = w.IsPlaying;
            GifEditorWindow.MouseHeld = () => true; // a handle being dragged
            w.TrimStart.Value = 10;
            Pump(120); // the play timer would have moved on three frames by now
            int shownWhileDragging = w.ShownFrame;
            bool pausedWhileDragging = !w.IsPlaying;
            w.TrimStart.Value = 12;
            Pump(60);
            int shownAfterSecondMove = w.ShownFrame;
            GifEditorWindow.MouseHeld = () => false; // let go: playback resumes inside the new range
            Pump(500);
            bool resumed = w.IsPlaying;
            int shownAfter = w.ShownFrame;
            w.TrimEnd.Value = 5; // tries to pass the start handle
            var range = w.TrimRange;
            w.Scrubber.Value = 2; // before the trimmed range
            Pump(30);
            int scrubbed = w.ShownFrame;
            bool pausedAfterScrub = !w.IsPlaying;
            w.Close();
            GifEditorWindow.MouseHeld = () => System.Windows.Input.Mouse.LeftButton == System.Windows.Input.MouseButtonState.Pressed;
            log.Add($"gif trim: playing={playingAtStart} whileDragging frame={shownWhileDragging} paused={pausedWhileDragging} (expect True 10 True) " +
                    $"second={shownAfterSecondMove} (expect 12) resumed={resumed} frame={shownAfter} (expect True, 12..29) " +
                    $"crossed range={range.First}..{range.Last} (expect 12..12) scrub={scrubbed} paused={pausedAfterScrub} (expect 12 True)");
        }
        catch (Exception ex) { log.Add("gif trim: FAILED " + ex.Message); }

        // ---- video trim: 3 s of red, green, blue; keep 1.0 to 2.0 s, which should be all green ----
        try
        {
            string src = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Overskin-trim-src.mp4");
            string dst = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Overskin-trim-out.mp4");
            MakeTestVideo(src, 3, null);
            Task.Run(() => VideoTrim.TrimAsync(src, dst, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2))).GetAwaiter().GetResult();
            double seconds = Task.Run(() => VideoTrim.DurationAsync(dst)).GetAwaiter().GetResult().TotalSeconds;
            string mid = VideoPixel(dst, 0.5);
            string named = System.IO.Path.GetFileName(VideoTrim.TrimmedName(src));

            // the window: handles can't cross, and the range follows them
            var w = new VideoEditorWindow(src, _ => { }) { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
            w.Show();
            Pump(1500);
            w.TrimStart.Value = 2;
            w.TrimEnd.Value = 1;
            var range = w.TrimRange;
            w.TrimStart.Value = 0.6;
            w.TrimEnd.Value = 2.4;
            SaveWindow(w, "video-editor.png");
            w.TrimStart.Value = 2;
            w.TrimEnd.Value = 1;
            range = w.TrimRange;
            w.Close();
            System.IO.File.Delete(src);
            System.IO.File.Delete(dst);
            log.Add($"video trim: duration={seconds:0.0}s (expect 1.0) middle={mid} (expect green) name={named} (expect Overskin-trim-src trimmed.mp4) " +
                    $"window range={range.Start:0.#}..{range.End:0.#} (expect 2..2)");
        }
        catch (Exception ex) { log.Add("video trim: FAILED " + ex.GetType().Name + " " + ex.Message); }

        // ---- sound: a video with a 440 Hz tone has an AAC track, and keeps it when trimmed ----
        try
        {
            string src = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Overskin-sound.mp4");
            string cut = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Overskin-sound-cut.mp4");
            MakeTestVideo(src, 2, chunk =>
            {
                var s = new short[960];
                for (int i = 0; i < 480; i++)
                {
                    double t = (chunk * 480 + i) / 48000.0;
                    s[i * 2] = s[i * 2 + 1] = (short)(Math.Sin(2 * Math.PI * 440 * t) * 12000);
                }
                return s;
            });
            string Track(string p) => Task.Run(async () =>
            {
                var file = await global::Windows.Storage.StorageFile.GetFileFromPathAsync(p);
                var profile = await global::Windows.Media.MediaProperties.MediaEncodingProfile.CreateFromFileAsync(file);
                return profile.Audio == null ? "none" : $"{profile.Audio.Subtype} {profile.Audio.SampleRate}Hz {profile.Audio.ChannelCount}ch";
            }).GetAwaiter().GetResult();
            string before = Track(src);
            double length = Task.Run(() => VideoTrim.DurationAsync(src)).GetAwaiter().GetResult().TotalSeconds;
            Task.Run(() => VideoTrim.TrimAsync(src, cut, TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1.5))).GetAwaiter().GetResult();
            string after = Track(cut);
            System.IO.File.Delete(src);
            System.IO.File.Delete(cut);
            log.Add($"sound: track={before} length={length:0.0}s (expect AAC 48000Hz 2ch, 2.0s) trimmed track={after} (expect the same)");
        }
        catch (Exception ex) { log.Add("sound: FAILED " + ex.GetType().Name + " " + ex.Message); }

        // ---- system sound opens and delivers (silence counts); kept in memory and thrown away ----
        try
        {
            using var speakers = AudioSource.Open(loopback: true);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var chunks = new System.Collections.Concurrent.BlockingCollection<AudioChunk>(1000);
            using (var mixer = new AudioMixer(clock, new[] { (speakers, 1f) }, chunks)) { Thread.Sleep(500); mixer.Finish(); }
            int count = chunks.Count;
            var first = chunks.TryTake(out var c) ? c.Time.TotalMilliseconds : -1;
            log.Add($"system sound: opened={speakers.Name} chunks in 0.5 s={count} (expect ~50) first at {first:0}ms (expect 0) microphones listed={AudioSource.Microphones().Count}");
        }
        catch (Exception ex) { log.Add("system sound: FAILED " + ex.Message); }

        // ---- webcam bubble: a white camera picture lands as a circle in the bottom-right corner ----
        try
        {
            int w = 400, h = 200;
            var frame = new byte[w * h * 4];
            var cam = Enumerable.Repeat((byte)255, 64 * 48 * 4).ToArray();
            var o = new VideoOptions { Webcam = true, WebcamCorner = "BottomRight", WebcamShape = "Circle", WebcamSize = 40 };
            WebcamBubble.Draw(frame, w, h, cam, 64, 48, o);
            var (bx, by, size) = WebcamBubble.Place(w, h, o);
            string At(int x, int y) => frame[(y * w + x) * 4] > 128 ? "white" : "black";
            log.Add($"webcam bubble: at {bx},{by} size {size} (expect 312,112 size 80) centre={At(bx + size / 2, by + size / 2)} " +
                    $"corner of its box={At(bx + 2, by + 2)} top-left of frame={At(5, 5)} (expect white black black)");
        }
        catch (Exception ex) { log.Add("webcam bubble: FAILED " + ex.Message); }

        // ---- watermark: white text in the bottom-right, nothing at the top-left; the video layer agrees ----
        try
        {
            var grey = Solid(400, 200, 0x60);
            var o = new WatermarkOptions { Kind = "Text", Text = "OVERSKIN", Position = "BottomRight", Size = 20, Opacity = 100, Color = "White" };
            var stamped = Watermark.Apply(grey, o);
            var px = Pixels(stamped);
            int Brightest(byte[] p, int x0, int y0, int x1, int y1, int w)
            {
                int best = 0;
                for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) best = Math.Max(best, p[(y * w + x) * 4 + 1]);
                return best;
            }
            int corner = Brightest(px, 200, 140, 400, 200, 400), topLeft = Brightest(px, 0, 0, 100, 60, 400);
            var frame = new byte[400 * 200 * 4];
            for (int i = 0; i < frame.Length; i += 4) { frame[i] = frame[i + 1] = frame[i + 2] = 0x60; frame[i + 3] = 255; }
            Watermark.Blend(frame, Watermark.Layer(o, 400, 200)!);
            int videoCorner = Brightest(frame, 200, 140, 400, 200, 400);
            var tiledOptions = o.Copy();
            tiledOptions.Position = "Tiled";
            var tiled = Pixels(Watermark.Apply(grey, tiledOptions));
            int tiledTopLeft = Brightest(tiled, 0, 0, 200, 100, 400);
            log.Add($"watermark: bottom-right brightest={corner} top-left={topLeft} (expect >200, 96) video layer bottom-right={videoCorner} (expect >200) " +
                    $"tiled reaches the top-left={tiledTopLeft > 200} (expect True) empty text stamps nothing={!new WatermarkOptions().HasContent} (expect True)");
        }
        catch (Exception ex) { log.Add("watermark: FAILED " + ex.Message); }

        // ---- workflows: steps follow the switches, the summary reads well, and they survive saving ----
        try
        {
            var w = new Workflow { Name = "Share", Hotkey = "Ctrl+Alt+1", Capture = "Window", Delay = 3, AutoHide = true, Watermark = true, Save = false, Copy = true, CopyText = true };
            var s = w.Steps;
            var settings = new AppSettings();
            settings.Workflows.Add(w);
            var back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(settings))!;
            var odd = new Workflow { Name = "  ", Capture = "Moon", Delay = 7 };
            odd.Normalize();
            log.Add($"workflow: summary=\"{w.Summary}\" copy={s.Copy} copyText={s.CopyText} (expect False True) " +
                    $"saved and read back: {back.Workflows.Count} \"{back.Workflows[0].Name}\" {back.Workflows[0].Hotkey} {back.Workflows[0].Capture} " +
                    $"(expect 1 \"Share\" Ctrl+Alt+1 Window) cleaned up: \"{odd.Name}\" {odd.Capture} {odd.Delay} (expect \"Workflow\" Region 0)");
        }
        catch (Exception ex) { log.Add("workflow: FAILED " + ex.Message); }

        // ---- steps guide: HTML, Markdown and a PDF that Windows itself can open ----
        try
        {
            var steps = new List<RecordedStep>
            {
                new() { Shot = Solid(500, 300, 0xE0), At = new Point(120, 80), Right = false, Window = "Notepad", Target = "Save", TargetIsText = false },
                new() { Shot = Solid(500, 300, 0xC0), At = new Point(300, 200), Right = true, Window = "Explorer", Target = "“Shop” (now)", TargetIsText = true, Text = "Pick \"Open with\" from the menu" },
            };
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Overskin-guide-test");
            if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
            System.IO.Directory.CreateDirectory(dir);
            var accent = System.Windows.Media.Color.FromRgb(0x4F, 0x8C, 0xFF);
            string html = System.IO.Path.Combine(dir, "guide.html"), md = System.IO.Path.Combine(dir, "guide.md"), pdf = System.IO.Path.Combine(dir, "guide.pdf");
            StepGuide.SaveHtml(steps, "How to save", accent, html);
            StepGuide.SaveMarkdown(steps, "How to save", accent, md);
            StepGuide.SavePdf(steps, "How to save", accent, pdf);
            string htmlText = System.IO.File.ReadAllText(html);
            int pictures = System.Text.RegularExpressions.Regex.Matches(htmlText, "data:image/png;base64").Count;
            bool edited = htmlText.Contains("Pick &quot;Open with&quot; from the menu");
            string mdText = System.IO.File.ReadAllText(md);
            int mdPictures = System.IO.Directory.GetFiles(System.IO.Path.Combine(dir, "guide images")).Length;
            var (pages, pageW, pageH) = Task.Run(async () =>
            {
                using var stream = System.IO.File.OpenRead(pdf);
                using var random = System.IO.WindowsRuntimeStreamExtensions.AsRandomAccessStream(stream);
                var doc = await global::Windows.Data.Pdf.PdfDocument.LoadFromStreamAsync(random);
                using var page = doc.GetPage(0);
                // OVERSKIN_TEST_OUT: keep a picture of the page to look at (made-up content only)
                if (Environment.GetEnvironmentVariable("OVERSKIN_TEST_OUT") is { Length: > 0 } outDir)
                {
                    using var file = System.IO.File.Create(System.IO.Path.Combine(outDir, "guide-page1.png"));
                    using var png = System.IO.WindowsRuntimeStreamExtensions.AsRandomAccessStream(file);
                    await page.RenderToStreamAsync(png);
                    System.IO.File.Copy(html, System.IO.Path.Combine(outDir, "guide.html"), true);
                }
                return ((int)doc.PageCount, page.Size.Width, page.Size.Height);
            }).GetAwaiter().GetResult();
            try { System.IO.Directory.Delete(dir, true); } catch { }
            log.Add($"steps guide: html pictures={pictures} edited sentence kept={edited} (expect 2 True) markdown pictures={mdPictures} " +
                    $"links={mdText.Contains("guide%20images/step-02.png")} (expect 2 True) pdf pages={pages} size={pageW:0}x{pageH:0} (expect 2 1123x793: A4 landscape in screen units) " +
                    $"pdf text escaping={StepGuide.Pdf("a (b) “c” é")} (expect a \\(b\\) \\223c\\224 \\351)");
        }
        catch (Exception ex) { log.Add("steps guide: FAILED " + ex.GetType().Name + " " + ex.Message); }

        // ---- steps window: drop a step, and the heading follows the title box ----
        try
        {
            var steps = new List<RecordedStep>
            {
                new() { Shot = Solid(200, 120, 0xE0), At = new Point(20, 20), Right = false, Window = "A" },
                new() { Shot = Solid(200, 120, 0xE0), At = new Point(40, 40), Right = false, Window = "B" },
            };
            var w = new StepsWindow(steps, System.Windows.Media.Colors.SteelBlue, System.IO.Path.GetTempPath(), (_, _) => { }, _ => { })
            { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
            w.Show();
            Pump(200);
            var trash = FindAll<System.Windows.Controls.Button>(w).First(b => (b.ToolTip as string) == "Leave this step out");
            trash.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Pump(50);
            int left = w.Steps.Count;
            string heading = w.Heading;
            SaveWindow(w, "steps-window.png");
            w.Close();
            log.Add($"steps window: after deleting one, {left} left (expect 1) heading=\"{heading}\" (expect \"Steps\")");
        }
        catch (Exception ex) { log.Add("steps window: FAILED " + ex.Message); }

        // ---- compare: red before, blue after; the divider at a quarter shows red left of it and blue right ----
        try
        {
            var red = SolidColour(400, 200, 0, 0, 230);
            var blue = SolidColour(400, 200, 230, 0, 0);
            var w = new CompareWindow(red, blue, "before.png", "after.png") { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false, Width = 402, Height = 242 };
            w.Show();
            Pump(200);
            w.Split = 0.25;
            Pump(50);
            var content = (FrameworkElement)w.Content;
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(content);
            var px = Pixels(rtb);
            int cw = rtb.PixelWidth;
            string At(int x, int y) { int o = (y * cw + x) * 4; return px[o + 2] > 150 && px[o] < 80 ? "red" : px[o] > 150 && px[o + 2] < 80 ? "blue" : px[o] > 80 && px[o + 2] > 80 ? "purple" : $"r{px[o + 2]}b{px[o]}"; }
            int midY = 40 + (rtb.PixelHeight - 40) / 2;
            string left = At(cw / 10, midY + 30), right = At(cw * 3 / 4, midY + 30);
            w.Split = 0.9;
            Pump(30);
            rtb.Render(content);
            px = Pixels(rtb);
            string moved = At(cw * 3 / 4, midY + 30);
            w.Split = 0.5;
            SaveWindow(w, "compare.png");
            w.Close();
            log.Add($"compare: left of divider={left} right={right} (expect red blue) after moving the divider right, that spot={moved} (expect red)");
        }
        catch (Exception ex) { log.Add("compare: FAILED " + ex.Message); }
        try { WorkflowDialogPicture(); } catch (Exception ex) { log.Add("workflow dialog: FAILED " + ex.Message); }

        // ---- capture bar Pick: the click takes a colour; it must not capture or close the overlay ----
        try
        {
            var lime = SolidColour(400, 300, 0, 230, 120);
            var overlay = CaptureOverlay.ForTest(lime, new NativeMethods.RECT { Left = 0, Top = 0, Right = 400, Bottom = 300 });
            bool closed = false;
            overlay.Closed += (_, _) => closed = true;
            var bar = overlay.ToolbarForTest!;
            overlay.BeginPickColour();
            bool picking = overlay.IsPickingColour;
            var down = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent };
            var up = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent };
            overlay.RaiseEvent(down);
            overlay.RaiseEvent(up);
            var colour = bar.Colour;
            log.Add($"capture bar pick: picking={picking} (expect True) colour={Theming.ToHex(colour)} (expect #78E600) " +
                    $"captured={overlay.Selection != null} closed={closed} still picking={overlay.IsPickingColour} (expect False False False)");
            overlay.Close();
        }
        catch (Exception ex) { log.Add("capture bar pick: FAILED " + ex.Message); }

        // ---- live text by the letter: drag within a word, across words, and erase only the letters picked ----
        try
        {
            int tw = 420, th = 90;
            const double Size = 40, OriginX = 20, OriginY = 18;
            var face = new System.Windows.Media.Typeface("Segoe UI");
            System.Windows.Media.FormattedText Ft(string s) => new(s, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, Size, System.Windows.Media.Brushes.Black, 1.0);
            var dv = new System.Windows.Media.DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(System.Windows.Media.Brushes.White, null, new Rect(0, 0, tw, th));
                dc.DrawText(Ft("Hello there"), new Point(OriginX, OriginY));
            }
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(tw, th, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(dv);
            var page = ImageLoader.Normalize(rtb);
            var lines = Task.Run(() => TextRecognizer.RecognizeLinesAsync(page)).GetAwaiter().GetResult();
            var words = lines?.SelectMany(l => l).OrderBy(w => w.Bounds.Left).ToList() ?? new();
            var ed = new EditorWindow(page, null, "letters", new EditorHost { SaveNew = _ => null, Pin = (_, _) => { } });
            ed.SelectWordsForTest(words, 0, 0);
            double midY = words.Count > 0 ? words[0].Bounds.Top + words[0].Bounds.Height / 2 : 0;
            var e0 = ed.EdgesForTest(0);

            // how far the guessed gaps are from where Segoe UI really put them
            double worst = 0;
            for (int k = 1; k < "Hello".Length; k++)
                worst = Math.Max(worst, Math.Abs(e0[k] - (OriginX + Ft("Hello"[..k]).WidthIncludingTrailingWhitespace)));

            string inside = ed.DragOverTextForTest(new Point(e0[1] + 1, midY), new Point(e0[4] - 1, midY));
            var e1 = ed.EdgesForTest(1);
            string across = ed.DragOverTextForTest(new Point(e0[2] + 1, midY), new Point(e1[3] + 1, midY));
            string click = ed.DragOverTextForTest(new Point((e1[2] + e1[3]) / 2 + 1, midY), new Point((e1[2] + e1[3]) / 2 + 1, midY));
            string clickCaret = ed.CaretForTest;
            string dbl = ed.DoubleClickForTest(new Point((e1[0] + e1[5]) / 2, midY));

            // erase "ell": the H and the o stay, the middle goes white
            ed.DragOverTextForTest(new Point(e0[1] + 1, midY), new Point(e0[4] - 1, midY));
            var erase = ed.EraseSelectedForTest();
            for (int spin = 0; !erase.IsCompleted && spin < 300; spin++) { DoEvents(); Thread.Sleep(20); }
            var after = Pixels(ed.BaseForTest);
            int Darkest(double x0, double x1)
            {
                int best = 255;
                for (int y = (int)words[0].Bounds.Top; y < (int)words[0].Bounds.Bottom; y++)
                    for (int x = (int)Math.Ceiling(x0); x < (int)x1; x++) best = Math.Min(best, (int)after[(y * tw + x) * 4 + 1]);
                return best;
            }
            int h = Darkest(e0[0] + 2, e0[1] - 3), middle = Darkest(e0[1] + 3, e0[4] - 3), o = Darkest(e0[4] + 3, e0[5] - 2);
            if (Environment.GetEnvironmentVariable("OVERSKIN_TEST_OUT") is { Length: > 0 } letterOut)
            {
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(ed.BaseForTest));
                using var f = System.IO.File.Create(System.IO.Path.Combine(letterOut, "letters-erased.png"));
                enc.Save(f);
            }
            ed.CloseForTest();

            // the cursor: click between e and l of Hello, Backspace takes the e and the rest of the line closes up;
            // then Backspace at the start of "there" takes the space
            var ed2 = new EditorWindow(page, null, "letters", new EditorHost { SaveNew = _ => null, Pin = (_, _) => { } });
            ed2.SelectWordsForTest(words, 0, 0);
            var f0 = ed2.EdgesForTest(0);
            var del = ed2.ClickAndDeleteForTest(new Point(f0[2] + 0.4, midY), back: true);
            for (int spin = 0; !del.IsCompleted && spin < 300; spin++) { DoEvents(); Thread.Sleep(20); }
            string afterBack = ed2.WordsForTest + " caret " + ed2.CaretForTest;
            var g1 = ed2.EdgesForTest(1);
            del = ed2.ClickAndDeleteForTest(new Point(g1[0] + 0.3, midY), back: true);
            for (int spin = 0; !del.IsCompleted && spin < 300; spin++) { DoEvents(); Thread.Sleep(20); }
            string afterSpace = ed2.WordsForTest;
            var edited = ed2.BaseForTest;
            var reread = Task.Run(() => TextRecognizer.RecognizeLinesAsync(ImageLoader.Normalize(edited))).GetAwaiter().GetResult();
            string readBack = string.Join(" ", reread?.SelectMany(l => l).Select(x => x.Text) ?? Array.Empty<string>());
            if (Environment.GetEnvironmentVariable("OVERSKIN_TEST_OUT") is { Length: > 0 } editOut)
            {
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(edited));
                using var f = System.IO.File.Create(System.IO.Path.Combine(editOut, "letters-deleted.png"));
                enc.Save(f);
            }
            ed2.CloseForTest();

            // other fonts, straight from the ink: Georgia and Consolas, compared with where the font put each letter
            string Fonts()
            {
                var res = new List<string>();
                foreach (var (fontName, word) in new[] { ("Georgia", "Waffles"), ("Consolas", "minimum"), ("Times New Roman", "Typewriter") })
                {
                    var tf = new System.Windows.Media.Typeface(fontName);
                    System.Windows.Media.FormattedText F(string t) => new(t, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, 30, System.Windows.Media.Brushes.Black, 1.0);
                    var v = new System.Windows.Media.DrawingVisual();
                    using (var dc = v.RenderOpen())
                    {
                        dc.DrawRectangle(System.Windows.Media.Brushes.White, null, new Rect(0, 0, 300, 70));
                        dc.DrawText(F(word), new Point(15, 15));
                    }
                    var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(300, 70, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    bmp.Render(v);
                    var ink = F(word).BuildGeometry(new Point(15, 15)).Bounds;
                    var e3 = new EditorWindow(ImageLoader.Normalize(bmp), null, "font", new EditorHost { SaveNew = _ => null, Pin = (_, _) => { } });
                    e3.SelectWordsForTest(new List<OcrWord> { new(word, ink) }, 0, 0);
                    var edges = e3.EdgesForTest(0);
                    double worstFont = 0;
                    for (int k = 1; k < word.Length; k++)
                    {
                        // the letter boundary is somewhere between where one letter's ink ends and the next one's starts
                        double end = F(word[..k]).BuildGeometry(new Point(15, 15)).Bounds.Right;
                        double next = 15 + F(word[..k]).WidthIncludingTrailingWhitespace;
                        double startNext = F(word[k].ToString()).BuildGeometry(new Point(next, 15)).Bounds.Left;
                        double lo2 = Math.Min(end, startNext), hi2 = Math.Max(end, startNext);
                        double off = edges[k] < lo2 ? lo2 - edges[k] : edges[k] > hi2 ? edges[k] - hi2 : 0;
                        worstFont = Math.Max(worstFont, off);
                    }
                    e3.CloseForTest();
                    res.Add($"{fontName} off by {worstFont:0.0}" + (worstFont > 2.5 ? $" [edges {string.Join(" ", edges.Select(x => x.ToString("0.0")))} ink {string.Join(" ", Enumerable.Range(1, word.Length - 1).Select(k => F(word[..k]).BuildGeometry(new Point(15, 15)).Bounds.Right.ToString("0.0")))}]" : ""));
                }
                return string.Join(", ", res);
            }
            string fonts = Fonts();

            log.Add($"live text letters: words={string.Join("|", words.Select(w => w.Text))} (expect Hello|there) letter gaps off by at most {worst:0.0} px (expect under 3) " +
                    $"inside=\"{inside}\" (expect ell) across=\"{across}\" (expect llo the) click=\"{click}\" caret {clickCaret} (expect empty, there@3) double-click=\"{dbl}\" (expect there) " +
                    $"backspace after He: {afterBack} (expect Hllo|there caret Hllo@1) backspace before there: {afterSpace} (expect Hllothere) read back \"{readBack}\" " +
                    $"fonts from ink: {fonts} (expect each under 2.5; Times kerns T over y) " +
                    $"after erasing ell: H darkest={h} middle={middle} o darkest={o} (expect dark, light, dark)");
        }
        catch (Exception ex) { log.Add("live text letters: FAILED " + ex.GetType().Name + " " + ex.Message); }

        // ---- typing into a screenshot: the font, size and colour are recognised, and typed letters match a real rendering ----
        try
        {
            var host = new EditorHost { SaveNew = _ => null, Pin = (_, _) => { } };
            System.Windows.Media.Imaging.BitmapSource Page(string font, System.Windows.FontWeight weight, double size, string text,
                System.Windows.Media.Color ink, System.Windows.Media.Color paper, out Rect inkBox, out List<OcrWord> words)
            {
                var tf = new System.Windows.Media.Typeface(new System.Windows.Media.FontFamily(font), FontStyles.Normal, weight, FontStretches.Normal);
                System.Windows.Media.FormattedText F(string t) => new(t, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, size, new System.Windows.Media.SolidColorBrush(ink), 1.0);
                var v = new System.Windows.Media.DrawingVisual();
                var origin = new Point(14, 12);
                using (var dc = v.RenderOpen())
                {
                    dc.DrawRectangle(new System.Windows.Media.SolidColorBrush(paper), null, new Rect(0, 0, 360, 60));
                    dc.DrawText(F(text), origin);
                }
                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(360, 60, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bmp.Render(v);
                inkBox = F(text).BuildGeometry(origin).Bounds;
                // each word's box, as the text reader would give it
                words = new List<OcrWord>();
                int at = 0;
                foreach (var word in text.Split(' '))
                {
                    double x = origin.X + F(text[..at]).WidthIncludingTrailingWhitespace;
                    var g = F(word).BuildGeometry(new Point(x, origin.Y)).Bounds;
                    words.Add(new OcrWord(word, g));
                    at += word.Length + 1;
                }
                return ImageLoader.Normalize(bmp);
            }

            // which font, which size: five styles on different backgrounds
            var cases = new (string Font, System.Windows.FontWeight Weight, double Size, System.Windows.Media.Color Ink, System.Windows.Media.Color Paper)[]
            {
                ("Segoe UI", FontWeights.Normal, 15, System.Windows.Media.Colors.Black, System.Windows.Media.Colors.White),
                ("Arial", FontWeights.Bold, 14, System.Windows.Media.Color.FromRgb(30, 30, 30), System.Windows.Media.Color.FromRgb(240, 240, 240)),
                ("Georgia", FontWeights.Normal, 18, System.Windows.Media.Color.FromRgb(40, 60, 140), System.Windows.Media.Colors.White),
                ("Consolas", FontWeights.Normal, 13, System.Windows.Media.Color.FromRgb(220, 220, 220), System.Windows.Media.Color.FromRgb(30, 30, 30)),
                ("Times New Roman", FontWeights.Normal, 20, System.Windows.Media.Colors.Black, System.Windows.Media.Color.FromRgb(255, 250, 235)),
            };
            var found = new List<string>();
            double slowest = 0;
            int right = 0;
            foreach (var c in cases)
            {
                var page = Page(c.Font, c.Weight, c.Size, "The quick brown fox jumps", c.Ink, c.Paper, out _, out var words);
                var ed = new EditorWindow(page, null, "type", host);
                ed.SelectWordsForTest(words, 0, 0);
                var (style, ms) = ed.MatchLineForTest(0);
                slowest = Math.Max(slowest, ms);
                string want = c.Font + (c.Weight == FontWeights.Bold ? " Bold" : "");
                bool ok = style != null && style.Name == want && Math.Abs(style.Size - c.Size) <= 1;
                if (ok) right++;
                found.Add($"{want} {c.Size}->{style?.Name ?? "none"} {style?.Size:0.#}");
                ed.CloseForTest();
            }

            // typing: "Hello world", cursor after Hello, type " big"; compare with "Hello big world" drawn for real
            var black = System.Windows.Media.Colors.Black;
            var white = System.Windows.Media.Colors.White;
            var before = Page("Segoe UI", FontWeights.Normal, 16, "Hello world", black, white, out _, out var hw);
            var wanted = Page("Segoe UI", FontWeights.Normal, 16, "Hello big world", black, white, out _, out _);
            var ed2 = new EditorWindow(before, null, "type", host);
            ed2.SelectWordsForTest(hw, 0, 0);
            var e0 = ed2.EdgesForTest(0);
            var typing = ed2.ClickAndTypeForTest(new Point(e0[^1] - 0.2, hw[0].Bounds.Top + hw[0].Bounds.Height / 2), " big");
            for (int spin = 0; !typing.IsCompleted && spin < 300; spin++) { DoEvents(); Thread.Sleep(10); }
            var typed = ed2.BaseForTest;
            string model = ed2.WordsForTest;
            var p1 = Pixels(typed);
            var p2 = Pixels(wanted);
            // where each run of ink columns starts, in both pictures
            List<int> Starts(byte[] p)
            {
                var starts = new List<int>();
                bool was = false;
                for (int x = 0; x < 360; x++)
                {
                    bool ink = false;
                    for (int y = 0; y < 60 && !ink; y++) ink = p[(y * 360 + x) * 4 + 1] < 160;
                    if (ink && !was) starts.Add(x);
                    was = ink;
                }
                return starts;
            }
            var s1 = Starts(p1);
            var s2 = Starts(p2);
            string letterOff = s1.Count == s2.Count ? s1.Zip(s2, (u, v) => Math.Abs(u - v)).Max().ToString() : $"runs {s1.Count} vs {s2.Count}";
            var reread = Task.Run(() => TextRecognizer.RecognizeLinesAsync(ImageLoader.Normalize(typed))).GetAwaiter().GetResult();
            string readBack = string.Join(" ", reread?.SelectMany(l => l).Select(x => x.Text) ?? Array.Empty<string>());
            if (Environment.GetEnvironmentVariable("OVERSKIN_TEST_OUT") is { Length: > 0 } typeOut)
            {
                foreach (var (img, file) in new[] { (typed, "typed.png"), (wanted, "typed-real.png") })
                {
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(img));
                    using var f = System.IO.File.Create(System.IO.Path.Combine(typeOut, file));
                    enc.Save(f);
                }
            }
            ed2.CloseForTest();

            // the same, with another line beside this one on the same row (a time after a name): the whole
            // height of the letters moves, none of the old bottoms are left behind
            var beside = hw.Append(new OcrWord("12:30", new Rect(300, hw[0].Bounds.Top, 34, hw[0].Bounds.Height))).ToList();
            var ed3 = new EditorWindow(before, null, "type", host);
            ed3.SelectWordsForTest(beside, 0, 0, new[] { 0, 0, 1 });
            var e3 = ed3.EdgesForTest(0);
            var typing3 = ed3.ClickAndTypeForTest(new Point(e3[^1] - 0.2, hw[0].Bounds.Top + hw[0].Bounds.Height / 2), " big");
            for (int spin = 0; !typing3.IsCompleted && spin < 300; spin++) { DoEvents(); Thread.Sleep(10); }
            var s3 = Starts(Pixels(ed3.BaseForTest));
            string besideOff = s3.Count == s2.Count ? s3.Zip(s2, (u, v) => Math.Abs(u - v)).Max().ToString() : $"runs {s3.Count} vs {s2.Count}";
            ed3.CloseForTest();
            log.Add($"typing beside another line: letters off from the real thing by at most {besideOff} px (expect 0 to 2, no leftover halves)");
            log.Add($"typing: fonts recognised {right}/{cases.Length} (expect 5/5) [{string.Join("; ", found)}] slowest match {slowest:0} ms (expect under 600, once per line) " +
                    $"typed \" big\": words={model} (expect Hello|big|world) read back \"{readBack}\" (expect Hello big world) " +
                    $"letters off from the real thing by at most {letterOff} px (expect 0 to 2)");
        }
        catch (Exception ex) { log.Add("typing: FAILED " + ex.GetType().Name + " " + ex.Message + ex.StackTrace); }


        // ---- typing into ClearType-style text (RGB on one, BGR on the other): the typed edges come out coloured the same way ----
        try
        {
            var host2 = new EditorHost { SaveNew = _ => null, Pin = (_, _) => { } };
            var results = new List<string>();
            foreach (var (ink, paper, label) in new[] { (System.Windows.Media.Colors.Black, System.Windows.Media.Colors.White, "dark on white"),
                                                         (System.Windows.Media.Colors.White, System.Windows.Media.Color.FromRgb(32, 33, 36), "white on dark") })
            {
                var before = EditorWindow.ClearTypeForTest("Hello world", "Segoe UI", 16, ink, paper, edges: label.StartsWith("dark") ? 1 : 2, filter: 1, sat: 1.6, gamma: 0.85);
                var wanted = EditorWindow.ClearTypeForTest("Hello big world", "Segoe UI", 16, ink, paper, edges: label.StartsWith("dark") ? 1 : 2, filter: 1, sat: 1.6, gamma: 0.85);
                var pxB = Pixels(before);
                // the words, where their ink is
                var words = new List<OcrWord>();
                {
                    int top = 60, bottom = 0;
                    var cols = new List<int>();
                    for (int x = 0; x < 360; x++)
                        for (int y = 0; y < 60; y++)
                        {
                            int o = (y * 360 + x) * 4;
                            if (Math.Abs(pxB[o + 1] - paper.G) > 60) { cols.Add(x); top = Math.Min(top, y); bottom = Math.Max(bottom, y); }
                        }
                    cols = cols.Distinct().OrderBy(x => x).ToList();
                    int start = cols[0], prev = cols[0];
                    var spans = new List<(int, int)>();
                    foreach (int x in cols.Skip(1)) { if (x > prev + 3) { spans.Add((start, prev)); start = x; } prev = x; }
                    spans.Add((start, prev));
                    var names = new[] { "Hello", "world" };
                    for (int k = 0; k < Math.Min(2, spans.Count); k++)
                        words.Add(new OcrWord(names[k], new Rect(spans[k].Item1, top, spans[k].Item2 - spans[k].Item1 + 1, bottom - top + 1)));
                }
                var ed = new EditorWindow(before, null, "cleartype", host2);
                ed.SelectWordsForTest(words, 0, 0);
                var e0 = ed.EdgesForTest(0);
                var typing = ed.ClickAndTypeForTest(new Point(e0[^1] - 0.2, words[0].Bounds.Top + words[0].Bounds.Height / 2), " big");
                for (int spin = 0; !typing.IsCompleted && spin < 300; spin++) { DoEvents(); Thread.Sleep(10); }
                var (style, matchMs) = ed.MatchLineForTest(0);
                var typed = ed.BaseForTest;
                // coloured edge pixels in "big", in what was typed and in the real thing
                int Fringe(byte[] p)
                {
                    int n = 0;
                    for (int y = 0; y < 60; y++)
                        for (int x = 55; x < 85; x++)
                        {
                            int o = (y * 360 + x) * 4;
                            if (Math.Abs(p[o + 2] - p[o]) > 40) n++;
                        }
                    return n;
                }
                var pt = Pixels(typed);
                var pw = Pixels(wanted);
                double diff = 0;
                int count = 0;
                for (int y = 0; y < 60; y++)
                    for (int x = 50; x < 90; x++)
                    {
                        int o = (y * 360 + x) * 4;
                        for (int c = 0; c < 3; c++) diff += Math.Abs(pt[o + c] - pw[o + c]);
                        count += 3;
                    }
                var reread = Task.Run(() => TextRecognizer.RecognizeLinesAsync(ImageLoader.Normalize(typed))).GetAwaiter().GetResult();
                string readBack = string.Join(" ", reread?.SelectMany(l => l).Select(x => x.Text) ?? Array.Empty<string>());
                results.Add($"{label}: matched {style?.Name} {style?.Size:0.#} edges={(style?.Edges switch { 1 => "ClearType RGB", 2 => "ClearType BGR", _ => "grey" })} " +
                            $"display={style?.Display} gamma={style?.Gamma} filter={style?.Filter} colour={style?.Saturation} in {matchMs:0} ms coloured edge pixels typed={Fringe(pt)} real={Fringe(pw)} " +
                            $"mean difference around big={diff / count:0.0} read back \"{readBack}\"");
                if (Environment.GetEnvironmentVariable("OVERSKIN_TEST_OUT") is { Length: > 0 } ctOut)
                    foreach (var (img, file) in new[] { (typed, $"cleartype-{results.Count}-typed.png"), (wanted, $"cleartype-{results.Count}-real.png") })
                    {
                        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(img));
                        using var f = System.IO.File.Create(System.IO.Path.Combine(ctOut, file));
                        enc.Save(f);
                    }
                ed.CloseForTest();
            }
            log.Add("typing into ClearType: " + string.Join(" | ", results) + " (expect ClearType RGB then BGR, gamma 0.85, filter 1, colour 1.6, coloured edge counts close, read back Hello big world)");
        }
        catch (Exception ex) { log.Add("typing into ClearType: FAILED " + ex.GetType().Name + " " + ex.Message + ex.StackTrace); }

        // ---- pin Free Transform: move, scale from a corner, stretch a side, rotate, Esc ----
        try
        {
            var pic = SolidColour(300, 200, 40, 120, 220);
            var pin = new ImageOverlayWindow(pic, "transform test", 100, new WindowManager())
            { PlaceAt = new Int32Rect(-6000, -6000, 0, 0), ShowActivated = false };
            pin.Show();
            Pump(100);
            pin.Transforming = true;
            Pump(50);
            double w0 = pin.ImageWidth, h0 = pin.ImageHeight;
            var c0 = pin.Centre;
            string R(double v) => Math.Round(v).ToString();
            Point Corner(int sx, int sy) => new(pin.Centre.X + sx * pin.ImageWidth / 2, pin.Centre.Y + sy * pin.ImageHeight / 2); // only while unrotated

            var zones = $"{pin.HitTest(pin.Centre).Zone}/{pin.HitTest(Corner(1, 1)).Zone}/{pin.HitTest(Corner(1, 1) + new Vector(20, 20)).Zone}";

            // move
            pin.TransformStart(ImageOverlayWindow.Zone.Move, 0, 0, c0);
            pin.TransformMove(c0 + new Vector(50, 20), false, false);
            pin.TransformEnd();
            var moved = pin.Centre - c0;

            // bottom-right corner, in proportion: top-left stays where it was
            var topLeft = Corner(-1, -1);
            var br = Corner(1, 1);
            pin.TransformStart(ImageOverlayWindow.Zone.Handle, 1, 1, br);
            pin.TransformMove(br + new Vector(60, 40), false, false);
            pin.TransformEnd();
            double aspectAfter = pin.ImageWidth / pin.ImageHeight;
            var topLeftAfter = Corner(-1, -1);

            // right side: wider only, left side stays
            double hBefore = pin.ImageHeight, leftBefore = Corner(-1, 0).X, wBefore = pin.ImageWidth;
            var right = Corner(1, 0);
            pin.TransformStart(ImageOverlayWindow.Zone.Handle, 1, 0, right);
            pin.TransformMove(right + new Vector(80, 30), false, false);
            pin.TransformEnd();
            string side = $"w +{R(pin.ImageWidth - wBefore)} h +{R(pin.ImageHeight - hBefore)} left edge moved {R(Corner(-1, 0).X - leftBefore)}";

            // Alt on a corner: the middle stays put
            var mid = pin.Centre;
            var tl = Corner(-1, -1);
            pin.TransformStart(ImageOverlayWindow.Zone.Handle, -1, -1, tl);
            pin.TransformMove(tl + new Vector(-30, -30), false, true);
            pin.TransformEnd();
            var altShift = pin.Centre - mid;

            // rotate from the right of the middle to below it: a quarter turn clockwise; Shift snaps
            var centre = pin.Centre;
            pin.TransformStart(ImageOverlayWindow.Zone.Rotate, 0, 0, centre + new Vector(300, 0));
            pin.TransformMove(centre + new Vector(5, 300), true, false);
            pin.TransformEnd();
            double angle = pin.Rotation;
            var centreAfterTurn = pin.Centre - centre;

            // a slow drag: the window should keep its size (no resize on every move) and the picture's middle stay put
            var sizes = new HashSet<(int, int)>();
            double drift = 0;
            var c1 = pin.Centre;
            pin.TransformStart(ImageOverlayWindow.Zone.Rotate, 0, 0, c1 + new Vector(200, 0));
            for (int i = 1; i <= 40; i++)
            {
                double a = i * Math.PI / 80;
                pin.TransformMove(c1 + new Vector(200 * Math.Cos(a), 200 * Math.Sin(a)), false, false);
                Pump(2);
                NativeMethods.GetWindowRect(pin.Handle, out var wr);
                sizes.Add((wr.Width, wr.Height));
                drift = Math.Max(drift, (pin.Centre - c1).Length);
            }
            pin.TransformEnd();
            pin.Rotation = 0;
            var corner0 = Corner(1, 1);
            pin.TransformStart(ImageOverlayWindow.Zone.Handle, 1, 1, corner0);
            for (int i = 1; i <= 30; i++)
            {
                pin.TransformMove(corner0 + new Vector(i * 2, i * 2), false, false);
                Pump(2);
                NativeMethods.GetWindowRect(pin.Handle, out var wr);
                sizes.Add((wr.Width, wr.Height));
            }
            pin.TransformEnd();
            string steady = $"window sizes during 70 moves={sizes.Count} (expect 1 to 3) rotate drift={drift:0.0} (expect under 1)";

            // the saved place is the picture's box, not the room around it
            var state = pin.ToState();
            double boxLeft = pin.Centre.X - (pin.Width - 2 * 34) / 2;

            pin.Rotation = 20;
            SaveWindow(pin, "pin-transform-rotated.png");
            bool cursorBuilt = ImageOverlayWindow.RotateCursor(new Vector(1, 1)) != System.Windows.Input.Cursors.Hand;
            bool reverted = pin.Revert();
            Pump(30);
            var back = $"{R(pin.ImageWidth)}x{R(pin.ImageHeight)} {pin.Rotation:0}° middle off by {R((pin.Centre - c0).Length)}";
            SaveWindow(pin, "pin-transform.png");
            pin.Close();
            log.Add($"pin transform: zones={zones} (expect Move/Handle/Rotate) moved={R(moved.X)},{R(moved.Y)} (expect 50,20) " +
                    $"corner keeps shape {w0 / h0:0.000}->{aspectAfter:0.000} and top-left off by {R((topLeftAfter - topLeft).Length)} (expect same, 0) " +
                    $"side: {side} (expect w +80 h +0 left edge moved 0) alt keeps middle off by {R(altShift.Length)} (expect 0 or 1: whole pixels) " +
                    $"rotate={angle:0.#}° (expect 90) middle moved {R(centreAfterTurn.Length)} (expect 0) saved left={R(state.Left)} box left={R(boxLeft)} (expect equal) " +
                    $"esc reverted={reverted} back to {back} (expect True {R(w0)}x{R(h0)} 0° off by 0) rotate cursor built={cursorBuilt} (expect True) {steady}");
        }
        catch (Exception ex) { log.Add("pin transform: FAILED " + ex.GetType().Name + " " + ex.Message + ex.StackTrace); }
    }

    /// <summary>With OVERSKIN_TEST_OUT set, keeps a picture of one of our own windows (never the screen).</summary>
    private static void SaveWindow(Window w, string name)
    {
        if (Environment.GetEnvironmentVariable("OVERSKIN_TEST_OUT") is not { Length: > 0 } dir) return;
        Pump(300);
        var content = (FrameworkElement)w.Content;
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(content.ActualWidth * 1.5), (int)(content.ActualHeight * 1.5), 144, 144, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(content);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using var f = System.IO.File.Create(System.IO.Path.Combine(dir, name));
        enc.Save(f);
    }

    /// <summary>The workflow dialog, for looking at.</summary>
    private static void WorkflowDialogPicture()
    {
        if (Environment.GetEnvironmentVariable("OVERSKIN_TEST_OUT") is not { Length: > 0 }) return;
        var w = new WorkflowWindow(new Workflow { Name = "Share safely", Hotkey = "Ctrl+Shift+F9", AutoHide = true, Watermark = true }, _ => null)
        { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        w.Show();
        SaveWindow(w, "workflow-dialog.png");
        w.Close();
    }

    private static System.Windows.Media.Imaging.BitmapSource Solid(int w, int h, byte v) => SolidColour(w, h, v, v, v);

    private static System.Windows.Media.Imaging.BitmapSource SolidColour(int w, int h, byte b, byte g, byte r)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255; }
        var bmp = System.Windows.Media.Imaging.BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, px, w * 4);
        bmp.Freeze();
        return bmp;
    }

    private static byte[] Pixels(System.Windows.Media.Imaging.BitmapSource b)
    {
        var c = new System.Windows.Media.Imaging.FormatConvertedBitmap(b, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        var px = new byte[c.PixelWidth * c.PixelHeight * 4];
        c.CopyPixels(px, c.PixelWidth * 4, 0);
        return px;
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var deeper in FindAll<T>(child)) yield return deeper;
        }
    }

    /// <summary>A test video: one second each of red, green and blue (repeating), with an optional sound track.</summary>
    private static void MakeTestVideo(string path, int seconds, Func<int, short[]>? sound)
    {
        int w = 320, h = 180, fps = 30;
        var queue = new System.Collections.Concurrent.BlockingCollection<VideoFrame>(8);
        System.Collections.Concurrent.BlockingCollection<AudioChunk>? audio = sound == null ? null : new(64);
        var encode = Task.Run(() => VideoEncoder.EncodeAsync(path, w, h, fps, queue, audio: audio));
        var feedSound = Task.Run(() =>
        {
            if (audio == null) return;
            // 10 ms chunks of 48 kHz stereo
            for (int i = 0; i < seconds * 100; i++) audio.Add(new AudioChunk(sound!(i), TimeSpan.FromMilliseconds(i * 10)));
            audio.CompleteAdding();
        });
        for (int i = 0; i < fps * seconds; i++)
        {
            var f = new byte[w * h * 4];
            int c = (i / fps) % 3; // 0 red, 1 green, 2 blue
            for (int o = 0; o < f.Length; o += 4) { f[o + (2 - c)] = 230; f[o + 3] = 255; }
            queue.Add(new VideoFrame(f, TimeSpan.FromSeconds(i / (double)fps)));
        }
        queue.CompleteAdding();
        feedSound.GetAwaiter().GetResult();
        encode.GetAwaiter().GetResult();
    }

    /// <summary>"red", "green", "blue" or the colour, of the middle of a video's frame at <paramref name="seconds"/>.</summary>
    private static string VideoPixel(string path, double seconds) => Task.Run(async () =>
    {
        var file = await global::Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        var clip = await global::Windows.Media.Editing.MediaClip.CreateFromFileAsync(file);
        var comp = new global::Windows.Media.Editing.MediaComposition();
        comp.Clips.Add(clip);
        using var thumb = await comp.GetThumbnailAsync(TimeSpan.FromSeconds(seconds), 160, 90, global::Windows.Media.Editing.VideoFramePrecision.NearestFrame);
        var decoder = await global::Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(thumb);
        var data = await decoder.GetPixelDataAsync(global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, global::Windows.Graphics.Imaging.BitmapAlphaMode.Ignore,
            new global::Windows.Graphics.Imaging.BitmapTransform(), global::Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation,
            global::Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
        var px = data.DetachPixelData();
        int o = ((int)decoder.PixelHeight / 2 * (int)decoder.PixelWidth + (int)decoder.PixelWidth / 2) * 4;
        byte b = px[o], g = px[o + 1], r = px[o + 2];
        return r > 150 && g < 80 && b < 80 ? "red" : g > 150 && r < 80 && b < 80 ? "green" : b > 150 && r < 80 && g < 80 ? "blue" : $"r{r}g{g}b{b}";
    }).GetAwaiter().GetResult();
}

#endif
