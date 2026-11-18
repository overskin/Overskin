using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Overskin;

/// <summary>
/// Draws traced paths scaled to fit, with a constant one-pixel stroke at any size.
/// Colour traces are drawn in their own colours. Used for the in-app preview, the area
/// picker and the on-screen ghost.
/// </summary>
public sealed class PathPreview : FrameworkElement
{
    private readonly List<(Geometry Geometry, Brush? Brush)> _layers = new();
    private BitmapSource? _backdrop;
    private double _imageWidth, _imageHeight;
    private bool _thin;

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(PathPreview),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Line colour for single-colour traces. A dependency property so it can follow the theme.</summary>
    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double StrokeThickness { get; set; } = 1.0;

    /// <summary>When true the paths fill the element exactly instead of keeping their aspect ratio.</summary>
    public bool Stretch { get; set; }

    public void Set(TraceResult? result, bool showBackdrop)
    {
        _layers.Clear();
        _backdrop = null;

        if (result != null)
        {
            _imageWidth = result.Width;
            _imageHeight = result.Height;
            _thin = result.ThinPreview;
            if (showBackdrop) _backdrop = TraceEngine.BuildBackdropBitmap(result);

            if (result.ColourIndex != null && result.Colours != null)
            {
                foreach (int k in result.ColourIndex.Distinct())
                {
                    var brush = new SolidColorBrush(result.Colours[k]);
                    brush.Freeze();
                    _layers.Add((TraceEngine.BuildGeometry(result, k), brush));
                }
            }
            else
            {
                _layers.Add((TraceEngine.BuildGeometry(result), null));
            }
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_imageWidth <= 0 || _imageHeight <= 0) return;
        double w = ActualWidth, h = ActualHeight;
        if (w < 1 || h < 1) return;

        double sx = w / _imageWidth, sy = h / _imageHeight;
        if (!Stretch) sx = sy = Math.Min(sx, sy);
        double ox = (w - _imageWidth * sx) / 2, oy = (h - _imageHeight * sy) / 2;

        dc.PushTransform(new TranslateTransform(ox, oy));
        dc.PushTransform(new ScaleTransform(sx, sy));

        if (_backdrop != null)
            dc.DrawImage(_backdrop, new Rect(0, 0, _imageWidth, _imageHeight));

        double scale = Math.Max(1e-6, Math.Min(sx, sy));
        // hatching: one traced pixel wide (never under a third of a screen pixel), so tone shows as density
        double screenWidth = _thin ? Math.Clamp(scale, 0.35, StrokeThickness) : StrokeThickness;
        double thickness = screenWidth / scale;
        foreach (var (geometry, brush) in _layers)
        {
            var b = brush ?? (Stroke.IsFrozen ? Stroke : Stroke.Clone());
            var pen = new Pen(b, thickness)
            {
                LineJoin = PenLineJoin.Round,
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
            };
            pen.Freeze();
            dc.DrawGeometry(null, pen, geometry);
        }

        dc.Pop();
        dc.Pop();
    }
}
