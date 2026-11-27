using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Overskin.Linux.Interface;

/// <summary>The little bar shown while recording: time so far, Stop and Cancel. Drag it anywhere.</summary>
public sealed class RecordBar : Window
{
    private readonly TextBlock _time = new() { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, MinWidth = 44 };
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DateTime _started = DateTime.Now;

    private Button? _pauseButton, _penButton;
    private bool _paused;
    private TimeSpan _pausedFor;
    private DateTime _pausedAt;

    /// <param name="stopAfter">Seconds after which the recording stops by itself (0: never).</param>
    /// <param name="pause">Pauses or carries on (null when this recording can't pause).</param>
    /// <param name="pen">Turns drawing on the screen on or off (null when there's no pen).</param>
    public RecordBar(bool gif, Action stop, Action cancel, int stopAfter = 0, Action? pause = null, Action? pen = null)
    {
        bool stopped = false;
        Title = "Overskin recording";
        SystemDecorations = SystemDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = new SolidColorBrush(Colours.Panel);

        var dot = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(Color.Parse("#EF4444")), VerticalAlignment = VerticalAlignment.Center };
        var stopButton = Ui.Button(gif ? "Stop and make the GIF" : "Stop", stop, Icons.Stop, "primary");
        var cancelButton = Ui.IconButton(Icons.Close, "Cancel: throw this recording away", cancel);
        var buttons = new List<Control> { dot, _time, stopButton };
        if (pause != null)
        {
            _pauseButton = Ui.IconButton(Icons.Pause, "Pause  (" + Hotkey(App.Settings.HotkeyPause) + ")", pause);
            buttons.Add(_pauseButton);
        }
        if (pen != null)
        {
            _penButton = Ui.IconButton(Icons.Pen, "Draw on the screen while it records  (" + Hotkey(App.Settings.HotkeyDraw) + ")", pen);
            buttons.Add(_penButton);
        }
        buttons.Add(cancelButton);
        Content = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 6, 6, 6),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children = { },
            },
        };
        foreach (var b in buttons) ((StackPanel)((Border)Content!).Child!).Children.Add(b);
        _tick.Tick += (_, _) =>
        {
            if (_paused) { dot.Opacity = 0.35; return; }
            var t = DateTime.Now - _started - _pausedFor;
            _time.Text = stopAfter > 0
                ? $"{(int)t.TotalMinutes}:{t.Seconds:00} / {stopAfter / 60}:{stopAfter % 60:00}"
                : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
            if (stopAfter > 0 && t.TotalSeconds >= stopAfter && !stopped)
            {
                stopped = true;
                stop();
            }
            dot.Opacity = t.Milliseconds < 500 ? 1 : 0.35;
        };
        _tick.Start();
        _time.Text = "0:00";

        PointerPressed += (_, e) => { if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.Source is not Button) BeginMoveDrag(e); };
        Opened += (_, _) =>
        {
            // bottom middle of the main screen
            var area = Screens.Primary?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
            var scale = Screens.Primary?.Scaling ?? 1;
            Position = new PixelPoint(area.X + (int)((area.Width - Bounds.Width * scale) / 2), area.Bottom - (int)((Bounds.Height + 40) * scale));
        };
        Closed += (_, _) => _tick.Stop();
    }

    private static string Hotkey(string keys) => OperatingSystem.IsMacOS() ? Overskin.Linux.Platform.Mac.Symbols(keys) : keys;

    /// <summary>Shows whether it's paused (the clock stops while it is).</summary>
    public void ShowPaused(bool paused)
    {
        if (paused == _paused) return;
        _paused = paused;
        if (paused) _pausedAt = DateTime.Now; else _pausedFor += DateTime.Now - _pausedAt;
        if (_pauseButton != null)
        {
            _pauseButton.Content = Ui.Icon(paused ? Icons.Play : Icons.Pause, 18);
            ToolTip.SetTip(_pauseButton, (paused ? "Carry on" : "Pause") + "  (" + Hotkey(App.Settings.HotkeyPause) + ")");
        }
    }

    /// <summary>Shows whether the pen is on.</summary>
    public void ShowPen(bool on)
    {
        if (_penButton != null) _penButton.Background = on ? new SolidColorBrush(Colours.Accent) : null;
    }
}
