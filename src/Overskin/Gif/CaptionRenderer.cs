using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Overskin;

public enum CaptionStyle { None, Caption, Meme }

/// <summary>Everything that decides how a captioned frame looks.</summary>
public sealed record CaptionSettings(CaptionStyle Style, string Top, string Bottom, double Size);

/// <summary>
/// Draws a frame with its caption. Used both for the live preview and for export, so what you see
/// is exactly what you get. Sizes scale with the output width, like the meme tools do.
/// </summary>
public static class CaptionRenderer
{
    // The white-bar caption memes use Futura Condensed Extra Bold; Windows doesn't ship it, so
    // the nearest installed heavy condensed face is used when it's missing.
    private static readonly Typeface CaptionFace = PickCaptionFace();
    private static readonly Typeface MemeFace = new(new FontFamily("Impact, Arial Black"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private static Typeface PickCaptionFace()
    {
        foreach (var name in new[] { "Futura Condensed ExtraBold", "Futura Condensed Extra Bold", "Futura Extra Black Condensed" })
        {
            var face = new Typeface(new FontFamily(name), FontStyles.Normal, FontWeights.ExtraBold, FontStretches.Condensed);
            if (face.TryGetGlyphTypeface(out var glyph) && glyph.FamilyNames.Values.Any(n => n.StartsWith("Futura", StringComparison.OrdinalIgnoreCase)))
                return face;
        }
        var bahn = new Typeface(new FontFamily("Bahnschrift"), FontStyles.Normal, FontWeights.Bold, FontStretches.Condensed);
        if (bahn.TryGetGlyphTypeface(out _)) return bahn;
        return new Typeface(new FontFamily("Arial Narrow, Arial"), FontStyles.Normal, FontWeights.Bold, FontStretches.Condensed);
    }

    private static FormattedText Text(string text, Typeface face, double em, double maxWidth, Brush brush) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, Math.Max(4, em), brush, 1.0)
        {
            MaxTextWidth = Math.Max(10, maxWidth),
            TextAlignment = TextAlignment.Center,
        };

    /// <summary>Output size for a frame scaled to <paramref name="outW"/>, including any caption bar.</summary>
    public static Size Layout(int frameW, int frameH, int outW, CaptionSettings s, out double barHeight)
    {
        double frameHeight = Math.Round(frameH * (double)outW / frameW);
        barHeight = 0;
        if (s.Style == CaptionStyle.Caption && !string.IsNullOrWhiteSpace(s.Top))
        {
            double em = outW * 0.075 * s.Size;
            var ft = Text(s.Top.Trim(), CaptionFace, em, outW * 0.9, Brushes.Black);
            barHeight = Math.Round(ft.Height + em * 1.1);
        }
        return new Size(outW, Math.Max(1, barHeight + frameHeight));
    }

    public static void Draw(DrawingContext dc, BitmapSource frame, int outW, CaptionSettings s)
    {
        var size = Layout(frame.PixelWidth, frame.PixelHeight, outW, s, out double bar);
        double frameH = size.Height - bar;

        if (bar > 0)
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, outW, bar));
            double em = outW * 0.075 * s.Size;
            var ft = Text(s.Top.Trim(), CaptionFace, em, outW * 0.9, Brushes.Black);
            dc.DrawText(ft, new Point(outW * 0.05, (bar - ft.Height) / 2));
        }

        dc.DrawImage(frame, new Rect(0, bar, outW, frameH));

        if (s.Style == CaptionStyle.Meme)
        {
            double em = outW * 0.1 * s.Size;
            DrawOutlined(dc, s.Top, em, outW, top: true, 0, frameH);
            DrawOutlined(dc, s.Bottom, em, outW, top: false, 0, frameH);
        }
    }

    /// <summary>Classic image-macro text: white Impact capitals with a thick black outline.</summary>
    private static void DrawOutlined(DrawingContext dc, string text, double em, int outW, bool top, double y0, double frameH)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var ft = Text(text.Trim().ToUpper(CultureInfo.CurrentCulture), MemeFace, em, outW * 0.94, Brushes.White);
        double margin = frameH * 0.03;
        double y = top ? y0 + margin : y0 + frameH - ft.Height - margin;
        var geometry = ft.BuildGeometry(new Point(outW * 0.03, y));
        var pen = new Pen(Brushes.Black, Math.Max(1.5, em * 0.11)) { LineJoin = PenLineJoin.Round };
        dc.DrawGeometry(null, pen, geometry);
        dc.DrawGeometry(Brushes.White, null, geometry);
    }

    /// <summary>One finished frame as BGRA bytes.</summary>
    public static byte[] Render(BitmapSource frame, int outW, CaptionSettings s, out int width, out int height)
    {
        var size = Layout(frame.PixelWidth, frame.PixelHeight, outW, s, out _);
        width = outW;
        height = (int)size.Height;
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            Draw(dc, frame, outW, s);
        }
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var px = new byte[width * height * 4];
        rtb.CopyPixels(px, width * 4, 0);
        return px;
    }
}

/// <summary>Shows a frame with its caption, scaled to fit, exactly as it will be exported.</summary>
public sealed class GifPreview : FrameworkElement
{
    private BitmapSource? _frame;
    private CaptionSettings _settings = new(CaptionStyle.None, "", "", 1);
    private int _outW = 480;

    public void Show(BitmapSource frame, CaptionSettings settings, int outW)
    {
        _frame = frame;
        _settings = settings;
        _outW = Math.Max(16, outW);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_frame == null || ActualWidth < 2 || ActualHeight < 2) return;
        var size = CaptionRenderer.Layout(_frame.PixelWidth, _frame.PixelHeight, _outW, _settings, out _);
        double k = Math.Min(1.0 * ActualWidth / size.Width, ActualHeight / size.Height);
        k = Math.Min(k, Math.Max(1, 520.0 / size.Width)); // small clips get enlarged, but not absurdly
        double w = size.Width * k, h = size.Height * k;
        dc.PushTransform(new TranslateTransform((ActualWidth - w) / 2, (ActualHeight - h) / 2));
        dc.PushTransform(new ScaleTransform(k, k));
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, size.Width, size.Height));
        CaptionRenderer.Draw(dc, _frame, _outW, _settings);
        dc.Pop();
        dc.Pop();
    }
}
