using System.Collections.Concurrent;
using System.IO;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Storage;

namespace Overskin;

/// <summary>
/// Makes a video again frame by frame: each frame of the input is read, changed (zoomed, blurred)
/// and written out, and then the input's sound is put back under the new picture.
/// </summary>
internal static class FrameRender
{
    /// <summary>
    /// Changes a frame: the input's pixels and size, the output's size (the input's, made even),
    /// and the time in seconds; returns the output's pixels.
    /// </summary>
    public delegate byte[] Process(byte[] pixels, int srcWidth, int srcHeight, int width, int height, double time);

    public static async Task RenderAsync(string input, string output, bool highQuality, IProgress<double>? progress, double length, Process process, int fps = 0)
    {
        string silent = Path.Combine(Path.GetTempPath(), $"Overskin-frames-{Guid.NewGuid():N}.mp4");
        try
        {
            await Task.Run(() =>
            {
                using var reader = new MediaReader(input);
                int w = reader.Width & ~1, h = reader.Height & ~1;
                int rate = fps > 0 ? fps : (int)Math.Round(reader.FrameRate);
                var queue = new BlockingCollection<VideoFrame>(boundedCapacity: 6);
                var encoding = Task.Run(() => VideoEncoder.EncodeAsync(silent, w, h, Math.Max(1, rate), queue, highQuality));
                try
                {
                    while (reader.Next() is { } frame)
                    {
                        double t = frame.Time.TotalSeconds;
                        queue.Add(new VideoFrame(process(frame.Pixels, reader.Width, reader.Height, w, h, t), frame.Time));
                        progress?.Report(Math.Min(0.9, t / Math.Max(0.1, length) * 0.9));
                        if (encoding.IsCompleted) break; // the encoder gave up: its error is thrown below
                    }
                }
                finally
                {
                    queue.CompleteAdding();
                }
                encoding.GetAwaiter().GetResult();
            });

            // the sound from the input, back under the new picture
            var picture = await StorageFile.GetFileFromPathAsync(silent);
            var original = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(input));
            var composition = new MediaComposition();
            composition.Clips.Add(await MediaClip.CreateFromFileAsync(picture));
            try { composition.BackgroundAudioTracks.Add(await BackgroundAudioTrack.CreateFromFileAsync(original)); } catch { }
            var profile = await MediaEncodingProfile.CreateFromFileAsync(picture);
            if (profile.Video != null) profile.Video.Bitrate = Math.Max(profile.Video.Bitrate, highQuality ? 8_000_000u : 4_000_000u);
            if (composition.BackgroundAudioTracks.Count > 0)
            {
                var withSound = await MediaEncodingProfile.CreateFromFileAsync(original);
                profile.Audio = withSound.Audio;
            }
            var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(Path.GetFullPath(output))!);
            var target = await folder.CreateFileAsync(Path.GetFileName(output), CreationCollisionOption.ReplaceExisting);
            var render = composition.RenderToFileAsync(target, MediaTrimmingPreference.Precise, profile);
            if (progress != null) render.Progress = (_, p) => progress.Report(0.9 + p / 1000.0);
            var result = await render;
            if (result != Windows.Media.Transcoding.TranscodeFailureReason.None)
                throw new InvalidOperationException("Windows couldn't save the video (" + result + ").");
        }
        finally
        {
            try { File.Delete(silent); } catch { }
        }
    }
}

/// <summary>A box in a video that's blurred between two times, such as a name or an email address.</summary>
public sealed class VideoBlur
{
    /// <summary>In video pixels.</summary>
    public System.Windows.Rect Area { get; set; }
    public double Start { get; set; }
    public double End { get; set; }
    /// <summary>What it hides, when it was found by "Find private details" (shown in the list).</summary>
    public string Label { get; set; } = "";

    /// <summary>Makes the video again with every box blurred while it's due.</summary>
    public static Task RenderAsync(string input, string output, IReadOnlyList<VideoBlur> blurs, double length, IProgress<double>? progress = null) =>
        FrameRender.RenderAsync(input, output, true, progress, length, (src, srcW, srcH, w, h, t) =>
        {
            var pixels = ZoomTrack.Crop(src, srcW, w, h);
            foreach (var b in blurs)
                if (t >= b.Start && t < b.End) Blur(pixels, w, h, b.Area);
            return pixels;
        });

    /// <summary>
    /// Blurs a box heavily: averages it into a coarse grid and spreads that back out smoothly,
    /// so nothing underneath can be read, but it still looks soft rather than blocky.
    /// </summary>
    public static void Blur(byte[] pixels, int w, int h, System.Windows.Rect area)
    {
        int x0 = Math.Clamp((int)Math.Floor(area.X), 0, w), y0 = Math.Clamp((int)Math.Floor(area.Y), 0, h);
        int x1 = Math.Clamp((int)Math.Ceiling(area.Right), 0, w), y1 = Math.Clamp((int)Math.Ceiling(area.Bottom), 0, h);
        int bw = x1 - x0, bh = y1 - y0;
        if (bw < 2 || bh < 2) return;
        int cell = Math.Max(8, Math.Min(bw, bh) / 3);
        int gw = Math.Max(1, (bw + cell - 1) / cell), gh = Math.Max(1, (bh + cell - 1) / cell);
        var grid = new double[gw * gh * 3];
        var count = new int[gw * gh];
        for (int y = y0; y < y1; y++)
        {
            int gy = (y - y0) / cell, row = y * w * 4;
            for (int x = x0; x < x1; x++)
            {
                int g = gy * gw + (x - x0) / cell, i = row + x * 4;
                grid[g * 3] += pixels[i];
                grid[g * 3 + 1] += pixels[i + 1];
                grid[g * 3 + 2] += pixels[i + 2];
                count[g]++;
            }
        }
        for (int g = 0; g < count.Length; g++)
            if (count[g] > 0) for (int c = 0; c < 3; c++) grid[g * 3 + c] /= count[g];
        Parallel.For(y0, y1, y =>
        {
            double fy = Math.Clamp((y - y0 + 0.5) / cell - 0.5, 0, gh - 1);
            int ga = (int)fy, gb = Math.Min(gh - 1, ga + 1);
            double ky = fy - ga;
            int row = y * w * 4;
            for (int x = x0; x < x1; x++)
            {
                double fx = Math.Clamp((x - x0 + 0.5) / cell - 0.5, 0, gw - 1);
                int xa = (int)fx, xb = Math.Min(gw - 1, xa + 1);
                double kx = fx - xa;
                int i = row + x * 4;
                for (int c = 0; c < 3; c++)
                {
                    double top = grid[(ga * gw + xa) * 3 + c] * (1 - kx) + grid[(ga * gw + xb) * 3 + c] * kx;
                    double bottom = grid[(gb * gw + xa) * 3 + c] * (1 - kx) + grid[(gb * gw + xb) * 3 + c] * kx;
                    pixels[i + c] = (byte)Math.Clamp(top * (1 - ky) + bottom * ky + 0.5, 0, 255);
                }
            }
        });
    }

    public VideoBlur Clone() => (VideoBlur)MemberwiseClone();
}
