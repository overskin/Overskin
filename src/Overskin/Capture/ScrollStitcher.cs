using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Overskin;

/// <summary>
/// Joins screenshots of the same area taken while its content scrolls down into one tall
/// picture. Each new frame is matched against the previous one row by row: a fixed header
/// and footer (rows that never change) are found first, then the scroll distance is the
/// shift that lines up the most rows in between. Only the rows that are new get added.
/// </summary>
public sealed class ScrollStitcher
{
    public enum Step { Moved, Same, Lost }

    /// <summary>Stops growing past this height, so a never-ending page can't use up memory.</summary>
    public const int MaxHeight = 30000;

    private readonly int _w, _h, _stride;
    private readonly List<byte[]> _rows = new();
    private byte[] _previous;
    private ulong[] _previousHashes;
    private int _header = -1, _footer = -1;

    public int Height => _rows.Count + Math.Max(0, _footer);
    public int LastShift { get; private set; }
    public int ScrollArea => _h - Math.Max(0, _header) - Math.Max(0, _footer);
    public bool Full => _rows.Count >= MaxHeight;

    public ScrollStitcher(byte[] first, int width, int height)
    {
        _w = width;
        _h = height;
        _stride = width * 4;
        _previous = first;
        _previousHashes = Hashes(first);
    }

    public Step Add(byte[] frame)
    {
        var hashes = Hashes(frame);
        var a = _previousHashes;

        if (_header < 0)
        {
            int top = 0, bottom = 0;
            while (top < _h / 2 && a[top] == hashes[top]) top++;
            while (bottom < _h / 2 && a[_h - 1 - bottom] == hashes[_h - 1 - bottom]) bottom++;
            if (top + bottom >= _h - 8) return Step.Same; // nothing moved yet
            _header = top;
            _footer = bottom;
            // everything above the footer of the first frame
            for (int r = 0; r < _h - _footer; r++) _rows.Add(Row(_previous, r));
        }

        int start = _header, end = _h - _footer;
        if (Enumerable.Range(start, end - start).All(r => a[r] == hashes[r])) return Step.Same;

        int shift = FindShift(a, hashes, start, end);
        if (shift <= 0) return Step.Lost;

        for (int r = end - shift; r < end && !Full; r++) _rows.Add(Row(frame, r));
        LastShift = shift;
        _previous = frame;
        _previousHashes = hashes;
        return Step.Moved;
    }

    /// <summary>The shift d where new row r matches old row r + d for the most distinctive rows.</summary>
    private int FindShift(ulong[] a, ulong[] b, int start, int end)
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
                if (b[r] == Blank) continue;
                considered++;
                if (b[r] == a[r + d]) matched++;
            }
            if (considered < MinOverlap) continue;
            double score = matched / (double)considered;
            if (score > bestScore + 1e-9)
            {
                bestScore = score;
                best = d;
            }
        }
        return bestScore >= 0.8 ? best : 0;
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

    /// <summary>Marks a row of one colour: it matches anywhere, so it says nothing about the shift.</summary>
    private const ulong Blank = 0;

    private ulong[] Hashes(byte[] frame)
    {
        // leave out the edges, where a moving scrollbar would make every row look different
        int margin = _w > 240 ? 24 : 0;
        var hashes = new ulong[_h];
        for (int r = 0; r < _h; r++)
        {
            int o = r * _stride;
            uint first = BitConverter.ToUInt32(frame, o + margin * 4) & 0xFFFFFF;
            bool uniform = true;
            ulong hash = 1469598103934665603UL;
            for (int x = margin; x < _w - margin; x++)
            {
                uint px = BitConverter.ToUInt32(frame, o + x * 4) & 0xFFFFFF; // ignore alpha
                if (px != first) uniform = false;
                hash = (hash ^ px) * 1099511628211UL;
            }
            hashes[r] = uniform ? Blank : (hash == Blank ? 1 : hash);
        }
        return hashes;
    }
}
