using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace Overskin;

/// <summary>
/// "Match Windows": the accent colour Windows uses, and the see-through, blurred window
/// background of Windows 11 (acrylic), with Windows' own rounded corners and shadow.
/// </summary>
internal static class WindowLook
{
    /// <summary>Windows 11 (build 22000 and later) draws the see-through background itself.</summary>
    public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

    /// <summary>Windows 10 (from the April 2018 update) can blur behind a window too, the older way.</summary>
    public static bool GlassSupported => Environment.OSVersion.Version.Build >= 17134;

    /// <summary>The accent colour chosen in Windows' settings, lightened a step on dark themes so it reads well; null if unknown.</summary>
    public static Color? WindowsAccent(bool darkTheme)
    {
        try
        {
            var ui = new Windows.UI.ViewManagement.UISettings();
            var c = ui.GetColorValue(darkTheme ? Windows.UI.ViewManagement.UIColorType.AccentLight1 : Windows.UI.ViewManagement.UIColorType.Accent);
            return Color.FromRgb(c.R, c.G, c.B);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Raised (on any thread) when the Windows accent colour changes.</summary>
    public static event Action? AccentChanged;
    private static Windows.UI.ViewManagement.UISettings? _watch;

    /// <summary>Starts listening for the Windows accent colour changing.</summary>
    public static void Watch()
    {
        if (_watch != null) return;
        try
        {
            _watch = new Windows.UI.ViewManagement.UISettings();
            _watch.ColorValuesChanged += (_, _) => AccentChanged?.Invoke();
        }
        catch { _watch = null; }
    }

    /// <summary>
    /// Sets a window up for the see-through background. Call before the window has a handle: it
    /// can't then be a layered (AllowsTransparency) window.
    /// </summary>
    public static void PrepareGlass(Window w)
    {
        w.AllowsTransparency = false;
        w.WindowStyle = WindowStyle.SingleBorderWindow; // Windows' corners and shadow; the chrome below hides the frame
        WindowChrome.SetWindowChrome(w, new WindowChrome
        {
            CaptionHeight = 0,
            GlassFrameThickness = new Thickness(-1),
            ResizeBorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });
        w.SourceInitialized += (_, _) => Apply(w, !Theming.IsLight);
    }

    /// <summary>
    /// Windows 10: the window keeps its own rounded outline and see-through edges, and Windows
    /// blurs what's behind it inside that outline only (cut to shape, so no square frame shows).
    /// Kept up to date as the window is resized, scaled or maximised.
    /// </summary>
    public static void BlurInside(Window w, System.Windows.Controls.Border frame)
    {
        void Update()
        {
            var handle = new WindowInteropHelper(w).Handle;
            if (handle == IntPtr.Zero || !frame.IsLoaded || frame.ActualWidth < 2) return;
            var source = PresentationSource.FromVisual(w);
            if (source?.CompositionTarget == null) return;
            var toDevice = source.CompositionTarget.TransformToDevice;
            var box = frame.TransformToAncestor(w).TransformBounds(new Rect(frame.RenderSize));
            double scale = box.Width / Math.Max(1, frame.ActualWidth) * toDevice.M11;
            box = new Rect(box.X * toDevice.M11, box.Y * toDevice.M22, box.Width * toDevice.M11, box.Height * toDevice.M22);
            var r = w.WindowState == WindowState.Maximized ? new CornerRadius(0) : frame.CornerRadius;
            var points = Outline(box, r, scale);
            var region = CreatePolygonRgn(points, points.Length, 2);
            if (SetWindowRgn(handle, region, true) == 0) DeleteObject(region); // once set, Windows owns it
            BlurBehind(handle);
        }
        w.SourceInitialized += (_, _) => Update();
        frame.Loaded += (_, _) => Update();
        frame.SizeChanged += (_, _) => Update();
        w.SizeChanged += (_, _) => Update();
        w.StateChanged += (_, _) => w.Dispatcher.BeginInvoke(Update, System.Windows.Threading.DispatcherPriority.Render);
        w.DpiChanged += (_, _) => Update();
    }

    /// <summary>The outline of a box with these corners, as points in device pixels.</summary>
    internal static RPOINT[] Outline(Rect b, CornerRadius r, double scale)
    {
        var list = new List<RPOINT>();
        void Corner(double cx, double cy, double radius, double from)
        {
            radius *= scale;
            if (radius < 1)
            {
                list.Add(new RPOINT { X = (int)Math.Round(cx), Y = (int)Math.Round(cy) });
                return;
            }
            // the corner's centre sits inside the box by the radius
            double ox = cx + (from is 180 or 90 ? radius : -radius), oy = cy + (from is 180 or 270 ? radius : -radius);
            int steps = Math.Max(4, (int)(radius / 2));
            for (int i = 0; i <= steps; i++)
            {
                double a = (from + 90.0 * i / steps) * Math.PI / 180;
                list.Add(new RPOINT { X = (int)Math.Round(ox + radius * Math.Cos(a)), Y = (int)Math.Round(oy + radius * Math.Sin(a)) });
            }
        }
        Corner(b.Left, b.Top, r.TopLeft, 180);
        Corner(b.Right, b.Top, r.TopRight, 270);
        Corner(b.Right, b.Bottom, r.BottomRight, 0);
        Corner(b.Left, b.Bottom, r.BottomLeft, 90);
        return list.ToArray();
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RPOINT { public int X, Y; }

    [DllImport("gdi32.dll")] private static extern IntPtr CreatePolygonRgn(RPOINT[] points, int count, int fillMode);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

    /// <summary>Turns the see-through background on (again), dark or light to suit the theme.</summary>
    public static void Apply(Window w, bool dark)
    {
        var handle = new WindowInteropHelper(w).Handle;
        if (handle == IntPtr.Zero) return;
        if (!IsWindows11)
        {
            BlurBehind(handle); // the outline is kept by BlurInside
            return;
        }
        if (HwndSource.FromHwnd(handle) is { CompositionTarget: { } target }) target.BackgroundColor = Colors.Transparent;
        var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(handle, ref margins);
        int on = dark ? 1 : 0;
        DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        int round = DWMWCP_ROUND;
        DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        int backdrop = DWMSBT_TRANSIENTWINDOW; // acrylic: blurred and see-through
        if (DwmSetWindowAttribute(handle, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)) != 0)
        {
            int mica = 1; // the first Windows 11 release only knows the older switch
            DwmSetWindowAttribute(handle, DWMWA_MICA_EFFECT, ref mica, sizeof(int));
        }
    }

    /// <summary>
    /// Windows 10's blur: whatever is behind the window shows through, blurred. (Its "acrylic"
    /// variant makes dragging the window stutter, so the plain blur is used.)
    /// </summary>
    private static void BlurBehind(IntPtr handle)
    {
        var accent = new ACCENT_POLICY { AccentState = ACCENT_ENABLE_BLURBEHIND };
        int size = Marshal.SizeOf(accent);
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new WINDOWCOMPOSITIONATTRIBDATA { Attribute = WCA_ACCENT_POLICY, Data = ptr, SizeOfData = size };
            SetWindowCompositionAttribute(handle, ref data);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    private const int WCA_ACCENT_POLICY = 19, ACCENT_ENABLE_BLURBEHIND = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct ACCENT_POLICY { public int AccentState, AccentFlags, GradientColor, AnimationId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWCOMPOSITIONATTRIBDATA { public int Attribute; public IntPtr Data; public int SizeOfData; }

    [DllImport("user32.dll")] private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WINDOWCOMPOSITIONATTRIBDATA data);

    /// <summary>A colour made see-through, so the blurred background shows.</summary>
    public static SolidColorBrush Clear(Color c, double alpha)
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)Math.Round(alpha * 255), c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_SYSTEMBACKDROP_TYPE = 38, DWMWA_MICA_EFFECT = 1029;
    private const int DWMWCP_ROUND = 2, DWMSBT_TRANSIENTWINDOW = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
