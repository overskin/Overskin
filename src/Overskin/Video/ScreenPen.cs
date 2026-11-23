using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Overskin;

/// <summary>
/// Drawing on the screen while it records: a see-through sheet over the recorded area that takes
/// the mouse while the pen is on. Pen strokes, arrows, boxes and highlights show in the recording
/// and fade away a few seconds after they're drawn. The recorded pointer is hidden while the pen
/// is on, and clicks made with it don't zoom or ring.
/// </summary>
internal sealed class ScreenPen : Window
{
    public enum Tool { Pen, Arrow, Box, Highlight }

    /// <summary>Whether the pen is on right now (read by the recorder's threads).</summary>
    public static volatile bool Drawing;

    public static readonly Color[] Colours =
    {
        Color.FromRgb(0xEF, 0x44, 0x44), Color.FromRgb(0xFA, 0xCC, 0x15), Color.FromRgb(0x22, 0xC5, 0x5E),
        Color.FromRgb(0x3B, 0x82, 0xF6), Color.FromRgb(0xFF, 0xFF, 0xFF),
    };

    private readonly Int32Rect _area;
    private readonly Canvas _sheet = new() { Background = Brushes.Transparent, ClipToBounds = true };
    private Shape? _current;
    private Point _start;

    public Tool Current { get; set; } = Tool.Pen;
    public int ColourIndex { get; set; }
    public Color Colour => Colours[ColourIndex % Colours.Length];
    /// <summary>Seconds a mark stays before it fades.</summary>
    public double Linger { get; set; } = 3;
    /// <summary>
    /// Marks fade away on their own after <see cref="Linger"/>. When off they stay until the pen
    /// is turned off, and then all go together.
    /// </summary>
    public bool AutoFade { get; set; } = true;
    public bool On { get; private set; }
    /// <summary>Raised when the pen is turned on or off and when a mark starts or ends, so the recording bar can stay on top.</summary>
    public event Action? Touched;

    public ScreenPen(Int32Rect area)
    {
        _area = area;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Left = -10000;
        Top = -10000;
        Width = 1;
        Height = 1;
        Content = _sheet;
        Cursor = Cursors.Pen;
        _sheet.MouseLeftButtonDown += Down;
        _sheet.MouseMove += Move;
        _sheet.MouseLeftButtonUp += Up;
        _sheet.LostMouseCapture += (_, _) => Finish();
        // a right-click puts the pen away, so the screen works normally again
        _sheet.MouseRightButtonUp += (_, e) => { if (On) { SetOn(false); e.Handled = true; } };
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            NativeMethods.TrySetExStyle(h, NativeMethods.GetExStyle(h) | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW, out _);
            Placement.MoveTo(h, _area, true);
            SetClickThrough(true);
        };
    }

    /// <summary>Turns the pen on or off. Off, the sheet lets every click through to what's under it.</summary>
    public void Toggle() => SetOn(!On);

    public void SetOn(bool on)
    {
        if (on && !IsVisible) Show();
        On = on;
        Drawing = on;
        // a near-invisible fill takes the mouse; fully clear lets it through
        _sheet.Background = on ? new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) : Brushes.Transparent;
        SetClickThrough(!on);
        if (!on)
        {
            Finish();
            if (!AutoFade) FadeAll();
        }
        HideWhenClear();
        Touched?.Invoke();
    }

    private void SetClickThrough(bool through)
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero) return;
        long ex = NativeMethods.GetExStyle(h);
        ex = through ? ex | NativeMethods.WS_EX_TRANSPARENT : ex & ~NativeMethods.WS_EX_TRANSPARENT;
        NativeMethods.TrySetExStyle(h, ex, out _);
    }

    protected override void OnClosed(EventArgs e)
    {
        Drawing = false;
        base.OnClosed(e);
    }

    private Brush Ink(double opacity = 1)
    {
        var b = new SolidColorBrush(Colour) { Opacity = opacity };
        b.Freeze();
        return b;
    }

    private void Down(object sender, MouseButtonEventArgs e)
    {
        if (!On) return;
        _start = e.GetPosition(_sheet);
        double thick = Math.Max(3, Math.Min(_sheet.ActualWidth, _sheet.ActualHeight) / 180);
        _current = Current switch
        {
            Tool.Box => new Rectangle { Stroke = Ink(), StrokeThickness = thick, RadiusX = thick, RadiusY = thick },
            Tool.Arrow => new Path { Fill = Ink(), Stroke = Ink(), StrokeThickness = thick, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round },
            Tool.Highlight => new Polyline { Stroke = Ink(0.38), StrokeThickness = thick * 6, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Flat, StrokeEndLineCap = PenLineCap.Flat },
            _ => new Polyline { Stroke = Ink(), StrokeThickness = thick, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round },
        };
        if (_current is Polyline line) line.Points.Add(_start);
        _sheet.Children.Add(_current);
        _sheet.CaptureMouse();
        Update(_start);
        e.Handled = true;
        Touched?.Invoke();
    }

    private void Move(object sender, MouseEventArgs e)
    {
        if (_current != null && e.LeftButton == MouseButtonState.Pressed) Update(e.GetPosition(_sheet));
    }

    private void Up(object sender, MouseButtonEventArgs e)
    {
        if (_current == null) return;
        Update(e.GetPosition(_sheet));
        _sheet.ReleaseMouseCapture();
        Touched?.Invoke();
    }

    private void Update(Point p)
    {
        switch (_current)
        {
            case Polyline line:
                if (line.Points.Count == 0 || (line.Points[^1] - p).Length >= 1.5) line.Points.Add(p);
                break;
            case Rectangle box:
                Canvas.SetLeft(box, Math.Min(_start.X, p.X));
                Canvas.SetTop(box, Math.Min(_start.Y, p.Y));
                box.Width = Math.Abs(p.X - _start.X);
                box.Height = Math.Abs(p.Y - _start.Y);
                break;
            case Path arrow:
                arrow.Data = ArrowGeometry(_start, p, arrow.StrokeThickness);
                break;
        }
    }

    internal static Geometry ArrowGeometry(Point from, Point to, double thick)
    {
        var v = to - from;
        double length = v.Length;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(from, false, false);
            c.LineTo(to, true, true);
            if (length > 4)
            {
                v /= length;
                double head = Math.Min(length * 0.5, thick * 5);
                var n = new Vector(-v.Y, v.X);
                var b = to - v * head;
                c.BeginFigure(to, true, true);
                c.LineTo(b + n * head * 0.55, true, true);
                c.LineTo(b - n * head * 0.55, true, true);
            }
        }
        g.Freeze();
        return g;
    }

    /// <summary>The mark just drawn stays a moment, then fades out and goes.</summary>
    private void Finish()
    {
        var done = _current;
        _current = null;
        if (done == null || !AutoFade) return;
        FadeOut(done, TimeSpan.FromSeconds(Linger));
    }

    private void FadeOut(UIElement mark, TimeSpan after)
    {
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(600)) { BeginTime = after };
        fade.Completed += (_, _) => { _sheet.Children.Remove(mark); HideWhenClear(); };
        mark.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Marks kept on screen (fading turned off) all go once drawing is turned off.</summary>
    private void FadeAll()
    {
        foreach (UIElement mark in _sheet.Children.Cast<UIElement>().ToList()) FadeOut(mark, TimeSpan.Zero);
    }

    /// <summary>Off and with nothing left fading: off the screen altogether.</summary>
    private void HideWhenClear()
    {
        if (!On && _sheet.Children.Count == 0 && IsVisible) Hide();
    }

    /// <summary>Removes every mark straight away.</summary>
    public void Clear()
    {
        _current = null;
        _sheet.Children.Clear();
    }

    /// <summary>How many marks are showing (tests).</summary>
    internal int Marks => _sheet.Children.Count;

    /// <summary>Draws a mark from code (tests).</summary>
    internal void DrawForTest(Point from, Point to)
    {
        _start = from;
        bool was = On;
        On = true;
        _current = new Polyline { Stroke = Ink(), StrokeThickness = 4 };
        if (Current == Tool.Arrow) _current = new Path { Fill = Ink(), Stroke = Ink(), StrokeThickness = 4 };
        if (Current == Tool.Box) _current = new Rectangle { Stroke = Ink(), StrokeThickness = 4 };
        if (_current is Polyline l) l.Points.Add(from);
        _sheet.Children.Add(_current);
        Update(new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2));
        Update(to);
        Finish();
        On = was;
    }
}
