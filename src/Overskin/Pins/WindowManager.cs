using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static Overskin.NativeMethods;

namespace Overskin;

/// <summary>A top-level window as shown in the list.</summary>
public sealed record WindowInfo(IntPtr Handle, string Title, ImageSource? Icon);

/// <summary>What a window currently looks like from our point of view.</summary>
public readonly record struct TargetState(bool Pinned, int Opacity, bool ClickThrough, bool Modified);

/// <summary>Outcome of an attempt to change another window.</summary>
public readonly record struct OpResult(bool Ok, string? Error)
{
    public static readonly OpResult Success = new(true, null);
    public static OpResult Fail(string message) => new(false, message);
}

/// <summary>
/// Enumerates top-level windows and applies pin / opacity / click-through to them,
/// remembering each window's original state so it can be put back exactly.
/// </summary>
public sealed class WindowManager
{
    private sealed class Tracked
    {
        public IntPtr Handle;
        public bool OriginalLayered;
        public bool OriginalTransparent;
        public bool OriginalTopmost;
        public bool HadLayeredAttributes;
        public uint OriginalColorKey;
        public byte OriginalAlpha = 255;
        public uint OriginalLayeredFlags;

        public bool Pinned;
        public int Opacity = 100;
        public bool ClickThrough;

        public bool IsDefault => Pinned == OriginalTopmost && Opacity == 100 && ClickThrough == OriginalTransparent;
    }

    private readonly Dictionary<IntPtr, Tracked> _tracked = new();
    private readonly Dictionary<IntPtr, ImageSource?> _iconCache = new();
    private readonly uint _ownPid = (uint)Environment.ProcessId;

    /// <summary>
    /// Our own click-through windows (pinned images). WindowFromPoint skips them, so
    /// <see cref="WindowAt"/> checks these by rectangle instead.
    /// </summary>
    public HashSet<IntPtr> OwnClickThroughWindows { get; } = new();

    public int ModifiedCount => _tracked.Count;

    public bool IsOwnWindow(IntPtr hWnd)
    {
        GetWindowThreadProcessId(hWnd, out uint pid);
        return pid == _ownPid;
    }

    // ---- enumeration ---------------------------------------------------------------------

    public List<WindowInfo> ListWindows()
    {
        var result = new List<WindowInfo>();
        EnumWindows((hWnd, _) =>
        {
            if (IsListable(hWnd))
                result.Add(new WindowInfo(hWnd, WindowTitle(hWnd), GetIcon(hWnd)));
            return true;
        }, IntPtr.Zero);

        // forget icons of windows that are gone
        var alive = result.Select(w => w.Handle).ToHashSet();
        foreach (var key in _iconCache.Keys.Where(k => !alive.Contains(k)).ToList())
            _iconCache.Remove(key);

        Prune();
        return result;
    }

    private bool IsListable(IntPtr hWnd)
    {
        if (!IsWindowVisible(hWnd)) return false;
        if (IsCloaked(hWnd)) return false;
        if (IsOwnWindow(hWnd)) return false;

        long ex = GetExStyle(hWnd);
        if ((ex & WS_EX_TOOLWINDOW) != 0) return false;

        // owned windows (dialogs, palettes) only count when they ask for a taskbar button
        if (GetWindow(hWnd, GW_OWNER) != IntPtr.Zero && (ex & WS_EX_APPWINDOW) == 0) return false;

        if (GetWindowTextLength(hWnd) == 0) return false;

        string cls = ClassName(hWnd);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;

        return true;
    }

    private ImageSource? GetIcon(IntPtr hWnd)
    {
        if (_iconCache.TryGetValue(hWnd, out var cached)) return cached;

        IntPtr hIcon = IntPtr.Zero;
        foreach (int kind in new[] { ICON_SMALL2, ICON_SMALL, ICON_BIG })
        {
            SendMessageTimeout(hWnd, WM_GETICON, new IntPtr(kind), IntPtr.Zero, SMTO_ABORTIFHUNG, 40, out hIcon);
            if (hIcon != IntPtr.Zero) break;
        }
        if (hIcon == IntPtr.Zero) hIcon = GetClassLongPtr(hWnd, GCLP_HICONSM);
        if (hIcon == IntPtr.Zero) hIcon = GetClassLongPtr(hWnd, GCLP_HICON);

        ImageSource? icon = null;
        if (hIcon != IntPtr.Zero)
        {
            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                icon = src;
            }
            catch
            {
                // an icon handle can die between asking for it and reading it
            }
        }
        _iconCache[hWnd] = icon;
        return icon;
    }

    // ---- hit testing ---------------------------------------------------------------------

    /// <summary>
    /// Top-level window under a screen point. Uses WindowFromPoint + GetAncestor(GA_ROOT), and also
    /// finds windows we made click-through, which WindowFromPoint cannot see any more.
    /// </summary>
    internal IntPtr WindowAt(POINT pt)
    {
        IntPtr hit = WindowFromPoint(pt);
        IntPtr root = hit == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hit, GA_ROOT);

        bool anyHidden = OwnClickThroughWindows.Count > 0 || _tracked.Values.Any(t => t.ClickThrough);
        if (!anyHidden) return root;

        // EnumWindows walks the z-order top to bottom: the first match is the one on top.
        IntPtr found = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            if (hWnd == root) { found = hWnd; return false; }
            bool hidden = OwnClickThroughWindows.Contains(hWnd)
                          || (_tracked.TryGetValue(hWnd, out var t) && t.ClickThrough);
            if (hidden && IsWindowVisible(hWnd) && !IsIconic(hWnd) && VisibleBounds(hWnd).Contains(pt))
            {
                found = hWnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);

        return found != IntPtr.Zero ? found : root;
    }

    // ---- state ---------------------------------------------------------------------------

    public TargetState GetState(IntPtr hWnd)
    {
        if (_tracked.TryGetValue(hWnd, out var t))
            return new TargetState(t.Pinned, t.Opacity, t.ClickThrough, true);

        long ex = GetExStyle(hWnd);
        return new TargetState((ex & WS_EX_TOPMOST) != 0, 100, (ex & WS_EX_TRANSPARENT) != 0, false);
    }

    public OpResult SetPinned(IntPtr hWnd, bool pinned)
    {
        if (!IsWindow(hWnd)) return Gone();

        if (!pinned)
        {
            if (!SetTopmost(hWnd, false, out int err)) return Denied(hWnd, err);
            // Unpinning hands the window back exactly as we found it.
            if (_tracked.TryGetValue(hWnd, out var existing))
            {
                existing.Pinned = false;
                RestoreStyle(existing);
                _tracked.Remove(hWnd);
            }
            return OpResult.Success;
        }

        var t = Track(hWnd, out bool isNew);
        if (!SetTopmost(hWnd, true, out int error) || (GetExStyle(hWnd) & WS_EX_TOPMOST) == 0)
        {
            if (isNew) _tracked.Remove(hWnd);
            return Denied(hWnd, error);
        }
        t.Pinned = true;
        return OpResult.Success;
    }

    public OpResult SetOpacity(IntPtr hWnd, int percent)
    {
        if (!IsWindow(hWnd)) return Gone();
        percent = Math.Clamp(percent, 10, 100);

        var t = Track(hWnd, out bool isNew);
        int previous = t.Opacity;
        t.Opacity = percent;

        var result = Apply(t);
        if (!result.Ok)
        {
            t.Opacity = previous;
            if (isNew) _tracked.Remove(hWnd);
            return result;
        }
        ReleaseIfDefault(t);
        return OpResult.Success;
    }

    public OpResult SetClickThrough(IntPtr hWnd, bool enabled)
    {
        if (!IsWindow(hWnd)) return Gone();

        var t = Track(hWnd, out bool isNew);
        bool previous = t.ClickThrough;
        t.ClickThrough = enabled;

        var result = Apply(t);
        if (!result.Ok)
        {
            t.ClickThrough = previous;
            if (isNew) _tracked.Remove(hWnd);
            return result;
        }
        ReleaseIfDefault(t);
        return OpResult.Success;
    }

    /// <summary>Puts one window back to how it was before we touched it.</summary>
    public void Restore(IntPtr hWnd)
    {
        if (!_tracked.TryGetValue(hWnd, out var t)) return;
        _tracked.Remove(hWnd);
        if (!IsWindow(hWnd)) return;

        RestoreStyle(t);
        if (t.Pinned != t.OriginalTopmost) SetTopmost(hWnd, t.OriginalTopmost, out _);
    }

    /// <summary>Puts every modified window back. Called on exit.</summary>
    public void RestoreAll()
    {
        foreach (var hWnd in _tracked.Keys.ToList())
        {
            try { Restore(hWnd); }
            catch { /* keep going: the remaining windows still need restoring */ }
        }
        _tracked.Clear();
    }

    /// <summary>Drops records of windows that have been closed.</summary>
    public void Prune()
    {
        foreach (var hWnd in _tracked.Keys.Where(h => !IsWindow(h)).ToList())
            _tracked.Remove(hWnd);
    }

    // ---- internals -----------------------------------------------------------------------

    private Tracked Track(IntPtr hWnd, out bool isNew)
    {
        if (_tracked.TryGetValue(hWnd, out var t))
        {
            isNew = false;
            return t;
        }

        long ex = GetExStyle(hWnd);
        t = new Tracked
        {
            Handle = hWnd,
            OriginalLayered = (ex & WS_EX_LAYERED) != 0,
            OriginalTransparent = (ex & WS_EX_TRANSPARENT) != 0,
            OriginalTopmost = (ex & WS_EX_TOPMOST) != 0,
        };
        if (t.OriginalLayered && GetLayeredWindowAttributes(hWnd, out uint key, out byte alpha, out uint flags))
        {
            t.HadLayeredAttributes = true;
            t.OriginalColorKey = key;
            t.OriginalAlpha = (flags & LWA_ALPHA) != 0 ? alpha : (byte)255;
            t.OriginalLayeredFlags = flags;
        }
        t.Pinned = t.OriginalTopmost;
        t.ClickThrough = t.OriginalTransparent;

        _tracked[hWnd] = t;
        isNew = true;
        return t;
    }

    /// <summary>Pushes the tracked opacity and click-through onto the real window.</summary>
    private OpResult Apply(Tracked t)
    {
        IntPtr hWnd = t.Handle;
        long ex = GetExStyle(hWnd);
        bool needLayered = t.OriginalLayered || t.Opacity < 100 || t.ClickThrough;

        long want = ex;
        if (needLayered) want |= WS_EX_LAYERED; else want &= ~WS_EX_LAYERED;
        if (t.ClickThrough) want |= WS_EX_TRANSPARENT; else want &= ~WS_EX_TRANSPARENT;

        if (!needLayered && (ex & WS_EX_LAYERED) != 0)
            SetLayeredWindowAttributes(hWnd, 0, 255, LWA_ALPHA);

        if (want != ex)
        {
            if (!TrySetExStyle(hWnd, want, out int err)) return Denied(hWnd, err);
            if ((GetExStyle(hWnd) & (WS_EX_LAYERED | WS_EX_TRANSPARENT)) != (want & (WS_EX_LAYERED | WS_EX_TRANSPARENT)))
                return Denied(hWnd, ERROR_ACCESS_DENIED);
        }

        if (needLayered)
        {
            // Keep a colour key the app set itself, and scale against any alpha it already had.
            uint flags = LWA_ALPHA | (t.OriginalLayeredFlags & LWA_COLORKEY);
            byte alpha = (byte)Math.Clamp((int)Math.Round(t.OriginalAlpha * t.Opacity / 100.0), 1, 255);

            bool ownLayer = t.OriginalLayered && !t.HadLayeredAttributes; // draws itself with UpdateLayeredWindow
            if (!ownLayer || t.Opacity < 100)
            {
                if (!SetLayeredWindowAttributes(hWnd, t.OriginalColorKey, alpha, flags))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == ERROR_ACCESS_DENIED) return Denied(hWnd, err);
                    return OpResult.Fail($"“{ShortTitle(hWnd)}” draws its own transparency, so its opacity can't be changed.");
                }
            }
        }
        else
        {
            Repaint(hWnd);
        }

        return OpResult.Success;
    }

    /// <summary>Restores the two style bits we touch, and the original layered attributes.</summary>
    private static void RestoreStyle(Tracked t)
    {
        IntPtr hWnd = t.Handle;
        if (!IsWindow(hWnd)) return;

        long ex = GetExStyle(hWnd);
        const long ours = WS_EX_LAYERED | WS_EX_TRANSPARENT;
        long original = (t.OriginalLayered ? WS_EX_LAYERED : 0) | (t.OriginalTransparent ? WS_EX_TRANSPARENT : 0);
        long want = (ex & ~ours) | original;

        if (t.OriginalLayered)
        {
            if (t.HadLayeredAttributes)
                SetLayeredWindowAttributes(hWnd, t.OriginalColorKey, t.OriginalAlpha, t.OriginalLayeredFlags);
        }
        else if ((ex & WS_EX_LAYERED) != 0)
        {
            SetLayeredWindowAttributes(hWnd, 0, 255, LWA_ALPHA);
        }

        if (want != ex) TrySetExStyle(hWnd, want, out _);
        Repaint(hWnd);
    }

    private void ReleaseIfDefault(Tracked t)
    {
        if (!t.IsDefault) return;
        RestoreStyle(t);
        _tracked.Remove(t.Handle);
    }

    private static bool SetTopmost(IntPtr hWnd, bool topmost, out int error)
    {
        bool ok = SetWindowPos(hWnd, topmost ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        error = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
    }

    private static void Repaint(IntPtr hWnd) =>
        RedrawWindow(hWnd, IntPtr.Zero, IntPtr.Zero, RDW_ERASE | RDW_INVALIDATE | RDW_FRAME | RDW_ALLCHILDREN);

    private static string ShortTitle(IntPtr hWnd)
    {
        string title = WindowTitle(hWnd);
        if (title.Length == 0) return "This window";
        return title.Length > 38 ? title[..36].TrimEnd() + "…" : title;
    }

    private static OpResult Gone() => OpResult.Fail("That window has been closed.");

    private static OpResult Denied(IntPtr hWnd, int error)
    {
        string name = ShortTitle(hWnd);
        if (error is ERROR_ACCESS_DENIED or 0)
            return OpResult.Fail($"Can't change “{name}”. It runs as administrator; start Overskin as administrator to control it.");
        return OpResult.Fail($"Windows refused to change “{name}” (error {error}).");
    }
}
