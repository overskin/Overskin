using Avalonia;
using Avalonia.Media.Imaging;
using Overskin.Linux.Core;

namespace Overskin.Linux.Platform;

/// <summary>
/// Reading text in pictures with Tesseract (the tesseract command), which every Linux
/// distribution packages. Used for auto-redact, copying text and searching the library.
/// </summary>
public static class Ocr
{
    /// <summary>Macs read text with Apple's Vision, built in; Linux needs Tesseract.</summary>
    public static bool IsAvailable => Mac.IsMac || ScreenGrabber.HasCommand("tesseract");

    public static string InstallHint => Mac.IsMac
        ? "Reading text uses macOS's Vision text reader, which needs macOS 10.15 or later."
        : "Reading text needs Tesseract: Arch: sudo pacman -S tesseract tesseract-data-eng · " +
          "Debian/Ubuntu: sudo apt install tesseract-ocr · Fedora: sudo dnf install tesseract";

    /// <summary>The words in the picture, grouped into lines, with where each sits (picture pixels). Null without Tesseract.</summary>
    public static async Task<List<List<OcrWord>>?> ReadLinesAsync(Bitmap image)
    {
        if (Mac.IsMac)
        {
            // Vision first; Tesseract (from Homebrew) if Vision can't run
            if (await MacText.ReadLinesAsync(image) is { } read) return Fixed(read);
            if (!ScreenGrabber.HasCommand("tesseract")) return null;
        }
        if (!IsAvailable) return null;
        var file = Path.Combine(Path.GetTempPath(), $"overskin-ocr-{Environment.ProcessId}-{Guid.NewGuid():N}.png");
        try
        {
            // small text reads far better scaled up
            double scale = image.PixelSize.Height < 1400 ? 2 : 1;
            var input = scale == 1 ? image : Effects.Resize(image, (int)(image.PixelSize.Width * scale), (int)(image.PixelSize.Height * scale));
            input.Save(file);
            var tsv = await ScreenGrabber.OutputOf("tesseract", $"\"{file}\" stdout --psm 11 tsv");
            return tsv == null ? null : Fixed(Parse(tsv, scale));
        }
        finally
        {
            try { File.Delete(file); } catch { }
        }
    }

    /// <summary>Letters and numbers the reader mixed up, put right from what's round them (as on Windows).</summary>
    private static List<List<OcrWord>> Fixed(List<List<OcrWord>> lines) =>
        lines.Select(l => l.Select(w => w with { Text = Overskin.OcrFix.Word(w.Text) }).ToList()).ToList();

    public static async Task<string?> ReadTextAsync(Bitmap image)
    {
        var lines = await ReadLinesAsync(image);
        return lines == null ? null : string.Join("\n", lines.Select(l => string.Join(" ", l.Select(w => w.Text))));
    }

    /// <summary>Tesseract's TSV: level, page, block, paragraph, line, word, left, top, width, height, confidence, text.</summary>
    public static List<List<OcrWord>> Parse(string tsv, double scale = 1)
    {
        var lines = new Dictionary<(int, int, int, int), List<OcrWord>>();
        var order = new List<(int, int, int, int)>();
        foreach (var row in tsv.Split('\n').Skip(1))
        {
            var f = row.TrimEnd('\r').Split('\t');
            if (f.Length < 12 || f[0] != "5" || string.IsNullOrWhiteSpace(f[11])) continue;
            if (!double.TryParse(f[10], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var conf) || conf < 30) continue;
            var key = (int.Parse(f[1]), int.Parse(f[2]), int.Parse(f[3]), int.Parse(f[4]));
            if (!lines.TryGetValue(key, out var list)) { lines[key] = list = new(); order.Add(key); }
            list.Add(new OcrWord(f[11].Trim(), new Rect(int.Parse(f[6]) / scale, int.Parse(f[7]) / scale, int.Parse(f[8]) / scale, int.Parse(f[9]) / scale)));
        }
        return order.Select(k => lines[k]).ToList();
    }
}
