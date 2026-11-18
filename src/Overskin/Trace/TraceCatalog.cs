using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Overskin;

/// <summary>One picture kept in the Trace catalog.</summary>
public sealed class CatalogItem : INotifyPropertyChanged
{
    private ImageSource? _thumb;
    private bool _isCurrent;

    public required string Path { get; init; }
    public DateTime Added { get; init; }

    public ImageSource? Thumb
    {
        get => _thumb;
        set { _thumb = value; Raise(); }
    }

    /// <summary>The picture loaded in the Trace tab right now.</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set { if (_isCurrent != value) { _isCurrent = value; Raise(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Every picture you trace is kept as a PNG in a folder of its own, named by its content so the
/// same picture is never stored twice.
/// </summary>
public static class TraceCatalog
{
    public static string Folder => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Overskin",
        App.Quiet ? "Trace catalog (test)" : "Trace catalog");

    /// <summary>Stores the picture (if it isn't already) and returns its file.</summary>
    public static string? Add(BitmapSource image)
    {
        try
        {
            var normal = image.Format == PixelFormats.Bgra32 ? image : ImageLoader.Normalize(image);
            int stride = normal.PixelWidth * 4;
            var pixels = new byte[stride * normal.PixelHeight];
            normal.CopyPixels(pixels, stride, 0);
            string name = Convert.ToHexString(SHA1.HashData(pixels))[..16].ToLowerInvariant() + ".png";

            Directory.CreateDirectory(Folder);
            string path = System.IO.Path.Combine(Folder, name);
            if (!File.Exists(path) && !ImageFiles.TrySave(normal, path, out _)) return null;
            return path;
        }
        catch
        {
            return null; // the catalog is a convenience; tracing still works without it
        }
    }

    /// <summary>Everything in the catalog, newest first.</summary>
    public static List<CatalogItem> Load()
    {
        try
        {
            if (!Directory.Exists(Folder)) return new();
            return new DirectoryInfo(Folder).GetFiles("*.png")
                .OrderByDescending(f => f.CreationTimeUtc)
                .Select(f => new CatalogItem { Path = f.FullName, Added = f.CreationTime })
                .ToList();
        }
        catch
        {
            return new();
        }
    }

    public static void Remove(string path)
    {
        try { File.Delete(path); } catch { }
    }

    public static async Task LoadThumbAsync(CatalogItem item)
    {
        try
        {
            item.Thumb = await Task.Run(() =>
            {
                using var stream = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelHeight = 120;
                bmp.StreamSource = stream;
                bmp.EndInit();
                bmp.Freeze();
                return (ImageSource)bmp;
            });
        }
        catch
        {
            // unreadable file: the tile just stays empty
        }
    }
}
