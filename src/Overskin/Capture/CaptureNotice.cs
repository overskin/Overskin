using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using static Overskin.NativeMethods;

namespace Overskin;

/// <summary>
/// The little card that slides in after a capture: a thumbnail and quick actions. It never
/// takes focus, so it can pop up over a game without interrupting it. Swipe the card (anywhere
/// but the thumbnail, which drags the picture out into other apps) left to open the capture in
/// the viewer, or right to send it away.
/// </summary>
public sealed class CaptureNotice : Window
{
    private static CaptureNotice? _current;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(7) };
    // the card (with its shadow) rises out of the corner, growing a little, and settles with a small bounce
    private readonly TranslateTransform _slide = new(0, 36);
    private readonly ScaleTransform _grow = new(0.86, 0.86);
    private bool _closing;

    // swiping: how far the card has to travel, or how fast it's flicked, to count
    internal const double SwipeDistance = 80, FlickSpeed = 0.7; // units, units per millisecond
    private readonly Action? _view;
    private readonly TextBlock _caption;
    private readonly string _captionText;
    private double _home = double.NaN, _grabX;
    private bool _swiping;
    private (double X, int Time) _last, _prev;

    public static void ShowFor(BitmapSource image, string? path, string caption,
        Action edit, Action pin, Action copy, Action? openFolder, Action? view = null)
    {
        _current?.FadeAway();
        _current = new CaptureNotice(image, path, caption, edit, pin, copy, openFolder, view);
        _current.Show();
    }

    public static void CloseAll() => _current?.Close();

    /// <summary>For the self-test: how far through its pop-up the shown card is (scale, rise, opacity).</summary>
    internal static (double Scale, double Rise, double Opacity)? StateForTest =>
        _current is { } c ? (c._grow.ScaleX, c._slide.Y, c.Opacity) : null;

    /// <summary>For the self-test: the shown card.</summary>
    internal static CaptureNotice? CurrentForTest => _current;

    private CaptureNotice(BitmapSource image, string? path, string caption,
        Action edit, Action pin, Action copy, Action? openFolder, Action? view)
    {
        _view = view;
        _captionText = caption;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = (FontFamily)FindResource("UiFont");
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        Opacity = 0;

        var thumb = new Image
        {
            Source = image, Stretch = Stretch.Uniform, MaxWidth = 290, MaxHeight = 150,
            Cursor = Cursors.Hand, ToolTip = "Click to edit, or drag it into any app",
        };
        RenderOptions.SetBitmapScalingMode(thumb, BitmapScalingMode.HighQuality);
        DragOut.Attach(thumb, () => path, () => image, () => { edit(); FadeAway(); });
        var thumbFrame = new Border
        {
            Child = thumb, Background = (Brush)FindResource("Bg"), CornerRadius = (CornerRadius)FindResource("RadiusSmall"),
            Padding = new Thickness(4), MinHeight = 60, HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var text = _caption = new TextBlock
        {
            Text = caption, Style = (Style)FindResource("Dim"), Margin = new Thickness(2, 9, 0, 8), FontSize = 11.5,
            ToolTip = view != null ? "Swipe the card left to open it, or right to dismiss it" : "Swipe the card right to dismiss it",
        };

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(Action("IcPencil", "Edit", edit));
        actions.Children.Add(Action("IcPin", "Pin", pin));
        actions.Children.Add(Action("IcCopy", "Copy", copy));
        if (openFolder != null) actions.Children.Add(Action("IcFolderOpen", "Show in folder", openFolder));

        var close = new Button { Style = (Style)FindResource("BtnIcon"), ToolTip = "Dismiss", HorizontalAlignment = HorizontalAlignment.Right };
        close.Content = new Icon { Data = (Geometry)FindResource("IcX"), Width = 13, Height = 13 };
        close.Click += (_, _) => FadeAway();

        var bottom = new Grid();
        bottom.Children.Add(actions);
        bottom.Children.Add(close);

        var stack = new StackPanel { Width = 300 };
        stack.Children.Add(thumbFrame);
        stack.Children.Add(text);
        stack.Children.Add(bottom);

        var card = new Border
        {
            Child = stack,
            Background = (Brush)FindResource("Panel"),
            BorderBrush = (Brush)FindResource("LineStrong"),
            BorderThickness = new Thickness(1),
            CornerRadius = (CornerRadius)FindResource("RadiusCard"),
            Padding = new Thickness(10, 10, 8, 8),
            Margin = new Thickness(14),
        };
        var shadow = new Border
        {
            Background = Brushes.Black, Opacity = 0.5, Margin = new Thickness(16, 18, 12, 10),
            CornerRadius = (CornerRadius)FindResource("RadiusCard"),
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 14 },
        };
        var root = new Grid
        {
            RenderTransformOrigin = new Point(1, 1),
            RenderTransform = new TransformGroup { Children = { _grow, _slide } },
        };
        root.Children.Add(shadow);
        root.Children.Add(card);
        Content = root;

        // the card itself (not the thumbnail, which drags the picture out, or the buttons) swipes
        card.MouseLeftButtonDown += (_, e) =>
        {
            if (_closing || IsInside(e.OriginalSource as DependencyObject, thumb)) return;
            if (!card.CaptureMouse()) return;
            e.Handled = true;
            _grabX = e.GetPosition(this).X;
            BeginSwipe(Environment.TickCount);
        };
        card.MouseMove += (_, e) =>
        {
            if (!_swiping || !card.IsMouseCaptured) return;
            // the window moves with the pointer, so where the pointer is in it says how far to go
            MoveSwipe(Left - _home + e.GetPosition(this).X - _grabX, Environment.TickCount);
        };
        card.MouseLeftButtonUp += (_, e) =>
        {
            if (!_swiping) return;
            e.Handled = true;
            card.ReleaseMouseCapture();
        };
        card.LostMouseCapture += (_, _) => { if (_swiping) EndSwipe(Environment.TickCount); };

        _timer.Tick += (_, _) => { if (!_swiping) FadeAway(); };
        MouseEnter += (_, _) => _timer.Stop();
        MouseLeave += (_, _) => { if (!_closing && !_swiping) _timer.Start(); };
    }

    private static bool IsInside(DependencyObject? d, DependencyObject outer)
    {
        for (; d != null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (ReferenceEquals(d, outer)) return true;
        return false;
    }

    /// <summary>The card is picked up: it stops counting down and follows the pointer sideways.</summary>
    internal void BeginSwipe(int time)
    {
        if (_closing) return;
        _timer.Stop();
        if (double.IsNaN(_home)) _home = Left;
        // catch it mid-way if it's still springing back
        double at = Left, seen = Opacity;
        BeginAnimation(LeftProperty, null);
        Left = at;
        BeginAnimation(OpacityProperty, null);
        Opacity = seen;
        _swiping = true;
        _last = _prev = (at - _home, time);
    }

    /// <summary>The card is <paramref name="dx"/> units from where it sat (left is negative).</summary>
    internal void MoveSwipe(double dx, int time)
    {
        if (!_swiping) return;
        // nothing to open: it leans left a little and no further
        if (dx < 0 && _view == null) dx = -Math.Min(Math.Sqrt(-dx) * 3, 30);
        Left = _home + dx;
        // Going right it fades as it goes. Going left it fades too as it nears the point where
        // letting go opens the viewer, and is faint once it's there.
        Opacity = dx > 0 ? Math.Clamp(1 - dx / (ActualWidth * 0.9), 0.15, 1)
            : _view == null ? 1
            : Math.Clamp(1 - 0.65 * (-dx - SwipeDistance * 0.4) / (SwipeDistance * 0.6), 0.35, 1);
        if (time != _last.Time) _prev = _last;
        _last = (dx, time);
        _caption.Text = Outcome(dx, 0) switch
        {
            1 => "Let go to dismiss",
            -1 => "Let go to open in the viewer",
            _ => _captionText,
        };
    }

    /// <summary>What letting go here does: 1 dismisses, -1 opens the viewer, 0 puts the card back.</summary>
    private int Outcome(double dx, double speed)
    {
        bool far = Math.Abs(dx) >= SwipeDistance;
        bool flick = Math.Abs(dx) >= SwipeDistance * 0.3 && Math.Abs(speed) >= FlickSpeed && Math.Sign(speed) == Math.Sign(dx);
        if (!far && !flick) return 0;
        if (dx > 0) return 1;
        return _view != null ? -1 : 0;
    }

    /// <summary>Let go: dismissed, opened, or back into its corner.</summary>
    internal int EndSwipe(int time)
    {
        if (!_swiping) return 0;
        _swiping = false;
        double dx = Left - _home;
        int dt = Math.Max(1, _last.Time - _prev.Time);
        // only a movement just before letting go counts as a flick, not one before a pause
        double speed = time - _last.Time > 80 ? 0 : (_last.X - _prev.X) / dt;
        int outcome = Outcome(dx, speed);
        _caption.Text = _captionText;
        if (outcome == 1)
        {
            _closing = true;
            var away = TimeSpan.FromMilliseconds(170);
            var slide = new DoubleAnimation(_home + ActualWidth + 40, away) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            BeginAnimation(LeftProperty, slide);
            var fade = new DoubleAnimation(0, away);
            fade.Completed += (_, _) => Close();
            BeginAnimation(OpacityProperty, fade);
        }
        else if (outcome == -1)
        {
            try { _view!(); } catch { }
            FadeAway();
        }
        else
        {
            var back = new DoubleAnimation(_home, TimeSpan.FromMilliseconds(260)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 } };
            back.Completed += (_, _) =>
            {
                if (_swiping || _closing) return;
                BeginAnimation(LeftProperty, null);
                Left = _home;
            };
            BeginAnimation(LeftProperty, back);
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
            if (!IsMouseOver) _timer.Start();
        }
        return outcome;
    }

    private Button Action(string icon, string tip, Action act)
    {
        var b = new Button { Style = (Style)FindResource("BtnIcon"), Width = 30, Height = 28, ToolTip = tip, Margin = new Thickness(0, 0, 2, 0) };
        b.Content = new Icon { Data = (Geometry)FindResource(icon), Width = 15, Height = 15 };
        b.Click += (_, _) =>
        {
            act();
            if (tip is "Edit" or "Pin") FadeAway();
        };
        return b;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr h = new WindowInteropHelper(this).Handle;
        TrySetExStyle(h, GetExStyle(h) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, out _);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 4;
        Top = area.Bottom - ActualHeight - 4;
        _home = Left;

        var settle = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };
        var rise = TimeSpan.FromMilliseconds(340);
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(170)));
        _slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, rise) { EasingFunction = settle });
        _grow.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, rise) { EasingFunction = settle });
        _grow.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, rise) { EasingFunction = settle });
        _timer.Start();
    }

    private void FadeAway()
    {
        if (_closing) return;
        _closing = true;
        _timer.Stop();
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
        var sink = new CubicEase { EasingMode = EasingMode.EaseIn };
        _slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(20, TimeSpan.FromMilliseconds(160)) { EasingFunction = sink });
        _grow.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, TimeSpan.FromMilliseconds(160)) { EasingFunction = sink });
        _grow.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, TimeSpan.FromMilliseconds(160)) { EasingFunction = sink });
    }

    protected override void OnClosed(EventArgs e)
    {
        if (ReferenceEquals(_current, this)) _current = null;
        base.OnClosed(e);
    }
}

/// <summary>Shows text read from the screen, already copied, editable before copying again.</summary>
public sealed class TextResultWindow : Window
{
    public TextResultWindow(string text, bool qr = false)
    {
        Title = qr ? "QR code" : "Text from screen";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        Width = 460;
        Height = qr ? 230 : 330;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = (FontFamily)FindResource("UiFont");
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        var heading = new StackPanel { Orientation = Orientation.Horizontal };
        heading.Children.Add(new Icon { Data = (Geometry)FindResource(qr ? "IcScan" : "IcScanText"), Width = 15, Height = 15, Foreground = (Brush)FindResource("Accent"), Margin = new Thickness(0, 0, 8, 0) });
        heading.Children.Add(new TextBlock { Text = qr ? "QR code" : "Text from screen", Style = (Style)FindResource("Heading") });
        heading.Children.Add(new TextBlock { Text = "Copied", Style = (Style)FindResource("Dim"), Margin = new Thickness(8, 1, 0, 0) });
        heading.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        heading.Background = Brushes.Transparent;

        var box = new TextBox
        {
            Text = text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Foreground = (Brush)FindResource("Text"), CaretBrush = (Brush)FindResource("Text"),
            SelectionBrush = (Brush)FindResource("Accent"), Background = (Brush)FindResource("Bg"),
            BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 6, 8, 6), FontSize = 12.5, Margin = new Thickness(0, 10, 0, 10),
        };

        var copy = new Button { Style = (Style)FindResource("BtnAccent"), Content = "Copy", MinWidth = 76 };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(box.Text); Toast.Show("Copied."); } catch { Toast.Show("The clipboard is busy. Try again.", true); }
            Close();
        };
        var close = new Button { Style = (Style)FindResource("Btn"), Content = "Close", Margin = new Thickness(0, 0, 6, 0) };
        close.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        // a code that's a web address can be opened straight away
        if (qr && QrReader.LinkIn(text) is { } link)
        {
            var open = new Button { Style = (Style)FindResource("Btn"), Content = "Open link", Margin = new Thickness(0, 0, 6, 0), ToolTip = link.AbsoluteUri };
            open.Click += (_, _) =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true }); } catch { }
                Close();
            };
            buttons.Children.Add(open);
        }
        buttons.Children.Add(close);
        buttons.Children.Add(copy);

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(box, 1);
        Grid.SetRow(buttons, 2);
        grid.Children.Add(heading);
        grid.Children.Add(box);
        grid.Children.Add(buttons);

        Content = new Border
        {
            Child = grid, Margin = new Thickness(12),
            Background = (Brush)FindResource("Panel"), BorderBrush = (Brush)FindResource("LineStrong"),
            BorderThickness = new Thickness(1), CornerRadius = (CornerRadius)FindResource("RadiusCard"),
            Padding = new Thickness(14, 12, 14, 12),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = 0.5 },
        };

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Opacity = 0;
        ContentRendered += (_, _) => BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
    }
}
