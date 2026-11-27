using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Overskin.Linux.Core;

namespace Overskin.Linux.Platform;

/// <summary>Turning pictures and videos into other formats with ffmpeg, and combining pictures.</summary>
public static class Converter
{
    public static readonly string[] Pictures = { ".png", ".jpg", ".jpeg", ".bmp", ".webp", ".tif", ".tiff" };
    public static readonly string[] Moving = { ".gif", ".mp4", ".webm", ".mkv", ".mov" };

    public static bool IsPicture(string path) => Pictures.Contains(Path.GetExtension(path).ToLowerInvariant());
    public static bool IsMoving(string path) => Moving.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static string FfmpegHint => OperatingSystem.IsMacOS()
        ? "Converting needs ffmpeg. Install it with Homebrew (brew install ffmpeg), then try again."
        : "Converting needs ffmpeg (Arch: sudo pacman -S ffmpeg · Debian/Ubuntu: sudo apt install ffmpeg · Fedora: sudo dnf install ffmpeg).";

    /// <summary>What a file can become.</summary>
    public static string[] TargetsFor(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var all = IsPicture(path) ? new[] { ".png", ".jpg", ".webp", ".bmp", ".gif", ".mp4" }
                : ext == ".gif" ? new[] { ".mp4", ".webm", ".png" }
                : new[] { ".gif", ".mp4", ".webm", ".png" };
        return all.Where(t => t != ext && !(t == ".jpg" && ext == ".jpeg")).ToArray();
    }

    /// <summary>A free name next to the file, with the new extension.</summary>
    public static string Beside(string path, string extension)
    {
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var target = Path.Combine(dir, name + extension);
        for (int i = 2; File.Exists(target); i++) target = Path.Combine(dir, $"{name} ({i}){extension}");
        return target;
    }

    public static async Task<string> ConvertAsync(string path, string extension)
    {
        var target = Beside(path, extension);
        // PNG from a picture needs nothing extra
        if (extension == ".png" && IsPicture(path))
        {
            using var b = new Bitmap(path);
            b.Save(target);
            return target;
        }
        if (!ScreenGrabber.HasCommand("ffmpeg")) throw new InvalidOperationException(FfmpegHint);

        string args = (IsPicture(path), extension) switch
        {
            (true, ".mp4") => $"-loop 1 -t 3 -i \"{path}\" -vf \"scale=trunc(iw/2)*2:trunc(ih/2)*2,format=yuv420p\" -c:v libx264 -r 30 \"{target}\"",
            (true, ".gif") => $"-i \"{path}\" -vf \"split[a][b];[a]palettegen[p];[b][p]paletteuse\" \"{target}\"",
            (true, _) => $"-i \"{path}\" -q:v 2 \"{target}\"",
            (false, ".gif") => $"-i \"{path}\" -vf \"fps=15,split[a][b];[a]palettegen[p];[b][p]paletteuse\" \"{target}\"",
            (false, ".png") => $"-i \"{path}\" -frames:v 1 \"{target}\"",
            (false, ".mp4") => $"-i \"{path}\" -movflags +faststart -vf \"scale=trunc(iw/2)*2:trunc(ih/2)*2\" -c:v libx264 -pix_fmt yuv420p \"{target}\"",
            (false, ".webm") => $"-i \"{path}\" -c:v libvpx-vp9 -b:v 0 -crf 34 \"{target}\"",
            _ => $"-i \"{path}\" \"{target}\"",
        };
        var (ok, error) = await ScreenGrabber.RunAsync("ffmpeg", "-hide_banner -loglevel error -y " + args, 600);
        if (!ok || !File.Exists(target)) throw new InvalidOperationException("ffmpeg couldn't convert it: " + error);
        return target;
    }

    public static async Task VideoToGifAsync(string video, string gif, int fps, int maxWidth)
    {
        if (!ScreenGrabber.HasCommand("ffmpeg")) throw new InvalidOperationException(FfmpegHint);
        var scale = maxWidth > 0 ? $"scale='min({maxWidth},iw)':-1:flags=lanczos," : "";
        var (ok, error) = await ScreenGrabber.RunAsync("ffmpeg",
            $"-hide_banner -loglevel error -y -i \"{video}\" -vf \"fps={fps},{scale}split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4\" \"{gif}\"", 900);
        if (!ok || !File.Exists(gif)) throw new InvalidOperationException("Couldn't make the GIF: " + error);
    }

    /// <summary>The first frame of a video, for thumbnails. Null without ffmpeg.</summary>
    public static async Task<Bitmap?> FirstFrameAsync(string video, int width)
    {
        if (!ScreenGrabber.HasCommand("ffmpeg")) return null;
        var tmp = Path.Combine(Path.GetTempPath(), $"overskin-thumb-{Guid.NewGuid():N}.png");
        try
        {
            var (ok, _) = await ScreenGrabber.RunAsync("ffmpeg", $"-hide_banner -loglevel error -y -i \"{video}\" -frames:v 1 -vf scale={width}:-1 \"{tmp}\"", 30);
            if (!ok || !File.Exists(tmp)) return null;
            using var s = File.OpenRead(tmp);
            return new Bitmap(s);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    public enum Layout { SideBySide, Stacked, Grid }

    /// <summary>Pictures put together, with a gap, on the backdrop colour.</summary>
    public static Bitmap Combine(IReadOnlyList<Bitmap> images, Layout layout, int gap, Color backdrop)
    {
        int n = images.Count;
        int cols = layout switch { Layout.SideBySide => n, Layout.Stacked => 1, _ => (int)Math.Ceiling(Math.Sqrt(n)) };
        int rows = (int)Math.Ceiling(n / (double)cols);
        var colWidths = new int[cols];
        var rowHeights = new int[rows];
        for (int i = 0; i < n; i++)
        {
            colWidths[i % cols] = Math.Max(colWidths[i % cols], images[i].PixelSize.Width);
            rowHeights[i / cols] = Math.Max(rowHeights[i / cols], images[i].PixelSize.Height);
        }
        int w = colWidths.Sum() + gap * (cols + 1), h = rowHeights.Sum() + gap * (rows + 1);
        return Imaging.Render(new PixelSize(w, h), ctx =>
        {
            if (backdrop.A > 0) ctx.FillRectangle(new SolidColorBrush(backdrop), new Rect(0, 0, w, h));
            for (int i = 0; i < n; i++)
            {
                int c = i % cols, r = i / cols;
                double x = gap + colWidths.Take(c).Sum() + gap * c + (colWidths[c] - images[i].PixelSize.Width) / 2.0;
                double y = gap + rowHeights.Take(r).Sum() + gap * r + (rowHeights[r] - images[i].PixelSize.Height) / 2.0;
                ctx.DrawImage(images[i], new Rect(images[i].Size), new Rect(x, y, images[i].PixelSize.Width, images[i].PixelSize.Height));
            }
        });
    }
}
