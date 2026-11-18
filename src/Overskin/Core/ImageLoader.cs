using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Overskin;

/// <summary>Loads images from files, drops and the clipboard into frozen BGRA bitmaps.</summary>
public static class ImageLoader
{
    public const string FileFilter =
        "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff|All files|*.*";

    private static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff" };

    public static bool LooksLikeImage(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Decodes the first frame of an image file. WebP goes through the Windows codec, which is
    /// present when "WebP Image Extensions" is installed (it ships with current Windows 10/11).
    /// </summary>
    public static BitmapSource LoadFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return Decode(stream);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or System.Runtime.InteropServices.COMException)
        {
            bool webp = Path.GetExtension(path).Equals(".webp", StringComparison.OrdinalIgnoreCase);
            throw new InvalidOperationException(webp
                ? "This PC has no WebP codec. Install “WebP Image Extensions” from the Microsoft Store."
                : $"Couldn't read {Path.GetFileName(path)}. The format isn't supported.");
        }
    }

    private static BitmapSource Decode(Stream stream)
    {
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return Normalize(decoder.Frames[0]);
    }

    /// <summary>Converts to 32-bit BGRA at 96 DPI so one image pixel is one unit everywhere else.</summary>
    public static BitmapSource Normalize(BitmapSource source)
    {
        BitmapSource bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
        byte[] pixels = new byte[stride * h];
        bgra.CopyPixels(pixels, stride, 0);

        var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    /// <summary>Image from the clipboard: a copied bitmap, or a copied image file.</summary>
    public static BitmapSource? FromClipboard(out string? name)
    {
        name = null;
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                foreach (string? file in Clipboard.GetFileDropList())
                {
                    if (file != null && LooksLikeImage(file))
                    {
                        name = Path.GetFileName(file);
                        return LoadFile(file);
                    }
                }
            }

            var data = Clipboard.GetDataObject();
            if (data == null) return null;

            // Browsers and editors put a real PNG on the clipboard; it keeps transparency.
            if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream png)
            {
                name = "Pasted image";
                return Decode(png);
            }

            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
            {
                name = "Pasted image";
                // The plain bitmap format often carries an all-zero alpha channel; drop it.
                return Normalize(new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0));
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch
        {
            // clipboard is busy or holds something we can't decode
        }
        return null;
    }

    /// <summary>First image file in a drag-and-drop payload, if any.</summary>
    public static string? FileFromDrop(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop)) return null;
        if (data.GetData(DataFormats.FileDrop) is not string[] files) return null;
        return files.FirstOrDefault(LooksLikeImage) ?? files.FirstOrDefault();
    }

    /// <summary>Greyscale copy that keeps the alpha channel.</summary>
    public static BitmapSource ToGrayscale(BitmapSource source)
    {
        BitmapSource bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
        byte[] px = new byte[stride * h];
        bgra.CopyPixels(px, stride, 0);
        for (int i = 0; i < px.Length; i += 4)
        {
            byte g = (byte)((px[i] * 29 + px[i + 1] * 150 + px[i + 2] * 77) >> 8);
            px[i] = px[i + 1] = px[i + 2] = g;
        }
        var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
        result.Freeze();
        return result;
    }
}
