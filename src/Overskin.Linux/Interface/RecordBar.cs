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

    public RecordBar(bool gif, Action stop, Action cancel)
    {
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
        Content = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 6, 6, 6),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children = { dot, _time, stopButton, cancelButton },
            },
        };
        _tick.Tick += (_, _) =>
        {
            var t = DateTime.Now - _started;
            _time.Text = $"{(int)t.TotalMinutes}:{t.Seconds:00}";
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
}
