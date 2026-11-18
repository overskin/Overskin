using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Overskin;

/// <summary>
/// "Convert to" in the Library: any capture into another format. Pictures, GIFs and MP4 videos
/// can each become PNG, JPG, BMP, TIFF, GIF or MP4. A still picture becomes a 5-second video or a
/// one-frame GIF; a GIF keeps its timing as a video; a video becomes an animated GIF (15 fps, up
/// to 30 seconds, at most 720 px wide). The new file goes next to the original.
/// </summary>
public static class Converter
{
    public static readonly string[] Targets = { "PNG", "JPG", "BMP", "TIFF", "GIF", "MP4" };

    private const int GifFps = 15, GifSeconds = 30, GifMaxWidth = 720;

    public static string Kind(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".gif" => "GIF",
        ".mp4" => "MP4",
        ".jpg" or ".jpeg" => "JPG",
        ".bmp" => "BMP",
        ".tif" or ".tiff" => "TIFF",
        _ => "PNG",
    };

    /// <summary>Converts and returns the new file's path. Throws with a readable message when it can't.</summary>
    public static async Task<string> ConvertAsync(string path, string target)
    {
        string from = Kind(path);
        if (from == target) throw new InvalidOperationException($"It's already a {target}.");
        string output = FreePath(Path.ChangeExtension(path, "." + target.ToLowerInvariant()));

        switch (target)
        {
            case "MP4":
                await ToVideo(path, from, output);
                break;
            case "GIF":
                var clip = from == "MP4" ? await VideoToClip(path) : GifClip.FromImage(await StillOf(path, from));
                await Task.Run(() => WriteGif(clip, output));
                break;
            default:
                var still = await StillOf(path, from);
                if (!ImageFiles.TrySave(still, output, out string? error)) throw new InvalidOperationException(error);
                break;
        }
        return output;
    }

    private static string FreePath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!, name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    /// <summary>A picture of the file: itself, a GIF's first frame, or a video's first frame.</summary>
    private static async Task<BitmapSource> StillOf(string path, string kind)
    {
        if (kind == "MP4")
        {
            var frames = await VideoFrames(path, new[] { TimeSpan.Zero }, maxWidth: 8192);
            return frames[0];
        }
        if (kind == "GIF") return GifReader.Read(path, 1).FrameBitmap(0);
        return ImageLoader.LoadFile(path);
    }

    private static void WriteGif(GifClip clip, string output)
    {
        string temp = output + ".tmp";
        using (var file = File.Create(temp))
        using (var writer = new GifWriter(file, clip.Width, clip.Height))
            for (int i = 0; i < clip.Count; i++) writer.AddFrame(clip.Frames[i], clip.DelaysMs[i]);
        File.Move(temp, output, overwrite: true);
    }

    private static async Task ToVideo(string path, string from, string output)
    {
        // frames and how long each one shows
        var frames = new List<(byte[] Pixels, int Ms)>();
        int w, h;
        if (from == "GIF")
        {
            var clip = await Task.Run(() => GifReader.Read(path));
            (w, h) = (clip.Width, clip.Height);
            for (int i = 0; i < clip.Count; i++) frames.Add((clip.Frames[i], Math.Max(20, clip.DelaysMs[i])));
        }
        else
        {
            var still = ImageLoader.Normalize(await StillOf(path, from));
            (w, h) = (still.PixelWidth, still.PixelHeight);
            var px = new byte[w * h * 4];
            still.CopyPixels(px, w * 4, 0);
            frames.Add((px, 5000)); // a still becomes five seconds
        }

        // H.264 wants even sizes, and Windows' encoder tops out at 4K
        double fit = Math.Min(1, Math.Min(3840.0 / w, 2160.0 / h));
        int ow = Math.Max(2, (int)(w * fit) & ~1), oh = Math.Max(2, (int)(h * fit) & ~1);
        const int Fps = 30;
        var queue = new BlockingCollection<VideoFrame>(8);
        var encode = Task.Run(() => VideoEncoder.EncodeAsync(output, ow, oh, Fps, queue));
        await Task.Run(() =>
        {
            double t = 0;
            foreach (var (pixels, ms) in frames)
            {
                var fitted = Fit(pixels, w, h, ow, oh);
                // repeat each frame for as long as it shows, so players get a steady frame rate
                int copies = Math.Max(1, (int)Math.Round(ms / 1000.0 * Fps));
                for (int c = 0; c < copies; c++)
                {
                    queue.Add(new VideoFrame(fitted, TimeSpan.FromSeconds(t)));
                    t += 1.0 / Fps;
                }
            }
            queue.CompleteAdding();
        });
        await encode;
    }

    /// <summary>Crops or scales BGRA pixels to the even size the encoder needs.</summary>
    private static byte[] Fit(byte[] px, int w, int h, int ow, int oh)
    {
        if (ow == w && oh == h) return px;
        if (ow <= w && oh <= h && w - ow <= 1 && h - oh <= 1)
        {
            var cropped = new byte[ow * oh * 4];
            for (int y = 0; y < oh; y++) Buffer.BlockCopy(px, y * w * 4, cropped, y * ow * 4, ow * 4);
            return cropped;
        }
        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        var scaled = new TransformedBitmap(src, new ScaleTransform(ow / (double)w, oh / (double)h));
        var outPx = new byte[ow * oh * 4];
        new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0).CopyPixels(new System.Windows.Int32Rect(0, 0, ow, oh), outPx, ow * 4, 0);
        return outPx;
    }

    /// <summary>A video as GIF frames: 15 a second, the first 30 seconds, at most 720 px wide.</summary>
    private static async Task<GifClip> VideoToClip(string path)
    {
        var file = await global::Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        var props = await file.Properties.GetVideoPropertiesAsync();
        double seconds = Math.Min(GifSeconds, Math.Max(0.1, props.Duration.TotalSeconds));
        int count = Math.Max(1, (int)(seconds * GifFps));
        var times = Enumerable.Range(0, count).Select(i => TimeSpan.FromSeconds(i / (double)GifFps)).ToList();
        var frames = await VideoFrames(path, times, GifMaxWidth);
        var clip = new GifClip { Width = frames[0].PixelWidth, Height = frames[0].PixelHeight };
        foreach (var f in frames)
        {
            var bgra = ImageLoader.Normalize(f);
            var px = new byte[clip.Width * clip.Height * 4];
            bgra.CopyPixels(new System.Windows.Int32Rect(0, 0, clip.Width, clip.Height), px, clip.Width * 4, 0);
            clip.Frames.Add(px);
            clip.DelaysMs.Add(1000 / GifFps);
        }
        return clip;
    }

    private static async Task<List<BitmapSource>> VideoFrames(string path, IList<TimeSpan> times, int maxWidth)
    {
        var file = await global::Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        var props = await file.Properties.GetVideoPropertiesAsync();
        int vw = (int)Math.Max(2, props.Width), vh = (int)Math.Max(2, props.Height);
        double k = Math.Min(1, maxWidth / (double)vw);
        int w = Math.Max(2, (int)(vw * k)), h = Math.Max(2, (int)(vh * k));

        var clip = await global::Windows.Media.Editing.MediaClip.CreateFromFileAsync(file);
        var composition = new global::Windows.Media.Editing.MediaComposition();
        composition.Clips.Add(clip);
        var limit = clip.OriginalDuration - TimeSpan.FromMilliseconds(1);
        var safe = times.Select(t => t > limit ? limit : t).ToList();
        var thumbs = await composition.GetThumbnailsAsync(safe, w, h, global::Windows.Media.Editing.VideoFramePrecision.NearestFrame);

        var result = new List<BitmapSource>();
        foreach (var thumb in thumbs)
        {
            using (thumb)
            {
                var copy = new MemoryStream();
                await thumb.AsStreamForRead().CopyToAsync(copy);
                copy.Position = 0;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = copy;
                bmp.EndInit();
                bmp.Freeze();
                result.Add(bmp);
            }
        }
        if (result.Count == 0) throw new InvalidOperationException("Couldn't read frames from the video.");
        return result;
    }
}
