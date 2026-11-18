using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using static Overskin.NativeMethods;

namespace Overskin;

/// <summary>
/// Small message near the bottom of the primary screen. It never takes focus, clicks pass
/// straight through it, and it never shows up in screenshots or recordings.
/// </summary>
public static class Toast
{
    private static ToastWindow? _window;

    /// <summary>Shows a message that fades out by itself.</summary>
    public static void Show(string message, bool warning = false, double seconds = 2.8)
    {
        Window().Display(message, warning, TimeSpan.FromSeconds(warning ? Math.Max(seconds, 4.5) : seconds));
    }

    /// <summary>Shows a message that stays until replaced or hidden (countdown, progress).</summary>
    public static void ShowSticky(string message) => Window().Display(message, false, null);

    /// <summary>A message with a colour sample instead of the usual dot.</summary>
    public static void ShowColour(string message, Color colour) => Window().Display(message, false, TimeSpan.FromSeconds(3.5), colour);

    public static void Hide() => _window?.FadeOut();

    /// <summary>True when the message window is being kept out of captures.</summary>
    public static bool IsGuarded => _window?.IsGuarded == true;

    /// <summary>Gone this instant, without fading: used right before taking a screenshot.</summary>
    public static void HideNow() => _window?.HideNow();

    public static void Shutdown()
    {
        _window?.Close();
        _window = null;
    }

    private static ToastWindow Window() => _window ??= new ToastWindow();

    private sealed class ToastWindow : GuardedWindow
    {
        private readonly TextBlock _text;
        private readonly Border _dot;
        private readonly DispatcherTimer _timer = new();

        public ToastWindow() : base(clickThrough: true)
        {
            Focusable = false;
            IsHitTestVisible = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            Alpha = 0;

            _dot = new Border
            {
                Width = 6,
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(0, 0, 9, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            _text = new TextBlock
            {
                FontFamily = (FontFamily)Application.Current.Resources["UiFont"],
                FontSize = 12.5,
                MaxWidth = 440,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(_dot);
            row.Children.Add(_text);

            var box = new Border
            {
                BorderThickness = new Thickness(1),
                Padding = new Thickness(13, 9, 14, 9),
                Child = row,
                UseLayoutRounding = true,
            };
            // resource references rather than fixed brushes, so the toast follows a style change
            box.SetResourceReference(Border.BackgroundProperty, "Panel");
            box.SetResourceReference(Border.BorderBrushProperty, "LineStrong");
            _text.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            Content = box;

            _timer.Tick += (_, _) => FadeOut();
        }

        public void Display(string message, bool warning, TimeSpan? duration, Color? swatch = null)
        {
            _text.Text = message;
            if (swatch is { } c)
            {
                _dot.Background = new SolidColorBrush(c);
                _dot.Width = _dot.Height = 14;
                _dot.CornerRadius = new CornerRadius(3);
                _dot.BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF));
                _dot.BorderThickness = new Thickness(1);
            }
            else
            {
                _dot.SetResourceReference(Border.BackgroundProperty, warning ? "Warn" : "Accent");
                _dot.Width = _dot.Height = 6;
                _dot.CornerRadius = new CornerRadius(3);
                _dot.BorderThickness = new Thickness(0);
            }

            if (!IsVisible) Show();
            UpdateLayout();

            var area = SystemParameters.WorkArea;
            Left = area.Left + (area.Width - ActualWidth) / 2;
            Top = area.Bottom - ActualHeight - 36;

            BeginAnimation(AlphaProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));

            _timer.Stop();
            if (duration is { } d)
            {
                _timer.Interval = d;
                _timer.Start();
            }
        }

        public void HideNow()
        {
            _timer.Stop();
            BeginAnimation(AlphaProperty, null);
            Alpha = 0;
            Hide();
        }

        public void FadeOut()
        {
            _timer.Stop();
            if (!IsVisible) return;
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220));
            fade.Completed += (_, _) =>
            {
                if (Alpha <= 0.01 && !_timer.IsEnabled) Hide();
            };
            BeginAnimation(AlphaProperty, fade);
        }
    }
}
