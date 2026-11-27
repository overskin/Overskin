using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Overskin.Linux.Core;

/// <summary>
/// Puts a screenshot inside a device or window frame (as on Windows): a browser window, a laptop
/// or a phone, drawn here (no brand's design), or any frame picture you have (a PNG with a
/// see-through screen, such as the product bezels device makers publish for designers), in which
/// case the see-through part is found and the screenshot is fitted into it.
/// </summary>
public static class Mockup
{
    public static readonly string[] Frames = { "None", "Browser", "Laptop", "Phone", "Custom" };

    public static Bitmap Apply(Bitmap shot, string frame, string? customPath) => frame switch
    {
        "Browser" => Browser(shot),
        "Laptop" => Laptop(shot),
        "Phone" => Phone(shot),
        "Custom" when customPath != null && File.Exists(customPath) && Custom(shot, customPath) is { } framed => framed,
        _ => shot,
    };

    private static IBrush Solid(byte r, byte g, byte b) => new SolidColorBrush(Color.FromRgb(r, g, b));

    private static void Picture(DrawingContext ctx, Bitmap shot, Rect to)
    {
        using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
            ctx.DrawImage(shot, new Rect(shot.Size), to);
    }

    /// <summary>A plain browser window: a bar with three dots and an address field.</summary>
    private static Bitmap Browser(Bitmap shot)
    {
        double w = shot.PixelSize.Width, h = shot.PixelSize.Height;
        double bar = Math.Clamp(w * 0.045, 26, 64), r = Math.Clamp(w * 0.012, 6, 16);
        return Imaging.Render(new PixelSize((int)w, (int)(h + bar)), ctx =>
        {
            using (ctx.PushClip(new RoundedRect(new Rect(0, 0, w, h + bar), r)))
            {
                ctx.FillRectangle(Solid(0xEC, 0xED, 0xF0), new Rect(0, 0, w, bar));
                double dot = bar * 0.24;
                var colours = new[] { Solid(0xFF, 0x5F, 0x57), Solid(0xFE, 0xBC, 0x2E), Solid(0x28, 0xC8, 0x40) };
                for (int i = 0; i < 3; i++) ctx.DrawEllipse(colours[i], null, new Point(bar * 0.55 + i * dot * 2.6, bar / 2), dot / 2 * 1.05, dot / 2 * 1.05);
                double fieldW = Math.Min(w * 0.5, 720), fieldH = bar * 0.56;
                ctx.DrawRectangle(Brushes.White, new Pen(Solid(0xD6, 0xD8, 0xDD), 1), new Rect((w - fieldW) / 2, (bar - fieldH) / 2, fieldW, fieldH), fieldH / 2, fieldH / 2);
                ctx.FillRectangle(Solid(0xD6, 0xD8, 0xDD), new Rect(0, bar - 1, w, 1));
                Picture(ctx, shot, new Rect(0, bar, w, h));
            }
        });
    }

    /// <summary>A laptop: a dark bezel round the screen with a camera, and a base underneath.</summary>
    private static Bitmap Laptop(Bitmap shot)
    {
        double w = shot.PixelSize.Width, h = shot.PixelSize.Height;
        double bezel = Math.Max(10, w * 0.022), top = bezel * 1.35, r = bezel * 1.1;
        double lidW = w + 2 * bezel, lidH = h + top + bezel;
        double baseH = Math.Max(10, w * 0.028), overhang = lidW * 0.07;
        double totalW = lidW + 2 * overhang, totalH = lidH + baseH;
        return Imaging.Render(new PixelSize((int)Math.Ceiling(totalW), (int)Math.Ceiling(totalH)), ctx =>
        {
            var lid = new Rect(overhang, 0, lidW, lidH);
            ctx.DrawRectangle(Solid(0x1C, 0x1C, 0x1F), new Pen(Solid(0x3A, 0x3B, 0x40), Math.Max(1, bezel * 0.08)), lid, r, r);
            ctx.DrawEllipse(Solid(0x35, 0x36, 0x3B), null, new Point(overhang + lidW / 2, top / 2), bezel * 0.16, bezel * 0.16);
            Picture(ctx, shot, new Rect(overhang + bezel, top, w, h));
            // the base: a light slab, rounded underneath, with a notch for opening the lid
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                double y = lidH, rr = baseH * 0.9;
                g.BeginFigure(new Point(0, y), true);
                g.LineTo(new Point(totalW, y));
                g.ArcTo(new Point(totalW - rr, y + baseH), new Size(rr, rr), 0, false, SweepDirection.Clockwise);
                g.LineTo(new Point(rr, y + baseH));
                g.ArcTo(new Point(0, y), new Size(rr, rr), 0, false, SweepDirection.Clockwise);
                g.EndFigure(true);
            }
            var metal = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromRgb(0xD9, 0xDB, 0xE0), 0), new GradientStop(Color.FromRgb(0xA9, 0xAC, 0xB4), 1) },
            };
            ctx.DrawGeometry(metal, null, geometry);
            double notch = lidW * 0.14;
            ctx.DrawRectangle(Solid(0x9C, 0x9F, 0xA7), null, new Rect((totalW - notch) / 2, lidH, notch, baseH * 0.32), baseH * 0.16, baseH * 0.16);
        });
    }

    /// <summary>A phone: a rounded black body, the screen with rounded corners, and a pill-shaped camera cut-out.</summary>
    private static Bitmap Phone(Bitmap shot)
    {
        double w = shot.PixelSize.Width, h = shot.PixelSize.Height, side = Math.Min(w, h);
        bool sideways = w > h;
        double bezel = Math.Max(8, side * 0.04), screenR = side * 0.09, bodyR = screenR + bezel;
        double bw = w + 2 * bezel, bh = h + 2 * bezel;
        return Imaging.Render(new PixelSize((int)Math.Ceiling(bw), (int)Math.Ceiling(bh)), ctx =>
        {
            ctx.DrawRectangle(Solid(0x10, 0x10, 0x12), new Pen(Solid(0x3C, 0x3D, 0x42), Math.Max(1.5, bezel * 0.12)), new Rect(0, 0, bw, bh), bodyR, bodyR);
            var screen = new Rect(bezel, bezel, w, h);
            using (ctx.PushClip(new RoundedRect(screen, screenR)))
                Picture(ctx, shot, screen);
            // the camera cut-out sits at the top, or at the left when the phone is turned sideways
            double pillLong = side * 0.3, pillShort = Math.Max(8, side * 0.075);
            var pill = sideways
                ? new Rect(bezel + pillShort * 0.45, (bh - pillLong) / 2, pillShort, pillLong)
                : new Rect((bw - pillLong) / 2, bezel + pillShort * 0.45, pillLong, pillShort);
            ctx.DrawRectangle(Brushes.Black, null, pill, pillShort / 2, pillShort / 2);
        });
    }

    /// <summary>
    /// Your own frame: the see-through area in the middle of the picture is the screen. The
    /// screenshot fills it (cropped to fit if its shape differs) and the frame goes on top.
    /// </summary>
    public static Bitmap? Custom(Bitmap shot, string path)
    {
        Bitmap frame;
        try { frame = new Bitmap(path); }
        catch { return null; }
        if (FindScreen(frame) is not { } screen) return null;
        int fw = frame.PixelSize.Width, fh = frame.PixelSize.Height;
        return Imaging.Render(new PixelSize(fw, fh), ctx =>
        {
            double scale = Math.Max(screen.Width / shot.PixelSize.Width, screen.Height / shot.PixelSize.Height);
            double dw = shot.PixelSize.Width * scale, dh = shot.PixelSize.Height * scale;
            using (ctx.PushClip(screen))
                Picture(ctx, shot, new Rect(screen.X + (screen.Width - dw) / 2, screen.Y + (screen.Height - dh) / 2, dw, dh));
            ctx.DrawImage(frame, new Rect(frame.Size), new Rect(0, 0, fw, fh));
        });
    }

    /// <summary>The see-through area of a frame picture that doesn't touch its edges (the screen), or null.</summary>
    public static Rect? FindScreen(Bitmap frame)
    {
        int w = frame.PixelSize.Width, h = frame.PixelSize.Height;
        var px = Imaging.Pixels(frame, out _);
        var seen = new bool[w * h];
        var stack = new Stack<int>();
        Rect? best = null;
        double bestArea = 0;
        for (int start = 0; start < w * h; start++)
        {
            if (seen[start] || px[start * 4 + 3] > 40) continue;
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1, count = 0;
            bool edge = false;
            stack.Push(start);
            seen[start] = true;
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                int x = i % w, y = i / w;
                count++;
                if (x == 0 || y == 0 || x == w - 1 || y == h - 1) edge = true;
                x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
                void Try(int j) { if (!seen[j] && px[j * 4 + 3] <= 40) { seen[j] = true; stack.Push(j); } }
                if (x > 0) Try(i - 1);
                if (x < w - 1) Try(i + 1);
                if (y > 0) Try(i - w);
                if (y < h - 1) Try(i + w);
            }
            // the screen is enclosed by the frame; the space round a device touches the edges
            if (!edge && count > bestArea && count > w * h * 0.02)
            {
                bestArea = count;
                best = new Rect(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
            }
        }
        return best;
    }
}
