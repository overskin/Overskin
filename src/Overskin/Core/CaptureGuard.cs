using System.Windows;
using System.Windows.Interop;

namespace Overskin;

/// <summary>
/// Keeps Overskin's own floating windows (messages, the recording bar, cards and outlines) out of
/// every screenshot and recording, whoever takes it.
/// </summary>
public static class CaptureGuard
{
    /// <summary>Call once the window has a handle (OnSourceInitialized or later).</summary>
    public static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero) NativeMethods.ExcludeFromCapture(handle);
    }

    /// <summary>Applies as soon as the window gets its handle.</summary>
    public static void Protect(Window window)
    {
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Apply(window);
        else window.SourceInitialized += (_, _) => Apply(window);
    }
}

/// <summary>
/// A floating window that never appears in screenshots or recordings. Windows can only hide
/// windows drawn as one solid piece (with at most one transparency for the whole window), so
/// these have no soft shadow, and their rounded corners come from <see cref="Corners"/>, which
/// shapes the window itself; <see cref="Alpha"/> fades the whole window.
/// </summary>
public class GuardedWindow : Window
{
    private readonly bool _clickThrough;
    private IntPtr _handle;
    private double _alpha = 1;

    public static readonly DependencyProperty AlphaProperty = DependencyProperty.Register(
        nameof(Alpha), typeof(double), typeof(GuardedWindow),
        new PropertyMetadata(1.0, (d, e) => ((GuardedWindow)d).SetAlpha((double)e.NewValue)));

    /// <summary>0 to 1; animatable.</summary>
    public double Alpha
    {
        get => (double)GetValue(AlphaProperty);
        set => SetValue(AlphaProperty, value);
    }

    public GuardedWindow(bool clickThrough)
    {
        _clickThrough = clickThrough;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = false;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        UseLayoutRounding = true;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _handle = new WindowInteropHelper(this).Handle;
        long ex = NativeMethods.GetExStyle(_handle) | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
        if (_clickThrough) ex |= NativeMethods.WS_EX_TRANSPARENT;
        NativeMethods.TrySetExStyle(_handle, ex, out _);
        SetAlpha(_alpha);
        NativeMethods.ExcludeFromCapture(_handle);
        ApplyShape();
    }

    /// <summary>
    /// Rounds the window itself (it can't have see-through corners), so a Border with the same
    /// CornerRadius inside it keeps Overskin's leaf shape.
    /// </summary>
    public CornerRadius Corners
    {
        get => _corners;
        set { _corners = value; ApplyShape(); }
    }
    private CornerRadius _corners;

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        ApplyShape();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ApplyShape();
    }

    private void ApplyShape()
    {
        if (_handle == IntPtr.Zero || ActualWidth <= 0 || ActualHeight <= 0) return;
        var c = _corners;
        if (c.TopLeft <= 0 && c.TopRight <= 0 && c.BottomRight <= 0 && c.BottomLeft <= 0)
        {
            SetWindowRgn(_handle, IntPtr.Zero, true);
            return;
        }
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        int w = (int)Math.Round(ActualWidth * dpi.DpiScaleX), h = (int)Math.Round(ActualHeight * dpi.DpiScaleY);
        var points = RoundedOutline(w, h, c.TopLeft * dpi.DpiScaleX, c.TopRight * dpi.DpiScaleX,
                                    c.BottomRight * dpi.DpiScaleX, c.BottomLeft * dpi.DpiScaleX);
        var region = CreatePolygonRgn(points, points.Length, 2 /* WINDING */);
        if (region != IntPtr.Zero && SetWindowRgn(_handle, region, true) == 0) DeleteObject(region); // on success Windows owns it
    }

    /// <summary>The outline of a rectangle with its own radius at each corner, as pixel points (clockwise from the top left).</summary>
    internal static Point32[] RoundedOutline(int w, int h, double tl, double tr, double br, double bl)
    {
        double limit = Math.Min(w, h) / 2.0;
        tl = Math.Min(tl, limit); tr = Math.Min(tr, limit); br = Math.Min(br, limit); bl = Math.Min(bl, limit);
        var list = new List<Point32>();
        void Arc(double cx, double cy, double r, double from)
        {
            if (r < 0.5)
            {
                list.Add(new Point32((int)Math.Round(cx + r * Math.Cos(from * Math.PI / 180 + Math.PI / 4)), (int)Math.Round(cy + r * Math.Sin(from * Math.PI / 180 + Math.PI / 4))));
                return;
            }
            int steps = Math.Max(4, (int)Math.Ceiling(r));
            for (int i = 0; i <= steps; i++)
            {
                double a = (from + 90.0 * i / steps) * Math.PI / 180;
                list.Add(new Point32((int)Math.Round(cx + r * Math.Cos(a)), (int)Math.Round(cy + r * Math.Sin(a))));
            }
        }
        // the region excludes its right and bottom edges, so the far side sits at w and h
        Arc(tl, tl, tl, 180);
        Arc(w - tr, tr, tr, 270);
        Arc(w - br, h - br, br, 0);
        Arc(bl, h - bl, bl, 90);
        return list.ToArray();
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal readonly record struct Point32(int X, int Y);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr CreatePolygonRgn(Point32[] points, int count, int fillMode);

    /// <summary>True when the window has its rounded shape (for the self-test).</summary>
    public bool IsShaped
    {
        get
        {
            if (_handle == IntPtr.Zero) return false;
            var region = CreateRectRgn(0, 0, 0, 0);
            try { return GetWindowRgn(_handle, region) == 3 /* COMPLEXREGION */; }
            finally { DeleteObject(region); }
        }
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowRgn(IntPtr hWnd, IntPtr region);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr region, bool redraw);

    private void SetAlpha(double value)
    {
        _alpha = Math.Clamp(value, 0, 1);
        if (_handle != IntPtr.Zero)
            NativeMethods.SetLayeredWindowAttributes(_handle, 0, (byte)Math.Round(_alpha * 255), NativeMethods.LWA_ALPHA);
    }

    /// <summary>True when Windows agreed to keep this window out of captures.</summary>
    public bool IsGuarded =>
        _handle != IntPtr.Zero && GetWindowDisplayAffinity(_handle, out uint a) && a == NativeMethods.WDA_EXCLUDEFROMCAPTURE;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowDisplayAffinity(IntPtr hWnd, out uint affinity);
}
