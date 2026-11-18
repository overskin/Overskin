using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using static Overskin.NativeMethods;

namespace Overskin;

/// <summary>Helpers shared by the two placement windows.</summary>
internal static class Placement
{
    /// <summary>Largest rectangle with the image's aspect ratio that fits inside <paramref name="box"/>, centred.</summary>
    public static Int32Rect Fit(Int32Rect box, int imageWidth, int imageHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0 || box.Width <= 0 || box.Height <= 0) return box;
        double k = Math.Min((double)box.Width / imageWidth, (double)box.Height / imageHeight);
        int w = Math.Max(1, (int)Math.Round(imageWidth * k)), h = Math.Max(1, (int)Math.Round(imageHeight * k));
        return new Int32Rect(box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h);
    }

    /// <summary>
    /// Positions a WPF window in physical pixels. Done twice because moving onto a monitor with a
    /// different scale makes WPF resize the window once in response.
    /// </summary>
    public static void MoveTo(IntPtr hWnd, Int32Rect r, bool topmost)
    {
        IntPtr after = topmost ? HWND_TOPMOST : IntPtr.Zero;
        uint flags = SWP_NOACTIVATE | (topmost ? 0 : SWP_NOZORDER);
        SetWindowPos(hWnd, after, r.X, r.Y, r.Width, r.Height, flags);
        SetWindowPos(hWnd, after, r.X, r.Y, r.Width, r.Height, flags);
    }
}

/// <summary>
/// Full-screen dimmed layer across every monitor. The user drags a rectangle; the result is in
/// physical screen pixels, read straight from GetCursorPos so DPI scaling can't skew it.
/// </summary>
public sealed class AreaSelectWindow : Window
{
    private readonly Canvas _canvas = new();
    private readonly Rectangle _box;
    private readonly Image _preview;
    private readonly Border _hint;
    private readonly TextBlock _hintText;
    private readonly System.Windows.Media.Imaging.BitmapSource? _trace;
    private POINT _start;
    private bool _dragging;

    public Int32Rect? Selection { get; private set; }

    public AreaSelectWindow(System.Windows.Media.Imaging.BitmapSource? trace, string hint = "Drag a rectangle where the drawing should go.  Esc cancels.")
    {
        _trace = trace;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(0x59, 0, 0, 0));
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Cursor = Cursors.Cross;
        Left = 0;
        Top = 0;
        Width = 200;
        Height = 200;

        var accent = (Brush)Application.Current.Resources["Accent"];
        _box = new Rectangle
        {
            Stroke = accent,
            StrokeThickness = 1,
            Fill = new SolidColorBrush(accent is SolidColorBrush a ? Color.FromArgb(0x1E, a.Color.R, a.Color.G, a.Color.B) : Color.FromArgb(0x1E, 255, 255, 255)),
            Visibility = Visibility.Collapsed,
            SnapsToDevicePixels = true,
        };
        // the original picture, faint: instant to draw, unlike the traced strokes
        _preview = new Image { Source = trace, Stretch = Stretch.Fill, Opacity = 0.55, Visibility = Visibility.Collapsed, IsHitTestVisible = false };

        _hintText = new TextBlock
        {
            Text = hint,
            Foreground = (Brush)Application.Current.Resources["Text"],
            FontFamily = (FontFamily)Application.Current.Resources["UiFont"],
            FontSize = 12.5,
        };
        _hint = new Border
        {
            Background = (Brush)Application.Current.Resources["Panel"],
            BorderBrush = (Brush)Application.Current.Resources["LineStrong"],
            BorderThickness = new Thickness(1),
            CornerRadius = (CornerRadius)Application.Current.Resources["RadiusControl"],
            Padding = new Thickness(13, 8, 13, 9),
            Child = _hintText,
            IsHitTestVisible = false,
        };

        _canvas.Children.Add(_box);
        _canvas.Children.Add(_preview);
        _canvas.Children.Add(_hint);
        Content = _canvas;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Cover();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        Cover();
        Activate();
        Focus();
        PlaceHint(CursorPos());
    }

    private void Cover()
    {
        var vs = VirtualScreen();
        Placement.MoveTo(new WindowInteropHelper(this).Handle, new Int32Rect(vs.Left, vs.Top, vs.Width, vs.Height), true);
    }

    private Point Local(POINT p) => PointFromScreen(new Point(p.X, p.Y));

    private void PlaceHint(POINT cursor)
    {
        _hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var p = Local(cursor);
        double x = Math.Max(8, Math.Min(p.X + 18, ActualWidth - _hint.DesiredSize.Width - 8));
        double y = Math.Max(8, Math.Min(p.Y + 22, ActualHeight - _hint.DesiredSize.Height - 8));
        Canvas.SetLeft(_hint, x);
        Canvas.SetTop(_hint, y);
    }

    private static Int32Rect RectOf(POINT a, POINT b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _start = CursorPos();
        _dragging = true;
        CaptureMouse();
        _box.Visibility = Visibility.Visible;
        _preview.Visibility = _trace != null ? Visibility.Visible : Visibility.Collapsed;
        UpdateBox(_start);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var now = CursorPos();
        if (_dragging) UpdateBox(now);
        PlaceHint(now);
    }

    private void UpdateBox(POINT now)
    {
        var r = RectOf(_start, now);
        var a = Local(new POINT(r.X, r.Y));
        var b = Local(new POINT(r.X + r.Width, r.Y + r.Height));
        Canvas.SetLeft(_box, a.X);
        Canvas.SetTop(_box, a.Y);
        _box.Width = Math.Max(0, b.X - a.X);
        _box.Height = Math.Max(0, b.Y - a.Y);

        if (_trace != null)
        {
            // show the drawing exactly where it will land inside the dragged box
            var fit = Placement.Fit(r, _trace.PixelWidth, _trace.PixelHeight);
            var fa = Local(new POINT(fit.X, fit.Y));
            var fb = Local(new POINT(fit.X + fit.Width, fit.Y + fit.Height));
            Canvas.SetLeft(_preview, fa.X);
            Canvas.SetTop(_preview, fa.Y);
            _preview.Width = Math.Max(1, fb.X - fa.X);
            _preview.Height = Math.Max(1, fb.Y - fa.Y);
        }

        _hintText.Text = $"{r.Width} × {r.Height} px";
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();

        var r = RectOf(_start, CursorPos());
        if (r.Width >= 12 && r.Height >= 12) Selection = r;
        Close();
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e) => Close();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }
}

/// <summary>
/// Faint outline of the traced paths at the chosen spot, shown before drawing.
/// Click-through and never focused, so it can sit over the drawing program.
/// </summary>
public sealed class GhostWindow : Window
{
    private readonly PathPreview _preview;
    private IntPtr _handle;
    private Int32Rect _rect;

    public GhostWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = 100;
        Height = 100;

        var accent = (Brush)Application.Current.Resources["Accent"];
        _preview = new PathPreview { Stroke = accent, Stretch = true, Opacity = 0.9 };

        var frame = new Rectangle
        {
            Stroke = accent,
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 4, 4 },
            Opacity = 0.5,
            SnapsToDevicePixels = true,
        };

        var grid = new Grid();
        grid.Children.Add(_preview);
        grid.Children.Add(frame);
        Content = grid;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _handle = new WindowInteropHelper(this).Handle;
        TrySetExStyle(_handle, GetExStyle(_handle) | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, out _);
    }

    /// <summary>Shows the paths inside a rectangle given in physical screen pixels.</summary>
    public void Present(TraceResult trace, Int32Rect rect)
    {
        _rect = rect;
        _preview.Set(trace, false);
        if (!IsVisible) Show();
        Placement.MoveTo(_handle, _rect, true);
    }
}
