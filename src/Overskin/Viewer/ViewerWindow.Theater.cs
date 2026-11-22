using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;
using static Overskin.NativeMethods;

namespace Overskin;

/// <summary>
/// Where the viewer opens (beside Overskin's window, not on top of it) and full screen: the picture
/// or video on black over the whole monitor, taskbar included, with nothing else in the way.
/// </summary>
public sealed partial class ViewerWindow
{
    private FrameworkElement _titleBar = null!, _toolStrip = null!, _filmBorder = null!;
    private bool _theater;
    private RECT _beforeTheater;
    private WindowState _stateBeforeTheater;
    private bool _topmostBeforeTheater;
    private Visibility _filmBeforeTheater;
    private Brush? _stageBeforeTheater;
    private readonly Button _exitTheater = new() { Width = 40, Height = 40, ToolTip = "Leave full screen  (Esc)", Visibility = Visibility.Collapsed };
    private readonly DispatcherTimer _theaterIdle = new() { Interval = TimeSpan.FromSeconds(2) };

    private void BuildTheater()
    {
        _exitTheater.SetResourceReference(StyleProperty, "BtnIcon");
        _exitTheater.Content = new Overskin.Icon { Data = (Geometry)FindResource("IcMinimize2"), Width = 18, Height = 18, Foreground = Brushes.White };
        _exitTheater.Background = new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0));
        _exitTheater.HorizontalAlignment = HorizontalAlignment.Right;
        _exitTheater.VerticalAlignment = VerticalAlignment.Top;
        _exitTheater.Margin = new Thickness(0, 16, 16, 0);
        _exitTheater.Click += (_, _) => ToggleTheater();
        _stage.Children.Add(_exitTheater);
        // the button shows while the mouse moves and tucks away when it rests
        _theaterIdle.Tick += (_, _) =>
        {
            _theaterIdle.Stop();
            if (_theater && !_exitTheater.IsMouseOver)
            {
                _exitTheater.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(400)));
                _stage.Cursor = Cursors.None;
            }
        };
        _stage.MouseMove += (_, _) =>
        {
            if (!_theater) return;
            _exitTheater.BeginAnimation(OpacityProperty, null);
            _exitTheater.Opacity = 1;
            if (_stage.Cursor == Cursors.None) _stage.Cursor = null;
            _theaterIdle.Stop();
            _theaterIdle.Start();
        };
        Closed += (_, _) => _theaterIdle.Stop();
    }

    private void ToggleTheater()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || _frame == null) return;
        var chrome = WindowChrome.GetWindowChrome(this);
        if (!_theater)
        {
            _stateBeforeTheater = WindowState;
            _topmostBeforeTheater = Topmost;
            if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
            GetWindowRect(handle, out _beforeTheater);
            var monitor = MonitorBounds(new POINT((_beforeTheater.Left + _beforeTheater.Right) / 2, (_beforeTheater.Top + _beforeTheater.Bottom) / 2));
            _theater = true;
            _filmBeforeTheater = _filmBorder.Visibility;
            _titleBar.Visibility = _toolStrip.Visibility = _filmBorder.Visibility = Visibility.Collapsed;
            _frame.Margin = new Thickness(0);
            _frame.BorderThickness = new Thickness(0);
            _frame.CornerRadius = new CornerRadius(0);
            _stageBeforeTheater = _stage.Background;
            _stage.Background = Brushes.Black;
            if (chrome != null) { chrome.CaptionHeight = 0; chrome.ResizeBorderThickness = new Thickness(0); }
            int square = 1;
            DwmSetWindowAttribute(handle, 33, ref square, sizeof(int));
            Topmost = true;
            SetWindowPos(handle, HWND_TOPMOST, monitor.Left, monitor.Top, monitor.Width, monitor.Height, SWP_FRAMECHANGED);
            _exitTheater.Visibility = Visibility.Visible;
            _exitTheater.Opacity = 1;
            _theaterIdle.Start();
        }
        else
        {
            _theater = false;
            _theaterIdle.Stop();
            _exitTheater.Visibility = Visibility.Collapsed;
            _stage.Cursor = null;
            _titleBar.Visibility = _toolStrip.Visibility = Visibility.Visible;
            _filmBorder.Visibility = _filmBeforeTheater;
            _frame.BorderThickness = new Thickness(1);
            _frame.SetResourceReference(Border.CornerRadiusProperty, "RadiusWindow");
            _stage.Background = _stageBeforeTheater;
            _stage.SetResourceReference(Grid.BackgroundProperty, "Stage");
            if (chrome != null) { chrome.CaptionHeight = 44; chrome.ResizeBorderThickness = new Thickness(6); }
            int round = 2;
            DwmSetWindowAttribute(handle, 33, ref round, sizeof(int));
            Topmost = _topmostBeforeTheater;
            var r = _beforeTheater;
            SetWindowPos(handle, _topmostBeforeTheater ? HWND_TOPMOST : HWND_NOTOPMOST, r.Left, r.Top, r.Width, r.Bottom - r.Top, SWP_FRAMECHANGED);
            if (_stateBeforeTheater == WindowState.Maximized) WindowState = WindowState.Maximized;
            OnStateChanged(EventArgs.Empty);
        }
        if (_fit) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => FitNow(animate: false));
    }

    /// <summary>
    /// Opens beside <paramref name="main"/> on the side with more room, so it doesn't land on top
    /// of Overskin. With no room either side it goes in the middle of the screen instead, and stays
    /// above Overskin even when that's set to stay on top.
    /// </summary>
    public void PlaceBeside(Window main)
    {
        var handle = new WindowInteropHelper(main).Handle;
        if (handle == IntPtr.Zero || !main.IsVisible || main.WindowState == WindowState.Minimized) return;
        GetWindowRect(handle, out var m);
        var work = MonitorWorkArea(new POINT((m.Left + m.Right) / 2, (m.Top + m.Bottom) / 2));
        var dpi = VisualTreeHelper.GetDpi(main);
        double sx = dpi.DpiScaleX, sy = dpi.DpiScaleY;
        // all in this window's units (device-independent pixels)
        double workL = work.Left / sx, workT = work.Top / sy, workR = work.Right / sx, workB = work.Bottom / sy;
        double mainL = m.Left / sx, mainR = m.Right / sx, mainT = m.Top / sy;
        const double gap = 12;
        double roomRight = workR - mainR - gap * 2, roomLeft = mainL - workL - gap * 2;
        double height = Math.Min(Height, workB - workT - gap * 2);
        WindowStartupLocation = WindowStartupLocation.Manual;
        double room = Math.Max(roomRight, roomLeft);
        if (room >= MinWidth)
        {
            Width = Math.Min(Width, room);
            Height = height;
            Left = roomRight >= roomLeft ? mainR + gap : mainL - gap - Width;
            Top = Math.Clamp(mainT, workT + gap, Math.Max(workT + gap, workB - gap - Height));
            return;
        }
        Width = Math.Min(Width, workR - workL - gap * 2);
        Height = height;
        Left = workL + (workR - workL - Width) / 2;
        Top = workT + (workB - workT - Height) / 2;
        if (main.Topmost) Topmost = true;
    }

#if DEBUG
    internal bool TheaterForTest => _theater;
    internal void ToggleTheaterForTest() => ToggleTheater();
#endif
}
