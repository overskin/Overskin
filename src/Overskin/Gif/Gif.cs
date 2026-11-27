using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Overskin;

/// <summary>An animation as plain frames: BGRA bytes (alpha ignored) and how long each one shows.</summary>
public sealed class GifClip
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public List<byte[]> Frames { get; } = new();
    public List<int> DelaysMs { get; } = new();

    public int Count => Frames.Count;
    public int TotalMs => DelaysMs.Sum();

    public BitmapSource FrameBitmap(int index)
    {
        var bmp = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgr32, null, Frames[index], Width * 4);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>A still picture as a one-frame clip, so it can be captioned too.</summary>
    public static GifClip FromImage(BitmapSource image)
    {
        var bgra = ImageLoader.Normalize(new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0));
        var px = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
        bgra.CopyPixels(px, bgra.PixelWidth * 4, 0);
        var clip = new GifClip { Width = bgra.PixelWidth, Height = bgra.PixelHeight };
        clip.Frames.Add(px);
        clip.DelaysMs.Add(1000);
        return clip;
    }
}

/// <summary>
/// Writes animated GIFs: a 256-colour palette per frame (median cut), only the pixels that
/// changed since the previous frame (the rest are transparent), LZW compression, looping forever.
/// </summary>
public sealed class GifWriter : IDisposable
{
    private readonly Stream _out;
    private readonly int _w, _h;
    private int[]? _previous;       // what the viewer is showing after the last frame, as RGB
    private double _elapsedMs;      // exact time so far
    private int _writtenCs;         // time written so far, in GIF centiseconds

    /// <summary>Pixels with alpha under half are written see-through (a speech bubble); otherwise alpha is ignored.</summary>
    private readonly bool _seeThrough;

    /// <param name="repeats">How many times it plays: 0 for forever.</param>
    /// <param name="seeThrough">Keep see-through pixels see-through (screen grabs can have no alpha at all, so it's off unless asked for).</param>
    public GifWriter(Stream output, int width, int height, int repeats = 0, bool seeThrough = false)
    {
        _seeThrough = seeThrough;
        _out = output;
        _w = width;
        _h = height;

        Write("GIF89a");
        WriteShort(width);
        WriteShort(height);
        _out.WriteByte(0);  // no global colour table
        _out.WriteByte(0);  // background
        _out.WriteByte(0);  // aspect

        // Loop forever (0), or play a set number of times. With no loop block a GIF plays once;
        // with one, viewers play it once and then repeat it the count's number of times.
        if (repeats != 1)
        {
            int more = repeats <= 0 ? 0 : repeats - 1;
            _out.Write(new byte[] { 0x21, 0xFF, 0x0B });
            Write("NETSCAPE2.0");
            _out.Write(new byte[] { 0x03, 0x01, (byte)(more & 0xFF), (byte)(more >> 8), 0x00 });
        }
    }

    private void Write(string ascii) { foreach (char c in ascii) _out.WriteByte((byte)c); }
    private void WriteShort(int v) { _out.WriteByte((byte)(v & 0xFF)); _out.WriteByte((byte)(v >> 8)); }

    public void AddFrame(byte[] bgra, int delayMs)
    {
        int n = _w * _h;

        // ---- palette: median cut over a 15-bit colour histogram ----
        var hist = new int[32768];
        for (int i = 0, p = 0; i < n; i++, p += 4)
            if (!_seeThrough || bgra[p + 3] >= 128) hist[((bgra[p + 2] >> 3) << 10) | ((bgra[p + 1] >> 3) << 5) | (bgra[p] >> 3)]++;
        var (palette, binToIndex) = MedianCut(hist, 255); // index 255 is kept for "unchanged"

        // ---- map pixels, and find what changed ----
        var indices = new byte[n];
        var rgb = new int[n];
        int minX = _w, minY = _h, maxX = -1, maxY = -1;
        for (int y = 0, i = 0; y < _h; y++)
        {
            for (int x = 0; x < _w; x++, i++)
            {
                int p = i * 4;
                if (_seeThrough && bgra[p + 3] < 128)
                {
                    // see-through (a speech bubble): the transparent index, on every frame
                    indices[i] = 255;
                    rgb[i] = -1;
                }
                else
                {
                    int idx = binToIndex[((bgra[p + 2] >> 3) << 10) | ((bgra[p + 1] >> 3) << 5) | (bgra[p] >> 3)];
                    indices[i] = (byte)idx;
                    rgb[i] = palette[idx];
                }
                if (_previous == null || _previous[i] != rgb[i])
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }

        // timing: whole centiseconds, carrying the rounding forward so the clip keeps its length
        _elapsedMs += delayMs;
        int cs = Math.Max(2, (int)Math.Round(_elapsedMs / 10) - _writtenCs);
        _writtenCs += cs;

        bool first = _previous == null;
        if (maxX < 0) { minX = minY = 0; maxX = maxY = 0; } // nothing changed: a 1-pixel transparent frame
        int fw = maxX - minX + 1, fh = maxY - minY + 1;

        var sub = new byte[fw * fh];
        for (int y = 0; y < fh; y++)
        {
            for (int x = 0; x < fw; x++)
            {
                int i = (minY + y) * _w + minX + x;
                bool same = !first && _previous![i] == rgb[i];
                sub[y * fw + x] = same ? (byte)255 : indices[i];
            }
        }
        _previous = rgb;

        // graphic control: keep previous frame underneath, index 255 transparent
        // (the first frame uses it too, for anything see-through such as a speech bubble)
        _out.Write(new byte[] { 0x21, 0xF9, 0x04, (byte)((1 << 2) | 1) });
        WriteShort(cs);
        _out.WriteByte(255);
        _out.WriteByte(0);

        // image descriptor with a local 256-entry colour table
        _out.WriteByte(0x2C);
        WriteShort(minX); WriteShort(minY); WriteShort(fw); WriteShort(fh);
        _out.WriteByte(0x80 | 7);
        for (int k = 0; k < 256; k++)
        {
            int c = k < palette.Length ? palette[k] : 0;
            _out.WriteByte((byte)(c >> 16)); _out.WriteByte((byte)(c >> 8)); _out.WriteByte((byte)c);
        }

        Lzw.Encode(sub, 8, _out);
    }

    public void Dispose()
    {
        _out.WriteByte(0x3B); // trailer
        _out.Flush();
    }

    // ---- median cut ------------------------------------------------------------------------

    private sealed class Box
    {
        public int R0, R1, G0, G1, B0, B1; // inclusive 5-bit ranges
        public long Count;
    }

    /// <summary>Returns RGB colours (0xRRGGBB) and, for every 15-bit bin, the colour it maps to.</summary>
    private static (int[] Palette, int[] BinToIndex) MedianCut(int[] hist, int maxColours)
    {
        var boxes = new List<Box> { Shrink(new Box { R0 = 0, R1 = 31, G0 = 0, G1 = 31, B0 = 0, B1 = 31 }, hist) };
        while (boxes.Count < maxColours)
        {
            // split the box with the most pixels spread over the widest range
            Box? pick = null;
            long best = 0;
            foreach (var b in boxes)
            {
                int span = Math.Max(b.R1 - b.R0, Math.Max(b.G1 - b.G0, b.B1 - b.B0));
                if (span == 0) continue;
                long score = b.Count * span;
                if (score > best) { best = score; pick = b; }
            }
            if (pick == null) break;
            boxes.Remove(pick);
            var (a, c) = Split(pick, hist);
            boxes.Add(a);
            boxes.Add(c);
        }

        var palette = new int[boxes.Count];
        var map = new int[32768];
        for (int k = 0; k < boxes.Count; k++)
        {
            var b = boxes[k];
            long sr = 0, sg = 0, sb = 0, total = 0;
            for (int r = b.R0; r <= b.R1; r++)
                for (int g = b.G0; g <= b.G1; g++)
                    for (int bl = b.B0; bl <= b.B1; bl++)
                    {
                        int bin = (r << 10) | (g << 5) | bl;
                        map[bin] = k;
                        long cnt = hist[bin];
                        sr += cnt * r; sg += cnt * g; sb += cnt * bl; total += cnt;
                    }
            if (total == 0) total = 1;
            int R = (int)(sr * 255 / (31 * total)), G = (int)(sg * 255 / (31 * total)), B = (int)(sb * 255 / (31 * total));
            palette[k] = (R << 16) | (G << 8) | B;
        }
        return (palette, map);
    }

    private static Box Shrink(Box b, int[] hist)
    {
        int r0 = 31, r1 = 0, g0 = 31, g1 = 0, b0 = 31, b1 = 0;
        long count = 0;
        for (int r = b.R0; r <= b.R1; r++)
            for (int g = b.G0; g <= b.G1; g++)
                for (int bl = b.B0; bl <= b.B1; bl++)
                {
                    int c = hist[(r << 10) | (g << 5) | bl];
                    if (c == 0) continue;
                    count += c;
                    if (r < r0) r0 = r; if (r > r1) r1 = r;
                    if (g < g0) g0 = g; if (g > g1) g1 = g;
                    if (bl < b0) b0 = bl; if (bl > b1) b1 = bl;
                }
        if (count == 0) return b;
        return new Box { R0 = r0, R1 = r1, G0 = g0, G1 = g1, B0 = b0, B1 = b1, Count = count };
    }

    private static (Box, Box) Split(Box b, int[] hist)
    {
        int dr = b.R1 - b.R0, dg = b.G1 - b.G0, db = b.B1 - b.B0;
        int axis = dr >= dg && dr >= db ? 0 : dg >= db ? 1 : 2;
        int lo = axis == 0 ? b.R0 : axis == 1 ? b.G0 : b.B0;
        int hi = axis == 0 ? b.R1 : axis == 1 ? b.G1 : b.B1;

        // counts per slice along the axis, then cut at the median
        var slice = new long[32];
        for (int r = b.R0; r <= b.R1; r++)
            for (int g = b.G0; g <= b.G1; g++)
                for (int bl = b.B0; bl <= b.B1; bl++)
                {
                    int v = axis == 0 ? r : axis == 1 ? g : bl;
                    slice[v] += hist[(r << 10) | (g << 5) | bl];
                }
        long half = b.Count / 2, run = 0;
        int cut = lo;
        for (int v = lo; v < hi; v++)
        {
            run += slice[v];
            cut = v;
            if (run >= half) break;
        }

        var a = new Box { R0 = b.R0, R1 = b.R1, G0 = b.G0, G1 = b.G1, B0 = b.B0, B1 = b.B1 };
        var c = new Box { R0 = b.R0, R1 = b.R1, G0 = b.G0, G1 = b.G1, B0 = b.B0, B1 = b.B1 };
        if (axis == 0) { a.R1 = cut; c.R0 = cut + 1; }
        else if (axis == 1) { a.G1 = cut; c.G0 = cut + 1; }
        else { a.B1 = cut; c.B0 = cut + 1; }
        return (Shrink(a, hist), Shrink(c, hist));
    }
}

/// <summary>GIF's variable-length LZW, with the output split into 255-byte sub-blocks.</summary>
internal static class Lzw
{
    [ThreadStatic] private static int[]? _table;

    public static void Encode(byte[] pixels, int minCodeSize, Stream output)
    {
        output.WriteByte((byte)minCodeSize);
        int clear = 1 << minCodeSize, end = clear + 1;
        int codeSize = minCodeSize + 1, next = end + 1;
        var Table = _table ??= new int[4096 * 256];
        Array.Fill(Table, -1);

        var block = new byte[255];
        int blockLen = 0;
        int bitBuffer = 0, bitCount = 0;

        void Emit(int code)
        {
            bitBuffer |= code << bitCount;
            bitCount += codeSize;
            while (bitCount >= 8)
            {
                block[blockLen++] = (byte)bitBuffer;
                bitBuffer >>= 8;
                bitCount -= 8;
                if (blockLen == 255) { output.WriteByte(255); output.Write(block, 0, 255); blockLen = 0; }
            }
        }

        Emit(clear);
        if (pixels.Length > 0)
        {
            int prefix = pixels[0];
            for (int i = 1; i < pixels.Length; i++)
            {
                int k = pixels[i];
                int key = (prefix << 8) | k;
                int found = Table[key];
                if (found >= 0)
                {
                    prefix = found;
                    continue;
                }
                Emit(prefix);
                if (next < 4096)
                {
                    Table[key] = next++;
                    if (next > (1 << codeSize) && codeSize < 12) codeSize++;
                }
                else
                {
                    // table full: start over
                    Emit(clear);
                    Array.Fill(Table, -1);
                    codeSize = minCodeSize + 1;
                    next = end + 1;
                }
                prefix = k;
            }
            Emit(prefix);
            // the decoder widens its codes one step after we add an entry: match it before the end code
            if (next >= (1 << codeSize) && codeSize < 12) codeSize++;
        }
        Emit(end);
        if (bitCount > 0) { block[blockLen++] = (byte)bitBuffer; if (blockLen == 255) { output.WriteByte(255); output.Write(block, 0, 255); blockLen = 0; } }
        if (blockLen > 0) { output.WriteByte((byte)blockLen); output.Write(block, 0, blockLen); }
        output.WriteByte(0);
    }
}

/// <summary>Reads any GIF into whole frames, honouring frame offsets, delays and disposal.</summary>
public static class GifReader
{
    public static GifClip Read(string path, int maxFrames = 600)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

        int w = decoder.Frames[0].PixelWidth, h = decoder.Frames[0].PixelHeight;
        try
        {
            if (decoder.Metadata is BitmapMetadata meta)
            {
                w = Convert.ToInt32(meta.GetQuery("/logscrdesc/Width"));
                h = Convert.ToInt32(meta.GetQuery("/logscrdesc/Height"));
            }
        }
        catch { }

        var clip = new GifClip { Width = w, Height = h };
        var canvas = new byte[w * h * 4];
        Array.Fill(canvas, (byte)255); // white paper under transparent areas

        foreach (var frame in decoder.Frames.Take(maxFrames))
        {
            int left = 0, top = 0, delay = 10, disposal = 0;
            if (frame.Metadata is BitmapMetadata fm)
            {
                left = Query(fm, "/imgdesc/Left", 0);
                top = Query(fm, "/imgdesc/Top", 0);
                delay = Query(fm, "/grctlext/Delay", 10);
                disposal = Query(fm, "/grctlext/Disposal", 0);
            }

            var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            int fw = bgra.PixelWidth, fh = bgra.PixelHeight;
            var px = new byte[fw * fh * 4];
            bgra.CopyPixels(px, fw * 4, 0);

            byte[]? before = disposal == 3 ? (byte[])canvas.Clone() : null;
            for (int y = 0; y < fh; y++)
            {
                int cy = top + y;
                if (cy < 0 || cy >= h) continue;
                for (int x = 0; x < fw; x++)
                {
                    int cx = left + x;
                    if (cx < 0 || cx >= w) continue;
                    int s = (y * fw + x) * 4;
                    if (px[s + 3] < 128) continue; // transparent: whatever was there stays
                    int d = (cy * w + cx) * 4;
                    canvas[d] = px[s]; canvas[d + 1] = px[s + 1]; canvas[d + 2] = px[s + 2]; canvas[d + 3] = 255;
                }
            }

            clip.Frames.Add((byte[])canvas.Clone());
            clip.DelaysMs.Add(Math.Max(20, (delay <= 1 ? 10 : delay) * 10));

            if (disposal == 2)
            {
                for (int y = Math.Max(0, top); y < Math.Min(h, top + fh); y++)
                    for (int x = Math.Max(0, left); x < Math.Min(w, left + fw); x++)
                    {
                        int d = (y * w + x) * 4;
                        canvas[d] = canvas[d + 1] = canvas[d + 2] = canvas[d + 3] = 255;
                    }
            }
            else if (disposal == 3 && before != null)
            {
                canvas = before;
            }
        }
        return clip;
    }

    private static int Query(BitmapMetadata m, string q, int fallback)
    {
        try { return m.GetQuery(q) is { } v ? Convert.ToInt32(v) : fallback; }
        catch { return fallback; }
    }
}
