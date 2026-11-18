using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Overskin.Linux.Core;
using Overskin.Linux.Interface;
using Overskin.Linux.Platform;

namespace Overskin.Linux;

/// <summary>overskin --selftest: checks the parts that don't need a person at the mouse. Exit code 0 means all passed.</summary>
public static class SelfTest
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

        Try("trace engine (shared with Windows)", () =>
        {
            var square = Imaging.Render(new PixelSize(60, 60), ctx =>
            {
                ctx.FillRectangle(Brushes.White, new Rect(0, 0, 60, 60));
                ctx.FillRectangle(Brushes.Black, new Rect(10, 10, 40, 40));
            });
            var source = TraceEngine.PrepareBgra(Imaging.Pixels(square, out _), 60, 60);
            var outline = TraceEngine.Trace(source, new TraceOptions { Tolerance = 1, MinPathLength = 4 }, default);
            var hatch = TraceEngine.Trace(source, new TraceOptions { Mode = TraceMode.Hatch, Spacing = 5, OutlineToo = false }, default);
            // hatch rows are joined into one zig-zag, as on Windows
            Check("trace engine (shared with Windows)", outline.Paths.Count >= 1 && hatch.PointCount >= 12, $"outline {outline.Paths.Count}, hatch {hatch.Paths.Count} strokes / {hatch.PointCount} points");
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

        Try("windows build", () =>
        {
            var main = new MainWindow();
            var editor = new EditorWindow(Quarters(320, 200), null);
            Check("windows build", main.Content != null && editor.Content != null);
        });

        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Console.WriteLine(_failed == 0 ? "All passed." : $"{_failed} failed.");
        return _failed == 0 ? 0 : 1;
    }
}
