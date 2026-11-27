using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Overskin;

/// <summary>
/// The AI models inside Overskin.exe, run on this PC with ONNX Runtime (nothing is sent
/// anywhere): IS-Net finds the subject of a picture to cut out, and CLIP turns pictures and
/// words into fingerprints that can be compared, so the Library finds pictures by what's in them.
/// Each model is loaded the first time it's needed and then kept.
/// </summary>
public static class AiModels
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, InferenceSession> Sessions = new();

    /// <summary>Whether this copy of Overskin has the models in it.</summary>
    public static bool Available =>
        typeof(AiModels).Assembly.GetManifestResourceInfo("Overskin.cutout-model.onnx") != null;

    private static byte[] Resource(string name)
    {
        using var stream = typeof(AiModels).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("This copy of Overskin was built without its AI models.");
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static InferenceSession Session(string name)
    {
        lock (Gate)
        {
            if (Sessions.TryGetValue(name, out var s)) return s;
            var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount - 1, 1, 8),
            };
            s = new InferenceSession(Resource(name), options);
            Sessions[name] = s;
            return s;
        }
    }

    /// <summary>
    /// Runs work that draws with WPF (which needs a thread of its own kind) away from the window,
    /// so it stays responsive while a model thinks.
    /// </summary>
    public static Task<T> OffThread<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { done.SetResult(work()); }
            catch (Exception ex) { done.SetException(ex); }
        }) { IsBackground = true, Name = "Overskin AI" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    /// <summary>A picture as plain BGRA bytes at <paramref name="w"/> × <paramref name="h"/>, scaled smoothly.</summary>
    private static byte[] Pixels(BitmapSource image, int w, int h)
    {
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
            dc.DrawImage(image, new Rect(0, 0, w, h));
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var px = new byte[w * h * 4];
        rtb.CopyPixels(px, w * 4, 0);
        return px;
    }

    /// <summary>
    /// Tries every model on a made-up picture and says how it went, for "--check-ai": proof that
    /// a build (single-file and installed included) really carries working models.
    /// </summary>
    public static List<string> Check()
    {
        var log = new List<string> { $"models inside: {Available} (expect True)" };
        try
        {
            // a red ball on a plain grey background
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(200, 200, 200)), null, new Rect(0, 0, 400, 300));
                dc.DrawEllipse(new RadialGradientBrush(Color.FromRgb(255, 90, 80), Color.FromRgb(170, 20, 20)), null, new Point(200, 150), 80, 80);
            }
            var rtb = new RenderTargetBitmap(400, 300, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var cut = CutOut(rtb);
            log.Add(cut == null ? "cut out: nothing found (expect a ball)"
                : $"cut out: {cut.PixelWidth}x{cut.PixelHeight} (expect about 170x170) in {watch.ElapsedMilliseconds} ms");
            if (cut != null)
            {
                var px = new byte[cut.PixelWidth * cut.PixelHeight * 4];
                new FormatConvertedBitmap(cut, PixelFormats.Bgra32, null, 0).CopyPixels(px, cut.PixelWidth * 4, 0);
                log.Add($"cut out: corner see-through={px[3]} (expect under 20) middle solid={px[(cut.PixelHeight / 2 * cut.PixelWidth + cut.PixelWidth / 2) * 4 + 3]} (expect 255)");
            }
            watch.Restart();
            var picture = PictureFingerprint(rtb);
            float ball = Likeness(picture, WordsFingerprint("a picture of a red ball"));
            float dog = Likeness(picture, WordsFingerprint("a picture of a dog"));
            log.Add($"picture search: red ball={ball:0.000} dog={dog:0.000} (expect ball higher) in {watch.ElapsedMilliseconds} ms");
            log.Add($"tokenizer: [{string.Join(", ", ClipTokenizer.Encode("a photo of a cat"))}] (expect 49406, 320, 1125, 539, 320, 2368, 49407)");
        }
        catch (Exception ex)
        {
            log.Add("FAILED " + ex);
        }
        return log;
    }

    // ===================================================================================
    //  cutting out the subject
    // ===================================================================================

    private const int CutoutSide = 1024;

    /// <summary>
    /// How much each pixel belongs to the subject, 0 to 255, at the picture's own size. Call from
    /// a background thread; the picture must be frozen.
    /// </summary>
    public static byte[] SubjectMask(BitmapSource image)
    {
        int w = image.PixelWidth, h = image.PixelHeight;
        var px = Pixels(image, CutoutSide, CutoutSide);
        // IS-Net reads the picture at 1024 × 1024, each colour as (value / 255 − 0.5)
        var input = new DenseTensor<float>(new[] { 1, 3, CutoutSide, CutoutSide });
        int plane = CutoutSide * CutoutSide;
        var data = input.Buffer.Span;
        for (int i = 0; i < plane; i++)
        {
            data[i] = px[i * 4 + 2] / 255f - 0.5f;
            data[plane + i] = px[i * 4 + 1] / 255f - 0.5f;
            data[2 * plane + i] = px[i * 4] / 255f - 0.5f;
        }
        float[] map;
        var session = Session("Overskin.cutout-model.onnx");
        using (var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(session.InputMetadata.Keys.First(), input) }))
            map = results.First(r => r.Name == session.OutputMetadata.Keys.First()).AsEnumerable<float>().ToArray();
        // stretched to 0..1, the way IS-Net's makers do
        float lo = map.Min(), hi = map.Max(), span = Math.Max(1e-6f, hi - lo);
        var mask = new byte[plane];
        for (int i = 0; i < plane; i++) mask[i] = (byte)Math.Clamp((map[i] - lo) / span * 255f, 0, 255);
        // back to the picture's own size, smoothly
        var small = BitmapSource.Create(CutoutSide, CutoutSide, 96, 96, PixelFormats.Gray8, null, mask, CutoutSide);
        var big = new TransformedBitmap(small, new ScaleTransform(w / (double)CutoutSide, h / (double)CutoutSide));
        var result = new byte[w * h];
        var sized = big.PixelWidth == w && big.PixelHeight == h ? (BitmapSource)big : new CroppedBitmap(big, new Int32Rect(0, 0, Math.Min(w, big.PixelWidth), Math.Min(h, big.PixelHeight)));
        sized.CopyPixels(result, sized.PixelWidth, 0);
        if (sized.PixelWidth != w)
        {
            // a rounding pixel short: copy row by row
            var fixedUp = new byte[w * h];
            for (int y = 0; y < Math.Min(h, sized.PixelHeight); y++)
                Array.Copy(result, y * sized.PixelWidth, fixedUp, y * w, Math.Min(w, sized.PixelWidth));
            result = fixedUp;
        }
        return result;
    }

    /// <summary>
    /// The subject of the picture on a see-through background, trimmed to the subject (with a
    /// little room). Null when nothing stands out.
    /// </summary>
    public static BitmapSource? CutOut(BitmapSource image)
    {
        var src = ImageLoader.Normalize(image);
        int w = src.PixelWidth, h = src.PixelHeight;
        var mask = SubjectMask(src);
        var px = new byte[w * h * 4];
        new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0).CopyPixels(px, w * 4, 0);
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                // soft edges stay soft, faint noise in the background goes
                int a = mask[i] < 12 ? 0 : mask[i];
                px[i * 4 + 3] = (byte)(px[i * 4 + 3] * a / 255);
                if (a > 60)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        if (maxX < 0 || (maxX - minX) * (maxY - minY) < 64) return null;
        Defringe(px, w, h);
        int pad = Math.Max(4, Math.Min(w, h) / 100);
        minX = Math.Max(0, minX - pad); minY = Math.Max(0, minY - pad);
        maxX = Math.Min(w - 1, maxX + pad); maxY = Math.Min(h - 1, maxY + pad);
        var full = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        var cut = new CroppedBitmap(full, new Int32Rect(minX, minY, maxX - minX + 1, maxY - minY + 1));
        var done = ImageLoader.Normalize(cut);
        done.Freeze();
        return done;
    }

    /// <summary>
    /// The soft edge of a cut-out still holds some of the old background's colour, which shows as a
    /// halo on anything else. Each part-see-through pixel takes the colour of the solid subject
    /// next to it instead (its see-through-ness stays), and fully clear pixels are emptied.
    /// </summary>
    internal static void Defringe(byte[] px, int w, int h)
    {
        var colour = (byte[])px.Clone();
        const int reach = 3;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                byte a = px[o + 3];
                if (a == 0) { px[o] = px[o + 1] = px[o + 2] = 0; continue; }
                if (a >= 230) continue;
                double sb = 0, sg = 0, sr = 0, n = 0;
                for (int dy = -reach; dy <= reach; dy++)
                {
                    int yy = y + dy;
                    if (yy < 0 || yy >= h) continue;
                    for (int dx = -reach; dx <= reach; dx++)
                    {
                        int xx = x + dx;
                        if (xx < 0 || xx >= w) continue;
                        int q = (yy * w + xx) * 4;
                        if (colour[q + 3] < 230) continue;
                        double k = 1.0 / (1 + dx * dx + dy * dy); // nearer counts more
                        sb += colour[q] * k; sg += colour[q + 1] * k; sr += colour[q + 2] * k; n += k;
                    }
                }
                if (n <= 0) continue;
                px[o] = (byte)(sb / n); px[o + 1] = (byte)(sg / n); px[o + 2] = (byte)(sr / n);
            }
    }

    // ===================================================================================
    //  CLIP: pictures and words as comparable fingerprints
    // ===================================================================================

    private const int ClipSide = 224;
    private static readonly float[] Mean = { 0.48145466f, 0.4578275f, 0.40821073f };
    private static readonly float[] Spread = { 0.26862954f, 0.26130258f, 0.27577711f };

    /// <summary>A picture's fingerprint: 512 numbers, of length one. Call from a background thread.</summary>
    public static float[] PictureFingerprint(BitmapSource image)
    {
        // the middle square, shrunk to 224 × 224
        int w = image.PixelWidth, h = image.PixelHeight, side = Math.Min(w, h);
        var square = new CroppedBitmap(image, new Int32Rect((w - side) / 2, (h - side) / 2, side, side));
        var px = Pixels(square, ClipSide, ClipSide);
        var input = new DenseTensor<float>(new[] { 1, 3, ClipSide, ClipSide });
        int plane = ClipSide * ClipSide;
        var data = input.Buffer.Span;
        for (int i = 0; i < plane; i++)
        {
            data[i] = (px[i * 4 + 2] / 255f - Mean[0]) / Spread[0];
            data[plane + i] = (px[i * 4 + 1] / 255f - Mean[1]) / Spread[1];
            data[2 * plane + i] = (px[i * 4] / 255f - Mean[2]) / Spread[2];
        }
        var session = Session("Overskin.clip-vision.onnx");
        using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor("pixel_values", input) });
        return Unit(results.First(r => r.Name == "image_embeds").AsEnumerable<float>().ToArray());
    }

    /// <summary>Some words' fingerprint, comparable with pictures'.</summary>
    public static float[] WordsFingerprint(string text)
    {
        var ids = ClipTokenizer.Encode(text);
        var input = new DenseTensor<long>(ids.Select(i => (long)i).ToArray(), new[] { 1, ids.Count });
        var session = Session("Overskin.clip-text.onnx");
        using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor("input_ids", input) });
        return Unit(results.First(r => r.Name == "text_embeds").AsEnumerable<float>().ToArray());
    }

    /// <summary>How alike two fingerprints are: about 0.3 for a good match, under 0.2 for none.</summary>
    public static float Likeness(float[] a, float[] b)
    {
        float s = 0;
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++) s += a[i] * b[i];
        return s;
    }

    private static float[] Unit(float[] v)
    {
        double n = Math.Sqrt(v.Sum(x => (double)x * x));
        if (n > 0) for (int i = 0; i < v.Length; i++) v[i] = (float)(v[i] / n);
        return v;
    }
}
