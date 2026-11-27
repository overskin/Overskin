using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Overskin.Linux.Core;
using Overskin.Linux.Interface;
using Overskin.Linux.Platform;

namespace Overskin.Linux;

/// <summary>overskin --selftest: checks the parts that don't need a person at the mouse. Exit code 0 means all passed.</summary>
public static partial class SelfTest
{
    private static int _failed;

    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  (" + detail + ")" : "")}");
        if (!ok) _failed++;
    }

    private static void Try(string name, Action test)
    {
        try { test(); }
        catch (Exception ex) { Check(name, false, ex.GetType().Name + ": " + ex.Message); }
    }

    private static async Task TryAsync(string name, Func<Task> test)
    {
        try { await test(); }
        catch (Exception ex) { Check(name, false, ex.GetType().Name + ": " + ex.Message); }
    }

    /// <summary>A test picture: four coloured quarters.</summary>
    public static Bitmap Quarters(int w, int h) => Imaging.Render(new PixelSize(w, h), ctx =>
    {
        ctx.FillRectangle(Brushes.Red, new Rect(0, 0, w / 2, h / 2));
        ctx.FillRectangle(Brushes.Lime, new Rect(w / 2, 0, w - w / 2, h / 2));
        ctx.FillRectangle(Brushes.Blue, new Rect(0, h / 2, w / 2, h - h / 2));
        ctx.FillRectangle(Brushes.White, new Rect(w / 2, h / 2, w - w / 2, h - h / 2));
    });

    private static (byte B, byte G, byte R, byte A) PixelAt(Bitmap b, int x, int y)
    {
        var px = Imaging.Pixels(b, out int stride);
        int i = y * stride + x * 4;
        return (px[i], px[i + 1], px[i + 2], px[i + 3]);
    }

    public static async Task<int> RunAsync()
    {
        // never hang a build: give up loudly if something gets stuck
        _ = Task.Delay(TimeSpan.FromMinutes(4)).ContinueWith(_ =>
        {
            Console.WriteLine("FAIL  the self-test took longer than 4 minutes and was stopped");
            Environment.Exit(2);
        });
        Console.WriteLine($"Overskin for Linux self-test on {Environment.OSVersion} " +
                          $"({(OperatingSystem.IsLinux() ? (ScreenGrabber.IsWayland ? "Wayland" : "X11") : "not Linux")})");

        Try("settings round trip", () =>
        {
            var s = new Settings { SaveFolder = Path.Combine(Path.GetTempPath(), "overskin-test-shots"), AfterCapture = "notice", PenWidth = 7 };
            s.Save();
            var back = Settings.Load();
            Check("settings round trip", back.SaveFolder == s.SaveFolder && back.AfterCapture == "notice" && back.PenWidth == 7);
            File.Delete(Settings.FilePath);
        });

        Try("new file names don't clash", () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "overskin-test-" + Guid.NewGuid().ToString("N")[..8]);
            var s = new Settings { SaveFolder = folder, FileNamePattern = "shot" };
            var a = s.NewFilePath();
            File.WriteAllText(a, "");
            var b = s.NewFilePath();
            Check("new file names don't clash", a != b && b.EndsWith("shot (2).png"), Path.GetFileName(b));
            Directory.Delete(folder, true);
        });

        Try("crop", () =>
        {
            var pic = Quarters(200, 100);
            var part = Imaging.Crop(pic, new PixelRect(100, 50, 60, 30));
            var p = PixelAt(part, 5, 5);
            Check("crop", part.PixelSize == new PixelSize(60, 30) && p.R == 255 && p.G == 255 && p.B == 255,
                  $"{part.PixelSize}, pixel {p}");
        });

        Try("pixelate", () =>
        {
            var pic = Quarters(64, 64);
            var px = Imaging.Pixelate(pic, new PixelRect(16, 16, 32, 32), 32); // one block over all four colours
            var a = PixelAt(px, 0, 0);
            var b = PixelAt(px, 31, 31);
            Check("pixelate", px.PixelSize == new PixelSize(32, 32) && a == b && a.R is > 100 and < 200, $"{a} vs {b}");
        });

        Try("png save and load", () =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"overskin-test-{Environment.ProcessId}.png");
            Imaging.SavePng(Quarters(40, 30), path);
            using var back = new Bitmap(path);
            var p = PixelAt(back, 30, 25);
            Check("png save and load", back.PixelSize == new PixelSize(40, 30) && p.R == 255 && p.B == 255, $"{p}");
            File.Delete(path);
        });

        Try("editor marks end up in the picture", () =>
        {
            var pic = Imaging.Render(new PixelSize(300, 200), ctx => ctx.FillRectangle(Brushes.White, new Rect(0, 0, 300, 200)));
            var canvas = new EditorCanvas(pic);
            canvas.Add(new ShapeMark { Kind = Tool.Rectangle, From = new Point(20, 20), To = new Point(120, 100), Colour = Colors.Red, Width = 6 });
            canvas.Add(new ShapeMark { Kind = Tool.Arrow, From = new Point(150, 150), To = new Point(280, 40), Colour = Colors.Blue, Width = 5 });
            canvas.Add(new TextMark { At = new Point(30, 140), Text = "Hello", Size = 28, Colour = Colors.Black });
            canvas.Add(new CounterMark { At = new Point(250, 170), Number = 1, Colour = Colors.Green });
            var stroke = new StrokeMark { Colour = Colors.Orange, Width = 8, Highlighter = true };
            stroke.Points.AddRange(new[] { new Point(10, 190), new Point(100, 190) });
            canvas.Add(stroke);
            var result = canvas.Export();
            var edge = PixelAt(result, 20, 60);   // on the box's left edge
            var blank = PixelAt(result, 70, 60);  // inside the box, untouched
            Check("editor marks end up in the picture",
                  result.PixelSize == pic.PixelSize && edge.R > 200 && edge.G < 80 && blank == (255, 255, 255, 255),
                  $"edge {edge}, inside {blank}");

            canvas.Undo();
            canvas.Undo();
            Check("undo and redo", canvas.Marks.Count == 3 && canvas.CanRedo);
            canvas.Redo();
            Check("redo", canvas.Marks.Count == 4);
        });

        Try("pixelate mark hides the original", () =>
        {
            var pic = Quarters(100, 100);
            var canvas = new EditorCanvas(pic);
            // strength 12 makes 38 px blocks: one block over the middle covers all four colours
            canvas.Add(new PatchMark { Area = new Rect(31, 31, 38, 38), Mode = HideMode.Pixelate, Strength = 12 });
            var result = canvas.Export();
            var a = PixelAt(result, 32, 32);
            var b = PixelAt(result, 67, 67);
            Check("pixelate mark hides the original", a == b && a != (0, 0, 255, 255), $"{a} vs {b}");

            var blurred = new EditorCanvas(pic);
            blurred.Add(new PatchMark { Area = new Rect(30, 30, 40, 40), Mode = HideMode.Blur, Strength = 6 });
            var mid = PixelAt(blurred.Export(), 50, 50);
            Check("blur mixes the colours", mid.R is > 60 and < 240 && mid.B is > 30, $"{mid}");
        });

        Try("smart erase fills from around", () =>
        {
            // a black bar on a blue top half and a light bottom half disappears into them
            var pic = Imaging.Render(new PixelSize(200, 120), ctx =>
            {
                ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(40, 90, 200)), new Rect(0, 0, 200, 60));
                ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(240, 240, 240)), new Rect(0, 60, 200, 60));
                ctx.FillRectangle(Brushes.Black, new Rect(60, 20, 80, 12));
            });
            var canvas = new EditorCanvas(pic) { Tool = Tool.Erase, PenWidth = 3 };
            var mask = Effects.StrokeMask(new[] { new Point(60, 26), new Point(140, 26) }, 10, 200, 120);
            canvas.ReplacePicture(p => Effects.Inpaint(p, mask), flatten: false);
            var p = PixelAt(canvas.Picture, 100, 26);
            Check("smart erase fills from around", p.B > 150 && p.R < 90, $"{p}");
            canvas.Undo();
            Check("smart erase can be undone", PixelAt(canvas.Picture, 100, 26) == (0, 0, 0, 255));
        });

        Try("crop, turn and flip", () =>
        {
            var canvas = new EditorCanvas(Quarters(200, 100));
            canvas.Add(new ShapeMark { Kind = Tool.Rectangle, From = new Point(10, 10), To = new Point(50, 40), Colour = Colors.Black, Width = 4 });
            canvas.Crop(new Rect(100, 0, 100, 50));
            Check("crop keeps the part", canvas.PixelSize == new PixelSize(100, 50) && canvas.Marks.Count == 0 && PixelAt(canvas.Picture, 10, 10).G == 255);
            canvas.ReplacePicture(p => Effects.Rotate(p, clockwise: true), flatten: true);
            Check("turn", canvas.PixelSize == new PixelSize(50, 100));
            canvas.Undo();
            canvas.Undo();
            Check("undo crop and turn", canvas.PixelSize == new PixelSize(200, 100) && canvas.Marks.Count == 1);
            var flipped = Effects.Flip(Quarters(200, 100), horizontal: true);
            Check("flip", PixelAt(flipped, 10, 10).G == 255 && PixelAt(flipped, 10, 10).R == 0);
        });

        Try("beautify", () =>
        {
            var pic = Quarters(200, 100);
            var b = Beautify.Render(pic, new BeautifyOptions { Padding = 40, Aspect = 1 });
            var corner = PixelAt(b, 2, 2);
            Check("beautify", b.PixelSize == new PixelSize(280, 280) && corner.B > 200 && corner.R < 120, $"{b.PixelSize} corner {corner}");
        });

        Try("scrolling capture stitches a page", () =>
        {
            // a tall page with distinct stripes, seen through a 200-pixel window scrolled 70 at a time
            var page = new Px(120, 600);
            var rnd = new Random(4);
            for (int y = 0; y < 600; y++)
            {
                byte r = (byte)rnd.Next(256), g = (byte)rnd.Next(256), b = (byte)rnd.Next(256);
                for (int x = 0; x < 120; x++) { int i = (y * 120 + x) * 4; page.D[i] = (byte)(b + x); page.D[i + 1] = g; page.D[i + 2] = (byte)(r ^ x); page.D[i + 3] = 255; }
            }
            var stitcher = new ScrollStitcher(page.Crop(new PixelRect(0, 0, 120, 200)));
            for (int top = 70; top <= 400; top += 70) stitcher.Add(page.Crop(new PixelRect(0, top, 120, 200)));
            var result = stitcher.Result();
            // the last view shows rows 350 to 550
            bool same = result.H == 550 && Enumerable.Range(0, 550).All(y => result.D[y * 120 * 4] == page.D[y * 120 * 4]);
            Check("scrolling capture stitches a page", same, $"{result.H} px tall");
        });

        Try("auto-zoom camera follows a click", () =>
        {
            var cam = new SmartCamera(1000, 600) { ZoomIn = 2 };
            for (int i = 0; i < 60; i++) cam.Update(1 / 30.0, new System.Windows.Point(800, 450), clicked: i == 0);
            var v = cam.View();
            Check("auto-zoom camera follows a click", cam.Zoom > 1.8 && v.X > 400 && v.Width < 560, $"zoom {cam.Zoom:0.00}, view x {v.X:0} w {v.Width:0}");
        });

        if (ScreenGrabber.HasCommand("ffmpeg") && ScreenGrabber.HasCommand("ffprobe"))
        {
            await TryAsync("converting, GIFs and auto-zoom (ffmpeg)", async () =>
            {
                var folder = Path.Combine(Path.GetTempPath(), "overskin-ffmpeg-" + Guid.NewGuid().ToString("N")[..6]);
                Directory.CreateDirectory(folder);
                var png = Path.Combine(folder, "pic.png");
                Imaging.SavePng(Quarters(320, 240), png);
                var jpg = await Converter.ConvertAsync(png, ".jpg");
                var mp4 = await Converter.ConvertAsync(png, ".mp4");
                var gif = Path.Combine(folder, "clip.gif");
                await Converter.VideoToGifAsync(mp4, gif, 10, 160);
                var size = new FileInfo(mp4).Length;
                // a pretend pointer track: a click in the bottom-right quarter
                var track = new PointerTrack();
                await AutoZoom.ApplyAsync(mp4, track, new PixelRect(0, 0, 320, 240), 2, 0);
                Check("converting, GIFs and auto-zoom (ffmpeg)", File.Exists(jpg) && File.Exists(gif) && new FileInfo(mp4).Length > 0,
                      $"jpg {new FileInfo(jpg).Length} B, gif {new FileInfo(gif).Length} B, mp4 {size} → {new FileInfo(mp4).Length} B");
                Directory.Delete(folder, true);
            });
        }
        else Console.WriteLine("skip  converting, GIFs and auto-zoom (no ffmpeg here)");

        if (ScreenGrabber.HasCommand("ffmpeg") && ScreenGrabber.HasCommand("ffprobe"))
        {
            await TryAsync("GIF editor: a speech bubble stays see-through in the GIF (ffmpeg)", async () =>
            {
                var dir = Path.Combine(Path.GetTempPath(), $"overskin-bubble-{Guid.NewGuid():N}");
                Directory.CreateDirectory(dir);
                try
                {
                    var look = new GifEditor.GifLook(0, 3, "", "", 32, "Anton", Colors.White, true, 0, false, null);
                    for (int k = 0; k < 3; k++) GifEditor.RenderFrame(Quarters(200, 100), look).Save(Path.Combine(dir, $"o{k:00000}.png"));
                    var gif = Path.Combine(dir, "b.gif");
                    var (ok, error) = await ScreenGrabber.RunAsync("ffmpeg", GifEditor.FfmpegArgs(Path.Combine(dir, "o%05d.png"), "10", "0", gif, seeThrough: true), 60);
                    using var back = ok ? new Bitmap(gif) : null;
                    var top = back != null ? PixelAt(back, 100, 10) : default;
                    var below = back != null ? PixelAt(back, 100, 80) : default;
                    Check("GIF editor: a speech bubble stays see-through in the GIF (ffmpeg)", ok && top.A == 0 && below.A == 255,
                        ok ? $"inside the bubble {top}, below it {below}" : error);
                }
                finally { try { Directory.Delete(dir, true); } catch { } }
            });

            await TryAsync("video editor makes the finished video (ffmpeg)", async () =>
            {
                var folder = Path.Combine(Path.GetTempPath(), "overskin-video-" + Guid.NewGuid().ToString("N")[..6]);
                Directory.CreateDirectory(folder);
                var clip = Path.Combine(folder, "clip.mp4");
                // six seconds of a test picture with a tone
                var (made, why) = await ScreenGrabber.RunAsync("ffmpeg", $"-hide_banner -loglevel error -y -f lavfi -i testsrc=duration=6:size=320x240:rate=25 -f lavfi -i sine=frequency=440:duration=6 -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest \"{clip}\"", 120);
                if (!made) throw new InvalidOperationException(why);
                var p = new VideoProject { Duration = 6, Width = 320, Height = 240, HasSound = true, End = 6, Speed = 2, Crop = new Rect(0, 0, 0.5, 1) };
                p.Cuts.Add((2, 3));
                p.Overlays.Add(new VideoOverlay { Kind = VideoOverlay.Kinds.Caption, Text = "hello there", Start = 0.5, End = 1.5, Words = "Light", Look = "Bold" });
                p.Overlays.Add(new VideoOverlay { Kind = VideoOverlay.Kinds.Text, Text = "Hi", Start = 4, End = 5, At = new Point(0.5, 0.5) });
                var output = Path.Combine(folder, "out.mp4");
                await VideoEditor.MakeAsync(clip, p, output, Path.Combine(folder, "work"));
                var probe = await ScreenGrabber.OutputOf("ffprobe", $"-v error -show_entries stream=codec_type,width,height:format=duration -of csv=p=0 \"{output}\"") ?? "";
                var lines = probe.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                double length = double.Parse(lines.Last(), CultureInfo.InvariantCulture);
                bool video = lines.Any(l => l.StartsWith("video,160,240")), audio = lines.Any(l => l.StartsWith("audio"));
                await File.WriteAllTextAsync(Path.Combine(folder, "out.srt"), p.Srt());
                // five seconds kept, at double speed: two and a half
                Check("video editor makes the finished video (ffmpeg)", video && audio && Math.Abs(length - 2.5) < 0.15,
                      $"{string.Join(" | ", lines)}");
                Directory.Delete(folder, true);
            });

            await TryAsync("recording with webcam and mixed sound (ffmpeg)", async () =>
            {
                var folder = Path.Combine(Path.GetTempPath(), "overskin-rec-" + Guid.NewGuid().ToString("N")[..6]);
                Directory.CreateDirectory(folder);
                var output = Path.Combine(folder, "rec.mp4");
                // stand-ins: a white screen, a red camera, and two tones
                var args = Recorder.FfmpegArgs("-f lavfi -i color=c=white:size=640x400:rate=30:duration=2", "-f lavfi -i color=c=red:size=320x240:rate=30:duration=2",
                    new[] { "-f lavfi -i sine=frequency=300:duration=2", "-f lavfi -i sine=frequency=500:duration=2" }, new PixelRect(40, 40, 400, 320), 30, output);
                var (ok, why) = await ScreenGrabber.RunAsync("ffmpeg", "-hide_banner -loglevel error -y " + args, 120);
                if (!ok) throw new InvalidOperationException(why);
                var probe = await ScreenGrabber.OutputOf("ffprobe", $"-v error -show_entries stream=codec_type,width,height -of csv=p=0 \"{output}\"") ?? "";
                var still = Path.Combine(folder, "still.png");
                await ScreenGrabber.RunAsync("ffmpeg", $"-hide_banner -loglevel error -y -ss 1 -i \"{output}\" -frames:v 1 \"{still}\"", 60);
                using var frame = new Bitmap(still);
                // the bubble is 80 px (a quarter of 320), 24 px in from the bottom-right corner
                var bubble = PixelAt(frame, 400 - 24 - 40, 320 - 24 - 40);
                var corner = PixelAt(frame, 400 - 26, 320 - 26);
                var away = PixelAt(frame, 20, 20);
                Check("recording with webcam and mixed sound (ffmpeg)",
                      probe.Contains("video,400,320") && probe.Contains("audio") && bubble.R > 200 && bubble.G < 80 && corner.G > 200 && away.G > 200,
                      $"{probe.Replace('\n', ' ').Trim()}; bubble {bubble}, outside the circle {corner}, away {away}");
                Directory.Delete(folder, true);
            });

            await TryAsync("dim round the pointer and sound for subtitles (ffmpeg)", async () =>
            {
                var folder = Path.Combine(Path.GetTempPath(), "overskin-dim-" + Guid.NewGuid().ToString("N")[..6]);
                Directory.CreateDirectory(folder);
                var clip = Path.Combine(folder, "grey.mp4");
                var (made, why) = await ScreenGrabber.RunAsync("ffmpeg", $"-hide_banner -loglevel error -y -f lavfi -i color=c=0xC8C8C8:size=320x240:duration=2:rate=15 -f lavfi -i sine=frequency=300:duration=2 -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest \"{clip}\"", 120);
                if (!made) throw new InvalidOperationException(why);
                var track = new PointerTrack();
                track.AddForTest(0, 240, 120);
                track.AddForTest(2, 240, 120);
                await AutoZoom.DimAsync(clip, track, new PixelRect(0, 0, 320, 240), 0);
                var still = Path.Combine(folder, "still.png");
                await ScreenGrabber.RunAsync("ffmpeg", $"-hide_banner -loglevel error -y -ss 1 -i \"{clip}\" -frames:v 1 \"{still}\"", 60);
                using var frame = new Bitmap(still);
                var near = PixelAt(frame, 240, 120);
                var far = PixelAt(frame, 20, 20);
                var sound = await Overskin.Subtitles.ReadSoundAsync(clip);
                Check("dim round the pointer and sound for subtitles (ffmpeg)", near.G > 170 && far.G < 130 && Math.Abs(sound.Length - 32000) < 1600,
                      $"near the pointer {near.G}, far {far.G}; sound {sound.Length} samples at 16 kHz");
                Directory.Delete(folder, true);
            });

            await TryAsync("instant replay keeps the last seconds (ffmpeg)", async () =>
            {
                var folder = Path.Combine(Path.GetTempPath(), "overskin-replay-test-" + Guid.NewGuid().ToString("N")[..6]);
                Directory.CreateDirectory(folder);
                // 21 seconds of a stand-in screen, kept as 15 seconds (pieces of 5 s, 5 kept)
                var args = Replay.Args("-f lavfi -i testsrc=size=320x240:rate=30:duration=21", null, 15, 30, folder);
                var (ok, why) = await ScreenGrabber.RunAsync("ffmpeg", args, 180);
                if (!ok) throw new InvalidOperationException(why);
                int pieces = Directory.GetFiles(folder, "piece*.ts").Length;
                var output = Path.Combine(folder, "replay.mp4");
                await Replay.SaveFromAsync(folder, 15, output);
                var probe = (await ScreenGrabber.OutputOf("ffprobe", $"-v error -show_entries format=duration -of csv=p=0 \"{output}\"") ?? "").Trim();
                double length = double.Parse(probe, CultureInfo.InvariantCulture);
                Check("instant replay keeps the last seconds (ffmpeg)", pieces >= 4 && Math.Abs(length - 15) < 1.2, $"{pieces} pieces, saved {length:0.00}s");
                Directory.Delete(folder, true);
            });
        }

        Try("combine pictures", () =>
        {
            var a = Quarters(100, 60);
            var b = Quarters(80, 40);
            var side = Converter.Combine(new[] { a, b }, Converter.Layout.SideBySide, 10, Colors.White);
            var stack = Converter.Combine(new[] { a, b }, Converter.Layout.Stacked, 10, Colors.White);
            var grid = Converter.Combine(new[] { a, b, a }, Converter.Layout.Grid, 10, Colors.White);
            Check("combine pictures", side.PixelSize == new PixelSize(210, 80) && stack.PixelSize == new PixelSize(120, 130) && grid.PixelSize == new PixelSize(210, 150),
                  $"{side.PixelSize} {stack.PixelSize} {grid.PixelSize}");
        });

        Try("rename several files", () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "overskin-rename-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(folder);
            var files = new[] { "b.png", "a.png" }.Select(n => Path.Combine(folder, n)).ToList();
            foreach (var f in files) File.WriteAllText(f, "x");
            var tempNames = files.Select(f => f + ".overskin-rename").ToList();
            for (int i = 0; i < files.Count; i++) File.Move(files[i], tempNames[i]);
            for (int i = 0; i < files.Count; i++) File.Move(tempNames[i], Path.Combine(folder, $"Trip {i + 1}.png"));
            Check("rename several files", File.Exists(Path.Combine(folder, "Trip 1.png")) && File.Exists(Path.Combine(folder, "Trip 2.png")));
            Directory.Delete(folder, true);
        });

        Try("themes", () =>
        {
            foreach (var t in Theming.Themes) Theming.Apply(t.Name);
            Theming.Apply("Graphite");
            Check("themes", Colours.Bg == Color.Parse("#121316"), $"{Theming.Themes.Length} applied");
        });

        Try("auto-hide finds a card and its details", () =>
        {
            const string tsv = "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n" +
                               "5\t1\t1\t1\t1\t1\t10\t10\t40\t20\t96\tCard\n" +
                               "5\t1\t1\t1\t1\t2\t60\t10\t40\t20\t91\t4lll\n" +
                               "5\t1\t1\t1\t1\t3\t110\t10\t40\t20\t93\t1111\n" +
                               "5\t1\t1\t1\t1\t4\t160\t10\t40\t20\t92\t1111\n" +
                               "5\t1\t1\t1\t1\t5\t210\t10\t40\t20\t95\t1111\n" +
                               "5\t1\t1\t1\t2\t1\t10\t40\t40\t20\t96\tExp\n" +
                               "5\t1\t1\t1\t2\t2\t60\t40\t50\t20\t96\t09/27\n" +
                               "5\t1\t1\t1\t2\t3\t120\t40\t40\t20\t96\tCVC\n" +
                               "5\t1\t1\t1\t2\t4\t170\t40\t30\t20\t96\t737\n" +
                               "5\t1\t1\t1\t3\t1\t10\t70\t200\t20\t96\t2001:db8::7334\n";
            var lines = Ocr.Parse(tsv);
            var words = lines.Select(l => (IReadOnlyList<Overskin.Shared.TextWord>)l.Select(w => new Overskin.Shared.TextWord(w.Text, w.Bounds.X, w.Bounds.Y, w.Bounds.Width, w.Bounds.Height)).ToList()).ToList();
            var found = Overskin.Shared.PrivateDetails.Find(words);
            var kinds = string.Join(", ", found.Select(f => f.Kind));
            Check("auto-hide finds a card and its details", kinds == "card number, IP address, expiry date, security code", kinds);
        });

        Try("selection maps to picture pixels", () =>
        {
            var surface = new SelectionSurface(Quarters(400, 200));
            surface.Measure(new Size(200, 100));
            surface.Arrange(new Rect(0, 0, 200, 100)); // shown at half size, as on a 2x screen
            var r = surface.ToPixels(new Point(50, 80), new Point(10, 20));
            Check("selection maps to picture pixels", r == new PixelRect(20, 40, 80, 120), r.ToString());
        });

        await TryAsync("a second start hands its command over", async () =>
        {
            var got = new TaskCompletionSource<string[]>();
            using var stop = new CancellationTokenSource();
            SingleInstance.Listen(a => got.TrySetResult(a), stop.Token);
            bool sent = false;
            for (int i = 0; i < 20 && !sent; i++) { sent = SingleInstance.TrySend(new[] { "--capture", "--delay", "2" }); if (!sent) await Task.Delay(100); }
            var done = await Task.WhenAny(got.Task, Task.Delay(3000));
            stop.Cancel();
            Check("a second start hands its command over",
                  sent && done == got.Task && string.Join(" ", got.Task.Result) == "--capture --delay 2");
        });

        if (OperatingSystem.IsLinux() && !ScreenGrabber.IsWayland && X11.IsAvailable)
        {
            Try("X11 screen grab", () =>
            {
                var shot = X11.GrabRoot();
                var p = PixelAt(shot, shot.PixelSize.Width / 2, shot.PixelSize.Height / 2);
                Check("X11 screen grab", shot.PixelSize.Width > 0 && p.A == 255, $"{shot.PixelSize}");
            });
        }
        else Console.WriteLine("skip  X11 screen grab (no X display here)");

        Try("spotlight darkens round the box", () =>
        {
            var grey = Imaging.Render(new PixelSize(200, 100), ctx => ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(200, 200, 200)), new Rect(0, 0, 200, 100)));
            var canvas = new EditorCanvas(grey);
            canvas.AddRange(new Mark[] { new SpotlightMark { Area = new Rect(50, 25, 100, 50) }, new SpotlightMark { Area = new Rect(60, 30, 40, 30) } });
            var shot = canvas.Export();
            var outside = PixelAt(shot, 10, 10);
            var inside = PixelAt(shot, 100, 50);
            Check("spotlight darkens round the box", outside.G is > 60 and < 100 && inside.G == 200, $"outside {outside.G}, inside {inside.G} (overlapping spots don't darken each other)");
        });

        Try("social sizes come out exactly that big", () =>
        {
            var pic = Quarters(300, 200);
            var insta = Beautify.Render(pic, new BeautifyOptions { OutputWidth = 1080, OutputHeight = 1350, Aspect = 1080 / 1350.0 });
            var yt = Beautify.Render(pic, new BeautifyOptions { OutputWidth = 1280, OutputHeight = 720, Aspect = 1280 / 720.0 });
            Check("social sizes come out exactly that big", insta.PixelSize == new PixelSize(1080, 1350) && yt.PixelSize == new PixelSize(1280, 720),
                  $"{insta.PixelSize} {yt.PixelSize}");
        });

        Try("effects: border, torn edge and filters", () =>
        {
            var pic = Imaging.Render(new PixelSize(120, 80), ctx => ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(200, 50, 50)), new Rect(0, 0, 120, 80)));
            var bordered = PictureEffects.Render(pic, new EffectsOptions { Border = 10, BorderColour = "#3B82F6" });
            var edge = PixelAt(bordered, 3, 40);
            var torn = PictureEffects.Render(pic, new EffectsOptions { Torn = "Bottom" });
            var bottom = PixelAt(torn, 60, 79);
            var grey = PixelAt(PictureEffects.Render(pic, new EffectsOptions { Filter = "Grayscale" }), 60, 40);
            var inverted = PixelAt(PictureEffects.Render(pic, new EffectsOptions { Filter = "Invert" }), 60, 40);
            Check("effects: border, torn edge and filters",
                  bordered.PixelSize == new PixelSize(140, 100) && edge.B > 200 && edge.R < 80 && bottom.A < 60 && grey.R == grey.G && grey.G == grey.B && inverted.R == 55,
                  $"border {edge}, torn bottom alpha {bottom.A}, grey {grey.R}, inverted red {inverted.R}");
        });

        Try("watermark goes where it's told", () =>
        {
            var pic = Imaging.Render(new PixelSize(400, 300), ctx => ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(40, 40, 40)), new Rect(0, 0, 400, 300)));
            var marked = Watermark.Apply(pic, new WatermarkOptions { Text = "@overskin", Position = "BottomRight", Size = 12, Opacity = 100 });
            var px = Imaging.Pixels(marked, out int stride);
            int Bright(int x0, int y0, int x1, int y1)
            {
                int n = 0;
                for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) if (px[y * stride + x * 4 + 1] > 150) n++;
                return n;
            }
            int corner = Bright(250, 240, 400, 300), elsewhere = Bright(0, 0, 200, 150);
            var tiled = Watermark.Apply(pic, new WatermarkOptions { Text = "@overskin", Position = "Tiled", Size = 6, Opacity = 100 });
            var tpx = Imaging.Pixels(tiled, out _);
            int tiledBright = 0;
            for (int i = 1; i < tpx.Length; i += 4) if (tpx[i] > 150) tiledBright++;
            Check("watermark goes where it's told", corner > 100 && elsewhere == 0 && tiledBright > corner, $"corner {corner} bright px, elsewhere {elsewhere}, tiled {tiledBright}");
        });

        if (Ai.Available)
        {
            await TryAsync("AI: cut out and picture search", async () =>
            {
                // a red ball on a plain grey background, as in the Windows check
                var ball = Imaging.Render(new PixelSize(400, 300), ctx =>
                {
                    ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(200, 200, 200)), new Rect(0, 0, 400, 300));
                    ctx.DrawEllipse(new RadialGradientBrush { GradientStops = { new GradientStop(Color.FromRgb(255, 90, 80), 0), new GradientStop(Color.FromRgb(170, 20, 20), 1) } },
                        null, new Point(200, 150), 80, 80);
                });
                var px = Ai.Of(ball);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var cut = await Task.Run(() => Ai.CutOut(px));
                long cutMs = watch.ElapsedMilliseconds;
                byte corner = 255, middle = 0;
                if (cut is { } c)
                {
                    corner = c.Data[3];
                    middle = c.Data[(c.Height / 2 * c.Width + c.Width / 2) * 4 + 3];
                }
                watch.Restart();
                var (redBall, dog) = await Task.Run(() =>
                {
                    var print = Ai.PictureFingerprint(px);
                    return (Ai.Likeness(print, Ai.WordsFingerprint("a picture of a red ball")), Ai.Likeness(print, Ai.WordsFingerprint("a picture of a dog")));
                });
                var tokens = string.Join(", ", Overskin.ClipTokenizer.Encode("a photo of a cat"));
                Check("AI: cut out and picture search",
                      cut is { Width: > 150 and < 200, Height: > 150 and < 200 } && corner < 20 && middle > 250 && redBall > dog && tokens == "49406, 320, 1125, 539, 320, 2368, 49407",
                      $"cut {cut?.Width}x{cut?.Height} corner alpha {corner} middle {middle} in {cutMs} ms; red ball {redBall:0.000} vs dog {dog:0.000} in {watch.ElapsedMilliseconds} ms; tokens {tokens}");
            });
        }
        else Console.WriteLine("skip  AI (built without the models)");

        Try("favourites and tags", () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "overskin-marks-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(folder);
            var a = Path.Combine(folder, "a.png");
            var b = Path.Combine(folder, "b.png");
            Imaging.SavePng(Quarters(20, 20), a);
            Imaging.SavePng(Quarters(20, 20), b);
            LibraryMarks.SetFavourite(new[] { a }, true);
            LibraryMarks.SetTags(b, LibraryMarks.Parse("#work, receipts #work"));
            LibraryMarks.Renamed(b, b + ".x");
            File.Move(b, b + ".x");
            Check("favourites and tags", LibraryMarks.IsFavourite(a) && !LibraryMarks.IsFavourite(b + ".x")
                  && string.Join(" ", LibraryMarks.TagsOf(b + ".x")) == "work receipts",
                  $"tags {string.Join(" ", LibraryMarks.TagsOf(b + ".x"))}");
            Directory.Delete(folder, true);
        });

        Try("duplicates are found, different pictures aren't", () =>
        {
            var page = Imaging.Render(new PixelSize(300, 200), ctx =>
            {
                ctx.FillRectangle(Brushes.White, new Rect(0, 0, 300, 200));
                for (int i = 0; i < 8; i++) ctx.FillRectangle(Brushes.Black, new Rect(20, 20 + i * 20, 120 + i * 15, 8));
            });
            var other = Imaging.Render(new PixelSize(300, 200), ctx =>
            {
                ctx.FillRectangle(Brushes.White, new Rect(0, 0, 300, 200));
                for (int i = 0; i < 8; i++) ctx.FillRectangle(Brushes.Black, new Rect(20, 20 + i * 20, 240 - i * 15, 8));
            });
            var smaller = Effects.Resize(page, 294, 196);
            var p1 = Duplicates.PrintOf(Ai.Of(page));
            bool same = Duplicates.Same(p1, Duplicates.PrintOf(Ai.Of(smaller)));
            bool different = Duplicates.Same(p1, Duplicates.PrintOf(Ai.Of(other)));
            Check("duplicates are found, different pictures aren't", same && !different, $"saved smaller: {same}, another page: {different}");
        });

        await TryAsync("send to phone: the page and the QR code", async () =>
        {
            var file = Path.Combine(Path.GetTempPath(), $"overskin-phone-{Guid.NewGuid():N}.png");
            Imaging.SavePng(Quarters(40, 20), file);
            using var share = Overskin.PhoneShare.Start(file);
            if (share == null) { Console.WriteLine("skip  send to phone (no home network address here)"); return; }
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var page = await http.GetStringAsync(share.Url);
            var bytes = await http.GetByteArrayAsync(share.Url + "/file");
            var read = QrReader.Read(PhoneWindow.QrCode(share.Url, 300));
            Check("send to phone: the page and the QR code", page.Contains("Save to Photos") && bytes.Length == new FileInfo(file).Length && read == share.Url,
                  $"page {page.Length} chars, file {bytes.Length} B, QR says {(read == share.Url ? "the address" : read ?? "nothing")}");
            File.Delete(file);
        });

        await TryAsync("recording: stop after, countdown, dim round the pointer", async () =>
        {
            bool stopped = false;
            var bar = new RecordBar(gif: false, () => stopped = true, () => { }, stopAfter: 1);
            await Task.Delay(1600);
            bar.Close();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var run = Countdown.RunAsync(new PixelRect(100, 100, 400, 300), new PixelRect(0, 0, 800, 600), seconds: 1);
            bool went = await run;
            long ms = watch.ElapsedMilliseconds;
            var frame = Enumerable.Repeat((byte)200, 200 * 100 * 4).ToArray();
            Overskin.Spotlight.Apply(frame, 200, 100, 150, 50, 30);
            Check("recording: stop after, countdown, dim round the pointer",
                  stopped && went && ms is > 900 and < 2000 && frame[(50 * 200 + 150) * 4] == 200 && frame[(10 * 200 + 10) * 4] == 100,
                  $"stopped by itself {stopped}, countdown went {went} in {ms} ms, near pointer {frame[(50 * 200 + 150) * 4]}, far {frame[(10 * 200 + 10) * 4]}");
        });

        Try("chat names and profile pictures are found", () =>
        {
            var line = new List<OcrWord>
            {
                new("Finn", new Rect(80, 40, 30, 15)), new("Lax", new Rect(114, 40, 25, 15)),
                new("Today", new Rect(150, 40, 40, 15)), new("at", new Rect(194, 40, 14, 15)), new("3:45", new Rect(212, 40, 30, 15)), new("PM", new Rect(246, 40, 20, 15)),
            };
            var mention = new List<OcrWord> { new("hey", new Rect(80, 70, 25, 15)), new("@sam", new Rect(110, 70, 35, 15)), new("mail@x.com", new Rect(150, 70, 70, 15)) };
            var found = People.ChatNames(new[] { (IReadOnlyList<OcrWord>)line, mention }, 400, 200);
            var kinds = string.Join(", ", found.Select(f => f.Kind));
            Check("chat names and profile pictures are found", kinds == "name, profile picture, name" && found[0].X < 81 && found[0].X + found[0].Width < 145, kinds);
        });

        Try("search everything finds the right thing", () =>
        {
            var items = App.Current.PaletteItems();
            string Top(string q) => items.Select((it, i) => (it, i, s: CommandSearch.Score(it, q))).Where(x => x.s > 0)
                .OrderByDescending(x => x.s).ThenBy(x => x.i).Select(x => x.it.Title).FirstOrDefault() ?? "(nothing)";
            var results = new[] { ("colour", "Colour picker"), ("qr", "Text or QR code from screen"), ("rec gif", "Record a GIF"), ("measure", "Ruler"), ("zzzz", "(nothing)") };
            var wrong = results.Where(r => Top(r.Item1) != r.Item2).Select(r => $"{r.Item1} → {Top(r.Item1)}").ToList();
            Check("search everything finds the right thing", wrong.Count == 0 && items.Count > 20, wrong.Count == 0 ? $"{items.Count} things to find" : string.Join(", ", wrong));
        });

        Try("the box fits what's inside it", () =>
        {
            var page = Imaging.Render(new PixelSize(300, 200), ctx =>
            {
                ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(240, 240, 240)), new Rect(0, 0, 300, 200));
                ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(40, 90, 200)), new Rect(100, 60, 80, 50));
            });
            var surface = new SelectionSurface(page);
            bool was = App.Settings.Tighten;
            App.Settings.Tighten = true;
            var fitted = surface.Fitted(new PixelRect(70, 30, 160, 120));
            App.Settings.Tighten = false;
            var loose = surface.Fitted(new PixelRect(70, 30, 160, 120));
            App.Settings.Tighten = was;
            Check("the box fits what's inside it", fitted == new PixelRect(100, 60, 80, 50) && loose == new PixelRect(70, 30, 160, 120), $"fitted {fitted}, off {loose}");
        });

        Try("settings search", () =>
        {
            Control Card(string heading, params string[] rows)
            {
                var panel = new StackPanel { Children = { Ui.Label(heading, "h2") } };
                foreach (var r in rows) panel.Children.Add(new CheckBox { Content = r });
                return Ui.Card(panel);
            }
            var general = Card("General", "Open this window when Overskin starts", "Move old captures to the trash");
            var look = Card("Look", "Mode", "Theme");
            var keys = Card("Hotkeys", "Capture an area", "Colour picker");
            var cards = new[] { general, look, keys };
            int trash = MainWindow.FilterSettings(cards, "trash");
            bool rowOnly = general.IsVisible && !look.IsVisible && !((StackPanel)((Border)general).Child!).Children[1].IsVisible;
            int heading = MainWindow.FilterSettings(cards, "look");
            int none = MainWindow.FilterSettings(cards, "zzz");
            int all = MainWindow.FilterSettings(cards, "");
            Check("settings search", trash == 1 && rowOnly && heading == 1 && none == 0 && all == 3, $"trash {trash}, look {heading}, nonsense {none}, cleared {all}");
        });

        Try("old captures are found for the trash, favourites kept", () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "overskin-clean-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(folder);
            string Make(string name, int daysOld)
            {
                var p = Path.Combine(folder, name);
                File.WriteAllText(p, "x");
                File.SetLastWriteTime(p, DateTime.Now.AddDays(-daysOld));
                return p;
            }
            var old = Make("old.png", 40);
            var fav = Make("fav.png", 40);
            Make("new.png", 2);
            Make("notes.txt", 400);
            LibraryMarks.SetFavourite(new[] { fav }, true);
            var found = App.OldCaptures(folder, 30, DateTime.Now).Select(Path.GetFileName).ToList();
            Check("old captures are found for the trash, favourites kept", found.SequenceEqual(new[] { "old.png" }) && App.OldCaptures(folder, 0, DateTime.Now).Count == 0,
                  string.Join(", ", found));
            Directory.Delete(folder, true);
        });

        Try("preview card", () =>
        {
            var card = typeof(PreviewCard);
            Check("preview card", PreviewCard.LeftFade(0) == 1 && PreviewCard.LeftFade(120) < 0.01 && PreviewCard.LeftFade(80) is > 0.2 and < 0.5,
                  $"fade at 0 {PreviewCard.LeftFade(0):0.00}, 80 {PreviewCard.LeftFade(80):0.00}, 120 {PreviewCard.LeftFade(120):0.00}");
        });

        Try("fonts that come with Overskin load", () =>
        {
            var missing = new List<string>();
            var widths = new Dictionary<string, double>();
            foreach (var name in new[] { "Anton", "Bebas Neue", "Oswald", "Bangers", "Permanent Marker", "Comic Neue", "Roboto" })
            {
                var face = FontChoices.Face(name);
                if (!FontManager.Current.TryGetGlyphTypeface(face, out var g) || !g.FamilyName.StartsWith(name.Split(' ')[0], StringComparison.OrdinalIgnoreCase))
                    missing.Add(name);
                widths[name] = new FormattedText("caption text", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 40, Brushes.Black).Width;
            }
            var marked = new EditorCanvas(Quarters(300, 100));
            marked.AddRange(new Mark[] { new TextMark { At = new Point(10, 20), Text = "Hello", Size = 40, Colour = Colors.Black, FontName = "Bangers" } });
            Check("fonts that come with Overskin load", missing.Count == 0 && widths.Values.Distinct().Count() == widths.Count && marked.Export().PixelSize.Width == 300,
                  missing.Count == 0 ? $"{FontChoices.All.Count} offered: {string.Join(", ", FontChoices.All.Select(f => f.Name))}" : "missing: " + string.Join(", ", missing));
        });

        Try("file names from a pattern", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "overskin-names-" + Guid.NewGuid().ToString("N")[..6]);
            var when = new DateTime(2026, 11, 21, 18, 4, 33);
            var info = ("Safari", "Docs: Getting started");
            string Name(string pattern, string ext = ".png") => Path.GetRelativePath(root, FileNames.NewPath(root, ext, pattern, info, when));
            var plain = Name(FileNames.Default);
            var folders = Name("{app}/{date} {time}");
            var typed = Name("{type} {title}", ".gif");
            File.WriteAllText(Path.Combine(root, "Shot 001.png"), "x");
            var counted = Name("Shot {n}");
            var old = Name("Overskin_{0:yyyy-MM-dd_HH-mm-ss}");
            var expected = new[] { "Overskin 2026-11-21 18.04.33.png", Path.Combine("Safari", "2026-11-21 18.04.33.png"), "GIF Docs Getting started.gif", "Shot 002.png", "Overskin_2026-11-21_18-04-33.png" };
            var got = new[] { plain, folders, typed, counted, old };
            Check("file names from a pattern", got.SequenceEqual(expected), string.Join(" | ", got));
            Directory.Delete(root, true);
        });

        await TryAsync("live pins follow their window", async () =>
        {
            int calls = 0;
            var pin = new PinWindow(Quarters(40, 20));
            pin.Live(() => Task.FromResult<Bitmap?>(++calls < 3 ? Quarters(60 + calls * 10, 30) : null), TimeSpan.FromMilliseconds(100));
            pin.Show();
            for (int i = 0; i < 20 && pin.IsLive; i++) await Task.Delay(100);
            var size = pin.PictureSize;
            pin.Close();
            Check("live pins follow their window", size == new PixelSize(80, 30) && !pin.IsLive && calls == 3,
                  $"showing {size} after {calls} looks; stopped when the window went: {!pin.IsLive}");
        });

        Try("GIF editor: captions, colours, crop, mirror, looks", () =>
        {
            var frame = Quarters(200, 100); // red top-left, green top-right, blue bottom-left, white bottom-right
            GifEditor.GifLook Look(int style = 0, string text = "", Color? colour = null, bool above = true, int look = 0, bool mirror = false, Rect? crop = null, string font = "Anton") =>
                new(0, style, text, "", 32, font, colour ?? Colors.White, above, look, mirror, crop);
            var plain = GifEditor.RenderFrame(frame, Look());
            var barAbove = GifEditor.RenderFrame(frame, Look(1, "hello", Colors.Black, above: true));
            var barBelow = GifEditor.RenderFrame(frame, Look(1, "hello", Color.Parse("#FACC15"), above: false));
            var mirrored = GifEditor.RenderFrame(frame, Look(mirror: true));
            var cropped = GifEditor.RenderFrame(frame, Look(crop: new Rect(0.5, 0, 0.5, 0.5)));
            var grey = GifEditor.RenderFrame(frame, Look(look: 1));
            var bubble = GifEditor.RenderFrame(frame, Look(3, "hi there"));
            var otherFont = GifEditor.RenderFrame(frame, Look(1, "hello", Colors.White, font: "Bangers"));
            var whiteBar = GifEditor.RenderFrame(frame, Look(1, "hello", Colors.White));
            bool fontChanges = !Imaging.Pixels(otherFont, out _).SequenceEqual(Imaging.Pixels(whiteBar, out _));
            var topLeftAbove = PixelAt(barAbove, 1, 1);
            var bottomBelow = PixelAt(barBelow, 1, barBelow.PixelSize.Height - 2);
            var mirroredCorner = PixelAt(mirrored, 2, 2);
            var croppedCorner = PixelAt(cropped, 2, 2);
            var greyCorner = PixelAt(grey, 2, 2);
            var bubbleTop = PixelAt(bubble, 100, 10);
            var bubbleBelow = PixelAt(bubble, 100, 80);
            // the tail: near its tip on the left by default, and not there when it points right
            var tailLeft = PixelAt(bubble, 46, 32);
            var tailRight = PixelAt(GifEditor.RenderFrame(frame, Look(3) with { Tail = "Right" }), 46, 32);
            Check("GIF editor: captions, colours, crop, mirror, looks",
                  plain.PixelSize == new PixelSize(200, 100) && barAbove.PixelSize.Height > 100 && topLeftAbove.R < 30 && bottomBelow is { R: > 200, B: < 60 }
                  && mirroredCorner.G > 200 && mirroredCorner.R < 60 && cropped.PixelSize.Width == 100 && croppedCorner.G > 200
                  && greyCorner.R == greyCorner.G && bubbleTop.A == 0 && bubbleBelow.A == 255 && tailLeft.A == 0 && tailRight.A == 255 && fontChanges,
                  $"bar above {topLeftAbove}, yellow bar below {bottomBelow}, mirrored corner {mirroredCorner}, cropped {cropped.PixelSize} {croppedCorner}, grey {greyCorner}, " +
                  $"bubble {bubbleTop} / below {bubbleBelow} / tail {tailLeft} {tailRight}, font changes {fontChanges}");
        });

        Try("video editor: trims, cuts, speed and times", () =>
        {
            var p = new VideoProject { Duration = 20, Width = 1280, Height = 720, HasSound = true, Start = 2, End = 18 };
            p.Cuts.Add((8, 10));
            p.Speed = 2;
            var kept = p.Kept();
            bool keptOk = kept.Count == 2 && kept[0] == (2, 8) && kept[1] == (10, 18);
            // 2 s in is the start; 9 s is cut out; 12 s is 6 + 2 = 8 s of kept video, at double speed 4 s
            bool times = p.ToOutput(2) == 0 && p.ToOutput(9) == null && Math.Abs(p.ToOutput(12)!.Value - 4) < 1e-9 && Math.Abs(p.OutputLength - 7) < 1e-9;
            p.Overlays.Add(new VideoOverlay { Kind = VideoOverlay.Kinds.Caption, Text = "First line", Start = 3, End = 5 });
            p.Overlays.Add(new VideoOverlay { Kind = VideoOverlay.Kinds.Caption, Text = "Cut out", Start = 8.5, End = 9.5 });
            var srt = p.Srt().Replace("\r", "");
            p.Crop = new Rect(0.25, 0, 0.5, 1);
            var filter = p.Filter(new[] { (0.5, 1.5) });
            bool filterOk = filter.Contains("trim=start=2:end=8") && filter.Contains("trim=start=10:end=18") && filter.Contains("concat=n=2:v=1:a=1")
                            && filter.Contains("crop=640:720:320:0") && filter.Contains("setpts=PTS/2") && filter.Contains("atempo=2") && filter.Contains("enable='between(t,0.5,1.5)'");
            Check("video editor: trims, cuts, speed and times",
                  keptOk && times && srt.Contains("00:00:00,500 --> 00:00:01,500\nFirst line") && !srt.Contains("Cut out") && filterOk
                  && VideoProject.Tempo(3) == "atempo=2,atempo=1.5" && VideoProject.Tempo(0.25) == "atempo=0.5,atempo=0.5",
                  $"kept {string.Join(" ", kept)}, 12 s → {p.ToOutput(12):0.##} s, length {p.OutputLength:0.##} s, srt {srt.Split('\n').Length} lines, filter ok {filterOk}");
        });

        Try("video editor: caption looks and word by word", () =>
        {
            var line = new VideoOverlay { Kind = VideoOverlay.Kinds.Caption, Text = "hello there friend", Start = 0, End = 3, Look = "Classic", Words = "Light" };
            var parts = CaptionLook.Expand(new[] { line });
            Bitmap Draw(VideoOverlay o) => Imaging.Render(new PixelSize(640, 360), ctx => CaptionLook.Draw(ctx, o, 640, 360));
            int Lit(Bitmap b, byte r, byte g)
            {
                var px = Imaging.Pixels(b, out _);
                int n = 0;
                for (int i = 0; i < px.Length; i += 4) if (px[i + 3] > 200 && Math.Abs(px[i + 2] - r) < 20 && Math.Abs(px[i + 1] - g) < 20 && px[i] < 120) n++;
                return n;
            }
            var first = parts[0].Clone();
            int yellow = Lit(Draw(first), 0xFF, 0xD3);
            var whole = line.Clone();
            whole.Words = "Line";
            int none = Lit(Draw(whole), 0xFF, 0xD3);
            var boxed = parts[1].Clone();
            boxed.Look = "Boxed";
            int green = Lit(Draw(boxed), 0x22, 0xC5);
            var build = parts[0].Clone();
            build.Words = "Build";
            int builtInk = Imaging.Pixels(Draw(build), out _).Where((_, i) => i % 4 == 3).Count(a => a > 200);
            var all = whole.Clone();
            int wholeInk = Imaging.Pixels(Draw(all), out _).Where((_, i) => i % 4 == 3).Count(a => a > 200);
            Check("video editor: caption looks and word by word",
                  parts.Count == 3 && Math.Abs(parts[0].End - parts[1].Start) < 1e-9 && parts[2].End == 3 && yellow > 50 && none == 0 && green > 50 && builtInk > 0,
                  $"{parts.Count} parts, lit word {yellow} px (whole line {none}), boxed word {green} px green, built-up ink {builtInk} vs whole {wholeInk}");
        });

        Try("subtitles: the speech engine loads", () =>
        {
            if (!Overskin.Subtitles.Available) { Console.WriteLine("skip  subtitles engine (built without the speech model)"); return; }
            using var stream = typeof(Overskin.Subtitles).Assembly.GetManifestResourceStream("Overskin.whisper-model.bin")!;
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            using var factory = Whisper.net.WhisperFactory.FromBuffer(bytes);
            using var processor = factory.CreateBuilder().WithLanguage("auto").Build();
            Check("subtitles: the speech engine loads", processor != null, $"model {bytes.Length / 1048576} MB");
        });

        Try("Mac recording devices are told apart", () =>
        {
            const string listing = """
                [AVFoundation indev @ 0x7f8] AVFoundation video devices:
                [AVFoundation indev @ 0x7f8] [0] FaceTime HD Camera
                [AVFoundation indev @ 0x7f8] [1] Capture screen 0
                [AVFoundation indev @ 0x7f8] [2] Capture screen 1
                [AVFoundation indev @ 0x7f8] AVFoundation audio devices:
                [AVFoundation indev @ 0x7f8] [0] BlackHole 2ch
                [AVFoundation indev @ 0x7f8] [1] MacBook Pro Microphone
                """;
            var list = Devices.ParseMac(listing);
            var screen2 = Devices.Screen(list, 1);
            Check("Mac recording devices are told apart",
                  Devices.Camera(list)?.Index == "0" && Devices.Screen(list, 0)?.Index == "1" && screen2?.Index == "2"
                  && Devices.Loopback(list)?.Index == "0" && Devices.Microphone(list)?.Index == "1",
                  $"camera {Devices.Camera(list)?.Name}, screens {Devices.Screen(list, 0)?.Index}/{screen2?.Index}, loopback {Devices.Loopback(list)?.Name}, mic {Devices.Microphone(list)?.Name}");
        });

        Try("QR codes are read", () =>
        {
            var writer = new ZXing.BarcodeWriterPixelData
            {
                Format = ZXing.BarcodeFormat.QR_CODE,
                Options = new ZXing.Common.EncodingOptions { Width = 180, Height = 180, Margin = 2 },
            };
            var data = writer.Write("https://overskin.app/test");
            // ZXing draws BGRA; shown on a grey background like a code on screen
            var code = Imaging.FromPixels(data.Pixels, new PixelSize(data.Width, data.Height));
            var page = Imaging.Render(new PixelSize(400, 260), ctx =>
            {
                ctx.FillRectangle(Brushes.Gray, new Rect(0, 0, 400, 260));
                ctx.DrawImage(code, new Rect(110, 40, 180, 180));
            });
            var text = QrReader.Read(page);
            Check("QR codes are read", text == "https://overskin.app/test" && QrReader.LinkIn(text!) != null, text ?? "nothing found");
        });

        Try("mixed-up letters and numbers are fixed", () =>
        {
            Check("mixed-up letters and numbers are fixed",
                  Overskin.OcrFix.Word("HELL0") == "HELLO" && Overskin.OcrFix.Word("2O26") == "2026" && Overskin.OcrFix.Word("1st") == "1st",
                  $"{Overskin.OcrFix.Word("HELL0")} {Overskin.OcrFix.Word("2O26")} {Overskin.OcrFix.Word("1st")}");
        });

        Try("colours are written as hex", () =>
            Check("colours are written as hex", ScreenTools.Hex(Color.FromRgb(255, 140, 8)) == "#FF8C08", ScreenTools.Hex(Color.FromRgb(255, 140, 8))));

        Try("colours stay true in red-first pictures (as a Mac loads them)", () =>
        {
            // a Mac decodes pictures red-first; this makes one like that, with a known green
            var mac = new WriteableBitmap(new PixelSize(4, 2), new Vector(96, 96), Avalonia.Platform.PixelFormat.Rgba8888, Avalonia.Platform.AlphaFormat.Premul);
            using (var fb = mac.Lock())
                unsafe
                {
                    var p = (byte*)fb.Address;
                    for (int y = 0; y < 2; y++)
                        for (int x = 0; x < 4; x++)
                        {
                            var q = p + y * fb.RowBytes + x * 4;
                            q[0] = 80; q[1] = 160; q[2] = 30; q[3] = 255; // R 80, G 160, B 30: a grass green
                        }
                }
            var px = Imaging.Pixels(mac, out _);
            var crop = Imaging.Crop(mac, new PixelRect(1, 0, 2, 2));
            var cropped = PixelAt(crop, 0, 0);
            bool ok = px[0] == 30 && px[1] == 160 && px[2] == 80 && cropped is { B: 30, G: 160, R: 80 };
            Check("colours stay true in red-first pictures (as a Mac loads them)", ok,
                $"read as B{px[0]} G{px[1]} R{px[2]}, cropped {cropped}");
        });

        Try("the Windows icons all draw", () =>
        {
            var bad = new List<string>();
            int count = 0;
            foreach (var f in typeof(Lucide).GetFields())
            {
                count++;
                try
                {
                    var g = StreamGeometry.Parse((string)f.GetValue(null)!);
                    var b = g.Bounds;
                    if (b.Width <= 0 && b.Height <= 0 || b.Right > 24.5 || b.Bottom > 24.5 || b.X < -0.5 || b.Y < -0.5) bad.Add(f.Name + " " + b);
                }
                catch (Exception ex) { bad.Add(f.Name + ": " + ex.Message); }
            }
            Check("the Windows icons all draw", count > 100 && bad.Count == 0, bad.Count == 0 ? $"{count} icons" : string.Join("; ", bad.Take(5)));
        });

        Try("every page of the main window builds", () =>
        {
            var main = new MainWindow();
            var pages = new List<string>();
            foreach (var page in new[] { "Capture", "Record", "Library", "Pins", "Settings" })
            {
                main.Go(page);
                if (((ContentControl)((DockPanel)main.Content!).Children[2].GetLogicalChildren().OfType<ContentControl>().First()).Content is Control c) pages.Add(page);
            }
            main.Close();
            Check("every page of the main window builds", pages.Count == 5, string.Join(", ", pages));
        });

        Try("how-to guides are saved", () =>
        {
            var steps = new List<GuideStep>
            {
                new() { Picture = Quarters(320, 200), Caption = "Click \"Settings\" <here> & wait" },
                new() { Picture = Quarters(200, 300), Caption = "Pick a theme" },
                new() { Picture = Quarters(100, 100), Caption = "Left out", Keep = false },
            };
            var kept = steps.Where(s => s.Keep).ToList();
            var dir = Path.Combine(Path.GetTempPath(), $"overskin-guide-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var accent = Color.Parse("#4F8CFF");
                string html = Path.Combine(dir, "g.html"), md = Path.Combine(dir, "g.md"), pdf = Path.Combine(dir, "g.pdf");
                StepGuide.SaveHtml(kept, "My guide", accent, html);
                StepGuide.SaveMarkdown(kept, "My guide", md);
                StepGuide.SavePdf(kept, "My guide", accent, pdf);
                var page = File.ReadAllText(html);
                var notes = File.ReadAllText(md);
                var pdfBytes = File.ReadAllBytes(pdf);
                var pdfText = System.Text.Encoding.ASCII.GetString(pdfBytes);
                using var tall = StepGuide.TallPicture(kept, "My guide", accent);
                bool htmlOk = page.Contains("My guide") && page.Contains("&lt;here&gt;") && page.Contains("data:image/") && !page.Contains("Left out");
                bool mdOk = notes.Contains("# My guide") && notes.Contains("Pick a theme") && !notes.Contains("Left out");
                bool pdfOk = pdfText.StartsWith("%PDF-") && pdfText.TrimEnd().EndsWith("%%EOF") && pdfText.Contains("/Count 2 ");
                mdOk &= File.Exists(Path.Combine(dir, "g images", "step-01.png")) && File.Exists(Path.Combine(dir, "g images", "step-02.png"));
                bool tallOk = tall.PixelSize.Width == 1200 && tall.PixelSize.Height > 400;
                Check("how-to guides are saved", htmlOk && mdOk && pdfOk && tallOk,
                    $"page {htmlOk}, notes {mdOk}, PDF {pdfOk} ({pdfBytes.Length / 1024} KB), picture {tall.PixelSize}");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        });

        // ---- the Mac parts that can be checked anywhere ----
        Try("Mac hotkeys are understood", () =>
        {
            bool a = Mac.TryParseHotKey("Ctrl+Alt+S", out uint key, out uint mods);
            bool b = Mac.TryParseHotKey("Cmd+Shift+2", out uint key2, out uint mods2);
            bool bad = Mac.TryParseHotKey("S", out _, out _) || Mac.TryParseHotKey("Ctrl+Nope", out _, out _);
            Check("Mac hotkeys are understood", a && key == 1 && mods == (1 << 12 | 1 << 11) && b && key2 == 19 && mods2 == (1 << 8 | 1 << 9) && !bad
                  && Mac.Symbols("Ctrl+Alt+S") == "⌃⌥S", $"{Mac.Symbols("Ctrl+Alt+S")} {Mac.Symbols("Cmd+Shift+2")}");
        });

        Try("Mac text reader's answer is read right", () =>
        {
            // Vision's boxes are fractions of the picture, measured up from the bottom-left
            const string json = """[{"t":"Hello there","b":[0.1,0.5,0.5,0.2],"w":[{"t":"Hello","s":0,"b":[0.1,0.5,0.2,0.2]},{"t":"there","s":6,"b":null}]}]""";
            var lines = MacText.Parse(json, 200, 100);
            var hello = lines[0][0].Bounds;
            var there = lines[0][1].Bounds;
            Check("Mac text reader's answer is read right",
                  lines.Count == 1 && lines[0].Count == 2 && hello == new Rect(20, 30, 40, 20) && Math.Abs(there.X - (20 + 6 * 100 / 11.0)) < 0.01 && there.Y == 30,
                  $"{hello} / {there}");
        });

        if (Mac.IsMac)
        {
            Console.WriteLine($"Mac: {Environment.OSVersion}, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
            Try("Mac: displays and the pointer", () =>
            {
                var displays = Mac.Displays();
                var under = Mac.DisplayUnderPointer();
                var at = Mac.PointerPoints();
                Check("Mac: displays and the pointer", displays.Count > 0 && under.Width > 0 && under.Contains(at.X, at.Y),
                      $"{displays.Count} display(s), pointer {at.X:0},{at.Y:0} on {under.X:0},{under.Y:0} {under.Width:0}x{under.Height:0}");
            });
            Try("Mac: permissions", () =>
            {
                bool screen = Mac.CanReadScreen(ask: false), drive = Mac.CanDrive();
                Console.WriteLine($"info  Screen Recording {(screen ? "allowed" : "NOT allowed yet")}, Accessibility {(drive ? "allowed" : "not allowed (only scrolling capture needs it)")}");
                Check("Mac: permissions", true);
            });
            await TryAsync("Mac: screenshot", async () =>
            {
                if (!Mac.CanReadScreen(ask: false)) { Console.WriteLine("skip  Mac: screenshot (allow Screen Recording first)"); return; }
                var shot = await ScreenGrabber.GrabAsync();
                var p = PixelAt(shot, shot.PixelSize.Width / 2, shot.PixelSize.Height / 2);
                Check("Mac: screenshot", shot.PixelSize.Width > 100 && p.A == 255 && Mac.Shot.Scale >= 1,
                      $"{shot.PixelSize}, {Mac.Shot.Scale:0.##}x");
            });
            Try("Mac: windows on screen", () =>
            {
                var windows = Mac.Windows();
                Check("Mac: windows on screen", true, $"{windows.Count} found{(windows.Count > 0 ? ", front: " + windows[0].Title : "")}");
            });
            await TryAsync("Mac: clipboard", async () =>
            {
                string text = "Overskin " + Guid.NewGuid().ToString("N")[..8];
                bool put = Mac.CopyText(text);
                var back = (await ScreenGrabber.OutputOf("/usr/bin/pbpaste", ""))?.Trim();
                bool png = Mac.CopyPng(Imaging.Png(Quarters(40, 20)));
                var types = await ScreenGrabber.OutputOf("/usr/bin/osascript", "-e \"clipboard info\"") ?? "";
                var pasted = Mac.PastePicture();
                Bitmap? round = null;
                if (pasted != null) { using var ms = new MemoryStream(pasted); round = new Bitmap(ms); }
                Check("Mac: clipboard", put && back == text && png && types.Contains("PNGf") && round?.PixelSize == new PixelSize(40, 20),
                      $"text back \"{back}\", picture types: {types.Trim()}, pasted back {round?.PixelSize.ToString() ?? "nothing"}");
            });
            await TryAsync("Mac: Vision reads text", async () =>
            {
                var page = Imaging.Render(new PixelSize(600, 120), ctx =>
                {
                    ctx.FillRectangle(Brushes.White, new Rect(0, 0, 600, 120));
                    ctx.DrawText(new FormattedText("Overskin reads 1234 words", System.Globalization.CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, new Typeface("Helvetica"), 40, Brushes.Black), new Point(20, 30));
                });
                var lines = await MacText.ReadLinesAsync(page);
                var text = lines == null ? "(Vision didn't run; see the log)" : string.Join(" | ", lines.Select(l => string.Join(" ", l.Select(w => w.Text))));
                var first = lines?.FirstOrDefault()?.FirstOrDefault()?.Bounds;
                Check("Mac: Vision reads text", text.Contains("Overskin reads 1234 words") && first is { } f && f.X > 5 && f.X < 60 && f.Y > 10 && f.Y < 60,
                      $"\"{text}\" first word at {first}");
            });
            await TryAsync("Mac: the face finder runs", async () =>
            {
                var faces = await People.FacesAsync(Quarters(300, 200));
                Check("Mac: the face finder runs", faces.Count == 0, $"{faces.Count} faces in a picture with none (the log says if Vision couldn't run)");
            });
            Try("Mac: a hotkey can be registered", () =>
            {
                bool ok = Mac.AddHotKey("Ctrl+Alt+Shift+F12", () => { });
                Mac.ClearHotKeys();
                Check("Mac: a hotkey can be registered", ok);
            });
            Try("Mac: the Trash", () =>
            {
                var file = Path.Combine(Path.GetTempPath(), $"overskin-trash-test-{Guid.NewGuid():N}.txt");
                File.WriteAllText(file, "test");
                bool ok = Mac.Trash(file);
                Check("Mac: the Trash", ok && !File.Exists(file), ok ? "moved to the Trash" : "macOS wouldn't");
            });
            Check("Mac: tools", true, $"ffmpeg {(ScreenGrabber.HasCommand("ffmpeg") ? "found" : "not installed (GIFs need it: brew install ffmpeg)")}, " +
                                      $"avconvert {(File.Exists("/usr/bin/avconvert") ? "found" : "missing")}, screencapture {(File.Exists("/usr/sbin/screencapture") ? "found" : "missing")}");
        }

        await TextEditingAsync();

        Try("windows build", () =>
        {
            var main = new MainWindow();
            var editor = new EditorWindow(Quarters(320, 200), null);
            var file = Path.Combine(Path.GetTempPath(), $"overskin-view-{Guid.NewGuid():N}.png");
            Imaging.SavePng(Quarters(60, 40), file);
            var viewer = new ViewerWindow(new[] { file }, file);
            var compare = new CompareWindow(file, file);
            var effects = new EffectsWindow(Quarters(100, 60));
            var text = new TextResultWindow("hello");
            Check("windows build", main.Content != null && editor.Content != null && viewer.Content != null && compare.Content != null && effects.Content != null && text.Content != null);
            File.Delete(file);
        });

        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Console.WriteLine(_failed == 0 ? "All passed." : $"{_failed} failed.");
        return _failed == 0 ? 0 : 1;
    }
}
