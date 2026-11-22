using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using static Overskin.NativeMethods;

namespace Overskin;

/// <summary>Notification-area icon built directly on Shell_NotifyIcon, with a WPF menu.</summary>
public sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = WM_APP + 41;

    private readonly HwndSource _source;
    private readonly ContextMenu _menu;
    private readonly uint _taskbarCreated;
    private IntPtr _hIcon;
    private bool _added;

    public event Action? ShowRequested;
    public event Action? ExitRequested;

    /// <param name="fillMenu">Adds the menu entries shown between "Show" and "Exit".</param>
    public TrayIcon(HwndSource source, string tooltip, Action<ItemCollection> fillMenu)
    {
        _source = source;
        _tooltip = tooltip;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _hIcon = LoadAppIcon(GetSystemMetrics(SM_CXSMICON));

        _fillMenu = fillMenu;
        _menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        Rebuild();

        _source.AddHook(WndProc);
        Add();
    }

    private readonly Action<ItemCollection> _fillMenu;

    /// <summary>Fills the menu again, e.g. after workflows were added or renamed.</summary>
    public void Rebuild()
    {
        _menu.Items.Clear();
        var show = new MenuItem { Header = "Show Overskin", FontWeight = FontWeights.SemiBold };
        show.Click += (_, _) => ShowRequested?.Invoke();
        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => ExitRequested?.Invoke();
        _menu.Items.Add(show);
        _menu.Items.Add(new Separator());
        int before = _menu.Items.Count;
        _fillMenu(_menu.Items);
        if (_menu.Items.Count > before) _menu.Items.Add(new Separator());
        _menu.Items.Add(exit);
        foreach (var item in _menu.Items.OfType<MenuItem>().Where(i => i.Items.Count > 0))
            item.SubmenuOpened += (_, e) => { if (ReferenceEquals(e.OriginalSource, item)) PlaceSubmenu(item); };
    }

    /// <summary>
    /// Every submenu opens the same way: to the right of its item and, as the menu sits by the
    /// taskbar, upwards from the item's bottom edge (downwards from its top when the menu is in the
    /// top half of the screen). Left to itself, WPF opens short submenus down and pushes tall ones
    /// up, so they jumped about.
    /// </summary>
    private static void PlaceSubmenu(MenuItem item)
    {
        if (item.Template?.FindName("PART_Popup", item) is not Popup popup) return;
        popup.Placement = PlacementMode.Custom;
        popup.HorizontalOffset = 0;
        popup.VerticalOffset = 0;
        popup.CustomPopupPlacementCallback = (popupSize, targetSize, _) => SubmenuPlacements(item, popupSize, targetSize);
    }

    /// <summary>Where a submenu goes: right of the item, then left of it if there's no room on the right.</summary>
    internal static CustomPopupPlacement[] SubmenuPlacements(MenuItem item, Size popupSize, Size targetSize)
    {
        // the callback works in device pixels; the popup has a 10 px margin for its shadow and a
        // 4 px padding plus a 1 px border before its first item
        var dpi = VisualTreeHelper.GetDpi(item);
        double k = dpi.DpiScaleY, inset = 15 * k, overlap = 8 * dpi.DpiScaleX;
        bool up = OpensUp(item);
        double y = up ? targetSize.Height - popupSize.Height + inset : -inset;
        return new[]
        {
            new CustomPopupPlacement(new Point(targetSize.Width - overlap, y), PopupPrimaryAxis.Horizontal),
            new CustomPopupPlacement(new Point(-popupSize.Width + overlap, y), PopupPrimaryAxis.Horizontal),
        };
    }

    /// <summary>True when the menu is in the bottom half of its screen (the usual taskbar), so submenus grow upwards.</summary>
    internal static bool OpensUp(FrameworkElement item)
    {
        try
        {
            var mid = item.PointToScreen(new Point(item.ActualWidth / 2, item.ActualHeight / 2));
            var work = MonitorWorkArea(new POINT((int)mid.X, (int)mid.Y));
            return mid.Y > (work.Top + work.Bottom) / 2.0;
        }
        catch
        {
            return true;
        }
    }

    private readonly string _tooltip;

    private NOTIFYICONDATA Data() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _source.Handle,
        uID = 1,
        uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
        uCallbackMessage = CallbackMessage,
        hIcon = _hIcon,
        szTip = _tooltip,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private void Add()
    {
        var data = Data();
        _added = Shell_NotifyIcon(NIM_ADD, ref data);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            switch (lParam.ToInt32() & 0xFFFF)
            {
                case WM_LBUTTONUP:
                case WM_LBUTTONDBLCLK:
                    ShowRequested?.Invoke();
                    break;
                case WM_RBUTTONUP:
                    OpenMenu();
                    break;
            }
            handled = true;
        }
        else if (msg == (int)_taskbarCreated)
        {
            Add(); // Explorer restarted and forgot about us
        }
        return IntPtr.Zero;
    }

    private void OpenMenu()
    {
        Rebuild(); // so it shows what's happening now (a recording to stop) and the current hotkeys
        _menu.IsOpen = true;
        // A tray menu only closes on an outside click if its window is the foreground one.
        if (PresentationSource.FromVisual(_menu) is HwndSource popup)
            SetForegroundWindow(popup.Handle);
    }

    /// <summary>Picks the closest size out of the embedded .ico and turns it into an HICON.</summary>
    public static IntPtr LoadAppIcon(int size)
    {
        try
        {
            var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/overskin.ico"));
            if (info == null) return IntPtr.Zero;
            using var ms = new MemoryStream();
            info.Stream.CopyTo(ms);
            byte[] ico = ms.ToArray();

            int count = BitConverter.ToUInt16(ico, 4);
            int bestOffset = 0, bestLength = 0, bestScore = int.MaxValue;
            for (int i = 0; i < count; i++)
            {
                int entry = 6 + i * 16;
                int width = ico[entry] == 0 ? 256 : ico[entry];
                int score = width >= size ? width - size : (size - width) * 4; // prefer scaling down
                if (score < bestScore)
                {
                    bestScore = score;
                    bestLength = BitConverter.ToInt32(ico, entry + 8);
                    bestOffset = BitConverter.ToInt32(ico, entry + 12);
                }
            }
            if (bestLength == 0) return IntPtr.Zero;

            byte[] image = new byte[bestLength];
            Buffer.BlockCopy(ico, bestOffset, image, 0, bestLength);
            return CreateIconFromResourceEx(image, (uint)bestLength, true, 0x00030000, size, size, 0);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        _source.RemoveHook(WndProc);
        if (_added)
        {
            var data = Data();
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }
        if (_hIcon != IntPtr.Zero)
        {
            DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }
    }
}
