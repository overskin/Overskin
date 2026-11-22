using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Overskin;

/// <summary>
/// A crop box over a picture or video shown in a Canvas: drag to draw one, drag inside to move it,
/// drag an edge or corner to resize it, optionally held to a shape (16:9, 1:1…). Outside the box
/// is dimmed. While not being edited it still shows what will be kept, without handles.
/// Everything is kept in the source's own pixels.
/// </summary>
public sealed class CropBox
{
    private readonly Canvas _canvas;
    private readonly Func<(double Scale, double X, double Y)> _fit;
    private readonly Func<Size> _source;
    private Rect? _box;                    // in source pixels
    private bool _active;
    private int _grab;                     // which part is held: 0 none, 1 drawing, 2 moving, 10+ edges (bit flags + 10)
    private Point _from;
    private Rect _start;

    private const int Left = 1, Top = 2, Right = 4, Bottom = 8;

    /// <summary>Width over height to hold the box to; 0 for any shape.</summary>
    public double Ratio { get; private set; }

    /// <summary>Round the box to even sizes and positions (videos need them).</summary>
    public bool Even { get; init; }

    public event Action? Changed;

    public CropBox(Canvas canvas, Func<(double Scale, double X, double Y)> fit, Func<Size> source)
    {
        _canvas = canvas;
        _fit = fit;
        _source = source;
        canvas.Background = Brushes.Transparent;
        canvas.IsHitTestVisible = false;
        canvas.MouseLeftButtonDown += Down;
        canvas.MouseMove += Move;
        canvas.MouseLeftButtonUp += Up;
        canvas.SizeChanged += (_, _) => Draw();
    }

    /// <summary>The area kept, in source pixels; null when nothing is cropped.</summary>
    public Int32Rect? Crop
    {
        get
        {
            if (_box is not { } b) return null;
            var src = _source();
            int x = (int)Math.Round(b.X), y = (int)Math.Round(b.Y);
            int w = (int)Math.Round(b.Width), h = (int)Math.Round(b.Height);
            if (Even) { x &= ~1; y &= ~1; w &= ~1; h &= ~1; }
            x = Math.Clamp(x, 0, (int)src.Width - 2);
            y = Math.Clamp(y, 0, (int)src.Height - 2);
            w = Math.Clamp(w, 2, (int)src.Width - x);
            h = Math.Clamp(h, 2, (int)src.Height - y);
            if (Even) { w &= ~1; h &= ~1; }
            if (w >= src.Width - 1 && h >= src.Height - 1) return null; // the whole thing
            return new Int32Rect(x, y, w, h);
        }
    }

    /// <summary>True while the box can be drawn and moved (the canvas takes the mouse).</summary>
    public bool Active
    {
        get => _active;
        set
        {
            _active = value;
            _canvas.IsHitTestVisible = value;
            _canvas.Cursor = value ? Cursors.Cross : null;
            Draw();
        }
    }

    public void Reset()
    {
        _box = null;
        Draw();
        Changed?.Invoke();
    }

    /// <summary>Holds the box to a shape; with no box yet, the largest box of that shape, centred.</summary>
    public void SetRatio(double ratio)
    {
        Ratio = ratio;
        if (ratio > 0)
        {
            var src = _source();
            var within = _box ?? new Rect(0, 0, src.Width, src.Height);
            double w = within.Width, h = w / ratio;
            if (h > within.Height) { h = within.Height; w = h * ratio; }
            _box = new Rect(within.X + (within.Width - w) / 2, within.Y + (within.Height - h) / 2, w, h);
        }
        Draw();
        Changed?.Invoke();
    }

    /// <summary>For the self-test: sets the box directly, in source pixels.</summary>
    internal void SetForTest(Rect box)
    {
        _box = box;
        Draw();
        Changed?.Invoke();
    }

    // ---- mouse -----------------------------------------------------------------------------

    private Point ToSource(Point canvasPoint)
    {
        var (s, x, y) = _fit();
        return s <= 0 ? canvasPoint : new Point((canvasPoint.X - x) / s, (canvasPoint.Y - y) / s);
    }

    /// <summary>Which edges are under the pointer (within a few screen pixels), or inside, or nothing.</summary>
    private int HitTest(Point source)
    {
        if (_box is not { } b) return 0;
        double reach = 7 / Math.Max(0.01, _fit().Scale);
        bool nearX = source.X > b.Left - reach && source.X < b.Right + reach, nearY = source.Y > b.Top - reach && source.Y < b.Bottom + reach;
        int edges = 0;
        if (nearY && Math.Abs(source.X - b.Left) <= reach) edges |= Left;
        if (nearY && Math.Abs(source.X - b.Right) <= reach) edges |= Right;
        if (nearX && Math.Abs(source.Y - b.Top) <= reach) edges |= Top;
        if (nearX && Math.Abs(source.Y - b.Bottom) <= reach) edges |= Bottom;
        if (edges != 0) return 10 + edges;
        return b.Contains(source) ? 2 : 0;
    }

    private void Down(object sender, MouseButtonEventArgs e)
    {
        if (!_active) return;
        var p = Clamp(ToSource(e.GetPosition(_canvas)));
        _grab = HitTest(p);
        if (_grab == 0)
        {
            _grab = 1;
            _box = new Rect(p, p);
        }
        _from = p;
        _start = _box ?? Rect.Empty;
        _canvas.CaptureMouse();
        e.Handled = true;
    }

    private void Move(object sender, MouseEventArgs e)
    {
        if (!_active) return;
        var p = Clamp(ToSource(e.GetPosition(_canvas)));
        if (_grab == 0)
        {
            _canvas.Cursor = HitTest(p) switch
            {
                2 => Cursors.SizeAll,
                10 + Left or 10 + Right => Cursors.SizeWE,
                10 + Top or 10 + Bottom => Cursors.SizeNS,
                10 + (Left | Top) or 10 + (Right | Bottom) => Cursors.SizeNWSE,
                10 + (Right | Top) or 10 + (Left | Bottom) => Cursors.SizeNESW,
                _ => Cursors.Cross,
            };
            return;
        }
        var src = _source();
        if (_grab == 1)
        {
            _box = Shaped(_from, p, src);
        }
        else if (_grab == 2)
        {
            var d = p - _from;
            double x = Math.Clamp(_start.X + d.X, 0, src.Width - _start.Width), y = Math.Clamp(_start.Y + d.Y, 0, src.Height - _start.Height);
            _box = new Rect(x, y, _start.Width, _start.Height);
        }
        else
        {
            int edges = _grab - 10;
            double l = _start.Left, t = _start.Top, r = _start.Right, b = _start.Bottom;
            if ((edges & Left) != 0) l = Math.Min(p.X, r - 4);
            if ((edges & Right) != 0) r = Math.Max(p.X, l + 4);
            if ((edges & Top) != 0) t = Math.Min(p.Y, b - 4);
            if ((edges & Bottom) != 0) b = Math.Max(p.Y, t + 4);
            if (Ratio > 0)
            {
                // the shape holds: a side edge sets the width, a top or bottom edge the height, a corner whichever moved more
                bool byWidth = (edges & (Left | Right)) != 0 && ((edges & (Top | Bottom)) == 0 || Math.Abs(r - l - _start.Width) >= Math.Abs(b - t - _start.Height));
                double w = r - l, h = b - t;
                if (byWidth) h = w / Ratio; else w = h * Ratio;
                if ((edges & (Left | Right)) == 0) { double cx = (_start.Left + _start.Right) / 2; l = cx - w / 2; r = cx + w / 2; }
                else if ((edges & Left) != 0) l = r - w; else r = l + w;
                if ((edges & (Top | Bottom)) == 0) { double cy = (_start.Top + _start.Bottom) / 2; t = cy - h / 2; b = cy + h / 2; }
                else if ((edges & Top) != 0) t = b - h; else b = t + h;
                if (l < 0 || t < 0 || r > src.Width || b > src.Height) return; // no room to grow that way
            }
            _box = new Rect(new Point(l, t), new Point(r, b));
        }
        Draw();
        Changed?.Invoke();
    }

    private void Up(object sender, MouseButtonEventArgs e)
    {
        if (_grab == 0) return;
        _grab = 0;
        _canvas.ReleaseMouseCapture();
        // a click without a drag leaves nothing cropped
        if (_box is { } b && (b.Width < 8 || b.Height < 8)) _box = null;
        Draw();
        Changed?.Invoke();
    }

    private Point Clamp(Point p)
    {
        var src = _source();
        return new Point(Math.Clamp(p.X, 0, src.Width), Math.Clamp(p.Y, 0, src.Height));
    }

    /// <summary>A box dragged from <paramref name="a"/> to <paramref name="b"/>, held to the shape and kept inside.</summary>
    private Rect Shaped(Point a, Point b, Size src)
    {
        if (Ratio <= 0) return new Rect(a, b);
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double w = Math.Abs(dx), h = Math.Abs(dy);
        if (w / Math.Max(0.01, h) > Ratio) h = w / Ratio; else w = h * Ratio;
        // kept inside the source
        double maxW = dx >= 0 ? src.Width - a.X : a.X, maxH = dy >= 0 ? src.Height - a.Y : a.Y;
        if (w > maxW) { w = maxW; h = w / Ratio; }
        if (h > maxH) { h = maxH; w = h * Ratio; }
        return new Rect(new Point(a.X, a.Y), new Point(a.X + (dx < 0 ? -w : w), a.Y + (dy < 0 ? -h : h)));
    }

    // ---- drawing -----------------------------------------------------------------------------

    public void Draw()
    {
        _canvas.Children.Clear();
        var (s, ox, oy) = _fit();
        if (s <= 0 || _box is not { } b) return;
        var src = _source();
        var whole = new Rect(ox, oy, src.Width * s, src.Height * s);
        var box = new Rect(ox + b.X * s, oy + b.Y * s, b.Width * s, b.Height * s);

        var dim = new Path
        {
            Data = new GeometryGroup { FillRule = FillRule.EvenOdd, Children = { new RectangleGeometry(whole), new RectangleGeometry(box) } },
            Fill = new SolidColorBrush(Color.FromArgb(_active ? (byte)150 : (byte)190, 0, 0, 0)),
            IsHitTestVisible = false,
        };
        _canvas.Children.Add(dim);
        var frame = new Rectangle { Width = box.Width, Height = box.Height, Stroke = Brushes.White, StrokeThickness = 1.5, IsHitTestVisible = false };
        Canvas.SetLeft(frame, box.X);
        Canvas.SetTop(frame, box.Y);
        _canvas.Children.Add(frame);
        if (!_active) return;

        // thirds, to line things up
        for (int i = 1; i <= 2; i++)
        {
            _canvas.Children.Add(Line(box.X + box.Width * i / 3, box.Y, box.X + box.Width * i / 3, box.Bottom));
            _canvas.Children.Add(Line(box.X, box.Y + box.Height * i / 3, box.Right, box.Y + box.Height * i / 3));
        }
        // handles at the corners and the middle of each side
        foreach (var (hx, hy) in new[]
                 {
                     (box.Left, box.Top), (box.Right, box.Top), (box.Left, box.Bottom), (box.Right, box.Bottom),
                     ((box.Left + box.Right) / 2, box.Top), ((box.Left + box.Right) / 2, box.Bottom),
                     (box.Left, (box.Top + box.Bottom) / 2), (box.Right, (box.Top + box.Bottom) / 2),
                 })
        {
            var handle = new Rectangle { Width = 9, Height = 9, Fill = Brushes.White, Stroke = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), StrokeThickness = 1, IsHitTestVisible = false };
            Canvas.SetLeft(handle, hx - 4.5);
            Canvas.SetTop(handle, hy - 4.5);
            _canvas.Children.Add(handle);
        }
        if (Crop is { } c)
        {
            var label = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 2, 6, 3),
                Child = new TextBlock { Text = $"{c.Width} × {c.Height}", Foreground = Brushes.White, FontSize = 11 },
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(label, box.X);
            Canvas.SetTop(label, box.Y >= 24 ? box.Y - 22 : box.Y + 4);
            _canvas.Children.Add(label);
        }
    }

    private static Line Line(double x1, double y1, double x2, double y2) => new()
    {
        X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), StrokeThickness = 1, IsHitTestVisible = false,
    };
}
