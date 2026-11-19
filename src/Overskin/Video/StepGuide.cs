using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Overskin;

/// <summary>Saves recorded steps as a guide: one HTML file, Markdown with a folder of pictures, or a PDF.</summary>
public static class StepGuide
{
    private static byte[] Encode(BitmapSource image, bool jpeg)
    {
        BitmapEncoder encoder = jpeg ? new JpegBitmapEncoder { QualityLevel = 90 } : new PngBitmapEncoder();
        // JPEG has no transparency: flatten onto white first
        BitmapSource source = jpeg ? new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0) : image;
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static string Subtitle(int count) =>
        $"{count} step{(count == 1 ? "" : "s")} · {DateTime.Now.ToString("d MMMM yyyy", CultureInfo.CurrentCulture)}";

    // ---- HTML ------------------------------------------------------------------------------

    /// <summary>One self-contained page: the pictures are inside it, so it can be emailed or put online as is.</summary>
    public static void SaveHtml(IReadOnlyList<RecordedStep> steps, string title, Color accent, string path)
    {
        string hex = $"#{accent.R:X2}{accent.G:X2}{accent.B:X2}";
        var sb = new StringBuilder();
        sb.Append("<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append($"<title>{WebUtility.HtmlEncode(title)}</title>\n<style>\n");
        sb.Append(":root { --accent: " + hex + "; }\n");
        sb.Append("body { margin: 0; background: #f4f5f7; color: #1f2328; font: 16px/1.5 \"Segoe UI\", system-ui, sans-serif; }\n");
        sb.Append("main { max-width: 1040px; margin: 0 auto; padding: 40px 20px 60px; }\n");
        sb.Append("h1 { font-size: 30px; margin: 0 0 4px; } .sub { color: #656d76; margin: 0 0 32px; }\n");
        sb.Append("ol { list-style: none; padding: 0; margin: 0; counter-reset: step; }\n");
        sb.Append("li { counter-increment: step; background: #fff; border: 1px solid #d0d7de; border-radius: 10px; padding: 18px; margin: 0 0 22px; }\n");
        sb.Append("li p { display: flex; gap: 12px; align-items: center; margin: 0 0 14px; font-weight: 600; font-size: 18px; }\n");
        sb.Append("li p::before { content: counter(step); flex: none; width: 32px; height: 32px; border-radius: 50%; background: var(--accent); color: #fff; display: grid; place-items: center; font-size: 15px; }\n");
        sb.Append("img { display: block; max-width: 100%; height: auto; border: 1px solid #d0d7de; border-radius: 6px; }\n");
        sb.Append("</style>\n</head>\n<body>\n<main>\n");
        sb.Append($"<h1>{WebUtility.HtmlEncode(title)}</h1>\n<p class=\"sub\">{WebUtility.HtmlEncode(Subtitle(steps.Count))}</p>\n<ol>\n");
        for (int i = 0; i < steps.Count; i++)
        {
            string caption = StepTutorial.Caption(steps[i]);
            string data = Convert.ToBase64String(Encode(StepTutorial.Marked(steps[i], accent), jpeg: false));
            sb.Append($"<li><p>{WebUtility.HtmlEncode(caption)}</p><img src=\"data:image/png;base64,{data}\" alt=\"Step {i + 1}: {WebUtility.HtmlEncode(caption)}\"></li>\n");
        }
        sb.Append("</ol>\n</main>\n</body>\n</html>\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    // ---- Markdown --------------------------------------------------------------------------

    /// <summary>A .md file and, next to it, a folder with one picture per step.</summary>
    public static void SaveMarkdown(IReadOnlyList<RecordedStep> steps, string title, Color accent, string path)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string name = Path.GetFileNameWithoutExtension(path);
        string images = name + " images";
        Directory.CreateDirectory(Path.Combine(dir, images));
        var sb = new StringBuilder();
        sb.Append($"# {title}\n\n_{Subtitle(steps.Count)}_\n\n");
        for (int i = 0; i < steps.Count; i++)
        {
            string file = $"step-{i + 1:00}.png";
            File.WriteAllBytes(Path.Combine(dir, images, file), Encode(StepTutorial.Marked(steps[i], accent), jpeg: false));
            string caption = StepTutorial.Caption(steps[i]);
            // spaces in the folder name are written as %20 so every Markdown viewer finds the picture
            sb.Append($"{i + 1}. {caption}\n\n   ![Step {i + 1}]({Uri.EscapeDataString(images)}/{file})\n\n");
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    // ---- PDF -------------------------------------------------------------------------------

    /// <summary>One landscape A4 page per step: the number, the sentence and the picture.</summary>
    public static void SavePdf(IReadOnlyList<RecordedStep> steps, string title, Color accent, string path)
    {
        const double PageW = 842, PageH = 595, Margin = 36;
        var pdf = new PdfWriter();
        int fontRegular = pdf.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        int fontBold = pdf.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        int pagesId = pdf.Reserve();
        var pageIds = new List<int>();
        string r = Num(accent.R / 255.0), g = Num(accent.G / 255.0), b = Num(accent.B / 255.0);

        for (int i = 0; i < steps.Count; i++)
        {
            var picture = StepTutorial.Marked(steps[i], accent);
            byte[] jpeg = Encode(picture, jpeg: true);
            int imageId = pdf.AddStream($"<< /Type /XObject /Subtype /Image /Width {picture.PixelWidth} /Height {picture.PixelHeight} " +
                                        "/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode", jpeg);

            var c = new StringBuilder();
            double top = PageH - Margin;
            // a small header: the guide's title and where we are
            c.Append($"BT /F1 9 Tf 0.4 0.43 0.46 rg {Num(Margin)} {Num(top - 9)} Td ({Pdf(title)}  -  {i + 1} of {steps.Count}) Tj ET\n");
            top -= 26;
            // the number in a coloured circle
            double cx = Margin + 15, cy = top - 15;
            c.Append($"{r} {g} {b} rg {Circle(cx, cy, 15)} f\n");
            string number = (i + 1).ToString();
            c.Append($"BT /F2 14 Tf 1 1 1 rg {Num(cx - number.Length * 3.9)} {Num(cy - 5)} Td ({number}) Tj ET\n");
            // the sentence, wrapped
            var lines = Wrap(StepTutorial.Caption(steps[i]), 17, PageW - Margin * 2 - 44);
            double ty = cy + (lines.Count - 1) * 10.5 - 6;
            foreach (var line in lines)
            {
                c.Append($"BT /F2 17 Tf 0.12 0.14 0.16 rg {Num(Margin + 44)} {Num(ty)} Td ({Pdf(line)}) Tj ET\n");
                ty -= 21;
            }
            double below = Math.Min(cy - 15, ty + 21 - 6) - 14;
            // the picture, as big as fits, centred, with a thin border
            double boxW = PageW - Margin * 2, boxH = below - Margin;
            double k = Math.Min(boxW / picture.PixelWidth, boxH / picture.PixelHeight);
            double iw = picture.PixelWidth * k, ih = picture.PixelHeight * k, ix = Margin + (boxW - iw) / 2, iy = below - ih;
            c.Append($"q {Num(iw)} 0 0 {Num(ih)} {Num(ix)} {Num(iy)} cm /Im1 Do Q\n");
            c.Append($"0.82 0.84 0.87 RG 0.75 w {Num(ix)} {Num(iy)} {Num(iw)} {Num(ih)} re S\n");

            int contentId = pdf.AddStream("<<", Encoding.ASCII.GetBytes(c.ToString()));
            int pageId = pdf.Add($"<< /Type /Page /Parent {pagesId} 0 R /MediaBox [0 0 {Num(PageW)} {Num(PageH)}] " +
                                 $"/Resources << /Font << /F1 {fontRegular} 0 R /F2 {fontBold} 0 R >> /XObject << /Im1 {imageId} 0 R >> >> " +
                                 $"/Contents {contentId} 0 R >>");
            pageIds.Add(pageId);
        }
        pdf.Set(pagesId, $"<< /Type /Pages /Kids [{string.Join(" ", pageIds.Select(p => p + " 0 R"))}] /Count {pageIds.Count} >>");
        int info = pdf.Add($"<< /Title ({Pdf(title)}) /Producer (Overskin) >>");
        int catalog = pdf.Add($"<< /Type /Catalog /Pages {pagesId} 0 R >>");
        File.WriteAllBytes(path, pdf.Finish(catalog, info));
    }

    private static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>A circle as four Bézier curves.</summary>
    private static string Circle(double cx, double cy, double rad)
    {
        double k = rad * 0.5523;
        return $"{Num(cx + rad)} {Num(cy)} m " +
               $"{Num(cx + rad)} {Num(cy + k)} {Num(cx + k)} {Num(cy + rad)} {Num(cx)} {Num(cy + rad)} c " +
               $"{Num(cx - k)} {Num(cy + rad)} {Num(cx - rad)} {Num(cy + k)} {Num(cx - rad)} {Num(cy)} c " +
               $"{Num(cx - rad)} {Num(cy - k)} {Num(cx - k)} {Num(cy - rad)} {Num(cx)} {Num(cy - rad)} c " +
               $"{Num(cx + k)} {Num(cy - rad)} {Num(cx + rad)} {Num(cy - k)} {Num(cx + rad)} {Num(cy)} c";
    }

    /// <summary>Breaks text into lines no wider than <paramref name="width"/> points, measured with Arial (the same widths as Helvetica).</summary>
    private static List<string> Wrap(string text, double size, double width)
    {
        var face = new Typeface(new FontFamily("Arial"), System.Windows.FontStyles.Normal, System.Windows.FontWeights.Bold, System.Windows.FontStretches.Normal);
        double Measure(string s) => new FormattedText(s, CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight, face, size, Brushes.Black, 1).WidthIncludingTrailingWhitespace;
        var lines = new List<string>();
        var line = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string tryLine = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && Measure(tryLine) > width)
            {
                lines.Add(line);
                line = word;
            }
            else line = tryLine;
        }
        if (line.Length > 0) lines.Add(line);
        if (lines.Count == 0) lines.Add("");
        return lines;
    }

    /// <summary>Text for a PDF string in WinAnsi: brackets and backslashes escaped, curly quotes kept, anything else outside it becomes "?".</summary>
    internal static string Pdf(string s)
    {
        var sb = new StringBuilder();
        foreach (char ch in s)
        {
            int code = ch switch
            {
                '‘' => 0x91, '’' => 0x92, '“' => 0x93, '”' => 0x94, '–' => 0x96, '—' => 0x97, '…' => 0x85, '•' => 0x95, '€' => 0x80,
                _ when ch < 0x7F && ch >= 0x20 => ch,
                _ when ch >= 0xA0 && ch <= 0xFF => ch,
                _ => '?',
            };
            if (code is '(' or ')' or '\\') sb.Append('\\').Append((char)code);
            else if (code < 0x80) sb.Append((char)code);
            else sb.Append('\\').Append(Convert.ToString(code, 8).PadLeft(3, '0'));
        }
        return sb.ToString();
    }

    /// <summary>Just enough of a PDF writer: numbered objects, streams, and the cross-reference table.</summary>
    private sealed class PdfWriter
    {
        private readonly List<byte[]?> _objects = new();

        public int Reserve()
        {
            _objects.Add(null);
            return _objects.Count;
        }

        public int Add(string body)
        {
            _objects.Add(Encoding.ASCII.GetBytes(body));
            return _objects.Count;
        }

        public void Set(int id, string body) => _objects[id - 1] = Encoding.ASCII.GetBytes(body);

        /// <summary><paramref name="dictionaryStart"/> is the stream's dictionary without its closing "&gt;&gt;".</summary>
        public int AddStream(string dictionaryStart, byte[] data)
        {
            using var ms = new MemoryStream();
            var head = Encoding.ASCII.GetBytes($"{dictionaryStart} /Length {data.Length} >>\nstream\n");
            ms.Write(head);
            ms.Write(data);
            ms.Write(Encoding.ASCII.GetBytes("\nendstream"));
            _objects.Add(ms.ToArray());
            return _objects.Count;
        }

        public byte[] Finish(int catalog, int info)
        {
            using var ms = new MemoryStream();
            void Write(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
            Write("%PDF-1.4\n");
            ms.Write(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' }); // marks the file as binary
            var offsets = new long[_objects.Count];
            for (int i = 0; i < _objects.Count; i++)
            {
                offsets[i] = ms.Position;
                Write($"{i + 1} 0 obj\n");
                ms.Write(_objects[i] ?? Encoding.ASCII.GetBytes("null"));
                Write("\nendobj\n");
            }
            long xref = ms.Position;
            Write($"xref\n0 {_objects.Count + 1}\n0000000000 65535 f \n");
            foreach (long o in offsets) Write($"{o:0000000000} 00000 n \n");
            Write($"trailer\n<< /Size {_objects.Count + 1} /Root {catalog} 0 R /Info {info} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            return ms.ToArray();
        }
    }
}
