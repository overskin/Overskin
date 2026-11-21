using System.Runtime.InteropServices;
using System.Windows;
using static Overskin.NativeMethods;

namespace Overskin;

/// <summary>
/// The pointer as it moved, recorded on its own thread several times faster than the video, kept
/// apart from the picture. The recording draws its own pointer from this afterwards (a fraction of a
/// second later), so it can look both ways in time: smoothing then needs no lag, and the pointer
/// can be blurred along the path it really took.
/// </summary>
internal sealed class CursorTrack : IDisposable
{
    public readonly record struct Sample(double Time, double X, double Y, bool Down, IntPtr Handle, bool Showing);

    private readonly List<Sample> _samples = new();
    private readonly List<double> _clicks = new();
    private readonly object _gate = new();
    private readonly Func<double> _now;
    private readonly Int32Rect _area;
    private readonly Thread _thread;
    private volatile bool _stop;

    /// <summary>Samples per second.</summary>
    public const int Rate = 250;

    public CursorTrack(Int32Rect area, Func<double> now, bool start = true)
    {
        _area = area;
        _now = now;
        _thread = new Thread(Run) { IsBackground = true, Name = "Overskin pointer track", Priority = ThreadPriority.AboveNormal };
        if (start) _thread.Start();
    }

    private void Run()
    {
        while (!_stop)
        {
            GetCursorPos(out var p);
            bool drawing = ScreenPen.Drawing; // drawing on the screen: no pointer, and the pen's clicks aren't clicks
            bool down = !drawing && (GetAsyncKeyState(0x01) & 0x8000) != 0;
            var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
            bool showing = !drawing && GetCursorInfo(ref info) && (info.flags & CURSOR_SHOWING) != 0 && info.hCursor != IntPtr.Zero;
            Add(new Sample(_now(), p.X - _area.X, p.Y - _area.Y, down, showing ? info.hCursor : IntPtr.Zero, showing));
            Thread.Sleep(1000 / Rate);
        }
    }

    /// <summary>Adds a sample (the tracking thread does this; tests call it directly).</summary>
    public void Add(Sample s)
    {
        lock (_gate)
        {
            if (_samples.Count > 0 && s.Time <= _samples[^1].Time) return;
            if (s.Down && (_samples.Count == 0 || !_samples[^1].Down)) _clicks.Add(s.Time);
            _samples.Add(s);
        }
    }

    /// <summary>The time of the newest sample.</summary>
    public double Latest
    {
        get { lock (_gate) return _samples.Count == 0 ? double.NegativeInfinity : _samples[^1].Time; }
    }

    /// <summary>Clicks (the moment the left button went down) between two times, with where they were.</summary>
    public List<(double Time, Point At)> ClicksBetween(double from, double to)
    {
        lock (_gate)
        {
            var found = new List<(double, Point)>();
            foreach (double t in _clicks)
                if (t >= from && t <= to) found.Add((t, RawAt(t)));
            return found;
        }
    }

    /// <summary>The newest sample at or before <paramref name="t"/>.</summary>
    public Sample? SampleAt(double t)
    {
        lock (_gate)
        {
            if (_samples.Count == 0) return null;
            int i = Index(t);
            return _samples[Math.Max(0, i)];
        }
    }

    /// <summary>Where the real pointer was at <paramref name="t"/> (straight between samples).</summary>
    public Point RawAt(double t)
    {
        lock (_gate)
        {
            if (_samples.Count == 0) return new Point();
            int i = Index(t);
            if (i < 0) return new Point(_samples[0].X, _samples[0].Y);
            if (i >= _samples.Count - 1) return new Point(_samples[^1].X, _samples[^1].Y);
            var a = _samples[i];
            var b = _samples[i + 1];
            double k = (t - a.Time) / Math.Max(1e-9, b.Time - a.Time);
            return new Point(a.X + (b.X - a.X) * k, a.Y + (b.Y - a.Y) * k);
        }
    }

    /// <summary>
    /// The smoothed pointer at <paramref name="t"/>: a bell-shaped (Gaussian) average of where it
    /// was <paramref name="sigma"/> seconds either side, looking no further ahead than
    /// <paramref name="ahead"/>. Every move then speeds up and slows down gently (ease in, ease
    /// out) and shakes are gone, with no lag. Near a click the window narrows, so the pointer is
    /// exactly where the click happened.
    /// </summary>
    public Point SmoothAt(double t, double sigma, double ahead)
    {
        if (sigma <= 1e-4) return RawAt(t);
        lock (_gate)
        {
            if (_samples.Count == 0) return new Point();
            // narrow the window close to a click (on either side of it)
            double nearest = double.MaxValue;
            foreach (double c in _clicks)
            {
                double d = Math.Abs(c - t);
                if (d < nearest) nearest = d;
            }
            double s = sigma * Math.Clamp(nearest / (2.5 * sigma), 0.03, 1);
            double reach = 3 * s;
            double from = t - reach, to = t + Math.Min(reach, ahead);
            int i = Math.Max(0, Index(from));
            double sx = 0, sy = 0, sw = 0;
            for (; i < _samples.Count && _samples[i].Time <= to; i++)
            {
                var p = _samples[i];
                double u = (p.Time - t) / s;
                double w = Math.Exp(-0.5 * u * u);
                sx += p.X * w;
                sy += p.Y * w;
                sw += w;
            }
            if (sw <= 1e-9) return RawAtUnlocked(t);
            return new Point(sx / sw, sy / sw);
        }
    }

    private Point RawAtUnlocked(double t)
    {
        int i = Math.Clamp(Index(t), 0, _samples.Count - 1);
        return new Point(_samples[i].X, _samples[i].Y);
    }

    /// <summary>Index of the newest sample at or before <paramref name="t"/> (-1 if none).</summary>
    private int Index(double t)
    {
        int lo = 0, hi = _samples.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_samples[mid].Time <= t) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found;
    }

    /// <summary>Forgets samples older than <paramref name="t"/>, keeping the clicks.</summary>
    public void Trim(double t)
    {
        lock (_gate)
        {
            int i = Index(t);
            if (i > 0) _samples.RemoveRange(0, i);
            _clicks.RemoveAll(c => c < t - 2);
        }
    }

    public void Dispose()
    {
        _stop = true;
        if (_thread.IsAlive) _thread.Join(500);
    }
}

/// <summary>
/// Draws the recording's own pointer: smoothed, eased, and blurred along its path while it moves,
/// the way it looks in a film rather than a jumpy screen grab.
/// </summary>
internal sealed class SmoothCursorPainter : IDisposable
{
    private readonly Dictionary<(PointerArt.Shape, int), PointerSprite>? _vector;
    private readonly Dictionary<(IntPtr, int), PointerSprite?> _system = new();

    /// <param name="vector">The White or Black pointers, or null to use Windows' own pointer.</param>
    public SmoothCursorPainter(Dictionary<(PointerArt.Shape, int), PointerSprite>? vector) => _vector = vector;

    /// <summary>Seconds of the bell curve for a smoothing setting of 0 to 100.</summary>
    public static double Sigma(int smoothing) => smoothing <= 0 ? 0 : 0.13 * Math.Pow(smoothing / 100.0, 1.1);

    /// <summary>How long the shutter is open, as a share of a frame, for a motion blur setting of 0 to 100.</summary>
    public static double Shutter(int blur) => Math.Clamp(blur, 0, 100) / 100.0;

    /// <summary>The picture to draw for this Windows pointer at this size; null for nothing.</summary>
    public PointerSprite? SpriteFor(IntPtr handle, int size)
    {
        if (handle == IntPtr.Zero) return null;
        int id = SystemPointers.IdOf(handle);
        if (_vector != null && id is 32512 or 32513)
        {
            var shape = id == 32512 ? PointerArt.Shape.Arrow : PointerArt.Shape.Text;
            if (_vector.TryGetValue((shape, size), out var exact)) return exact;
            return _vector.Where(s => s.Key.Item1 == shape).OrderBy(s => Math.Abs(s.Key.Item2 - size)).Select(s => s.Value).FirstOrDefault();
        }
        if (!_system.TryGetValue((handle, size), out var sprite))
        {
            sprite = FromWindows(handle, id, size);
            _system[(handle, size)] = sprite;
        }
        return sprite;
    }

    /// <summary>
    /// Turns a Windows pointer into a sprite with soft edges: drawn once on black and once on white,
    /// the difference gives how see-through each pixel is.
    /// </summary>
    private static PointerSprite? FromWindows(IntPtr handle, int id, int size)
    {
        IntPtr icon = id != 0 ? SystemPointers.LoadAtSize(id, size) : IntPtr.Zero;
        bool loaded = icon != IntPtr.Zero;
        if (!loaded) icon = CopyIcon(handle);
        if (icon == IntPtr.Zero) return null;
        try
        {
            int hx = 0, hy = 0;
            if (GetIconInfo(icon, out var ii))
            {
                hx = ii.xHotspot;
                hy = ii.yHotspot;
                if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
                if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
            }
            double k = loaded ? 1 : size / (double)Math.Max(1, GetSystemMetrics(SM_CXCURSOR));
            var black = Draw(icon, size, 0);
            var white = Draw(icon, size, 255);
            if (black == null || white == null) return null;
            var px = new byte[size * size * 4];
            bool any = false;
            for (int i = 0; i < size * size; i++)
            {
                int o = i * 4;
                int diff = (white[o] - black[o] + white[o + 1] - black[o + 1] + white[o + 2] - black[o + 2]) / 3;
                int a = Math.Clamp(255 - diff, 0, 255);
                if (a == 0) continue;
                any = true;
                px[o] = (byte)Math.Min(black[o], a);
                px[o + 1] = (byte)Math.Min(black[o + 1], a);
                px[o + 2] = (byte)Math.Min(black[o + 2], a);
                px[o + 3] = (byte)a;
            }
            return any ? new PointerSprite(px, size, size, hx * k, hy * k) : null;
        }
        finally
        {
            DestroyIcon(icon);
        }
    }

    private static byte[]? Draw(IntPtr icon, int size, byte paper)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);
        var header = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = size, biHeight = -size, biPlanes = 1, biBitCount = 32 };
        IntPtr dib = CreateDIBSection(mem, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero)
        {
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
            return null;
        }
        IntPtr old = SelectObject(mem, dib);
        try
        {
            var fill = new byte[size * size * 4];
            if (paper != 0) Array.Fill(fill, paper);
            Marshal.Copy(fill, 0, bits, fill.Length);
            DrawIconEx(mem, 0, 0, icon, size, size, 0, IntPtr.Zero, DI_NORMAL);
            GdiFlush();
            Marshal.Copy(bits, fill, 0, fill.Length);
            return fill;
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(dib);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>
    /// Lays the sprite over the frame at every point of <paramref name="path"/> (output pixels),
    /// averaged: one point is a sharp pointer, many along a fast move is a motion-blurred streak.
    /// Each point is placed to a fraction of a pixel, so slow glides don't step.
    /// </summary>
    public static void Paint(byte[] frame, int frameW, int frameH, PointerSprite s, IReadOnlyList<Point> path)
    {
        if (path.Count == 0) return;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in path)
        {
            minX = Math.Min(minX, p.X - s.HotX);
            minY = Math.Min(minY, p.Y - s.HotY);
            maxX = Math.Max(maxX, p.X - s.HotX + s.Width);
            maxY = Math.Max(maxY, p.Y - s.HotY + s.Height);
        }
        int bx = Math.Max(0, (int)Math.Floor(minX)), by = Math.Max(0, (int)Math.Floor(minY));
        int bx1 = Math.Min(frameW, (int)Math.Ceiling(maxX) + 1), by1 = Math.Min(frameH, (int)Math.Ceiling(maxY) + 1);
        int bw = bx1 - bx, bh = by1 - by;
        if (bw <= 0 || bh <= 0) return;
        var acc = new float[bw * bh * 4];
        float share = 1f / path.Count;
        foreach (var p in path)
        {
            double left = p.X - s.HotX, top = p.Y - s.HotY;
            int ix = (int)Math.Floor(left), iy = (int)Math.Floor(top);
            float fx = (float)(left - ix), fy = (float)(top - iy);
            // each sprite pixel lands on up to four frame pixels
            float w00 = (1 - fx) * (1 - fy) * share, w10 = fx * (1 - fy) * share, w01 = (1 - fx) * fy * share, w11 = fx * fy * share;
            for (int sy = 0; sy < s.Height; sy++)
            {
                int y0 = iy + sy - by;
                if (y0 < -1 || y0 >= bh) continue;
                for (int sx = 0; sx < s.Width; sx++)
                {
                    int si = (sy * s.Width + sx) * 4;
                    if (s.Pixels[si + 3] == 0) continue;
                    int x0 = ix + sx - bx;
                    if (x0 < -1 || x0 >= bw) continue;
                    Add(acc, bw, bh, x0, y0, s.Pixels, si, w00);
                    Add(acc, bw, bh, x0 + 1, y0, s.Pixels, si, w10);
                    Add(acc, bw, bh, x0, y0 + 1, s.Pixels, si, w01);
                    Add(acc, bw, bh, x0 + 1, y0 + 1, s.Pixels, si, w11);
                }
            }
        }
        // premultiplied "over"
        for (int y = 0; y < bh; y++)
        {
            for (int x = 0; x < bw; x++)
            {
                int ai = (y * bw + x) * 4;
                float a = acc[ai + 3];
                if (a <= 0.5f) continue;
                int fi = ((by + y) * frameW + bx + x) * 4;
                float keep = 1 - Math.Min(255, a) / 255f;
                frame[fi] = (byte)Math.Clamp(acc[ai] + frame[fi] * keep + 0.5f, 0, 255);
                frame[fi + 1] = (byte)Math.Clamp(acc[ai + 1] + frame[fi + 1] * keep + 0.5f, 0, 255);
                frame[fi + 2] = (byte)Math.Clamp(acc[ai + 2] + frame[fi + 2] * keep + 0.5f, 0, 255);
            }
        }
    }

    private static void Add(float[] acc, int bw, int bh, int x, int y, byte[] px, int si, float w)
    {
        if (w <= 0 || x < 0 || y < 0 || x >= bw || y >= bh) return;
        int ai = (y * bw + x) * 4;
        acc[ai] += px[si] * w;
        acc[ai + 1] += px[si + 1] * w;
        acc[ai + 2] += px[si + 2] * w;
        acc[ai + 3] += px[si + 3] * w;
    }

    /// <summary>
    /// The points the pointer passes through while the shutter is open for a frame at
    /// <paramref name="t"/>, in output pixels: one when it's still, more the faster it moves.
    /// </summary>
    public static List<Point> ShutterPath(Func<double, Point> at, double t, double frameTime, double shutter)
    {
        var centre = at(t);
        if (shutter <= 0) return new List<Point> { centre };
        double half = frameTime * shutter / 2;
        var a = at(t - half);
        var b = at(t + half);
        double length = (centre - a).Length + (b - centre).Length;
        int n = Math.Clamp((int)Math.Ceiling(length / 1.2), 1, 40);
        if (n == 1) return new List<Point> { centre };
        var path = new List<Point>(n);
        for (int i = 0; i < n; i++) path.Add(at(t - half + 2 * half * i / (n - 1)));
        return path;
    }

    public void Dispose() => _system.Clear();
}
