using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Overskin;

/// <summary>One recognised word and where it is, in pixels of the original picture.</summary>
public readonly record struct OcrWord(string Text, Rect Bounds);

/// <summary>Reads text out of a picture with the OCR engine built into Windows. Works offline.</summary>
public static class TextRecognizer
{
    /// <summary>The engine for the user's display languages, or null when no OCR language is installed.</summary>
    private static OcrEngine? Engine() => OcrEngine.TryCreateFromUserProfileLanguages();

    public static bool IsAvailable => Engine() != null;

    /// <summary>Recognised text with its line breaks, or null when no OCR language is installed.</summary>
    public static async Task<string?> RecognizeAsync(BitmapSource image)
    {
        var lines = await RecognizeLinesAsync(image);
        if (lines == null) return null;
        var text = new StringBuilder();
        foreach (var line in lines)
            text.AppendLine(string.Join(" ", line.Select(w => w.Text)));
        return text.ToString().TrimEnd();
    }

    /// <summary>Every line as its words with their boxes, or null when no OCR language is installed.</summary>
    public static async Task<List<List<OcrWord>>?> RecognizeLinesAsync(BitmapSource image)
    {
        var engine = Engine();
        if (engine == null) return null;

        // small text reads much better enlarged; the engine's limit is 10 000 px per side
        double scale = Math.Clamp(1600.0 / Math.Max(image.PixelWidth, image.PixelHeight), 1, 3);
        if (Math.Max(image.PixelWidth, image.PixelHeight) * scale > OcrEngine.MaxImageDimension)
            scale = (double)OcrEngine.MaxImageDimension / Math.Max(image.PixelWidth, image.PixelHeight);
        BitmapSource source = Math.Abs(scale - 1) > 0.01
            ? new TransformedBitmap(image, new System.Windows.Media.ScaleTransform(scale, scale))
            : image;

        using var memory = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
        encoder.Save(memory);
        memory.Position = 0;

        using var stream = memory.AsRandomAccessStream();
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(bitmap);

        var lines = new List<List<OcrWord>>();
        foreach (var line in result.Lines)
        {
            var words = new List<OcrWord>();
            foreach (var word in line.Words)
            {
                var r = word.BoundingRect;
                words.Add(new OcrWord(word.Text, new Rect(r.X / scale, r.Y / scale, r.Width / scale, r.Height / scale)));
            }
            if (words.Count > 0) lines.Add(words);
        }
        return lines;
    }
}
