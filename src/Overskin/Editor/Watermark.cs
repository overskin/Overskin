using System.Globalization;
using System.IO;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Overskin;

/// <summary>A name, logo or handle stamped on screenshots and videos.</summary>
public sealed class WatermarkOptions
{
    public bool OnScreenshots { get; set; }
    public bool OnVideos { get; set; }
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

    // ---- tiled across ----
    /// <summary>Tilt of the rows, in degrees (-90 to 90; 0 is level).</summary>
    public int TileAngle { get; set; } = -30;
    /// <summary>Space between marks, as a percentage of the usual (25 close together to 300 far apart).</summary>
    public int TileSpacing { get; set; } = 100;
    /// <summary>Shift every other row half a step, like bricks.</summary>
    public bool TileStagger { get; set; } = true;
    /// <summary>Text as an outline only, so the picture shows through the letters.</summary>
    public bool Outline { get; set; }

    public static readonly string[] Positions = { "TopLeft", "TopRight", "BottomLeft", "BottomRight", "Center", "Tiled" };

    public static string PositionName(string p) => p switch
    {
        "TopLeft" => "Top left", "TopRight" => "Top right", "BottomLeft" => "Bottom left",
        "BottomRight" => "Bottom right", "Center" => "Middle", "Tiled" => "Tiled across", _ => p,
    };

    /// <summary>True when there's something to stamp.</summary>
    [JsonIgnore]
    public bool HasContent => Kind == "Image" ? File.Exists(ImagePath) : !string.IsNullOrWhiteSpace(Text);

    public void Normalize()
    {
        if (Kind is not ("Text" or "Image")) Kind = "Text";
        Text ??= "";
        ImagePath ??= "";
        if (!Positions.Contains(Position)) Position = "BottomRight";
        Size = Math.Clamp(Size, 2, 30);
        Opacity = Math.Clamp(Opacity, 10, 100);
        if (Color is not ("White" or "Black")) Color = "White";
        TileAngle = Math.Clamp(TileAngle, -90, 90);
        TileSpacing = Math.Clamp(TileSpacing == 0 ? 100 : TileSpacing, 25, 300);
    }

    public WatermarkOptions Copy() => (WatermarkOptions)MemberwiseClone();
}

/// <summary>Draws a watermark onto pictures, or into a transparent layer for video frames.</summary>
public static class Watermark
{
    private static readonly Dictionary<string, BitmapSource?> Logos = new();

    private static BitmapSource? Logo(string path)
    {
        lock (Logos)
        {
            if (Logos.TryGetValue(path, out var cached)) return cached;
            BitmapSource? logo = null;
            try
            {
                var b = new BitmapImage();
                b.BeginInit();
                b.CacheOption = BitmapCacheOption.OnLoad;
                b.UriSource = new Uri(Path.GetFullPath(path));
                b.EndInit();
                b.Freeze();
                logo = b;
            }
            catch { }
            Logos[path] = logo;
            return logo;
        }
    }

    /// <summary>Draws the watermark for a <paramref name="w"/> × <paramref name="h"/> picture (pixels).</summary>
    private static void DrawOn(DrawingContext dc, WatermarkOptions o, double w, double h)
    {
        double side = Math.Min(w, h), height = Math.Max(8, side * o.Size / 100.0), margin = Math.Max(6, side * 0.03);
        Drawing? mark = MakeMark(o, height);
        if (mark == null) return;
        var b = mark.Bounds;
        dc.PushOpacity(o.Opacity / 100.0);
        if (o.Position == "Tiled")
        {
            // rows of marks, tilted, spaced as chosen, every other row shifted half a step (if wanted)
            double k = o.TileSpacing / 100.0;
            double stepX = b.Width + height * 3 * k, stepY = b.Height + height * 2.5 * k;
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));
            dc.PushTransform(new RotateTransform(o.TileAngle, w / 2, h / 2));
            double reach = Math.Sqrt(w * w + h * h);
            int row = 0;
            for (double y = h / 2 - reach; y < h / 2 + reach; y += stepY, row++)
                for (double x = w / 2 - reach + (o.TileStagger ? (row % 2) * stepX / 2 : 0); x < w / 2 + reach; x += stepX)
                {
                    dc.PushTransform(new TranslateTransform(x - b.X, y - b.Y));
                    dc.DrawDrawing(mark);
                    dc.Pop();
                }
            dc.Pop();
            dc.Pop();
        }
        else
        {
            double x = o.Position switch
            {
                "TopLeft" or "BottomLeft" => margin,
                "Center" => (w - b.Width) / 2,
                _ => w - margin - b.Width,
            };
            double y = o.Position switch
            {
                "TopLeft" or "TopRight" => margin,
                "Center" => (h - b.Height) / 2,
                _ => h - margin - b.Height,
            };
            dc.PushTransform(new TranslateTransform(Math.Round(x - b.X), Math.Round(y - b.Y)));
            dc.DrawDrawing(mark);
            dc.Pop();
        }
        dc.Pop();
    }

    /// <summary>The mark itself, <paramref name="height"/> pixels tall.</summary>
    private static Drawing? MakeMark(WatermarkOptions o, double height)
    {
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            if (o.Kind == "Image")
            {
                var logo = Logo(o.ImagePath);
                if (logo == null || logo.PixelHeight == 0) return null;
                double width = height * logo.PixelWidth / logo.PixelHeight;
                dc.DrawImage(logo, new Rect(0, 0, width, height));
            }
            else
            {
                string text = o.Text.Trim();
                if (text.Length == 0) return null;
                var face = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
                var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, height * 0.8, Brushes.White, 1.0);
                var geometry = ft.BuildGeometry(new Point(0, 0));
                bool white = o.Color == "White";
                var shadow = geometry.Clone();
                shadow.Transform = new TranslateTransform(height * 0.04, height * 0.05);
                if (o.Outline)
                {
                    // just the outline, with a thin contrasting edge so it reads on any background
                    double pen = Math.Max(1, height * 0.045);
                    dc.DrawGeometry(null, new Pen(white ? new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)) : new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), pen * 2.2), geometry);
                    dc.DrawGeometry(null, new Pen(white ? Brushes.White : Brushes.Black, pen), geometry);
                }
                else
                {
                    dc.DrawGeometry(new SolidColorBrush(white ? Color.FromArgb(110, 0, 0, 0) : Color.FromArgb(110, 255, 255, 255)), null, shadow);
                    dc.DrawGeometry(white ? Brushes.White : Brushes.Black, null, geometry);
                }
            }
        }
        group.Freeze();
        return group;
    }

    /// <summary>The picture with the watermark on it.</summary>
    public static BitmapSource Apply(BitmapSource image, WatermarkOptions o)
    {
        if (!o.HasContent) return image;
        int w = image.PixelWidth, h = image.PixelHeight;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(image, new Rect(0, 0, w, h));
            DrawOn(dc, o, w, h);
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>
    /// The watermark alone on a transparent <paramref name="w"/> × <paramref name="h"/> layer
    /// (premultiplied BGRA), for blending into video frames; null when there's nothing to draw.
    /// Must be called on the UI thread.
    /// </summary>
    public static byte[]? Layer(WatermarkOptions o, int w, int h)
    {
        if (!o.HasContent) return null;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) DrawOn(dc, o, w, h);
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var px = new byte[w * h * 4];
        rtb.CopyPixels(px, w * 4, 0);
        return px;
    }

    /// <summary>Blends a <see cref="Layer"/> onto a frame of the same size.</summary>
    public static void Blend(byte[] frame, byte[] layer)
    {
        int rows = 64, chunk = (frame.Length / 4 + rows - 1) / rows;
        Parallel.For(0, rows, part =>
        {
            int start = part * chunk * 4, end = Math.Min(frame.Length, (part + 1) * chunk * 4);
            for (int i = start; i < end; i += 4)
            {
                int a = layer[i + 3];
                if (a == 0) continue;
                int keep = 255 - a;
                frame[i] = (byte)(layer[i] + frame[i] * keep / 255);
                frame[i + 1] = (byte)(layer[i + 1] + frame[i + 1] * keep / 255);
                frame[i + 2] = (byte)(layer[i + 2] + frame[i + 2] * keep / 255);
            }
        });
    }
}
