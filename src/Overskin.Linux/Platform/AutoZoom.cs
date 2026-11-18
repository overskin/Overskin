using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Overskin.Linux.Core;
using WPoint = System.Windows.Point;

namespace Overskin.Linux.Platform;

/// <summary>Where the pointer was during a recording, and when it clicked.</summary>
public sealed class PointerTrack
{
    public readonly record struct Sample(double T, double X, double Y, bool Down);

    private readonly List<Sample> _samples = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    public IReadOnlyList<Sample> Samples => _samples;

    /// <summary>Starts following the pointer, or null where the desktop won't say where it is.</summary>
    public static async Task<PointerTrack?> StartAsync()
    {
        if (await Input.PointerAsync() == null) return null;
        var track = new PointerTrack();
        _ = Task.Run(async () =>
        {
            while (!track._stop.IsCancellationRequested)
            {
                if (await Input.PointerAsync() is { } p)
                    lock (track._samples) track._samples.Add(new Sample(track._clock.Elapsed.TotalSeconds, p.At.X, p.At.Y, p.LeftDown));
                try { await Task.Delay(ScreenGrabber.IsWayland ? 40 : 15, track._stop.Token); } catch { }
            }
        });
        return track;
    }

    public void Stop() => _stop.Cancel();

    /// <summary>The pointer at time t, and whether a click started between t0 and t.</summary>
    public (Point At, bool Clicked) At(double t0, double t)
    {
        lock (_samples)
        {
            if (_samples.Count == 0) return (default, false);
            int i = _samples.FindLastIndex(s => s.T <= t);
            if (i < 0) i = 0;
            var a = _samples[i];
            var at = new Point(a.X, a.Y);
            if (i + 1 < _samples.Count)
            {
                var b = _samples[i + 1];
                double k = Math.Clamp((t - a.T) / Math.Max(1e-6, b.T - a.T), 0, 1);
                at = new Point(a.X + (b.X - a.X) * k, a.Y + (b.Y - a.Y) * k);
            }
            bool clicked = false;
            for (int j = Math.Max(1, _samples.FindIndex(s => s.T > t0)); j > 0 && j < _samples.Count && _samples[j].T <= t; j++)
                if (_samples[j].Down && !_samples[j - 1].Down) clicked = true;
            if (!Clicks) clicked = Dwelled(t0, t);
            return (at, clicked);
        }
    }

    /// <summary>X11 reports the button; elsewhere only the position is known.</summary>
    public bool Clicks { get; init; } = !ScreenGrabber.IsWayland;

    /// <summary>Without clicks: the pointer came to rest after a real move, which is when a click usually happens.</summary>
    private bool Dwelled(double t0, double t)
    {
        var recent = _samples.Where(s => s.T > t - 1.2 && s.T <= t).ToList();
        if (recent.Count < 4) return false;
        var still = recent.Where(s => s.T > t - 0.5).ToList();
        if (still.Count < 2) return false;
        double spread = still.Max(s => Math.Abs(s.X - still[0].X) + Math.Abs(s.Y - still[0].Y));
        double travel = Math.Abs(recent[0].X - still[0].X) + Math.Abs(recent[0].Y - still[0].Y);
        // only the moment it settles counts, not every frame after
        var before = _samples.Where(s => s.T > t0 - 0.5 && s.T <= t0).ToList();
        double spreadBefore = before.Count < 2 ? double.MaxValue : before.Max(s => Math.Abs(s.X - before[0].X) + Math.Abs(s.Y - before[0].Y));
        return spread < 4 && travel > 60 && spreadBefore >= 4;
    }
}

/// <summary>
/// Auto-zoom: redraws a recording so it glides in on each click, follows the pointer and eases
/// back out, with a large smoothed pointer and click rings. Frames go through ffmpeg both ways.
/// </summary>
public static class AutoZoom
{
    public static async Task ApplyAsync(string video, PointerTrack track, PixelRect area, double zoomIn, double startOffset)
    {
        var probe = await ScreenGrabber.OutputOf("ffprobe", $"-v error -select_streams v:0 -show_entries stream=width,height,avg_frame_rate -of csv=p=0 \"{video}\"");
        var f = probe?.Trim().Split(',');
        if (f is not { Length: 3 }) throw new InvalidOperationException("couldn't read the video's size");
        int w = int.Parse(f[0]), h = int.Parse(f[1]);
        var rate = f[2].Split('/');
        double fps = double.Parse(rate[0], CultureInfo.InvariantCulture) / Math.Max(1, double.Parse(rate.Length > 1 ? rate[1] : "1", CultureInfo.InvariantCulture));
        if (fps <= 0 || fps > 240) fps = 30;
        double k = w / (double)Math.Max(1, area.Width); // picture pixels to video pixels

        var cursor = Sprite(Math.Max(1, h / 540.0));
        var ring = RingSprite(Math.Max(1, h / 540.0));
        var output = Path.Combine(Path.GetDirectoryName(video)!, Path.GetFileNameWithoutExtension(video) + ".zoom.mp4");

        using var decoder = Process.Start(new ProcessStartInfo("ffmpeg", $"-hide_banner -loglevel error -i \"{video}\" -f rawvideo -pix_fmt bgra -")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        using var encoder = Process.Start(new ProcessStartInfo("ffmpeg",
            $"-hide_banner -loglevel error -y -f rawvideo -pix_fmt bgra -s {w}x{h} -r {fps.ToString(CultureInfo.InvariantCulture)} -i - " +
            $"-c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p -movflags +faststart \"{output}\"")
        { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = true })!;
        _ = decoder.StandardError.ReadToEndAsync();
        var encoderErrors = encoder.StandardError.ReadToEndAsync();

        var camera = new SmartCamera(w, h) { ZoomIn = zoomIn };
        var frame = new byte[w * h * 4];
        var result = new byte[w * h * 4];
        var input = decoder.StandardOutput.BaseStream;
        var outStream = encoder.StandardInput.BaseStream;
        double dt = 1 / fps;
        var rings = new List<(Point At, double Since)>();

        await Task.Run(async () =>
        {
            for (int n = 0; ; n++)
            {
                if (!await ReadFull(input, frame)) break;
                double t = startOffset + n * dt;
                var (at, clicked) = track.At(t - dt, t);
                var p = new WPoint((at.X - area.X) * k, (at.Y - area.Y) * k);
                camera.Update(dt, p, clicked);
                if (clicked) rings.Add((new Point(p.X, p.Y), 0));

                var view = camera.View();
                Sample(frame, result, w, h, view.X, view.Y, view.Width, view.Height);

                // the pointer and click rings, where they land in the zoomed view
                double s = w / view.Width;
                Point Place(double x, double y) => new((x - view.X) * s, (y - view.Y) * s);
                for (int r = rings.Count - 1; r >= 0; r--)
                {
                    var (ringAt, since) = rings[r];
                    if (since > 0.45) { rings.RemoveAt(r); continue; }
                    var c = Place(ringAt.X, ringAt.Y);
                    Blend(result, w, h, ring, (int)(c.X - ring.W / 2.0), (int)(c.Y - ring.H / 2.0), 1 - since / 0.45);
                    rings[r] = (ringAt, since + dt);
                }
                var pc = Place(camera.Cursor.X, camera.Cursor.Y);
                Blend(result, w, h, cursor, (int)pc.X - 2, (int)pc.Y - 2, 1);

                await outStream.WriteAsync(result);
            }
        });
        outStream.Close();
        await decoder.WaitForExitAsync();
        await encoder.WaitForExitAsync();
        if (encoder.ExitCode != 0 || !File.Exists(output)) throw new InvalidOperationException("auto-zoom: " + await encoderErrors);
        File.Move(output, video, overwrite: true);
    }

    private static async Task<bool> ReadFull(Stream s, byte[] buffer)
    {
        int got = 0;
        while (got < buffer.Length)
        {
            int n = await s.ReadAsync(buffer.AsMemory(got));
            if (n == 0) return false;
            got += n;
        }
        return true;
    }

    /// <summary>Scales the view's part of the frame up to the whole frame (bilinear).</summary>
    private static void Sample(byte[] src, byte[] dst, int w, int h, double vx, double vy, double vw, double vh)
    {
        if (vw >= w - 0.5 && vh >= h - 0.5) { Buffer.BlockCopy(src, 0, dst, 0, src.Length); return; }
        double sx = vw / w, sy = vh / h;
        Parallel.For(0, h, y =>
        {
            double fy = vy + (y + 0.5) * sy - 0.5;
            int y0 = Math.Clamp((int)Math.Floor(fy), 0, h - 1), y1 = Math.Min(h - 1, y0 + 1);
            double ty = Math.Clamp(fy - y0, 0, 1);
            int o = y * w * 4;
            for (int x = 0; x < w; x++)
            {
                double fx = vx + (x + 0.5) * sx - 0.5;
                int x0 = Math.Clamp((int)Math.Floor(fx), 0, w - 1), x1 = Math.Min(w - 1, x0 + 1);
                double tx = Math.Clamp(fx - x0, 0, 1);
                int a = (y0 * w + x0) * 4, b = (y0 * w + x1) * 4, c = (y1 * w + x0) * 4, d = (y1 * w + x1) * 4;
                for (int ch = 0; ch < 3; ch++)
                {
                    double top = src[a + ch] + (src[b + ch] - src[a + ch]) * tx;
                    double bottom = src[c + ch] + (src[d + ch] - src[c + ch]) * tx;
                    dst[o + x * 4 + ch] = (byte)(top + (bottom - top) * ty + 0.5);
                }
                dst[o + x * 4 + 3] = 255;
            }
        });
    }

    private static void Blend(byte[] dst, int w, int h, Px sprite, int left, int top, double alpha)
    {
        for (int y = 0; y < sprite.H; y++)
        {
            int dy = top + y;
            if (dy < 0 || dy >= h) continue;
            for (int x = 0; x < sprite.W; x++)
            {
                int dx = left + x;
                if (dx < 0 || dx >= w) continue;
                int s = (y * sprite.W + x) * 4, d = (dy * w + dx) * 4;
                double a = sprite.D[s + 3] / 255.0 * alpha;
                if (a <= 0) continue;
                // the sprite is premultiplied
                for (int ch = 0; ch < 3; ch++) dst[d + ch] = (byte)Math.Clamp(sprite.D[s + ch] * alpha + dst[d + ch] * (1 - a), 0, 255);
            }
        }
    }

    /// <summary>A big, crisp arrow pointer.</summary>
    private static Px Sprite(double scale)
    {
        var shape = StreamGeometry.Parse("M2,2 L2,30 L9,23 L14,34 L19,32 L14,21 L24,21 Z");
        int size = (int)Math.Ceiling(40 * scale);
        return Px.From(Imaging.Render(new PixelSize(size, size), ctx =>
        {
            using (ctx.PushTransform(Matrix.CreateScale(scale, scale)))
                ctx.DrawGeometry(Brushes.White, new Pen(Brushes.Black, 2) { LineJoin = PenLineJoin.Round }, shape);
        }));
    }

    private static Px RingSprite(double scale)
    {
        int size = (int)Math.Ceiling(70 * scale);
        return Px.From(Imaging.Render(new PixelSize(size, size), ctx =>
            ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb(60, 79, 140, 255)), new Pen(new SolidColorBrush(Color.FromArgb(220, 79, 140, 255)), 3 * scale),
                new Point(size / 2.0, size / 2.0), size / 2.0 - 3 * scale, size / 2.0 - 3 * scale)));
    }
}
