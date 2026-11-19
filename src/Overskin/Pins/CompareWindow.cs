using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Overskin;

/// <summary>
/// Two pictures pinned on top for comparing: a divider you drag to wipe between before and
/// after, or the second faded over the first (good for checking a drawing against its reference).
/// </summary>
public sealed class CompareWindow : Window
{
    private readonly Image _before = new() { Stretch = Stretch.Uniform };
    private readonly Image _after = new() { Stretch = Stretch.Uniform };
    // fills the whole stage, so its clip is in the stage's own coordinates
    private readonly Border _afterHost = new() { Background = Brushes.Transparent, IsHitTestVisible = false };
    private readonly Grid _stage = new() { ClipToBounds = true, Background = Brushes.Transparent };
    private readonly Rectangle _line = new() { Width = 2, Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false };
    private readonly Border _handle;
    private readonly Slider _fade = new() { Minimum = 0, Maximum = 1, Value = 0.5, Width = 110, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly RadioButton _wipeMode, _fadeMode;
    private readonly TextBlock _leftLabel, _rightLabel;
    private double _split = 0.5;
    private bool _dragging;

    /// <summary>Where the divider is, 0 (left) to 1 (right).</summary>
    public double Split
    {
        get => _split;
        set { _split = Math.Clamp(value, 0, 1); Layout(); }
    }

    public bool FadeMode => _fadeMode.IsChecked == true;

    public CompareWindow(BitmapSource before, BitmapSource after, string beforeName, string afterName)
    {
        Title = "Compare";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = true;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        FontFamily = (FontFamily)FindResource("UiFont");
        FontSize = 12;
        SetResourceReference(ForegroundProperty, "Text");
        UseLayoutRounding = true;

        _before.Source = before;
        _after.Source = after;
        RenderOptions.SetBitmapScalingMode(_before, BitmapScalingMode.HighQuality);
        RenderOptions.SetBitmapScalingMode(_after, BitmapScalingMode.HighQuality);

        // the size of the larger picture, fitted to the screen
        var work = SystemParameters.WorkArea;
        double w = Math.Max(before.PixelWidth, after.PixelWidth), h = Math.Max(before.PixelHeight, after.PixelHeight);
        double k = Math.Min(1, Math.Min(work.Width * 0.7 / w, (work.Height * 0.7 - 40) / h));
        Width = Math.Max(360, w * k);
        Height = Math.Max(240, h * k + 40);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _handle = new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 8, ShadowDepth = 1, Opacity = 0.4 },
            Child = new Icon { Data = (Geometry)FindResource("IcArrowLeftRight"), Width = 14, Height = 14, Foreground = Brushes.Black },
        };
        _line.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 4, ShadowDepth = 0, Opacity = 0.5 };
        _leftLabel = Label(beforeName, HorizontalAlignment.Left);
        _rightLabel = Label(afterName, HorizontalAlignment.Right);

        _stage.Children.Add(_before);
        _afterHost.Child = _after;
        _stage.Children.Add(_afterHost);
        _stage.Children.Add(_line);
        _stage.Children.Add(_handle);
        _stage.Children.Add(_leftLabel);
        _stage.Children.Add(_rightLabel);
        _stage.SizeChanged += (_, _) => Layout();
        _stage.MouseLeftButtonDown += (_, e) => { if (!FadeMode) { _dragging = true; _stage.CaptureMouse(); Move(e); } };
        _stage.MouseMove += (_, e) => { if (_dragging) Move(e); };
        _stage.MouseLeftButtonUp += (_, _) => { _dragging = false; _stage.ReleaseMouseCapture(); };
        _stage.MouseWheel += (_, e) =>
        {
            if (FadeMode) _fade.Value += e.Delta > 0 ? 0.05 : -0.05;
            else Split += e.Delta > 0 ? -0.02 : 0.02;
        };
        _fade.ValueChanged += (_, _) => Layout();

        // the bar along the top: drag the window by it
        var bar = new DockPanel { Height = 40, LastChildFill = false, Background = Brushes.Transparent };
        bar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new Icon { Data = (Geometry)FindResource("IcColumns2"), Width = 15, Height = 15, Foreground = (Brush)FindResource("Accent") });
        title.Children.Add(new TextBlock { Text = "Compare", FontWeight = FontWeights.Bold, Margin = new Thickness(8, 0, 12, 1), VerticalAlignment = VerticalAlignment.Center });
        _wipeMode = new RadioButton { Style = (Style)FindResource("Seg"), GroupName = "CompareMode" + GetHashCode(), Content = "Slider", MinWidth = 0, IsChecked = true, ToolTip = "Drag across the picture to wipe between them" };
        _fadeMode = new RadioButton { Style = (Style)FindResource("Seg"), GroupName = "CompareMode" + GetHashCode(), Content = "Fade", MinWidth = 0, ToolTip = "The second picture over the first, see-through" };
        _wipeMode.Checked += (_, _) => Layout();
        _fadeMode.Checked += (_, _) => Layout();
        title.Children.Add(_wipeMode);
        title.Children.Add(_fadeMode);
        title.Children.Add(_fade);
        DockPanel.SetDock(title, Dock.Left);
        bar.Children.Add(title);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        var swap = BarButton("IcArrowLeftRight", "Swap them");
        swap.Click += (_, _) => Swap();
        var close = new Button { Style = (Style)FindResource("BtnCaptionClose"), ToolTip = "Close", Content = new Icon { Data = (Geometry)FindResource("IcX"), Width = 14, Height = 14 } };
        close.Click += (_, _) => Close();
        right.Children.Add(swap);
        right.Children.Add(close);
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(right);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
        root.RowDefinitions.Add(new RowDefinition());
        root.Children.Add(bar);
        Grid.SetRow(_stage, 1);
        root.Children.Add(_stage);
        var frame = new Border { BorderThickness = new Thickness(1), Child = root, ClipToBounds = true };
        frame.SetResourceReference(Border.BackgroundProperty, "Bg");
        frame.SetResourceReference(Border.BorderBrushProperty, "Line");
        frame.SetResourceReference(Border.CornerRadiusProperty, "RadiusWindow");
        Content = frame;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key == Key.Left) Split -= 0.02;
            else if (e.Key == Key.Right) Split += 0.02;
            else if (e.Key == Key.S) Swap();
            else if (e.Key == Key.F) { if (FadeMode) _wipeMode.IsChecked = true; else _fadeMode.IsChecked = true; }
        };
    }

    private TextBlock Label(string text, HorizontalAlignment side) => new()
    {
        Text = text, HorizontalAlignment = side, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10, 0, 10, 10),
        Padding = new Thickness(8, 3, 8, 3), Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)),
        FontSize = 11.5, IsHitTestVisible = false, MaxWidth = 260, TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private Button BarButton(string icon, string tip) => new()
    {
        Style = (Style)FindResource("BtnIcon"), Width = 30, Height = 28, ToolTip = tip, Margin = new Thickness(0, 0, 4, 0),
        Content = new Icon { Data = (Geometry)FindResource(icon), Width = 14, Height = 14 },
    };

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        int round = 2;
        NativeMethods.DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 33, ref round, sizeof(int));
    }

    private void Move(MouseEventArgs e)
    {
        var box = PictureBox();
        if (box.Width <= 0) return;
        Split = (e.GetPosition(_stage).X - box.X) / box.Width;
    }

    /// <summary>Where the first picture is drawn inside the stage.</summary>
    private Rect PictureBox()
    {
        if (_before.Source is not BitmapSource b || _stage.ActualWidth <= 0) return Rect.Empty;
        double k = Math.Min(_stage.ActualWidth / b.PixelWidth, _stage.ActualHeight / b.PixelHeight);
        double w = b.PixelWidth * k, h = b.PixelHeight * k;
        return new Rect((_stage.ActualWidth - w) / 2, (_stage.ActualHeight - h) / 2, w, h);
    }

    private void Layout()
    {
        if (!IsInitialized || _stage.ActualWidth <= 0) return;
        bool fade = FadeMode;
        _fade.Visibility = fade ? Visibility.Visible : Visibility.Collapsed;
        _line.Visibility = _handle.Visibility = fade ? Visibility.Collapsed : Visibility.Visible;
        _leftLabel.Visibility = _rightLabel.Visibility = fade ? Visibility.Collapsed : Visibility.Visible;
        if (fade)
        {
            _afterHost.Clip = null;
            _afterHost.Opacity = _fade.Value;
            return;
        }
        _afterHost.Opacity = 1;
        var box = PictureBox();
        double x = box.X + box.Width * _split;
        // the second picture shows to the right of the divider
        _afterHost.Clip = new RectangleGeometry(new Rect(x, 0, Math.Max(0, _stage.ActualWidth - x), _stage.ActualHeight));
        _line.Margin = new Thickness(x - 1, box.Y, 0, 0);
        _line.Height = box.Height;
        _line.VerticalAlignment = VerticalAlignment.Top;
        _handle.Margin = new Thickness(x - 13, 0, 0, 0);
    }

    private void Swap()
    {
        (_before.Source, _after.Source) = (_after.Source, _before.Source);
        (_leftLabel.Text, _rightLabel.Text) = (_rightLabel.Text, _leftLabel.Text);
        Layout();
    }
}
