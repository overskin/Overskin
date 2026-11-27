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
    /// <summary>The window's own content: the canvas (the whole virtual screen), shifted so this window's monitor shows.</summary>
    private readonly Canvas _host = new();
    /// <summary>The monitor this window covers: the one the pointer was on. Every other monitor gets a <see cref="Mirror"/>.</summary>
    private RECT _home;
    private readonly List<Mirror> _mirrors = new();
    private readonly Image _backdrop = new();
    private readonly Path _dim = new() { Fill = new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)), IsHitTestVisible = false };
    private readonly Rectangle _frame = new() { StrokeThickness = 1, IsHitTestVisible = false, SnapsToDevicePixels = true };
    private readonly Line _ruler = new() { StrokeThickness = 1.5, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly Border _label = new();
    private readonly TextBlock _labelText = new();
    private readonly Border _magnifier = new();
    private readonly Line _crossH = new() { Stroke = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly Line _crossV = new() { Stroke = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)), IsHitTestVisible = false, Visibility = Visibility.Collapsed };

    /// <summary>From the settings: whether the magnifier starts on, and whether crosshair lines follow the pointer.</summary>
    public static bool MagnifierOn = true, CrosshairOn;
    /// <summary>From the settings: a dragged box is fitted to what's inside it.</summary>
    public static bool TightenOn;

    /// <summary>Whether fitting the box was on when the overlay closed (it can be switched on the capture bar).</summary>
    public bool TightenShown => _toolbar?.Tighten ?? TightenOn;

    /// <summary>Whether the magnifier was on when the overlay closed (it can be switched on the capture bar).</summary>
    public bool MagnifierShown => _toolbar?.Magnifier ?? MagnifierOn;
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
        if (mode == OverlayMode.Region) FindImages();

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
        _canvas.Children.Add(_crossH);
        _canvas.Children.Add(_crossV);
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
            _toolbar.PickFromScreenRequested += BeginPickColour;
            _canvas.Children.Add(_toolbar);
        }
        _home = MonitorBounds(CursorPos());
        _host.Children.Add(_canvas);
        Content = _host;
        Closed += (_, _) =>
        {
            foreach (var m in _mirrors) m.Close();
            _mirrors.Clear();
        };
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
        OpenMirrors();
        Activate();
        Focus();
        Update(CursorPos());
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Layout();
    }

    /// <summary>
    /// Covers the pointer's monitor only. A single window stretched over monitors that refresh at
    /// different rates (360, 144 and 60 Hz, say) is shown at the slowest pace and stutters; one
    /// window per monitor keeps each one smooth.
    /// </summary>
    private void Cover()
    {
        var home = AllMonitors().Count > 1 ? _home : _virtual;
        if (home.Width <= 0 || home.Height <= 0) home = _virtual;
        Placement.MoveTo(new WindowInteropHelper(this).Handle, new Int32Rect(home.Left, home.Top, home.Width, home.Height), true);
        Layout();
    }

    /// <summary>Opens a mirror on every other monitor, showing its part of the same canvas.</summary>
    private void OpenMirrors()
    {
        if (_mirrors.Count > 0 || !IsVisible) return;
        var monitors = AllMonitors();
        if (monitors.Count < 2) return;
        foreach (var m in monitors)
        {
            if (m.Left == _home.Left && m.Top == _home.Top && m.Right == _home.Right && m.Bottom == _home.Bottom) continue;
            var mirror = new Mirror(this, m);
            _mirrors.Add(mirror);
            mirror.Show();
        }
    }

    /// <summary>
    /// Another monitor's view of the capture screen: a picture of its part of the owner's canvas,
    /// kept up to date by WPF. The mouse is handed to the owner, which does all the work.
    /// </summary>
    private sealed class Mirror : Window
    {
        private readonly CaptureOverlay _owner;
        private readonly RECT _monitor;

        public Mirror(CaptureOverlay owner, RECT monitor)
        {
            _owner = owner;
            _monitor = monitor;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Background = Brushes.Black;
            Cursor = Cursors.Cross;
            Left = monitor.Left;
            Top = monitor.Top;
            Width = 100;
            Height = 100;
            var a = owner.Local(monitor.Left, monitor.Top);
            var b = owner.Local(monitor.Right, monitor.Bottom);
            var brush = new VisualBrush(owner._canvas)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(a, b),
                Stretch = Stretch.Fill,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
            };
            RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.NearestNeighbor);
            Content = new Rectangle { Fill = brush };
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var handle = new WindowInteropHelper(this).Handle;
            TrySetExStyle(handle, GetExStyle(handle) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, out _);
            Placement.MoveTo(handle, new Int32Rect(_monitor.Left, _monitor.Top, _monitor.Width, _monitor.Height), true);
        }

        protected override void OnMouseMove(MouseEventArgs e) => _owner.OnMouseMove(e);
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) => _owner.OnMouseLeftButtonDown(e);
        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => _owner.OnMouseLeftButtonUp(e);
        protected override void OnMouseRightButtonDown(MouseButtonEventArgs e) => _owner.OnMouseRightButtonDown(e);
    }

    /// <summary>The whole window is drawn at one scale, so physical pixels map to it uniformly.</summary>
    private void Layout()
    {
        _scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        _backdrop.Width = _virtual.Width / _scale;
        _backdrop.Height = _virtual.Height / _scale;
        // the canvas is the whole virtual screen; this window shows its own monitor's part of it
        _canvas.Width = _backdrop.Width;
        _canvas.Height = _backdrop.Height;
        var home = AllMonitors().Count > 1 ? _home : _virtual;
        // moved as a whole (not the canvas inside it), so the mirrors' pictures of the canvas stay in its own coordinates
        _host.RenderTransform = new TranslateTransform(-(home.Left - _virtual.Left) / _scale, -(home.Top - _virtual.Top) / _scale);
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
            if (IsOverlay(h)) return true;
            var r = VisibleBounds(h);
            if (r.Width >= 24 && r.Height >= 24) list.Add((r, h));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>
    /// Windows no one means to capture: ones the mouse goes straight through (the screen-wide
    /// layers that Medal, Discord, NVIDIA, Xbox Game Bar and the like keep over everything), and
    /// see-through tool windows that don't take clicks. Picking those gave the whole screen.
    /// </summary>
    internal static bool IsOverlay(IntPtr h)
    {
        long ex = GetExStyle(h);
        if ((ex & WS_EX_TRANSPARENT) != 0) return true;
        return (ex & WS_EX_LAYERED) != 0 && (ex & (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)) != 0 && (ex & WS_EX_NOACTIVATE) != 0;
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

    // ---- pictures inside the screenshot ----

    private IReadOnlyList<FoundImage>? _images;
    private readonly CancellationTokenSource _imageSearch = new();
    /// <summary>The picture the selection snapped to, if it did.</summary>
    private FoundImage? _snapped;

    /// <summary>Looks for pictures in the frozen screen in the background; hovering snaps to them once found.</summary>
    private void FindImages()
    {
        int w = _frozen.PixelWidth, h = _frozen.PixelHeight;
        var token = _imageSearch.Token;
        Task.Run(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var found = ImageFinder.Find(_pixels, w, h, token);
            CrashLog.Note($"capture screen: {found.Count} pictures found in {sw.ElapsedMilliseconds} ms");
            return found;
        }, token).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && !token.IsCancellationRequested)
            {
                _images = t.Result;
                if (IsVisible && !_dragging) Update(CursorPos());
            }
        }, token, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
        Closed += (_, _) => _imageSearch.Cancel();
    }

#if DEBUG
    /// <summary>For the self-test: waits for the picture search, then what a click at (x, y) would take.</summary>
    internal (int Count, Int32Rect Target, bool Picture) PictureTargetForTest(int x, int y)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (_images == null && DateTime.UtcNow < until) { App.DoEventsForTest(); Thread.Sleep(10); }
        var p = new POINT(x + _virtual.Left, y + _virtual.Top);
        var r = Target(p);
        return (_images?.Count ?? -1, new Int32Rect(r.Left - _virtual.Left, r.Top - _virtual.Top, r.Width, r.Height), PictureAt(p) != null);
    }
#endif

#if DEBUG
    /// <summary>For the self-test: how many visible windows are skipped as overlays, and the size of the first one that would be picked.</summary>
    internal static (int Skipped, string First) WindowsForTest()
    {
        int skipped = 0;
        EnumWindows((h, _) =>
        {
            if (IsWindowVisible(h) && !IsIconic(h) && !IsCloaked(h) && IsOverlay(h)) skipped++;
            return true;
        }, IntPtr.Zero);
        var first = VisibleWindows().FirstOrDefault();
        return (skipped, first.Item2 == IntPtr.Zero ? "none" : $"{first.Item1.Width}x{first.Item1.Height}");
    }
#endif

    /// <summary>The picture under the cursor when it's smaller than the window there; Ctrl skips pictures.</summary>
    private FoundImage? PictureAt(POINT p)
    {
        if (_images is not { Count: > 0 } images || _mode != OverlayMode.Region || Pick != CapturePick.Region) return null;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) return null;
        if (ImageFinder.At(images, p.X - _virtual.Left, p.Y - _virtual.Top) is not { } found) return null;
        var window = Hovered(p);
        if ((long)found.Bounds.Width * found.Bounds.Height >= (long)window.Width * window.Height * 0.9) return null;
        return found with { Bounds = new Int32Rect(found.Bounds.X + _virtual.Left, found.Bounds.Y + _virtual.Top, found.Bounds.Width, found.Bounds.Height) };
    }

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
        _ when PictureAt(p) is { } picture => new RECT
        {
            Left = picture.Bounds.X, Top = picture.Bounds.Y,
            Right = picture.Bounds.X + picture.Bounds.Width, Bottom = picture.Bounds.Y + picture.Bounds.Height,
        },
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
                _snapped = _mode == OverlayMode.Region && Pick == CapturePick.Region && _toolbar?.FixedSize == null ? PictureAt(p) : null;
                ShowRect(Target(p), solid: _toolbar?.FixedSize != null && Pick == CapturePick.Region);
                break;
            case OverlayMode.Ruler when _dragging:
                ShowRuler(_start, p);
                break;
        }
        bool magnify = Tool == CaptureTool.None && (_toolbar?.Magnifier ?? MagnifierOn) && !(_toolbar?.IsMouseOver ?? false);
        _magnifier.Visibility = magnify ? Visibility.Visible : Visibility.Collapsed;

        // crosshair lines right across the screen, for lining up an edge
        bool cross = CrosshairOn && Tool == CaptureTool.None && _mode is OverlayMode.Region or OverlayMode.Colour && !(_toolbar?.IsMouseOver ?? false);
        _crossH.Visibility = _crossV.Visibility = cross ? Visibility.Visible : Visibility.Collapsed;
        if (cross)
        {
            var c = Local(p.X, p.Y);
            double y = Math.Floor(c.Y) + 0.5 / _scale, x = Math.Floor(c.X) + 0.5 / _scale;
            _crossH.StrokeThickness = _crossV.StrokeThickness = 1 / _scale;
            _crossH.X1 = 0; _crossH.X2 = _backdrop.Width; _crossH.Y1 = _crossH.Y2 = y;
            _crossV.Y1 = 0; _crossV.Y2 = _backdrop.Height; _crossV.X1 = _crossV.X2 = x;
        }
        var cursor = Tool != CaptureTool.None ? Cursors.Pen : Cursors.Cross;
        if (Cursor != cursor) Cursor = cursor;

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

    private readonly RectangleGeometry _dimWhole = new(), _dimHole = new();
    private GeometryGroup? _dimShapeCache;
    private GeometryGroup _dimShape => _dimShapeCache ??= new GeometryGroup { FillRule = FillRule.EvenOdd, Children = { _dimWhole, _dimHole } };

    private bool? _frameSolid;

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
        if (_frameSolid != solid)
        {
            _frameSolid = solid;
            _frame.StrokeDashArray = solid ? null : new DoubleCollection { 4, 3 };
        }
        // a picture with rounded corners gets a frame with the same corners
        double round = _snapped is { } picture ? picture.Radius / _scale : 0;
        _frame.RadiusX = _frame.RadiusY = round;

        // the dimming is the whole screen with a hole: the same two shapes, moved, not made anew each time
        var whole = new Rect(0, 0, _backdrop.Width, _backdrop.Height);
        if (_dimWhole.Rect != whole) _dimWhole.Rect = whole;
        _dimHole.Rect = rect;
        _dimHole.RadiusX = _dimHole.RadiusY = round;
        if (_dim.Data != _dimShape) _dim.Data = _dimShape;

        _labelText.Text = _snapped != null ? $"Picture   {r.Width} × {r.Height}   ·   Ctrl for the window" : $"{r.Width} × {r.Height}";
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
        _frameSolid = false;
        _dim.Data = new RectangleGeometry(new Rect(0, 0, _backdrop.Width, _backdrop.Height));

        int dx = b.X - a.X, dy = b.Y - a.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        double angle = Math.Atan2(-dy, dx) * 180 / Math.PI;
        _labelText.Text = $"{Math.Abs(dx) + 1} × {Math.Abs(dy) + 1} px   ·   {length:0.#} px   ·   {angle:0.#}°";
        _label.Visibility = Visibility.Visible;
        Canvas.SetLeft(_label, lb.X + 14);
        Canvas.SetTop(_label, lb.Y - 30);
    }

    private bool _framesHooked;
    private POINT _lastDrawn = new(int.MinValue, int.MinValue);

    /// <summary>
    /// The box, magnifier and readout follow the pointer frame by frame: just before each screen
    /// refresh the pointer's position is read and drawn, so the box keeps up with the pointer the
    /// way Windows' own snipping does. (Drawing on each mouse message instead lagged a frame or
    /// two behind, as the message often landed just after a frame had been put together.)
    /// </summary>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_framesHooked) return;
        _framesHooked = true;
        CompositionTarget.Rendering += OnFrame;
        Closed += (_, _) => CompositionTarget.Rendering -= OnFrame;
        OnFrame(null, EventArgs.Empty);
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var p = CursorPos();
        if (p.X == _lastDrawn.X && p.Y == _lastDrawn.Y) return;
        _lastDrawn = p;
        Update(p);
    }

    private bool _pickingColour;
    /// <summary>The press picked a colour, so its release mustn't also capture.</summary>
    private bool _swallowRelease;

    /// <summary>The capture bar's Pick: the next click takes a colour instead of capturing.</summary>
    internal void BeginPickColour()
    {
        _pickingColour = true;
        Cursor = Cursors.Cross;
        Toast.Show("Click anywhere to take its colour");
        Focus();
    }

    internal bool IsPickingColour => _pickingColour;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_toolbar?.IsMouseOver == true) return;
        if (_pickingColour && _toolbar != null)
        {
            // the colour under the click, from the frozen screen
            _pickingColour = false;
            _swallowRelease = true;
            Cursor = null;
            Toast.Hide();
            _toolbar.SetColour(PixelAt(CursorPos()));
            if (Tool == CaptureTool.None) _toolbar.SetTool(CaptureTool.Pen);
            e.Handled = true;
            return;
        }
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
        if (_swallowRelease && e != null)
        {
            _swallowRelease = false;
            CrashLog.Note("capture screen: release after picking a colour, ignored");
            return;
        }
        if (_drawing != null)
        {
            CrashLog.Note($"capture screen: click drew with the {Tool} tool instead of capturing");
            ReleaseMouseCapture();
            if (_drawing.IsUseful) _marks.Add(_drawing);
            _drawing = null;
            _toolbar?.SetCanUndo(_marks.Count > 0);
            _ink.InvalidateVisual();
            return;
        }
        if (e != null && _toolbar?.IsMouseOver == true && !_dragging)
        {
            CrashLog.Note("capture screen: release over the capture bar, ignored");
            return;
        }
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
                // a click on a picture inside a window takes just the picture, not the window
                var picture = !box && Pick == CapturePick.Region && _toolbar?.FixedSize == null ? PictureAt(end) : null;
                if (!box && picture == null && Pick is CapturePick.Region or CapturePick.Window && _toolbar?.FixedSize == null)
                    SelectedWindow = HoveredWindow(end)?.Handle ?? IntPtr.Zero;
                if (picture != null) CrashLog.Note($"capture screen: clicked a picture inside the window, radius {picture.Value.Radius}");
                r = ClipToScreen(r);
                if (box && TightenShown && r.Width > 8 && r.Height > 8)
                {
                    var (tl, tt, tr, tb) = Tighten.Fit(_pixels, _frozen.PixelWidth,
                        r.Left - _virtual.Left, r.Top - _virtual.Top, r.Right - _virtual.Left, r.Bottom - _virtual.Top);
                    CrashLog.Note($"capture screen: box fitted to its content, {r.Width}x{r.Height} -> {tr - tl}x{tb - tt}");
                    r = new RECT { Left = tl + _virtual.Left, Top = tt + _virtual.Top, Right = tr + _virtual.Left, Bottom = tb + _virtual.Top };
                }
                if (r.Width >= 2 && r.Height >= 2) Selection = new Int32Rect(r.Left, r.Top, r.Width, r.Height);
                CrashLog.Note($"capture screen: {(box ? "dragged" : "clicked")}, pick={Pick}, {r.Width}x{r.Height}, window={SelectedWindow != IntPtr.Zero}");
                Close();
                break;
            case OverlayMode.Window:
                if (_hasCurrent) Selection = new Int32Rect(_current.Left, _current.Top, _current.Width, _current.Height);
                if (Pick == CapturePick.Window) SelectedWindow = HoveredWindow(end)?.Handle ?? IntPtr.Zero;
                CrashLog.Note($"window screen: clicked, highlighted={_hasCurrent}, pick={Pick}, window={SelectedWindow != IntPtr.Zero}");
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

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        // letting go of Ctrl snaps back to the picture under the mouse
        if (e.Key is Key.LeftCtrl or Key.RightCtrl && !_dragging) Update(CursorPos());
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // typing a colour code in the colour popup isn't a shortcut
        if (Keyboard.FocusedElement is TextBox) return;
        // holding Ctrl takes the whole window instead of the picture under the mouse
        if (e.Key is Key.LeftCtrl or Key.RightCtrl && !_dragging && _images is { Count: > 0 }) Update(CursorPos());
        if (e.Key == Key.Escape)
        {
            if (_pickingColour) { _pickingColour = false; Toast.Hide(); return; }
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
                case Key.T: _toolbar.ToggleTighten(); e.Handled = true; return;
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
