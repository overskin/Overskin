using System.Windows.Input;
using System.Windows.Interop;

namespace Overskin;

/// <summary>A key combination such as Ctrl+Alt+T. Stored in settings as text.</summary>
public readonly record struct Hotkey(ModifierKeys Modifiers, Key Key)
{
    public bool IsValid => Key != Key.None;

    public override string ToString()
    {
        var parts = new List<string>(4);
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var mods = ModifierKeys.None;
        var key = Key.None;
        foreach (string raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= ModifierKeys.Control; break;
                case "alt": mods |= ModifierKeys.Alt; break;
                case "shift": mods |= ModifierKeys.Shift; break;
                case "win" or "windows": mods |= ModifierKeys.Windows; break;
                case "esc": key = Key.Escape; break;
                default:
                    if (raw.Length == 1 && char.IsDigit(raw[0])) key = Key.D0 + (raw[0] - '0');
                    else if (!Enum.TryParse(raw, true, out key)) return false;
                    break;
            }
        }
        if (key == Key.None) return false;
        hotkey = new Hotkey(mods, key);
        return true;
    }

    private static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
        Key.Escape => "Esc",
        _ => key.ToString(),
    };
}

/// <summary>Thin wrapper over RegisterHotKey bound to one window handle.</summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _handlers = new();

    public HotkeyManager(HwndSource source)
    {
        _source = source;
        _source.AddHook(WndProc);
    }

    /// <summary>Registers a hotkey. Returns false when another program already owns the combination.</summary>
    public bool Register(int id, Hotkey hotkey, Action handler, bool allowRepeat = false)
    {
        Unregister(id);
        if (!hotkey.IsValid) return false;

        uint mods = (uint)hotkey.Modifiers; // ModifierKeys values match MOD_ALT / MOD_CONTROL / MOD_SHIFT / MOD_WIN
        if (!allowRepeat) mods |= NativeMethods.MOD_NOREPEAT;
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(hotkey.Key);

        if (!NativeMethods.RegisterHotKey(_source.Handle, id, mods, vk)) return false;
        _handlers[id] = handler;
        return true;
    }

    public void Unregister(int id)
    {
        if (_handlers.Remove(id))
            NativeMethods.UnregisterHotKey(_source.Handle, id);
    }

    public void UnregisterAll()
    {
        foreach (int id in _handlers.Keys.ToList()) Unregister(id);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && _handlers.TryGetValue(wParam.ToInt32(), out var handler))
        {
            handled = true;
            handler();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
    }
}
