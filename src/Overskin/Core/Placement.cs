using System.Windows;
using static Overskin.NativeMethods;

namespace Overskin;

/// <summary>Puts our own windows exactly where they belong, in physical pixels.</summary>
internal static class Placement
{
    /// <summary>
    /// Positions a WPF window in physical pixels. Done twice because moving onto a monitor with a
    /// different scale makes WPF resize the window once in response.
    /// </summary>
    public static void MoveTo(IntPtr hWnd, Int32Rect r, bool topmost)
    {
        IntPtr after = topmost ? HWND_TOPMOST : IntPtr.Zero;
        uint flags = SWP_NOACTIVATE | (topmost ? 0 : SWP_NOZORDER);
        SetWindowPos(hWnd, after, r.X, r.Y, r.Width, r.Height, flags);
        SetWindowPos(hWnd, after, r.X, r.Y, r.Width, r.Height, flags);
    }
}
