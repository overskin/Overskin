using Avalonia;
using Avalonia.Media.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Overskin.Linux.Core;

/// <summary>
/// The AI models inside Overskin, the same ones the Windows version uses, run on this computer
/// with ONNX Runtime (nothing is sent anywhere): IS-Net finds the subject of a picture to cut out,
/// and CLIP turns pictures and words into fingerprints, so the Library finds pictures by what's in
/// them. Pictures are handled as plain BGRA bytes here, so all of it can run in the background.
/// </summary>
public static class Ai
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, InferenceSession> Sessions = new();

    /// <summary>Whether this build has the models in it, and the AI engine can run here.</summary>
    public static bool Available => typeof(Ai).Assembly.GetManifestResourceInfo("Overskin.cutout-model.onnx") != null && EngineLoads;

    private static bool? _engine;

    /// <summary>Whether ONNX Runtime's native library is here and loads (checked once).</summary>
    private static bool EngineLoads
    {
        get
        {
            if (_engine is { } known) return known;
            try
            {
                _engine = System.Runtime.InteropServices.NativeLibrary.TryLoad("onnxruntime", typeof(InferenceSession).Assembly, null, out _);
            }
            catch { _engine = false; }
            if (_engine == false) Log.Write("ai: the AI engine (onnxruntime) isn't here or won't load");
            return _engine.Value;
        }
    }

    private static InferenceSession Session(string name)
    {
        lock (Gate)
        {
            if (Sessions.TryGetValue(name, out var s)) return s;
            using var stream = typeof(Ai).Assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException("This copy of Overskin was built without its AI models.");
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount - 1, 1, 8),
            };
            s = new InferenceSession(bytes, options);
            Sessions[name] = s;
            return s;
        }
    }

    // ---- pictures as bytes ---------------------------------------------------------------------

    /// <summary>A picture's pixels, premultiplied BGRA, and its size.</summary>
    public readonly record struct Pixels(byte[] Data, int Width, int Height);

    /// <summary>Reads a picture's pixels (on any thread that can read the bitmap).</summary>
    public static Pixels Of(Bitmap image) => new(Imaging.Pixels(image, out _), image.PixelSize.Width, image.PixelSize.Height);

    /// <summary>
    /// Part of a picture (<paramref name="sx"/>, <paramref name="sy"/>, <paramref name="sw"/> × <paramref name="sh"/>)
    /// scaled to <paramref name="dw"/> × <paramref name="dh"/>: each new pixel is the average of the
    /// old ones it covers when shrinking, smoothly blended when growing. Pure arithmetic, so it's safe anywhere.
    /// </summary>
    public static byte[] Resample(Pixels src, int sx, int sy, int sw, int sh, int dw, int dh)
    {
        var dst = new byte[dw * dh * 4];
        double kx = sw / (double)dw, ky = sh / (double)dh;
        Parallel.For(0, dh, y =>
        {
            for (int x = 0; x < dw; x++)
            {
                double x0 = sx + x * kx, x1 = x0 + kx, y0 = sy + y * ky, y1 = y0 + ky;
                double b = 0, g = 0, r = 0, a = 0, weight = 0;
                if (kx >= 1 && ky >= 1)
                {
                    // shrinking: the average of the source pixels this one covers
                    for (int yy = (int)y0; yy < Math.Min(src.Height, (int)Math.Ceiling(y1)); yy++)
                    {
                        double wy = Math.Min(yy + 1, y1) - Math.Max(yy, y0);
                        for (int xx = (int)x0; xx < Math.Min(src.Width, (int)Math.Ceiling(x1)); xx++)
                        {
                            double wgt = wy * (Math.Min(xx + 1, x1) - Math.Max(xx, x0));
                            int o = (yy * src.Width + xx) * 4;
                            b += src.Data[o] * wgt; g += src.Data[o + 1] * wgt; r += src.Data[o + 2] * wgt; a += src.Data[o + 3] * wgt;
                            weight += wgt;
                        }
                    }
                }
                else
                {
                    // growing: blend the four nearest
                    double fx = Math.Clamp(x0 + kx / 2 - 0.5, 0, src.Width - 1), fy = Math.Clamp(y0 + ky / 2 - 0.5, 0, src.Height - 1);
                    int ix = (int)fx, iy = (int)fy, ix2 = Math.Min(src.Width - 1, ix + 1), iy2 = Math.Min(src.Height - 1, iy + 1);
                    double tx = fx - ix, ty = fy - iy;
                    foreach (var (px, py, wgt) in new[] { (ix, iy, (1 - tx) * (1 - ty)), (ix2, iy, tx * (1 - ty)), (ix, iy2, (1 - tx) * ty), (ix2, iy2, tx * ty) })
                    {
                        int o = (py * src.Width + px) * 4;
                        b += src.Data[o] * wgt; g += src.Data[o + 1] * wgt; r += src.Data[o + 2] * wgt; a += src.Data[o + 3] * wgt;
                        weight += wgt;
                    }
                }
                int d = (y * dw + x) * 4;
                if (weight <= 0) continue;
                dst[d] = (byte)Math.Round(b / weight); dst[d + 1] = (byte)Math.Round(g / weight);
                dst[d + 2] = (byte)Math.Round(r / weight); dst[d + 3] = (byte)Math.Round(a / weight);
            }
        });
        return dst;
    }

    /// <summary>A colour's value with any see-through part shown against white, as the models expect.</summary>
    private static float OnWhite(byte premultiplied, byte alpha) => (premultiplied + (255 - alpha)) / 255f;

    // ---- cutting out the subject ---------------------------------------------------------------

    private const int CutoutSide = 1024;

    /// <summary>How much each pixel belongs to the subject, 0 to 255, at the picture's own size.</summary>
    public static byte[] SubjectMask(Pixels picture)
    {
        int w = picture.Width, h = picture.Height;
        var px = Resample(picture, 0, 0, w, h, CutoutSide, CutoutSide);
        // IS-Net reads the picture at 1024 × 1024, each colour as (value / 255 − 0.5)
        var input = new DenseTensor<float>(new[] { 1, 3, CutoutSide, CutoutSide });
        int plane = CutoutSide * CutoutSide;
        var data = input.Buffer.Span;
        for (int i = 0; i < plane; i++)
        {
            byte a = px[i * 4 + 3];
            data[i] = OnWhite(px[i * 4 + 2], a) - 0.5f;
            data[plane + i] = OnWhite(px[i * 4 + 1], a) - 0.5f;
            data[2 * plane + i] = OnWhite(px[i * 4], a) - 0.5f;
        }
        float[] map;
        var session = Session("Overskin.cutout-model.onnx");
        using (var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(session.InputMetadata.Keys.First(), input) }))
            map = results.First(r => r.Name == session.OutputMetadata.Keys.First()).AsEnumerable<float>().ToArray();
        // stretched to 0..1, the way IS-Net's makers do, then back to the picture's own size, smoothly
        float lo = map.Min(), hi = map.Max(), span = Math.Max(1e-6f, hi - lo);
        var mask = new byte[w * h];
        double kx = CutoutSide / (double)w, ky = CutoutSide / (double)h;
        Parallel.For(0, h, y =>
        {
            double fy = Math.Clamp((y + 0.5) * ky - 0.5, 0, CutoutSide - 1);
            int iy = (int)fy, iy2 = Math.Min(CutoutSide - 1, iy + 1);
            double ty = fy - iy;
            for (int x = 0; x < w; x++)
            {
                double fx = Math.Clamp((x + 0.5) * kx - 0.5, 0, CutoutSide - 1);
                int ix = (int)fx, ix2 = Math.Min(CutoutSide - 1, ix + 1);
                double tx = fx - ix;
                double v = map[iy * CutoutSide + ix] * (1 - tx) * (1 - ty) + map[iy * CutoutSide + ix2] * tx * (1 - ty)
                         + map[iy2 * CutoutSide + ix] * (1 - tx) * ty + map[iy2 * CutoutSide + ix2] * tx * ty;
                mask[y * w + x] = (byte)Math.Clamp((v - lo) / span * 255, 0, 255);
            }
        });
        return mask;
    }

    /// <summary>
    /// The subject on a see-through background, trimmed to the subject (with a little room), with
    /// clean edges. Null when nothing stands out. Pixels come back premultiplied BGRA.
    /// </summary>
    public static Pixels? CutOut(Pixels picture)
    {
        int w = picture.Width, h = picture.Height;
        var mask = SubjectMask(picture);
        // straight (not premultiplied) colours while the edges are worked on
        var px = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            int o = i * 4;
            byte a0 = picture.Data[o + 3];
            if (a0 > 0)
            {
                px[o] = (byte)Math.Min(255, picture.Data[o] * 255 / a0);
                px[o + 1] = (byte)Math.Min(255, picture.Data[o + 1] * 255 / a0);
                px[o + 2] = (byte)Math.Min(255, picture.Data[o + 2] * 255 / a0);
            }
            int m = mask[i] < 12 ? 0 : mask[i]; // soft edges stay soft, faint noise in the background goes
            px[o + 3] = (byte)(a0 * m / 255);
        }
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (px[(y * w + x) * 4 + 3] > 60)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
        if (maxX < 0 || (maxX - minX) * (maxY - minY) < 64) return null;
        Defringe(px, w, h);
        int pad = Math.Max(4, Math.Min(w, h) / 100);
        minX = Math.Max(0, minX - pad); minY = Math.Max(0, minY - pad);
        maxX = Math.Min(w - 1, maxX + pad); maxY = Math.Min(h - 1, maxY + pad);
        int cw = maxX - minX + 1, ch = maxY - minY + 1;
        var cut = new byte[cw * ch * 4];
        for (int y = 0; y < ch; y++)
            for (int x = 0; x < cw; x++)
            {
                int s = ((minY + y) * w + minX + x) * 4, d = (y * cw + x) * 4;
                int a = px[s + 3];
                // back to premultiplied, as Avalonia keeps pictures
                cut[d] = (byte)(px[s] * a / 255); cut[d + 1] = (byte)(px[s + 1] * a / 255); cut[d + 2] = (byte)(px[s + 2] * a / 255); cut[d + 3] = (byte)a;
            }
        return new Pixels(cut, cw, ch);
    }

    /// <summary>
    /// The soft edge of a cut-out still holds some of the old background's colour, which shows as a
    /// halo on anything else: each part-see-through pixel takes the colour of the solid subject next
    /// to it (straight BGRA in, straight out), and fully clear pixels are emptied. As on Windows.
    /// </summary>
    public static void Defringe(byte[] px, int w, int h)
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
                        double k = 1.0 / (1 + dx * dx + dy * dy);
                        sb += colour[q] * k; sg += colour[q + 1] * k; sr += colour[q + 2] * k; n += k;
                    }
                }
                if (n <= 0) continue;
                px[o] = (byte)(sb / n); px[o + 1] = (byte)(sg / n); px[o + 2] = (byte)(sr / n);
            }
    }

    // ---- CLIP: pictures and words as comparable fingerprints -----------------------------------

    private const int ClipSide = 224;
    private static readonly float[] Mean = { 0.48145466f, 0.4578275f, 0.40821073f };
    private static readonly float[] Spread = { 0.26862954f, 0.26130258f, 0.27577711f };

    /// <summary>A picture's fingerprint: 512 numbers, of length one.</summary>
    public static float[] PictureFingerprint(Pixels picture)
    {
        // the middle square, shrunk to 224 × 224
        int side = Math.Min(picture.Width, picture.Height);
        var px = Resample(picture, (picture.Width - side) / 2, (picture.Height - side) / 2, side, side, ClipSide, ClipSide);
        var input = new DenseTensor<float>(new[] { 1, 3, ClipSide, ClipSide });
        int plane = ClipSide * ClipSide;
        var data = input.Buffer.Span;
        for (int i = 0; i < plane; i++)
        {
            byte a = px[i * 4 + 3];
            data[i] = (OnWhite(px[i * 4 + 2], a) - Mean[0]) / Spread[0];
            data[plane + i] = (OnWhite(px[i * 4 + 1], a) - Mean[1]) / Spread[1];
            data[2 * plane + i] = (OnWhite(px[i * 4], a) - Mean[2]) / Spread[2];
        }
        var session = Session("Overskin.clip-vision.onnx");
        using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor("pixel_values", input) });
        return Unit(results.First(r => r.Name == "image_embeds").AsEnumerable<float>().ToArray());
    }

    /// <summary>Some words' fingerprint, comparable with pictures'.</summary>
    public static float[] WordsFingerprint(string text)
    {
        var ids = Overskin.ClipTokenizer.Encode(text);
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

    /// <summary>The search's fingerprint: what the words mean, as a picture or a screenshot (as on Windows).</summary>
    public static float[] QueryPrint(string query)
    {
        var a = WordsFingerprint($"a picture of {query}");
        var b = WordsFingerprint($"a screenshot of {query}");
        var mean = new float[a.Length];
        for (int i = 0; i < a.Length; i++) mean[i] = a[i] + b[i];
        return Unit(mean);
    }

    private static float[] Unit(float[] v)
    {
        double n = Math.Sqrt(v.Sum(x => (double)x * x));
        if (n > 0) for (int i = 0; i < v.Length; i++) v[i] = (float)(v[i] / n);
        return v;
    }
}
