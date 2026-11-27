using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Overskin.Linux.Core;
using Overskin.Linux.Platform;

namespace Overskin.Linux.Interface;

/// <summary>How a pin was, to open it again the same after a restart (as on Windows).</summary>
public sealed class PinState
{
    public string Path { get; set; } = "";
    /// <summary>Where the middle of the picture is, in desktop units.</summary>
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double Rotation { get; set; }
    public double Opacity { get; set; } = 1;
    public bool FlipHorizontal { get; set; }
    public bool FlipVertical { get; set; }
    public bool Grayscale { get; set; }
    public bool Locked { get; set; }
    public int Grid { get; set; }
}

/// <summary>
/// A picture that stays on top of everything (as on Windows). Drag to move, scroll to zoom,
/// Ctrl+scroll for see-through, double-click or Esc to close. Click it for Free Transform, the
/// way Photoshop does it: a box with eight handles turned with the picture; drag inside to move
/// (Shift: straight across or down), a corner to scale in proportion (Shift: stretch freely; Alt:
/// from the middle), a side to stretch one way (Shift: in proportion; Alt: from the middle), and
/// outside the box to rotate round the middle (Shift: 15° steps). Enter or a double-click keeps it,
/// Esc puts it back, Ctrl+T shows or hides the box, and the arrow keys nudge (Shift: ten at a time).
/// </summary>
public sealed class PinWindow : Window
{
    private const double PadSize = 34, HandleSize = 8, HandleReach = 7, MinSide = 16;

    private Bitmap _picture;
    private Bitmap _shown;
    private readonly PinSurface _surface;
    private bool _live;
    private bool _through, _locked, _flipH, _flipV, _gray;
    private int _grid;
    private double _w, _h, _angle;
    private bool _transforming;
    private (double W, double H, double Angle, Point Centre)? _before;

    /// <summary>The file the picture is kept in, so it comes back after a restart.</summary>
    public string? SourcePath { get; private set; }

    /// <summary>For the self-test: the size of what the pin shows, and whether it still follows a window.</summary>
    public PixelSize PictureSize => _shown.PixelSize;
    public bool IsLive => _live;

    /// <summary>Every pin that's open, for "make pins clickable again" and for keeping them over a restart.</summary>
    public static readonly List<PinWindow> All = new();

    /// <summary>Raised when a pin opens or closes, or its click-through changes (the Pins page lists them).</summary>
    public static event Action? ListChanged;

    private static void Announce() => Avalonia.Threading.Dispatcher.UIThread.Post(() => ListChanged?.Invoke());

    /// <summary>What the pin shows, for the Pins page's list.</summary>
    public Bitmap Shown => _shown;

    /// <summary>How big it's shown, against the picture's own size (100 is actual size).</summary>
    public int ZoomPercent => _shown.PixelSize.Width > 0 ? (int)Math.Round(_w * Scaling / _shown.PixelSize.Width * 100) : 100;

    private double Pad => _transforming ? PadSize : 1;
    private double Scaling => Screens.Primary?.Scaling ?? 1;

    /// <summary>Lets clicks pass through to whatever is under the pin, for tracing over it.</summary>
    public bool ClickThrough
    {
        get => _through;
        set
        {
            _through = value;
            Announce();
            var handle = Mac.IsMac ? Mac.NSWindowOf(TryGetPlatformHandle()) : TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (!Input.SetClickThrough(handle, value) && value)
                Desktop.Notify("Can't let clicks through here", "This desktop doesn't allow it for Overskin's windows.", App.IconFile);
        }
    }

    /// <param name="at">Where the picture came from on screen (picture pixels of the last screenshot): the pin opens exactly there.</param>
    /// <param name="source">The file it's from, if it's one; otherwise it's kept in Overskin's own folder.</param>
    public PinWindow(Bitmap picture, PixelRect? at = null, string? source = null, PinState? restore = null)
    {
        _picture = _shown = picture;
        SourcePath = source;
        Title = "Overskin pin";
        Icon = App.Icon;
        SystemDecorations = SystemDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Focusable = true;

        _w = picture.PixelSize.Width / Scaling;
        _h = picture.PixelSize.Height / Scaling;
        Content = _surface = new PinSurface(this);
        Resize();
        ContextMenu = BuildMenu();

        Opened += (_, _) => { All.Add(this); Announce(); };
        if (Mac.IsMac) Opened += (_, _) => Mac.Float(Mac.NSWindowOf(TryGetPlatformHandle()));
        if (restore != null) Opened += (_, _) => Restore(restore);
        else if (at is { } spot) PlaceAt(spot);
        Closed += (_, _) => { All.Remove(this); _live = false; Announce(); };
        KeyDown += OnKey;
    }

    // ---- the menu ----

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        void Item(string text, Action click)
        {
            var m = new MenuItem { Header = text };
            m.Click += (_, _) => click();
            menu.Items.Add(m);
        }
        MenuItem Toggle(string text, Func<bool> get, Action<bool> set)
        {
            var m = new MenuItem { Header = text, ToggleType = MenuItemToggleType.CheckBox };
            m.Click += (_, _) => set(!get());
            menu.Opening += (_, _) => m.IsChecked = get();
            menu.Items.Add(m);
            return m;
        }
        Item("Copy image", () => _ = Desktop.CopyImageAsync(_shown, this));
        Item("Save", () =>
        {
            var path = App.Settings.NewFilePath();
            Imaging.SavePng(_shown, path);
            Desktop.Notify("Pin saved", Path.GetFileName(path), path);
        });
        Item("Save as PNG…", () => _ = SaveAsAsync());
        Item("Open in the editor", () => new EditorWindow(_shown, null).Show());
        menu.Items.Add(new Separator());
        foreach (int percent in new[] { 100, 75, 50, 25 }) Item($"Opacity {percent}%", () => Opacity = percent / 100.0);
        Item("Let clicks through, to trace over it", () =>
        {
            if (Opacity > 0.8) Opacity = 0.5;
            ClickThrough = true;
            Desktop.Notify("Clicks now pass through the pin", "To move or close it again: tray menu → Make pins clickable again.", App.IconFile);
        });
        Toggle("Lock position", () => _locked, v => { _locked = v; if (v) Transforming = false; });
        menu.Items.Add(new Separator());
        Item("Rotate left", () => Turn(-90));
        Item("Rotate right", () => Turn(90));
        Toggle("Flip horizontal", () => _flipH, v => { _flipH = v; Remake(); });
        Toggle("Flip vertical", () => _flipV, v => { _flipV = v; Remake(); });
        Toggle("Grayscale", () => _gray, v => { _gray = v; Remake(); });
        var grid = new MenuItem { Header = "Drawing grid" };
        foreach (int n in new[] { 0, 3, 4, 6, 8 })
        {
            var g = new MenuItem { Header = n == 0 ? "Off" : $"{n} × {n}", ToggleType = MenuItemToggleType.Radio };
            g.Click += (_, _) => { _grid = n; _surface.InvalidateVisual(); };
            menu.Opening += (_, _) => g.IsChecked = _grid == n;
            grid.Items.Add(g);
        }
        menu.Items.Add(grid);
        Item("Free transform (scale, stretch, rotate)", () => Transforming = true);
        Item("Reset size and rotation", ResetSize);
        menu.Items.Add(new Separator());
        Item("Close", Close);
        return menu;
    }

    private async Task SaveAsAsync()
    {
        var path = await Ui.PickSavePathAsync(this, "Pin.png");
        if (path == null) return;
        try { Imaging.SavePng(_shown, path); }
        catch (Exception ex) { MessageWindow.Show("Couldn't save the pin", ex.Message); }
    }

    /// <summary>The picture as shown: flipped and in grey when those are on (the turn is drawn, not baked in).</summary>
    private void Remake()
    {
        var b = _picture;
        if (_flipH) b = Effects.Flip(b, horizontal: true);
        if (_flipV) b = Effects.Flip(b, horizontal: false);
        if (_gray)
        {
            var px = Imaging.Pixels(b, out _);
            for (int i = 0; i < px.Length; i += 4)
            {
                byte y = (byte)Math.Round(px[i] * 0.114 + px[i + 1] * 0.587 + px[i + 2] * 0.299);
                px[i] = px[i + 1] = px[i + 2] = y;
            }
            b = Imaging.FromPixels(px, b.PixelSize);
        }
        _shown = b;
        _surface.InvalidateVisual();
    }

    /// <summary>A quarter turn, round the middle.</summary>
    private void Turn(int degrees)
    {
        var centre = Centre;
        Place(_w, _h, Normalise(_angle + degrees), centre);
    }

    private void ResetSize()
    {
        var centre = Centre;
        Place(_shown.PixelSize.Width / Scaling, _shown.PixelSize.Height / Scaling, 0, centre);
    }

    // ---- where it is ----

    private double DesktopScale => DesktopScaling > 0 ? DesktopScaling : 1;

    /// <summary>The middle of the picture, in desktop units.</summary>
    internal Point Centre => new(Position.X / DesktopScale + Width / 2, Position.Y / DesktopScale + Height / 2);

    /// <summary>The window's size: the turned picture's box, plus room for the handles while transforming.</summary>
    private void Resize()
    {
        double r = _angle * Math.PI / 180, c = Math.Abs(Math.Cos(r)), s = Math.Abs(Math.Sin(r));
        Width = Math.Max(24, _w * c + _h * s + 2 * Pad);
        Height = Math.Max(24, _w * s + _h * c + 2 * Pad);
        _surface.InvalidateVisual();
    }

    /// <summary>Sets size, angle and where the middle is, all at once.</summary>
    private void Place(double w, double h, double angle, Point centre)
    {
        _w = Math.Max(MinSide, w);
        _h = Math.Max(MinSide, h);
        _angle = angle;
        Resize();
        Position = new PixelPoint((int)Math.Round((centre.X - Width / 2) * DesktopScale), (int)Math.Round((centre.Y - Height / 2) * DesktopScale));
    }

    /// <summary>Opens over the spot the picture was snipped from, so it looks as if it never moved.</summary>
    private void PlaceAt(PixelRect spot)
    {
        WindowStartupLocation = WindowStartupLocation.Manual;
        if (Mac.IsMac)
        {
            // one picture pixel is 1/scale of a point on the display it came from
            _w = spot.Width / Math.Max(0.1, Mac.Shot.Scale);
            _h = spot.Height / Math.Max(0.1, Mac.Shot.Scale);
            Resize();
            var corner = Mac.FromPicture(new Point(spot.X, spot.Y));
            var size = new Mac.CGRect(corner.X - 1, corner.Y - 1, _w + 2, _h + 2);
            Opened += (_, _) => Mac.Place(Mac.NSWindowOf(TryGetPlatformHandle()), size);
            return;
        }
        // X11: picture pixels are the desktop's own; Wayland decides where windows go by itself
        Position = new PixelPoint(spot.X - 1, spot.Y - 1);
    }

    public bool Transforming
    {
        get => _transforming;
        set
        {
            if (value && _locked) return;
            if (value == _transforming) return;
            var centre = Centre;
            _transforming = value;
            _before = value ? (_w, _h, _angle, centre) : null;
            Place(_w, _h, _angle, centre);
            if (value) Focus();
        }
    }

    // ---- keeping pins over a restart ----

    /// <summary>Where the picture is kept: its own file, or a copy in Overskin's folder.</summary>
    public string Keep()
    {
        if (SourcePath != null && File.Exists(SourcePath)) return SourcePath;
        var folder = Path.Combine(Path.GetDirectoryName(Log.FilePath)!, "pins");
        Directory.CreateDirectory(folder);
        SourcePath = Path.Combine(folder, $"pin-{Guid.NewGuid():N}.png");
        Imaging.SavePng(_picture, SourcePath);
        return SourcePath;
    }

    public PinState ToState()
    {
        var c = Centre;
        return new PinState
        {
            Path = Keep(), X = c.X, Y = c.Y, Width = _w, Height = _h, Rotation = _angle, Opacity = Opacity,
            FlipHorizontal = _flipH, FlipVertical = _flipV, Grayscale = _gray, Locked = _locked, Grid = _grid,
        };
    }

    private void Restore(PinState s)
    {
        Opacity = Math.Clamp(s.Opacity, 0.15, 1);
        _flipH = s.FlipHorizontal;
        _flipV = s.FlipVertical;
        _gray = s.Grayscale;
        _locked = s.Locked;
        _grid = s.Grid;
        Remake();
        // a spot off every screen (a monitor was unplugged) puts it in the middle instead
        bool onScreen = Screens.All.Any(sc =>
        {
            var b = sc.Bounds;
            double k = DesktopScale;
            return s.X * k >= b.X && s.X * k < b.Right && s.Y * k >= b.Y && s.Y * k < b.Bottom;
        });
        var centre = onScreen ? new Point(s.X, s.Y) : Centre;
        Place(s.Width > 0 ? s.Width : _w, s.Height > 0 ? s.Height : _h, s.Rotation, centre);
    }

    /// <summary>Saves where every pin is (on quitting), so they come back next time.</summary>
    public static void SaveAll()
    {
        try
        {
            App.Settings.Pins = All.Where(p => !p._live).Select(p => p.ToState()).ToList();
            App.Settings.Save();
        }
        catch (Exception ex) { Log.Write("saving pins: " + ex.Message); }
    }

    /// <summary>Opens last time's pins again; and clears out kept copies nothing uses any more.</summary>
    public static void RestoreAll()
    {
        foreach (var s in App.Settings.Pins.ToList())
        {
            if (!File.Exists(s.Path)) continue;
            try { new PinWindow(new Bitmap(s.Path), source: s.Path, restore: s).Show(); }
            catch (Exception ex) { Log.Write("restoring a pin: " + ex.Message); }
        }
        try
        {
            var folder = Path.Combine(Path.GetDirectoryName(Log.FilePath)!, "pins");
            if (!Directory.Exists(folder)) return;
            var used = new HashSet<string>(App.Settings.Pins.Select(p => p.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.GetFiles(folder, "*.png"))
                if (!used.Contains(f)) File.Delete(f);
        }
        catch { }
    }

    // ---- live pins ----

    /// <summary>
    /// Keeps the pin showing a window as it changes: <paramref name="next"/> gives its latest picture
    /// (null when the window has gone, which stops it). Runs until the pin closes.
    /// </summary>
    public void Live(Func<Task<Bitmap?>> next, TimeSpan every)
    {
        _live = true;
        Title = "Overskin live pin";
        Opened += async (_, _) =>
        {
            while (_live && IsVisible)
            {
                await Task.Delay(every);
                Bitmap? fresh = null;
                try { fresh = await next(); } catch (Exception ex) { Log.Write("live pin: " + ex.Message); }
                if (fresh == null) { _live = false; break; }
                bool resized = fresh.PixelSize != _picture.PixelSize;
                double kx = _w / Math.Max(1, _picture.PixelSize.Width), ky = _h / Math.Max(1, _picture.PixelSize.Height);
                _picture = fresh;
                Remake();
                if (resized) Place(fresh.PixelSize.Width * kx, fresh.PixelSize.Height * ky, _angle, Centre);
            }
        };
    }

    // ---- free transform ----

    /// <summary>What a drag does: move, rotate, or pull a handle (Sx, Sy are -1, 0 or 1 across and down).</summary>
    internal enum Zone { None, Move, Rotate, Handle }

    private Zone _dragZone;
    private int _sx, _sy;
    private Point _dragStart, _startCentre;
    private double _startW, _startH, _startAngle;
    private bool _dragged;
    private string? _readout;

    private static Vector Rotated(Vector v, double degrees)
    {
        double r = degrees * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r);
        return new Vector(v.X * c - v.Y * s, v.X * s + v.Y * c);
    }

    private static double Normalise(double angle)
    {
        angle %= 360;
        if (angle > 180) angle -= 360;
        if (angle <= -180) angle += 360;
        return angle;
    }

    /// <summary>What's under a point (desktop units): a handle, the inside, or the outside (rotate).</summary>
    internal (Zone Zone, int Sx, int Sy) HitTest(Point screen)
    {
        var local = Rotated(screen - Centre, -_angle);
        double hw = _w / 2, hh = _h / 2;
        // corners first, then sides, as Photoshop does when they crowd together
        foreach (var (sx, sy) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1), (0, -1), (1, 0), (0, 1), (-1, 0) })
        {
            var h = new Vector(sx * hw, sy * hh);
            if ((local - h).Length <= HandleReach) return (Zone.Handle, sx, sy);
        }
        if (Math.Abs(local.X) <= hw && Math.Abs(local.Y) <= hh) return (Zone.Move, 0, 0);
        return (Zone.Rotate, 0, 0);
    }

    internal void TransformStart(Zone zone, int sx, int sy, Point screen)
    {
        _dragZone = zone;
        _sx = sx;
        _sy = sy;
        _dragStart = screen;
        _startCentre = Centre;
        _startW = _w;
        _startH = _h;
        _startAngle = _angle;
        _dragged = false;
    }

    internal void TransformMove(Point screen, bool shift, bool alt)
    {
        if (((Vector)(screen - _dragStart)).Length > 2) _dragged = true;
        switch (_dragZone)
        {
            case Zone.Move:
            {
                Vector d = screen - _dragStart;
                if (shift) d = Math.Abs(d.X) >= Math.Abs(d.Y) ? new Vector(d.X, 0) : new Vector(0, d.Y);
                Place(_startW, _startH, _startAngle, _startCentre + d);
                _readout = $"X: {Math.Round(d.X)}  Y: {Math.Round(d.Y)}";
                break;
            }
            case Zone.Rotate:
            {
                Vector from = _dragStart - _startCentre;
                Vector to = screen - _startCentre;
                if (from.Length < 1 || to.Length < 1) return;
                double turn = (Math.Atan2(to.Y, to.X) - Math.Atan2(from.Y, from.X)) * 180 / Math.PI;
                double angle = _startAngle + turn;
                if (shift) angle = Math.Round(angle / 15) * 15;
                angle = Normalise(angle);
                Place(_startW, _startH, angle, _startCentre);
                _readout = $"{angle:0.0}°";
                break;
            }
            case Zone.Handle:
            {
                var m = Rotated(screen - _startCentre, -_startAngle);
                // the opposite side stays put; with Alt, the middle does
                var anchor = alt ? new Vector(0, 0) : new Vector(-_sx * _startW / 2, -_sy * _startH / 2);
                bool corner = _sx != 0 && _sy != 0;
                bool proportional = corner ? !shift : shift;
                double w = _startW, h = _startH;
                if (_sx != 0) w = alt ? 2 * _sx * m.X : _sx * (m.X - anchor.X);
                if (_sy != 0) h = alt ? 2 * _sy * m.Y : _sy * (m.Y - anchor.Y);
                if (proportional)
                {
                    double k;
                    if (corner)
                    {
                        // along the diagonal, so it follows the pointer smoothly in any direction
                        var reach = new Vector(_sx * _startW / 2, _sy * _startH / 2) - anchor;
                        k = Vector.Dot(new Vector(m.X, m.Y) - anchor, reach) / reach.SquaredLength;
                    }
                    else k = _sx != 0 ? w / _startW : h / _startH;
                    k = Math.Max(k, MinSide / Math.Min(_startW, _startH));
                    w = _startW * k;
                    h = _startH * k;
                }
                w = Math.Max(MinSide, w);
                h = Math.Max(MinSide, h);
                var middle = alt ? new Vector(0, 0)
                    : new Vector(_sx != 0 ? anchor.X + _sx * w / 2 : 0, _sy != 0 ? anchor.Y + _sy * h / 2 : 0);
                Place(w, h, _startAngle, _startCentre + Rotated(middle, _startAngle));
                _readout = $"W: {Math.Round(w * Scaling)} px\nH: {Math.Round(h * Scaling)} px";
                break;
            }
        }
    }

    internal void TransformEnd()
    {
        _dragZone = Zone.None;
        _readout = null;
        _surface.InvalidateVisual();
    }

    /// <summary>Esc: back to how it was when the box appeared. False when nothing had changed.</summary>
    internal bool Revert()
    {
        if (_before is not { } b) return false;
        bool changed = Math.Abs(b.W - _w) > 0.01 || Math.Abs(b.H - _h) > 0.01 || Math.Abs(b.Angle - _angle) > 0.01 || ((Vector)(b.Centre - Centre)).Length > 0.01;
        if (!changed) return false;
        Place(b.W, b.H, b.Angle, b.Centre);
        _before = (b.W, b.H, b.Angle, b.Centre);
        return true;
    }

    // ---- mouse and keys ----

    private Point ScreenPoint(PointerEventArgs e)
    {
        var p = e.GetPosition(this);
        return new Point(Position.X / DesktopScale + p.X, Position.Y / DesktopScale + p.Y);
    }

    private bool _pressed;

    internal void Pressed(PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2)
        {
            if (_transforming) Transforming = false; // a double-click keeps it, as in Photoshop
            else Close();
            return;
        }
        if (_locked) return;
        var screen = ScreenPoint(e);
        var (zone, sx, sy) = _transforming ? HitTest(screen) : (Zone.Move, 0, 0);
        TransformStart(zone, sx, sy, screen);
        _pressed = true;
        e.Pointer.Capture(_surface);
    }

    internal void Moved(PointerEventArgs e)
    {
        var screen = ScreenPoint(e);
        if (_pressed && _dragZone != Zone.None)
        {
            TransformMove(screen, e.KeyModifiers.HasFlag(KeyModifiers.Shift), e.KeyModifiers.HasFlag(KeyModifiers.Alt));
            return;
        }
        if (!_transforming) return;
        var (zone, sx, sy) = HitTest(screen);
        _surface.Cursor = zone switch
        {
            Zone.Handle => new Cursor((sx, sy) switch
            {
                (-1, -1) or (1, 1) => StandardCursorType.TopLeftCorner,
                (1, -1) or (-1, 1) => StandardCursorType.TopRightCorner,
                (0, _) => StandardCursorType.SizeNorthSouth,
                _ => StandardCursorType.SizeWestEast,
            }),
            Zone.Rotate => new Cursor(StandardCursorType.Cross),
            _ => new Cursor(StandardCursorType.SizeAll),
        };
    }

    internal void Released(PointerReleasedEventArgs e)
    {
        if (!_pressed) return;
        _pressed = false;
        e.Pointer.Capture(null);
        bool click = !_dragged && _dragZone == Zone.Move;
        TransformEnd();
        // a click (not a drag) on the picture brings up the box
        if (click && !_transforming && !_locked) Transforming = true;
    }

    internal void Wheel(PointerWheelEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))
            Opacity = Math.Clamp(Opacity + e.Delta.Y * 0.05, 0.15, 1);
        else
        {
            double k = e.Delta.Y > 0 ? 1.1 : 1 / 1.1;
            if (Math.Min(_w, _h) * k >= MinSide && Math.Max(_w, _h) * k < 20000) Place(_w * k, _h * k, _angle, Centre);
        }
        e.Handled = true;
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        switch (e.Key)
        {
            case Key.Escape:
                if (_transforming && Revert()) break;
                if (_transforming) Transforming = false;
                else Close();
                break;
            case Key.Enter:
                Transforming = false;
                break;
            case Key.T when ctrl:
                Transforming = !_transforming;
                break;
            case Key.C when ctrl:
                _ = Desktop.CopyImageAsync(_shown, this);
                break;
            case Key.Left or Key.Right or Key.Up or Key.Down when !_locked:
            {
                double step = shift ? 10 : 1;
                var d = new Vector(e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0, e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0);
                Place(_w, _h, _angle, Centre + d);
                break;
            }
            default: return;
        }
        e.Handled = true;
    }

    // ---- for the self-test ----

    internal (double W, double H, double Angle) ShapeForTest => (_w, _h, _angle);
    internal void TurnForTest(int degrees) => Turn(degrees);
    internal void FlipForTest() { _flipH = !_flipH; Remake(); }
    internal void GrayForTest() { _gray = true; Remake(); }
    internal Bitmap ShownForTest => _shown;

    /// <summary>The picture turned and scaled, with the grid and the transform box over it.</summary>
    private sealed class PinSurface : Control
    {
        private readonly PinWindow _pin;
        private static readonly IBrush BoxLine = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
        private static readonly IBrush BoxHalo = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));

        public PinSurface(PinWindow pin)
        {
            _pin = pin;
            ClipToBounds = false;
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e) => _pin.Pressed(e);
        protected override void OnPointerMoved(PointerEventArgs e) => _pin.Moved(e);
        protected override void OnPointerReleased(PointerReleasedEventArgs e) => _pin.Released(e);
        protected override void OnPointerWheelChanged(PointerWheelEventArgs e) => _pin.Wheel(e);

        public override void Render(DrawingContext ctx)
        {
            var p = _pin;
            // the whole window takes the pointer (the room round the box is where rotating happens)
            ctx.FillRectangle(p._transforming ? new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) : Brushes.Transparent, new Rect(Bounds.Size));
            var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);
            var box = new Rect(-p._w / 2, -p._h / 2, p._w, p._h);
            using (ctx.PushTransform(Matrix.CreateRotation(p._angle * Math.PI / 180) * Matrix.CreateTranslation(centre.X, centre.Y)))
            {
                using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
                    ctx.DrawImage(p._shown, new Rect(p._shown.Size), box);
                if (p._grid > 1)
                {
                    var line = new Pen(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), 1);
                    var shade = new Pen(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), 1);
                    for (int i = 1; i < p._grid; i++)
                    {
                        double x = box.X + box.Width * i / p._grid, y = box.Y + box.Height * i / p._grid;
                        ctx.DrawLine(shade, new Point(x + 1, box.Y), new Point(x + 1, box.Bottom));
                        ctx.DrawLine(line, new Point(x, box.Y), new Point(x, box.Bottom));
                        ctx.DrawLine(shade, new Point(box.X, y + 1), new Point(box.Right, y + 1));
                        ctx.DrawLine(line, new Point(box.X, y), new Point(box.Right, y));
                    }
                }
                if (!p._transforming)
                {
                    ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Colours.Accent, 0.8), 1), box.Inflate(0.5));
                    return;
                }
                // the box: a dark line with a light halo, readable on anything, and eight handles
                ctx.DrawRectangle(null, new Pen(BoxHalo, 3), box);
                ctx.DrawRectangle(null, new Pen(BoxLine, 1), box);
                foreach (var (sx, sy) in new[] { (-1, -1), (0, -1), (1, -1), (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0) })
                {
                    var at = new Point(sx * p._w / 2, sy * p._h / 2);
                    ctx.DrawRectangle(Brushes.White, new Pen(BoxLine, 1), new Rect(at.X - HandleSize / 2, at.Y - HandleSize / 2, HandleSize, HandleSize));
                }
            }
            if (p._readout is { } text)
            {
                var ft = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Inter"), 11, Brushes.White);
                var r = new Rect(6, 6, ft.Width + 12, ft.Height + 8);
                ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(220, 18, 19, 22)), null, r, 4, 4);
                ctx.DrawText(ft, new Point(12, 10));
            }
        }
    }
}
