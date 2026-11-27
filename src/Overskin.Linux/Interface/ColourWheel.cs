using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Overskin.Linux.Core;

namespace Overskin.Linux.Interface;

/// <summary>
/// Any colour (as on Windows): hue around the edge, saturation from the centre out; brightness is
/// set separately. Click or drag to choose; <see cref="ColourChanged"/> fires as it moves.
/// </summary>
public sealed class ColourWheel : Control
{
    private Bitmap? _wheel;
    private int _wheelPixels;
    private bool _dragging;

    /// <summary>0–360.</summary>
    public double Hue { get; private set; }

    /// <summary>0–1.</summary>
    public double Saturation { get; private set; } = 1;

    public event Action? ColourChanged;

    public void SetHueSaturation(double hue, double saturation)
    {
        Hue = ((hue % 360) + 360) % 360;
        Saturation = Math.Clamp(saturation, 0, 1);
        InvalidateVisual();
    }

    public static Color FromHsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = (int)(h / 60) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    public static (double H, double S, double V) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0 : max == r ? 60 * ((g - b) / d % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        return ((h + 360) % 360, max == 0 ? 0 : d / max, max);
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>A wheel at full brightness, anti-aliased at the rim.</summary>
    public static Bitmap CreateBitmap(int size)
    {
        var px = new byte[size * size * 4];
        double c = (size - 1) / 2.0, radius = size / 2.0;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                double dx = x - c, dy = y - c;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                double alpha = Math.Clamp(radius - dist, 0, 1); // one-pixel soft edge
                if (alpha <= 0) continue;
                double hue = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360;
                var col = FromHsv(hue, Math.Min(1, dist / (radius - 0.5)), 1);
                int p = (y * size + x) * 4;
                px[p] = (byte)(col.B * alpha);
                px[p + 1] = (byte)(col.G * alpha);
                px[p + 2] = (byte)(col.R * alpha);
                px[p + 3] = (byte)(255 * alpha);
            }
        return Imaging.FromPixels(px, new PixelSize(size, size));
    }

    public override void Render(DrawingContext ctx)
    {
        double size = Math.Min(Bounds.Width, Bounds.Height);
        if (size < 4) return;
        // drawn at the screen's own resolution so the rim stays crisp
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        int pixels = (int)Math.Round(size * scale);
        if (_wheel == null || _wheelPixels != pixels)
        {
            _wheel = CreateBitmap(pixels);
            _wheelPixels = pixels;
        }
        double ox = (Bounds.Width - size) / 2, oy = (Bounds.Height - size) / 2;
        // the whole square takes clicks, not just the drawn pixels
        ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        ctx.DrawImage(_wheel, new Rect(_wheel.Size), new Rect(ox, oy, size, size));

        // the marker: a dark ring with a light ring inside, readable on any colour
        double r = size / 2;
        double angle = Hue * Math.PI / 180;
        var at = new Point(ox + r + Math.Cos(angle) * Saturation * (r - 1), oy + r + Math.Sin(angle) * Saturation * (r - 1));
        ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), 3), at, 6, 6);
        ctx.DrawEllipse(null, new Pen(Brushes.White, 1.6), at, 6, 6);
    }

    private void PickAt(Point p)
    {
        double size = Math.Min(Bounds.Width, Bounds.Height);
        double r = size / 2;
        double dx = p.X - Bounds.Width / 2, dy = p.Y - Bounds.Height / 2;
        Hue = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360;
        Saturation = Math.Clamp(Math.Sqrt(dx * dx + dy * dy) / Math.Max(1, r - 1), 0, 1);
        InvalidateVisual();
        ColourChanged?.Invoke();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragging = true;
        e.Pointer.Capture(this);
        PickAt(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_dragging) PickAt(e.GetPosition(this));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    /// <summary>For the self-test: a click at a point of the wheel.</summary>
    internal void PickForTest(Point p) => PickAt(p);
}
