using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Overskin.Linux.Core;
using WPoint = System.Windows.Point;

namespace Overskin.Linux.Platform;

/// <summary>What a recording's finishing pass does (see <see cref="AutoZoom.FinishAsync"/>).</summary>
public sealed class RecordFinish
{
    /// <summary>How far to zoom in on clicks; 0 or 1 for no zooming.</summary>
    public double Zoom { get; init; }
    public bool DrawPointer { get; init; }
    /// <summary>Picture pixels per point on the screen (2 on a Retina display).</summary>
    public double PointScale { get; init; } = 1;
    public RecordLook Look { get; init; } = new();
    public List<KeyPress> Keys { get; init; } = new();
    public bool Editable { get; init; }
}

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

    /// <summary>For the self-test: a pretend pointer position at a time.</summary>
    public void AddForTest(double t, double x, double y, bool down = false)
    {
        lock (_samples) _samples.Add(new Sample(t, x, y, down));
    }

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
    /// <summary>
    /// Zooms a recording in on each click (as on Windows, and editable afterwards), drawing
    /// Overskin's own pointer and the click rings (the system pointer wasn't recorded).
    /// </summary>
    public static Task ApplyAsync(string video, PointerTrack track, PixelRect area, double zoomIn, double startOffset) =>
        FinishAsync(video, track, area, startOffset, new RecordFinish { Zoom = zoomIn, DrawPointer = true, Look = new RecordLook(), Editable = true });

    /// <summary>
    /// Finishes a recording (as on Windows): zooms in on clicks and follows the pointer, draws a
    /// smooth pointer, click rings and shortcut labels, and lays it on a backdrop, as asked. With
    /// editable zooms the plain recording is kept next to it (in a hidden ".overskin" folder) with
    /// where the pointer was, so the video editor can change the zooms later. The sound is kept.
    /// </summary>
    /// <param name="clock">Where a moment of the video was on the pointer's clock (pauses skipped); without it, <paramref name="startOffset"/> plus the time.</param>
    public static async Task FinishAsync(string video, PointerTrack? pointer, PixelRect area, double startOffset, RecordFinish o, Func<double, double>? clock = null)
    {
        var probe = await ScreenGrabber.OutputOf("ffprobe", $"-v error -select_streams v:0 -show_entries stream=width,height,avg_frame_rate:format=duration -of csv=p=0 \"{video}\"");
        var lines = probe?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? Array.Empty<string>();
        var f = lines.FirstOrDefault(l => l.Split(',').Length == 3)?.Split(',');
        if (f is not { Length: 3 }) throw new InvalidOperationException("couldn't read the video's size");
        int w = int.Parse(f[0]), h = int.Parse(f[1]);
        var rate = f[2].Split('/');
        double fps = double.Parse(rate[0], CultureInfo.InvariantCulture) / Math.Max(1, double.Parse(rate.Length > 1 ? rate[1] : "1", CultureInfo.InvariantCulture));
        if (fps <= 0 || fps > 240) fps = 30;
        double duration = lines.Select(l => double.TryParse(l, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0).FirstOrDefault(d => d > 0);
        if (duration <= 0) duration = 1;

        var track = pointer != null
            ? ZoomTrack.FromPointer(pointer, area, w, h, fps, duration, startOffset, o.Zoom, o.Look.ZoomHold, clock)
            : new ZoomTrack { Width = w, Height = h, Fps = fps };
        track.ZoomRate = o.Look.ZoomRate;
        track.PanRate = o.Look.PanRate;
        track.DrawPointer = o.DrawPointer && pointer != null;
        track.PointerStyle = o.Look.PointerStyle;
        track.PointerSize = o.Look.PointerSize;
        track.PointScale = o.PointScale * w / Math.Max(1.0, area.Width);
        track.PointerSmoothing = o.Look.PointerSmoothing;
        track.MotionBlur = o.Look.MotionBlur;
        track.ClickRings = o.Look.ClickRings;
        track.Keys = o.Keys;
        track.Backdrop = o.Look.Backdrop;
        if (!track.Anything) return;

        string raw;
        if (o.Editable && track.Zooms.Count > 0)
        {
            raw = ZoomTrack.RawPath(video);
            File.Copy(video, raw, overwrite: true);
            track.Save(video);
        }
        else
        {
            raw = Path.Combine(Path.GetTempPath(), $"overskin-plain-{Guid.NewGuid():N}.mp4");
            File.Copy(video, raw, overwrite: true);
        }
        try { await ZoomTrack.RenderAsync(raw, video, track); }
        finally { if (!(o.Editable && track.Zooms.Count > 0)) try { File.Delete(raw); } catch { } }
    }

    /// <summary>
    /// Darkens the video a little except round where the pointer was (the same soft circle as on
    /// Windows), frame by frame through ffmpeg, from the pointer's recorded path.
    /// </summary>
    public static async Task DimAsync(string video, PointerTrack track, PixelRect area, double startOffset, Func<double, double>? clock = null)
    {
        var probe = await ScreenGrabber.OutputOf("ffprobe", $"-v error -select_streams v:0 -show_entries stream=width,height,avg_frame_rate -of csv=p=0 \"{video}\"");
        var f = probe?.Trim().Split(',');
        if (f is not { Length: 3 }) throw new InvalidOperationException("couldn't read the video's size");
        int w = int.Parse(f[0]), h = int.Parse(f[1]);
        var rate = f[2].Split('/');
        double fps = double.Parse(rate[0], CultureInfo.InvariantCulture) / Math.Max(1, double.Parse(rate.Length > 1 ? rate[1] : "1", CultureInfo.InvariantCulture));
        if (fps <= 0 || fps > 240) fps = 30;
        double k = w / (double)Math.Max(1, area.Width);
        var output = Path.Combine(Path.GetDirectoryName(video)!, Path.GetFileNameWithoutExtension(video) + ".dim.mp4");
        using var decoder = Process.Start(new ProcessStartInfo("ffmpeg", $"-nostdin -hide_banner -loglevel error -i \"{video}\" -f rawvideo -pix_fmt bgra -")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        // the sound, if there is any, is carried across unchanged
        using var encoder = Process.Start(new ProcessStartInfo("ffmpeg",
            $"-hide_banner -loglevel error -y -f rawvideo -pix_fmt bgra -s {w}x{h} -r {fps.ToString(CultureInfo.InvariantCulture)} -i - -i \"{video}\" " +
            $"-map 0:v -map 1:a? -c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p -c:a copy -movflags +faststart \"{output}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true })!;
        _ = decoder.StandardError.ReadToEndAsync();
        var encoderErrors = encoder.StandardError.ReadToEndAsync();
        var frame = new byte[w * h * 4];
        var input = decoder.StandardOutput.BaseStream;
        var outStream = encoder.StandardInput.BaseStream;
        double radius = Overskin.Spotlight.Radius(w, h);
        await Task.Run(async () =>
        {
            for (int n = 0; ; n++)
            {
                if (!await ReadFull(input, frame)) break;
                double t = clock?.Invoke(n / fps) ?? startOffset + n / fps;
                var (at, _) = track.At(t, t);
                Overskin.Spotlight.Apply(frame, w, h, (at.X - area.X) * k, (at.Y - area.Y) * k, radius);
                await outStream.WriteAsync(frame);
            }
        });
        outStream.Close();
        await decoder.WaitForExitAsync();
        await encoder.WaitForExitAsync();
        if (encoder.ExitCode != 0 || !File.Exists(output)) throw new InvalidOperationException("dim around the pointer: " + await encoderErrors);
        File.Move(output, video, overwrite: true);
    }

    internal static async Task<bool> ReadFull(Stream s, byte[] buffer)
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
    internal static void Sample(byte[] src, byte[] dst, int w, int h, double vx, double vy, double vw, double vh)
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

    internal static void Blend(byte[] dst, int w, int h, Px sprite, int left, int top, double alpha)
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
    internal static Px Sprite(double scale)
    {
        var shape = StreamGeometry.Parse("M2,2 L2,30 L9,23 L14,34 L19,32 L14,21 L24,21 Z");
        int size = (int)Math.Ceiling(40 * scale);
        return Px.From(Imaging.Render(new PixelSize(size, size), ctx =>
        {
            using (ctx.PushTransform(Matrix.CreateScale(scale, scale)))
                ctx.DrawGeometry(Brushes.White, new Pen(Brushes.Black, 2) { LineJoin = PenLineJoin.Round }, shape);
        }));
    }

    internal static Px RingSprite(double scale)
    {
        int size = (int)Math.Ceiling(70 * scale);
        return Px.From(Imaging.Render(new PixelSize(size, size), ctx =>
            ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb(60, 79, 140, 255)), new Pen(new SolidColorBrush(Color.FromArgb(220, 79, 140, 255)), 3 * scale),
                new Point(size / 2.0, size / 2.0), size / 2.0 - 3 * scale, size / 2.0 - 3 * scale)));
    }
}
