using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Overskin;

/// <summary>Colour looks and mirroring for GIF frames, the same for the preview and the saved file.</summary>
public static class GifLook
{
    public static BitmapSource Apply(BitmapSource frame, string look, bool mirror)
    {
        if (look is "" or "Normal" && !mirror) return frame;
        BitmapSource src = frame;
        if (mirror)
        {
            var flipped = new TransformedBitmap(src, new ScaleTransform(-1, 1));
            flipped.Freeze();
            src = flipped;
        }
        if (look is "" or "Normal") return src;

        var bgra = src.Format == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        bgra.CopyPixels(px, stride, 0);
        for (int i = 0; i < px.Length; i += 4)
        {
            double b = px[i], g = px[i + 1], r = px[i + 2];
            double grey = 0.299 * r + 0.587 * g + 0.114 * b;
            switch (look)
            {
                case "Grey":
                    r = g = b = grey;
                    break;
                case "Sepia":
                    (r, g, b) = (grey * 1.07 + 20, grey * 0.87 + 10, grey * 0.62);
                    break;
                case "Vivid":
                    // more colour and a touch more contrast
                    (r, g, b) = (grey + (r - grey) * 1.55, grey + (g - grey) * 1.55, grey + (b - grey) * 1.55);
                    (r, g, b) = ((r - 128) * 1.08 + 128, (g - 128) * 1.08 + 128, (b - 128) * 1.08 + 128);
                    break;
            }
            px[i] = (byte)Math.Clamp(b, 0, 255);
            px[i + 1] = (byte)Math.Clamp(g, 0, 255);
            px[i + 2] = (byte)Math.Clamp(r, 0, 255);
        }
        var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
        result.Freeze();
        return result;
    }
}
