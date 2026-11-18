using System.Runtime.InteropServices;
using System.Windows.Threading;
using static Overskin.NativeMethods;

namespace Overskin;

/// <summary>
/// "Pick window" mode: the next left click anywhere on screen selects the window under it.
/// A low-level mouse hook is the only way to see (and swallow) a click meant for another program.
/// </summary>
public sealed class WindowPicker : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Func<IntPtr, bool> _isOwnWindow;
    private readonly LowLevelMouseProc _proc; // kept in a field so the GC can't collect the callback
    private IntPtr _hook;
    private bool _swallowUp;

    /// <summary>Raised on the UI thread with the picked top-level window, or Zero when cancelled.</summary>
    public event Action<IntPtr>? Finished;

    public bool IsActive => _hook != IntPtr.Zero;

    public WindowPicker(Dispatcher dispatcher, Func<IntPtr, bool> isOwnWindow)
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

    public void Cancel()
    {
        if (!IsActive) return;
        Stop();
        Finished?.Invoke(IntPtr.Zero);
    }

    private void Stop()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();

            if (msg == WM_LBUTTONDOWN && !_swallowUp)
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                IntPtr hit = WindowFromPoint(data.pt);
                IntPtr root = hit == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hit, GA_ROOT);

                if (root == IntPtr.Zero || _isOwnWindow(root))
                {
                    // clicking Overskin itself just leaves pick mode; let the click through
                    _dispatcher.BeginInvoke(Cancel);
                    return CallNextHookEx(_hook, nCode, wParam, lParam);
                }

                _swallowUp = true; // the target must not see half a click
                _dispatcher.BeginInvoke(() => Finished?.Invoke(root));
                return new IntPtr(1);
            }

            if (msg == WM_LBUTTONUP && _swallowUp)
            {
                _dispatcher.BeginInvoke(Stop);
                return new IntPtr(1);
            }

            if (msg == WM_RBUTTONDOWN && !_swallowUp)
            {
                _dispatcher.BeginInvoke(Cancel);
                return new IntPtr(1);
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose() => Stop();
}
