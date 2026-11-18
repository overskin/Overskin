using System.Windows;

namespace Overskin;

/// <summary>
/// The "auto-zoom" camera for screen recordings: it glides in towards each click, follows the
/// pointer while you work, and eases back out after a few quiet seconds. The pointer itself is
/// smoothed, so small shakes and jumps turn into calm, steady movement.
/// Everything is in pixels of the recorded area.
/// </summary>
public sealed class SmartCamera
{
    public double ZoomIn { get; init; } = 1.8;
    /// <summary>Seconds without a click before zooming back out.</summary>
    public double Linger { get; init; } = 2.6;
    /// <summary>How quickly the zoom changes (higher is faster); zooming out runs at 60% of it.</summary>
    public double ZoomRate { get; init; } = 5.5;
    /// <summary>How quickly the view follows the pointer.</summary>
    public double PanRate { get; init; } = 4.5;
    /// <summary>How quickly the drawn pointer catches up with the real one; 0 for no smoothing.</summary>
    public double PointerRate { get; init; } = 16;

    private readonly double _w, _h;
    private double _zoom = 1, _targetZoom = 1;
    private Point _centre, _target, _cursor;
    private bool _hasCursor;
    private double _sinceClick = double.MaxValue;

    public SmartCamera(double width, double height)
    {
        _w = width;
        _h = height;
        _centre = _target = new Point(width / 2, height / 2);
    }

    public double Zoom => _zoom;
    /// <summary>The smoothed pointer.</summary>
    public Point Cursor => _cursor;

    /// <summary>Advances by <paramref name="dt"/> seconds with the real pointer at <paramref name="pointer"/>.</summary>
    public void Update(double dt, Point pointer, bool clicked)
    {
        if (!_hasCursor)
        {
            _cursor = pointer;
            _hasCursor = true;
        }
        // pointer: quick but steady (removes jitter without feeling laggy)
        _cursor = PointerRate <= 0 ? pointer : Approach(_cursor, pointer, dt, PointerRate);

        bool inside = pointer.X >= 0 && pointer.Y >= 0 && pointer.X < _w && pointer.Y < _h;
        _sinceClick = clicked && inside ? 0 : _sinceClick + dt;
        _targetZoom = _sinceClick < Linger && ZoomIn > 1 ? ZoomIn : 1;

        // while zoomed, keep the pointer comfortably inside the view rather than glued to the centre
        if (_targetZoom > 1)
        {
            double viewW = _w / _targetZoom, viewH = _h / _targetZoom;
            double marginX = viewW * 0.28, marginY = viewH * 0.28;
            double x = _target.X, y = _target.Y;
            if (clicked && inside) (x, y) = (pointer.X, pointer.Y);
            if (_cursor.X < x - viewW / 2 + marginX) x = _cursor.X + viewW / 2 - marginX;
            if (_cursor.X > x + viewW / 2 - marginX) x = _cursor.X - viewW / 2 + marginX;
            if (_cursor.Y < y - viewH / 2 + marginY) y = _cursor.Y + viewH / 2 - marginY;
            if (_cursor.Y > y + viewH / 2 - marginY) y = _cursor.Y - viewH / 2 + marginY;
            _target = new Point(x, y);
        }
        else
        {
            _target = new Point(_w / 2, _h / 2);
        }

        _zoom += (_targetZoom - _zoom) * Ease(dt, _targetZoom > _zoom ? ZoomRate : ZoomRate * 0.6);
        _centre = Approach(_centre, _target, dt, PanRate);
    }

    /// <summary>The part of the recorded area to show, kept inside it.</summary>
    public Rect View()
    {
        double vw = _w / _zoom, vh = _h / _zoom;
        double x = Math.Clamp(_centre.X - vw / 2, 0, _w - vw);
        double y = Math.Clamp(_centre.Y - vh / 2, 0, _h - vh);
        return new Rect(x, y, vw, vh);
    }

    /// <summary>Frame-rate independent exponential smoothing factor.</summary>
    private static double Ease(double dt, double rate) => 1 - Math.Exp(-dt * rate);

    private static Point Approach(Point from, Point to, double dt, double rate)
    {
        double k = Ease(dt, rate);
        return new Point(from.X + (to.X - from.X) * k, from.Y + (to.Y - from.Y) * k);
    }
}
