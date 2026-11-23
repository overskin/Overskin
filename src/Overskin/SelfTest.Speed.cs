#if DEBUG
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Overskin;

/// <summary>How long each part of "after a capture" takes, on a screenshot-like picture.</summary>
public partial class App
{
    internal static BitmapSource FakeScreenshot(int w, int h)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x31, 0x33, 0x38)), null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x2B, 0x2D, 0x31)), null, new Rect(0, 0, 240, h));
            var face = new Typeface("Segoe UI");
            var rnd = new Random(4);
            for (int y = 20; y < h - 30; y += 28)
            {
                string line = string.Join(" ", Enumerable.Range(0, 6 + rnd.Next(10)).Select(_ => new string('a', 2 + rnd.Next(8))));
                dc.DrawText(new FormattedText(line, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 15, Brushes.Gainsboro, 1), new Point(260, y));
            }
            for (int i = 0; i < 6; i++)
                dc.DrawRoundedRectangle(new LinearGradientBrush(Colors.SteelBlue, Colors.Orange, 30), null, new Rect(900 + i * 200, 300, 180, 120), 8, 8);
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var image = new FormatConvertedBitmap(rtb, PixelFormats.Bgra32, null, 0);
        var copy = new WriteableBitmap(image);
        copy.Freeze();
        return copy;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetTopWindow")] private static extern IntPtr GetTopWindowForTest(IntPtr parent);

    /// <summary>The top-level windows from the top down: whose they are and whether naming skips them (no pictures).</summary>
    private static void WindowList(List<string> log)
    {
        for (var w = GetTopWindowForTest(IntPtr.Zero); w != IntPtr.Zero; w = NativeMethods.GetWindow(w, 2))
        {
            if (!NativeMethods.IsWindowVisible(w)) continue;
            NativeMethods.GetWindowRect(w, out var r);
            NativeMethods.GetWindowThreadProcessId(w, out uint pid);
            string name = "";
            try { name = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { }
            long ex = NativeMethods.GetExStyle(w);
            log.Add($"{name,-24} {NativeMethods.ClassName(w),-40} ex={ex:X8} overlay={CaptureOverlay.IsOverlay(w)} layer={NameInfo.IsOverlayLayer(w, r, pid)} rect={r.Left},{r.Top} {r.Right - r.Left}x{r.Bottom - r.Top}");
        }
        foreach (var (x, y) in new[] { (1280, 720), (-960, 540), (-1500, 2600) })
            log.Add($"named at {x},{y}: {NameInfo.At(x, y)}");
    }

    private static void SpeedTests(List<string> log)
    {
        var shot = FakeScreenshot(2560, 1440);
        string dir = Path.Combine(Path.GetTempPath(), "Overskin-speed");
        Directory.CreateDirectory(dir);
        var sw = Stopwatch.StartNew();
        ImageFiles.TrySave(shot, Path.Combine(dir, "a.png"), out _);
        long png = sw.ElapsedMilliseconds;
        sw.Restart();
        ImageFiles.TrySave(shot, Path.Combine(dir, "a.jpg"), out _, 90);
        long jpg = sw.ElapsedMilliseconds;
        sw.Restart();
        var bmp = new BmpBitmapEncoder();
        bmp.Frames.Add(BitmapFrame.Create(shot));
        using (var ms = new MemoryStream()) bmp.Save(ms);
        long dib = sw.ElapsedMilliseconds;
        sw.Restart();
        var data = new DataObject();
        data.SetImage(shot);
        long setImage = sw.ElapsedMilliseconds;
        sw.Restart();
        bool notice = false;
        CaptureNotice.ShowFor(shot, Path.Combine(dir, "a.png"), "2560 × 1440", () => { }, () => { }, () => { }, null);
        DoEvents();
        notice = true;
        long card = sw.ElapsedMilliseconds;
        sw.Restart();
        CaptureNotice.ShowFor(shot, Path.Combine(dir, "a.png"), "2560 × 1440", () => { }, () => { }, () => { }, null);
        DoEvents();
        long card2 = sw.ElapsedMilliseconds;
        log.Add($"preview card again: {card2} ms");
        // the clipboard, as a capture copies it (what was there is put back afterwards when it's text)
        string? before = null;
        try { if (Clipboard.ContainsText()) before = Clipboard.GetText(); } catch { }
        sw.Restart();
        ClipboardFiles.Copy(new[] { Path.Combine(dir, "a.png") }, shot);
        long flush = sw.ElapsedMilliseconds;
        sw.Restart();
        try { Clipboard.SetImage(shot); } catch { }
        long plain = sw.ElapsedMilliseconds;
        sw.Restart();
        bool fastOk = FastClipboard.Copy(shot, Path.Combine(dir, "a.png"));
        long fast = sw.ElapsedMilliseconds;
        // what's on the clipboard now: the file, the PNG and a picture other apps can read back
        bool hasFile = Clipboard.ContainsFileDropList() && Clipboard.GetFileDropList()[0]!.EndsWith("a.png");
        var back = Clipboard.GetImage();
        bool hasPng = Clipboard.GetDataObject()?.GetDataPresent("PNG") == true;
        log.Add($"fast clipboard: ok={fastOk} in {fast} ms, file={hasFile} png={hasPng} picture back={back?.PixelWidth}x{back?.PixelHeight} (expect True True 2560x1440)");
        try { if (before != null) Clipboard.SetText(before); else Clipboard.Clear(); } catch { }
        log.Add($"clipboard: file and picture {flush} ms, picture only {plain} ms");
        log.Add($"after a capture (2560x1440): save PNG {png} ms, save JPG {jpg} ms, clipboard bitmap {dib} ms, data object {setImage} ms, preview card shown={notice} in {card} ms");
    }
}
#endif
