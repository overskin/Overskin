using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using static Overskin.NativeMethods;

namespace Overskin;

public enum OverlayMode
{
    /// <summary>Drag a rectangle, or click to take the window under the cursor.</summary>
    Region,
    /// <summary>Click a window.</summary>
    Window,
    /// <summary>Click a pixel to read its colour.</summary>
    Colour,
    /// <summary>Drag to measure; stays open until Esc.</summary>
    Ruler,
}

/// <summary>
/// Full-screen layer over a frozen copy of every monitor. Everything works in physical pixels
/// read from the cursor, and the frozen copy is what gets captured, so what you see is exactly
/// what you get, even for menus and tooltips that would vanish on a click.
/// </summary>
public sealed class CaptureOverlay : Window
{
    private const int MagnifierPixels = 15; // odd, so there is a centre pixel
    private const double MagnifierCell = 8;

    private readonly OverlayMode _mode;
    private readonly BitmapSource _frozen;
    private readonly byte[] _pixels;
    private readonly RECT _virtual;
    private readonly List<(RECT Bounds, IntPtr Handle)> _windows;
    private double _scale = 1;

    private readonly Canvas _canvas = new();
    private readonly Image _backdrop = new();
    private readonly Path _dim = new() { Fill = new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)), IsHitTestVisible = false };
    private readonly Rectangle _frame = new() { StrokeThickness = 1, IsHitTestVisible = false, SnapsToDevicePixels = true };
    private readonly Line _ruler = new() { StrokeThickness = 1.5, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly Border _label = new();
    private readonly TextBlock _labelText = new();
    private readonly Border _magnifier = new();
    private readonly Rectangle _zoom = new();
    private readonly ImageBrush _zoomBrush;
    private readonly Border _swatch = new() { Width = 12, Height = 12, Margin = new Thickness(0, 0, 7, 0) };
    private readonly TextBlock _zoomText = new();
    private readonly Border _hint = new();

    // the capture toolbar (screenshots only) and the marks drawn with it, in physical pixels
    private readonly CaptureToolbar? _toolbar;
    private readonly InkLayer _ink;
    private readonly List<CaptureMark> _marks = new();
    private CaptureMark? _drawing;
    private bool _pointerWanted;

    private POINT _start;
    private bool _dragging;
    private RECT _current;
    private bool _hasCurrent;

    /// <summary>The chosen rectangle in physical screen pixels (Region and Window modes).</summary>
    public Int32Rect? Selection { get; private set; }

    /// <summary>The clicked colour (Colour mode).</summary>
    public Color? PickedColour { get; private set; }

    /// <summary>Whether crops include the mouse pointer (the toolbar can switch it).</summary>
    public bool IncludePointer => _toolbar?.Pointer ?? _pointerWanted;

    /// <summary>True when the toolbar was shown, so its choices are worth remembering.</summary>
    public bool HasToolbar => _toolbar != null;

    private CaptureOverlay(OverlayMode mode, string hint, BitmapSource frozen, RECT virtualScreen, bool tools, bool pointer)
    {
        _pointerWanted = pointer;
        _mode = mode;
        _frozen = frozen;
        _virtual = virtualScreen;
        _pixels = new byte[frozen.PixelWidth * frozen.PixelHeight * 4];
        frozen.CopyPixels(_pixels, frozen.PixelWidth * 4, 0);
        _windows = mode is OverlayMode.Region or OverlayMode.Window ? VisibleWindows() : new();

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;
        Left = virtualScreen.Left;
        Top = virtualScreen.Top;
        Width = 200;
        Height = 200;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        var res = Application.Current.Resources;
        var accent = (Brush)res["Accent"];

        _backdrop.Source = frozen;
        RenderOptions.SetBitmapScalingMode(_backdrop, BitmapScalingMode.NearestNeighbor);
        _frame.Stroke = accent;
        _ruler.Stroke = accent;

        _labelText.Foreground = Brushes.White;
        _labelText.FontFamily = (FontFamily)res["UiFont"];
        _labelText.FontSize = 11.5;
        _label.Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x10, 0x10, 0x12));
        _label.CornerRadius = (CornerRadius)res["RadiusSmall"];
        _label.Padding = new Thickness(7, 3, 7, 4);
        _label.Child = _labelText;
        _label.IsHitTestVisible = false;
        _label.Visibility = Visibility.Collapsed;

        // magnifier: an image brush looking at a few pixels around the cursor, drawn blocky
        _zoomBrush = new ImageBrush(frozen) { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
        _zoom.Width = _zoom.Height = MagnifierPixels * MagnifierCell;
        _zoom.Fill = _zoomBrush;
        RenderOptions.SetBitmapScalingMode(_zoom, BitmapScalingMode.NearestNeighbor);
        var centre = new Rectangle
        {
            Width = MagnifierCell + 2, Height = MagnifierCell + 2, Stroke = Brushes.White, StrokeThickness = 1,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var cross = new Grid();
        cross.Children.Add(_zoom);
        cross.Children.Add(centre);
        _zoomText.Foreground = Brushes.White;
        _zoomText.FontFamily = (FontFamily)res["UiFont"];
        _zoomText.FontSize = 11;
        _swatch.BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
        _swatch.BorderThickness = new Thickness(1);
        var readout = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 6, 2, 0) };
        readout.Children.Add(_swatch);
        readout.Children.Add(_zoomText);
        var magStack = new StackPanel();
        magStack.Children.Add(new Border { Child = cross, ClipToBounds = true });
        magStack.Children.Add(readout);
        _magnifier.Child = magStack;
        _magnifier.Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x10, 0x10, 0x12));
        _magnifier.BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF));
        _magnifier.BorderThickness = new Thickness(1);
        _magnifier.CornerRadius = (CornerRadius)res["RadiusSmall"];
        _magnifier.Padding = new Thickness(5);
        _magnifier.IsHitTestVisible = false;

        var hintText = new TextBlock
        {
            Text = hint, Foreground = (Brush)res["Text"], FontFamily = (FontFamily)res["UiFont"], FontSize = 12.5,
        };
        _hint.Child = hintText;
        _hint.Background = (Brush)res["Panel"];
        _hint.BorderBrush = (Brush)res["LineStrong"];
        _hint.BorderThickness = new Thickness(1);
        _hint.CornerRadius = (CornerRadius)res["RadiusControl"];
        _hint.Padding = new Thickness(14, 8, 14, 9);
        _hint.IsHitTestVisible = false;

        _ink = new InkLayer(this) { IsHitTestVisible = false };

        _canvas.Children.Add(_backdrop);
        _canvas.Children.Add(_ink);
        if (mode != OverlayMode.Colour) _canvas.Children.Add(_dim);
        _canvas.Children.Add(_frame);
        _canvas.Children.Add(_ruler);
        _canvas.Children.Add(_label);
        _canvas.Children.Add(_hint);
        _canvas.Children.Add(_magnifier);

        if (tools && mode is OverlayMode.Region or OverlayMode.Window)
        {
            _toolbar = new CaptureToolbar(mode == OverlayMode.Window ? CapturePick.Window : CapturePick.Region, pointer);
            _toolbar.Changed += () => { Update(CursorPos()); Focus(); };
            _toolbar.UndoRequested += Undo;
            _toolbar.CloseRequested += Close;
            _canvas.Children.Add(_toolbar);
        }
        Content = _canvas;
    }

#if DEBUG
    /// <summary>For the self-test: an overlay over a made-up "screen", never shown.</summary>
    internal static CaptureOverlay ForTest(BitmapSource frozen, RECT screen) => new(OverlayMode.Region, "", frozen, screen, tools: true, pointer: false);

    internal void AddMarkForTest(CaptureTool tool, Color colour, params Point[] points)
    {
        var mark = new CaptureMark(tool, colour, points[0]);
        foreach (var p in points.Skip(1)) mark.Add(p);
        _marks.Add(mark);
    }

    internal CaptureToolbar? ToolbarForTest => _toolbar;
#endif

    private CapturePick Pick => _toolbar?.Pick ?? (_mode == OverlayMode.Window ? CapturePick.Window : CapturePick.Region);
    private CaptureTool Tool => _toolbar?.Tool ?? CaptureTool.None;

    private void Undo()
    {
        if (_marks.Count == 0) return;
        _marks.RemoveAt(_marks.Count - 1);
        _toolbar?.SetCanUndo(_marks.Count > 0);
        _ink.InvalidateVisual();
    }

    /// <summary>Draws the hand-made marks in physical screen pixels.</summary>
    private void DrawMarks(DrawingContext dc)
    {
        foreach (var m in _marks) m.Draw(dc, _scale);
        _drawing?.Draw(dc, _scale);
    }

    /// <summary>Shows the marks over the frozen screen, at the overlay's scale.</summary>
    private sealed class InkLayer : FrameworkElement
    {
        private readonly CaptureOverlay _owner;
        public InkLayer(CaptureOverlay owner) => _owner = owner;

        protected override void OnRender(DrawingContext dc)
        {
            dc.PushTransform(new ScaleTransform(1 / _owner._scale, 1 / _owner._scale));
            dc.PushTransform(new TranslateTransform(-_owner._virtual.Left, -_owner._virtual.Top));
            _owner.DrawMarks(dc);
            dc.Pop();
            dc.Pop();
        }
    }

    // the mouse pointer at the moment of freezing: a small piece of screen with the pointer drawn in
    private BitmapSource? _pointerPatch;
    private Int32Rect _pointerRect;

    /// <summary>
    /// Freezes the screen and opens the overlay. Hide your own windows before calling.
    /// With <paramref name="pointer"/>, the mouse pointer as it was right now is put back into crops.
    /// </summary>
    public static CaptureOverlay? Open(OverlayMode mode, string hint, bool pointer = false, bool tools = false)
    {
        var vs = VirtualScreen();
        var cursor = CursorPos();
        var frozen = CaptureScreen(vs.Left, vs.Top, vs.Width, vs.Height);
        if (frozen == null) return null;
        var overlay = new CaptureOverlay(mode, hint, frozen, vs, tools, pointer);
        if (pointer || tools)
        {
            // the pointer stays out of the frozen picture (you'd see a stuck copy while choosing);
            // instead a 96 px patch around it is grabbed with the pointer drawn in
            int x = Math.Max(vs.Left, cursor.X - 48), y = Math.Max(vs.Top, cursor.Y - 48);
            int w = Math.Min(96, vs.Right - x), h = Math.Min(96, vs.Bottom - y);
            if (w > 0 && h > 0)
            {
                overlay._pointerPatch = CaptureScreen(x, y, w, h, cursor: true);
                overlay._pointerRect = new Int32Rect(x, y, w, h);
            }
        }
        return overlay;
    }

    /// <summary>Part of the frozen screen, in physical screen coordinates.</summary>
    public BitmapSource Crop(Int32Rect r)
    {
        var local = new Int32Rect(r.X - _virtual.Left, r.Y - _virtual.Top, r.Width, r.Height);
        BitmapSource cropped = ImageLoader.Normalize(new CroppedBitmap(_frozen, local));
        bool pointer = IncludePointer && _pointerPatch != null &&
                       _pointerRect.X < r.X + r.Width && _pointerRect.X + _pointerRect.Width > r.X &&
                       _pointerRect.Y < r.Y + r.Height && _pointerRect.Y + _pointerRect.Height > r.Y;
        if (!pointer && _marks.Count == 0) return cropped;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(cropped, new Rect(0, 0, r.Width, r.Height));
            dc.PushTransform(new TranslateTransform(-r.X, -r.Y));
            foreach (var m in _marks) m.Draw(dc, _scale);
            dc.Pop();
            if (pointer)
                dc.DrawImage(_pointerPatch, new Rect(_pointerRect.X - r.X, _pointerRect.Y - r.Y, _pointerRect.Width, _pointerRect.Height));
        }
        var rtb = new RenderTargetBitmap(r.Width, r.Height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        return ImageLoader.Normalize(rtb);
    }

    // ---- setup ---------------------------------------------------------------------------

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
        Update(CursorPos());
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Layout();
    }

    private void Cover()
    {
        Placement.MoveTo(new WindowInteropHelper(this).Handle, new Int32Rect(_virtual.Left, _virtual.Top, _virtual.Width, _virtual.Height), true);
        Layout();
    }

    /// <summary>The whole window is drawn at one scale, so physical pixels map to it uniformly.</summary>
    private void Layout()
    {
        _scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        _backdrop.Width = _virtual.Width / _scale;
        _backdrop.Height = _virtual.Height / _scale;
        _magnifier.LayoutTransform = new ScaleTransform(1 / _scale, 1 / _scale); // same physical size on every monitor
        _ink.Width = _backdrop.Width;
        _ink.Height = _backdrop.Height;
        PlaceHint();
    }

    private Point Local(double x, double y) => new((x - _virtual.Left) / _scale, (y - _virtual.Top) / _scale);

    private void PlaceHint()
    {
        var cursor = CursorPos();
        var mon = MonitorBounds(cursor);
        _hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var top = Local(mon.Left + mon.Width / 2.0, mon.Top);
        double y = top.Y + 18;
        if (_toolbar != null)
        {
            _toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(_toolbar, top.X - _toolbar.DesiredSize.Width / 2);
            Canvas.SetTop(_toolbar, y);
            y += _toolbar.DesiredSize.Height + 10;
        }
        Canvas.SetLeft(_hint, top.X - _hint.DesiredSize.Width / 2);
        Canvas.SetTop(_hint, y);
    }

    private static List<(RECT, IntPtr)> VisibleWindows()
    {
        var list = new List<(RECT, IntPtr)>();
        uint own = (uint)Environment.ProcessId;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h) || IsCloaked(h)) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == own) return true;
            if (ClassName(h) is "Progman" or "WorkerW") return true;
            var r = VisibleBounds(h);
            if (r.Width >= 24 && r.Height >= 24) list.Add((r, h));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    // ---- interaction ---------------------------------------------------------------------

    private RECT ClipToScreen(RECT r) => new()
    {
        Left = Math.Max(r.Left, _virtual.Left),
        Top = Math.Max(r.Top, _virtual.Top),
        Right = Math.Min(r.Right, _virtual.Right),
        Bottom = Math.Min(r.Bottom, _virtual.Bottom),
    };

    /// <summary>The window under the cursor, or failing that the monitor.</summary>
    private RECT Hovered(POINT p) => ClipToScreen(HoveredWindow(p)?.Bounds ?? MonitorBounds(p));

    private (RECT Bounds, IntPtr Handle)? HoveredWindow(POINT p)
    {
        foreach (var w in _windows)
            if (w.Bounds.Contains(p)) return w;
        return null;
    }

    /// <summary>The window that was clicked to choose it, when a window was chosen that way.</summary>
    public IntPtr SelectedWindow { get; private set; }

    /// <summary>True when something was drawn on the frozen screen.</summary>
    public bool HasMarks => _marks.Count > 0;

    /// <summary>A fixed-size box centred on the pointer, kept on the screen.</summary>
    private RECT FixedAt(POINT p, Size size)
    {
        var mon = MonitorBounds(p);
        int w = (int)Math.Min(size.Width, mon.Width), h = (int)Math.Min(size.Height, mon.Height);
        int x = Math.Clamp(p.X - w / 2, mon.Left, mon.Right - w), y = Math.Clamp(p.Y - h / 2, mon.Top, mon.Bottom - h);
        return new RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
    }

    /// <summary>A dragged box, held to the chosen shape.</summary>
    private RECT Shaped(POINT a, POINT b)
    {
        double ratio = _toolbar?.Ratio ?? 0;
        if (ratio <= 0) return Span(a, b);
        int dx = b.X - a.X, dy = b.Y - a.Y;
        double w = Math.Abs(dx), h = Math.Abs(dy);
        if (w / Math.Max(1, h) > ratio) h = w / ratio; else w = h * ratio;
        var end = new POINT(a.X + (int)Math.Round(w) * (dx < 0 ? -1 : 1), a.Y + (int)Math.Round(h) * (dy < 0 ? -1 : 1));
        return Span(a, end);
    }

    private static RECT Span(POINT a, POINT b) => new()
    {
        Left = Math.Min(a.X, b.X),
        Top = Math.Min(a.Y, b.Y),
        Right = Math.Max(a.X, b.X) + 1,
        Bottom = Math.Max(a.Y, b.Y) + 1,
    };

    private Color PixelAt(POINT p)
    {
        int x = Math.Clamp(p.X - _virtual.Left, 0, _frozen.PixelWidth - 1);
        int y = Math.Clamp(p.Y - _virtual.Top, 0, _frozen.PixelHeight - 1);
        int i = (y * _frozen.PixelWidth + x) * 4;
        return Color.FromRgb(_pixels[i + 2], _pixels[i + 1], _pixels[i]);
    }

    /// <summary>What a click at <paramref name="p"/> would capture with the current pick.</summary>
    private RECT Target(POINT p) => Pick switch
    {
        CapturePick.Screen => ClipToScreen(MonitorBounds(p)),
        CapturePick.AllScreens => _virtual,
        CapturePick.Region when _toolbar?.FixedSize is { } size => FixedAt(p, size),
        _ => Hovered(p),
    };

    private void HideSelection()
    {
        _hasCurrent = false;
        _frame.Width = _frame.Height = 0;
        _dim.Data = null;
        _label.Visibility = Visibility.Collapsed;
    }

    private void Update(POINT p)
    {
        // what is selected right now
        if (Tool != CaptureTool.None)
        {
            HideSelection();
            if (_drawing != null)
            {
                _drawing.Add(new Point(p.X, p.Y));
                _ink.InvalidateVisual();
            }
        }
        else switch (_mode)
        {
            case OverlayMode.Region when _dragging:
                ShowRect(Shaped(_start, p), solid: true);
                break;
            case OverlayMode.Region:
            case OverlayMode.Window:
                ShowRect(Target(p), solid: _toolbar?.FixedSize != null && Pick == CapturePick.Region);
                break;
            case OverlayMode.Ruler when _dragging:
                ShowRuler(_start, p);
                break;
        }
        bool magnify = Tool == CaptureTool.None && (_toolbar?.Magnifier ?? true) && !(_toolbar?.IsMouseOver ?? false);
        _magnifier.Visibility = magnify ? Visibility.Visible : Visibility.Collapsed;
        Cursor = Tool != CaptureTool.None ? Cursors.Pen : Cursors.Cross;

        // magnifier and readout
        int half = MagnifierPixels / 2;
        _zoomBrush.Viewbox = new Rect(p.X - _virtual.Left - half, p.Y - _virtual.Top - half, MagnifierPixels, MagnifierPixels);
        var colour = PixelAt(p);
        _swatch.Background = new SolidColorBrush(colour);
        _zoomText.Text = $"{Theming.ToHex(colour)}   {p.X}, {p.Y}";

        _magnifier.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var at = Local(p.X, p.Y);
        double mw = _magnifier.DesiredSize.Width, mh = _magnifier.DesiredSize.Height;
        double mx = at.X + 18, my = at.Y + 18;
        if (mx + mw > _backdrop.Width - 4) mx = at.X - 18 - mw;
        if (my + mh > _backdrop.Height - 4) my = at.Y - 18 - mh;
        Canvas.SetLeft(_magnifier, mx);
        Canvas.SetTop(_magnifier, my);
    }

    private void ShowRect(RECT r, bool solid)
    {
        _current = r;
        _hasCurrent = r.Width > 0 && r.Height > 0;
        var a = Local(r.Left, r.Top);
        var b = Local(r.Right, r.Bottom);
        var rect = new Rect(a, b);

        Canvas.SetLeft(_frame, rect.X);
        Canvas.SetTop(_frame, rect.Y);
        _frame.Width = rect.Width;
        _frame.Height = rect.Height;
        _frame.StrokeDashArray = solid ? null : new DoubleCollection { 4, 3 };

        var full = new RectangleGeometry(new Rect(0, 0, _backdrop.Width, _backdrop.Height));
        _dim.Data = new GeometryGroup { FillRule = FillRule.EvenOdd, Children = { full, new RectangleGeometry(rect) } };

        _labelText.Text = $"{r.Width} × {r.Height}";
        _label.Visibility = Visibility.Visible;
        _label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double ly = rect.Y - _label.DesiredSize.Height - 6;
        Canvas.SetLeft(_label, rect.X);
        Canvas.SetTop(_label, ly < 4 ? rect.Y + 6 : ly);
    }

    private void ShowRuler(POINT a, POINT b)
    {
        var la = Local(a.X + 0.5, a.Y + 0.5);
        var lb = Local(b.X + 0.5, b.Y + 0.5);
        _ruler.X1 = la.X; _ruler.Y1 = la.Y; _ruler.X2 = lb.X; _ruler.Y2 = lb.Y;
        _ruler.Visibility = Visibility.Visible;

        var r = Span(a, b);
        var ra = Local(r.Left, r.Top);
        var rb = Local(r.Right, r.Bottom);
        Canvas.SetLeft(_frame, ra.X);
        Canvas.SetTop(_frame, ra.Y);
        _frame.Width = rb.X - ra.X;
        _frame.Height = rb.Y - ra.Y;
        _frame.StrokeDashArray = new DoubleCollection { 4, 3 };
        _dim.Data = new RectangleGeometry(new Rect(0, 0, _backdrop.Width, _backdrop.Height));

        int dx = b.X - a.X, dy = b.Y - a.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        double angle = Math.Atan2(-dy, dx) * 180 / Math.PI;
        _labelText.Text = $"{Math.Abs(dx) + 1} × {Math.Abs(dy) + 1} px   ·   {length:0.#} px   ·   {angle:0.#}°";
        _label.Visibility = Visibility.Visible;
        Canvas.SetLeft(_label, lb.X + 14);
        Canvas.SetTop(_label, lb.Y - 30);
    }

    protected override void OnMouseMove(MouseEventArgs e) => Update(CursorPos());

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_toolbar?.IsMouseOver == true) return;
        _start = CursorPos();
        if (Tool != CaptureTool.None)
        {
            _drawing = new CaptureMark(Tool, _toolbar!.Colour, new Point(_start.X, _start.Y));
            CaptureMouse();
            _hint.Visibility = Visibility.Collapsed;
            return;
        }
        if (_mode is OverlayMode.Ruler || (_mode == OverlayMode.Region && Pick == CapturePick.Region && _toolbar?.FixedSize == null))
        {
            _dragging = true;
            CaptureMouse();
        }
        _hint.Visibility = Visibility.Collapsed;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drawing != null)
        {
            ReleaseMouseCapture();
            if (_drawing.IsUseful) _marks.Add(_drawing);
            _drawing = null;
            _toolbar?.SetCanUndo(_marks.Count > 0);
            _ink.InvalidateVisual();
            return;
        }
        if (e != null && _toolbar?.IsMouseOver == true && !_dragging) return;
        var end = CursorPos();
        bool moved = Math.Abs(end.X - _start.X) > 3 || Math.Abs(end.Y - _start.Y) > 3;
        if (_dragging)
        {
            _dragging = false;
            ReleaseMouseCapture();
        }

        switch (_mode)
        {
            case OverlayMode.Region:
                bool box = moved && Pick == CapturePick.Region && _toolbar?.FixedSize == null;
                var r = box ? Shaped(_start, end) : Target(end);
                if (!box && Pick is CapturePick.Region or CapturePick.Window && _toolbar?.FixedSize == null)
                    SelectedWindow = HoveredWindow(end)?.Handle ?? IntPtr.Zero;
                r = ClipToScreen(r);
                if (r.Width >= 2 && r.Height >= 2) Selection = new Int32Rect(r.Left, r.Top, r.Width, r.Height);
                Close();
                break;
            case OverlayMode.Window:
                if (_hasCurrent) Selection = new Int32Rect(_current.Left, _current.Top, _current.Width, _current.Height);
                if (Pick == CapturePick.Window) SelectedWindow = HoveredWindow(end)?.Handle ?? IntPtr.Zero;
                Close();
                break;
            case OverlayMode.Colour:
                PickedColour = PixelAt(end);
                Close();
                break;
            case OverlayMode.Ruler:
                break; // measurements stay up until Esc
        }
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        // with a drawing tool picked, right-click puts the pointer back to choosing instead of quitting
        if (Tool != CaptureTool.None)
        {
            _toolbar!.SetTool(CaptureTool.None);
            return;
        }
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            return;
        }

        if (_toolbar != null && !_dragging && _drawing == null)
        {
            bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            switch (e.Key)
            {
                case Key.D1 or Key.NumPad1: _toolbar.SetPick(CapturePick.Region); e.Handled = true; return;
                case Key.D2 or Key.NumPad2: _toolbar.SetPick(CapturePick.Window); e.Handled = true; return;
                case Key.D3 or Key.NumPad3: _toolbar.SetPick(CapturePick.Screen); e.Handled = true; return;
                case Key.D4 or Key.NumPad4: _toolbar.SetPick(CapturePick.AllScreens); e.Handled = true; return;
                case Key.P: _toolbar.SetTool(Tool == CaptureTool.Pen ? CaptureTool.None : CaptureTool.Pen); e.Handled = true; return;
                case Key.A: _toolbar.SetTool(Tool == CaptureTool.Arrow ? CaptureTool.None : CaptureTool.Arrow); e.Handled = true; return;
                case Key.B: _toolbar.SetTool(Tool == CaptureTool.Box ? CaptureTool.None : CaptureTool.Box); e.Handled = true; return;
                case Key.H: _toolbar.SetTool(Tool == CaptureTool.Highlighter ? CaptureTool.None : CaptureTool.Highlighter); e.Handled = true; return;
                case Key.C: _toolbar.NextColour(); e.Handled = true; return;
                case Key.M: _toolbar.ToggleMagnifier(); e.Handled = true; return;
                case Key.Z when ctrl: Undo(); e.Handled = true; return;
            }
        }

        // pixel-exact aiming: arrows move the pointer one pixel (Shift: ten), even mid-drag
        int dx = e.Key == Key.Left ? -1 : e.Key == Key.Right ? 1 : 0;
        int dy = e.Key == Key.Up ? -1 : e.Key == Key.Down ? 1 : 0;
        if (dx != 0 || dy != 0)
        {
            int step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
            var p = CursorPos();
            int x = Math.Clamp(p.X + dx * step, _virtual.Left, _virtual.Right - 1);
            int y = Math.Clamp(p.Y + dy * step, _virtual.Top, _virtual.Bottom - 1);
            SetCursorPos(x, y);
            Update(CursorPos());
            e.Handled = true;
            return;
        }

        // Enter acts like a click where the pointer is (window under it, colour under it)
        if (e.Key == Key.Enter && !_dragging && _mode is OverlayMode.Region or OverlayMode.Window or OverlayMode.Colour)
        {
            _start = CursorPos();
            OnMouseLeftButtonUp(null!);
            e.Handled = true;
        }
    }
}

/// <summary>One mark drawn on the frozen screen: points in physical screen pixels.</summary>
public sealed class CaptureMark
{
    private readonly List<Point> _points = new();

    public CaptureTool Tool { get; }
    public Color Colour { get; }

    public CaptureMark(CaptureTool tool, Color colour, Point start)
    {
        Tool = tool;
        Colour = colour;
        _points.Add(start);
    }

    public void Add(Point p)
    {
        if (Tool is CaptureTool.Arrow or CaptureTool.Box)
        {
            if (_points.Count == 1) _points.Add(p);
            else _points[1] = p;
        }
        else if ((p - _points[^1]).Length >= 1)
        {
            _points.Add(p);
        }
    }

    public bool IsUseful => _points.Count > 1 && (_points[^1] - _points[0]).Length + _points.Count > 3;

    /// <summary><paramref name="scale"/> is physical pixels per DIP, so marks look the same size on any screen.</summary>
    public void Draw(DrawingContext dc, double scale)
    {
        if (_points.Count < 2) return;
        var brush = new SolidColorBrush(Colour);
        switch (Tool)
        {
            case CaptureTool.Pen:
                dc.DrawGeometry(null, new Pen(brush, 4 * scale) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, Polyline());
                break;
            case CaptureTool.Highlighter:
                var marker = new SolidColorBrush(Color.FromArgb(0x70, Colour.R, Colour.G, Colour.B));
                dc.DrawGeometry(null, new Pen(marker, 18 * scale) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat, LineJoin = PenLineJoin.Round }, Polyline());
                break;
            case CaptureTool.Box:
                dc.DrawRectangle(null, new Pen(brush, 3 * scale) { LineJoin = PenLineJoin.Miter }, new Rect(_points[0], _points[1]));
                break;
            case CaptureTool.Arrow:
                var a = _points[0];
                var b = _points[1];
                var dir = b - a;
                if (dir.Length < 1) return;
                dir.Normalize();
                double head = 16 * scale;
                var side = new Vector(-dir.Y, dir.X);
                var tipBase = b - dir * head;
                dc.DrawLine(new Pen(brush, 4 * scale) { StartLineCap = PenLineCap.Round }, a, b - dir * head * 0.6);
                var tip = new StreamGeometry();
                using (var g = tip.Open())
                {
                    g.BeginFigure(b, true, true);
                    g.LineTo(tipBase + side * head * 0.55, false, false);
                    g.LineTo(tipBase - side * head * 0.55, false, false);
                }
                dc.DrawGeometry(brush, null, tip);
                break;
        }
    }

    private Geometry Polyline()
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(_points[0], false, false);
            g.PolyLineTo(_points.Skip(1).ToList(), true, true);
        }
        return geometry;
    }
}
