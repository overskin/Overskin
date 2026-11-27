using Avalonia;
using Avalonia.Media.Imaging;
using Overskin.Linux.Core;
using ZXing;
using ZXing.Common;

namespace Overskin.Linux.Platform;

/// <summary>
/// Reads QR codes (and ordinary barcodes) in a picture, offline, with ZXing, as on Windows.
/// "Text from screen" tries this first, so dragging over a QR code copies what it says.
/// </summary>
public static class QrReader
{
    /// <summary>What every code in the picture says, one per line, or null when there's none.</summary>
    public static string? Read(Bitmap image)
    {
        int w = image.PixelSize.Width, h = image.PixelSize.Height;
        if (w < 8 || h < 8) return null;
        var found = Decode(Imaging.Pixels(image, out _), w, h);
        // a small code on screen reads better a little bigger
        if (found == null && Math.Max(w, h) < 400)
        {
            var big = Effects.Resize(image, w * 3, h * 3);
            found = Decode(Imaging.Pixels(big, out _), w * 3, h * 3);
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
        var results = reader.DecodeMultiple(new RGBLuminanceSource(pixels, w, h, RGBLuminanceSource.BitmapFormat.BGRA32));
        if (results == null || results.Length == 0) return null;
        var texts = results.Select(r => r.Text).Where(t => !string.IsNullOrEmpty(t)).Distinct().ToList();
        return texts.Count == 0 ? null : string.Join("\n", texts);
    }

    /// <summary>The link a code holds, when it's just a web address.</summary>
    public static Uri? LinkIn(string text) =>
        Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) ? uri : null;
}
