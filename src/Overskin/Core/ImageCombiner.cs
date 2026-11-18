using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Overskin;

public enum CombineLayout { SideBySide, Stacked, Grid }

/// <summary>
/// Puts several pictures into one: in a row, in a column, or in a grid. Pictures are scaled to a
/// shared height (row), width (column) or cell (grid), taken from the middle-sized picture so
/// one huge or tiny screenshot doesn't decide the size of all the others.
/// </summary>
public static class ImageCombiner
{
    public const int Gap = 16;
    private const double MaxSide = 16000;

    public static BitmapSource Combine(IReadOnlyList<BitmapSource> images, CombineLayout layout, Color background, int gap = Gap)
    {
        if (images.Count == 0) throw new ArgumentException("No pictures to combine.");
        var sizes = images.Select(i => new Size(Math.Max(1, i.PixelWidth), Math.Max(1, i.PixelHeight))).ToList();
        var rects = new List<Rect>();
        double width, height;

        switch (layout)
        {
            case CombineLayout.SideBySide:
            {
                double h = Median(sizes.Select(s => s.Height));
                double x = gap;
                foreach (var s in sizes)
                {
                    double w = s.Width * h / s.Height;
                    rects.Add(new Rect(x, gap, w, h));
                    x += w + gap;
                }
                (width, height) = (x, h + 2 * gap);
                break;
            }
            case CombineLayout.Stacked:
            {
                double w = Median(sizes.Select(s => s.Width));
                double y = gap;
                foreach (var s in sizes)
                {
                    double h = s.Height * w / s.Width;
                    rects.Add(new Rect(gap, y, w, h));
                    y += h + gap;
                }
                (width, height) = (w + 2 * gap, y);
                break;
            }
            default:
            {
                int columns = (int)Math.Ceiling(Math.Sqrt(sizes.Count));
                int rows = (int)Math.Ceiling(sizes.Count / (double)columns);
                double cw = Median(sizes.Select(s => s.Width)), ch = Median(sizes.Select(s => s.Height));
                for (int i = 0; i < sizes.Count; i++)
                {
                    var s = sizes[i];
                    double scale = Math.Min(cw / s.Width, ch / s.Height);
                    double w = s.Width * scale, h = s.Height * scale;
                    double cx = gap + (i % columns) * (cw + gap), cy = gap + (i / columns) * (ch + gap);
                    rects.Add(new Rect(cx + (cw - w) / 2, cy + (ch - h) / 2, w, h));
                }
                (width, height) = (gap + columns * (cw + gap), gap + rows * (ch + gap));
                break;
            }
        }

        // keep the result a size every program can open
        double shrink = Math.Min(1, MaxSide / Math.Max(width, height));
        int pw = Math.Max(1, (int)Math.Round(width * shrink)), ph = Math.Max(1, (int)Math.Round(height * shrink));

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(shrink, shrink));
            dc.DrawRectangle(new SolidColorBrush(background), null, new Rect(0, 0, width, height));
            for (int i = 0; i < images.Count; i++)
                dc.DrawImage(images[i], Snap(rects[i]));
            dc.Pop();
        }
        var target = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    private static Rect Snap(Rect r) => new(Math.Round(r.X), Math.Round(r.Y), Math.Round(r.Width), Math.Round(r.Height));

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[sorted.Count / 2];
    }
}
