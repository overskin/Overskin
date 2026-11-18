using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Overskin;

public enum AnnotationKind { Pen, Highlighter, Line, Arrow, Rectangle, Ellipse, Text, Step, Pixelate }

/// <summary>
/// One mark on a screenshot, in image pixels. Kept as plain data so undo is just a copy of the
/// list, and turned into WPF shapes whenever the picture is redrawn.
/// </summary>
public sealed class Annotation
{
    public AnnotationKind Kind { get; init; }
    /// <summary>Pen and highlighter: every point. Shapes: two corners. Text and steps: one anchor.</summary>
    public List<Point> Points { get; init; } = new();
    public Color Color { get; set; } = Colors.Red;
    public double Size { get; set; } = 3;
    public string Text { get; set; } = "";
    public int Number { get; set; }
    /// <summary>Pixelate: the blocky picture of what was underneath.</summary>
    public BitmapSource? Pixels { get; set; }
    /// <summary>Boxes and ellipses: solid instead of outlined.</summary>
    public bool Filled { get; set; }
    /// <summary>Hidden areas: blurred, pixelated or mosaic.</summary>
    public HideMode Mode { get; set; } = HideMode.Pixelate;

    public Annotation Clone() => new()
    {
        Kind = Kind,
        Points = new List<Point>(Points),
        Color = Color,
        Size = Size,
        Text = Text,
        Number = Number,
        Pixels = Pixels,
        Filled = Filled,
        Mode = Mode,
    };

    public void Translate(double dx, double dy)
    {
        for (int i = 0; i < Points.Count; i++) Points[i] = new Point(Points[i].X + dx, Points[i].Y + dy);
    }

    public double FontSize => 12 + Size * 3.5;
    public double StepDiameter => 22 + Size * 2.5;

    /// <summary>Rough area the mark covers, for clicking on it and for the selection box.</summary>
    public Rect Bounds()
    {
        if (Points.Count == 0) return Rect.Empty;
        switch (Kind)
        {
            case AnnotationKind.Text:
            {
                var formatted = Measure();
                return new Rect(Points[0], new Size(formatted.Width, formatted.Height));
            }
            case AnnotationKind.Step:
            {
                double r = StepDiameter / 2;
                return new Rect(Points[0].X - r, Points[0].Y - r, r * 2, r * 2);
            }
        }
        double minX = Points.Min(p => p.X), minY = Points.Min(p => p.Y);
        double maxX = Points.Max(p => p.X), maxY = Points.Max(p => p.Y);
        var rect = new Rect(minX, minY, maxX - minX, maxY - minY);
        double pad = Kind == AnnotationKind.Highlighter ? Size * 2 : Kind == AnnotationKind.Arrow ? Size * 3 : Size / 2;
        rect.Inflate(pad, pad);
        return rect;
    }

    private Size Measure()
    {
        var tb = new TextBlock { Text = Text.Length == 0 ? " " : Text, FontSize = FontSize, FontWeight = FontWeights.SemiBold };
        tb.FontFamily = (FontFamily)Application.Current.Resources["UiFont"];
        tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return tb.DesiredSize;
    }

    private static Rect Span(Point a, Point b) => new(a, b);

    /// <summary>Text that stays readable on any background: dark or light halo depending on the colour.</summary>
    private static Effect Halo(Color c) => new DropShadowEffect
    {
        Color = TraceEngine.Luma(c) > 140 ? Colors.Black : Colors.White,
        BlurRadius = 4,
        ShadowDepth = 0,
        Opacity = 0.75,
    };

    public UIElement ToElement()
    {
        var brush = new SolidColorBrush(Color);
        brush.Freeze();
        switch (Kind)
        {
            case AnnotationKind.Pen:
            case AnnotationKind.Highlighter:
            {
                bool hl = Kind == AnnotationKind.Highlighter;
                return new Polyline
                {
                    Points = new PointCollection(Points),
                    Stroke = brush,
                    StrokeThickness = hl ? Size * 4 : Size,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = hl ? PenLineCap.Square : PenLineCap.Round,
                    StrokeEndLineCap = hl ? PenLineCap.Square : PenLineCap.Round,
                    Opacity = hl ? 0.4 : 1,
                };
            }
            case AnnotationKind.Line:
                return new Line
                {
                    X1 = Points[0].X, Y1 = Points[0].Y, X2 = Points[^1].X, Y2 = Points[^1].Y,
                    Stroke = brush, StrokeThickness = Size, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                };
            case AnnotationKind.Arrow:
                return new Path
                {
                    Data = ArrowGeometry(Points[0], Points[^1], Size),
                    Fill = brush, Stroke = brush, StrokeThickness = Size,
                    StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                };
            case AnnotationKind.Rectangle:
            case AnnotationKind.Ellipse:
            {
                var r = Span(Points[0], Points[^1]);
                Shape shape = Kind == AnnotationKind.Rectangle
                    ? new Rectangle { RadiusX = Size * 0.6, RadiusY = Size * 0.6 }
                    : new Ellipse();
                shape.Width = Math.Max(1, r.Width);
                shape.Height = Math.Max(1, r.Height);
                shape.Stroke = brush;
                shape.StrokeThickness = Size;
                if (Filled) shape.Fill = brush;
                Canvas.SetLeft(shape, r.X);
                Canvas.SetTop(shape, r.Y);
                return shape;
            }
            case AnnotationKind.Text:
            {
                var tb = new TextBlock
                {
                    Text = Text, Foreground = brush, FontSize = FontSize, FontWeight = FontWeights.SemiBold,
                    FontFamily = (FontFamily)Application.Current.Resources["UiFont"], Effect = Halo(Color),
                };
                Canvas.SetLeft(tb, Points[0].X);
                Canvas.SetTop(tb, Points[0].Y);
                return tb;
            }
            case AnnotationKind.Step:
            {
                double d = StepDiameter;
                var grid = new Grid { Width = d, Height = d };
                grid.Children.Add(new Ellipse { Fill = brush, Stroke = new SolidColorBrush(Color.FromArgb(0x60, 0, 0, 0)), StrokeThickness = 1 });
                grid.Children.Add(new TextBlock
                {
                    Text = Number.ToString(),
                    Foreground = TraceEngine.Luma(Color) > 160 ? Brushes.Black : Brushes.White,
                    FontSize = d * 0.52, FontWeight = FontWeights.Bold,
                    FontFamily = (FontFamily)Application.Current.Resources["UiFont"],
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, d * 0.04),
                });
                Canvas.SetLeft(grid, Points[0].X - d / 2);
                Canvas.SetTop(grid, Points[0].Y - d / 2);
                return grid;
            }
            case AnnotationKind.Pixelate:
            {
                var r = Span(Points[0], Points[^1]);
                var img = new Image { Source = Pixels, Width = r.Width, Height = r.Height, Stretch = Stretch.Fill };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                Canvas.SetLeft(img, r.X);
                Canvas.SetTop(img, r.Y);
                return img;
            }
        }
        return new Canvas();
    }

    /// <summary>Shaft plus a solid head, sized from the line thickness.</summary>
    public static Geometry ArrowGeometry(Point from, Point to, double size)
    {
        var v = to - from;
        double len = v.Length;
        if (len < 0.5) return Geometry.Empty;
        v /= len;
        var n = new Vector(-v.Y, v.X);
        double head = Math.Min(len, Math.Max(10, size * 4.5));
        double width = head * 0.62;
        var baseCentre = to - v * head;

        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(from, false, false);
            ctx.LineTo(baseCentre + v * (size * 0.5), true, true);
            ctx.BeginFigure(to, true, true);
            ctx.LineTo(baseCentre + n * width, true, true);
            ctx.LineTo(baseCentre - n * width, true, true);
        }
        g.Freeze();
        return g;
    }

    /// <summary>Blocky copy of part of a picture, for hiding details.</summary>
    public static BitmapSource? Pixelated(BitmapSource image, Rect area, double size)
    {
        int x = Math.Clamp((int)Math.Floor(area.X), 0, image.PixelWidth - 1);
        int y = Math.Clamp((int)Math.Floor(area.Y), 0, image.PixelHeight - 1);
        int w = Math.Clamp((int)Math.Ceiling(area.Right) - x, 1, image.PixelWidth - x);
        int h = Math.Clamp((int)Math.Ceiling(area.Bottom) - y, 1, image.PixelHeight - y);
        if (w < 2 || h < 2) return null;

        double block = Math.Max(5, size * 3);
        double k = 1 / block;
        var crop = new CroppedBitmap(image, new Int32Rect(x, y, w, h));
        var small = new TransformedBitmap(crop, new ScaleTransform(Math.Max(k, 1.0 / w), Math.Max(k, 1.0 / h)));
        var frozen = ImageLoader.Normalize(small);
        return frozen;
    }
}
