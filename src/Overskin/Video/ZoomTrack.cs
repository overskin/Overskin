using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Windows;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Storage;

namespace Overskin;

/// <summary>One stretch of a recording that's zoomed in, following the pointer.</summary>
public sealed class ZoomSegment
{
    public double Start { get; set; }
    public double End { get; set; }
    public double Zoom { get; set; } = 1.8;
}

/// <summary>
/// Auto-zoom you can change afterwards. The recording itself is kept unzoomed (in a hidden
/// ".overskin" folder next to the video), with where the pointer was and where you clicked; the
/// zooms are worked out from the clicks and applied when the video is made. The video editor
/// can move, remove and add zooms and make the video again from the unzoomed recording.
/// </summary>
public sealed class ZoomTrack
{
    public int Width { get; set; }
    public int Height { get; set; }
    public int Fps { get; set; } = 30;
    public double ZoomRate { get; set; } = 5.5;
    public double PanRate { get; set; } = 4.5;
    /// <summary>Where the pointer was: time, x, y (video pixels).</summary>
    public List<float[]> Pointer { get; set; } = new();
    /// <summary>Clicks: time, x, y.</summary>
    public List<float[]> Clicks { get; set; } = new();
    public List<ZoomSegment> Zooms { get; set; } = new();

    private static string Hidden(string video)
    {
        string dir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(video)) ?? "", ".overskin");
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
            try { File.SetAttributes(dir, File.GetAttributes(dir) | System.IO.FileAttributes.Hidden); } catch { }
        }
        return dir;
    }

    public static string RawPath(string video) => Path.Combine(Hidden(video), Path.GetFileNameWithoutExtension(video) + ".raw.mp4");
    public static string TrackPath(string video) => Path.Combine(Hidden(video), Path.GetFileNameWithoutExtension(video) + ".zooms.json");

    /// <summary>Removes a video's unzoomed recording and zoom track, when it's deleted.</summary>
    public static void DeleteFor(string video)
    {
        try
        {
            string dir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(video)) ?? "", ".overskin");
            string name = Path.GetFileNameWithoutExtension(video);
            foreach (var f in new[] { Path.Combine(dir, name + ".raw.mp4"), Path.Combine(dir, name + ".zooms.json") })
                if (File.Exists(f)) File.Delete(f);
        }
        catch { }
    }

    public void Save(string video) => File.WriteAllText(TrackPath(video), JsonSerializer.Serialize(this));

    /// <summary>The zoom track of a video, when it was recorded with editable zooms and its unzoomed recording is still there.</summary>
    public static ZoomTrack? Load(string video)
    {
        try
        {
            string dir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(video)) ?? "", ".overskin");
            string name = Path.GetFileNameWithoutExtension(video);
            string track = Path.Combine(dir, name + ".zooms.json"), raw = Path.Combine(dir, name + ".raw.mp4");
            if (!File.Exists(track) || !File.Exists(raw)) return null;
            return JsonSerializer.Deserialize<ZoomTrack>(File.ReadAllText(track));
        }
        catch { return null; }
    }

    /// <summary>
    /// Zooms from clicks: each click zooms in, a click soon after keeps it in, and it zooms back
    /// out <paramref name="hold"/> seconds after the last one.
    /// </summary>
    public static List<ZoomSegment> FromClicks(IEnumerable<float[]> clicks, double hold, double zoom)
    {
        var list = new List<ZoomSegment>();
        foreach (var c in clicks.OrderBy(c => c[0]))
        {
            double t = c[0];
            if (list.Count > 0 && t - 0.3 <= list[^1].End) list[^1].End = t + hold;
            else list.Add(new ZoomSegment { Start = Math.Max(0, t - 0.3), End = t + hold, Zoom = zoom });
        }
        return list;
    }

    /// <summary>How far zoomed in at <paramref name="t"/>, easing in and out of each zoom.</summary>
    public double ZoomAt(double t)
    {
        double z = 1;
        double into = Math.Max(0.15, 2.8 / ZoomRate), outOf = into * 1.4;
        foreach (var s in Zooms)
        {
            double k;
            if (t < s.Start || t > s.End + outOf) continue;
            if (t < s.Start + into) k = Ease((t - s.Start) / into);
            else if (t <= s.End) k = 1;
            else k = 1 - Ease((t - s.End) / outOf);
            z = Math.Max(z, 1 + (s.Zoom - 1) * k);
        }
        return z;
    }

    private static double Ease(double x)
    {
        x = Math.Clamp(x, 0, 1);
        return x * x * (3 - 2 * x);
    }

    public Point PointerAt(double t)
    {
        if (Pointer.Count == 0) return new Point(Width / 2.0, Height / 2.0);
        int lo = 0, hi = Pointer.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (Pointer[mid][0] <= t) lo = mid; else hi = mid - 1;
        }
        var a = Pointer[lo];
        if (lo + 1 >= Pointer.Count) return new Point(a[1], a[2]);
        var b = Pointer[lo + 1];
        double k = Math.Clamp((t - a[0]) / Math.Max(1e-6, b[0] - a[0]), 0, 1);
        return new Point(a[1] + (b[1] - a[1]) * k, a[2] + (b[2] - a[2]) * k);
    }

    /// <summary>
    /// Makes the zoomed video from the unzoomed recording: each frame is the part of it the zooms
    /// say, following the pointer, scaled back up to full size; the sound is put back after.
    /// </summary>
    public static Task RenderAsync(string raw, string output, ZoomTrack track, bool highQuality, IProgress<double>? progress = null)
    {
        double length = Math.Max(0.1, track.Pointer.Count > 0 ? track.Pointer[^1][0] : 1);
        Point centre = default, target = default;
        bool first = true;
        double last = 0;
        return FrameRender.RenderAsync(raw, output, highQuality, progress, length, fps: Math.Max(1, track.Fps), process: (src, srcW, srcH, w, h, t) =>
        {
            double sx = w / (double)Math.Max(1, track.Width), sy = h / (double)Math.Max(1, track.Height);
            if (first) { centre = target = new Point(w / 2.0, h / 2.0); first = false; }
            double dt = Math.Clamp(t - last, 0.001, 0.25);
            last = t;
            double z = track.ZoomAt(t);
            var p = track.PointerAt(t);
            p = new Point(p.X * sx, p.Y * sy);
            double vw = w / z, vh = h / z;
            if (z > 1.01)
            {
                // keep the pointer comfortably inside the view rather than glued to the middle
                double mx = vw * 0.28, my = vh * 0.28;
                double x = target.X, y = target.Y;
                if (p.X < x - vw / 2 + mx) x = p.X + vw / 2 - mx;
                if (p.X > x + vw / 2 - mx) x = p.X - vw / 2 + mx;
                if (p.Y < y - vh / 2 + my) y = p.Y + vh / 2 - my;
                if (p.Y > y + vh / 2 - my) y = p.Y - vh / 2 + my;
                target = new Point(x, y);
            }
            else target = p;
            double ease = 1 - Math.Exp(-dt * track.PanRate);
            centre = new Point(centre.X + (target.X - centre.X) * ease, centre.Y + (target.Y - centre.Y) * ease);
            double left = Math.Clamp(centre.X - vw / 2, 0, w - vw), top = Math.Clamp(centre.Y - vh / 2, 0, h - vh);
            return z <= 1.001 ? Crop(src, srcW, w, h) : Zoom(src, srcW, srcH, left, top, vw, vh, w, h);
        });
    }

    internal static byte[] Crop(byte[] src, int srcW, int w, int h)
    {
        if (srcW == w && src.Length == w * h * 4) return src;
        var dst = new byte[w * h * 4];
        for (int y = 0; y < h; y++) Buffer.BlockCopy(src, y * srcW * 4, dst, y * w * 4, w * 4);
        return dst;
    }

    /// <summary>Part of a frame scaled up to the full size, smoothly (bilinear).</summary>
    private static byte[] Zoom(byte[] src, int srcW, int srcH, double left, double top, double vw, double vh, int w, int h)
    {
        var dst = new byte[w * h * 4];
        double kx = vw / w, ky = vh / h;
        Parallel.For(0, h, y =>
        {
            double fy = top + (y + 0.5) * ky - 0.5;
            int y0 = Math.Clamp((int)Math.Floor(fy), 0, srcH - 1), y1 = Math.Min(srcH - 1, y0 + 1);
            double wy = Math.Clamp(fy - y0, 0, 1);
            int row = y * w * 4;
            for (int x = 0; x < w; x++)
            {
                double fx = left + (x + 0.5) * kx - 0.5;
                int x0 = Math.Clamp((int)Math.Floor(fx), 0, srcW - 1), x1 = Math.Min(srcW - 1, x0 + 1);
                double wx = Math.Clamp(fx - x0, 0, 1);
                int a = (y0 * srcW + x0) * 4, b = (y0 * srcW + x1) * 4, c = (y1 * srcW + x0) * 4, d = (y1 * srcW + x1) * 4;
                for (int ch = 0; ch < 3; ch++)
                {
                    double top0 = src[a + ch] + (src[b + ch] - src[a + ch]) * wx;
                    double bot0 = src[c + ch] + (src[d + ch] - src[c + ch]) * wx;
                    dst[row + x * 4 + ch] = (byte)(top0 + (bot0 - top0) * wy + 0.5);
                }
                dst[row + x * 4 + 3] = 255;
            }
        });
        return dst;
    }
}
