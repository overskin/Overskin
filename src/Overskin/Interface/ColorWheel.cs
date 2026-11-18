using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Overskin;

/// <summary>
/// Hue around the edge, saturation from the centre out. Brightness is set separately.
/// Click or drag to choose; <see cref="ColorChanged"/> fires as it moves.
/// </summary>
public sealed class ColorWheel : FrameworkElement
{
    private WriteableBitmap? _wheel;
    private int _wheelPixels;
    private bool _dragging;

    /// <summary>0–360.</summary>
    public double Hue { get; private set; }

    /// <summary>0–1.</summary>
    public double Saturation { get; private set; } = 1;

    public event Action? ColorChanged;

    static ColorWheel()
    {
        FocusableProperty.OverrideMetadata(typeof(ColorWheel), new FrameworkPropertyMetadata(false));
    }

    public void SetHueSaturation(double hue, double saturation)
    {
        Hue = ((hue % 360) + 360) % 360;
        Saturation = Math.Clamp(saturation, 0, 1);
        InvalidateVisual();
    }

    /// <summary>Renders a wheel at full brightness, anti-aliased at the rim.</summary>
    public static WriteableBitmap CreateBitmap(int size)
    {
        var bmp = new WriteableBitmap(size, size, 96, 96, PixelFormats.Pbgra32, null);
        var px = new byte[size * size * 4];
        double c = (size - 1) / 2.0, radius = size / 2.0;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double dx = x - c, dy = y - c;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                double alpha = Math.Clamp(radius - dist, 0, 1); // one-pixel soft edge
                if (alpha <= 0) continue;

                double hue = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360;
                var col = Theming.FromHsv(hue, Math.Min(1, dist / (radius - 0.5)), 1);
                int p = (y * size + x) * 4;
                px[p] = (byte)(col.B * alpha);
                px[p + 1] = (byte)(col.G * alpha);
                px[p + 2] = (byte)(col.R * alpha);
                px[p + 3] = (byte)(255 * alpha);
            }
        }
        bmp.WritePixels(new Int32Rect(0, 0, size, size), px, size * 4, 0);
        bmp.Freeze();
        return bmp;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size < 4) return;

        // render the wheel at device resolution so the rim stays crisp
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int pixels = (int)Math.Round(size * scale);
        if (_wheel == null || _wheelPixels != pixels)
        {
            _wheel = CreateBitmap(pixels);
            _wheelPixels = pixels;
        }

        double ox = (ActualWidth - size) / 2, oy = (ActualHeight - size) / 2;
        dc.DrawImage(_wheel, new Rect(ox, oy, size, size));

        // marker: dark ring with a light ring inside, readable on any colour
        double r = size / 2;
        double angle = Hue * Math.PI / 180;
        var at = new Point(ox + r + Math.Cos(angle) * Saturation * (r - 1), oy + r + Math.Sin(angle) * Saturation * (r - 1));
        dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), 3), at, 6, 6);
        dc.DrawEllipse(null, new Pen(Brushes.White, 1.6), at, 6, 6);
    }

    private void PickAt(Point p)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        double r = size / 2;
        double dx = p.X - ActualWidth / 2, dy = p.Y - ActualHeight / 2;
        Hue = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360;
        Saturation = Math.Clamp(Math.Sqrt(dx * dx + dy * dy) / (r - 1), 0, 1);
        InvalidateVisual();
        ColorChanged?.Invoke();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _dragging = true;
        CaptureMouse();
        PickAt(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) PickAt(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override HitTestResult? HitTestCore(PointHitTestParameters p) =>
        new PointHitTestResult(this, p.HitPoint); // whole square is clickable, not just drawn pixels
}
