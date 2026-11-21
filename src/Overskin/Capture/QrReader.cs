using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZXing;
using ZXing.Common;

namespace Overskin;

/// <summary>
/// Reads QR codes (and ordinary barcodes) in a picture, offline, with ZXing. "Text from screen"
/// tries this first, so dragging over a QR code copies what it says.
/// </summary>
internal static class QrReader
{
    /// <summary>What every code in the picture says, one per line, or null when there's none.</summary>
    public static string? Read(BitmapSource image)
    {
        var bgra = image.Format == PixelFormats.Bgra32 || image.Format == PixelFormats.Pbgra32 ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        if (w < 8 || h < 8) return null;
        var pixels = new byte[w * h * 4];
        bgra.CopyPixels(pixels, w * 4, 0);
        var found = Decode(pixels, w, h);
        // a small code on screen reads better a little bigger
        if (found == null && Math.Max(w, h) < 400)
        {
            var big = new TransformedBitmap(bgra, new ScaleTransform(3, 3));
            var bigPixels = new byte[big.PixelWidth * big.PixelHeight * 4];
            new FormatConvertedBitmap(big, PixelFormats.Bgra32, null, 0).CopyPixels(bigPixels, big.PixelWidth * 4, 0);
            found = Decode(bigPixels, big.PixelWidth, big.PixelHeight);
        }
        return found;
    }

    private static string? Decode(byte[] pixels, int w, int h)
    {
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions { TryHarder = true, TryInverted = true },
        };
        var source = new RGBLuminanceSource(pixels, w, h, RGBLuminanceSource.BitmapFormat.BGRA32);
        var results = reader.DecodeMultiple(source);
        if (results == null || results.Length == 0) return null;
        var texts = results.Select(r => r.Text).Where(t => !string.IsNullOrEmpty(t)).Distinct().ToList();
        return texts.Count == 0 ? null : string.Join(Environment.NewLine, texts);
    }

    /// <summary>The link a code holds, when it's just a web address.</summary>
    public static Uri? LinkIn(string text) =>
        Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) ? uri : null;
}
