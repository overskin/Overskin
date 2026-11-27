using System.Globalization;
using System.Windows;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Overskin.Linux.Core;
using Overskin.Linux.Interface;
using Overskin.Linux.Platform;
using Point = Avalonia.Point;
using Rect = Avalonia.Rect;
using Size = Avalonia.Size;

namespace Overskin.Linux;

/// <summary>The editor's live text, typing into screenshots, and picking up marks (the same checks as on Windows).</summary>
public static partial class SelfTest
{
    private static readonly FontFamily InterFamily = new("avares://overskin/Assets/Fonts#Inter");

    private static FormattedText Ft(string s, Typeface face, double size, IBrush? brush = null) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, brush ?? Brushes.Black);

    /// <summary>A line of text on a plain background, and each word's box as a text reader would give it.</summary>
    private static Bitmap TextPage(Typeface face, double size, string text, Color ink, Color paper, out List<OcrWord> words, int w = 360, int h = 60, double ox = 14, double oy = 12)
    {
        var origin = new Point(ox, oy);
        var page = Imaging.Render(new PixelSize(w, h), ctx =>
        {
            ctx.FillRectangle(new SolidColorBrush(paper), new Rect(0, 0, w, h));
            ctx.DrawText(Ft(text, face, size, new SolidColorBrush(ink)), origin);
        });
        words = new List<OcrWord>();
        int at = 0;
        foreach (var word in text.Split(' '))
        {
            double x = origin.X + Ft(text[..at], face, size).WidthIncludingTrailingWhitespace;
            var g = Ft(word, face, size).BuildGeometry(new Point(x, origin.Y))!.Bounds;
            words.Add(new OcrWord(word, g));
            at += word.Length + 1;
        }
        return Imaging.FromPixels(Imaging.Pixels(page, out _), page.PixelSize); // a plain copy, as a screenshot would be
    }

    private static bool HasFont(string name)
    {
        try { return FontManager.Current.SystemFonts.Any(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)); }
        catch { return false; }
    }

    private static async Task TextEditingAsync()
    {
        await TryAsync("live text: select, copy and delete letters", async () =>
        {
            var face = new Typeface(InterFamily);
            const double Size = 40, OriginX = 20;
            var page = TextPage(face, Size, "Hello there", Colors.Black, Colors.White, out var words, 420, 90, OriginX, 18);
            var ed = new EditorWindow(page, null);
            ed.SelectWordsForTest(words, 0, 0);
            double midY = words[0].Bounds.Top + words[0].Bounds.Height / 2;
            var e0 = ed.EdgesForTest(0);
            // how far the gaps found from the ink are from where the font really put them
            double worst = 0;
            for (int k = 1; k < "Hello".Length; k++)
                worst = Math.Max(worst, Math.Abs(e0[k] - (OriginX + Ft("Hello"[..k], face, Size).WidthIncludingTrailingWhitespace)));
            string inside = ed.DragOverTextForTest(new Point(e0[1] + 1, midY), new Point(e0[4] - 1, midY));
            var e1 = ed.EdgesForTest(1);
            string across = ed.DragOverTextForTest(new Point(e0[2] + 1, midY), new Point(e1[3] + 1, midY));
            string click = ed.DragOverTextForTest(new Point((e1[2] + e1[3]) / 2 + 1, midY), new Point((e1[2] + e1[3]) / 2 + 1, midY));
            string caret = ed.CaretForTest;
            string dbl = ed.DoubleClickForTest(new Point((e1[0] + e1[5]) / 2, midY));

            // erase "ell": the H and the o stay, the middle goes white
            ed.DragOverTextForTest(new Point(e0[1] + 1, midY), new Point(e0[4] - 1, midY));
            await ed.EraseSelectedForTest();
            var after = Imaging.Pixels(ed.BaseForTest, out _);
            int Darkest(double x0, double x1)
            {
                int best = 255;
                for (int y = (int)words[0].Bounds.Top; y < (int)words[0].Bounds.Bottom; y++)
                    for (int x = (int)Math.Ceiling(x0); x < (int)x1; x++) best = Math.Min(best, (int)after[(y * 420 + x) * 4 + 1]);
                return best;
            }
            int hInk = Darkest(e0[0] + 2, e0[1] - 3), middle = Darkest(e0[1] + 3, e0[4] - 3), oInk = Darkest(e0[4] + 3, e0[5] - 2);

            // the cursor: between e and l, Backspace takes the e and the rest closes up; then Backspace before "there" takes the space
            var ed2 = new EditorWindow(page, null);
            ed2.SelectWordsForTest(words, 0, 0);
            var f0 = ed2.EdgesForTest(0);
            await ed2.ClickAndDeleteForTest(new Point(f0[2] + 0.4, midY), back: true);
            string afterBack = ed2.WordsForTest + " caret " + ed2.CaretForTest;
            var g1 = ed2.EdgesForTest(1);
            await ed2.ClickAndDeleteForTest(new Point(g1[0] + 0.3, midY), back: true);
            string afterSpace = ed2.WordsForTest;
            // the picture really changed: no ink left past where the line now ends
            var edited = Imaging.Pixels(ed2.BaseForTest, out _);
            double newEnd = ed2.EdgesForTest(0)[^1];
            bool cleanEnd = true;
            for (int y = 0; y < 90 && cleanEnd; y++)
                for (int x = (int)newEnd + 3; x < 420; x++)
                    if (edited[(y * 420 + x) * 4 + 1] < 200) { cleanEnd = false; break; }

            Check("live text: select, copy and delete letters",
                  worst < 3 && inside == "ell" && across == "llo the" && click == "" && caret == "there@3" && dbl == "there" &&
                  hInk < 100 && middle > 200 && oInk < 100 && afterBack == "Hllo|there caret Hllo@1" && afterSpace == "Hllothere" && cleanEnd,
                  $"letter gaps off by {worst:0.0} px; inside \"{inside}\", across \"{across}\", click \"{click}\" {caret}, double-click \"{dbl}\"; " +
                  $"after erasing ell: H {hInk}, middle {middle}, o {oInk}; backspace: {afterBack}; then {afterSpace}; end clean {cleanEnd}");
        });

        await TryAsync("typing into a screenshot: the font is recognised and new letters match", async () =>
        {
            var cases = new (string Font, FontWeight Weight, double Size, Color Ink, Color Paper)[]
            {
                ("Arial", FontWeight.Normal, 15, Colors.Black, Colors.White),
                ("Arial", FontWeight.Bold, 14, Color.FromRgb(30, 30, 30), Color.FromRgb(240, 240, 240)),
                ("Georgia", FontWeight.Normal, 18, Color.FromRgb(40, 60, 140), Colors.White),
                ("Courier New", FontWeight.Normal, 13, Color.FromRgb(220, 220, 220), Color.FromRgb(30, 30, 30)),
                ("Times New Roman", FontWeight.Normal, 20, Colors.Black, Color.FromRgb(255, 250, 235)),
            };
            var found = new List<string>();
            double slowest = 0;
            int right = 0, tried = 0;
            foreach (var c in cases)
            {
                if (!HasFont(c.Font)) continue;
                tried++;
                var page = TextPage(new Typeface(new FontFamily(c.Font), FontStyle.Normal, c.Weight), c.Size, "The quick brown fox jumps", c.Ink, c.Paper, out var words);
                var ed = new EditorWindow(page, null);
                ed.SelectWordsForTest(words, 0, 0);
                var (style, ms) = ed.MatchLineForTest(0);
                slowest = Math.Max(slowest, ms);
                string want = c.Font + (c.Weight == FontWeight.Bold ? " Bold" : "");
                bool ok = style != null && style.Name == want && Math.Abs(style.Size - c.Size) <= 1;
                if (ok) right++;
                found.Add($"{want} {c.Size}->{style?.Name ?? "none"} {style?.Size:0.#}" + (Environment.GetEnvironmentVariable("OVERSKIN_RANK") != null ? " [" + EditorWindow.LastRankForTest + "]" : ""));
            }

            // "Hello world", cursor after Hello, type " big"; compared with "Hello big world" drawn for real
            var face = new Typeface(InterFamily);
            var before = TextPage(face, 16, "Hello world", Colors.Black, Colors.White, out var hw);
            var wanted = TextPage(face, 16, "Hello big world", Colors.Black, Colors.White, out _);
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
            var s2 = Starts(Imaging.Pixels(wanted, out _));
            string Off(List<int> s1) => s1.Count == s2.Count ? s1.Zip(s2, (u, v) => Math.Abs(u - v)).Max().ToString() : $"runs {s1.Count} vs {s2.Count}";

            var ed2 = new EditorWindow(before, null);
            ed2.SelectWordsForTest(hw, 0, 0);
            var e0 = ed2.EdgesForTest(0);
            await ed2.ClickAndTypeForTest(new Point(e0[^1] - 0.2, hw[0].Bounds.Top + hw[0].Bounds.Height / 2), " big");
            string model = ed2.WordsForTest;
            string letterOff = Off(Starts(Imaging.Pixels(ed2.BaseForTest, out _)));
            var typedStyle = ed2.TypingStyleForTest(0);

            // the same with another line beside this one on the same row (a time after a name)
            var beside = hw.Append(new OcrWord("12:30", new Rect(300, hw[0].Bounds.Top, 34, hw[0].Bounds.Height))).ToList();
            var ed3 = new EditorWindow(before, null);
            ed3.SelectWordsForTest(beside, 0, 0, new[] { 0, 0, 1 });
            var e3 = ed3.EdgesForTest(0);
            await ed3.ClickAndTypeForTest(new Point(e3[^1] - 0.2, hw[0].Bounds.Top + hw[0].Bounds.Height / 2), " big");
            string besideOff = Off(Starts(Imaging.Pixels(ed3.BaseForTest, out _)));

            bool offOk = int.TryParse(letterOff, out int lo) && lo <= 2 && int.TryParse(besideOff, out int bo) && bo <= 2;
            Check("typing into a screenshot: the font is recognised and new letters match",
                  right == tried && slowest < 3000 && model == "Hello|big|world" && offOk && typedStyle?.Name == "Inter",
                  $"fonts {right}/{tried} [{string.Join("; ", found)}], slowest match {slowest:0} ms; typed words {model}, matched {typedStyle?.Name} {typedStyle?.Size:0.#}; " +
                  $"letters off by {letterOff} px, beside another line {besideOff} px");
        });

        await TryAsync("typing: the fine-tuning bar and restyling", async () =>
        {
            var face = new Typeface(InterFamily, FontStyle.Normal, FontWeight.Bold);
            var page = TextPage(face, 18, "Hello there", Colors.Black, Colors.White, out var words);
            var ed = new EditorWindow(page, null);
            ed.SelectWordsForTest(words, 0, 0, new[] { 0, 0 });
            var matched = ed.MatchLineForTest(0).Style;
            // a cursor in "there", so the bar applies to that line
            var t = ed.EdgesForTest(1);
            ed.DragOverTextForTest(new Point(t[2], words[1].Bounds.Center.Y), new Point(t[2], words[1].Bounds.Center.Y));
            bool shown = ed.TypeBarShownForTest;
            ed.TuneForTest("bigger");
            await ed.RestyleDoneForTest();
            var bigger = ed.TypingStyleForTest(1);
            ed.TuneForTest("regular");
            await ed.RestyleDoneForTest();
            var regular = ed.TypingStyleForTest(1);
            ed.TuneForTest("roboto");
            await ed.RestyleDoneForTest();
            var roboto = ed.TypingStyleForTest(1);
            string text = ed.TextForTest;
            ed.TuneForTest("reset");
            await ed.RestyleDoneForTest();
            var reset = ed.TypingStyleForTest(1);
            Check("typing: the fine-tuning bar and restyling",
                  matched?.Name == "Inter Bold" && shown && bigger != null && bigger.Size > matched.Size && regular?.Face.Weight == FontWeight.Normal &&
                  roboto?.Face.FontFamily.Name == "Roboto" && text == "Hello there" && reset?.Name == matched.Name && ed.TextForTest == "Hello there",
                  $"matched {matched?.Name} {matched?.Size:0.#}, bar shown {shown}, bigger {bigger?.Size:0.#}, regular {regular?.Name}, roboto {roboto?.Name}, " +
                  $"text after {text}, reset {reset?.Name}, text {ed.TextForTest}");
        });

        Try("links in screenshots and hiding selected text", () =>
        {
            var face = new Typeface(InterFamily);
            var page = TextPage(face, 16, "visit example.com or mail sam@test.org", Colors.Black, Colors.White, out var words, 420);
            var ed = new EditorWindow(page, null);
            ed.SelectWordsForTest(words, 1, 1, new[] { 0, 0, 0, 0, 0 });
            var link = ed.LinkAtForTest(words[1].Bounds.Center);
            var mail = ed.LinkAtForTest(words[4].Bounds.Center);
            var none = ed.LinkAtForTest(words[0].Bounds.Center);
            string hidden = ed.HideSelectedForTest();
            Check("links in screenshots and hiding selected text",
                  link?.Kind == Overskin.Linux.Platform.TextActionKind.Link && link.Value == "example.com" && mail?.Kind == Overskin.Linux.Platform.TextActionKind.Email && none == null && hidden == "True",
                  $"link {link?.Kind} {link?.Value}, mail {mail?.Kind} {mail?.Value}, plain word {none?.Kind.ToString() ?? "nothing"}, hidden {hidden}");
        });

        Try("picking up marks: move, stretch, delete, undo", () =>
        {
            var ed = new EditorWindow(Quarters(400, 300), null);
            var box = new ShapeMark { Kind = Tool.Rectangle, From = new Point(50, 50), To = new Point(150, 120), Colour = Colors.Red, Width = 4 };
            ed.Canvas.Add(box);
            ed.PickForTest(Tool.Select);
            ed.DragForTest(new Point(100, 52), new Point(130, 72));
            var moved = ed.MarksForTest.OfType<ShapeMark>().Single();
            // the bottom-right corner pulled out
            ed.DragForTest(new Point(180, 140), new Point(220, 180));
            var stretched = ed.MarksForTest.OfType<ShapeMark>().Single();
            ed.DeleteSelectedForTest();
            int afterDelete = ed.MarksForTest.Count;
            ed.Canvas.Undo();
            var back = ed.MarksForTest.OfType<ShapeMark>().SingleOrDefault();
            ed.Canvas.Undo();
            ed.Canvas.Undo();
            var original = ed.MarksForTest.OfType<ShapeMark>().SingleOrDefault();
            Check("picking up marks: move, stretch, delete, undo",
                  moved.From == new Point(80, 70) && moved.To == new Point(180, 140) && stretched.To == new Point(220, 180) && afterDelete == 0 &&
                  back?.To == new Point(220, 180) && original?.From == new Point(50, 50),
                  $"moved {moved.From}-{moved.To}, stretched to {stretched.To}, after delete {afterDelete}, undo {back?.To}, back to {original?.From}");
        });

        Try("colour wheel and device frames", () =>
        {
            var wheel = new ColourWheel { Width = 176, Height = 176 };
            wheel.Measure(new Size(176, 176));
            wheel.Arrange(new Rect(0, 0, 176, 176));
            wheel.PickForTest(new Point(176, 88)); // the right-hand edge: red, full strength
            var red = ColourWheel.FromHsv(wheel.Hue, wheel.Saturation, 1);
            wheel.PickForTest(new Point(88, 88)); // the middle: white
            var white = ColourWheel.FromHsv(wheel.Hue, wheel.Saturation, 1);
            var (h, sat, v) = ColourWheel.ToHsv(Color.FromRgb(40, 120, 200));
            var back = ColourWheel.FromHsv(h, sat, v);

            var shot = Quarters(200, 120);
            string sizes = string.Join(" ", Mockup.Frames.Take(4).Select(f => { var m = Mockup.Apply(shot, f, null); return $"{f}:{m.PixelSize.Width}x{m.PixelSize.Height}"; }));
            bool bigger = Mockup.Frames.Skip(1).Take(3).All(f => { var m = Mockup.Apply(shot, f, null); return m.PixelSize.Width >= 200 && m.PixelSize.Height > 120; });
            // your own frame: a dark picture with a see-through screen in the middle
            var frame = Imaging.Render(new PixelSize(300, 200), ctx => ctx.FillRectangle(Brushes.Black, new Rect(0, 0, 300, 200)));
            var fp = Imaging.Pixels(frame, out _);
            for (int y = 40; y < 160; y++) for (int x = 50; x < 250; x++) { int o = (y * 300 + x) * 4; fp[o] = fp[o + 1] = fp[o + 2] = fp[o + 3] = 0; }
            var framePath = Path.Combine(Path.GetTempPath(), $"overskin-frame-{Guid.NewGuid():N}.png");
            Imaging.SavePng(Imaging.FromPixels(fp, new PixelSize(300, 200)), framePath);
            var screen = Mockup.FindScreen(new Bitmap(framePath));
            var custom = Mockup.Custom(shot, framePath);
            var inside = custom != null ? PixelAt(custom, 60, 50) : default;
            var beautified = Beautify.Render(shot, new BeautifyOptions { Frame = "Phone", Padding = 20 });
            File.Delete(framePath);
            Check("colour wheel and device frames",
                  red.R > 250 && red.G < 5 && white.R > 250 && white.G > 250 && white.B > 250 && Math.Abs(back.B - 200) <= 1 && Math.Abs(back.G - 120) <= 1 &&
                  bigger && screen == new Rect(50, 40, 200, 120) && inside.R > 240 && inside.G < 20 && beautified.PixelSize.Width > 240,
                  $"wheel edge {red}, middle {white}, colour back {back}; {sizes}; own frame's screen {screen}, inside it {inside}; beautified {beautified.PixelSize}");
        });

        Try("workflows: saved, named, hotkeys checked, in the menus", () =>
        {
            var w = new Overskin.Workflow { Name = "Workflow 1", Capture = "Window", CopyText = true, Hotkey = "Ctrl+Alt+7", Delay = 3 };
            var json = System.Text.Json.JsonSerializer.Serialize(new Settings { Workflows = { w } });
            var back = System.Text.Json.JsonSerializer.Deserialize<Settings>(json)!.Workflows.Single();
            var dialog = new WorkflowWindow(new Overskin.Workflow { Name = "Workflow 2" }, null);
            dialog.SetForTest("Screen", copyText: true);
            var made = dialog.CurrentForTest();
            var s = App.Settings;
            string replayKey = s.HotkeyReplay;
            string reordered = string.Join("+", Enumerable.Reverse(replayKey.Split('+')));
            string? clash = App.Current.HotkeyUsedBy(reordered);
            s.Workflows.Add(w);
            string? clashFlow = App.Current.HotkeyUsedBy("Alt+Ctrl+7");
            string? self = App.Current.HotkeyUsedBy("Ctrl+Alt+7", except: w);
            bool inSearch = App.Current.PaletteItems().Any(p => p.Group == "Workflows" && p.Title == w.Title);
            s.Workflows.Remove(w);
            Check("workflows: saved, named, hotkeys checked, in the menus",
                  back.Capture == "Window" && back.CopyText && back.Delay == 3 && back.Hotkey == "Ctrl+Alt+7" && back.Describe() == "Copy text from the window in front after 3 s" &&
                  made.Name == "Copy text from the whole screen" && made.Steps.CopyText && !made.Steps.Copy &&
                  clash != null && clashFlow != null && self == null && inSearch,
                  $"loaded \"{back.Title}\" ({back.Summary}); new one named \"{made.Name}\"; {reordered} used by {clash ?? "nothing"}; Alt+Ctrl+7 used by {clashFlow ?? "nothing"}; in search {inSearch}");
        });

        Try("update check reads GitHub's answer", () =>
        {
            const string Base = "https://github.com/overskin/Overskin/releases/download/v9.1.0/";
            string Json(string tag, bool pre, string pageRepo = "overskin/Overskin") => $$"""
                { "tag_name": "{{tag}}", "prerelease": {{(pre ? "true" : "false")}}, "draft": false,
                  "html_url": "https://github.com/{{pageRepo}}/releases/tag/{{tag}}",
                  "assets": [ { "name": "OverskinSetup.exe", "browser_download_url": "{{Base}}OverskinSetup.exe", "size": 1 },
                              { "name": "Overskin-9.1.0-mac-AppleSilicon.zip", "browser_download_url": "{{Base}}Overskin-9.1.0-mac-AppleSilicon.zip", "size": 2 },
                              { "name": "Overskin-9.1.0-mac-Intel.zip", "browser_download_url": "{{Base}}Overskin-9.1.0-mac-Intel.zip", "size": 3 },
                              { "name": "overskin-9.1.0-linux-x64.tar.gz", "browser_download_url": "https://evil.example/x.tar.gz", "size": 4 } ] }
                """;
            var arm = Updater.ReadRelease(Json("v9.1.0", false), mac: true, arm: true);
            var intel = Updater.ReadRelease(Json("v9.1.0", false), mac: true, arm: false);
            var linux = Updater.ReadRelease(Json("v9.1.0", false), mac: false, arm: false);
            var pre = Updater.ReadRelease(Json("v9.2.0-beta", true), mac: true, arm: true);
            var other = Updater.ReadRelease(Json("v9.1.0", false, "someone/else"), mac: true, arm: true);
            Check("update check reads GitHub's answer",
                  arm?.DownloadSize == 2 && arm.Version == new Version(9, 1, 0) && intel?.DownloadSize == 3 && linux == null && pre == null && other == null &&
                  Updater.ParseTag("v3.2") == new Version(3, 2, 0) && Updater.ParseTag("nope") == null,
                  $"Apple silicon {arm?.DownloadUrl}, Intel {intel?.DownloadUrl}, Linux {(linux == null ? "none (not from GitHub)" : linux.DownloadUrl)}, test version {(pre == null ? "skipped" : "taken")}, other repository {(other == null ? "refused" : "taken")}");
        });

        await TryAsync("viewer: pictures, strip, zoom, rotate, search, videos and GIFs play", async () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "overskin-viewer-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(folder);
            var a = Path.Combine(folder, "alpha.png");
            var b = Path.Combine(folder, "beta.png");
            Imaging.SavePng(Quarters(120, 60), a);
            Imaging.SavePng(Quarters(80, 40), b);
            var files = new List<string> { a, b };
            bool media = Overskin.Linux.Platform.ScreenGrabber.HasCommand("ffmpeg") && Overskin.Linux.Platform.ScreenGrabber.HasCommand("ffprobe");
            string clip = Path.Combine(folder, "clip.mp4"), gif = Path.Combine(folder, "loop.gif");
            if (media)
            {
                await Overskin.Linux.Platform.ScreenGrabber.RunAsync("ffmpeg", $"-hide_banner -loglevel error -y -f lavfi -i testsrc=duration=3:size=320x240:rate=25 -c:v libx264 -pix_fmt yuv420p \"{clip}\"", 120);
                await Overskin.Linux.Platform.ScreenGrabber.RunAsync("ffmpeg", $"-hide_banner -loglevel error -y -f lavfi -i testsrc=duration=1:size=100x80:rate=10 \"{gif}\"", 120);
                files.Add(clip);
                files.Add(gif);
            }
            var viewer = new ViewerWindow(files, a);
            await Task.Delay(300);
            string first = viewer.TitleForTest;
            int film = viewer.FilmForTest;
            viewer.GoForTest(1);
            await Task.Delay(300);
            string second = viewer.TitleForTest;
            await viewer.RotateForTest(90);
            using (var turned = new Bitmap(b)) { }
            var size = new Bitmap(b).PixelSize;
            viewer.SearchForTest("alph");
            string found = viewer.TitleForTest;
            viewer.SearchForTest("");
            string video = "skipped (no ffmpeg)";
            bool videoOk = true;
            if (media)
            {
                viewer.GoForTest(2); // the video (the search put it back on the first)
                for (int i = 0; i < 100 && viewer.PlayerForTest?.Frame == null; i++) await Task.Delay(50);
                var player = viewer.PlayerForTest;
                await Task.Delay(600);
                double pos = player?.Position ?? 0;
                viewer.KeyForTest(Avalonia.Input.Key.Space); // pause
                bool paused = player is { Playing: false };
                viewer.GoForTest(1); // the GIF
                for (int i = 0; i < 100 && viewer.PlayerForTest?.Frame == null; i++) await Task.Delay(50);
                var looping = viewer.PlayerForTest;
                await Task.Delay(1500); // longer than the GIF: it goes round again
                bool stillPlaying = looping is { Playing: true, Loop: true };
                videoOk = player?.Frame != null && player.Size == new PixelSize(320, 240) && Math.Abs(player.Duration - 3) < 0.3 && pos > 0.2 && paused && looping?.Frame != null && stillPlaying;
                video = $"video {player?.Size} {player?.Duration:0.0}s, playing at {pos:0.00}s, paused {paused}; GIF frame {(looping?.Frame != null)}, still going round {stillPlaying}";
            }
            viewer.KeyForTest(Avalonia.Input.Key.F);
            bool theater = viewer.TheaterForTest;
            viewer.KeyForTest(Avalonia.Input.Key.Escape);
            bool back = !viewer.TheaterForTest;
            viewer.Close();
            try { Directory.Delete(folder, true); } catch { }
            Check("viewer: pictures, strip, zoom, rotate, search, videos and GIFs play",
                  first == "alpha.png" && film == files.Count && second == "beta.png" && size == new PixelSize(40, 80) && found == "alpha.png" && videoOk && theater && back,
                  $"opened on {first}, strip {film}, next {second}, beta turned to {size}, search found {found}; {video}; full screen {theater}, Esc back {back}");
        });

        Try("pins: turn, flip, grey, free transform, kept over a restart", () =>
        {
            var pin = new PinWindow(Quarters(200, 100));
            double scale = pin.Screens.Primary?.Scaling ?? 1;
            var start = pin.ShapeForTest;
            pin.TurnForTest(90);
            var turned = pin.ShapeForTest;
            double tall = pin.Height;
            pin.TurnForTest(-90);
            pin.FlipForTest();
            var corner = PixelAt(pin.ShownForTest, 2, 2); // was red; flipped, the green quarter
            pin.GrayForTest();
            var grey = PixelAt(pin.ShownForTest, 2, 2);
            pin.Transforming = true;
            var c = pin.Centre;
            double w = start.W, h = start.H;
            var onCorner = pin.HitTest(new Point(c.X + w / 2, c.Y + h / 2));
            var inside = pin.HitTest(c);
            var outside = pin.HitTest(new Point(c.X + w / 2 + 20, c.Y + h / 2 + 20));
            // pull the bottom-right corner out along the diagonal: in proportion, the top-left stays
            pin.TransformStart(PinWindow.Zone.Handle, 1, 1, new Point(c.X + w / 2, c.Y + h / 2));
            pin.TransformMove(new Point(c.X + w / 2 + w * 0.2, c.Y + h / 2 + h * 0.2), shift: false, alt: false);
            pin.TransformEnd();
            var bigger = pin.ShapeForTest;
            bool reverted = pin.Revert();
            var back = pin.ShapeForTest;
            pin.Transforming = false;
            var state = pin.ToState();
            bool kept = File.Exists(state.Path);
            var again = new PinWindow(new Bitmap(state.Path), source: state.Path, restore: state);
            File.Delete(state.Path);
            Check("pins: turn, flip, grey, free transform, kept over a restart",
                  Math.Abs(turned.Angle - 90) < 0.01 && tall > pin.Width - 1 && corner.G > 240 && corner.R < 20 && grey.R == grey.G && grey.G == grey.B &&
                  onCorner.Zone == PinWindow.Zone.Handle && onCorner.Sx == 1 && inside.Zone == PinWindow.Zone.Move && outside.Zone == PinWindow.Zone.Rotate &&
                  Math.Abs(bigger.W - w * 1.2) < 0.5 && Math.Abs(bigger.H - h * 1.2) < 0.5 && reverted && Math.Abs(back.W - w) < 0.01 &&
                  kept && state.FlipHorizontal && state.Grayscale && Math.Abs(state.Width - w) < 0.01,
                  $"size {start.W:0}×{start.H:0}, turned {turned.Angle}°; flipped corner {corner}, grey {grey}; corner {onCorner.Zone}, middle {inside.Zone}, beyond {outside.Zone}; " +
                  $"pulled to {bigger.W:0.#}×{bigger.H:0.#}, Esc back to {back.W:0.#}; kept in {Path.GetFileName(state.Path)}");
        });

        if (Overskin.Linux.Platform.ScreenGrabber.HasCommand("ffmpeg") && Overskin.Linux.Platform.ScreenGrabber.HasCommand("ffprobe"))
        await TryAsync("video editor: arrows, blur boxes, crop, a small copy, idle parts, editable zooms", async () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "overskin-vedit-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(folder);
            var clip = Path.Combine(folder, "clip.mp4");
            var run = Overskin.Linux.Platform.ScreenGrabber.RunAsync;
            await run("ffmpeg", $"-hide_banner -loglevel error -y -f lavfi -i testsrc=duration=6:size=320x240:rate=25 -f lavfi -i sine=frequency=440:duration=6 -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest \"{clip}\"", 120);
            var ed = await VideoEditor.LoadAsync(clip, quiet: true) ?? throw new InvalidOperationException("the editor didn't open");
            ed.SeekForTest(1); ed.CutForTest(); ed.SeekForTest(2); ed.CutForTest();
            ed.SeekForTest(0.5);
            ed.ArrowModeForTest();
            ed.PressForTest(new Point(40, 200), new Point(150, 120));
            ed.BlurModeForTest();
            ed.PressForTest(new Point(20, 20), new Point(140, 100));
            ed.SetCropForTest(new PixelRect(0, 0, 240, 240));
            var p = ed.ProjectForTest;
            int arrows = p.Overlays.Count(o => o.Kind == VideoOverlay.Kinds.Arrow), blurs = p.Blurs.Count;
            string cuts = string.Join(" ", p.Cuts.Select(c => $"{c.From:0.#}-{c.To:0.#}"));
            var output = Path.Combine(folder, "small.mp4");
            bool saved = await ed.SaveToAsync(output, 120 * 1024);
            long size = File.Exists(output) ? new FileInfo(output).Length : 0;
            var probe = await Overskin.Linux.Platform.ScreenGrabber.OutputOf("ffprobe", $"-v error -show_entries stream=codec_type,width,height:format=duration -of csv=p=0 \"{output}\"") ?? "";
            // the blurred corner is smooth in the saved video; the original there has the test picture's sharp edges
            async Task<double> Roughness(string video, double t)
            {
                var still = Path.Combine(folder, $"s{Guid.NewGuid():N}.png");
                await run("ffmpeg", $"-hide_banner -loglevel error -y -ss {t.ToString(CultureInfo.InvariantCulture)} -i \"{video}\" -frames:v 1 \"{still}\"", 60);
                using var b = new Bitmap(still);
                var px = Imaging.Pixels(b, out int stride);
                // the sharpest step between neighbouring pixels: a bar's edge in the original, soft once blurred
                double most = 0;
                for (int y = 30; y < 90; y++) for (int x = 30; x < 130; x++) most = Math.Max(most, Math.Abs(px[y * stride + x * 4 + 1] - px[y * stride + (x + 1) * 4 + 1]));
                return most;
            }
            double before = await Roughness(clip, 0.7), after = await Roughness(output, 0.7);

            // a video still for 3 seconds, then moving: the still part is found
            var idleClip = Path.Combine(folder, "idle.mp4");
            await run("ffmpeg", $"-hide_banner -loglevel error -y -f lavfi -i color=c=gray:size=160x120:duration=3:rate=10 -f lavfi -i testsrc=duration=2:size=160x120:rate=10 -filter_complex \"[0:v][1:v]concat=n=2:v=1[v]\" -map \"[v]\" -c:v libx264 -pix_fmt yuv420p \"{idleClip}\"", 120);
            var idle = await VideoEditor.FindIdleAsync(idleClip);

            // editable zooms: kept beside the video, made again with the sound
            var track = new PointerTrack();
            for (int i = 0; i <= 60; i++) track.AddForTest(i * 0.1, 100 + i, 80, down: i == 10);
            var zoomed = Path.Combine(folder, "zoom.mp4");
            File.Copy(clip, zoomed);
            await AutoZoom.ApplyAsync(zoomed, track, new PixelRect(0, 0, 320, 240), 2, 0);
            var zprobe = await Overskin.Linux.Platform.ScreenGrabber.OutputOf("ffprobe", $"-v error -show_entries stream=codec_type -of csv=p=0 \"{zoomed}\"") ?? "";
            var loaded = ZoomTrack.Load(zoomed);
            var zed = await VideoEditor.LoadAsync(zoomed, quiet: true);
            bool editable = zed?.ZoomsForTest is { Zooms.Count: >= 1 };
            zed?.Close();
            ed.Close();
            if (Environment.GetEnvironmentVariable("OVERSKIN_TEST_OUT") is { Length: > 0 } dumpTo) File.Copy(output, Path.Combine(dumpTo, "small.mp4"), true);
            try { Directory.Delete(folder, true); } catch { }
            Check("video editor: arrows, blur boxes, crop, a small copy, idle parts, editable zooms",
                  arrows == 1 && blurs == 1 && cuts == "1-2" && saved && size > 0 && size <= 120 * 1024 && probe.Contains("video,240,240") && probe.Contains("audio") &&
                  after < before * 0.4 && idle.Count == 1 && idle[0].Start < 1 && idle[0].End > 2 && idle[0].End < 3 &&
                  zprobe.Contains("audio") && loaded?.Zooms.Count >= 1 && editable,
                  $"arrows {arrows}, blurs {blurs}, cuts {cuts}; small copy {size / 1024} KB (cap 120) passes {string.Join(",", VideoEditor.LastPasses.Select(x => x / 1024))}, {probe.Replace('\n', ' ').Trim()}; " +
                  $"sharpest edge in the blurred corner {before:0} → {after:0}; idle {string.Join(" ", idle.Select(i => $"{i.Start:0.0}-{i.End:0.0}"))}; " +
                  $"zoomed video streams {zprobe.Replace('\n', ' ').Trim()}, zooms {loaded?.Zooms.Count}, editable in the editor {editable}");
        });

        if (ScreenGrabber.HasCommand("ffmpeg") && ScreenGrabber.HasCommand("ffprobe"))
        await TryAsync("recordings finished: smooth pointer, click rings, keys, backdrop, webcam corner", async () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "overskin-look-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(folder);
            var clip = Path.Combine(folder, "rec.mp4");
            await ScreenGrabber.RunAsync("ffmpeg", $"-hide_banner -loglevel error -y -f lavfi -i color=c=0x606060:size=640x400:rate=25:duration=3 -f lavfi -i sine=frequency=300:duration=3 -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest \"{clip}\"", 120);
            var track = new PointerTrack();
            for (int i = 0; i <= 30; i++) track.AddForTest(i * 0.1, 200, 150, down: i == 10);
            var finish = new RecordFinish
            {
                DrawPointer = true, Look = new RecordLook { Backdrop = "Ocean", PointerStyle = "White", PointerSize = 2, MotionBlur = 0, PointerSmoothing = 0 },
                Keys = new() { new KeyPress { T = 2.0, Label = "⌘ C" } },
            };
            await AutoZoom.FinishAsync(clip, track, new PixelRect(0, 0, 640, 400), 0, finish);
            async Task<Bitmap> At(double t)
            {
                var still = Path.Combine(folder, $"s{Guid.NewGuid():N}.png");
                await ScreenGrabber.RunAsync("ffmpeg", $"-hide_banner -loglevel error -y -ss {t.ToString(CultureInfo.InvariantCulture)} -i \"{clip}\" -frames:v 1 \"{still}\"", 60);
                return new Bitmap(still);
            }
            var early = await At(0.5);
            var corner = PixelAt(early, 3, 3);              // the backdrop: Ocean blue
            // the card is 640x400 shrunk into the middle: the pointer's tip (200,150) lands at about (x0 + 200k, y0 + 150k)
            int pad = Math.Max(8, (int)Math.Round(400 * 0.06)) & ~1, iw = (640 - 2 * pad) & ~1, ih = (400 - 2 * pad) & ~1;
            double k = iw / 640.0;
            int tipX = (640 - iw) / 2 + (int)(200 * k), tipY = (400 - ih) / 2 + (int)(150 * k);
            int white = 0;
            for (int y = tipY; y < tipY + 30; y++) for (int x = tipX; x < tipX + 20; x++) if (PixelAt(early, x, y).G > 230) white++;
            var ringFrame = await At(1.15);                 // a click at 1.0: its ring is round the tip
            int ringy = 0;
            for (int y = tipY - 40; y < tipY + 40; y++) for (int x = tipX - 40; x < tipX + 40; x++) if (Math.Abs(Math.Sqrt((x - tipX) * (x - tipX) + (y - tipY) * (y - tipY)) - 20) < 12 && PixelAt(ringFrame, x, y).G > 200) ringy++;
            var keyed = await At(2.3);
            int dark = 0;
            for (int x = 280; x < 360; x++) if (PixelAt(keyed, x, (int)(400 - 400 * 0.1 - 8)).G < 50) dark++;
            var streams = await ScreenGrabber.OutputOf("ffprobe", $"-v error -show_entries stream=codec_type -of csv=p=0 \"{clip}\"") ?? "";

            // webcam in the top-left as a rounded square
            var cam = Path.Combine(folder, "cam.mp4");
            var args = Recorder.FfmpegArgs("-f lavfi -i color=c=white:size=640x400:rate=30:duration=1", "-f lavfi -i color=c=red:size=320x240:rate=30:duration=1",
                Array.Empty<string>(), null, 30, cam, 400, "TopLeft", "Rounded", 30);
            await ScreenGrabber.RunAsync("ffmpeg", "-hide_banner -loglevel error -y " + args, 120);
            var camStill = Path.Combine(folder, "cam.png");
            await ScreenGrabber.RunAsync("ffmpeg", $"-hide_banner -loglevel error -y -ss 0.5 -i \"{cam}\" -frames:v 1 \"{camStill}\"", 60);
            using var camFrame = new Bitmap(camStill);
            var topLeft = PixelAt(camFrame, 24 + 60, 24 + 60);
            var bottomRight = PixelAt(camFrame, 600, 360);
            var roundedCorner = PixelAt(camFrame, 25, 25);
            string labels = $"{MacKeys.Label(8, false, false, false, true)} | {MacKeys.Label(0, false, false, false, false) ?? "none"} | {MacKeys.Label(36, false, false, false, false)} | {MacKeys.Label(123, false, false, true, false) ?? "none"}";
            try { Directory.Delete(folder, true); } catch { }
            Check("recordings finished: smooth pointer, click rings, keys, backdrop, webcam corner",
                  corner.B > corner.R + 40 && white > 20 && ringy > 10 && dark > 20 && streams.Contains("audio") &&
                  topLeft.R > 200 && topLeft.G < 60 && bottomRight.G > 200 && roundedCorner.G > 200 && labels == "⌘ C | none | Return | none",
                  $"backdrop corner {corner}; pointer {white} white px; ring {ringy} px; key label {dark} dark px; streams {streams.Replace('\n', ' ').Trim()}; " +
                  $"webcam top-left {topLeft}, far corner {bottomRight}, rounded corner {roundedCorner}; labels {labels}");
        });

        await TryAsync("pausing (pieces joined, times skip the pause) and drawing while recording", async () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "overskin-pause-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(folder);
            var rec = Recorder.ForTest(Path.Combine(folder, "joined.mp4"));
            rec.PauseForTest(1.0, (3.0, 5.5));
            double during = rec.TrackTimeForTest(1.0), after = rec.TrackTimeForTest(3.0);
            double? inPause = rec.VideoTimeForTest(4.0), later = rec.VideoTimeForTest(6.5);
            string joined = "skipped (no ffmpeg)";
            bool joinOk = true;
            if (ScreenGrabber.HasCommand("ffmpeg") && ScreenGrabber.HasCommand("ffprobe"))
            {
                var parts = new List<string>();
                for (int i = 0; i < 2; i++)
                {
                    var part = Path.Combine(folder, $"p{i}.mp4");
                    await ScreenGrabber.RunAsync("ffmpeg", $"-hide_banner -loglevel error -y -f lavfi -i testsrc=duration=2:size=320x240:rate=25 -f lavfi -i sine=duration=2 -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest " + '"' + part + '"', 120);
                    parts.Add(part);
                }
                await rec.JoinForTest(parts);
                var length = (await ScreenGrabber.OutputOf("ffprobe", "-v error -show_entries format=duration -of csv=p=0 " + '"' + Path.Combine(folder, "joined.mp4") + '"') ?? "").Trim();
                double.TryParse(length, NumberStyles.Float, CultureInfo.InvariantCulture, out double len);
                joinOk = Math.Abs(len - 4) < 0.3 && parts.All(f => !File.Exists(f));
                joined = $"joined {len:0.0}s, pieces tidied {parts.All(f => !File.Exists(f))}";
            }
            var pen = new ScreenPen(new PixelRect(0, 0, 400, 300)) { Linger = 0.1 };
            int drawn = pen.DrawForTest(new Point(10, 10), new Point(200, 150));
            for (int i = 0; i < 40 && pen.MarksForTest > 0; i++) await Task.Delay(50);
            int left = pen.MarksForTest;
            pen.Close();
            try { Directory.Delete(folder, true); } catch { }
            Check("pausing (pieces joined, times skip the pause) and drawing while recording",
                  Math.Abs(during - 2) < 0.01 && Math.Abs(after - 6.5) < 0.01 && inPause == null && later is { } l && Math.Abs(l - 3) < 0.01 && joinOk && drawn == 1 && left == 0,
                  $"1 s in → {during:0.0}, 3 s in → {after:0.0}; a moment in the pause → {(inPause?.ToString("0.0") ?? "none")}, after it → {later:0.0}; {joined}; pen drew {drawn}, after fading {left}");
        });

        Try("capture bar: shapes, exact sizes and the pointer drawn in", () =>
        {
            var surface = new SelectionSurface(Quarters(800, 600)) { Ratio = 16 / 9.0 };
            var end = surface.ShapedForTest(new Point(100, 100), new Point(260, 120));
            var box = surface.FixedForTest(new Point(790, 10), new PixelSize(400, 300));
            var pic = Imaging.Render(new PixelSize(60, 60), ctx =>
            {
                ctx.FillRectangle(Brushes.White, new Rect(0, 0, 60, 60));
                new PointerMark { At = new Point(10, 10), Scale = 2 }.Draw(ctx);
            });
            var inside = PixelAt(pic, 14, 30);
            Check("capture bar: shapes, exact sizes and the pointer drawn in",
                  Math.Abs((end.X - 100) / (end.Y - 100) - 16 / 9.0) < 0.01 && box == new PixelRect(400, 0, 400, 300) && inside.G < 60,
                  $"16:9 drag ends at {end}, a 400×300 box at the corner {box}, pointer ink {inside}");
        });

        await TryAsync("capture screen snaps to pictures", async () =>
        {
            var rnd = new Random(3);
            var screen = Imaging.Render(new PixelSize(800, 500), ctx =>
            {
                ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(245, 245, 245)), new Rect(0, 0, 800, 500));
                for (int y = 0; y < 160; y += 4)
                    for (int x = 0; x < 240; x += 4)
                        ctx.FillRectangle(new SolidColorBrush(Color.FromRgb((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256))), new Rect(300 + x, 150 + y, 4, 4));
            });
            screen = Imaging.FromPixels(Imaging.Pixels(screen, out _), screen.PixelSize);
            var surface = new SelectionSurface(screen);
            var (count, onPicture) = await surface.PictureForTestAsync(400, 220);
            var (_, offPicture) = await surface.PictureForTestAsync(100, 100);
            Check("capture screen snaps to pictures", count >= 1 && onPicture == new PixelRect(300, 150, 240, 160) && offPicture == null,
                  $"{count} found; over the picture {onPicture?.ToString() ?? "nothing"}, beside it {offPicture?.ToString() ?? "nothing"}");
        });

        await TryAsync("Move tool: lift text off and swap a picture", async () =>
        {
            var face = new Typeface(InterFamily, FontStyle.Normal, FontWeight.Bold);
            var page = TextPage(face, 30, "Move me", Colors.Black, Colors.White, out var words, 300, 80);
            var ed = new EditorWindow(page, null);
            ed.SelectWordsForTest(words, 0, 0);
            var r = words[0].Bounds;
            await ed.LiftForTest(new Int32Rect((int)r.X - 2, (int)r.Y - 2, (int)r.Width + 4, (int)r.Height + 4), keyOut: true, 0);
            var piece = ed.MarksForTest.OfType<PieceMark>().SingleOrDefault();
            var gap = PixelAt(ed.BaseForTest, (int)r.Center.X, (int)r.Center.Y);
            var corner = piece != null ? PixelAt(piece.Pixels, 0, 0) : default;
            bool inked = false;
            if (piece != null)
            {
                var pp = Imaging.Pixels(piece.Pixels, out _);
                for (int i = 3; i < pp.Length && !inked; i += 4) inked = pp[i] > 240 && pp[i - 1] < 40;
            }

            // a colourful picture in a flat page: found, then swapped for the four quarters
            var rnd = new Random(7);
            var shot = Imaging.Render(new PixelSize(600, 400), ctx =>
            {
                ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(236, 236, 236)), new Rect(0, 0, 600, 400));
                for (int y = 0; y < 150; y += 5)
                    for (int x = 0; x < 200; x += 5)
                        ctx.FillRectangle(new SolidColorBrush(Color.FromRgb((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256))), new Rect(200 + x, 120 + y, 5, 5));
            });
            shot = Imaging.FromPixels(Imaging.Pixels(shot, out _), shot.PixelSize);
            var ed2 = new EditorWindow(shot, null);
            int pictures = ed2.PicturesForTest();
            if (pictures > 0) ed2.SwapPictureForTest(0, Quarters(40, 30));
            var swapped = PixelAt(ed2.BaseForTest, 210, 130);
            Check("Move tool: lift text off and swap a picture",
                  piece != null && gap.G > 240 && corner.A < 20 && inked && pictures >= 1 && swapped.R > 240 && swapped.G < 20,
                  $"piece {(piece != null ? $"{piece.Pixels.PixelSize}" : "none")}, gap left {gap.G}, piece corner see-through {corner.A}, ink kept {inked}; " +
                  $"pictures found {pictures}, after swapping {swapped}");
        });
    }
}
