using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Overskin;

/// <summary>
/// Draws one outline icon from Themes/Icons.xaml. The geometry lives on a 24 x 24 grid and is
/// scaled to the element, while the stroke stays the same thickness at every size.
/// The colour is inherited like text colour, so an icon inside a button follows the button.
/// </summary>
public sealed class Icon : FrameworkElement
{
    private const double Grid = 24;

    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(Geometry), typeof(Icon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(Icon),
        new FrameworkPropertyMetadata(Brushes.White,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.Inherits));

    public static readonly DependencyProperty StrokeWidthProperty = DependencyProperty.Register(
        nameof(StrokeWidth), typeof(double), typeof(Icon),
        new FrameworkPropertyMetadata(1.35, FrameworkPropertyMetadataOptions.AffectsRender));

    public Geometry? Data
    {
        get => (Geometry?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>Stroke thickness in screen units, independent of the icon's size.</summary>
    public double StrokeWidth
    {
        get => (double)GetValue(StrokeWidthProperty);
        set => SetValue(StrokeWidthProperty, value);
    }

    static Icon()
    {
        WidthProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(15.0));
        HeightProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(15.0));
        VerticalAlignmentProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(VerticalAlignment.Center));
        IsHitTestVisibleProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(false));
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Data == null) return;
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size < 1) return;

        double scale = size / Grid;
        var brush = Foreground;
        var pen = new Pen(brush.IsFrozen ? brush : brush.CloneCurrentValue(), StrokeWidth / scale)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };

        dc.PushTransform(new TranslateTransform((ActualWidth - size) / 2, (ActualHeight - size) / 2));
        dc.PushTransform(new ScaleTransform(scale, scale));
        dc.DrawGeometry(null, pen, Data);
        dc.Pop();
        dc.Pop();
    }
}
