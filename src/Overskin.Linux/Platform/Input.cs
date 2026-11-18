using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Overskin.Linux.Core;

namespace Overskin.Linux.Platform;

/// <summary>
/// The pointer, mouse input and the windows on screen, for the features that need them
/// (window picking, scrolling capture, the step recorder, Trace, auto-zoom).
/// X11 does all of it directly. Wayland keeps most of it from apps: Hyprland and Sway tell where
/// windows and the pointer are, and ydotool (if set up) can move the mouse.
/// Coordinates are always in the pixels of <see cref="ScreenGrabber"/>'s pictures.
/// </summary>
public static class Input
{
    private const string X = "libX11.so.6";
    private const string Xtst = "libXtst.so.6";
    private const string Xext = "libXext.so.6";

    [DllImport(X)] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport(X)] private static extern int XCloseDisplay(IntPtr display);
    [DllImport(X)] private static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport(X)] private static extern int XFlush(IntPtr display);
    [DllImport(X)] private static extern int XSync(IntPtr display, bool discard);
    [DllImport(X)] private static extern bool XQueryPointer(IntPtr display, IntPtr window, out IntPtr root, out IntPtr child,
        out int rootX, out int rootY, out int winX, out int winY, out uint mask);
    [DllImport(X)] private static extern int XQueryKeymap(IntPtr display, byte[] keys);
    [DllImport(X)] private static extern byte XKeysymToKeycode(IntPtr display, IntPtr keysym);
    [DllImport(X)] private static extern IntPtr XInternAtom(IntPtr display, string name, bool onlyIfExists);
    [DllImport(X)] private static extern int XGetWindowProperty(IntPtr display, IntPtr window, IntPtr property, long offset, long length,
        bool delete, IntPtr reqType, out IntPtr actualType, out int actualFormat, out ulong items, out ulong bytesAfter, out IntPtr prop);
    [DllImport(X)] private static extern int XFree(IntPtr data);
    [DllImport(X)] private static extern bool XTranslateCoordinates(IntPtr display, IntPtr src, IntPtr dest, int x, int y, out int dx, out int dy, out IntPtr child);
    [DllImport(X)] private static extern int XGetWindowAttributes(IntPtr display, IntPtr window, out WindowAttributes attributes);
    [DllImport(Xtst)] private static extern int XTestFakeMotionEvent(IntPtr display, int screen, int x, int y, ulong delay);
    [DllImport(Xtst)] private static extern int XTestFakeButtonEvent(IntPtr display, uint button, bool press, ulong delay);
    [DllImport(Xext)] private static extern void XShapeCombineRectangles(IntPtr display, IntPtr window, int kind, int x, int y, IntPtr rects, int n, int op, int ordering);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowAttributes
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

    private static IntPtr _display;
    private static readonly object Gate = new();

    private static bool OnX11 => OperatingSystem.IsLinux() && !ScreenGrabber.IsWayland && X11.IsAvailable;

    private static IntPtr Display
    {
        get
        {
            if (_display == IntPtr.Zero) _display = XOpenDisplay(IntPtr.Zero);
            return _display;
        }
    }

    // ---- the pointer --------------------------------------------------------------------------

    /// <summary>Where the pointer is and whether the left button is down, or null where the desktop won't say.</summary>
    public static async Task<(Point At, bool LeftDown)?> PointerAsync()
    {
        if (OnX11)
        {
            lock (Gate)
            {
                if (Display == IntPtr.Zero) return null;
                XQueryPointer(Display, XDefaultRootWindow(Display), out _, out _, out int x, out int y, out _, out _, out uint mask);
                return (new Point(x, y), (mask & (1 << 8)) != 0);
            }
        }
        if (ScreenGrabber.IsWayland && ScreenGrabber.HasCommand("hyprctl"))
        {
            // "x, y" in the desktop's layout; turned into the focused monitor's picture pixels
            var text = await ScreenGrabber.OutputOf("hyprctl", "cursorpos");
            var parts = text?.Split(',', StringSplitOptions.TrimEntries);
            if (parts?.Length == 2 && double.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var lx)
                                   && double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var ly))
            {
                var m = await FocusedMonitorAsync();
                return (new Point((lx - m.X) * m.Scale, (ly - m.Y) * m.Scale), false);
            }
        }
        return null;
    }

    /// <summary>True while Esc is held down (X11 only), the way to stop drawing.</summary>
    public static bool EscapeDown()
    {
        if (!OnX11) return false;
        lock (Gate)
        {
            var keys = new byte[32];
            XQueryKeymap(Display, keys);
            int code = XKeysymToKeycode(Display, (IntPtr)0xff1b);
            return code > 0 && (keys[code / 8] & (1 << (code % 8))) != 0;
        }
    }

    // ---- making the mouse move ------------------------------------------------------------------

    /// <summary>Whether Overskin can move the mouse and click here, and if not, why.</summary>
    public static string? CannotDrive()
    {
        if (!OperatingSystem.IsLinux()) return "Moving the mouse is only built for Linux.";
        if (OnX11) return NativeLibrary.TryLoad(Xtst, out _) ? null : "Install libxtst (Arch: libxtst · Debian/Ubuntu: libxtst6).";
        if (ScreenGrabber.HasCommand("ydotool")) return null;
        return "Wayland doesn't let apps move the mouse. Install ydotool and start its service (systemctl --user enable --now ydotool), then try again.";
    }

    public static async Task MoveAsync(Point p)
    {
        if (OnX11)
        {
            lock (Gate) { XTestFakeMotionEvent(Display, -1, (int)Math.Round(p.X), (int)Math.Round(p.Y), 0); XFlush(Display); }
            return;
        }
        var m = await FocusedMonitorAsync();
        await Ydotool($"mousemove --absolute -x {(int)Math.Round(m.X + p.X / m.Scale)} -y {(int)Math.Round(m.Y + p.Y / m.Scale)}");
    }

    public static async Task ButtonAsync(bool down)
    {
        if (OnX11)
        {
            lock (Gate) { XTestFakeButtonEvent(Display, 1, down, 0); XFlush(Display); }
            return;
        }
        await Ydotool($"click {(down ? "0x40" : "0x80")}");
    }

    /// <summary>Scrolls the wheel by whole notches (positive is down).</summary>
    public static async Task ScrollAsync(int notches)
    {
        if (OnX11)
        {
            uint button = notches > 0 ? 5u : 4u;
            lock (Gate)
            {
                for (int i = 0; i < Math.Abs(notches); i++)
                {
                    XTestFakeButtonEvent(Display, button, true, 0);
                    XTestFakeButtonEvent(Display, button, false, 0);
                }
                XFlush(Display);
            }
            return;
        }
        await Ydotool($"mousemove --wheel -x 0 -y {-notches}");
    }

    private static Task Ydotool(string args) => ScreenGrabber.RunAsync("ydotool", args, 5);

    // ---- windows ------------------------------------------------------------------------------

    /// <summary>The windows on screen, top first, in picture pixels. Empty where the desktop won't say.</summary>
    public static async Task<List<(string Title, PixelRect Bounds)>> WindowsAsync()
    {
        var list = new List<(string, PixelRect)>();
        try
        {
            if (OnX11) list = X11Windows();
            else if (ScreenGrabber.IsWayland && ScreenGrabber.HasCommand("hyprctl")) list = await HyprlandWindowsAsync();
            else if (ScreenGrabber.IsWayland && ScreenGrabber.HasCommand("swaymsg")) list = await SwayWindowsAsync();
        }
        catch (Exception ex) { Log.Write("windows: " + ex.Message); }
        return list;
    }

    private static unsafe List<(string, PixelRect)> X11Windows()
    {
        var result = new List<(string, PixelRect)>();
        lock (Gate)
        {
            var d = Display;
            var root = XDefaultRootWindow(d);
            var stacking = XInternAtom(d, "_NET_CLIENT_LIST_STACKING", false);
            var frame = XInternAtom(d, "_NET_FRAME_EXTENTS", false);
            var nameAtom = XInternAtom(d, "_NET_WM_NAME", false);
            var utf8 = XInternAtom(d, "UTF8_STRING", false);
            var hidden = XInternAtom(d, "_NET_WM_STATE_HIDDEN", false);
            var state = XInternAtom(d, "_NET_WM_STATE", false);

            if (XGetWindowProperty(d, root, stacking, 0, 4096, false, IntPtr.Zero, out _, out _, out ulong n, out _, out var data) != 0 || data == IntPtr.Zero)
                return result;
            var windows = new IntPtr[n];
            for (int i = 0; i < (int)n; i++) windows[i] = ((IntPtr*)data)[i];
            XFree(data);

            foreach (var w in windows.Reverse()) // the list runs bottom to top
            {
                if (XGetWindowAttributes(d, w, out var a) == 0 || a.map_state != 2) continue; // not viewable
                if (HasAtom(d, w, state, hidden)) continue;
                XTranslateCoordinates(d, w, root, 0, 0, out int x, out int y, out _);
                int l = 0, r = 0, t = 0, b = 0;
                if (XGetWindowProperty(d, w, frame, 0, 4, false, IntPtr.Zero, out _, out _, out ulong fn, out _, out var fd) == 0 && fd != IntPtr.Zero)
                {
                    if (fn == 4) { l = (int)((long*)fd)[0]; r = (int)((long*)fd)[1]; t = (int)((long*)fd)[2]; b = (int)((long*)fd)[3]; }
                    XFree(fd);
                }
                string title = "";
                if (XGetWindowProperty(d, w, nameAtom, 0, 256, false, utf8, out _, out _, out ulong tn, out _, out var td) == 0 && td != IntPtr.Zero)
                {
                    title = Marshal.PtrToStringUTF8(td, (int)tn) ?? "";
                    XFree(td);
                }
                var rect = new PixelRect(x - l, y - t, a.width + l + r, a.height + t + b);
                if (rect.Width > 20 && rect.Height > 20) result.Add((title, rect));
            }
        }
        return result;
    }

    private static unsafe bool HasAtom(IntPtr d, IntPtr w, IntPtr property, IntPtr atom)
    {
        if (XGetWindowProperty(d, w, property, 0, 64, false, IntPtr.Zero, out _, out _, out ulong n, out _, out var data) != 0 || data == IntPtr.Zero)
            return false;
        bool found = false;
        for (int i = 0; i < (int)n; i++) if (((IntPtr*)data)[i] == atom) found = true;
        XFree(data);
        return found;
    }

    public readonly record struct Monitor(double X, double Y, double Scale, int Workspace);

    /// <summary>Where the focused monitor sits in the desktop's layout (Hyprland and Sway); the origin elsewhere.</summary>
    public static async Task<Monitor> FocusedMonitorAsync()
    {
        try
        {
            if (ScreenGrabber.HasCommand("hyprctl") && await ScreenGrabber.OutputOf("hyprctl", "monitors -j") is { } json)
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var m in doc.RootElement.EnumerateArray())
                    if (m.GetProperty("focused").GetBoolean())
                        return new Monitor(m.GetProperty("x").GetDouble(), m.GetProperty("y").GetDouble(), m.GetProperty("scale").GetDouble(),
                                           m.GetProperty("activeWorkspace").GetProperty("id").GetInt32());
            }
            if (ScreenGrabber.HasCommand("swaymsg") && await ScreenGrabber.OutputOf("swaymsg", "-t get_outputs -r") is { } sway)
            {
                using var doc = JsonDocument.Parse(sway);
                foreach (var m in doc.RootElement.EnumerateArray())
                    if (m.TryGetProperty("focused", out var f) && f.GetBoolean())
                    {
                        var r = m.GetProperty("rect");
                        return new Monitor(r.GetProperty("x").GetDouble(), r.GetProperty("y").GetDouble(),
                                           m.TryGetProperty("scale", out var s) ? s.GetDouble() : 1, 0);
                    }
            }
        }
        catch (Exception ex) { Log.Write("monitor: " + ex.Message); }
        return new Monitor(0, 0, 1, 0);
    }

    private static async Task<List<(string, PixelRect)>> HyprlandWindowsAsync()
    {
        var result = new List<(string Title, PixelRect Bounds, int Order)>();
        var m = await FocusedMonitorAsync();
        var json = await ScreenGrabber.OutputOf("hyprctl", "clients -j");
        if (json == null) return new();
        using var doc = JsonDocument.Parse(json);
        foreach (var c in doc.RootElement.EnumerateArray())
        {
            if (c.GetProperty("workspace").GetProperty("id").GetInt32() != m.Workspace) continue;
            if (c.TryGetProperty("hidden", out var h) && h.GetBoolean()) continue;
            var at = c.GetProperty("at");
            var size = c.GetProperty("size");
            double x = at[0].GetDouble(), y = at[1].GetDouble(), w = size[0].GetDouble(), hh = size[1].GetDouble();
            var rect = new PixelRect((int)((x - m.X) * m.Scale), (int)((y - m.Y) * m.Scale), (int)(w * m.Scale), (int)(hh * m.Scale));
            int order = c.TryGetProperty("focusHistoryID", out var f) ? f.GetInt32() : 99;
            // floating windows sit above tiled ones
            if (c.TryGetProperty("floating", out var fl) && fl.GetBoolean()) order -= 1000;
            result.Add((c.GetProperty("title").GetString() ?? "", rect, order));
        }
        return result.OrderBy(r => r.Order).Select(r => (r.Title, r.Bounds)).ToList();
    }

    private static async Task<List<(string, PixelRect)>> SwayWindowsAsync()
    {
        var result = new List<(string, PixelRect)>();
        var m = await FocusedMonitorAsync();
        var json = await ScreenGrabber.OutputOf("swaymsg", "-t get_tree -r");
        if (json == null) return result;
        using var doc = JsonDocument.Parse(json);
        void Walk(JsonElement node)
        {
            bool isWindow = node.TryGetProperty("pid", out _) && node.TryGetProperty("visible", out var v) && v.GetBoolean();
            if (isWindow)
            {
                var r = node.GetProperty("rect");
                result.Add((node.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    new PixelRect((int)((r.GetProperty("x").GetDouble() - m.X) * m.Scale), (int)((r.GetProperty("y").GetDouble() - m.Y) * m.Scale),
                                  (int)(r.GetProperty("width").GetDouble() * m.Scale), (int)(r.GetProperty("height").GetDouble() * m.Scale))));
            }
            foreach (var key in new[] { "floating_nodes", "nodes" })
                if (node.TryGetProperty(key, out var kids)) foreach (var k in kids.EnumerateArray()) Walk(k);
        }
        Walk(doc.RootElement);
        return result;
    }

    // ---- click-through windows ----------------------------------------------------------------

    /// <summary>Lets clicks pass through an Overskin window to whatever is under it (X11 and XWayland).</summary>
    public static unsafe bool SetClickThrough(IntPtr window, bool through)
    {
        if (!OperatingSystem.IsLinux() || window == IntPtr.Zero || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))) return false;
        try
        {
            lock (Gate)
            {
                const int ShapeInput = 2, ShapeSet = 0;
                if (through)
                {
                    XShapeCombineRectangles(Display, window, ShapeInput, 0, 0, IntPtr.Zero, 0, ShapeSet, 0);
                }
                else
                {
                    // one rectangle bigger than any window: input everywhere again
                    var rect = stackalloc short[4] { 0, 0, short.MaxValue, short.MaxValue };
                    XShapeCombineRectangles(Display, window, ShapeInput, 0, 0, (IntPtr)rect, 1, ShapeSet, 0);
                }
                XFlush(Display);
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("click-through: " + ex.Message);
            return false;
        }
    }
}
