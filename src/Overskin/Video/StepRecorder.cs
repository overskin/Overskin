using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using static Overskin.NativeMethods;

namespace Overskin;

/// <summary>One recorded click: the screen just before it, where it was, and what was clicked.</summary>
public sealed class RecordedStep
{
    public required BitmapSource Shot { get; init; }
    /// <summary>The click, in pixels of <see cref="Shot"/>.</summary>
    public required Point At { get; init; }
    public required bool Right { get; init; }
    public required string Window { get; init; }
    public string? Target { get; set; }
    /// <summary>True when <see cref="Target"/> is text read off the screen rather than a control's name.</summary>
    public bool TargetIsText { get; set; }
    /// <summary>Finding out what was clicked, which can take a moment.</summary>
    public Task? Naming { get; set; }
}

/// <summary>
/// Step recorder: every click is captured (the window being clicked, just before the click lands)
/// with the name of what was clicked, read through Windows' accessibility interface. Stopping turns
/// the steps into one numbered tutorial picture.
/// </summary>
public sealed class StepRecorder : IRecorder
{
    public const int MaxSteps = 50;

    private readonly Dispatcher _ui;
    private readonly Func<IntPtr, bool> _isOwnWindow;
    private readonly LowLevelMouseProc _proc;
    private readonly Stopwatch _clock = new();
    private readonly List<RecordedStep> _steps = new();
    private IntPtr _hook;
    private RecordBarWindow? _bar;

    public bool IsRecording => _hook != IntPtr.Zero;
    public TimeSpan Elapsed => _clock.Elapsed;
    public TimeSpan? Limit => null;
    public int Count => _steps.Count;

    /// <summary>Raised on the UI thread with the steps, or null when cancelled or nothing was clicked.</summary>
    public event Action<List<RecordedStep>?>? Finished;

    public StepRecorder(Dispatcher ui, Func<IntPtr, bool> isOwnWindow)
    {
        _ui = ui;
        _isOwnWindow = isOwnWindow;
        _proc = HookProc;
    }

    public bool Start()
    {
        if (IsRecording) return true;
        _steps.Clear();
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) return false;
        var mon = MonitorWorkArea(CursorPos());
        _bar = new RecordBarWindow(this, new Int32Rect(mon.Left, mon.Bottom - 80, mon.Width, 10));
        _bar.Show();
        _clock.Restart();
        Toast.Show("Recording steps: click through what you want to show. Stop when you're done.", seconds: 4);
        return true;
    }

    public void Stop() => End(keep: true);
    public void Cancel() => End(keep: false);

    private void End(bool keep)
    {
        if (!IsRecording) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _clock.Stop();
        _bar?.Close();
        _bar = null;
        // let the last steps finish finding out what was clicked
        var naming = _steps.Select(s => s.Naming).OfType<Task>().ToArray();
        if (keep && naming.Length > 0) Task.WaitAll(naming, TimeSpan.FromSeconds(4));
        Finished?.Invoke(keep && _steps.Count > 0 ? new List<RecordedStep>(_steps) : null);
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        int msg = wParam.ToInt32();
        if (nCode >= 0 && (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN) && _steps.Count < MaxSteps)
        {
            try { Record(Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam).pt, msg == WM_RBUTTONDOWN); }
            catch { /* a missed step is better than a stuck mouse */ }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>Runs inside the mouse hook, before the click reaches the program: keep it quick.</summary>
    private void Record(POINT pt, bool right)
    {
        IntPtr hit = WindowFromPoint(pt);
        IntPtr root = hit == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hit, GA_ROOT);
        if (root != IntPtr.Zero && _isOwnWindow(root)) return; // the Stop button and friends

        // the clicked window when it's a decent size, otherwise the whole screen
        var mon = MonitorBounds(pt);
        RECT area = mon;
        if (root != IntPtr.Zero && !IsDesktopWindow(root))
        {
            var b = VisibleBounds(root);
            var clipped = new RECT { Left = Math.Max(b.Left, mon.Left), Top = Math.Max(b.Top, mon.Top), Right = Math.Min(b.Right, mon.Right), Bottom = Math.Min(b.Bottom, mon.Bottom) };
            if (clipped.Width >= 240 && clipped.Height >= 160 && clipped.Contains(pt)) area = clipped;
        }
        var shot = CaptureScreen(area.Left, area.Top, area.Width, area.Height);
        if (shot == null) return;

        var step = new RecordedStep
        {
            Shot = shot,
            At = new Point(pt.X - area.Left, pt.Y - area.Top),
            Right = right,
            Window = root == IntPtr.Zero || IsDesktopWindow(root) ? "the desktop" : WindowTitle(root),
        };
        _steps.Add(step);

        // what was clicked, looked up away from the hook (it can take a moment): the control's own
        // name if the program gives one, otherwise the words on screen right where the click landed
        // (games and custom-drawn apps only ever say their own name)
        var point = new Point(pt.X, pt.Y);
        step.Naming = Task.Run(async () =>
        {
            var (name, label) = ElementName(point);
            bool useful = label != null && !SameAsWindow(name, step.Window);
            if (useful) { step.Target = label; return; }
            if (await TextAt(step.Shot, step.At) is { } text) { step.Target = $"“{text}”"; step.TargetIsText = true; }
        });
    }

    private static bool SameAsWindow(string? name, string window) =>
        string.IsNullOrWhiteSpace(name) || name.Contains(window, StringComparison.OrdinalIgnoreCase) || window.Contains(name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The words under or right next to a click, read off the screenshot: the short line of text
    /// the click is on (a button's label), or the nearest word on a longer line.
    /// </summary>
    public static async Task<string?> TextAt(BitmapSource shot, Point at)
    {
        try
        {
            // a strip around the click reads faster and better than the whole window
            int w = Math.Min(shot.PixelWidth, 520), h = Math.Min(shot.PixelHeight, 200);
            int x = (int)Math.Clamp(at.X - w / 2, 0, shot.PixelWidth - w), y = (int)Math.Clamp(at.Y - h / 2, 0, shot.PixelHeight - h);
            var crop = new CroppedBitmap(shot, new Int32Rect(x, y, w, h));
            crop.Freeze();
            var lines = await TextRecognizer.RecognizeLinesAsync(crop);
            if (lines == null || lines.Count == 0) return null;
            var click = new Point(at.X - x, at.Y - y);

            double Distance(Rect r)
            {
                double dx = click.X < r.Left ? r.Left - click.X : click.X > r.Right ? click.X - r.Right : 0;
                double dy = click.Y < r.Top ? r.Top - click.Y : click.Y > r.Bottom ? click.Y - r.Bottom : 0;
                return Math.Sqrt(dx * dx + dy * dy * 2);
            }
            var best = lines.Select(l =>
            {
                var r = Rect.Empty;
                foreach (var word in l) r.Union(word.Bounds);
                return (Words: l, Box: r, Distance: Distance(r));
            }).OrderBy(l => l.Distance).First();
            if (best.Distance > Math.Max(36, best.Box.Height * 2.2)) return null;

            string text = best.Words.Count <= 5
                ? string.Join(" ", best.Words.Select(word => word.Text))
                : best.Words.OrderBy(word => Distance(word.Bounds)).First().Text;
            text = text.Trim().Trim('.', ',', ':', ';', '|');
            return text.Length is >= 2 and <= 48 && text.Any(char.IsLetterOrDigit) ? text : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsDesktopWindow(IntPtr h) => ClassName(h) is "Progman" or "WorkerW" or "Shell_TrayWnd";

    private static string WindowTitle(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetWindowText(h, sb, sb.Capacity);
        string title = sb.ToString().Trim();
        // "Document - App" reads better as just the app
        int dash = title.LastIndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0 && dash < title.Length - 3) title = title[(dash + 3)..];
        return title.Length == 0 ? "the window" : title;
    }

    /// <summary>The control's own name, and "“Save” button" for the caption; nulls when the program doesn't say.</summary>
    private static (string? Name, string? Label) ElementName(Point screen)
    {
        try
        {
            var element = AutomationElement.FromPoint(screen);
            string name = element.Current.Name?.Trim() ?? "";
            if (name.Length == 0 || name.Length > 60) return (null, null);
            string type = element.Current.ControlType.ProgrammaticName.Replace("ControlType.", "");
            string kind = type switch
            {
                "Button" or "SplitButton" => "button", "MenuItem" => "menu item", "TabItem" => "tab", "Hyperlink" => "link",
                "CheckBox" => "check box", "RadioButton" => "option", "ListItem" or "TreeItem" or "DataItem" => "item",
                "Edit" or "Document" => "box", "ComboBox" => "list", _ => "",
            };
            return (name, kind.Length > 0 ? $"“{name}” {kind}" : $"“{name}”");
        }
        catch
        {
            return (null, null);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
}

/// <summary>Lays recorded steps out as one numbered tutorial picture.</summary>
public static class StepTutorial
{
    /// <summary>A classic arrow pointer with its tip at (0, 0).</summary>
    private static readonly Geometry PointerShape = MakePointer();

    private static Geometry MakePointer()
    {
        var g = Geometry.Parse("M0,0 L0,19 L4.6,14.8 L7.8,22 L11,20.6 L7.9,13.6 L14,13.6 Z");
        g.Freeze();
        return g;
    }

    public static string Caption(RecordedStep s)
    {
        string verb = s.Right ? "Right-click" : "Click";
        if (s.Target == null) return $"{verb} in {s.Window}";
        return s.TargetIsText ? $"{verb} {s.Target} in {s.Window}" : $"{verb} the {s.Target} in {s.Window}";
    }

    public static BitmapSource Build(IReadOnlyList<RecordedStep> steps, Color accent)
    {
        const double Width = 1100, Margin = 36, Gap = 34;
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var plain = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        double pixels = 1.0;
        var ink = new SolidColorBrush(Color.FromRgb(0x1F, 0x23, 0x28));
        var dim = new SolidColorBrush(Color.FromRgb(0x65, 0x6D, 0x76));
        var accentBrush = new SolidColorBrush(accent);

        FormattedText Text(string s, double size, Typeface face, Brush brush, double maxWidth = Width) =>
            new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush, pixels) { MaxTextWidth = maxWidth };

        // measure first, then draw
        var title = Text("Steps", 30, typeface, ink);
        var sub = Text($"{steps.Count} step{(steps.Count == 1 ? "" : "s")}  ·  {DateTime.Now:d MMMM yyyy, HH:mm}", 15, plain, dim);
        double y = Margin + title.Height + 4 + sub.Height + Gap;
        var layout = new List<(FormattedText Caption, Rect Image, double Scale)>();
        foreach (var s in steps)
        {
            var caption = Text(Caption(s), 19, typeface, ink, Width - 48);
            double scale = Math.Min(1, Width / s.Shot.PixelWidth);
            double top = y + Math.Max(caption.Height, 32) + 12;
            var rect = new Rect(Margin, top, s.Shot.PixelWidth * scale, s.Shot.PixelHeight * scale);
            layout.Add((caption, rect, scale));
            y = rect.Bottom + Gap;
        }
        double height = y - Gap + Margin;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, Width + 2 * Margin, height));
            dc.DrawText(title, new Point(Margin, Margin));
            dc.DrawText(sub, new Point(Margin, Margin + title.Height + 4));
            for (int i = 0; i < steps.Count; i++)
            {
                var (caption, rect, scale) = layout[i];
                double captionTop = rect.Top - 12 - Math.Max(caption.Height, 32);
                // number badge, then the sentence
                var badge = new Point(Margin + 16, captionTop + 16);
                dc.DrawEllipse(accentBrush, null, badge, 16, 16);
                var number = Text((i + 1).ToString(), 16, typeface, Brushes.White);
                dc.DrawText(number, new Point(badge.X - number.Width / 2, badge.Y - number.Height / 2));
                dc.DrawText(caption, new Point(Margin + 44, captionTop + 16 - caption.Height / 2));

                dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(0xD0, 0xD7, 0xDE)), 1), new Rect(rect.X - 0.5, rect.Y - 0.5, rect.Width + 1, rect.Height + 1));
                dc.DrawImage(steps[i].Shot, rect);
                // where the click was: a ring with a soft halo
                var at = new Point(rect.X + steps[i].At.X * scale, rect.Y + steps[i].At.Y * scale);
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x40, accent.R, accent.G, accent.B)), null, at, 26, 26);
                dc.DrawEllipse(null, new Pen(accentBrush, 3.5), at, 17, 17);
                // and a pointer in the middle, its tip on the spot, as if someone is clicking it
                dc.PushTransform(new TranslateTransform(at.X, at.Y));
                dc.DrawGeometry(Brushes.White, new Pen(Brushes.Black, 1.4) { LineJoin = PenLineJoin.Round }, PointerShape);
                dc.Pop();
            }
        }
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(Width + 2 * Margin), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }
}
