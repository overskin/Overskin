using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Overskin;

/// <summary>
/// A GIF from part of the video: the trimmed part (or a few seconds from where the video is),
/// opened in the GIF editor with what's said in it as the caption.
/// </summary>
public partial class VideoEditorWindow
{
    /// <summary>Opens a GIF in the GIF editor: the clip, its name, and a caption to start with.</summary>
    public static Action<GifClip, string, string>? OpenGif { get; set; }

    private const double GifLongest = 15, GifFps = 15, GifWidest = 640;

    private async void MakeGif_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || OpenGif == null) return;
        var (start, end) = Range;
        // the trimmed part when it's GIF-length; otherwise five seconds from where the video is
        if (end - start > GifLongest || end - start < 0.3)
        {
            start = Math.Clamp(Player.Position.TotalSeconds, 0, Math.Max(0, _length - 0.5));
            end = Math.Min(_length, start + 5);
        }
        _busy = true;
        Play(false);
        StatusText.Text = "Making the GIF…";
        try
        {
            var clip = await Task.Run(() => ReadClip(_path, start, end));
            if (clip.Count == 0)
            {
                Toast.Show("Couldn't read that part of the video.", true);
                return;
            }
            // what's said in that part becomes the caption
            string said = string.Join(" ", Captions.Where(c => c.End > start + 0.1 && c.Start < end - 0.1).OrderBy(c => c.Start).Select(c => c.Text.Trim()));
            OpenGif(clip, $"{System.IO.Path.GetFileNameWithoutExtension(_path)}  ·  {Clock(start)}–{Clock(end)}", said);
            StatusText.Text = "";
        }
        catch (Exception ex)
        {
            StatusText.Text = "";
            Toast.Show("Couldn't make the GIF: " + ex.Message, true);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>The frames from <paramref name="start"/> to <paramref name="end"/> seconds, at GIF speed and size.</summary>
    internal static GifClip ReadClip(string path, double start, double end)
    {
        using var reader = new MediaReader(path);
        double k = Math.Min(1, GifWidest / reader.Width);
        int w = Math.Max(2, (int)Math.Round(reader.Width * k)), h = Math.Max(2, (int)Math.Round(reader.Height * k));
        var clip = new GifClip { Width = w, Height = h };
        reader.Seek(TimeSpan.FromSeconds(start));
        double step = 1 / GifFps, due = start;
        double? last = null;
        while (reader.Next() is { } frame)
        {
            double t = frame.Time.TotalSeconds;
            if (t < due - step / 2) continue;
            if (t >= end) break;
            var src = BitmapSource.Create(reader.Width, reader.Height, 96, 96, PixelFormats.Bgra32, null, frame.Pixels, reader.Width * 4);
            BitmapSource scaled = k < 1 ? new TransformedBitmap(src, new ScaleTransform(w / (double)reader.Width, h / (double)reader.Height)) : src;
            var px = new byte[w * h * 4];
            new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0).CopyPixels(px, w * 4, 0);
            if (last is { } before) clip.DelaysMs[^1] = Math.Max(20, (int)Math.Round((t - before) * 1000));
            clip.Frames.Add(px);
            clip.DelaysMs.Add((int)Math.Round(step * 1000));
            last = t;
            due = t + step;
        }
        return clip;
    }
}
