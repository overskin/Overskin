using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Overskin;

/// <summary>Small previews of saved captures: pictures and GIFs decode their first frame; videos use Windows' own thumbnail.</summary>
public static class Thumbs
{
    public static bool IsVideo(string path) => Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase);
    public static bool IsGif(string path) => Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase);

    public static async Task<ImageSource?> LoadAsync(string path, int width)
    {
        try
        {
            if (IsVideo(path))
            {
                var file = await global::Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                using var thumb = await file.GetThumbnailAsync(global::Windows.Storage.FileProperties.ThumbnailMode.VideosView, (uint)width);
                var copy = new MemoryStream();
                await thumb.AsStreamForRead().CopyToAsync(copy);
                copy.Position = 0;
                return Decode(copy, width);
            }
            return await Task.Run(() =>
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return Decode(stream, width);
            });
        }
        catch
        {
            return null;
        }
    }

    private static BitmapImage Decode(Stream stream, int width)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.DecodePixelWidth = width;
        bmp.StreamSource = stream;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}
