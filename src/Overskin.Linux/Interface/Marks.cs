using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Overskin.Linux.Interface;

public enum Tool { Select, Move, Pen, Highlighter, Arrow, Line, Rectangle, Ellipse, Text, Hide, Counter, Erase, Crop, Spotlight }

/// <summary>Something drawn on the picture. Drawn in picture pixels.</summary>
public abstract class Mark
{
    public Color Colour { get; init; }
    public double Width { get; init; } = 4;
    public abstract void Draw(DrawingContext ctx);

    /// <summary>The box the mark takes up, for picking it with the Select tool.</summary>
    public abstract Rect Bounds { get; }

    /// <summary>A copy moved by <paramref name="d"/> (marks are never changed in place, so undo keeps the old one).</summary>
    public abstract Mark Moved(Vector d);

    /// <summary>Where the mark can be stretched from: line ends, or box corners (top-left, top-right, bottom-right, bottom-left).</summary>
    public virtual IReadOnlyList<Point> Handles => Array.Empty<Point>();

    /// <summary>A copy with handle <paramref name="handle"/> dragged to <paramref name="p"/>.</summary>
    public virtual Mark WithHandle(int handle, Point p) => this;

    /// <summary>A copy in another colour and thickness (marks that have them).</summary>
    public virtual Mark Restyled(Color colour, double width) => this;

    /// <summary>The four corners of a box, in handle order.</summary>
    protected static IReadOnlyList<Point> Corners(Rect r) => new[] { r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft };

    /// <summary>A box with one corner dragged (the opposite corner stays).</summary>
    protected static Rect DragCorner(Rect r, int handle, Point p) => handle switch
    {
        0 => new Rect(p, r.BottomRight).Normalize(),
        1 => new Rect(new Point(r.Left, p.Y), new Point(p.X, r.Bottom)).Normalize(),
        2 => new Rect(r.TopLeft, p).Normalize(),
        _ => new Rect(new Point(p.X, r.Top), new Point(r.Right, p.Y)).Normalize(),
    };

    protected Pen Stroke(double opacity = 1) => new(new SolidColorBrush(Colour, opacity), Width)
    {
        LineCap = PenLineCap.Round,
        LineJoin = PenLineJoin.Round,
    };
}

public sealed class StrokeMark : Mark
{
    public List<Point> Points { get; init; } = new();
    public bool Highlighter { get; init; }

    public override Rect Bounds
    {
        get
        {
            if (Points.Count == 0) return default;
            double x0 = Points.Min(p => p.X), y0 = Points.Min(p => p.Y), x1 = Points.Max(p => p.X), y1 = Points.Max(p => p.Y);
            double pad = (Highlighter ? Width * 4 : Width) / 2;
            return new Rect(x0 - pad, y0 - pad, x1 - x0 + 2 * pad, y1 - y0 + 2 * pad);
        }
    }

    public override Mark Moved(Vector d) => new StrokeMark { Colour = Colour, Width = Width, Highlighter = Highlighter, Points = Points.Select(p => p + d).ToList() };
    public override Mark Restyled(Color colour, double width) => new StrokeMark { Colour = colour, Width = width, Highlighter = Highlighter, Points = Points.ToList() };

    public override void Draw(DrawingContext ctx)
    {
        if (Points.Count == 0) return;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(Points[0], false);
            if (Points.Count == 1) g.LineTo(Points[0] + new Point(0.01, 0.01));
            foreach (var p in Points.Skip(1)) g.LineTo(p);
            g.EndFigure(false);
        }
        var pen = Highlighter
            ? new Pen(new SolidColorBrush(Colour, 0.4), Width * 4) { LineCap = PenLineCap.Square, LineJoin = PenLineJoin.Round }
            : Stroke();
        ctx.DrawGeometry(null, pen, geometry);
    }
}

public sealed class ShapeMark : Mark
{
    public Tool Kind { get; init; }
    public Point From { get; set; }
    public Point To { get; set; }
    /// <summary>Boxes and circles filled with the colour, not just outlined.</summary>
    public bool Filled { get; init; }

    private bool IsLine => Kind is Tool.Arrow or Tool.Line;

    public override Rect Bounds => new Rect(From, To).Normalize().Inflate(IsLine ? Width * 2 + 6 : Width / 2);
    public override IReadOnlyList<Point> Handles => IsLine ? new[] { From, To } : Corners(new Rect(From, To).Normalize());

    private ShapeMark With(Point from, Point to, Color? colour = null, double? width = null, bool? filled = null) =>
        new() { Kind = Kind, From = from, To = to, Colour = colour ?? Colour, Width = width ?? Width, Filled = filled ?? Filled };

    public override Mark Moved(Vector d) => With(From + d, To + d);
    public override Mark Restyled(Color colour, double width) => With(From, To, colour, width);
    public ShapeMark WithFilled(bool filled) => With(From, To, filled: filled);

    public override Mark WithHandle(int handle, Point p)
    {
        if (IsLine) return handle == 0 ? With(p, To) : With(From, p);
        var r = DragCorner(new Rect(From, To).Normalize(), handle, p);
        return With(r.TopLeft, r.BottomRight);
    }

    public override void Draw(DrawingContext ctx)
    {
        var rect = new Rect(From, To).Normalize();
        var fill = Filled ? new SolidColorBrush(Colour) : null;
        switch (Kind)
        {
            case Tool.Rectangle:
                ctx.DrawRectangle(fill, Stroke(), rect, Width, Width);
                break;
            case Tool.Ellipse:
                ctx.DrawEllipse(fill, Stroke(), rect.Center, rect.Width / 2, rect.Height / 2);
                break;
            case Tool.Arrow:
                DrawArrow(ctx);
                break;
            case Tool.Line:
                ctx.DrawLine(Stroke(), From, To);
                break;
        }
    }

    private void DrawArrow(DrawingContext ctx)
    {
        var d = To - From;
        double length = Math.Sqrt(d.X * d.X + d.Y * d.Y);
        if (length < 1) return;
        var unit = new Point(d.X / length, d.Y / length);
        var normal = new Point(-unit.Y, unit.X);
        double head = Math.Min(length * 0.6, Width * 4 + 10);

        var baseCentre = To - unit * head;
        ctx.DrawLine(Stroke(), From, To - unit * (head * 0.6));
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(To, true);
            g.LineTo(baseCentre + normal * (head * 0.55));
            g.LineTo(baseCentre - normal * (head * 0.55));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(new SolidColorBrush(Colour), new Pen(new SolidColorBrush(Colour), 1) { LineJoin = PenLineJoin.Round }, geometry);
    }
}

public sealed class TextMark : Mark
{
    public Point At { get; init; }
    public string Text { get; init; } = "";
    public double Size { get; init; } = 24;
    /// <summary>The font's name (see FontChoices); Inter when empty.</summary>
    public string FontName { get; init; } = "Inter";

    public static readonly Typeface Font = new("Inter", FontStyle.Normal, FontWeight.Bold);

    private FormattedText Formatted(IBrush brush) =>
        new(Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Overskin.Linux.Core.FontChoices.Face(FontName), Size, brush);

    public override Rect Bounds
    {
        get
        {
            var t = Formatted(Brushes.Black);
            return new Rect(At.X, At.Y, Math.Max(4, t.WidthIncludingTrailingWhitespace), Math.Max(4, t.Height));
        }
    }

    public override Mark Moved(Vector d) => new TextMark { At = At + d, Text = Text, Size = Size, FontName = FontName, Colour = Colour, Width = Width };
    public override Mark Restyled(Color colour, double width) => new TextMark { At = At, Text = Text, Size = Size, FontName = FontName, Colour = colour, Width = width };

    public override void Draw(DrawingContext ctx)
    {
        var text = Formatted(new SolidColorBrush(Colour));
        // a soft dark outline keeps it readable on any background
        var outline = text.BuildGeometry(At);
        if (outline != null)
            ctx.DrawGeometry(null, new Pen(new SolidColorBrush(Colors.Black, 0.45), Math.Max(2, Size / 8)) { LineJoin = PenLineJoin.Round }, outline);
        ctx.DrawText(text, At);
    }
}

public sealed class CounterMark : Mark
{
    public Point At { get; init; }
    public int Number { get; init; }

    private double Radius => Math.Max(14, Width * 3.5);
    public override Rect Bounds => new(At.X - Radius, At.Y - Radius, Radius * 2, Radius * 2);
    public override Mark Moved(Vector d) => new CounterMark { At = At + d, Number = Number, Colour = Colour, Width = Width };
    public override Mark Restyled(Color colour, double width) => new CounterMark { At = At, Number = Number, Colour = colour, Width = width };

    public override void Draw(DrawingContext ctx)
    {
        double r = Radius;
        ctx.DrawEllipse(new SolidColorBrush(Colour), new Pen(Brushes.White, Math.Max(2, r / 7)), At, r, r);
        var text = new FormattedText(Number.ToString(), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, TextMark.Font, r * 1.1, Brushes.White);
        ctx.DrawText(text, new Point(At.X - text.Width / 2, At.Y - text.Height / 2));
    }
}

/// <summary>
/// A box that stays bright while everything round it darkens. The darkening for every spotlight
/// is drawn once (<see cref="DrawDim"/>), so two spotlights don't darken each other.
/// </summary>
public sealed class SpotlightMark : Mark
{
    public Rect Area { get; init; }

    public override void Draw(DrawingContext ctx) { } // see DrawDim
    public override Rect Bounds => Area;
    public override IReadOnlyList<Point> Handles => Corners(Area);
    public override Mark Moved(Vector d) => new SpotlightMark { Area = Area.Translate(d) };
    public override Mark WithHandle(int handle, Point p) => new SpotlightMark { Area = DragCorner(Area, handle, p) };

    /// <summary>Everything outside the boxes darkened, with softly rounded holes.</summary>
    public static void DrawDim(DrawingContext ctx, IReadOnlyList<Rect> boxes, Size picture)
    {
        if (boxes.Count == 0) return;
        var holes = new GeometryGroup { FillRule = FillRule.NonZero };
        foreach (var r in boxes)
        {
            double round = Math.Min(Math.Min(r.Width, r.Height) / 2, 10);
            holes.Children.Add(new RectangleGeometry(r, round, round));
        }
        // just the picture (and a little over): a huge shape can fail to draw on some graphics cards
        var all = new RectangleGeometry(new Rect(-4, -4, picture.Width + 8, picture.Height + 8));
        ctx.DrawGeometry(new SolidColorBrush(Color.FromArgb(153, 0, 0, 0)), null, new CombinedGeometry(GeometryCombineMode.Exclude, all, holes));
    }
}

/// <summary>A blurred, pixelated or mosaic patch hiding what's under it, made from the picture when it was added.</summary>
public sealed class PatchMark : Mark
{
    public Rect Area { get; init; }
    public Overskin.Linux.Core.HideMode Mode { get; init; } = Overskin.Linux.Core.HideMode.Pixelate;
    public double Strength { get; init; } = 4;
    public Bitmap? Pixels { get; set; }

    public override Rect Bounds => Area;
    public override IReadOnlyList<Point> Handles => Corners(Area);
    /// <summary>Moved or stretched: the hidden pixels are made again from the picture under it (by the editor).</summary>
    public override Mark Moved(Vector d) => new PatchMark { Area = Area.Translate(d), Mode = Mode, Strength = Strength };
    public override Mark WithHandle(int handle, Point p) => new PatchMark { Area = DragCorner(Area, handle, p), Mode = Mode, Strength = Strength };

    public override void Draw(DrawingContext ctx)
    {
        if (Pixels != null)
        {
            using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None }))
                ctx.DrawImage(Pixels, new Rect(Pixels.Size), Area);
        }
        else
        {
            ctx.DrawRectangle(new SolidColorBrush(Colors.Gray, 0.5), new Pen(Brushes.White, 1, new DashStyle(new double[] { 4, 4 }, 0)), Area);
        }
    }
}

/// <summary>
/// A piece lifted off the picture with the Move tool (a word, an icon, a picture inside the
/// screenshot): drawn where it's dragged, at any size. See-through where the background was.
/// </summary>
public sealed class PieceMark : Mark
{
    public Rect Area { get; init; }
    public required Bitmap Pixels { get; init; }

    public override Rect Bounds => Area;
    public override IReadOnlyList<Point> Handles => Corners(Area);
    public override Mark Moved(Vector d) => new PieceMark { Area = Area.Translate(d), Pixels = Pixels };
    public override Mark WithHandle(int handle, Point p) => new PieceMark { Area = DragCorner(Area, handle, p), Pixels = Pixels };

    /// <summary>A corner dragged with the shape kept: scaled from the opposite corner.</summary>
    public PieceMark KeepShape(int handle, Point p)
    {
        var r = Area;
        if (r.Width < 1 || r.Height < 1) return this;
        var corners = Corners(r);
        var fixedCorner = corners[(handle + 2) % 4];
        var dragged = corners[handle];
        double sx = (p.X - fixedCorner.X) / (dragged.X - fixedCorner.X), sy = (p.Y - fixedCorner.Y) / (dragged.Y - fixedCorner.Y);
        double k = Math.Max(0.02, (Math.Abs(sx) + Math.Abs(sy)) / 2);
        var to = new Point(fixedCorner.X + (dragged.X - fixedCorner.X) * k, fixedCorner.Y + (dragged.Y - fixedCorner.Y) * k);
        return new PieceMark { Area = new Rect(fixedCorner, to).Normalize(), Pixels = Pixels };
    }

    public override void Draw(DrawingContext ctx)
    {
        bool sameSize = Math.Abs(Area.Width - Pixels.PixelSize.Width) < 0.5 && Math.Abs(Area.Height - Pixels.PixelSize.Height) < 0.5;
        using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = sameSize ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality }))
            ctx.DrawImage(Pixels, new Rect(Pixels.Size), Area);
    }
}

/// <summary>The mouse pointer drawn into a screenshot, where it was (its tip at <see cref="At"/>).</summary>
public sealed class PointerMark : Mark
{
    public Point At { get; init; }
    /// <summary>Picture pixels per point (2 on a Retina display).</summary>
    public double Scale { get; init; } = 1;
    private static readonly Geometry Arrow = StreamGeometry.Parse("M 0,0 L 0,22.5 L 5.4,17.4 L 9.1,25.6 L 13,23.9 L 9.4,15.8 L 16.8,15.8 Z");

    public override Rect Bounds => new(At.X, At.Y, 18 * Scale, 27 * Scale);
    public override Mark Moved(Vector d) => new PointerMark { At = At + d, Scale = Scale };

    public override void Draw(DrawingContext ctx)
    {
        using (ctx.PushTransform(Matrix.CreateScale(Scale, Scale) * Matrix.CreateTranslation(At.X, At.Y)))
            ctx.DrawGeometry(Brushes.Black, new Pen(Brushes.White, 1.4) { LineJoin = PenLineJoin.Round }, Arrow);
    }
}
