#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Overskin;

/// <summary>
/// Self-tests for fonts, the newer GIF editor options, screenshot size and the quicker clipboard.
/// Run with --selftest log --more --fonts.
/// </summary>
public partial class App
{
    private static void FontAndGifTests(List<string> log)
    {
        // ---- fonts that come with Overskin load, and look different from each other ----
        try
        {
            var names = FontLibrary.All.Select(c => c.Name).ToList();
            var missing = new List<string>();
            foreach (var n in new[] { "Anton", "Bebas Neue", "Oswald", "Bangers", "Permanent Marker", "Comic Neue" })
                if (!FontLibrary.Find(n).Face.TryGetGlyphTypeface(out _)) missing.Add(n);
            double Width(string font) => new FormattedText("caption text", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                FontLibrary.Find(font).Face, 40, Brushes.Black, 1.0).Width;
            log.Add($"fonts: {names.Count} offered [{string.Join(", ", names)}] bundled missing=[{string.Join(", ", missing)}] (expect none) " +
                    $"caption default={FontLibrary.CaptionDefault} (expect Anton, or Futura when installed) " +
                    $"widths anton={Width("Anton"):0} bangers={Width("Bangers"):0} marker={Width("Permanent Marker"):0} (expect all different)");
        }
        catch (Exception ex) { log.Add("fonts EXCEPTION " + ex.Message); }

        // ---- the caption uses the chosen font, colour and side ----
        try
        {
            var frame = BitmapSource.Create(200, 100, 96, 96, PixelFormats.Bgra32, null, Enumerable.Repeat((byte)0x80, 200 * 100 * 4).ToArray(), 800);
            frame.Freeze();
            var plain = new CaptionSettings(CaptionStyle.Caption, "hello there", "", 1);
            var other = plain with { Font = "Bangers" };
            var black = plain with { Colour = Colors.Black };
            var below = plain with { BarBelow = true };
            byte[] a = CaptionRenderer.Render(frame, 200, plain, out _, out int h);
            byte[] b = CaptionRenderer.Render(frame, 200, other, out _, out _);
            byte[] k = CaptionRenderer.Render(frame, 200, black, out _, out _);
            byte[] d = CaptionRenderer.Render(frame, 200, below, out _, out int hd);
            static (byte, byte) Px(byte[] p, int x, int y) => (p[(y * 200 + x) * 4], p[(y * 200 + x) * 4 + 1]);
            log.Add($"caption options: font changes the picture={!a.SequenceEqual(b)} (expect True) " +
                    $"black bar top-left={Px(k, 2, 2).Item1} (expect 0) white bar top-left={Px(a, 2, 2).Item1} (expect 255) " +
                    $"bar below: top-left={Px(d, 2, 2).Item1} (expect 128, the picture) bottom-left={Px(d, 2, hd - 2).Item1} (expect 255) same height={h == hd}");
        }
        catch (Exception ex) { log.Add("caption options EXCEPTION " + ex.Message); }

        // ---- GIF looks and mirroring ----
        try
        {
            var px = new byte[4 * 2 * 1];
            px[0] = 0; px[1] = 0; px[2] = 255; px[3] = 255;      // left: red
            px[4] = 255; px[5] = 0; px[6] = 0; px[7] = 255;      // right: blue
            var src = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, px, 8);
            src.Freeze();
            byte[] Read(BitmapSource s) { var o = new byte[8]; new FormatConvertedBitmap(s, PixelFormats.Bgra32, null, 0).CopyPixels(o, 8, 0); return o; }
            var grey = Read(GifLook.Apply(src, "Grey", false));
            var mirror = Read(GifLook.Apply(src, "Normal", true));
            var sepia = Read(GifLook.Apply(src, "Sepia", false));
            log.Add($"gif looks: grey left=({grey[2]},{grey[1]},{grey[0]}) (expect three equal) mirror left is blue={mirror[0] == 255 && mirror[2] == 0} (expect True) " +
                    $"sepia warm={sepia[2] > sepia[0]} (expect True) normal untouched={ReferenceEquals(GifLook.Apply(src, "Normal", false), src)} (expect True)");
        }
        catch (Exception ex) { log.Add("gif looks EXCEPTION " + ex.Message); }

        // ---- GIF repeat count in the file, and fewer frames ----
        try
        {
            byte[] Gif(int repeats)
            {
                using var m = new MemoryStream();
                using (var w = new GifWriter(m, 4, 4, repeats)) { w.AddFrame(new byte[64], 100); w.AddFrame(Enumerable.Repeat((byte)255, 64).ToArray(), 100); }
                return m.ToArray();
            }
            static int Loop(byte[] g)
            {
                var tag = System.Text.Encoding.ASCII.GetBytes("NETSCAPE2.0");
                for (int i = 0; i + tag.Length + 5 < g.Length; i++)
                    if (g.AsSpan(i, tag.Length).SequenceEqual(tag)) return g[i + tag.Length + 2] | (g[i + tag.Length + 3] << 8);
                return -1;
            }
            string gifFile = Path.Combine(Path.GetTempPath(), "Overskin repeat test.gif");
            File.WriteAllBytes(gifFile, Gif(3));
            var back = GifReader.Read(gifFile);
            log.Add($"gif repeat: forever={Loop(Gif(0))} (expect 0) once={Loop(Gif(1))} (expect -1, no loop block) three times={Loop(Gif(3))} (expect 2) " +
                    $"still reads={back?.Count} frames (expect 2)");

            var clip = new GifClip { Width = 40, Height = 30 };
            for (int i = 0; i < 20; i++) { clip.Frames.Add(new byte[40 * 30 * 4]); clip.DelaysMs.Add(50); }
            var ed = new GifEditorWindow(clip, "test", new GifEditorHost { Folder = Path.GetTempPath(), Saved = _ => { } })
            { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
            ed.Show();
            Pump(100);
            int before = ed.SequenceForTest.Count, msBefore = ed.SequenceForTest.Sum(s => s.DelayMs);
            ed.FewerSwitch.IsChecked = true;
            ed.FewerSwitch.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            int after = ed.SequenceForTest.Count, msAfter = ed.SequenceForTest.Sum(s => s.DelayMs);
            log.Add($"gif fewer frames: {before} -> {after} (expect 20 -> 10) length {msBefore} -> {msAfter} ms (expect the same)");

            // cropping starts with the whole frame boxed; Esc puts back what was there
            ed.CropButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Pump(50);
            string startBox = ed.CropBoxForTest?.ToString() ?? "none";
            ed.SetCropForTest(new Rect(5, 5, 20, 15));
            ed.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(ed), 0, System.Windows.Input.Key.Escape)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
            Pump(50);
            string afterEsc = ed.CropBoxForTest?.ToString() ?? "none";
            ed.CropButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            ed.SetCropForTest(new Rect(5, 5, 20, 15));
            ed.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(ed), 0, System.Windows.Input.Key.Enter)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
            Pump(50);
            var kept = ed.OutputSizeForTest;
            ed.Close();
            log.Add($"gif crop: starts framed={startBox} (expect 0,0,40,30) esc put back={afterEsc} (expect none) enter kept size={kept.Width}x{kept.Height} " +
                    $"(expect the 20x15 crop scaled to the output width)");
            log.Add($"gif fps: 10->{GifRecorder.ValidFps(10)} 30->{GifRecorder.ValidFps(30)} 60->{GifRecorder.ValidFps(60)} (expect 15 30 50)");
        }
        catch (Exception ex) { log.Add("gif repeat/fewer EXCEPTION " + ex.Message); }

        // ---- the clipboard: the picture first, the file added when it's saved ----
        try
        {
            var pic = BitmapSource.Create(30, 20, 96, 96, PixelFormats.Bgra32, null, Enumerable.Repeat((byte)200, 30 * 20 * 4).ToArray(), 120);
            pic.Freeze();
            string file = Path.Combine(Path.GetTempPath(), "Overskin clipboard test.png");
            ImageFiles.TrySave(pic, file, out _);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool copied = FastClipboard.Copy(pic);
            long ms = sw.ElapsedMilliseconds;
            bool imageFirst = Clipboard.ContainsImage(), fileFirst = Clipboard.ContainsFileDropList();
            bool added = FastClipboard.AddFile(file);
            bool imageAfter = Clipboard.ContainsImage();
            var files = Clipboard.ContainsFileDropList() ? Clipboard.GetFileDropList().Cast<string>().ToList() : new List<string>();
            // something else copied in between: the file isn't added on top of it
            FastClipboard.Copy(pic);
            Clipboard.SetText("someone else's text");
            bool addedOver = FastClipboard.AddFile(file);
            bool textKept = Clipboard.ContainsText() && !Clipboard.ContainsFileDropList();
            log.Add($"quick copy: copied={copied} in {ms} ms, picture at once={imageFirst} file at once={fileFirst} (expect True, False) " +
                    $"then file added={added} picture still there={imageAfter} file={(files.Count == 1 && files[0] == file)} (expect True True True) " +
                    $"not over newer copy: added={addedOver} other text kept={textKept} (expect False True)");
            Clipboard.Clear();
        }
        catch (Exception ex) { log.Add("quick copy EXCEPTION " + ex.Message); }
    }
}
#endif
