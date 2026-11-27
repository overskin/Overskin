using System.Globalization;
using System.Net;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Overskin.Linux.Core;
using Overskin.Linux.Platform;

namespace Overskin.Linux.Interface;

/// <summary>One recorded step: its picture (with the click marked), and what it says.</summary>
public sealed class GuideStep
{
    public required Bitmap Picture { get; init; }
    public string Caption { get; set; } = "";
    public bool Keep { get; set; } = true;
}

/// <summary>
/// After recording steps (as on Windows): check each one, reword it or leave it out, then make the
/// guide as one tall picture (for the editor), one web page with the pictures inside it,
/// Markdown, or a PDF with a page per step.
/// </summary>
public sealed class StepsWindow : Window
{
    private readonly List<GuideStep> _steps;
    private readonly TextBox _title = new() { Text = "How to…", MinWidth = 300 };
    private readonly TextBlock _status = Ui.Label("", "dim");

    public StepsWindow(List<GuideStep> steps)
    {
        _steps = steps;
        Title = "Your steps";
        Icon = App.Icon;
        Width = 760;
        Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var list = new StackPanel { Spacing = 12, Margin = new Thickness(16) };
        for (int i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var caption = new TextBox { Text = step.Caption, FontWeight = FontWeight.SemiBold };
            caption.TextChanged += (_, _) => step.Caption = caption.Text ?? "";
            var keep = new CheckBox { Content = "Leave this step out", IsChecked = !step.Keep };
            keep.IsCheckedChanged += (_, _) => step.Keep = keep.IsChecked != true;
            list.Children.Add(Ui.Card(new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new DockPanel { Children = { Docked(new TextBlock { Text = $"{i + 1}", FontWeight = FontWeight.Bold, Width = 28, VerticalAlignment = VerticalAlignment.Center }), caption } },
                    new Image { Source = step.Picture, MaxHeight = 260, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left },
                    keep,
                },
            }));
        }
        var buttons = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
        buttons.Children.Add(Tip(Ui.Button("One picture", Tall, Icons.Picture, "primary"), "One tall picture, opened in the editor"));
        buttons.Children.Add(Tip(Ui.Button("Web page", () => _ = SaveAsync("html")), "One web page with the pictures inside it"));
        buttons.Children.Add(Tip(Ui.Button("Markdown", () => _ = SaveAsync("md")), "A Markdown file and a folder of pictures beside it"));
        buttons.Children.Add(Tip(Ui.Button("PDF", () => _ = SaveAsync("pdf")), "One page per step"));
        var top = new StackPanel
        {
            Spacing = 8, Margin = new Thickness(16, 12),
            Children = { Ui.Label("Check each step: reword it, or leave it out. Then make the guide.", "dim"), new DockPanel { Children = { Docked(Ui.Label("Title", "dim"), new Thickness(0, 0, 8, 0)), _title } }, buttons, _status },
        };
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(new ScrollViewer { Content = list });
        Content = root;
    }

    private static Control Tip(Control c, string tip) { ToolTip.SetTip(c, tip); return c; }

    private static Control Docked(Control c, Thickness? margin = null)
    {
        DockPanel.SetDock(c, Dock.Left);
        if (margin is { } m) c.Margin = m;
        return c;
    }

    private List<GuideStep> Kept => _steps.Where(s => s.Keep).ToList();

    private void Tall()
    {
        var kept = Kept;
        if (kept.Count == 0) { _status.Text = "There are no steps left."; return; }
        new EditorWindow(StepGuide.TallPicture(kept, _title.Text ?? "", Colours.Accent), null).Show();
    }

    private async Task SaveAsync(string kind)
    {
        var kept = Kept;
        if (kept.Count == 0) { _status.Text = "There are no steps left."; return; }
        var (label, pattern) = kind switch { "html" => ("Web page", "*.html"), "md" => ("Markdown", "*.md"), _ => ("PDF", "*.pdf") };
        var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "Save the guide",
            SuggestedFileName = (string.IsNullOrWhiteSpace(_title.Text) ? "Steps" : _title.Text!.Trim()) + pattern[1..],
            DefaultExtension = pattern[2..],
            FileTypeChoices = new[] { new Avalonia.Platform.Storage.FilePickerFileType(label) { Patterns = new[] { pattern } } },
        });
        var path = file != null ? Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(file) : null;
        if (path == null) return;
        try
        {
            string title = string.IsNullOrWhiteSpace(_title.Text) ? "Steps" : _title.Text!.Trim();
            switch (kind)
            {
                case "html": StepGuide.SaveHtml(kept, title, Colours.Accent, path); break;
                case "md": StepGuide.SaveMarkdown(kept, title, path); break;
                default: StepGuide.SavePdf(kept, title, Colours.Accent, path); break;
            }
            _status.Text = "Saved " + Path.GetFileName(path);
            Desktop.Open(path);
        }
        catch (Exception ex) { _status.Text = "Couldn't save the guide: " + ex.Message; }
    }
}

/// <summary>Saves recorded steps as a guide (as on Windows): one HTML file, Markdown with a folder of pictures, a PDF, or one tall picture.</summary>
public static class StepGuide
{
    private static string Subtitle(int count) =>
        $"{count} step{(count == 1 ? "" : "s")} · {DateTime.Now.ToString("d MMMM yyyy", CultureInfo.CurrentCulture)}";

    /// <summary>One self-contained page: the pictures are inside it, so it can be emailed or put online as is.</summary>
    public static void SaveHtml(IReadOnlyList<GuideStep> steps, string title, Color accent, string path)
    {
        string hex = $"#{accent.R:X2}{accent.G:X2}{accent.B:X2}";
        var sb = new StringBuilder();
        sb.Append("<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append($"<title>{WebUtility.HtmlEncode(title)}</title>\n<style>\n:root {{ --accent: {hex}; }}\n");
        sb.Append("body { margin: 0; background: #f4f5f7; color: #1f2328; font: 16px/1.5 -apple-system, system-ui, sans-serif; }\n");
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
            string data = Convert.ToBase64String(Imaging.Png(steps[i].Picture));
            sb.Append($"<li><p>{WebUtility.HtmlEncode(steps[i].Caption)}</p><img src=\"data:image/png;base64,{data}\" alt=\"Step {i + 1}: {WebUtility.HtmlEncode(steps[i].Caption)}\"></li>\n");
        }
        sb.Append("</ol>\n</main>\n</body>\n</html>\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>A .md file and, next to it, a folder with one picture per step.</summary>
    public static void SaveMarkdown(IReadOnlyList<GuideStep> steps, string title, string path)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(path))!, name = Path.GetFileNameWithoutExtension(path), images = name + " images";
        Directory.CreateDirectory(Path.Combine(dir, images));
        var sb = new StringBuilder($"# {title}\n\n_{Subtitle(steps.Count)}_\n\n");
        for (int i = 0; i < steps.Count; i++)
        {
            string file = $"step-{i + 1:00}.png";
            Imaging.SavePng(steps[i].Picture, Path.Combine(dir, images, file));
            // spaces in the folder name are written as %20 so every Markdown viewer finds the picture
            sb.Append($"{i + 1}. {steps[i].Caption}\n\n   ![Step {i + 1}]({Uri.EscapeDataString(images)}/{file})\n\n");
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>The steps one under another in a single picture, numbered, on a light page.</summary>
    public static Bitmap TallPicture(IReadOnlyList<GuideStep> steps, string title, Color accent)
    {
        const int Width = 1200, Pad = 40, Gap = 30;
        var face = new Typeface(new FontFamily("Inter"), FontStyle.Normal, FontWeight.SemiBold);
        var heads = new List<FormattedText>();
        var sizes = new List<Size>();
        double height = Pad + 56;
        foreach (var s in steps)
        {
            var head = new FormattedText(s.Caption, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, 22, new SolidColorBrush(Color.FromRgb(31, 35, 40))) { MaxTextWidth = Width - 2 * Pad - 52 };
            heads.Add(head);
            double k = Math.Min(1, (Width - 2 * Pad) / (double)s.Picture.PixelSize.Width);
            var size = new Size(s.Picture.PixelSize.Width * k, s.Picture.PixelSize.Height * k);
            sizes.Add(size);
            height += Math.Max(36, head.Height) + 14 + size.Height + Gap;
        }
        height += Pad;
        return Imaging.Render(new PixelSize(Width, (int)Math.Ceiling(height)), ctx =>
        {
            ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(244, 245, 247)), new Rect(0, 0, Width, height));
            var titleText = new FormattedText(title, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Inter"), FontStyle.Normal, FontWeight.Bold), 30, Brushes.Black);
            ctx.DrawText(titleText, new Point(Pad, Pad));
            double y = Pad + 56;
            for (int i = 0; i < steps.Count; i++)
            {
                ctx.DrawEllipse(new SolidColorBrush(accent), null, new Point(Pad + 18, y + 18), 18, 18);
                var number = new FormattedText($"{i + 1}", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, 16, Brushes.White);
                ctx.DrawText(number, new Point(Pad + 18 - number.Width / 2, y + 18 - number.Height / 2));
                ctx.DrawText(heads[i], new Point(Pad + 52, y + 18 - Math.Min(heads[i].Height, 36) / 2));
                y += Math.Max(36, heads[i].Height) + 14;
                var box = new Rect(Pad, y, sizes[i].Width, sizes[i].Height);
                using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
                    ctx.DrawImage(steps[i].Picture, new Rect(steps[i].Picture.Size), box);
                ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(208, 215, 222)), 1), box);
                y += sizes[i].Height + Gap;
            }
        });
    }

    // ---- PDF ---------------------------------------------------------------------------------

    /// <summary>One landscape A4 page per step: the number, the sentence and the picture.</summary>
    public static void SavePdf(IReadOnlyList<GuideStep> steps, string title, Color accent, string path)
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
            var picture = steps[i].Picture;
            byte[] jpeg = Jpeg(picture);
            int imageId = pdf.AddStream($"<< /Type /XObject /Subtype /Image /Width {picture.PixelSize.Width} /Height {picture.PixelSize.Height} " +
                                        "/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode", jpeg);
            var c = new StringBuilder();
            double top = PageH - Margin;
            // a small header: the guide's title and where we are
            c.Append($"BT /F1 9 Tf 0.4 0.43 0.46 rg {Num(Margin)} {Num(top - 9)} Td ({Pdf(title)}  -  {i + 1} of {steps.Count}) Tj ET\n");
            top -= 26;
            double cx = Margin + 15, cy = top - 15;
            c.Append($"{r} {g} {b} rg {Circle(cx, cy, 15)} f\n");
            string number = (i + 1).ToString();
            c.Append($"BT /F2 14 Tf 1 1 1 rg {Num(cx - number.Length * 3.9)} {Num(cy - 5)} Td ({number}) Tj ET\n");
            var lines = Wrap(steps[i].Caption, 17, PageW - Margin * 2 - 44);
            double ty = cy + (lines.Count - 1) * 10.5 - 6;
            foreach (var line in lines)
            {
                c.Append($"BT /F2 17 Tf 0.12 0.14 0.16 rg {Num(Margin + 44)} {Num(ty)} Td ({Pdf(line)}) Tj ET\n");
                ty -= 21;
            }
            double below = Math.Min(cy - 15, ty + 21 - 6) - 14;
            // the picture, as big as fits, centred, with a thin border
            double boxW = PageW - Margin * 2, boxH = below - Margin;
            double k = Math.Min(boxW / picture.PixelSize.Width, boxH / picture.PixelSize.Height);
            double iw = picture.PixelSize.Width * k, ih = picture.PixelSize.Height * k, ix = Margin + (boxW - iw) / 2, iy = below - ih;
            c.Append($"q {Num(iw)} 0 0 {Num(ih)} {Num(ix)} {Num(iy)} cm /Im1 Do Q\n");
            c.Append($"0.82 0.84 0.87 RG 0.75 w {Num(ix)} {Num(iy)} {Num(iw)} {Num(ih)} re S\n");
            int contentId = pdf.AddStream("<<", Encoding.ASCII.GetBytes(c.ToString()));
            pageIds.Add(pdf.Add($"<< /Type /Page /Parent {pagesId} 0 R /MediaBox [0 0 {Num(PageW)} {Num(PageH)}] " +
                                $"/Resources << /Font << /F1 {fontRegular} 0 R /F2 {fontBold} 0 R >> /XObject << /Im1 {imageId} 0 R >> >> " +
                                $"/Contents {contentId} 0 R >>"));
        }
        pdf.Set(pagesId, $"<< /Type /Pages /Kids [{string.Join(" ", pageIds.Select(p => p + " 0 R"))}] /Count {pageIds.Count} >>");
        int info = pdf.Add($"<< /Title ({Pdf(title)}) /Producer (Overskin) >>");
        int catalog = pdf.Add($"<< /Type /Catalog /Pages {pagesId} 0 R >>");
        File.WriteAllBytes(path, pdf.Finish(catalog, info));
    }

    /// <summary>A picture as JPEG (flattened onto white: JPEG has no see-through).</summary>
    private static byte[] Jpeg(Bitmap picture)
    {
        var flat = Imaging.Render(picture.PixelSize, ctx =>
        {
            ctx.FillRectangle(Brushes.White, new Rect(0, 0, picture.PixelSize.Width, picture.PixelSize.Height));
            ctx.DrawImage(picture, new Rect(picture.Size), new Rect(0, 0, picture.PixelSize.Width, picture.PixelSize.Height));
        });
        var file = Path.Combine(Path.GetTempPath(), $"overskin-step-{Guid.NewGuid():N}.jpg");
        try
        {
            Imaging.SaveJpeg(flat, file, 90);
            return File.ReadAllBytes(file);
        }
        finally { try { File.Delete(file); } catch { } }
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

    /// <summary>Breaks text into lines no wider than <paramref name="width"/> points, measured as Helvetica Bold (Arial's widths, near enough).</summary>
    private static List<string> Wrap(string text, double size, double width)
    {
        var face = new Typeface(new FontFamily("Arial, Helvetica, Inter"), FontStyle.Normal, FontWeight.Bold);
        double Measure(string s) => new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, Brushes.Black).WidthIncludingTrailingWhitespace;
        var lines = new List<string>();
        var line = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string tryLine = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && Measure(tryLine) > width) { lines.Add(line); line = word; }
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

        public int Reserve() { _objects.Add(null); return _objects.Count; }
        public int Add(string body) { _objects.Add(Encoding.ASCII.GetBytes(body)); return _objects.Count; }
        public void Set(int id, string body) => _objects[id - 1] = Encoding.ASCII.GetBytes(body);

        /// <summary><paramref name="dictionaryStart"/> is the stream's dictionary without its closing "&gt;&gt;".</summary>
        public int AddStream(string dictionaryStart, byte[] data)
        {
            using var ms = new MemoryStream();
            ms.Write(Encoding.ASCII.GetBytes($"{dictionaryStart} /Length {data.Length} >>\nstream\n"));
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
