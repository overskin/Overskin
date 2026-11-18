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
/// these have no see-through corners or soft shadow; <see cref="Alpha"/> fades the whole window.
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
    }

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
