using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Overskin;

/// <summary>Writing screenshots to disk.</summary>
public static class ImageFiles
{
    /// <summary>
    /// PNG, or JPEG when the path ends in .jpg/.jpeg (transparent areas become white), at
    /// <paramref name="jpegQuality"/>. With <paramref name="maxBytes"/>, the file is made to fit:
    /// a JPEG first loses quality (down to 40), then size; a PNG stays lossless and gets smaller.
    /// </summary>
    public static bool TrySave(BitmapSource image, string path, out string? error, int jpegQuality = 92, long? maxBytes = null)
    {
        error = null;
        if (maxBytes is { } limit)
        {
            try
            {
                string? folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                var bytes = Fit(image, Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg", jpegQuality, limit);
                string part = path + ".tmp";
                File.WriteAllBytes(part, bytes);
                File.Move(part, path, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            BitmapEncoder encoder;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".jpg" or ".jpeg")
            {
                encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(jpegQuality, 10, 100) };
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

    /// <summary>The picture encoded to fit in <paramref name="limit"/> bytes.</summary>
    internal static byte[] Fit(BitmapSource image, bool jpeg, int quality, long limit)
    {
        byte[] Encode(BitmapSource source, int q)
        {
            BitmapEncoder encoder = jpeg ? new JpegBitmapEncoder { QualityLevel = Math.Clamp(q, 10, 100) } : new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(jpeg ? OnWhite(source) : source));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }
        var best = Encode(image, quality);
        if (best.Length <= limit) return best;
        if (jpeg)
        {
            // the highest quality that fits, down to 40 (below that it gets blotchy)
            int lo = 40, hi = quality - 1;
            byte[]? fitting = null;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                var tried = Encode(image, mid);
                if (tried.Length <= limit) { fitting = tried; lo = mid + 1; }
                else hi = mid - 1;
            }
            if (fitting != null) return fitting;
            quality = 40;
        }
        // still too big: make it smaller, keeping its shape
        var source = image;
        for (int round = 0; round < 8 && best.Length > limit; round++)
        {
            double k = Math.Sqrt(limit / (double)best.Length) * 0.95;
            int w = Math.Max(1, (int)(source.PixelWidth * k)), h = Math.Max(1, (int)(source.PixelHeight * k));
            var scaled = new TransformedBitmap(image, new ScaleTransform(w / (double)image.PixelWidth, h / (double)image.PixelHeight));
            source = ImageLoader.Normalize(scaled);
            best = Encode(source, quality);
        }
        return best;
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
    public static string NewPath(string folder, string extension) => FileNames.NewPath(folder, extension);
}
