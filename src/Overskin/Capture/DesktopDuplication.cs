using System.Runtime.InteropServices;
using static Overskin.NativeMethods;

namespace Overskin;

/// <summary>Copies a screen rectangle into a fixed-size BGRA frame, for video.</summary>
internal interface IFrameGrabber : IDisposable
{
    /// <summary>Copies the rectangle (screen pixels) scaled to the frame size, then lets <paramref name="overlay"/> draw on it with GDI.</summary>
    byte[] Grab(double x, double y, double width, double height, Action<IntPtr>? overlay);
    string Kind { get; }
}

/// <summary>Picks the fastest way to read the screen that works for this area.</summary>
internal static class FrameGrabbers
{
    public static IFrameGrabber For(System.Windows.Int32Rect area, int width, int height)
    {
        try
        {
            var fast = DuplicationGrabber.TryCreate(area, width, height);
            if (fast != null) return fast;
        }
        catch
        {
            // fall back to GDI
        }
        return new ScaledGrabber(width, height);
    }
}

/// <summary>
/// Reads the screen with DXGI desktop duplication: the picture comes straight from the graphics
/// card, several times faster than GDI on big screens. Works when the area is on one monitor,
/// that monitor isn't rotated and shows ordinary 8-bit colour; otherwise <see cref="TryCreate"/> returns null.
/// Windows hidden from captures stay hidden, as with GDI.
/// </summary>
internal sealed unsafe class DuplicationGrabber : IFrameGrabber
{
    private readonly int _w, _h;
    private readonly System.Windows.Int32Rect _area; // in screen pixels
    private readonly int _monitorLeft, _monitorTop;
    private IntPtr _device, _context, _output1, _duplication, _staging;
    private bool _haveFrame;

    // GDI surface for the pointer and anything else drawn on top
    private readonly IntPtr _screen, _mem, _dib, _old, _bits;

    public string Kind => "DXGI";

    private DuplicationGrabber(System.Windows.Int32Rect area, int w, int h, int monitorLeft, int monitorTop)
    {
        _area = area;
        _w = w;
        _h = h;
        _monitorLeft = monitorLeft;
        _monitorTop = monitorTop;
        _screen = GetDC(IntPtr.Zero);
        _mem = CreateCompatibleDC(_screen);
        var header = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        _dib = CreateDIBSection(_mem, ref header, 0, out _bits, IntPtr.Zero, 0);
        _old = SelectObject(_mem, _dib);
    }

    public static DuplicationGrabber? TryCreate(System.Windows.Int32Rect area, int w, int h)
    {
        var factoryIid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        if (CreateDXGIFactory1(ref factoryIid, out IntPtr factory) < 0) return null;
        try
        {
            for (uint a = 0; ; a++)
            {
                // IDXGIFactory::EnumAdapters (7)
                if (Call(factory, 7, a, out IntPtr adapter) < 0) break;
                try
                {
                    for (uint o = 0; ; o++)
                    {
                        // IDXGIAdapter::EnumOutputs (7)
                        if (Call(adapter, 7, o, out IntPtr output) < 0) break;
                        try
                        {
                            OutputDesc desc;
                            // IDXGIOutput::GetDesc (7)
                            if (((delegate* unmanaged[Stdcall]<IntPtr, OutputDesc*, int>)VTable(output, 7))(output, &desc) < 0) continue;
                            var r = desc.Desktop;
                            bool inside = area.X >= r.Left && area.Y >= r.Top && area.X + area.Width <= r.Right && area.Y + area.Height <= r.Bottom;
                            if (!inside) continue;
                            if (desc.Rotation is not (0 or 1)) return null; // rotated screens come out sideways
                            var grabber = new DuplicationGrabber(area, w, h, r.Left, r.Top);
                            if (!grabber.Open(adapter, output)) { grabber.Dispose(); return null; }
                            return grabber;
                        }
                        finally { Marshal.Release(output); }
                    }
                }
                finally { Marshal.Release(adapter); }
            }
            return null;
        }
        finally { Marshal.Release(factory); }
    }

    private bool Open(IntPtr adapter, IntPtr output)
    {
        // D3D11 device on the adapter that owns this monitor
        int hr = D3D11CreateDevice(adapter, 0 /* UNKNOWN: the adapter decides */, IntPtr.Zero, 0, IntPtr.Zero, 0, 7, out _device, out _, out _context);
        if (hr < 0) return false;
        var output1Iid = new Guid("00cddea8-939b-4b83-a340-a685226666cc");
        if (Marshal.QueryInterface(output, ref output1Iid, out _output1) < 0) return false;
        return Duplicate();
    }

    private bool Duplicate()
    {
        if (_duplication != IntPtr.Zero) { Marshal.Release(_duplication); _duplication = IntPtr.Zero; }
        // IDXGIOutput1::DuplicateOutput (22)
        IntPtr dup;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)VTable(_output1, 22))(_output1, _device, &dup);
        if (hr < 0) return false;
        _duplication = dup;
        _haveFrame = false;
        return true;
    }

    /// <summary>Copies the newest desktop picture of the area into the staging texture. False when there's nothing yet.</summary>
    private bool Refresh()
    {
        if (_duplication == IntPtr.Zero && !Duplicate()) return _haveFrame;
        FrameInfo info;
        IntPtr resource;
        // IDXGIOutputDuplication::AcquireNextFrame (8); wait a moment for the very first picture
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, FrameInfo*, IntPtr*, int>)VTable(_duplication, 8))(_duplication, _haveFrame ? 0u : 200u, &info, &resource);
        if (hr == unchecked((int)0x887A0027)) return _haveFrame; // no change since the last picture
        if (hr == unchecked((int)0x887A0026)) // access lost: a mode change, UAC or a full-screen game; start again
        {
            Duplicate();
            return _haveFrame;
        }
        if (hr < 0) throw new InvalidOperationException("Desktop duplication failed (0x" + hr.ToString("X8") + ").");
        // a frame with no present time only moved the pointer: before the first real picture there is nothing to copy yet
        bool pictureChanged = *(long*)&info != 0;
        try
        {
            var texIid = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
            if (Marshal.QueryInterface(resource, ref texIid, out IntPtr texture) < 0) return _haveFrame;
            try
            {
                if (!pictureChanged) return _haveFrame;
                if (_staging == IntPtr.Zero && !MakeStaging(texture)) throw new InvalidOperationException("This screen's colour format can't be read quickly.");
                var box = new Box
                {
                    Left = (uint)(_area.X - _monitorLeft), Top = (uint)(_area.Y - _monitorTop), Front = 0,
                    Right = (uint)(_area.X - _monitorLeft + _area.Width), Bottom = (uint)(_area.Y - _monitorTop + _area.Height), Back = 1,
                };
                // ID3D11DeviceContext::CopySubresourceRegion (46)
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, Box*, void>)VTable(_context, 46))
                    (_context, _staging, 0, 0, 0, 0, texture, 0, &box);
                _haveFrame = true;
            }
            finally { Marshal.Release(texture); }
        }
        finally
        {
            Marshal.Release(resource);
            // IDXGIOutputDuplication::ReleaseFrame (14)
            ((delegate* unmanaged[Stdcall]<IntPtr, int>)VTable(_duplication, 14))(_duplication);
        }
        return _haveFrame;
    }

    private bool MakeStaging(IntPtr desktop)
    {
        TextureDesc d;
        // ID3D11Texture2D::GetDesc (10)
        ((delegate* unmanaged[Stdcall]<IntPtr, TextureDesc*, void>)VTable(desktop, 10))(desktop, &d);
        if (d.Format is not (87 or 91)) return false; // B8G8R8A8 (UNORM or SRGB); HDR screens use other formats
        var s = new TextureDesc
        {
            Width = (uint)_area.Width, Height = (uint)_area.Height, MipLevels = 1, ArraySize = 1, Format = d.Format,
            SampleCount = 1, SampleQuality = 0, Usage = 3 /* STAGING */, BindFlags = 0, CPUAccessFlags = 0x20000 /* READ */, MiscFlags = 0,
        };
        IntPtr staging;
        // ID3D11Device::CreateTexture2D (5)
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, TextureDesc*, IntPtr, IntPtr*, int>)VTable(_device, 5))(_device, &s, IntPtr.Zero, &staging);
        if (hr < 0) return false;
        _staging = staging;
        return true;
    }

    public byte[] Grab(double x, double y, double width, double height, Action<IntPtr>? overlay)
    {
        // the first real picture can take a few tries
        for (int tries = 0; !Refresh(); tries++)
            if (tries >= 10) throw new InvalidOperationException("The screen picture didn't arrive.");
        Mapped map;
        // ID3D11DeviceContext::Map (14) and Unmap (15)
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, uint, Mapped*, int>)VTable(_context, 14))(_context, _staging, 0, 1 /* READ */, 0, &map);
        if (hr < 0) throw new InvalidOperationException("Couldn't read the screen picture.");
        try
        {
            // the view, relative to the area
            double vx = x - _area.X, vy = y - _area.Y;
            Scale((byte*)map.Data, (int)map.RowPitch, _area.Width, _area.Height, vx, vy, width, height, (byte*)_bits, _w, _h);
        }
        finally
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)VTable(_context, 15))(_context, _staging, 0);
        }
        if (overlay != null)
        {
            overlay(_mem);
            GdiFlush();
        }
        var buffer = new byte[_w * _h * 4];
        Marshal.Copy(_bits, buffer, 0, buffer.Length);
        return buffer;
    }

    /// <summary>
    /// Copies the view rectangle of the source into the destination: straight when the sizes match,
    /// otherwise with bilinear filtering.
    /// </summary>
    internal static void Scale(byte* src, int srcPitch, int srcW, int srcH, double vx, double vy, double vw, double vh, byte* dst, int dstW, int dstH)
    {
        int ix = (int)Math.Round(vx), iy = (int)Math.Round(vy);
        if (Math.Abs(vw - dstW) < 0.5 && Math.Abs(vh - dstH) < 0.5 && ix >= 0 && iy >= 0 && ix + dstW <= srcW && iy + dstH <= srcH)
        {
            var s = (IntPtr)src;
            var d = (IntPtr)dst;
            Parallel.For(0, dstH, row =>
                Buffer.MemoryCopy((byte*)s + (long)(iy + row) * srcPitch + ix * 4, (byte*)d + (long)row * dstW * 4, dstW * 4L, dstW * 4L));
            return;
        }
        double kx = vw / dstW, ky = vh / dstH;
        var sp = (IntPtr)src;
        var dp = (IntPtr)dst;
        Parallel.For(0, dstH, row =>
        {
            byte* s0 = (byte*)sp;
            byte* d = (byte*)dp + (long)row * dstW * 4;
            double sy = vy + (row + 0.5) * ky - 0.5;
            int y0 = Math.Clamp((int)Math.Floor(sy), 0, srcH - 1), y1 = Math.Min(y0 + 1, srcH - 1);
            int fy = (int)((sy - Math.Floor(sy)) * 256);
            if (sy < 0) fy = 0;
            byte* r0 = s0 + (long)y0 * srcPitch, r1 = s0 + (long)y1 * srcPitch;
            for (int col = 0; col < dstW; col++)
            {
                double sx = vx + (col + 0.5) * kx - 0.5;
                int x0 = Math.Clamp((int)Math.Floor(sx), 0, srcW - 1), x1 = Math.Min(x0 + 1, srcW - 1);
                int fx = sx < 0 ? 0 : (int)((sx - Math.Floor(sx)) * 256);
                byte* a = r0 + x0 * 4, b = r0 + x1 * 4, c = r1 + x0 * 4, e = r1 + x1 * 4;
                for (int ch = 0; ch < 3; ch++)
                {
                    int top = a[ch] * (256 - fx) + b[ch] * fx, bottom = c[ch] * (256 - fx) + e[ch] * fx;
                    d[col * 4 + ch] = (byte)((top * (256 - fy) + bottom * fy) >> 16);
                }
                d[col * 4 + 3] = 255;
            }
        });
    }

    public void Dispose()
    {
        if (_staging != IntPtr.Zero) Marshal.Release(_staging);
        if (_duplication != IntPtr.Zero) Marshal.Release(_duplication);
        if (_output1 != IntPtr.Zero) Marshal.Release(_output1);
        if (_context != IntPtr.Zero) Marshal.Release(_context);
        if (_device != IntPtr.Zero) Marshal.Release(_device);
        _staging = _duplication = _output1 = _context = _device = IntPtr.Zero;
        SelectObject(_mem, _old);
        DeleteObject(_dib);
        DeleteDC(_mem);
        ReleaseDC(IntPtr.Zero, _screen);
    }

    // ---- COM by vtable slot ----------------------------------------------------------------

    private static IntPtr VTable(IntPtr obj, int slot) => (*(IntPtr**)obj)[slot];

    private static int Call(IntPtr obj, int slot, uint arg, out IntPtr result)
    {
        IntPtr r;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)VTable(obj, slot))(obj, arg, &r);
        result = hr < 0 ? IntPtr.Zero : r;
        return hr;
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr featureLevels,
        uint featureLevelCount, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct OutputDesc
    {
        public fixed char DeviceName[32];
        public Rect Desktop;
        public int AttachedToDesktop;
        public int Rotation;
        public IntPtr Monitor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TextureDesc
    {
        public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CPUAccessFlags, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Box { public uint Left, Top, Front, Right, Bottom, Back; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Mapped { public IntPtr Data; public uint RowPitch, DepthPitch; }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct FrameInfo { }
}
