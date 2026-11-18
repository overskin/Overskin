using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Overskin;

/// <summary>Writing screenshots to disk.</summary>
public static class ImageFiles
{
    /// <summary>PNG, or JPEG when the path ends in .jpg/.jpeg (transparent areas become white).</summary>
    public static bool TrySave(BitmapSource image, string path, out string? error)
    {
        error = null;
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            BitmapEncoder encoder;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".jpg" or ".jpeg")
            {
                encoder = new JpegBitmapEncoder { QualityLevel = 92 };
                encoder.Frames.Add(BitmapFrame.Create(OnWhite(image)));
            }
            else if (ext == ".bmp")
            {
                encoder = new BmpBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(OnWhite(image)));
            }
            else if (ext is ".tif" or ".tiff")
            {
                encoder = new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw };
                encoder.Frames.Add(BitmapFrame.Create(image));
            }
            else
            {
                encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
            }

            // write next to the target first, so a failed save never leaves half a file
            string temp = path + ".tmp";
            using (var file = File.Create(temp)) encoder.Save(file);
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static BitmapSource OnWhite(BitmapSource image)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new System.Windows.Rect(0, 0, image.PixelWidth, image.PixelHeight));
            dc.DrawImage(image, new System.Windows.Rect(0, 0, image.PixelWidth, image.PixelHeight));
        }
        var rtb = new RenderTargetBitmap(image.PixelWidth, image.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        return new FormatConvertedBitmap(rtb, PixelFormats.Bgr24, null, 0);
    }

    /// <summary>A free file name like "Overskin 2026-11-16 14.03.22.png" in the folder.</summary>
    public static string NewPath(string folder, string extension)
    {
        string stamp = DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss");
        string path = Path.Combine(folder, $"Overskin {stamp}{extension}");
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(folder, $"Overskin {stamp} ({i}){extension}");
        return path;
    }
}
