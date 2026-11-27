using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Overskin.Linux.Core;
using Overskin.Linux.Interface;
using Overskin.Linux.Platform;

namespace Overskin.Linux;

/// <summary>
/// overskin --snapshots DIR --demo PICTURE: draws Overskin's own windows into PNGs for the README.
/// Only the windows' contents are drawn; nothing reads the screen.
/// </summary>
public static class Snapshots
{
    public static async Task<int> RunAsync(string outDir)
    {
        try
        {
            Directory.CreateDirectory(outDir);
            Settings.FilePath = Path.Combine(Path.GetTempPath(), "overskin-snapshots.json");
            var demo = ScreenGrabber.DemoFile != null ? new Bitmap(ScreenGrabber.DemoFile) : SelfTest.Quarters(1200, 900);
            // a library of made-up pictures, in a temporary folder
            var library = Path.Combine(Path.GetTempPath(), "overskin-snapshot-library");
            if (Directory.Exists(library)) Directory.Delete(library, true);
            Directory.CreateDirectory(library);
            Imaging.SavePng(demo, Path.Combine(library, "Overskin_2026-11-17_10-24-03.png"));
            Imaging.SavePng(Effects.Rotate(demo, true), Path.Combine(library, "Overskin_2026-11-16_18-02-11.png"));
            Imaging.SavePng(Beautify.Render(Effects.Resize(demo, 600, 450), new BeautifyOptions { Background = "Sunset" }), Path.Combine(library, "Launch post.png"));
            Imaging.SavePng(SelfTest.Quarters(800, 500), Path.Combine(library, "Colours test.png"));
            Imaging.SavePng(Effects.Flip(demo, true), Path.Combine(library, "Overskin_2026-11-15_09-41-55.png"));
            App.Settings.SaveFolder = library;

            var main = new MainWindow();
            foreach (var page in new[] { "Capture", "Record", "Library", "Pins", "Settings" })
            {
                main.Go(page);
                await Shoot(main, Path.Combine(outDir, $"linux-{page.ToLowerInvariant()}.png"), close: false);
            }
            Theming.Apply("Catppuccin Latte");
            main.Go("Capture");
            await Shoot(main, Path.Combine(outDir, "linux-light.png"), close: false);
            Theming.Apply("Graphite");
            main.Hide();
            App.Settings.SaveFolder = "/home/you/Pictures/Overskin";

            var editor = new EditorWindow(demo, "/home/you/Pictures/Overskin/Overskin_2026-11-17_10-24-03.png") { Width = 1100, Height = 820 };
            var c = editor.Canvas;
            c.Add(new StrokeMark { Colour = Color.Parse("#F59E0B"), Width = 9, Highlighter = true, Points = { } });
            ((StrokeMark)c.Marks[^1]).Points.AddRange(new[] { new Point(565, 122), new Point(1010, 122) });
            c.Add(new ShapeMark { Kind = Tool.Rectangle, From = new Point(605, 205), To = new Point(1075, 735), Colour = Color.Parse("#EF4444"), Width = 5 });
            c.Add(new CounterMark { At = new Point(605, 205), Number = 1, Colour = Color.Parse("#EF4444"), Width = 5 });
            c.Add(new ShapeMark { Kind = Tool.Arrow, From = new Point(560, 520), To = new Point(430, 598), Colour = Color.Parse("#EF4444"), Width = 6 });
            c.Add(new TextMark { At = new Point(470, 452), Text = "now on Linux", Size = 30, Colour = Color.Parse("#EF4444") });
            await Shoot(editor, Path.Combine(outDir, "linux-editor.png"));

            await Shoot(new RecordBar(false, () => { }, () => { }, 0, () => { }, () => { }), Path.Combine(outDir, "linux-recordbar.png"));
            var shown = Path.Combine(library, "Overskin_2026-11-17_10-24-03.png");
            await Shoot(new ViewerWindow(new[] { shown }, shown) { Width = 900, Height = 640 }, Path.Combine(outDir, "linux-viewer.png"));
            await Shoot(new TextResultWindow("Overskin reads 1234 words from any picture."), Path.Combine(outDir, "linux-text.png"));

            var overlay = new Window
            {
                Width = 1200, Height = 900, SystemDecorations = SystemDecorations.None,
                Content = new SelectionSurface(demo),
            };
            overlay.Opened += (_, _) => ((SelectionSurface)overlay.Content!).Pretend(new Point(600, 200), new Point(1080, 740));
            await Shoot(overlay, Path.Combine(outDir, "linux-overlay.png"));

            Console.WriteLine("snapshots saved to " + Path.GetFullPath(outDir));
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
            return 1;
        }
    }

    private static async Task Shoot(Window window, string path, bool close = true)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = new PixelPoint(-30000, -30000); // drawn off screen, never visible
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Show();
        await Task.Delay(600);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        double scale = 2;
        var size = new PixelSize((int)(window.Bounds.Width * scale), (int)(window.Bounds.Height * scale));
        using (var rtb = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale)))
        {
            rtb.Render(window);
            rtb.Save(path);
        }
        if (!close) return;
        if (window is MainWindow) window.Hide(); else window.Close();
    }
}
