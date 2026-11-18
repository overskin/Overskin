using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Threading;
using static Overskin.NativeMethods;

namespace Overskin;

/// <summary>A colour in the target program's palette, and where on screen to click to choose it.</summary>
public sealed class PaletteSwatch
{
    public int X { get; set; }
    public int Y { get; set; }
    public string Hex { get; set; } = "#000000";
    /// <summary>Left blank when drawing (usually the canvas colour).</summary>
    public bool Skip { get; set; }

    public Color Color => Theming.TryParseHex(Hex, out var c) ? c : Colors.Black;
}

/// <summary>
/// "Pick from screen" for palettes: while active, every left click elsewhere on screen is
/// swallowed and reported with the colour under it. Right-click (or <see cref="Stop"/>) ends it.
/// Clicks on Overskin's own windows go through normally.
/// </summary>
public sealed class PaletteCapture : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Func<IntPtr, bool> _isOwnWindow;
    private readonly LowLevelMouseProc _proc; // kept in a field so the GC can't collect the callback
    private IntPtr _hook;
    private bool _swallowUp;

    /// <summary>Raised on the UI thread with the click position and the colour there.</summary>
    public event Action<int, int, Color>? Picked;

    /// <summary>Raised on the UI thread when capture ends.</summary>
    public event Action? Ended;

    public bool IsActive => _hook != IntPtr.Zero;

    public PaletteCapture(Dispatcher dispatcher, Func<IntPtr, bool> isOwnWindow)
    {
        _dispatcher = dispatcher;
        _isOwnWindow = isOwnWindow;
        _proc = HookProc;
    }

    public bool Start()
    {
        if (IsActive) return true;
        _swallowUp = false;
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
        return _hook != IntPtr.Zero;
    }

    public void Stop()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        Ended?.Invoke();
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

            if (msg == WM_LBUTTONDOWN)
            {
                IntPtr hit = WindowFromPoint(data.pt);
                IntPtr root = hit == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hit, GA_ROOT);
                if (root != IntPtr.Zero && _isOwnWindow(root))
                    return CallNextHookEx(_hook, nCode, wParam, lParam);

                // The click never reaches the program, so the swatch looks the same when we read it.
                // Reading the pixel happens after the hook returns, to keep the hook fast.
                int x = data.pt.X, y = data.pt.Y;
                _swallowUp = true;
                _dispatcher.BeginInvoke(() =>
                {
                    var colour = ScreenPixel(x, y);
                    if (colour != null) Picked?.Invoke(x, y, colour.Value);
                });
                return new IntPtr(1);
            }

            if (msg == WM_LBUTTONUP && _swallowUp)
            {
                _swallowUp = false;
                return new IntPtr(1);
            }

            // finish on right-click; unhook only after the release, so the program never sees half a click
            if (msg == WM_RBUTTONDOWN) return new IntPtr(1);
            if (msg == WM_RBUTTONUP)
            {
                _dispatcher.BeginInvoke(Stop);
                return new IntPtr(1);
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }
}
