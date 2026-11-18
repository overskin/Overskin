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
            var canvas = new EditorCanvas(pic) { PixelBlock = 100 };
            canvas.Add(new PatchMark { Area = new Rect(0, 0, 100, 100) });
            var result = canvas.Export();
            var a = PixelAt(result, 5, 5);
            var b = PixelAt(result, 95, 95);
            Check("pixelate mark hides the original", a == b && a != (0, 0, 255, 255), $"{a} vs {b}");
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
