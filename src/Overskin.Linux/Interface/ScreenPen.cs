using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Overskin.Linux.Platform;

namespace Overskin.Linux.Interface;

/// <summary>
/// Drawing on the screen while it records (as on Windows): a see-through sheet over the recorded
/// area that takes the pointer while the pen is on. Pen strokes, arrows, boxes and highlights show
/// in the recording and fade away a few seconds after they're drawn. While the pen is off, the
/// sheet lets every click through to what's under it.
/// </summary>
public sealed class ScreenPen : Window
{
    public enum PenTool { Pen, Arrow, Box, Highlight }

    public static readonly Color[] Inks =
    {
        Color.FromRgb(0xEF, 0x44, 0x44), Color.FromRgb(0xFA, 0xCC, 0x15), Color.FromRgb(0x22, 0xC5, 0x5E),
        Color.FromRgb(0x3B, 0x82, 0xF6), Color.FromRgb(0xFF, 0xFF, 0xFF),
    };

    private readonly Sheet _sheet;
    private readonly List<(Mark Mark, DateTime Made)> _marks = new();
    private readonly DispatcherTimer _fade = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private Mark? _drawing;
    private Point _start;

    public PenTool Tool { get; set; } = PenTool.Pen;
    public int InkIndex { get; set; }
    public Color Ink => Inks[InkIndex % Inks.Length];
    /// <summary>Seconds a mark stays before it fades.</summary>
    public double Linger { get; set; } = 3;
    /// <summary>Marks fade away on their own; when off they stay until the pen is turned off.</summary>
    public bool AutoFade { get; set; } = true;
    public bool On { get; private set; }
    public event Action? Changed;

    /// <param name="area">The recorded area, in picture pixels of the screen it's on.</param>
    public ScreenPen(PixelRect area)
    {
        Title = "Overskin pen";
        SystemDecorations = SystemDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        ShowActivated = false;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        WindowStartupLocation = WindowStartupLocation.Manual;
        Content = _sheet = new Sheet(this);
        if (Mac.IsMac)
        {
            // over exactly the recorded part of the display, in points
            var corner = Mac.FromPicture(new Point(area.X, area.Y));
            var size = new Mac.CGRect(corner.X, corner.Y, area.Width / Mac.Shot.Scale, area.Height / Mac.Shot.Scale);
            Width = size.Width;
            Height = size.Height;
            Opened += (_, _) =>
            {
                var window = Mac.NSWindowOf(TryGetPlatformHandle());
                Mac.Place(window, size);
                Mac.Float(window);
            };
        }
        else
        {
            double scale = Screens.Primary?.Scaling ?? 1;
            Width = area.Width / scale;
            Height = area.Height / scale;
            Position = new PixelPoint(area.X, area.Y);
        }
        Opened += (_, _) => SetThrough(true);
        _fade.Tick += (_, _) => Fade();
        _fade.Start();
        KeyDown += OnKey;
        Closed += (_, _) => _fade.Stop();
    }

    private void SetThrough(bool through)
    {
        var handle = Mac.IsMac ? Mac.NSWindowOf(TryGetPlatformHandle()) : TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        Input.SetClickThrough(handle, through);
    }

    /// <summary>Turns the pen on (the sheet takes the pointer) or off (clicks go through, and marks that don't fade go).</summary>
    public void Toggle()
    {
        On = !On;
        SetThrough(!On);
        _sheet.Cursor = new Cursor(On ? StandardCursorType.Cross : StandardCursorType.Arrow);
        if (On) Activate();
        else if (!AutoFade) { _marks.Clear(); _sheet.InvalidateVisual(); }
        Changed?.Invoke();
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: if (On) Toggle(); break;
            case Key.D1 or Key.P: Tool = PenTool.Pen; break;
            case Key.D2 or Key.A: Tool = PenTool.Arrow; break;
            case Key.D3 or Key.B: Tool = PenTool.Box; break;
            case Key.D4 or Key.H: Tool = PenTool.Highlight; break;
            case Key.C: InkIndex++; break;
            default: return;
        }
        e.Handled = true;
        Changed?.Invoke();
    }

    /// <summary>Marks fade over their last half second, then go.</summary>
    private void Fade()
    {
        if (!AutoFade || _marks.Count == 0) return;
        var now = DateTime.Now;
        int before = _marks.Count;
        _marks.RemoveAll(m => (now - m.Made).TotalSeconds > Linger + 0.5);
        if (before != _marks.Count || _marks.Any(m => (now - m.Made).TotalSeconds > Linger)) _sheet.InvalidateVisual();
    }

    private double FadeOf(DateTime made)
    {
        if (!AutoFade) return 1;
        double age = (DateTime.Now - made).TotalSeconds;
        return age <= Linger ? 1 : Math.Clamp(1 - (age - Linger) / 0.5, 0, 1);
    }

    private void Begin(Point p)
    {
        if (!On) return;
        _start = p;
        double width = Math.Max(3, App.Settings.PenWidth);
        _drawing = Tool switch
        {
            PenTool.Pen => new StrokeMark { Colour = Ink, Width = width, Points = { p } },
            PenTool.Highlight => new StrokeMark { Colour = Color.Parse("#FACC15"), Width = width, Highlighter = true, Points = { p } },
            PenTool.Arrow => new ShapeMark { Kind = Overskin.Linux.Interface.Tool.Arrow, From = p, To = p, Colour = Ink, Width = width },
            _ => new ShapeMark { Kind = Overskin.Linux.Interface.Tool.Rectangle, From = p, To = p, Colour = Ink, Width = width },
        };
        _sheet.InvalidateVisual();
    }

    private void Move(Point p)
    {
        switch (_drawing)
        {
            case StrokeMark s: s.Points.Add(p); break;
            case ShapeMark sh: sh.To = p; break;
            default: return;
        }
        _sheet.InvalidateVisual();
    }

    private void End()
    {
        if (_drawing == null) return;
        _marks.Add((_drawing, DateTime.Now));
        _drawing = null;
        _sheet.InvalidateVisual();
    }

    /// <summary>For the self-test: a stroke from one point to another, as the mouse would draw it.</summary>
    internal int DrawForTest(Point a, Point b)
    {
        if (!On) Toggle();
        Begin(a);
        Move(b);
        End();
        return _marks.Count;
    }

    internal int MarksForTest => _marks.Count;

    private sealed class Sheet : Control
    {
        private readonly ScreenPen _pen;
        public Sheet(ScreenPen pen) { _pen = pen; }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            _pen.Begin(e.GetPosition(this));
            e.Pointer.Capture(this);
        }

        protected override void OnPointerMoved(PointerEventArgs e) => _pen.Move(e.GetPosition(this));

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            e.Pointer.Capture(null);
            _pen.End();
        }

        public override void Render(DrawingContext ctx)
        {
            // nearly see-through while the pen is on, so the sheet takes the pointer everywhere
            ctx.FillRectangle(_pen.On ? new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) : Brushes.Transparent, new Rect(Bounds.Size));
            foreach (var (mark, made) in _pen._marks)
                using (ctx.PushOpacity(_pen.FadeOf(made)))
                    mark.Draw(ctx);
            _pen._drawing?.Draw(ctx);
        }
    }
}
