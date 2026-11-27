using System.Globalization;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Overskin.Linux.Core;

/// <summary>A name, logo or handle stamped on screenshots (the same choices as on Windows).</summary>
public sealed class WatermarkOptions
{
    public bool OnScreenshots { get; set; }
    /// <summary>"Text" or "Image".</summary>
    public string Kind { get; set; } = "Text";
    public string Text { get; set; } = "";
    public string ImagePath { get; set; } = "";
    /// <summary>"TopLeft", "TopRight", "BottomLeft", "BottomRight", "Center" or "Tiled".</summary>
    public string Position { get; set; } = "BottomRight";
    /// <summary>Height as a percentage of the picture's shorter side.</summary>
    public int Size { get; set; } = 6;
    public int Opacity { get; set; } = 70;
    /// <summary>Text colour: "White" or "Black" (each with a soft shadow of the other).</summary>
    public string Color { get; set; } = "White";
    /// <summary>Tilt of the tiled rows, in degrees.</summary>
    public int TileAngle { get; set; } = -30;
    /// <summary>Space between tiled marks, as a percentage of the usual.</summary>
    public int TileSpacing { get; set; } = 100;
    public bool TileStagger { get; set; } = true;
    /// <summary>Text as an outline only.</summary>
    public bool Outline { get; set; }

    public static readonly string[] Positions = { "TopLeft", "TopRight", "BottomLeft", "BottomRight", "Center", "Tiled" };

    public static string PositionName(string p) => p switch
    {
        "TopLeft" => "Top left", "TopRight" => "Top right", "BottomLeft" => "Bottom left",
        "BottomRight" => "Bottom right", "Center" => "Middle", "Tiled" => "Tiled across", _ => p,
    };

    [JsonIgnore]
    public bool HasContent => Kind == "Image" ? File.Exists(ImagePath) : !string.IsNullOrWhiteSpace(Text);
}

/// <summary>Draws a watermark onto pictures.</summary>
public static class Watermark
{
    private static readonly Dictionary<string, Bitmap?> Logos = new();

    private static Bitmap? Logo(string path)
    {
        lock (Logos)
        {
            if (Logos.TryGetValue(path, out var cached)) return cached;
            Bitmap? logo = null;
            try { logo = new Bitmap(path); } catch { }
            Logos[path] = logo;
            return logo;
        }
    }

    /// <summary>The picture with the watermark on it.</summary>
    public static Bitmap Apply(Bitmap image, WatermarkOptions o)
    {
        if (!o.HasContent) return image;
        int w = image.PixelSize.Width, h = image.PixelSize.Height;
        return Imaging.Render(image.PixelSize, ctx =>
        {
            ctx.DrawImage(image, new Rect(image.Size), new Rect(0, 0, w, h));
            DrawOn(ctx, o, w, h);
        });
    }

    private static void DrawOn(DrawingContext ctx, WatermarkOptions o, double w, double h)
    {
        double side = Math.Min(w, h), height = Math.Max(8, side * o.Size / 100.0), margin = Math.Max(6, side * 0.03);
        var mark = MakeMark(o, height);
        if (mark == null) return;
        var (size, draw) = mark.Value;
        using var _ = ctx.PushOpacity(o.Opacity / 100.0);
        if (o.Position == "Tiled")
        {
            // rows of marks, tilted, spaced as chosen, every other row shifted half a step (if wanted)
            double k = o.TileSpacing / 100.0;
            double stepX = size.Width + height * 3 * k, stepY = size.Height + height * 2.5 * k;
            using var clip = ctx.PushClip(new Rect(0, 0, w, h));
            var turn = Matrix.CreateTranslation(-w / 2, -h / 2) * Matrix.CreateRotation(o.TileAngle * Math.PI / 180) * Matrix.CreateTranslation(w / 2, h / 2);
            using var rotate = ctx.PushTransform(turn);
            double reach = Math.Sqrt(w * w + h * h);
            int row = 0;
            for (double y = h / 2 - reach; y < h / 2 + reach; y += stepY, row++)
                for (double x = w / 2 - reach + (o.TileStagger ? (row % 2) * stepX / 2 : 0); x < w / 2 + reach; x += stepX)
                    using (ctx.PushTransform(Matrix.CreateTranslation(x, y))) draw(ctx);
            return;
        }
        double px = o.Position switch
        {
            "TopLeft" or "BottomLeft" => margin,
            "Center" => (w - size.Width) / 2,
            _ => w - margin - size.Width,
        };
        double py = o.Position switch
        {
            "TopLeft" or "TopRight" => margin,
            "Center" => (h - size.Height) / 2,
            _ => h - margin - size.Height,
        };
        using (ctx.PushTransform(Matrix.CreateTranslation(Math.Round(px), Math.Round(py)))) draw(ctx);
    }

    /// <summary>The mark's size and how to draw it at the origin, <paramref name="height"/> pixels tall.</summary>
    private static (Size Size, Action<DrawingContext> Draw)? MakeMark(WatermarkOptions o, double height)
    {
        if (o.Kind == "Image")
        {
            var logo = Logo(o.ImagePath);
            if (logo == null || logo.PixelSize.Height == 0) return null;
            double width = height * logo.PixelSize.Width / logo.PixelSize.Height;
            return (new Size(width, height), ctx => ctx.DrawImage(logo, new Rect(logo.Size), new Rect(0, 0, width, height)));
        }
        string text = o.Text.Trim();
        if (text.Length == 0) return null;
        var face = new Typeface(new FontFamily("Inter"), FontStyle.Normal, FontWeight.SemiBold);
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, height * 0.8, Brushes.White);
        var geometry = ft.BuildGeometry(new Point(0, 0));
        if (geometry == null) return null;
        var bounds = geometry.Bounds;
        bool white = o.Color == "White";
        return (new Size(bounds.Right, bounds.Bottom), ctx =>
        {
            if (o.Outline)
            {
                double pen = Math.Max(1, height * 0.045);
                ctx.DrawGeometry(null, new Pen(new SolidColorBrush(white ? Color.FromArgb(120, 0, 0, 0) : Color.FromArgb(120, 255, 255, 255)), pen * 2.2), geometry);
                ctx.DrawGeometry(null, new Pen(white ? Brushes.White : Brushes.Black, pen), geometry);
                return;
            }
            using (ctx.PushTransform(Matrix.CreateTranslation(height * 0.04, height * 0.05)))
                ctx.DrawGeometry(new SolidColorBrush(white ? Color.FromArgb(110, 0, 0, 0) : Color.FromArgb(110, 255, 255, 255)), null, geometry);
            ctx.DrawGeometry(white ? Brushes.White : Brushes.Black, null, geometry);
        });
    }
}
