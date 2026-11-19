using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static Overskin.NativeMethods;

namespace Overskin;

/// <summary>
/// Trace's Gartic Phone style: picking the game's pen, five brush sizes and opacity buttons,
/// measuring how thick each brush draws, and turning a painting plan into clicks and strokes.
/// </summary>
public partial class MainWindow
{
    /// <summary>-1 when not picking; 0 the pen, 1–5 the brush sizes, 6 faintest opacity, 7 full opacity.</summary>
    private int _garticPickStep = -1;

    /// <summary>The test lines being drawn to measure the brushes, while that is happening.</summary>
    private sealed record GarticMeasure(Int32Rect Region, double[] LineY, double X0, double X1, double[] Guess);
    private GarticMeasure? _garticMeasure;

    private bool _syncingGartic;

    private static readonly string[] GarticPickPrompts =
    {
        "Gartic's buttons, 1 of 8: click the pen tool.  Esc cancels.",
        "2 of 8: click the smallest brush size.",
        "3 of 8: the next brush size up.",
        "4 of 8: the middle brush size.",
        "5 of 8: the next size up.",
        "6 of 8: the biggest brush size.",
        "7 of 8: the faintest (most see-through) opacity.",
        "8 of 8: the full, solid opacity.",
    };

    /// <summary>Fills in the Gartic checklist: what's done, what's next, and what each button says.</summary>
    private void SyncGarticControls()
    {
        if (GarticButtonsText == null || _settings == null) return;
        _syncingGartic = true;
        var g = _settings.Gartic;
        int colours = _settings.Palette.Count(p => !p.Skip);
        bool pickingColours = _paletteCapture?.IsActive == true && _garticPickStep < 0;
        bool hasDark = _settings.Palette.Any(p => !p.Skip && TraceEngine.Luma(p.Color) <= 110);

        bool[] done = { colours > 0 && hasDark, g.ButtonsPicked, _area != null, g.BrushPx != null };
        int next = Array.IndexOf(done, false); // -1 when everything's ready

        GarticColoursText.Text = colours == 0 ? "Not picked yet" : !hasDark ? $"{colours} colours, but no black" : $"{colours} in use";
        GarticColoursButtonLabel.Text = pickingColours ? "Done" : colours > 0 ? "Pick again" : "Pick colours";
        GarticButtonsText.Text = _garticPickStep >= 0 ? $"Clicking {_garticPickStep + 1} of {GarticPickPrompts.Length}…"
            : g.ButtonsPicked ? "Pen, 5 sizes and opacity" : "Pen, 5 sizes and opacity: not picked yet";
        GarticPickButtonLabel.Text = _garticPickStep >= 0 ? "Cancel" : g.ButtonsPicked ? "Pick again" : "Pick buttons";
        GarticAreaText.Text = _area is { } a ? $"{a.Width} × {a.Height}" : "Not set yet";
        GarticAreaButtonLabel.Text = _area != null ? "Set again" : "Set area";
        GarticSizesText.Text = g.BrushPx is { } px ? string.Join(" · ", px.Select(v => v.ToString("0"))) + " px" : "Not measured yet";
        GarticMeasureButtonLabel.Text = g.BrushPx != null ? "Measure again" : "Measure";
        GarticMeasureButton.IsEnabled = done[0] && done[1] && done[2];
        GarticOpacitySwitch.IsChecked = g.UseOpacity;

        var dots = new[] { (GarticStep1Dot, GarticStep1Num), (GarticStep2Dot, GarticStep2Num), (GarticStep3Dot, GarticStep3Num), (GarticStep4Dot, GarticStep4Num) };
        for (int i = 0; i < dots.Length; i++)
        {
            var (dot, num) = dots[i];
            num.Text = done[i] ? "✓" : (i + 1).ToString();
            if (done[i])
            {
                dot.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "Accent");
                dot.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "Accent");
                num.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "AccentInk");
            }
            else
            {
                dot.Background = Brushes.Transparent;
                dot.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, i == next ? "Accent" : "TextFaint");
                num.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, i == next ? "Accent" : "TextDim");
            }
        }
        GarticNextText.Text = next switch
        {
            0 => "Start with 1: open Gartic, then pick its colours, black included.",
            1 => "Next, 2: Overskin hides and a message tells you which of Gartic's buttons to click, one at a time.",
            2 => "Next, 3: drag over Gartic's white canvas.",
            3 => "Last, 4: on an empty canvas, Measure draws five test lines, reads them and undoes them.",
            _ => "Ready. Draw on an empty canvas. Detail trades drawing time for accuracy.",
        };
        _syncingGartic = false;
    }

    private void GarticSetting_Click(object sender, RoutedEventArgs e)
    {
        if (_syncingGartic) return;
        _settings.Gartic.UseOpacity = GarticOpacitySwitch.IsChecked == true;
        SyncGarticControls();
        SaveSettings();
        ScheduleTrace();
    }

    // ---- picking the game's buttons ----------------------------------------------------------

    private void GarticPick_Click(object sender, RoutedEventArgs e)
    {
        if (_paletteCapture == null) return;
        if (_paletteCapture.IsActive)
        {
            _paletteCapture.Stop();
            return;
        }
        if (!_paletteCapture.Start())
        {
            Toast.Show("Couldn't start picking.", true);
            return;
        }
        _garticPickStep = 0;
        _garticPending = new GarticSetup();
        SyncGarticControls();
        _hotkeys?.Register(HkPaletteEsc, new Hotkey(ModifierKeys.None, Key.Escape), () => _paletteCapture.Stop());
        WindowState = WindowState.Minimized;
        Toast.ShowSticky(GarticPickPrompts[0]);
    }

    /// <summary>The clicks so far; they replace the saved buttons only once all eight are in, so cancelling keeps the old ones.</summary>
    private GarticSetup? _garticPending;

    private void GarticButtonPicked(int x, int y, Color colour)
    {
        var g = _garticPending ??= new GarticSetup();
        var spot = new PaletteSwatch { X = x, Y = y, Hex = Theming.ToHex(colour) };
        switch (_garticPickStep)
        {
            case 0: g.Pen = spot; break;
            case >= 1 and <= 5: g.Sizes.Add(spot); break;
            case 6: g.OpacityLow = spot; break;
            case 7: g.OpacityHigh = spot; break;
        }
        _garticPickStep++;
        if (_garticPickStep >= GarticPickPrompts.Length)
        {
            var saved = _settings.Gartic;
            saved.Pen = g.Pen;
            saved.Sizes = g.Sizes;
            saved.OpacityLow = g.OpacityLow;
            saved.OpacityHigh = g.OpacityHigh;
            _garticPending = null;
            _paletteCapture?.Stop(); // ends the picking; OnPaletteCaptureEnded tidies up
            Toast.Show(_area == null ? "Gartic's buttons are set. Next: Set area over the canvas." : "Gartic's buttons are set. Last step: Measure.");
            return;
        }
        Toast.ShowSticky(GarticPickPrompts[_garticPickStep]);
    }

    // ---- tracing -------------------------------------------------------------------------------

    /// <summary>Paints at screen size where it can (at most 900 px across, to keep planning quick).</summary>
    private TraceOptions GarticOptions()
    {
        var g = _settings.Gartic;
        double baseScale = BaseScale();
        double screenLongest = _traceImage == null ? 1 : Math.Max(_traceImage.Width, _traceImage.Height) * baseScale;
        double k = Math.Min(1, 900 / Math.Max(1, screenLongest)); // working pixels per screen pixel
        return new TraceOptions
        {
            Mode = TraceMode.Gartic,
            Scale = baseScale * k,
            Palette = _settings.Palette.Select(p => (p.Color, p.Skip)).ToList(),
            // measured brushes are in screen pixels; with no area chosen yet, the guess is as good as anything
            BrushSizes = g.BrushPx != null && _area != null ? g.BrushPx.Select(v => v * k).ToArray() : Array.Empty<double>(),
            OpacityLevels = g.UseOpacity ? GarticSetup.OpacityLevels : 1,
            // Detail 100% = a sensible balance; higher fixes smaller mistakes and takes longer
            Tolerance2 = 28 * 100 / Math.Max(10, ScaleSlider.Value),
        };
    }

    /// <summary>The painting plan as mouse strokes, each with the pen, brush, opacity and colour it needs.</summary>
    private List<DrawStroke>? GarticStrokes(TraceResult t, Int32Rect rect)
    {
        var g = _settings.Gartic;
        if (!g.ButtonsPicked)
        {
            Toast.Show("Pick Gartic's buttons first (Gartic Phone card): the pen, the five brushes and the opacity.");
            return null;
        }
        double sx = (double)rect.Width / t.Width, sy = (double)rect.Height / t.Height;
        var pen = new Point(g.Pen!.X, g.Pen.Y);
        var strokes = new List<DrawStroke>(t.Paths.Count);
        // drawn like a hand: each stroke eases in and out and wanders a little (see HumanStroke)
        var rng = new Random(t.Paths.Count);
        double cruise = GarticCruise();
        for (int p = 0; p < t.Paths.Count; p++)
        {
            var path = t.Paths[p];
            var mapped = new Point[path.Length];
            for (int i = 0; i < path.Length; i++) mapped[i] = new Point(rect.X + path[i].X * sx, rect.Y + path[i].Y * sy);
            mapped = HumanStroke.Path(mapped, cruise, 0.9, rng);
            int colour = t.ColourIndex![p];
            Point? swatch = colour < _settings.Palette.Count ? new Point(_settings.Palette[colour].X, _settings.Palette[colour].Y) : null;
            var size = g.Sizes[Math.Clamp(t.SizeIndex![p], 0, g.Sizes.Count - 1)];
            int level = g.UseOpacity ? t.OpacityIndex![p] : GarticSetup.OpacityLevels;
            strokes.Add(new DrawStroke(mapped, swatch, pen, new Point(size.X, size.Y), g.OpacityAt(level)));
        }
        return strokes;
    }

    /// <summary>Pixels per tick at full speed for the chosen drawing speed.</summary>
    private double GarticCruise() => _settings.TraceSpeed switch { "Careful" => 9, "Fast" => 28, _ => 18 };

    // ---- measuring the brushes ---------------------------------------------------------------

    /// <summary>
    /// Draws a short dark line with each brush in the top-left of the area, then (once it's done)
    /// reads how thick each one came out and undoes them with Ctrl+Z.
    /// </summary>
    private void GarticMeasure_Click(object sender, RoutedEventArgs e)
    {
        if (_drawer.IsRunning) return;
        var g = _settings.Gartic;
        if (!g.ButtonsPicked)
        {
            Toast.Show("Pick Gartic's buttons first.");
            return;
        }
        if (_area is not { } area)
        {
            Toast.Show("Set the area to Gartic's canvas first: click Set area.");
            return;
        }
        var dark = _settings.Palette.Where(p => !p.Skip).OrderBy(p => TraceEngine.Luma(p.Color)).FirstOrDefault();
        if (dark == null || TraceEngine.Luma(dark.Color) > 110)
        {
            Toast.Show("Pick the game's colours first (including black), so the test lines show up.");
            return;
        }

        // generous room for each line (three times the guessed thickness), squeezed if the area is short
        var guess = g.BrushPx ?? Painter.DefaultSizes(area.Width, area.Height);
        var room = guess.Select(v => v * 3 + 14).ToArray();
        double available = area.Height - 24 - 6 * (room.Length - 1);
        if (room.Sum() > available) room = room.Select(r => r * available / room.Sum()).ToArray();
        double length = Math.Max(90, guess.Max() * 3);
        double x0 = area.X + 16 + guess.Max(), x1 = x0 + length, y = area.Y + 12;
        var lineY = new double[GarticSetup.SizeCount];
        for (int i = 0; i < lineY.Length; i++)
        {
            lineY[i] = Math.Round(y + room[i] / 2);
            y += room[i] + 6;
        }
        if (available < 60 || x1 + guess.Max() > area.X + area.Width)
        {
            Toast.Show("The area is too small to measure in. Set it to Gartic's whole canvas.");
            return;
        }

        var pen = new Point(g.Pen!.X, g.Pen.Y);
        var full = g.OpacityAt(GarticSetup.OpacityLevels);
        var strokes = new List<DrawStroke>();
        for (int i = 0; i < GarticSetup.SizeCount; i++)
        {
            // many small steps, so the game sees a real drag rather than one jump
            var points = Enumerable.Range(0, (int)(length / 4) + 1).Select(k => new Point(Math.Min(x1, x0 + k * 4), lineY[i])).ToArray();
            strokes.Add(new DrawStroke(points, new Point(dark.X, dark.Y), pen, new Point(g.Sizes[i].X, g.Sizes[i].Y), full));
        }

        int pad = (int)Math.Ceiling(guess.Max() * 2) + 8;
        int left = Math.Max(area.X, (int)x0 - pad), right = Math.Min(area.X + area.Width, (int)x1 + pad);
        var region = new Int32Rect(left, area.Y, right - left, Math.Min(area.Height, (int)(y - area.Y) + 8));
        _garticMeasure = new GarticMeasure(region, lineY, x0, x1, guess);
        StartDrawing(strokes, area, CurrentDrawSettings(countdown: 2));
    }

    private async Task FinishGarticMeasureAsync(GarticMeasure m, bool restoreWindow)
    {
        await Task.Delay(450); // let the game finish drawing the last line
        var shot = CaptureScreen(m.Region.X, m.Region.Y, m.Region.Width, m.Region.Height, cursor: false);
        double[]? measured = shot == null ? null : MeasureLines(shot, m);
        // the test lines go again either way
        UndoInGame(GarticSetup.SizeCount);
        if (restoreWindow) WindowState = WindowState.Normal;

        int found = measured?.Count(v => !double.IsNaN(v)) ?? 0;
        if (shot != null && found < GarticSetup.SizeCount) SaveMeasureShot(shot);
        if (measured == null || found < 3)
        {
            Toast.Show("Couldn't see the test lines. Make sure the area covers Gartic's empty canvas, a dark colour is picked, " +
                       "and the brushes were clicked smallest first, then measure again.", true);
            return;
        }
        string note = "";
        if (found < GarticSetup.SizeCount)
        {
            // a brush that didn't show is estimated from the nearest one that did, in the usual proportion
            var missing = Enumerable.Range(0, measured.Length).Where(i => double.IsNaN(measured[i])).ToList();
            foreach (int i in missing)
            {
                int near = Enumerable.Range(0, measured.Length).Where(k => !double.IsNaN(measured[k])).OrderBy(k => Math.Abs(k - i)).First();
                measured[i] = Math.Round(measured[near] * m.Guess[i] / m.Guess[near]);
            }
            note = $" Brush {string.Join(", ", missing.Select(i => i + 1))} didn't show, so it's estimated.";
        }
        Array.Sort(measured);
        _settings.Gartic.BrushPx = measured;
        SaveSettings();
        SyncGarticControls();
        ScheduleTrace();
        Toast.Show("Brushes measured: " + string.Join(" · ", measured.Select(v => v.ToString("0"))) + " px." + note +
                   " The test lines were undone; if any are left, clear the canvas.");
    }

    /// <summary>Keeps the picture of test lines that couldn't all be read, in the logs folder, to see what went wrong.</summary>
    private static void SaveMeasureShot(BitmapSource shot)
    {
        try
        {
            string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Overskin", "logs");
            System.IO.Directory.CreateDirectory(dir);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(shot));
            using var f = System.IO.File.Create(System.IO.Path.Combine(dir, "gartic-measure.png"));
            enc.Save(f);
        }
        catch { /* only a diagnostic */ }
    }

    /// <summary>
    /// How thick each test line is, in screen pixels (NaN for a line that isn't there).
    /// The canvas colour is the most common one in the picture, so any ink colour works. Each line
    /// is looked for anywhere in its own band (halfway to the lines either side), so it's found even
    /// if the game drew it a little off. In columns along the middle of the line, the thickness is
    /// how much ink the column holds (soft edge pixels count in part); the answer is the median.
    /// </summary>
    internal static double[]? MeasureLines(BitmapSource shot, double regionLeft, double regionTop, double[] lineY, double x0, double x1)
    {
        var bgra = new FormatConvertedBitmap(shot, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        if (w < 4 || h < 4) return null;
        var px = new byte[w * h * 4];
        bgra.CopyPixels(px, w * 4, 0);

        // the canvas colour: the most common colour (to 4 bits a channel), averaged
        var counts = new Dictionary<int, (int N, long B, long G, long R)>();
        for (int i = 0; i < px.Length; i += 4)
        {
            int key = (px[i] >> 4) | (px[i + 1] >> 4 << 4) | (px[i + 2] >> 4 << 8);
            counts.TryGetValue(key, out var c);
            counts[key] = (c.N + 1, c.B + px[i], c.G + px[i + 1], c.R + px[i + 2]);
        }
        var bg = counts.Values.MaxBy(c => c.N);
        double bgB = (double)bg.B / bg.N, bgG = (double)bg.G / bg.N, bgR = (double)bg.R / bg.N;
        double Ink(int x, int y)
        {
            int i = (y * w + x) * 4;
            double db = px[i] - bgB, dg = px[i + 1] - bgG, dr = px[i + 2] - bgR;
            return Math.Sqrt(db * db + dg * dg + dr * dr);
        }

        var result = new double[lineY.Length];
        for (int li = 0; li < lineY.Length; li++)
        {
            double cy = lineY[li] - regionTop;
            int top = (int)Math.Ceiling(li == 0 ? 0 : (lineY[li - 1] + lineY[li]) / 2 - regionTop);
            int bottom = (int)Math.Floor(li == lineY.Length - 1 ? h - 1 : (lineY[li] + lineY[li + 1]) / 2 - regionTop);
            top = Math.Clamp(top, 0, h - 1);
            bottom = Math.Clamp(bottom, top, h - 1);
            // the middle half of the line, clear of the round ends
            int cx0 = Math.Clamp((int)Math.Round(x0 + (x1 - x0) * 0.25 - regionLeft), 0, w - 1);
            int cx1 = Math.Clamp((int)Math.Round(x1 - (x1 - x0) * 0.25 - regionLeft), 0, w - 1);

            // how strong the ink is at the line's core
            var strengths = new List<double>();
            for (int x = cx0; x <= cx1; x += 2)
                for (int y = top; y <= bottom; y++)
                    strengths.Add(Ink(x, y));
            strengths.Sort();
            double core = strengths.Count == 0 ? 0 : strengths[(int)(strengths.Count * 0.98)];
            if (core < 60) { result[li] = double.NaN; continue; } // only canvas here

            var widths = new List<double>();
            for (int x = cx0; x <= cx1; x += 2)
            {
                // the solid ink nearest the expected middle, grown out over its soft edges
                int best = -1;
                double bestDist = double.MaxValue;
                for (int y = top; y <= bottom; y++)
                    if (Ink(x, y) >= core * 0.5 && Math.Abs(y - cy) < bestDist) { best = y; bestDist = Math.Abs(y - cy); }
                if (best < 0) continue;
                int a = best, b = best;
                while (a > top && Ink(x, a - 1) > core * 0.12) a--;
                while (b < bottom && Ink(x, b + 1) > core * 0.12) b++;
                double sum = 0;
                for (int y = a; y <= b; y++) sum += Math.Min(1, Ink(x, y) / core);
                widths.Add(sum);
            }
            if (widths.Count < 3) { result[li] = double.NaN; continue; }
            widths.Sort();
            result[li] = Math.Max(1, Math.Round(widths[widths.Count / 2], 1));
        }
        return result;
    }

    private static double[]? MeasureLines(BitmapSource shot, GarticMeasure m) =>
        MeasureLines(shot, m.Region.X, m.Region.Y, m.LineY, m.X0, m.X1);

    /// <summary>Presses Ctrl+Z in the game, <paramref name="times"/> times.</summary>
    private static void UndoInGame(int times)
    {
        const ushort VkControl = 0x11, VkZ = 0x5A;
        const uint KeyUp = 0x0002, InputKeyboard = 1;
        static void Key(ushort vk, bool up)
        {
            var input = new INPUT[1];
            input[0].type = InputKeyboard;
            input[0].u.ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KeyUp : 0 };
            SendInput(1, input, Marshal.SizeOf<INPUT>());
        }
        for (int i = 0; i < times; i++)
        {
            Key(VkControl, false);
            Key(VkZ, false);
            Key(VkZ, true);
            Key(VkControl, true);
            Thread.Sleep(80);
        }
    }
}
