using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Overskin.Linux.Interface;

/// <summary>
/// The seconds left before a delayed screenshot, in a small badge in the bottom-right corner. It
/// doesn't take the focus, so menus can be opened meanwhile, and it's gone before the picture.
/// </summary>
public sealed class DelayBadge : Window
{
    private readonly TextBlock _number = new() { FontSize = 30, FontWeight = FontWeight.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center };

    private DelayBadge()
    {
        Title = "Overskin";
        SystemDecorations = SystemDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        Width = 96;
        Height = 96;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        WindowStartupLocation = WindowStartupLocation.Manual;
        IsHitTestVisible = false;
        Content = new Border
        {
            Width = 80, Height = 80, CornerRadius = new CornerRadius(40),
            Background = new SolidColorBrush(Color.FromArgb(215, 18, 19, 22)),
            BorderBrush = new SolidColorBrush(Colours.Accent), BorderThickness = new Thickness(2),
            Child = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children = { _number, new TextBlock { Text = "capture", FontSize = 10, Foreground = new SolidColorBrush(Colours.Dim), HorizontalAlignment = HorizontalAlignment.Center } },
            },
        };
        Opened += (_, _) =>
        {
            var area = Screens.Primary?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
            var scale = Screens.Primary?.Scaling ?? 1;
            Position = new PixelPoint(area.Right - (int)(120 * scale), area.Bottom - (int)(120 * scale));
        };
    }

    /// <summary>Counts down <paramref name="seconds"/>, then closes (and waits a moment so it's off the screen).</summary>
    public static async Task RunAsync(int seconds)
    {
        if (seconds <= 0) return;
        var badge = new DelayBadge();
        badge.Show();
        for (int left = seconds; left > 0; left--)
        {
            badge._number.Text = left.ToString();
            await Task.Delay(1000);
        }
        badge.Close();
        await Task.Delay(150);
    }
}
