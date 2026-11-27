using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Overskin.Linux.Core;

namespace Overskin.Linux.Platform;

/// <summary>
/// The parts of macOS Overskin talks to directly: where the pointer is and which display it's on
/// (CoreGraphics), the windows on screen, moving the mouse, global hotkeys (Carbon), the
/// clipboard and the Trash (AppKit through the Objective-C runtime), and putting a window above
/// the menu bar. Nothing here is used on Linux.
/// </summary>
public static unsafe class Mac
{
    public static bool IsMac => OperatingSystem.IsMacOS();

    private const string CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string AppServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";
    private const string ObjCLib = "/usr/lib/libobjc.A.dylib";

    // ---- shapes macOS hands back ---------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    public struct CGPoint { public double X, Y; public CGPoint(double x, double y) { X = x; Y = y; } }

    [StructLayout(LayoutKind.Sequential)]
    public struct CGSize { public double Width, Height; }

    [StructLayout(LayoutKind.Sequential)]
    public struct CGRect
    {
        public CGPoint Origin;
        public CGSize Size;
        public double X => Origin.X;
        public double Y => Origin.Y;
        public double Width => Size.Width;
        public double Height => Size.Height;
        public CGRect(double x, double y, double w, double h) { Origin = new CGPoint(x, y); Size = new CGSize { Width = w, Height = h }; }
        public bool Contains(double x, double y) => x >= X && y >= Y && x < X + Width && y < Y + Height;
    }

    // ---- CoreGraphics --------------------------------------------------------------------------

    [DllImport(CG)] private static extern uint CGMainDisplayID();
    [DllImport(CG)] private static extern CGRect CGDisplayBounds(uint display);
    [DllImport(CG)] private static extern int CGGetDisplaysWithPoint(CGPoint point, uint max, uint* displays, out uint count);
    [DllImport(CG)] private static extern int CGGetActiveDisplayList(uint max, uint* displays, out uint count);
    [DllImport(CG)] private static extern IntPtr CGEventCreate(IntPtr source);
    [DllImport(CG)] private static extern CGPoint CGEventGetLocation(IntPtr evt);
    [DllImport(CG)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CGEventSourceButtonState(int state, int button);
    [DllImport(CG)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CGEventSourceKeyState(int state, ushort key);
    [DllImport(CG)] private static extern IntPtr CGEventCreateMouseEvent(IntPtr source, int type, CGPoint at, int button);
    [DllImport(CG)] private static extern IntPtr CGEventCreateScrollWheelEvent2(IntPtr source, int units, uint wheelCount, int wheel1, int wheel2, int wheel3);
    [DllImport(CG)] private static extern void CGEventPost(int tap, IntPtr evt);
    [DllImport(CG)] private static extern IntPtr CGWindowListCopyWindowInfo(uint option, uint relativeTo);
    [DllImport(CG)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CGRectMakeWithDictionaryRepresentation(IntPtr dict, out CGRect rect);
    [DllImport(CG)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CGPreflightScreenCaptureAccess();
    [DllImport(CG)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CGRequestScreenCaptureAccess();
    [DllImport(AppServices)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool AXIsProcessTrusted();

    // ---- CoreFoundation ------------------------------------------------------------------------

    [DllImport(CF)] private static extern nint CFArrayGetCount(IntPtr array);
    [DllImport(CF)] private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);
    [DllImport(CF)] private static extern IntPtr CFDictionaryGetValue(IntPtr dict, IntPtr key);
    [DllImport(CF)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFNumberGetValue(IntPtr number, int type, out long value);
    [DllImport(CF)] private static extern IntPtr CFStringCreateWithCString(IntPtr alloc, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint encoding);
    [DllImport(CF)] private static extern nint CFStringGetLength(IntPtr text);
    [DllImport(CF)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFStringGetCString(IntPtr text, byte* buffer, nint size, uint encoding);
    [DllImport(CF)] private static extern void CFRelease(IntPtr obj);
    private const uint Utf8 = 0x08000100;
    private const int CFNumberSInt64 = 4; // kCFNumberSInt64Type

    private static readonly Dictionary<string, IntPtr> Keys = new();
    private static IntPtr Key(string name)
    {
        lock (Keys)
        {
            if (!Keys.TryGetValue(name, out var k)) Keys[name] = k = CFStringCreateWithCString(IntPtr.Zero, name, Utf8);
            return k;
        }
    }

    private static string? CFText(IntPtr text)
    {
        if (text == IntPtr.Zero) return null;
        nint size = CFStringGetLength(text) * 4 + 1;
        var buffer = new byte[size];
        fixed (byte* b = buffer)
            if (!CFStringGetCString(text, b, size, Utf8)) return null;
        int end = Array.IndexOf(buffer, (byte)0);
        return System.Text.Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end);
    }

    private static long CFLong(IntPtr number) => number != IntPtr.Zero && CFNumberGetValue(number, CFNumberSInt64, out long v) ? v : 0;

    // ---- displays and the pointer --------------------------------------------------------------

    /// <summary>Where the pointer is, in macOS's global points (top-left of the main display is 0, 0; y goes down).</summary>
    public static CGPoint PointerPoints()
    {
        var e = CGEventCreate(IntPtr.Zero);
        if (e == IntPtr.Zero) return default;
        try { return CGEventGetLocation(e); }
        finally { CFRelease(e); }
    }

    /// <summary>The display the pointer is on (its bounds in global points), or the main one.</summary>
    public static CGRect DisplayUnderPointer()
    {
        var at = PointerPoints();
        uint id;
        if (CGGetDisplaysWithPoint(at, 1, &id, out uint n) == 0 && n > 0) return CGDisplayBounds(id);
        return CGDisplayBounds(CGMainDisplayID());
    }

    /// <summary>The main display (the one with the menu bar) in global points: Cocoa's window coordinates count up from its bottom.</summary>
    public static CGRect MainDisplay => CGDisplayBounds(CGMainDisplayID());

    /// <summary>Every display, in global points.</summary>
    public static List<CGRect> Displays()
    {
        var ids = stackalloc uint[16];
        var list = new List<CGRect>();
        if (CGGetActiveDisplayList(16, ids, out uint n) == 0)
            for (int i = 0; i < n; i++) list.Add(CGDisplayBounds(ids[i]));
        return list;
    }

    /// <summary>
    /// The display the last screenshot was of, in global points, and how many picture pixels make one
    /// point (2 on Retina screens). Everything in "picture pixels" is measured from its top-left.
    /// </summary>
    public static (CGRect Display, double Scale) Shot { get; set; } = (new CGRect(0, 0, 1, 1), 1);

    /// <summary>A point in global points as picture pixels of the last screenshot.</summary>
    public static Point ToPicture(CGPoint p) => new((p.X - Shot.Display.X) * Shot.Scale, (p.Y - Shot.Display.Y) * Shot.Scale);

    /// <summary>Picture pixels of the last screenshot as global points.</summary>
    public static CGPoint FromPicture(Point p) => new(Shot.Display.X + p.X / Shot.Scale, Shot.Display.Y + p.Y / Shot.Scale);

    public static bool LeftDown() => CGEventSourceButtonState(0 /* combined session state */, 0 /* left */);

    public static bool EscapeDown() => CGEventSourceKeyState(0, 0x35 /* kVK_Escape */);

    // ---- permissions ---------------------------------------------------------------------------

    /// <summary>Whether Overskin may read the screen (Privacy &amp; Security › Screen Recording). Asks the first time.</summary>
    public static bool CanReadScreen(bool ask)
    {
        try
        {
            if (CGPreflightScreenCaptureAccess()) return true;
            return ask && CGRequestScreenCaptureAccess();
        }
        catch { return true; } // older macOS: no such question
    }

    /// <summary>Whether Overskin may move the mouse (Privacy &amp; Security › Accessibility).</summary>
    public static bool CanDrive()
    {
        try { return AXIsProcessTrusted(); }
        catch { return false; }
    }

    // ---- driving the mouse ---------------------------------------------------------------------

    private static CGPoint _drivenAt;

    public static void MoveTo(CGPoint p)
    {
        _drivenAt = p;
        Post(CGEventCreateMouseEvent(IntPtr.Zero, LeftDown() ? 6 /* left dragged */ : 5 /* moved */, p, 0));
    }

    public static void Button(bool down)
    {
        var at = PointerPoints();
        Post(CGEventCreateMouseEvent(IntPtr.Zero, down ? 1 /* left down */ : 2 /* left up */, at, 0));
    }

    public static void Scroll(int notches)
    {
        try { Post(CGEventCreateScrollWheelEvent2(IntPtr.Zero, 1 /* lines */, 1, notches, 0, 0)); }
        catch (EntryPointNotFoundException) { Log.Write("scroll: needs macOS 13 or later"); }
    }

    private static void Post(IntPtr evt)
    {
        if (evt == IntPtr.Zero) return;
        CGEventPost(0 /* kCGHIDEventTap */, evt);
        CFRelease(evt);
    }

    // ---- windows on screen ---------------------------------------------------------------------

    /// <summary>The number macOS knows the window under a point (global points) by, or 0: for live pins.</summary>
    public static uint WindowIdAt(CGPoint at)
    {
        var list = CGWindowListCopyWindowInfo(1 | 16, 0);
        if (list == IntPtr.Zero) return 0;
        try
        {
            int me = Environment.ProcessId;
            for (nint i = 0; i < CFArrayGetCount(list); i++)
            {
                var info = CFArrayGetValueAtIndex(list, i);
                if (CFLong(CFDictionaryGetValue(info, Key("kCGWindowLayer"))) != 0) continue;
                if (CFLong(CFDictionaryGetValue(info, Key("kCGWindowOwnerPID"))) == me) continue;
                var boundsDict = CFDictionaryGetValue(info, Key("kCGWindowBounds"));
                if (boundsDict == IntPtr.Zero || !CGRectMakeWithDictionaryRepresentation(boundsDict, out var b) || !b.Contains(at.X, at.Y)) continue;
                return (uint)CFLong(CFDictionaryGetValue(info, Key("kCGWindowNumber")));
            }
        }
        finally { CFRelease(list); }
        return 0;
    }

    /// <summary>The app and title of the window in front (not Overskin's own).</summary>
    public static (string App, string Title) FrontWindow()
    {
        var list = CGWindowListCopyWindowInfo(1 | 16, 0);
        if (list == IntPtr.Zero) return ("", "");
        try
        {
            int me = Environment.ProcessId;
            for (nint i = 0; i < CFArrayGetCount(list); i++)
            {
                var info = CFArrayGetValueAtIndex(list, i);
                if (CFLong(CFDictionaryGetValue(info, Key("kCGWindowLayer"))) != 0) continue;
                if (CFLong(CFDictionaryGetValue(info, Key("kCGWindowOwnerPID"))) == me) continue;
                return (CFText(CFDictionaryGetValue(info, Key("kCGWindowOwnerName"))) ?? "", CFText(CFDictionaryGetValue(info, Key("kCGWindowName"))) ?? "");
            }
        }
        finally { CFRelease(list); }
        return ("", "");
    }

    /// <summary>The ordinary windows on screen, front first: title (or app) and bounds in global points.</summary>
    public static List<(string Title, CGRect Bounds)> Windows()
    {
        var result = new List<(string, CGRect)>();
        // on screen only, no desktop pictures and icons
        var list = CGWindowListCopyWindowInfo(1 | 16, 0);
        if (list == IntPtr.Zero) return result;
        try
        {
            int me = Environment.ProcessId;
            nint n = CFArrayGetCount(list);
            for (nint i = 0; i < n; i++)
            {
                var info = CFArrayGetValueAtIndex(list, i);
                if (CFLong(CFDictionaryGetValue(info, Key("kCGWindowLayer"))) != 0) continue; // menus, the Dock, overlays
                if (CFLong(CFDictionaryGetValue(info, Key("kCGWindowOwnerPID"))) == me) continue;
                var boundsDict = CFDictionaryGetValue(info, Key("kCGWindowBounds"));
                if (boundsDict == IntPtr.Zero || !CGRectMakeWithDictionaryRepresentation(boundsDict, out var bounds)) continue;
                if (bounds.Width < 40 || bounds.Height < 40) continue;
                var title = CFText(CFDictionaryGetValue(info, Key("kCGWindowName")));
                var owner = CFText(CFDictionaryGetValue(info, Key("kCGWindowOwnerName"))) ?? "";
                result.Add((string.IsNullOrEmpty(title) ? owner : title, bounds));
            }
        }
        finally { CFRelease(list); }
        return result;
    }

    // ---- the Objective-C runtime ----------------------------------------------------------------

    [DllImport(ObjCLib)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjCLib)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr target, IntPtr sel);
    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")] private static extern double SendDouble(IntPtr target, IntPtr sel);

    /// <summary>The accent colour set in System Settings › Appearance, or null if it can't be read.</summary>
    public static Avalonia.Media.Color? AccentColour()
    {
        if (!IsMac) return null;
        try
        {
            var colour = Send(objc_getClass("NSColor"), sel_registerName("controlAccentColor"));
            var space = Send(objc_getClass("NSColorSpace"), sel_registerName("sRGBColorSpace"));
            var rgb = Send(colour, sel_registerName("colorUsingColorSpace:"), space);
            if (rgb == IntPtr.Zero) return null;
            byte C(string part) => (byte)Math.Clamp(Math.Round(SendDouble(rgb, sel_registerName(part)) * 255), 0, 255);
            return Avalonia.Media.Color.FromRgb(C("redComponent"), C("greenComponent"), C("blueComponent"));
        }
        catch { return null; }
    }
    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr target, IntPtr sel, IntPtr a);
    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr target, IntPtr sel, IntPtr a, IntPtr b);
    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr target, IntPtr sel, IntPtr a, nuint b);
    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")] private static extern IntPtr SendNint(IntPtr target, IntPtr sel, nint a);
    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")] private static extern IntPtr SendNuint(IntPtr target, IntPtr sel, nuint a);
    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")] private static extern IntPtr SendBool(IntPtr target, IntPtr sel, byte a);
    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")] private static extern byte SendBool1(IntPtr target, IntPtr sel, IntPtr a);
    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")] private static extern byte SendBool2(IntPtr target, IntPtr sel, IntPtr a, IntPtr b);
    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")] private static extern byte SendBool3(IntPtr target, IntPtr sel, IntPtr a, IntPtr b, IntPtr c);
    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")] private static extern void SendFrame(IntPtr target, IntPtr sel, CGRect frame, byte display);
    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")] private static extern IntPtr SendBytes(IntPtr target, IntPtr sel, byte* bytes, nuint length);
    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")] private static extern CGRect SendRect(IntPtr target, IntPtr sel);
    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")] private static extern IntPtr SendUintPtr(IntPtr target, IntPtr sel, nuint a, IntPtr b);
    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")] private static extern nuint SendReturnsUint(IntPtr target, IntPtr sel);
    // Intel Macs hand back structs this big through a hidden pointer, with their own entry point
    [DllImport(ObjCLib, EntryPoint = "objc_msgSend_stret")] private static extern void SendRectStret(out CGRect result, IntPtr target, IntPtr sel);

    private static IntPtr Cls(string name) => objc_getClass(name);
    private static IntPtr Sel(string name) => sel_registerName(name);

    /// <summary>An NSString (autoreleased) for some text.</summary>
    private static IntPtr NSString(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text + "\0");
        fixed (byte* b = bytes)
            return Send(Cls("NSString"), Sel("stringWithUTF8String:"), (IntPtr)b);
    }

    /// <summary>Runs <paramref name="work"/> inside its own autorelease pool, so nothing it makes leaks.</summary>
    private static T Pool<T>(Func<T> work)
    {
        var pool = Send(Send(Cls("NSAutoreleasePool"), Sel("alloc")), Sel("init"));
        try { return work(); }
        finally { Send(pool, Sel("drain")); }
    }

    // ---- windows of Overskin's own ---------------------------------------------------------------

    /// <summary>The NSWindow behind an Avalonia window's platform handle.</summary>
    public static IntPtr NSWindowOf(Avalonia.Platform.IPlatformHandle? handle)
    {
        if (handle == null || handle.Handle == IntPtr.Zero) return IntPtr.Zero;
        // Avalonia hands over the NSWindow, or on some versions its content NSView
        return handle.HandleDescriptor == "NSView" ? Send(handle.Handle, Sel("window")) : handle.Handle;
    }

    /// <summary>
    /// Puts a window exactly over a display (global points), above the menu bar and the Dock, and on
    /// every Space including over full-screen apps, the way a screenshot overlay must be.
    /// </summary>
    public static void CoverDisplay(IntPtr nsWindow, CGRect display)
    {
        if (nsWindow == IntPtr.Zero) return;
        // Cocoa counts y up from the bottom of the main display
        var main = MainDisplay;
        var frame = new CGRect(display.X, main.Height - (display.Y + display.Height), display.Width, display.Height);
        SendNint(nsWindow, Sel("setLevel:"), 1000 /* NSScreenSaverWindowLevel */);
        // all Spaces (1), over full-screen apps (256), and left out of Exposé (16)
        SendNuint(nsWindow, Sel("setCollectionBehavior:"), 1 | 16 | 256);
        SendFrame(nsWindow, Sel("setFrame:display:"), frame, 1);
    }

    /// <summary>Keeps a window (a pin, a toolbar) floating above ordinary windows, on every Space.</summary>
    public static void Float(IntPtr nsWindow)
    {
        if (nsWindow == IntPtr.Zero) return;
        SendNint(nsWindow, Sel("setLevel:"), 3 /* NSFloatingWindowLevel */);
        SendNuint(nsWindow, Sel("setCollectionBehavior:"), 1 | 256);
    }

    /// <summary>Where one of Overskin's windows is, in global points (top-left origin, like the pointer).</summary>
    public static CGRect FrameOf(IntPtr nsWindow)
    {
        if (nsWindow == IntPtr.Zero) return default;
        CGRect f;
        if (RuntimeInformation.ProcessArchitecture == Architecture.X64) SendRectStret(out f, nsWindow, Sel("frame"));
        else f = SendRect(nsWindow, Sel("frame"));
        return new CGRect(f.X, MainDisplay.Height - (f.Y + f.Height), f.Width, f.Height);
    }

    /// <summary>Lets clicks pass through a window to whatever is under it.</summary>
    public static void ClickThrough(IntPtr nsWindow, bool through)
    {
        if (nsWindow != IntPtr.Zero) SendBool(nsWindow, Sel("setIgnoresMouseEvents:"), (byte)(through ? 1 : 0));
    }

    // ---- the clipboard -------------------------------------------------------------------------

    /// <summary>Puts a picture on the clipboard as PNG and TIFF (older apps only take TIFF).</summary>
    public static bool CopyPng(byte[] png) => Pool(() =>
    {
        var board = Send(Cls("NSPasteboard"), Sel("generalPasteboard"));
        if (board == IntPtr.Zero) return false;
        Send(board, Sel("clearContents"));
        IntPtr data;
        fixed (byte* b = png) data = SendBytes(Cls("NSData"), Sel("dataWithBytes:length:"), b, (nuint)png.Length);
        bool ok = SendBool2(board, Sel("setData:forType:"), data, NSString("public.png")) != 0;
        var image = Send(Send(Cls("NSImage"), Sel("alloc")), Sel("initWithData:"), data);
        if (image != IntPtr.Zero)
        {
            var tiff = Send(image, Sel("TIFFRepresentation"));
            if (tiff != IntPtr.Zero) ok |= SendBool2(board, Sel("setData:forType:"), tiff, NSString("public.tiff")) != 0;
            Send(image, Sel("release"));
        }
        return ok;
    });

    /// <summary>Puts files on the clipboard the way Finder does, so pasting gives the files themselves.</summary>
    public static bool CopyFiles(IReadOnlyList<string> paths) => Pool(() =>
    {
        var board = Send(Cls("NSPasteboard"), Sel("generalPasteboard"));
        if (board == IntPtr.Zero) return false;
        Send(board, Sel("clearContents"));
        var array = Send(Cls("NSMutableArray"), Sel("array"));
        foreach (var p in paths)
            Send(array, Sel("addObject:"), Send(Cls("NSURL"), Sel("fileURLWithPath:"), NSString(Path.GetFullPath(p))));
        return SendBool1(board, Sel("writeObjects:"), array) != 0;
    });

    /// <summary>
    /// The picture on the clipboard as PNG bytes: a copied picture (PNG or TIFF), or a copied picture
    /// file from Finder. Null when there's none.
    /// </summary>
    public static byte[]? PastePicture() => Pool(() =>
    {
        var board = Send(Cls("NSPasteboard"), Sel("generalPasteboard"));
        if (board == IntPtr.Zero) return null;
        var data = Send(board, Sel("dataForType:"), NSString("public.png"));
        if (data == IntPtr.Zero)
        {
            var tiff = Send(board, Sel("dataForType:"), NSString("public.tiff"));
            if (tiff != IntPtr.Zero)
            {
                var rep = Send(Cls("NSBitmapImageRep"), Sel("imageRepWithData:"), tiff);
                if (rep != IntPtr.Zero)
                    data = SendUintPtr(rep, Sel("representationUsingType:properties:"), 4 /* PNG */, Send(Cls("NSDictionary"), Sel("dictionary")));
            }
        }
        if (data != IntPtr.Zero) return Bytes(data);
        // a file copied in Finder: its address, as text
        var url = Send(board, Sel("dataForType:"), NSString("public.file-url"));
        if (url == IntPtr.Zero) return null;
        var text = System.Text.Encoding.UTF8.GetString(Bytes(url)).TrimEnd('\0');
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || !uri.IsFile || !File.Exists(uri.LocalPath)) return null;
        return File.ReadAllBytes(uri.LocalPath);
    });

    private static byte[] Bytes(IntPtr nsData)
    {
        var length = (int)SendReturnsUint(nsData, Sel("length"));
        var bytes = new byte[length];
        if (length > 0) Marshal.Copy(Send(nsData, Sel("bytes")), bytes, 0, length);
        return bytes;
    }

    /// <summary>Moves one of Overskin's windows to a place given in global points (top-left origin).</summary>
    public static void Place(IntPtr nsWindow, CGRect global)
    {
        if (nsWindow == IntPtr.Zero) return;
        var frame = new CGRect(global.X, MainDisplay.Height - (global.Y + global.Height), global.Width, global.Height);
        SendFrame(nsWindow, Sel("setFrame:display:"), frame, 1);
    }

    public static bool CopyText(string text) => Pool(() =>
    {
        var board = Send(Cls("NSPasteboard"), Sel("generalPasteboard"));
        if (board == IntPtr.Zero) return false;
        Send(board, Sel("clearContents"));
        return SendBool2(board, Sel("setString:forType:"), NSString(text), NSString("public.utf8-plain-text")) != 0;
    });

    // ---- the Trash -----------------------------------------------------------------------------

    /// <summary>Moves a file to the Trash, so it can be put back. False if macOS wouldn't.</summary>
    public static bool Trash(string path) => Pool(() =>
    {
        var manager = Send(Cls("NSFileManager"), Sel("defaultManager"));
        var url = Send(Cls("NSURL"), Sel("fileURLWithPath:"), NSString(Path.GetFullPath(path)));
        return SendBool3(manager, Sel("trashItemAtURL:resultingItemURL:error:"), url, IntPtr.Zero, IntPtr.Zero) != 0;
    });

    // ---- global hotkeys (Carbon) -----------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)] private struct EventTypeSpec { public uint EventClass, EventKind; }
    [StructLayout(LayoutKind.Sequential)] private struct EventHotKeyID { public uint Signature, Id; }

    [DllImport(Carbon)] private static extern IntPtr GetApplicationEventTarget();
    [DllImport(Carbon)] private static extern int InstallEventHandler(IntPtr target, IntPtr handler, uint count, EventTypeSpec* types, IntPtr userData, out IntPtr handlerRef);
    [DllImport(Carbon)] private static extern int RegisterEventHotKey(uint keyCode, uint modifiers, EventHotKeyID id, IntPtr target, uint options, out IntPtr hotKey);
    [DllImport(Carbon)] private static extern int UnregisterEventHotKey(IntPtr hotKey);
    [DllImport(Carbon)] private static extern int GetEventParameter(IntPtr evt, uint name, uint type, IntPtr actualType, nuint size, IntPtr actualSize, EventHotKeyID* data);

    private static uint Code(string four) => (uint)(four[0] << 24 | four[1] << 16 | four[2] << 8 | four[3]);

    private static bool _handlerInstalled;
    private static readonly Dictionary<uint, (IntPtr Ref, Action Run)> HotKeys = new();
    private static uint _nextId = 1;

    /// <summary>Raised on a hotkey, from macOS's main thread (Avalonia's UI thread).</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int OnHotKey(IntPtr callRef, IntPtr evt, IntPtr userData)
    {
        try
        {
            EventHotKeyID id;
            if (GetEventParameter(evt, Code("----"), Code("hkid"), IntPtr.Zero, (nuint)sizeof(EventHotKeyID), IntPtr.Zero, &id) == 0
                && HotKeys.TryGetValue(id.Id, out var hk))
                Avalonia.Threading.Dispatcher.UIThread.Post(hk.Run);
        }
        catch (Exception ex) { Log.Write("hotkey: " + ex.Message); }
        return 0;
    }

    /// <summary>
    /// Registers a global hotkey such as "Ctrl+Alt+S" (Alt is ⌥ Option, Cmd is ⌘). Works without any
    /// permission. False if the text isn't a hotkey or another app already has it.
    /// </summary>
    public static bool AddHotKey(string text, Action run)
    {
        if (!TryParseHotKey(text, out uint key, out uint modifiers)) return false;
        if (!_handlerInstalled)
        {
            var spec = new EventTypeSpec { EventClass = Code("keyb"), EventKind = 5 /* hot key pressed */ };
            delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int> handler = &OnHotKey;
            if (InstallEventHandler(GetApplicationEventTarget(), (IntPtr)handler, 1, &spec, IntPtr.Zero, out _) != 0) return false;
            _handlerInstalled = true;
        }
        uint id = _nextId++;
        if (RegisterEventHotKey(key, modifiers, new EventHotKeyID { Signature = Code("OVSK"), Id = id }, GetApplicationEventTarget(), 0, out var hotKey) != 0)
            return false;
        HotKeys[id] = (hotKey, run);
        return true;
    }

    public static void ClearHotKeys()
    {
        foreach (var (_, hk) in HotKeys) UnregisterEventHotKey(hk.Ref);
        HotKeys.Clear();
    }

    /// <summary>"Ctrl+Alt+S" as a Mac key code and Carbon modifiers.</summary>
    public static bool TryParseHotKey(string text, out uint key, out uint modifiers)
    {
        key = 0;
        modifiers = 0;
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return false;
        foreach (var m in parts[..^1])
        {
            switch (m.ToLowerInvariant())
            {
                case "ctrl" or "control" or "⌃": modifiers |= 1 << 12; break;
                case "alt" or "option" or "opt" or "⌥": modifiers |= 1 << 11; break;
                case "shift" or "⇧": modifiers |= 1 << 9; break;
                case "cmd" or "command" or "win" or "⌘": modifiers |= 1 << 8; break;
                default: return false;
            }
        }
        if (modifiers == 0) return false;
        var name = parts[^1].ToUpperInvariant();
        if (!KeyCodes.TryGetValue(name, out ushort code)) return false;
        key = code;
        return true;
    }

    /// <summary>The Mac's key codes (ANSI layout) for the keys a hotkey can end in.</summary>
    private static readonly Dictionary<string, ushort> KeyCodes = new()
    {
        ["A"] = 0, ["S"] = 1, ["D"] = 2, ["F"] = 3, ["H"] = 4, ["G"] = 5, ["Z"] = 6, ["X"] = 7, ["C"] = 8, ["V"] = 9,
        ["B"] = 11, ["Q"] = 12, ["W"] = 13, ["E"] = 14, ["R"] = 15, ["Y"] = 16, ["T"] = 17, ["1"] = 18, ["2"] = 19,
        ["3"] = 20, ["4"] = 21, ["6"] = 22, ["5"] = 23, ["9"] = 25, ["7"] = 26, ["8"] = 28, ["0"] = 29, ["O"] = 31,
        ["U"] = 32, ["I"] = 34, ["P"] = 35, ["L"] = 37, ["J"] = 38, ["K"] = 40, ["N"] = 45, ["M"] = 46,
        ["SPACE"] = 49, ["F1"] = 122, ["F2"] = 120, ["F3"] = 99, ["F4"] = 118, ["F5"] = 96, ["F6"] = 97, ["F7"] = 98,
        ["F8"] = 100, ["F9"] = 101, ["F10"] = 109, ["F11"] = 103, ["F12"] = 111,
    };

    /// <summary>A hotkey as the Mac shows it: ⌃⌥S.</summary>
    public static string Symbols(string text) =>
        string.Concat(text.Split('+', StringSplitOptions.TrimEntries).Select(p => p.ToLowerInvariant() switch
        {
            "ctrl" or "control" => "⌃", "alt" or "option" or "opt" => "⌥", "shift" => "⇧", "cmd" or "command" or "win" => "⌘",
            _ => p.ToUpperInvariant(),
        }));
}
