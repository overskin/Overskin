using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Overskin;

/// <summary>
/// Free Transform, the way Photoshop does it. Click a pinned picture and a box with eight
/// handles appears around it (turned with the picture):
/// drag inside to move (Shift: straight across or down);
/// drag a corner to scale in proportion (Shift: stretch freely; Alt: from the middle);
/// drag a side to stretch one way (Shift: in proportion; Alt: from the middle);
/// drag outside the box to rotate around the middle (Shift: 15° steps).
/// Enter or a double-click commits, Esc puts it back as it was, Ctrl+T shows or hides the box,
/// and the arrow keys nudge (Shift: ten at a time).
/// </summary>
public partial class ImageOverlayWindow
{
    private const double PadSize = 34, HandleSize = 8, HandleReach = 7;

    private bool _transforming;
    private double Pad => _transforming ? PadSize : 0;

    /// <summary>What a drag does: move, rotate, or pull a handle (Sx, Sy are -1, 0 or 1 across and down).</summary>
    internal enum Zone { None, Move, Rotate, Handle }

    private Zone _dragZone = Zone.None;
    private int _sx, _sy;
    private Point _dragStart, _startCentre;
    private double _startW, _startH, _startAngle;

    // how it was when the box appeared, for Esc
    private (double W, double H, double Angle, Point Centre)? _beforeTransform;

    /// <summary>The middle of the picture, in screen units (WPF), whatever the padding.</summary>
    internal Point Centre => (_stage?.Location ?? new Point(Left, Top)) + (Vector)_localCentre;

    /// <summary>
    /// While a handle or rotation is dragged, the window is a fixed "stage" with room to spare and
    /// only the picture moves inside it, so the window isn't resized on every mouse move (which
    /// made it jitter). It grows only when the picture would reach its edge, and a move drag moves
    /// it in one go. Null when not dragging. In screen units, snapped to whole device pixels.
    /// </summary>
    private Rect? _stage;

    /// <summary>Where the picture's middle is inside the window.</summary>
    private Point _localCentre;
    private double _laidW = -1, _laidH = -1;

    /// <summary>Puts the picture's middle at <paramref name="local"/> in the window, at its current size.</summary>
    private void LayoutContent(Point local)
    {
        _localCentre = local;
        Sheet.Width = _imageW;
        Sheet.Height = _imageH;
        Canvas.SetLeft(Sheet, local.X - _imageW / 2);
        Canvas.SetTop(Sheet, local.Y - _imageH / 2);
        if (_laidW != _imageW || _laidH != _imageH)
        {
            _laidW = _imageW;
            _laidH = _imageH;
            DrawGrid();
        }
        DrawTransformBox();
    }

    /// <summary>Moves and sizes the window in one step (WPF's Left, Top, Width and Height are four separate moves).</summary>
    private Rect SetBounds(Rect r)
    {
        double k = _dpiScale;
        int x = (int)Math.Round(r.X * k), y = (int)Math.Round(r.Y * k);
        int w = Math.Max(1, (int)Math.Ceiling(r.Width * k)), h = Math.Max(1, (int)Math.Ceiling(r.Height * k));
        var snapped = new Rect(x / k, y / k, w / k, h / k);
        if (Handle != IntPtr.Zero)
            NativeMethods.SetWindowPos(Handle, IntPtr.Zero, x, y, w, h, NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        else
        {
            Left = snapped.X; Top = snapped.Y; Width = snapped.Width; Height = snapped.Height;
        }
        return snapped;
    }

    /// <summary>Places the picture (already sized and turned) with its middle at <paramref name="centre"/>, on the stage.</summary>
    private void StagePlace(Point centre)
    {
        // room for any angle, plus the handles
        double r = Math.Sqrt(_imageW * _imageW + _imageH * _imageH) / 2 + PadSize;
        var need = new Rect(centre.X - r, centre.Y - r, 2 * r, 2 * r);
        if (_stage is { } st && _dragZone == Zone.Move)
        {
            // the whole window travels with the pointer; nothing inside it changes
            var moved = new Rect(centre - (Vector)_localCentre, st.Size);
            _stage = SetBounds(moved);
            return;
        }
        if (_stage is not { } stage || !stage.Contains(need))
        {
            double grow = Math.Max(80, r * 0.5);
            need.Inflate(grow, grow);
            stage = SetBounds(need);
            _stage = stage;
        }
        LayoutContent(centre - (Vector)_stage.Value.Location);
    }

    /// <summary>After a drag: the window shrinks back around the picture, in one step.</summary>
    private void LeaveStage()
    {
        if (_stage == null) return;
        var centre = Centre;
        _stage = null;
        double rad = _angle * Math.PI / 180, cos = Math.Abs(Math.Cos(rad)), sin = Math.Abs(Math.Sin(rad));
        double boxW = Math.Max(MinSide, _imageW * cos + _imageH * sin), boxH = Math.Max(MinSide, _imageW * sin + _imageH * cos);
        double pad = Pad;
        _appliedPad = pad;
        Frame.Margin = new Thickness(pad);
        var bounds = SetBounds(new Rect(centre.X - boxW / 2 - pad, centre.Y - boxH / 2 - pad, boxW + 2 * pad, boxH + 2 * pad));
        LayoutContent(centre - (Vector)bounds.Location);
    }

    internal double ImageWidth => _imageW;
    internal double ImageHeight => _imageH;

    /// <summary>Shows or hides the transform box.</summary>
    public bool Transforming
    {
        get => _transforming;
        set
        {
            bool on = value && !_locked && !_clickThrough;
            if (on == _transforming) return;
            _transforming = on;
            if (on) _beforeTransform = (_imageW, _imageH, _angle, Centre);
            ApplySize(keepCentre: true);
            if (on) ShowFrame(false);
        }
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        Transforming = true; // clicking a picture selects it, like a layer
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (_dragZone == Zone.None) Transforming = false;
    }

    // ---- geometry ------------------------------------------------------------------------------

    /// <summary>Turns a vector by <paramref name="degrees"/> clockwise (screen y points down), as RotateTransform does.</summary>
    private static Vector Turn(Vector v, double degrees)
    {
        double r = degrees * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r);
        return new Vector(v.X * c - v.Y * s, v.X * s + v.Y * c);
    }

    /// <summary>What's under a screen point: a handle, the inside, or the outside (rotate).</summary>
    internal (Zone Zone, int Sx, int Sy) HitTest(Point screen)
    {
        var local = Turn(screen - Centre, -_angle);
        double hw = _imageW / 2, hh = _imageH / 2;
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
        _startW = _imageW;
        _startH = _imageH;
        _startAngle = _angle;
        // the stage starts as the window is now, so nothing jumps on the first move
        _stage = new Rect(Left, Top, Width, Height);
    }

    internal void TransformMove(Point screen, bool shift, bool alt)
    {
        switch (_dragZone)
        {
            case Zone.Move:
            {
                var d = screen - _dragStart;
                if (shift) d = Math.Abs(d.X) >= Math.Abs(d.Y) ? new Vector(d.X, 0) : new Vector(0, d.Y);
                Place(_startW, _startH, _startAngle, _startCentre + d);
                ShowReadout(screen, $"X: {Math.Round(d.X)}  Y: {Math.Round(d.Y)}");
                break;
            }
            case Zone.Rotate:
            {
                var from = _dragStart - _startCentre;
                var to = screen - _startCentre;
                if (from.Length < 1 || to.Length < 1) return;
                double turn = (Math.Atan2(to.Y, to.X) - Math.Atan2(from.Y, from.X)) * 180 / Math.PI;
                double angle = _startAngle + turn;
                if (shift) angle = Math.Round(angle / 15) * 15;
                angle = Normalise(angle);
                Place(_startW, _startH, angle, _startCentre);
                ShowReadout(screen, $"{angle:0.0}°");
                break;
            }
            case Zone.Handle:
            {
                var m = Turn(screen - _startCentre, -_startAngle);
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
                        k = Vector.Multiply(new Vector(m.X, m.Y) - anchor, reach) / reach.LengthSquared;
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
                Place(w, h, _startAngle, _startCentre + Turn(middle, _startAngle));
                ShowReadout(screen, $"W: {Math.Round(w * _dpiScale)} px\nH: {Math.Round(h * _dpiScale)} px");
                break;
            }
        }
    }

    internal void TransformEnd()
    {
        _dragZone = Zone.None;
        LeaveStage();
        Readout.Visibility = Visibility.Collapsed;
        Raise(nameof(Details));
    }

    private static double Normalise(double angle)
    {
        angle %= 360;
        if (angle > 180) angle -= 360;
        if (angle <= -180) angle += 360;
        return angle;
    }

    /// <summary>Sets size, angle and where the middle is, all at once.</summary>
    private void Place(double w, double h, double angle, Point centre)
    {
        _imageW = w;
        _imageH = h;
        if (Math.Abs(angle - _angle) > 1e-6)
        {
            _angle = angle;
            Spin.Angle = angle;
            Raise(nameof(Rotation));
            Raise(nameof(RotationText));
        }
        if (_stage != null)
        {
            StagePlace(centre);
            return;
        }
        ApplySize();
        Left = centre.X - Width / 2;
        Top = centre.Y - Height / 2;
    }

    /// <summary>Esc: back to how it was when the box appeared.</summary>
    internal bool Revert()
    {
        if (_beforeTransform is not { } b) return false;
        bool changed = Math.Abs(b.W - _imageW) > 0.01 || Math.Abs(b.H - _imageH) > 0.01 || Math.Abs(b.Angle - _angle) > 0.01 || (b.Centre - Centre).Length > 0.01;
        if (!changed) return false;
        Place(b.W, b.H, b.Angle, b.Centre);
        Raise(nameof(Details));
        return true;
    }

    // ---- mouse and keys --------------------------------------------------------------------------

    private Point ScreenPoint(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        var origin = _stage?.Location ?? new Point(Left, Top);
        return new Point(origin.X + p.X, origin.Y + p.Y);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_locked || e.ButtonState != MouseButtonState.Pressed) return;
        if (e.ClickCount == 2 && _transforming)
        {
            Transforming = false; // double-click commits, as in Photoshop
            e.Handled = true;
            return;
        }
        if (!_transforming)
        {
            try { DragMove(); }
            catch (InvalidOperationException) { /* released before the drag began */ }
            return;
        }
        var screen = ScreenPoint(e);
        var (zone, sx, sy) = HitTest(screen);
        TransformStart(zone, sx, sy, screen);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_transforming) return;
        var screen = ScreenPoint(e);
        if (_dragZone != Zone.None)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                EndDrag();
                return;
            }
            TransformMove(screen, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));
            return;
        }
        var (zone, sx, sy) = HitTest(screen);
        Cursor = zone switch
        {
            Zone.Handle => ResizeCursor(Turn(new Vector(sx * _imageW, sy * _imageH), _angle)),
            Zone.Rotate => RotateCursor(screen - Centre),
            _ => Cursors.Arrow,
        };
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragZone != Zone.None) EndDrag();
    }

    private void EndDrag()
    {
        TransformEnd();
        ReleaseMouseCapture();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control), shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (e.Key)
        {
            case Key.Escape:
                if (_transforming && Revert()) { e.Handled = true; return; }
                if (_transforming) Transforming = false;
                else Close();
                break;
            case Key.Enter:
                Transforming = false;
                break;
            case Key.T when ctrl:
                Transforming = !_transforming;
                break;
            case Key.Left or Key.Right or Key.Up or Key.Down when !_locked:
            {
                double step = shift ? 10 : 1;
                Left += e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0;
                Top += e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0;
                e.Handled = true;
                break;
            }
        }
    }

    // ---- drawing the box -----------------------------------------------------------------------

    private static readonly Brush BoxLine = Frozen(Color.FromRgb(0x1E, 0x1E, 0x1E));
    private static readonly Brush BoxHalo = Frozen(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private void DrawTransformBox()
    {
        if (TransformLayer == null) return;
        TransformLayer.Children.Clear();
        if (!_transforming) return;
        var c = _localCentre;
        Point At(double sx, double sy) => c + Turn(new Vector(sx * _imageW / 2, sy * _imageH / 2), _angle);

        var outline = new PointCollection { At(-1, -1), At(1, -1), At(1, 1), At(-1, 1) };
        // a thin dark line with a pale edge, so it shows on light and dark pictures alike
        TransformLayer.Children.Add(new Polygon { Points = outline, Stroke = BoxHalo, StrokeThickness = 3 });
        TransformLayer.Children.Add(new Polygon { Points = outline, Stroke = BoxLine, StrokeThickness = 1 });
        foreach (var (sx, sy) in new[] { (-1, -1), (0, -1), (1, -1), (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0) })
        {
            var p = At(sx, sy);
            var handle = new Rectangle
            {
                Width = HandleSize, Height = HandleSize, Fill = Brushes.White, Stroke = BoxLine, StrokeThickness = 1,
                RenderTransform = new RotateTransform(_angle, HandleSize / 2, HandleSize / 2),
            };
            Canvas.SetLeft(handle, p.X - HandleSize / 2);
            Canvas.SetTop(handle, p.Y - HandleSize / 2);
            TransformLayer.Children.Add(handle);
        }
        // the reference point in the middle
        var ring = new Ellipse { Width = 9, Height = 9, Stroke = BoxLine, StrokeThickness = 1, Fill = BoxHalo };
        Canvas.SetLeft(ring, c.X - 4.5);
        Canvas.SetTop(ring, c.Y - 4.5);
        TransformLayer.Children.Add(ring);
        TransformLayer.Children.Add(new Line { X1 = c.X - 7, Y1 = c.Y, X2 = c.X + 7, Y2 = c.Y, Stroke = BoxLine, StrokeThickness = 1 });
        TransformLayer.Children.Add(new Line { X1 = c.X, Y1 = c.Y - 7, X2 = c.X, Y2 = c.Y + 7, Stroke = BoxLine, StrokeThickness = 1 });
    }

    private void ShowReadout(Point screen, string text)
    {
        ReadoutText.Text = text;
        Readout.Visibility = Visibility.Visible;
        var box = _stage ?? new Rect(Left, Top, Width, Height);
        var local = new Point(screen.X - box.X, screen.Y - box.Y);
        Readout.Margin = new Thickness(Math.Clamp(local.X + 16, 0, Math.Max(0, box.Width - 90)), Math.Clamp(local.Y + 18, 0, Math.Max(0, box.Height - 40)), 0, 0);
    }

    // ---- cursors ---------------------------------------------------------------------------------

    /// <summary>The double-headed resize arrow nearest the handle's direction on screen.</summary>
    private static Cursor ResizeCursor(Vector direction)
    {
        double a = Math.Atan2(direction.Y, direction.X) * 180 / Math.PI;
        int step = (int)Math.Round(((a % 180) + 180) % 180 / 45) % 4;
        return step switch { 0 => Cursors.SizeWE, 1 => Cursors.SizeNWSE, 2 => Cursors.SizeNS, _ => Cursors.SizeNESW };
    }

    private static readonly Dictionary<int, Cursor> RotateCursors = new();

    /// <summary>Photoshop's curved double arrow, turned to face the middle from wherever the pointer is.</summary>
    internal static Cursor RotateCursor(Vector fromCentre)
    {
        double a = Math.Atan2(fromCentre.Y, fromCentre.X) * 180 / Math.PI;
        int step = ((int)Math.Round(a / 45) % 8 + 8) % 8;
        if (RotateCursors.TryGetValue(step, out var cached)) return cached;
        Cursor cursor;
        try { cursor = MakeRotateCursor(step * 45); }
        catch { cursor = Cursors.Hand; }
        RotateCursors[step] = cursor;
        return cursor;
    }

    private static Cursor MakeRotateCursor(double degrees)
    {
        const int S = 32;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new RotateTransform(degrees + 45, S / 2.0, S / 2.0));
            // a quarter circle bowing away from the middle, an arrowhead at each end
            var arc = new StreamGeometry();
            using (var g = arc.Open())
            {
                g.BeginFigure(new Point(9, 22), false, false);
                g.ArcTo(new Point(22, 9), new Size(14, 14), 0, false, SweepDirection.Clockwise, true, false);
                g.BeginFigure(new Point(5, 18), true, true);
                g.LineTo(new Point(9, 26), true, false);
                g.LineTo(new Point(13, 19), true, false);
                g.BeginFigure(new Point(18, 5), true, true);
                g.LineTo(new Point(26, 9), true, false);
                g.LineTo(new Point(19, 13), true, false);
            }
            arc.Freeze();
            dc.DrawGeometry(Brushes.White, new Pen(Brushes.White, 4) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, arc);
            dc.DrawGeometry(Brushes.Black, new Pen(Brushes.Black, 1.6) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, arc);
            dc.Pop();
        }
        var bmp = new RenderTargetBitmap(S, S, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var png = new MemoryStream();
        encoder.Save(png);

        // a .cur file holding the PNG, with the hotspot in the middle
        using var cur = new MemoryStream();
        using (var w = new BinaryWriter(cur, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            w.Write((short)0); w.Write((short)2); w.Write((short)1);
            w.Write((byte)S); w.Write((byte)S); w.Write((byte)0); w.Write((byte)0);
            w.Write((short)(S / 2)); w.Write((short)(S / 2));
            w.Write((int)png.Length); w.Write(22);
            w.Write(png.ToArray());
        }
        cur.Position = 0;
        return new Cursor(cur);
    }
}
