#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Overskin;

/// <summary>Self-tests for pause and drawing while recording, QR codes, video blur, stills, file names and effects.</summary>
public partial class App
{
    private static void RoundTests(List<string> log)
    {
        string tmp = Path.Combine(Path.GetTempPath(), "Overskin-round");
        Directory.CreateDirectory(tmp);
        string? outDir = Environment.GetEnvironmentVariable("OVERSKIN_TEST_OUT") is { Length: > 0 } o ? o : null;
        void Keep(BitmapSource image, string name)
        {
            if (outDir == null) return;
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(image));
            using var f = File.Create(Path.Combine(outDir, name));
            enc.Save(f);
        }

        // ---- QR codes: one drawn small inside a screenshot is read back ----
        try
        {
            var writer = new ZXing.BarcodeWriterPixelData { Format = ZXing.BarcodeFormat.QR_CODE, Options = new ZXing.Common.EncodingOptions { Width = 150, Height = 150, Margin = 2 } };
            var code = writer.Write("https://example.com/overskin?x=1");
            var qr = BitmapSource.Create(code.Width, code.Height, 96, 96, PixelFormats.Bgra32, null, code.Pixels, code.Width * 4);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawImage(FakeScreenshot(900, 500), new Rect(0, 0, 900, 500));
                dc.DrawImage(qr, new Rect(600, 200, 150, 150));
            }
            var rtb = new RenderTargetBitmap(900, 500, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            string? read = QrReader.Read(rtb);
            string? none = QrReader.Read(FakeScreenshot(400, 300));
            log.Add($"QR code: read \"{read}\" link={QrReader.LinkIn(read ?? "")?.Host} (expect the address, example.com); a picture with none: {none ?? "null"} (expect null)");
            var card = new TextResultWindow(read ?? "", qr: true) { Left = -10000, Top = -10000, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
            card.Show();
            SaveWindow(card, "qr-result.png");
            card.Close();
        }
        catch (Exception ex) { log.Add("QR code: FAILED " + ex); }

        // ---- file names from a pattern ----
        try
        {
            var when = new DateTime(2026, 11, 21, 18, 4, 33);
            var discord = new NameInfo("Discord", "#general | My: Server?");
            string Name(string pattern, string ext = ".png") => Path.GetRelativePath(tmp, FileNames.NewPath(tmp, ext, pattern, discord, when, create: false));
            string folder = Path.Combine(tmp, "names");
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            Directory.CreateDirectory(folder);
            var made = new List<string>();
            for (int i = 0; i < 3; i++)
            {
                string p = FileNames.NewPath(folder, ".png", "{app} {n}", discord, when);
                File.WriteAllText(p, "");
                made.Add(Path.GetFileName(p));
            }
            string twice1 = FileNames.NewPath(folder, ".png", "Same", discord, when);
            File.WriteAllText(twice1, "");
            string twice2 = FileNames.NewPath(folder, ".png", "Same", discord, when);
            log.Add($"file names: default \"{Name(FileNames.Default)}\" | \"{Name("{app} {date} {time}")}\" | title \"{Name("{title}")}\" | folders \"{Name("{app}\\{year}-{month}\\{type} {time}", ".mp4")}\" " +
                    $"| counter {string.Join(", ", made)} | repeat \"{Path.GetFileName(twice2)}\" | empty \"{Name("  ")}\" | example \"{FileNames.Example("{app} {date}")}\"");
        }
        catch (Exception ex) { log.Add("file names: FAILED " + ex); }

        // ---- effects: border, corners, torn edge, shadow, filters ----
        try
        {
            var shot = FakeScreenshot(800, 450);
            var bordered = Effects.Render(shot, new EffectsOptions { Border = 10, BorderColour = "#EF4444", Corners = 12 });
            var torn = Effects.Render(shot, new EffectsOptions { Torn = "Bottom", Shadow = 50 });
            var gray = Effects.Render(shot, new EffectsOptions { Filter = "Grayscale" });
            var all = Effects.Render(shot, new EffectsOptions { Border = 6, Corners = 10, Torn = "All", Shadow = 40, Filter = "Sepia" });
            byte[] Px(BitmapSource b, int x, int y)
            {
                var c = new byte[4];
                new FormatConvertedBitmap(b, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(x, y, 1, 1), c, 4, 0);
                return c;
            }
            var edge = Px(bordered, 400, 4);        // in the border
            var corner = Px(bordered, 1, 1);        // outside the rounded corner
            var g = Px(gray, 900 / 2, 320);
            int pad = (torn.PixelWidth - 800) / 2;
            var tornBottom = Px(torn, pad + 400, pad + 449); // the very bottom row: torn away
            var tornTop = Px(torn, pad + 400, pad + 2);      // the top: kept
            log.Add($"effects: bordered {bordered.PixelWidth}x{bordered.PixelHeight} (expect 820x470) border pixel r{edge[2]}g{edge[1]}b{edge[0]} (expect red) corner alpha {corner[3]} (expect 0); " +
                    $"grey equal channels={g[0] == g[1] && g[1] == g[2]}; torn bottom alpha {tornBottom[3]} (expect under 255) top alpha {tornTop[3]} (expect 255); all {all.PixelWidth}x{all.PixelHeight}");
            Keep(bordered, "effects-border.png");
            Keep(torn, "effects-torn.png");
            Keep(all, "effects-all.png");
            var dialog = new EffectsWindow(shot) { Left = -10000, Top = -10000, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
            dialog.Show();
            SaveWindow(dialog, "effects-dialog.png");
            dialog.Close();
        }
        catch (Exception ex) { log.Add("effects: FAILED " + ex); }

        // ---- a still from a video, and blurring a box in it for a while ----
        try
        {
            string src = Path.Combine(tmp, "stills.mp4"), blurred = Path.Combine(tmp, "stills-blurred.mp4");
            int w = 640, h = 360, fps = 30;
            var queue = new System.Collections.Concurrent.BlockingCollection<VideoFrame>(8);
            var encode = Task.Run(() => VideoEncoder.EncodeAsync(src, w, h, fps, queue));
            for (int i = 0; i < fps * 4; i++)
            {
                // the left half shows the time as brightness; the right half is a sharp checkerboard
                var f = new byte[w * h * 4];
                byte level = (byte)Math.Min(255, i * 2);
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int k = (y * w + x) * 4;
                        byte v = x < w / 2 ? level : (((x / 8) + (y / 8)) % 2 == 0 ? (byte)255 : (byte)0);
                        f[k] = f[k + 1] = f[k + 2] = v;
                        f[k + 3] = 255;
                    }
                queue.Add(new VideoFrame(f, TimeSpan.FromSeconds(i / (double)fps)));
            }
            queue.CompleteAdding();
            encode.GetAwaiter().GetResult();

            var still = MediaReader.FrameAt(src, TimeSpan.FromSeconds(2.0));
            var p = new byte[4];
            still.CopyPixels(new Int32Rect(100, 100, 1, 1), p, 4, 0);
            // a box over the checkerboard, blurred from 1 s to 3 s
            var box = new Rect(380, 60, 200, 200);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Task.Run(() => VideoBlur.RenderAsync(src, blurred, new[] { new VideoBlur { Area = box, Start = 1, End = 3 } }, 4)).GetAwaiter().GetResult();
            long took = sw.ElapsedMilliseconds;
            double Spread(string path, double t)
            {
                var frame = MediaReader.FrameAt(path, TimeSpan.FromSeconds(t));
                var px = new byte[160 * 160 * 4];
                frame.CopyPixels(new Int32Rect(400, 80, 160, 160), px, 160 * 4, 0);
                double mean = 0, sq = 0;
                int n = px.Length / 4;
                for (int i = 0; i < px.Length; i += 4) { mean += px[i + 1]; sq += px[i + 1] * (double)px[i + 1]; }
                mean /= n;
                return Math.Sqrt(Math.Max(0, sq / n - mean * mean));
            }
            log.Add($"still at 2.0 s: {still.PixelWidth}x{still.PixelHeight} brightness {p[1]} (expect about 120, frame 60); " +
                    $"video blur made in {took} ms: checkerboard spread before {Spread(blurred, 0.5):0} during {Spread(blurred, 2):0} after {Spread(blurred, 3.5):0} (expect about 127, under 20, about 127)");

            // the video editor with a blur box listed
            var ed = new VideoEditorWindow(src, _ => { }) { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
            ed.Show();
            Pump(800);
            ed.AddBlurForTest(new VideoBlur { Area = box, Start = 0, End = 4, Label = "email address" });
            Pump(200);
            log.Add($"video editor blur list: {ed.BlurCountForTest} box (expect 1)");

            // cropping: the editor keeps the box to even sizes, and the saved video is just that part
            ed.SetCropForTest(new Rect(301, 41, 199, 119));
            var editorCrop = ed.CropForTest;
            ed.ActivateCropForTest();
            Pump(200);
            SaveWindow(ed, "video-editor-crop.png");
            string cropped = Path.Combine(tmp, "stills-cropped.mp4");
            Task.Run(() => VideoBlur.RenderAsync(src, cropped, Array.Empty<VideoBlur>(), 4, null, new Int32Rect(300, 40, 200, 120))).GetAwaiter().GetResult();
            var cf = MediaReader.FrameAt(cropped, TimeSpan.FromSeconds(2.0));
            var left = new byte[4];
            cf.CopyPixels(new Int32Rect(6, 60, 1, 1), left, 4, 0);    // the source's x 306: the time-brightness half
            var checks = new byte[40 * 40 * 4];
            cf.CopyPixels(new Int32Rect(100, 40, 40, 40), checks, 40 * 4, 0); // the source's x 400 to 440: the checkerboard
            if (Environment.GetEnvironmentVariable("OVERSKIN_TEST_OUT") is { Length: > 0 } cropDir)
            {
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(cf));
                using (var fs = File.Create(Path.Combine(cropDir, "video-crop.png"))) enc.Save(fs);
                var whole = MediaReader.FrameAt(src, TimeSpan.FromSeconds(2.0));
                var enc2 = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc2.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(whole));
                using (var fs = File.Create(Path.Combine(cropDir, "video-whole.png"))) enc2.Save(fs);
            }
            using (var probe = new MediaReader(cropped))
            {
                probe.Next();
                log.Add($"video crop reader: {probe.Width}x{probe.Height} stride {probe.StrideForTest} buffer {probe.LastLength} (rows of {probe.Width * 4}, {probe.Width * probe.Height * 4} in all)");
            }
            int dark = 0, light = 0;
            for (int i = 1; i < checks.Length; i += 4) { if (checks[i] < 60) dark++; else if (checks[i] > 195) light++; }
            log.Add($"video crop: editor box {editorCrop?.X},{editorCrop?.Y} {editorCrop?.Width}x{editorCrop?.Height} (expect 300,40 198x118, even) " +
                    $"saved {cf.PixelWidth}x{cf.PixelHeight} (expect 200x120) left brightness {left[1]} (expect about 120) checkerboard dark {dark} light {light} (expect both several hundred)");

            // the viewer plays it like a video site: zoom shown, a click pauses with a sign in the middle
            var viewer = new ViewerWindow(new List<string?> { src }, 0, new ViewerHost { Edit = (_, _, _) => { }, EditVideo = _ => { }, EditGif = _ => { }, Pin = (_, _, _) => { }, ShowInFolder = _ => { }, Changed = () => { } }) { Left = -10000, Top = -10000, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
            viewer.Show();
            Pump(1200);
            string zoom = viewer.ZoomTextForTest;
            bool bar = viewer.ControlsShownForTest;
            viewer.TogglePlayForTest();
            Pump(120);
            bool paused = !viewer.PlayingForTest;
            double sign = viewer.FlashOpacityForTest;
            SaveWindow(viewer, "viewer-video.png");
            Pump(900);
            double faded = viewer.FlashOpacityForTest;
            viewer.Close();
            log.Add($"viewer video: zoom \"{zoom}\" (expect a percentage) play bar={bar} paused by a click={paused} sign {sign:0.00} then {faded:0.00} (expect True True, showing then 0)");
            SaveWindow(ed, "video-editor-blur.png");
            ed.Close();
        }
        catch (Exception ex) { log.Add("video stills and blur: FAILED " + ex); }

        // ---- drawing on the screen while recording: marks show, then fade; the pointer is hidden meanwhile ----
        try
        {
            var area = new Int32Rect(-4000, -4000, 600, 400);
            var pen = new ScreenPen(area) { Linger = 0.3 };
            pen.Show();
            pen.SetOn(true);
            bool on = ScreenPen.Drawing;
            pen.DrawForTest(new Point(20, 20), new Point(200, 120));
            pen.Current = ScreenPen.Tool.Arrow;
            pen.DrawForTest(new Point(300, 300), new Point(500, 100));
            int marks = pen.Marks;
            Pump(1400);
            int after = pen.Marks;
            pen.SetOn(false);
            bool off = !ScreenPen.Drawing;
            pen.Close();
            log.Add($"screen pen: drawing flag on={on} off after={off} marks {marks} then {after} after fading (expect True True 2 then 0)");

            // with fading turned off, marks stay while drawing, and go once the pen is turned off
            var keep = new ScreenPen(area) { Linger = 0.3, AutoFade = false };
            keep.Show();
            keep.SetOn(true);
            keep.DrawForTest(new Point(20, 20), new Point(200, 120));
            Pump(1400);
            int stayed = keep.Marks;
            keep.SetOn(false);
            Pump(1200);
            int gone = keep.Marks;
            keep.Close();
            log.Add($"screen pen without fading: {stayed} mark after 1.4 s, {gone} after turning the pen off (expect 1 then 0)");

            // the recording bar with pause and the pen
            var recorder = new VideoRecorder(Current.Dispatcher);
            var bar = new RecordBarWindow(recorder, new Int32Rect(-4000, -4000, 600, 400)) { Left = -10000, Top = -10000 };
            var sheet = new ScreenPen(new Int32Rect(-4000, -4000, 600, 400));
            sheet.Show();
            bar.Show();
            sheet.Touched += bar.RaiseAbove;
            var barHandle = new System.Windows.Interop.WindowInteropHelper(bar).Handle;
            var sheetHandle = new System.Windows.Interop.WindowInteropHelper(sheet).Handle;
            bool BarAbove()
            {
                // walk down from the bar: the sheet must come after it
                for (var h = NativeMethods.GetWindow(barHandle, 2); h != IntPtr.Zero; h = NativeMethods.GetWindow(h, 2))
                    if (h == sheetHandle) return true;
                return false;
            }
            // the sheet lifted on top (as a click on it can do), then turned on: the bar comes back above it
            NativeMethods.SetWindowPos(sheetHandle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            bool lifted = !BarAbove();
            sheet.SetOn(true);
            bool back = BarAbove();
            sheet.SetOn(false);
            sheet.Close();
            log.Add($"recording bar over the pen: sheet lifted above it={lifted}, bar back on top once the pen is touched={back} (expect True True)");
            SaveWindow(bar, "record-bar.png");
            bar.Close();
        }
        catch (Exception ex) { log.Add("screen pen: FAILED " + ex); }

        // ---- toast corners: the edge of the rounded card blends into the outline, not white ----
        try
        {
            var probe = new GuardedWindow(clickThrough: true);
            var windowBg = (SolidColorBrush)probe.Background;
            var radius = (CornerRadius)Current.Resources["RadiusPill"];
            var card = new System.Windows.Controls.Border { Width = 240, Height = 40, CornerRadius = radius, BorderThickness = new Thickness(1) };
            card.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "Panel");
            card.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "LineStrong");
            var host = new System.Windows.Controls.Grid { Width = 240, Height = 40, Background = windowBg, Children = { card } };
            host.Measure(new Size(240, 40));
            host.Arrange(new Rect(0, 0, 240, 40));
            var rtb = new RenderTargetBitmap(240, 40, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(host);
            var px = new byte[240 * 40 * 4];
            rtb.CopyPixels(px, 240 * 4, 0);
            // the brightest pixel within the corner's curve, against the outline's own brightness
            int brightest = 0;
            for (int y = 0; y < 12; y++)
                for (int x = 0; x < 12; x++)
                    brightest = Math.Max(brightest, px[(y * 240 + x) * 4 + 1]);
            var line = ((SolidColorBrush)Current.Resources["LineStrong"]).Color;
            probe.Close();
            log.Add($"toast corners: window background {windowBg.Color} brightest corner pixel {brightest} outline {line.G} (expect no brighter than the outline, nowhere near 255)");
        }
        catch (Exception ex) { log.Add("toast corners: FAILED " + ex); }

        // ---- pictures inside a screenshot: round ones, big round corners, square corners on a gradient ----
        try
        {
            int w = 1600, h = 1000;
            var rnd = new Random(11);
            System.Windows.Media.Imaging.BitmapSource Photo(int pw, int ph)
            {
                var bits = new byte[pw * ph * 4];
                for (int y = 0; y < ph; y++)
                    for (int x = 0; x < pw; x++)
                    {
                        int i = (y * pw + x) * 4;
                        bits[i] = (byte)Math.Clamp(70 + x * 120 / pw + rnd.Next(-14, 14), 0, 255);
                        bits[i + 1] = (byte)Math.Clamp(110 + y * 100 / ph + rnd.Next(-14, 14), 0, 255);
                        bits[i + 2] = (byte)Math.Clamp(210 - x * 90 / pw + rnd.Next(-14, 14), 0, 255);
                        bits[i + 3] = 255;
                    }
                return System.Windows.Media.Imaging.BitmapSource.Create(pw, ph, 96, 96, PixelFormats.Pbgra32, null, bits, pw * 4);
            }
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x31, 0x33, 0x38)), null, new Rect(0, 0, w, h));
                // a gradient band, darker to lighter going down
                dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x1E, 0x1F, 0x22), Color.FromRgb(0x5A, 0x5C, 0x62), 90), null, new Rect(0, 520, w, 480));
                void Round(Rect r, double radius)
                {
                    dc.PushClip(new RectangleGeometry(r, radius, radius));
                    dc.DrawImage(Photo((int)r.Width, (int)r.Height), r);
                    dc.Pop();
                }
                Round(new Rect(100, 100, 96, 96), 48);      // a profile picture
                Round(new Rect(300, 100, 180, 180), 90);    // a big one
                Round(new Rect(600, 100, 320, 220), 70);    // big round corners
                Round(new Rect(1000, 100, 260, 120), 60);   // a pill
                dc.DrawImage(Photo(300, 200), new Rect(100, 640, 300, 200)); // square corners, on the gradient
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x58, 0x65, 0xF2)), null, new Point(900, 700), 32, 32); // a flat disc: not a picture
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x24, 0x80, 0x46)), null, new Rect(1100, 670, 200, 50), 25, 25); // a flat button
                var face = new Typeface("Segoe UI");
                for (int line = 0; line < 6; line++)
                    dc.DrawText(new FormattedText("A line of chat text sitting next to the pictures " + line, System.Globalization.CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, face, 15, Brushes.Gainsboro, 1.0), new Point(600, 360 + line * 24));
            }
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            var screen = new byte[w * h * 4];
            rtb.CopyPixels(screen, w * 4, 0);
            ImageFinder.Find(screen, w, h);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var found = ImageFinder.Find(screen, w, h);
            sw.Stop();
            string Show(FoundImage? f) => f is { } v ? $"{v.Bounds.X},{v.Bounds.Y} {v.Bounds.Width}x{v.Bounds.Height} r{v.Radius}" : "none";
            log.Add($"round pictures: {found.Count} found in {sw.ElapsedMilliseconds} ms (edges {ImageFinder.EdgeMs}, round {ImageFinder.RoundMs}): " +
                    $"avatar={Show(ImageFinder.At(found, 148, 148))} (expect 100,100 96x96 r48) " +
                    $"big={Show(ImageFinder.At(found, 390, 190))} (expect 300,100 180x180 r90) " +
                    $"corners={Show(ImageFinder.At(found, 760, 210))} (expect 600,100 320x220 r~70) " +
                    $"pill={Show(ImageFinder.At(found, 1130, 160))} (expect 1000,100 260x120 r60) " +
                    $"square on gradient={Show(ImageFinder.At(found, 250, 740))} (expect 100,640 300x200 r0) " +
                    $"flat disc={Show(ImageFinder.At(found, 900, 700))} flat button={Show(ImageFinder.At(found, 1200, 695))} text={Show(ImageFinder.At(found, 700, 380))} (expect none, none, none)");
            if (Environment.GetEnvironmentVariable("OVERSKIN_TEST_OUT") is { Length: > 0 } dir)
                SaveBgra(screen, w, h, System.IO.Path.Combine(dir, "round-pictures.png"));
        }
        catch (Exception ex) { log.Add("round pictures: FAILED " + ex); }

        // ---- cropping a GIF: the output is the cropped size, caption bar and all ----
        try
        {
            var clip = new GifClip { Width = 200, Height = 120 };
            for (int i = 0; i < 4; i++)
            {
                var f = new byte[200 * 120 * 4];
                for (int k = 0; k < f.Length; k += 4) { f[k] = 40; f[k + 1] = (byte)(60 * i); f[k + 2] = 200; f[k + 3] = 255; }
                clip.Frames.Add(f);
                clip.DelaysMs.Add(80);
            }
            var gw = new GifEditorWindow(clip, "crop", new GifEditorHost { Folder = Path.GetTempPath(), Saved = _ => { } })
            { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
            gw.Show();
            Pump(150);
            var before = gw.OutputSizeForTest;
            gw.SetCropForTest(new Rect(20, 10, 100, 60));
            Pump(50);
            var after = gw.OutputSizeForTest;
            var rendered = gw.RenderFrameForTest(0);
            SaveWindow(gw, "gif-editor-crop.png");
            gw.TopBox.Text = "a caption";
            Pump(50);
            var captioned = gw.OutputSizeForTest;
            gw.Close();
            log.Add($"gif crop: before {before.Width}x{before.Height} (expect 200x120) cropped {after.Width}x{after.Height} rendered {rendered.W}x{rendered.H} (expect 100x60 both) " +
                    $"with a caption {captioned.Width}x{captioned.Height} (expect 100 wide, taller than 60)");
        }
        catch (Exception ex) { log.Add("gif crop: FAILED " + ex); }

        // ---- updates: reading GitHub's answer, refusing anything that isn't Overskin's own release ----
        try
        {
            string Json(string tag, string url, bool pre = false) =>
                "{\"tag_name\":\"" + tag + "\",\"html_url\":\"https://github.com/overskin/Overskin/releases/tag/" + tag + "\",\"draft\":false,\"prerelease\":" + (pre ? "true" : "false") +
                ",\"assets\":[{\"name\":\"notes.txt\",\"browser_download_url\":\"https://github.com/x\",\"size\":1},{\"name\":\"OverskinSetup.exe\",\"browser_download_url\":\"" + url + "\",\"size\":123}]}";
            var good = Updater.ReadRelease(Json("v9.4.2", "https://github.com/overskin/Overskin/releases/download/v9.4.2/OverskinSetup.exe"));
            var elsewhere = Updater.ReadRelease(Json("v9.4.2", "https://example.com/OverskinSetup.exe"));
            var pre = Updater.ReadRelease(Json("v9.5.0-beta", "https://github.com/overskin/Overskin/releases/download/v9.5.0-beta/OverskinSetup.exe", pre: true));
            log.Add($"update parsing: version {good?.Version} size {good?.InstallerSize} newer than {Updater.Current}={good?.Version > Updater.Current} (expect 9.4.2 123 True) " +
                    $"other site={(elsewhere == null ? "refused" : "taken")} pre-release={(pre == null ? "skipped" : "taken")} (expect refused, skipped) " +
                    $"tags: {Updater.ParseTag("v3.1")} {Updater.ParseTag("3.2.0+abc")} {Updater.ParseTag("nope")?.ToString() ?? "none"} (expect 3.1.0 3.2.0 none)");

            // a real look at GitHub and a real download, only when asked (it's 90 MB)
            if (Environment.GetEnvironmentVariable("OVERSKIN_TEST_NET") == "1")
            {
                var newer = Task.Run(() => Updater.CheckAsync()).GetAwaiter().GetResult();
                using var http = new System.Net.Http.HttpClient();
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Overskin-Updater");
                var latest = Updater.ReadRelease(Task.Run(() => http.GetStringAsync($"https://api.github.com/repos/{Updater.Repository}/releases/latest")).GetAwaiter().GetResult());
                var sw = System.Diagnostics.Stopwatch.StartNew();
                string got = latest == null ? "" : Task.Run(() => Updater.DownloadAsync(latest, null)).GetAwaiter().GetResult();
                log.Add($"update on GitHub: newer than this copy={(newer == null ? "none" : newer.Version.ToString())} latest={latest?.Version} " +
                        $"downloaded {(got.Length > 0 ? new FileInfo(got).Length : 0)} bytes in {sw.ElapsedMilliseconds} ms, size and version checked (expect the release's size)");
                if (got.Length > 0) File.Delete(got);
            }
        }
        catch (Exception ex) { log.Add("updates: FAILED " + ex); }

        // ---- the clipboard picture comes back the right way up ----
        try
        {
            string? before = null;
            try { if (Clipboard.ContainsText()) before = Clipboard.GetText(); } catch { }
            var shot = FakeScreenshot(400, 300);
            FastClipboard.Copy(shot);
            var back = Clipboard.GetImage();
            var a = new byte[4];
            var b = new byte[4];
            shot.CopyPixels(new Int32Rect(100, 5, 1, 1), a, 4, 0);  // the sidebar, top
            new FormatConvertedBitmap(back, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(100, 5, 1, 1), b, 4, 0);
            try { if (before != null) Clipboard.SetText(before); else Clipboard.Clear(); } catch { }
            log.Add($"fast clipboard picture: same pixel={a[0] == b[0] && a[1] == b[1] && a[2] == b[2]} (expect True)");
        }
        catch (Exception ex) { log.Add("fast clipboard picture: FAILED " + ex); }
    }
}
#endif
