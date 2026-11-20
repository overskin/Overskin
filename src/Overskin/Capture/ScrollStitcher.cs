using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Overskin;

/// <summary>
/// Joins screenshots of the same area taken while its content scrolls down into one tall
/// picture. Each row is boiled down to a short strip of brightness values, and the scroll
/// distance is the shift that lines up the most rows between one frame and the next, closely
/// rather than exactly: a blinking cursor, a spinner, an animated picture or a header that
/// gains a shadow only spoil the rows they're in, and the rest still line up. A fixed header
/// and footer (rows that stay put) are found first; only the rows that are new get added.
/// </summary>
public sealed class ScrollStitcher
{
    public enum Step { Moved, Same, Lost }

    /// <summary>Stops growing past this height, so a never-ending page can't use up memory.</summary>
    public const int MaxHeight = 30000;

    private const int Bins = 48;        // brightness values per row for the coarse search
    private const int FineBins = 192;   // and for the final, exact pick
    private const float BinTolerance = 4.0f;  // brightness difference two pieces of a row may have and still match
    private const double Agreement = 0.55;    // share of rows that must line up

    private readonly int _w, _h, _stride;
    private readonly List<byte[]> _rows = new();
    private byte[] _previous;
    private float[][] _previousSig, _previousFine;
    private bool[] _previousBlank;
    private int _header = -1, _footer = -1;

    public int Height => _rows.Count + Math.Max(0, _footer);
    public int LastShift { get; private set; }
    public int ScrollArea => _h - Math.Max(0, _header) - Math.Max(0, _footer);
    public bool Full => _rows.Count >= MaxHeight;
    /// <summary>For the self-test: how well the last search lined up, and at which shift.</summary>
    internal double LastScore { get; private set; }
    internal int LastBest { get; private set; }
    internal string LastNote { get; private set; } = "";

    public ScrollStitcher(byte[] first, int width, int height)
    {
        _w = width;
        _h = height;
        _stride = width * 4;
        _previous = first;
        (_previousSig, _previousBlank) = Signature(first, Bins);
        _previousFine = Signature(first, FineBins).Rows;
    }

    public Step Add(byte[] frame)
    {
        var (sig, blank) = Signature(frame, Bins);
        var a = _previousSig;

        // rows that look the same as before, in place
        var still = new bool[_h];
        for (int r = 0; r < _h; r++) still[r] = Alike(a[r], sig[r]);

        int start = Math.Max(0, _header), end = _h - Math.Max(0, _footer);
        int moving = 0, counted = 0;
        for (int r = start; r < end; r++)
        {
            if (blank[r] && _previousBlank[r]) continue;
            counted++;
            if (!still[r]) moving++;
        }
        // nothing (or next to nothing: an animation, a caret) moved: the end of the page
        if (counted == 0 || moving < Math.Max(3, counted * 0.12)) return Step.Same;

        if (_header < 0)
        {
            // the header and footer are the rows at the top and bottom that stayed put
            int top = 0, bottom = 0;
            while (top < _h / 3 && still[top]) top++;
            while (bottom < _h / 3 && still[_h - 1 - bottom]) bottom++;
            _header = top;
            _footer = bottom;
            start = _header;
            end = _h - _footer;
            for (int r = 0; r < end; r++) _rows.Add(Row(_previous, r));
        }

        LastNote = $"header={_header} footer={_footer} counted={counted} moving={moving}";
        int shift = FindShift(a, sig, blank, start, end);
        // no shift fits, but most of it stayed put: the page has ended and something on it
        // (a video, an animation) is still moving
        if (shift <= 0) return counted - moving >= counted * 0.5 ? Step.Same : Step.Lost;
        // the coarse strips can be a row out on soft text; settle it with finer ones
        var fine = Signature(frame, FineBins).Rows;
        shift = Refine(_previousFine, fine, blank, start, end, shift);

        for (int r = end - shift; r < end && !Full; r++) _rows.Add(Row(frame, r));
        LastShift = shift;
        _previous = frame;
        _previousSig = sig;
        _previousFine = fine;
        _previousBlank = blank;
        return Step.Moved;
    }

    /// <summary>The shift d where new row r matches old row r + d for the most rows with something in them.</summary>
    private int FindShift(float[][] a, float[][] b, bool[] blank, int start, int end)
    {
        int area = end - start;
        const int MinOverlap = 12;
        int best = 0;
        double bestScore = 0;
        for (int d = 1; d <= area - MinOverlap; d++)
        {
            int considered = 0, matched = 0;
            for (int r = start; r + d < end; r++)
            {
                if (blank[r]) continue;
                considered++;
                if (Alike(b[r], a[r + d])) matched++;
            }
            if (considered < MinOverlap) continue;
            double score = matched / (double)considered;
            if (score > bestScore + 1e-9)
            {
                bestScore = score;
                best = d;
            }
        }
        LastScore = bestScore;
        LastBest = best;
        return bestScore >= Agreement ? best : 0;
    }

    /// <summary>Of the shifts next to <paramref name="d"/>, the one where the most of the rows' pieces line up.</summary>
    private static int Refine(float[][] a, float[][] b, bool[] blank, int start, int end, int d)
    {
        int best = d;
        double bestScore = -1;
        for (int c = Math.Max(1, d - 2); c <= d + 2; c++)
        {
            long same = 0, total = 0;
            for (int r = start; r + c < end; r++)
            {
                if (blank[r]) continue;
                var x = b[r];
                var y = a[r + c];
                for (int k = 0; k < x.Length; k++)
                {
                    total++;
                    if (Math.Abs(x[k] - y[k]) < BinTolerance) same++;
                }
            }
            if (total == 0) continue;
            double score = same / (double)total;
            if (score > bestScore) { bestScore = score; best = c; }
        }
        return best;
    }

    /// <summary>
    /// Two rows are alike when most of their pieces are: an animation, a caret or a floating
    /// button over part of a row doesn't stop the rest of it from lining up.
    /// </summary>
    private static bool Alike(float[] a, float[] b)
    {
        int same = 0;
        for (int k = 0; k < a.Length; k++) if (Math.Abs(a[k] - b[k]) < BinTolerance) same++;
        return same >= a.Length * 0.7;
    }

    /// <summary>The stitched picture: header, every new row in order, and the last footer.</summary>
    public BitmapSource Result()
    {
        int footer = Math.Max(0, _footer);
        var rows = _rows.Count > 0 ? _rows : Enumerable.Range(0, _h).Select(r => Row(_previous, r)).ToList();
        int height = rows.Count + (_rows.Count > 0 ? footer : 0);
        var pixels = new byte[height * _stride];
        for (int r = 0; r < rows.Count; r++) Buffer.BlockCopy(rows[r], 0, pixels, r * _stride, _stride);
        if (_rows.Count > 0)
            for (int r = 0; r < footer; r++)
                Buffer.BlockCopy(_previous, (_h - footer + r) * _stride, pixels, (rows.Count + r) * _stride, _stride);
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var bmp = BitmapSource.Create(_w, height, 96, 96, PixelFormats.Bgra32, null, pixels, _stride);
        bmp.Freeze();
        return bmp;
    }

    private byte[] Row(byte[] frame, int r)
    {
        var row = new byte[_stride];
        Buffer.BlockCopy(frame, r * _stride, row, 0, _stride);
        return row;
    }

    /// <summary>
    /// Each row as <paramref name="bins"/> average brightnesses, left to right, leaving out the
    /// edges (where a scrollbar moves). Rows of one even colour are marked blank: they match
    /// anywhere, so they say nothing about the shift.
    /// </summary>
    private (float[][] Rows, bool[] Blank) Signature(byte[] frame, int bins)
    {
        int margin = _w > 240 ? 24 : 0;
        int span = _w - 2 * margin;
        bins = Math.Max(1, Math.Min(bins, span));
        var rows = new float[_h][];
        var blank = new bool[_h];
        for (int r = 0; r < _h; r++)
        {
            int o = r * _stride;
            var sig = new float[bins];
            var counts = new int[bins];
            int lo = 255, hi = 0;
            for (int x = margin; x < _w - margin; x++)
            {
                int i = o + x * 4;
                int luma = (frame[i] * 29 + frame[i + 1] * 150 + frame[i + 2] * 77) >> 8;
                int bin = (x - margin) * bins / span;
                sig[bin] += luma;
                counts[bin]++;
                if (luma < lo) lo = luma;
                if (luma > hi) hi = luma;
            }
            for (int k = 0; k < bins; k++) if (counts[k] > 0) sig[k] /= counts[k];
            rows[r] = sig;
            blank[r] = hi - lo <= 2;
        }
        return (rows, blank);
    }
}
