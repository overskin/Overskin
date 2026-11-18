using System.Runtime.InteropServices;
using static Overskin.NativeMethods;

namespace Overskin;

/// <summary>
/// Copies the same screen rectangle over and over, fast: one reusable DIB section, no WPF
/// objects, safe to use from a background thread. Used for recording.
/// </summary>
public sealed class ScreenGrabber : IDisposable
{
    private readonly int _x, _y, _w, _h;
    private readonly IntPtr _screen, _mem, _dib, _old, _bits;

    public int Width => _w;
    public int Height => _h;

    public ScreenGrabber(int x, int y, int width, int height)
    {
        _x = x; _y = y; _w = width; _h = height;
        _screen = GetDC(IntPtr.Zero);
        _mem = CreateCompatibleDC(_screen);
        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // top-down rows
            biPlanes = 1,
            biBitCount = 32,
        };
        _dib = CreateDIBSection(_mem, ref header, 0, out _bits, IntPtr.Zero, 0);
        _old = SelectObject(_mem, _dib);
    }

    /// <summary>The rectangle as BGRA bytes (alpha is meaningless), optionally with the pointer drawn in.</summary>
    public byte[] Grab(bool cursor)
    {
        BitBlt(_mem, 0, 0, _w, _h, _screen, _x, _y, SRCCOPY);
        if (cursor) DrawCursor(_mem, _x, _y);
        GdiFlush();
        var buffer = new byte[_w * _h * 4];
        Marshal.Copy(_bits, buffer, 0, buffer.Length);
        return buffer;
    }

    public void Dispose()
    {
        SelectObject(_mem, _old);
        DeleteObject(_dib);
        DeleteDC(_mem);
        ReleaseDC(IntPtr.Zero, _screen);
    }
}
