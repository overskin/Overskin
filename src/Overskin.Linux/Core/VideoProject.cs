using System.Globalization;
using System.Text;
using Avalonia;
using Overskin.Linux.Interface;

namespace Overskin.Linux.Core;

/// <summary>A part of the video blurred for a while (a private detail): where, as fractions of the whole frame, and when.</summary>
public sealed class VideoBlur
{
    public Rect Area { get; set; }
    public double Start { get; set; }
    public double End { get; set; }
    /// <summary>What it hides, when it was found ("email", "phone number"…).</summary>
    public string Label { get; set; } = "";
    public VideoBlur Clone() => (VideoBlur)MemberwiseClone();
}

/// <summary>
/// An edit of a video: what's kept (trimmed ends, cut-out middles), the crop, the speed, and the
/// text and subtitles on it. Everything needed to make the finished video in one ffmpeg pass.
/// </summary>
public sealed class VideoProject
{
    public double Duration { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public bool HasSound { get; init; }

    public double Start { get; set; }
    public double End { get; set; }
    /// <summary>Middle parts taken out, in seconds of the original.</summary>
    public List<(double From, double To)> Cuts { get; } = new();
    /// <summary>The part kept, as fractions of the frame; null for all of it.</summary>
    public Rect? Crop { get; set; }
    public double Speed { get; set; } = 1;
    public List<VideoOverlay> Overlays { get; } = new();
    public List<VideoBlur> Blurs { get; } = new();
    /// <summary>A smaller size to make it at (to fit a file size), or null for the crop's own size.</summary>
    public PixelSize? ScaleTo { get; set; }

    /// <summary>The stretches of the original that are kept, in order.</summary>
    public List<(double From, double To)> Kept()
    {
        var kept = new List<(double, double)> { (Math.Max(0, Start), Math.Min(Duration, End <= 0 ? Duration : End)) };
        foreach (var (a, b) in Cuts.OrderBy(c => c.From))
        {
            var next = new List<(double, double)>();
            foreach (var (x, y) in kept)
            {
                if (b <= x || a >= y) { next.Add((x, y)); continue; }
                if (a > x) next.Add((x, a));
                if (b < y) next.Add((b, y));
            }
            kept = next;
        }
        return kept.Where(k => k.Item2 - k.Item1 > 0.04).ToList();
    }

    /// <summary>How long the finished video is.</summary>
    public double OutputLength => Kept().Sum(k => k.To - k.From) / Speed;

    /// <summary>Where a moment of the original lands in the finished video; null when it's cut out.</summary>
    public double? ToOutput(double t)
    {
        double before = 0;
        foreach (var (a, b) in Kept())
        {
            if (t < a) return null;
            if (t <= b) return (before + t - a) / Speed;
            before += b - a;
        }
        return null;
    }

    /// <summary>An overlay's time on the finished video (the part of it that's kept), or null if none is.</summary>
    public (double From, double To)? OutputSpan(double from, double to)
    {
        // the kept part: from the first kept moment at or after its start to the last at or before its end
        double? s = null, e = null;
        foreach (var (a, b) in Kept())
        {
            double x = Math.Max(a, from), y = Math.Min(b, to);
            if (y <= x) continue;
            s ??= ToOutput(x);
            e = ToOutput(y);
        }
        return s is { } ss && e is { } ee && ee > ss ? (ss, ee) : null;
    }

    /// <summary>The crop in the video's own pixels, with even sizes (H.264 needs them).</summary>
    public PixelRect CropPixels()
    {
        if (Crop is not { } c) return new PixelRect(0, 0, Width - Width % 2, Height - Height % 2);
        int x = (int)Math.Round(c.X * Width), y = (int)Math.Round(c.Y * Height);
        int w = Math.Max(16, (int)Math.Round(c.Width * Width)), h = Math.Max(16, (int)Math.Round(c.Height * Height));
        w = Math.Min(w, Width - x); h = Math.Min(h, Height - y);
        return new PixelRect(x, y, w - w % 2, h - h % 2);
    }

    /// <summary>The subtitles as an .srt file, with times on the finished video.</summary>
    public string Srt()
    {
        var lines = Overlays.Where(o => o.Kind == VideoOverlay.Kinds.Caption).OrderBy(o => o.Start)
            .Select(o => (Span: OutputSpan(o.Start, o.End), o.Text)).Where(x => x.Span != null)
            .Select(x => new Overskin.Caption(x.Span!.Value.From, x.Span.Value.To, x.Text));
        return Overskin.Subtitles.ToSrt(lines);
    }

    /// <summary>atempo only goes from 0.5 to 2, so other speeds are a chain of them.</summary>
    public static string Tempo(double speed)
    {
        var parts = new List<string>();
        while (speed > 2.0001) { parts.Add("atempo=2"); speed /= 2; }
        while (speed < 0.4999) { parts.Add("atempo=0.5"); speed /= 0.5; }
        parts.Add("atempo=" + speed.ToString("0.#####", CultureInfo.InvariantCulture));
        return string.Join(",", parts);
    }

    /// <summary>
    /// The ffmpeg filter that makes the finished video: the kept parts joined, cropped, sped up or
    /// slowed down, with each overlay picture (inputs 1 onwards) shown for its time.
    /// </summary>
    public string Filter(IReadOnlyList<(double From, double To)> overlayTimes)
    {
        string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        var kept = Kept();
        var f = new StringBuilder();
        // blur boxes first, on the whole video, at the original's times
        string source = "[0:v]";
        var blurs = Blurs.Where(b => b.End > b.Start && b.Area.Width > 0 && b.Area.Height > 0).ToList();
        for (int i = 0; i < blurs.Count; i++)
        {
            var b = blurs[i];
            int x = Math.Clamp((int)Math.Round(b.Area.X * Width), 0, Math.Max(0, Width - 2)), y = Math.Clamp((int)Math.Round(b.Area.Y * Height), 0, Math.Max(0, Height - 2));
            int w = Math.Clamp((int)Math.Round(b.Area.Width * Width), 2, Width - x), h = Math.Clamp((int)Math.Round(b.Area.Height * Height), 2, Height - y);
            double sigma = Math.Clamp(Math.Min(w, h) / 5.0, 4, 30);
            string label = $"[bz{i}]";
            f.Append($"{source}split[bb{i}][bt{i}];[bt{i}]crop={w}:{h}:{x}:{y},gblur=sigma={N(sigma)}:steps=3[bg{i}];" +
                     $"[bb{i}][bg{i}]overlay={x}:{y}:enable='between(t,{N(b.Start)},{N(b.End)})'{label};");
            source = label;
        }
        var parts = new List<string>();
        if (source == "[0:v]" || kept.Count == 1) parts.AddRange(Enumerable.Repeat(source, kept.Count));
        else
        {
            f.Append($"{source}split={kept.Count}{string.Concat(Enumerable.Range(0, kept.Count).Select(i => $"[sp{i}]"))};");
            parts.AddRange(Enumerable.Range(0, kept.Count).Select(i => $"[sp{i}]"));
        }
        for (int i = 0; i < kept.Count; i++)
        {
            f.Append($"{parts[i]}trim=start={N(kept[i].From)}:end={N(kept[i].To)},setpts=PTS-STARTPTS[v{i}];");
            if (HasSound) f.Append($"[0:a]atrim=start={N(kept[i].From)}:end={N(kept[i].To)},asetpts=PTS-STARTPTS[a{i}];");
        }
        for (int i = 0; i < kept.Count; i++) f.Append(HasSound ? $"[v{i}][a{i}]" : $"[v{i}]");
        f.Append($"concat=n={kept.Count}:v=1:a={(HasSound ? 1 : 0)}{(HasSound ? "[vc][ac];" : "[vc];")}");
        var c = CropPixels();
        f.Append($"[vc]crop={c.Width}:{c.Height}:{c.X}:{c.Y},setpts=PTS/{N(Speed)}[v]");
        if (HasSound) f.Append($";[ac]{Tempo(Speed)}[a]");
        string last = "v";
        for (int i = 0; i < overlayTimes.Count; i++)
        {
            f.Append($";[{last}][{i + 1}:v]overlay=0:0:enable='between(t,{N(overlayTimes[i].From)},{N(overlayTimes[i].To)})'[o{i}]");
            last = $"o{i}";
        }
        string scale = ScaleTo is { } s ? $"scale={s.Width}:{s.Height}:flags=lanczos," : "";
        f.Append($";[{last}]{scale}format=yuv420p[out]");
        return f.ToString();
    }
}
