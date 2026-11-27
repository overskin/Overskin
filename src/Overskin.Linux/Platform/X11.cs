using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Overskin.Linux.Platform;

/// <summary>Reads the X11 root window, which holds every monitor side by side.</summary>
public static unsafe class X11
{
    private const string Lib = "libX11.so.6";
    private const int ZPixmap = 2;
    private const ulong AllPlanes = ~0UL;

    [DllImport(Lib)] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport(Lib)] private static extern int XCloseDisplay(IntPtr display);
    [DllImport(Lib)] private static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport(Lib)] private static extern int XGetWindowAttributes(IntPtr display, IntPtr window, out XWindowAttributes attributes);
    [DllImport(Lib)] private static extern IntPtr XGetImage(IntPtr display, IntPtr drawable, int x, int y, uint width, uint height, ulong planes, int format);

    [StructLayout(LayoutKind.Sequential)]
    private struct XWindowAttributes
    {
        public int x, y, width, height, border_width, depth;
        public IntPtr visual, root;
        public int @class, bit_gravity, win_gravity, backing_store;
        public ulong backing_planes, backing_pixel;
        public int save_under;
        public IntPtr colormap;
        public int map_installed, map_state;
        public long all_event_masks, your_event_mask, do_not_propagate_mask;
        public int override_redirect;
        public IntPtr screen;
    }

    // The start of Xlib's XImage, up to the function table (64-bit layout)
    [StructLayout(LayoutKind.Sequential)]
    private struct XImage
    {
        public int width, height, xoffset, format;
        public IntPtr data;
        public int byte_order, bitmap_unit, bitmap_bit_order, bitmap_pad, depth, bytes_per_line, bits_per_pixel;
        public ulong red_mask, green_mask, blue_mask;
        public IntPtr obdata;
        public IntPtr create_image, destroy_image;
    }

    public static bool IsAvailable
    {
        get
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))) return false;
            try { return NativeLibrary.TryLoad(Lib, out _); } catch { return false; }
        }
    }

    public static Bitmap GrabRoot()
    {
        var display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero) throw new InvalidOperationException("can't open the X display");
        try
        {
            var root = XDefaultRootWindow(display);
            XGetWindowAttributes(display, root, out var attr);
            if (attr.width <= 0 || attr.height <= 0) throw new InvalidOperationException("the root window has no size");

            var imagePtr = XGetImage(display, root, 0, 0, (uint)attr.width, (uint)attr.height, AllPlanes, ZPixmap);
            if (imagePtr == IntPtr.Zero) throw new InvalidOperationException("XGetImage failed");
            var image = (XImage*)imagePtr;
            try
            {
                if (image->bits_per_pixel != 32)
                    throw new NotSupportedException($"{image->bits_per_pixel}-bit displays aren't supported");

                int w = image->width, h = image->height, stride = w * 4;
                var bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
                using (var fb = bitmap.Lock())
                {
                    for (int y = 0; y < h; y++)
                    {
                        var src = (uint*)((byte*)image->data + (long)y * image->bytes_per_line);
                        var dst = (uint*)((byte*)fb.Address + (long)y * fb.RowBytes);
                        for (int x = 0; x < w; x++) dst[x] = src[x] | 0xFF000000; // X leaves the alpha byte undefined
                    }
                }
                return bitmap;
            }
            finally
            {
                // XDestroyImage is a macro that calls through the image's own function table
                var destroy = (delegate* unmanaged<IntPtr, int>)image->destroy_image;
                destroy(imagePtr);
            }
        }
        finally
        {
            XCloseDisplay(display);
        }
    }
}
