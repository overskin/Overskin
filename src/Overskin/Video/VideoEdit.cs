using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace Overskin;

/// <summary>A label or an arrow shown over a video between two moments (seconds of the original).</summary>
public sealed class VideoOverlay
{
    public enum Kinds { Text, Arrow }
    public Kinds Kind { get; init; }
    public string Text { get; set; } = "";
    /// <summary>Text: its centre. Arrow: the tail. In video pixels.</summary>
    public Point A { get; set; }
    /// <summary>Arrow: the head.</summary>
    public Point B { get; set; }
    public double Start { get; set; }
    public double End { get; set; }

    /// <summary>The overlay drawn for a video <paramref name="videoHeight"/> pixels tall, and where its top-left goes.</summary>
    public (BitmapSource Picture, Point At) Render(int videoHeight)
    {
        var visual = new DrawingVisual();
        Rect bounds;
        using (var dc = visual.RenderOpen())
        {
            if (Kind == Kinds.Text)
            {
                double size = Math.Clamp(videoHeight * 0.045, 14, 96);
                var ft = new FormattedText(Text.Length == 0 ? " " : Text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), size, Brushes.White, 1.0);
                double padX = size * 0.7, padY = size * 0.35;
                double w = ft.Width + 2 * padX, h = ft.Height + 2 * padY;
                bounds = new Rect(A.X - w / 2, A.Y - h / 2, w, h);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(225, 20, 20, 24)), null, bounds, h * 0.3, h * 0.3);
                dc.DrawText(ft, new Point(bounds.X + padX, bounds.Y + padY));
            }
            else
            {
                double thick = Math.Max(3, videoHeight * 0.008);
                var red = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
                var geometry = Annotation.ArrowGeometry(A, B, thick);
                // a thin dark edge keeps it visible on red or busy backgrounds
                dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)), thick * 1.6) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geometry);
                dc.DrawGeometry(red, new Pen(red, thick) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geometry);
                bounds = geometry.GetRenderBounds(new Pen(Brushes.Black, thick * 1.6));
            }
        }
        bounds.Inflate(2, 2);
        int pw = Math.Max(1, (int)Math.Ceiling(bounds.Width)), ph = Math.Max(1, (int)Math.Ceiling(bounds.Height));
        var shifted = new DrawingVisual();
        using (var dc = shifted.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(-Math.Floor(bounds.X), -Math.Floor(bounds.Y)));
            dc.DrawDrawing(visual.Drawing);
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(pw + 1, ph + 1, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(shifted);
        rtb.Freeze();
        return (rtb, new Point(Math.Floor(bounds.X), Math.Floor(bounds.Y)));
    }

    public VideoOverlay Clone() => (VideoOverlay)MemberwiseClone();
}

/// <summary>
/// Saves an edited copy of a video with Windows' own editor: the parts kept (cuts taken out,
/// sound included) and text or arrows shown over it at their times. Also finds the idle
/// stretches, where nothing on screen moves, so they can be cut.
/// </summary>
public static class VideoEdit
{
    /// <summary>Where a moment of the original lands in the edited video (cuts close up the time).</summary>
    public static double ToOutput(double t, IReadOnlyList<(double Start, double End)> keep)
    {
        double before = 0;
        foreach (var (s, e) in keep)
        {
            if (t <= s) return before;
            if (t < e) return before + (t - s);
            before += e - s;
        }
        return before;
    }

    /// <summary>The parts of [start, end] left once the cuts are taken out.</summary>
    public static List<(double Start, double End)> Keep(double start, double end, IEnumerable<(double Start, double End)> cuts)
    {
        var keep = new List<(double, double)> { (start, end) };
        foreach (var (cs, ce) in cuts.OrderBy(c => c.Start))
        {
            var next = new List<(double, double)>();
            foreach (var (s, e) in keep)
            {
                if (ce <= s || cs >= e) { next.Add((s, e)); continue; }
                if (cs > s) next.Add((s, cs));
                if (ce < e) next.Add((ce, e));
            }
            keep = next;
        }
        return keep.Where(k => k.Item2 - k.Item1 >= 0.05).ToList();
    }

    public static async Task RenderAsync(string input, string output, IReadOnlyList<(double Start, double End)> keep,
        IReadOnlyList<VideoOverlay> overlays, int videoHeight, IProgress<double>? progress = null)
    {
        if (keep.Count == 0 || keep.Sum(k => k.End - k.Start) < 0.1) throw new InvalidOperationException("That leaves nothing to save.");
        var source = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(input));
        var composition = new MediaComposition();
        foreach (var (s, e) in keep)
        {
            var clip = await MediaClip.CreateFromFileAsync(source);
            var length = clip.OriginalDuration;
            clip.TrimTimeFromStart = TimeSpan.FromSeconds(Math.Clamp(s, 0, length.TotalSeconds));
            clip.TrimTimeFromEnd = TimeSpan.FromSeconds(Math.Clamp(length.TotalSeconds - e, 0, length.TotalSeconds));
            composition.Clips.Add(clip);
        }

        // overlays: each drawn once to a picture, shown for its time in the edited video
        var temps = new List<string>();
        try
        {
            if (overlays.Count > 0)
            {
                var layer = new MediaOverlayLayer();
                foreach (var o in overlays)
                {
                    double from = ToOutput(o.Start, keep), to = ToOutput(o.End, keep);
                    if (to - from < 0.05) continue;
                    var (picture, at) = o.Render(videoHeight);
                    string png = Path.Combine(Path.GetTempPath(), $"Overskin-overlay-{Guid.NewGuid():N}.png");
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(picture));
                    using (var f = File.Create(png)) encoder.Save(f);
                    temps.Add(png);
                    var file = await StorageFile.GetFileFromPathAsync(png);
                    var clip = await MediaClip.CreateFromImageFileAsync(file, TimeSpan.FromSeconds(to - from));
                    layer.Overlays.Add(new MediaOverlay(clip)
                    {
                        Position = new Windows.Foundation.Rect(at.X, at.Y, picture.PixelWidth, picture.PixelHeight),
                        Delay = TimeSpan.FromSeconds(from),
                        Opacity = 1,
                    });
                }
                if (layer.Overlays.Count > 0) composition.OverlayLayers.Add(layer);
            }

            var profile = await MediaEncodingProfile.CreateFromFileAsync(source);
            if (profile.Video != null) profile.Video.Bitrate = Math.Max(profile.Video.Bitrate, 4_000_000);
            string folder = Path.GetDirectoryName(Path.GetFullPath(output))!;
            Directory.CreateDirectory(folder);
            var storageFolder = await StorageFolder.GetFolderFromPathAsync(folder);
            var target = await storageFolder.CreateFileAsync(Path.GetFileName(output), CreationCollisionOption.ReplaceExisting);
            var render = composition.RenderToFileAsync(target, MediaTrimmingPreference.Precise, profile);
            if (progress != null) render.Progress = (_, p) => progress.Report(p / 100.0);
            var result = await render;
            if (result != TranscodeFailureReason.None)
            {
                try { await target.DeleteAsync(); } catch { }
                throw new InvalidOperationException("Windows couldn't save the video (" + result + ").");
            }
        }
        finally
        {
            foreach (var t in temps) try { File.Delete(t); } catch { }
        }
    }

    /// <summary>
    /// Stretches of at least <paramref name="minimum"/> seconds where the picture barely changes
    /// (no pointer moving, nothing typed or scrolling), less a short margin each side so cuts
    /// don't feel abrupt.
    /// </summary>
    public static async Task<List<(double Start, double End)>> FindIdleAsync(string path, double minimum = 2.0, IProgress<double>? progress = null)
    {
        var source = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        var clip = await MediaClip.CreateFromFileAsync(source);
        var composition = new MediaComposition();
        composition.Clips.Add(clip);
        double length = clip.OriginalDuration.TotalSeconds;
        const double Step = 0.25;
        var times = new List<TimeSpan>();
        for (double t = 0; t < length; t += Step) times.Add(TimeSpan.FromSeconds(t));
        var idle = new List<(double, double)>();
        byte[]? previous = null;
        double stillFrom = -1;
        const int Batch = 40;
        for (int b = 0; b < times.Count; b += Batch)
        {
            var batch = times.Skip(b).Take(Batch).ToList();
            for (int k = 0; k < batch.Count; k++)
            {
                // one at a time: the batch call can hand back the same frame for every time
                byte[] px;
                using (var thumb = await composition.GetThumbnailAsync(batch[k], 160, 90, VideoFramePrecision.NearestFrame)) px = await Pixels(thumb);
                double t = batch[k].TotalSeconds;
                bool still = previous != null && px.Length == previous.Length && Changed(previous, px) < 0.002;
                if (still && stillFrom < 0) stillFrom = t - Step;
                if (!still && stillFrom >= 0)
                {
                    Close(stillFrom, t - Step);
                    stillFrom = -1;
                }
                previous = px;
            }
            progress?.Report(Math.Min(1, (b + Batch) / (double)Math.Max(1, times.Count)));
        }
        if (stillFrom >= 0) Close(stillFrom, length);
        return idle;

        void Close(double from, double to)
        {
            const double Margin = 0.4;
            if (to - from >= minimum) idle.Add((from + Margin, to - Margin));
        }
    }

    /// <summary>The share of pixels that changed noticeably between two small frames.</summary>
    private static double Changed(byte[] a, byte[] b)
    {
        int changed = 0, n = a.Length / 4;
        for (int i = 0; i < a.Length; i += 4)
            if (Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]) > 24) changed++;
        return changed / (double)n;
    }

    private static async Task<byte[]> Pixels(Windows.Storage.Streams.IRandomAccessStream stream)
    {
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
        var data = await decoder.GetPixelDataAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Ignore,
            new Windows.Graphics.Imaging.BitmapTransform(), Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation, Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
        return data.DetachPixelData();
    }
}
