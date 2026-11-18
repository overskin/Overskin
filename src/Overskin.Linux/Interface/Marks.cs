using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Overskin.Linux.Interface;

public enum Tool { Pen, Highlighter, Arrow, Line, Rectangle, Ellipse, Text, Hide, Counter, Erase, Crop }

/// <summary>Something drawn on the picture. Drawn in picture pixels.</summary>
public abstract class Mark
{
    public Color Colour { get; init; }
    public double Width { get; init; } = 4;
    public abstract void Draw(DrawingContext ctx);

    protected Pen Stroke(double opacity = 1) => new(new SolidColorBrush(Colour, opacity), Width)
    {
        LineCap = PenLineCap.Round,
        LineJoin = PenLineJoin.Round,
    };
}

public sealed class StrokeMark : Mark
{
    public List<Point> Points { get; } = new();
    public bool Highlighter { get; init; }

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

    public override void Draw(DrawingContext ctx)
    {
        var rect = new Rect(From, To).Normalize();
        switch (Kind)
        {
            case Tool.Rectangle:
                ctx.DrawRectangle(null, Stroke(), rect, Width, Width);
                break;
            case Tool.Ellipse:
                ctx.DrawEllipse(null, Stroke(), rect.Center, rect.Width / 2, rect.Height / 2);
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

    public static readonly Typeface Font = new("Inter", FontStyle.Normal, FontWeight.Bold);

    public override void Draw(DrawingContext ctx)
    {
        var text = new FormattedText(Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Font, Size, new SolidColorBrush(Colour));
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

    public override void Draw(DrawingContext ctx)
    {
        double r = Math.Max(14, Width * 3.5);
        ctx.DrawEllipse(new SolidColorBrush(Colour), new Pen(Brushes.White, Math.Max(2, r / 7)), At, r, r);
        var text = new FormattedText(Number.ToString(), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, TextMark.Font, r * 1.1, Brushes.White);
        ctx.DrawText(text, new Point(At.X - text.Width / 2, At.Y - text.Height / 2));
    }
}

/// <summary>A blurred, pixelated or mosaic patch hiding what's under it, made from the picture when it was added.</summary>
public sealed class PatchMark : Mark
{
    public Rect Area { get; init; }
    public Overskin.Linux.Core.HideMode Mode { get; init; } = Overskin.Linux.Core.HideMode.Pixelate;
    public double Strength { get; init; } = 4;
    public Bitmap? Pixels { get; set; }

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
