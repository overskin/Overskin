using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Overskin.Linux.Core;

namespace Overskin.Linux.Platform;

/// <summary>One stretch of a recording that's zoomed in, following the pointer.</summary>
public sealed class ZoomSegment
{
    public double Start { get; set; }
    public double End { get; set; }
    public double Zoom { get; set; } = 1.8;
}

/// <summary>
/// Auto-zoom you can change afterwards (as on Windows). The recording itself is kept unzoomed (in a
/// hidden ".overskin" folder next to the video), with where the pointer was and where it clicked;
/// the zooms are worked out from the clicks and applied when the video is made. The video editor
/// can move, remove and add zooms and make the video again from the unzoomed recording.
/// </summary>
public sealed class ZoomTrack
{
    public int Width { get; set; }
    public int Height { get; set; }
    public double Fps { get; set; } = 30;
    public double ZoomRate { get; set; } = 1.6 * Math.Sqrt(7);
    public double PanRate { get; set; } = 12 * Math.Sqrt(0.12);
    /// <summary>Where the pointer was: time, x, y (video pixels).</summary>
    public List<float[]> Pointer { get; set; } = new();
    /// <summary>Clicks: time, x, y.</summary>
    public List<float[]> Clicks { get; set; } = new();
    public List<ZoomSegment> Zooms { get; set; } = new();

    // how the recording is finished, kept so it's the same when the zooms are changed later
    /// <summary>Overskin's own pointer is drawn (the system's wasn't recorded).</summary>
    public bool DrawPointer { get; set; }
    public string PointerStyle { get; set; } = "White";
    public double PointerSize { get; set; } = 1.5;
    /// <summary>Video pixels per point on the screen (2 on a Retina display).</summary>
    public double PointScale { get; set; } = 1;
    /// <summary>0 to 100: how much the pointer's path is smoothed.</summary>
    public int PointerSmoothing { get; set; } = 45;
    /// <summary>0 to 100: how much a fast pointer streaks.</summary>
    public int MotionBlur { get; set; } = 50;
    public bool ClickRings { get; set; } = true;
    public List<KeyPress> Keys { get; set; } = new();
    public string Backdrop { get; set; } = "None";

    /// <summary>Whether there's anything to draw at all.</summary>
    public bool Anything => Zooms.Count > 0 || DrawPointer || (ClickRings && Clicks.Count > 0) || Keys.Count > 0 || (Backdrop.Length > 0 && Backdrop != "None");

    /// <summary>How long a zoom stays in after the last click.</summary>
    public const double Hold = 2.5;

    private static string Hidden(string video, bool make)
    {
        string dir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(video)) ?? "", ".overskin");
        if (make) Directory.CreateDirectory(dir);
        return dir;
    }

    public static string RawPath(string video) => Path.Combine(Hidden(video, true), Path.GetFileNameWithoutExtension(video) + ".raw.mp4");
    public static string TrackPath(string video) => Path.Combine(Hidden(video, true), Path.GetFileNameWithoutExtension(video) + ".zooms.json");

    /// <summary>Removes a video's unzoomed recording and zoom track, when it's deleted.</summary>
    public static void DeleteFor(string video)
    {
        try
        {
            string dir = Hidden(video, false), name = Path.GetFileNameWithoutExtension(video);
            foreach (var f in new[] { Path.Combine(dir, name + ".raw.mp4"), Path.Combine(dir, name + ".zooms.json") })
                if (File.Exists(f)) File.Delete(f);
        }
        catch { }
    }

    public void Save(string video) => File.WriteAllText(TrackPath(video), JsonSerializer.Serialize(this));

    /// <summary>The zoom track of a video, when it was recorded with auto-zoom and its unzoomed recording is still there.</summary>
    public static ZoomTrack? Load(string video)
    {
        try
        {
            string dir = Hidden(video, false), name = Path.GetFileNameWithoutExtension(video);
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

    /// <summary>
    /// The track for a recording: where the pointer was at each frame and when it clicked (in video
    /// pixels and seconds of the video), and the zooms those clicks make.
    /// </summary>
    public static ZoomTrack FromPointer(PointerTrack pointer, PixelRect area, int w, int h, double fps, double duration, double startOffset, double zoom, double hold = Hold,
        Func<double, double>? clock = null)
    {
        var track = new ZoomTrack { Width = w, Height = h, Fps = fps };
        double k = w / (double)Math.Max(1, area.Width), dt = 1 / Math.Max(1, fps);
        for (double t = 0; t <= duration + dt / 2; t += dt)
        {
            double now = clock?.Invoke(t) ?? startOffset + t, before = clock?.Invoke(t - dt) ?? startOffset + t - dt;
            var (at, clicked) = pointer.At(before, now);
            float x = (float)((at.X - area.X) * k), y = (float)((at.Y - area.Y) * k);
            track.Pointer.Add(new[] { (float)t, x, y });
            if (clicked) track.Clicks.Add(new[] { (float)t, x, y });
        }
        track.Zooms = zoom > 1 ? FromClicks(track.Clicks, hold, zoom) : new();
        return track;
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

    /// <summary>Where the pointer was, smoothed (so a shaky hand glides), as on Windows.</summary>
    public Point SmoothAt(double t)
    {
        if (PointerSmoothing <= 0 || Pointer.Count < 2) return PointerAt(t);
        double sigma = 0.13 * Math.Pow(PointerSmoothing / 100.0, 1.1);
        double x = 0, y = 0, wsum = 0;
        for (int k = -6; k <= 6; k++)
        {
            double dt = k * sigma / 3, wgt = Math.Exp(-(dt * dt) / (2 * sigma * sigma));
            var p = PointerAt(t + dt);
            x += p.X * wgt; y += p.Y * wgt; wsum += wgt;
        }
        return new Point(x / wsum, y / wsum);
    }

    /// <summary>
    /// Makes the zoomed video from the unzoomed recording: each frame is the part of it the zooms
    /// say, following the pointer, scaled back up to full size, with a big pointer and a ring on
    /// each click (as the first recording had); the sound is carried across unchanged.
    /// </summary>
    public static async Task RenderAsync(string raw, string output, ZoomTrack track, IProgress<double>? progress = null)
    {
        var probe = await ScreenGrabber.OutputOf("ffprobe", $"-v error -select_streams v:0 -show_entries stream=width,height,avg_frame_rate -of csv=p=0 \"{raw}\"");
        var f = probe?.Trim().Split(',');
        if (f is not { Length: 3 }) throw new InvalidOperationException("couldn't read the recording's size");
        int w = int.Parse(f[0]), h = int.Parse(f[1]);
        var rate = f[2].Split('/');
        double fps = double.Parse(rate[0], CultureInfo.InvariantCulture) / Math.Max(1, double.Parse(rate.Length > 1 ? rate[1] : "1", CultureInfo.InvariantCulture));
        if (fps <= 0 || fps > 240) fps = Math.Max(1, track.Fps);
        double length = Math.Max(0.1, track.Pointer.Count > 0 ? track.Pointer[^1][0] : 1);
        double sx = w / (double)Math.Max(1, track.Width), sy = h / (double)Math.Max(1, track.Height);
        // the pictures drawn into frames, made before the frames go through
        bool dark = track.PointerStyle == "Black";
        double baseSize = 32 * track.PointerSize * track.PointScale * sx;
        double maxZoom = track.Zooms.Count > 0 ? track.Zooms.Max(z => z.Zoom) : 1;
        var pointers = new SortedDictionary<int, Sprite>();
        if (track.DrawPointer)
            for (double z = 1; z <= maxZoom + 0.05; z += 0.05)
            {
                int size = Math.Max(12, (int)Math.Round(baseSize * z / 2) * 2);
                if (!pointers.ContainsKey(size)) pointers[size] = RecordingLook.Pointer(size, dark);
            }
        var labels = track.Keys.Select(k => k.Label).Distinct().ToDictionary(l => l, l => RecordingLook.KeyLabel(l, h));
        var backdrop = VideoBackdrop.Create(track.Backdrop, w, h);
        double shutter = Math.Clamp(track.MotionBlur, 0, 100) / 100.0;
        string temp = output + ".part.mp4";

        using var decoder = Process.Start(new ProcessStartInfo("ffmpeg", $"-nostdin -hide_banner -loglevel error -i \"{raw}\" -f rawvideo -pix_fmt bgra -")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        using var encoder = Process.Start(new ProcessStartInfo("ffmpeg",
            $"-hide_banner -loglevel error -y -f rawvideo -pix_fmt bgra -s {w}x{h} -r {fps.ToString(CultureInfo.InvariantCulture)} -i - -i \"{raw}\" " +
            $"-map 0:v -map 1:a? -c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p -c:a copy -shortest -movflags +faststart \"{temp}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true })!;
        _ = decoder.StandardError.ReadToEndAsync();
        var encoderErrors = encoder.StandardError.ReadToEndAsync();
        var frame = new byte[w * h * 4];
        var result = new byte[w * h * 4];
        var input = decoder.StandardOutput.BaseStream;
        var outStream = encoder.StandardInput.BaseStream;
        Point centre = new(w / 2.0, h / 2.0), target = centre;
        double dt = 1 / fps;

        await Task.Run(async () =>
        {
            for (int n = 0; ; n++)
            {
                if (!await AutoZoom.ReadFull(input, frame)) break;
                double t = n * dt;
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
                AutoZoom.Sample(frame, result, w, h, left, top, vw, vh);

                // the click rings and the pointer, where they land in the zoomed view
                double s = w / vw;
                Point Place(double x, double y) => new((x - left) * s, (y - top) * s);
                if (track.ClickRings)
                    foreach (var c in track.Clicks)
                    {
                        double age = t - c[0];
                        if (age < 0 || age > 0.5) continue;
                        double grow = 1 - Math.Pow(1 - age / 0.5, 3);
                        var at = Place(c[1] * sx, c[2] * sy);
                        double k = track.PointScale * sx * s;
                        RecordingLook.Ring(result, w, h, at.X, at.Y, (8 + 26 * grow) * k, Math.Max(1.5, 2.6 * k), 1 - age / 0.5);
                    }
                if (track.DrawPointer && pointers.Count > 0)
                {
                    int want = (int)Math.Round(baseSize * s / 2) * 2;
                    var sprite = pointers.MinBy(kv => Math.Abs(kv.Key - want)).Value;
                    // fast moves streak a little, as a camera's shutter would show them
                    var from = track.SmoothAt(t - shutter * dt);
                    var to = track.SmoothAt(t);
                    var a0 = Place(from.X * sx, from.Y * sy);
                    var a1 = Place(to.X * sx, to.Y * sy);
                    double dist = Math.Sqrt((a1.X - a0.X) * (a1.X - a0.X) + (a1.Y - a0.Y) * (a1.Y - a0.Y));
                    int steps = Math.Clamp((int)(dist / 3), 1, 8);
                    for (int i = 0; i < steps; i++)
                    {
                        double q = steps == 1 ? 1 : i / (double)(steps - 1);
                        RecordingLook.Blend(result, w, h, sprite, a0.X + (a1.X - a0.X) * q, a0.Y + (a1.Y - a0.Y) * q, steps == 1 ? 1 : 1.6 / steps);
                    }
                }
                var finished = backdrop != null ? backdrop.Compose(backdrop.Shrink(result, w, h)) : result;
                if (labels.Count > 0) RecordingLook.DrawKey(finished, w, h, track.Keys, labels, t);
                await outStream.WriteAsync(finished);
                if (n % 15 == 0) progress?.Report(Math.Min(1, t / length));
            }
        });
        outStream.Close();
        await decoder.WaitForExitAsync();
        await encoder.WaitForExitAsync();
        if (encoder.ExitCode != 0 || !File.Exists(temp)) throw new InvalidOperationException("making the zooms: " + await encoderErrors);
        File.Move(temp, output, overwrite: true);
    }
}
