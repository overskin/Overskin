using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Overskin;

/// <summary>How a beautified screenshot is set out.</summary>
public sealed record BeautifyOptions
{
    public string Background { get; init; } = "Ocean";
    public double Padding { get; init; } = 64;
    public double Corners { get; init; } = 12;
    /// <summary>0 to 100.</summary>
    public double Shadow { get; init; } = 50;
    /// <summary>Width / height, or 0 to fit the picture.</summary>
    public double Aspect { get; init; }
    /// <summary>A device or window round the picture: see <see cref="Mockup.Frames"/>.</summary>
    public string Frame { get; init; } = "None";
    /// <summary>The frame picture for "Custom".</summary>
    public string? FramePath { get; init; }
}

/// <summary>
/// Puts a screenshot on a backdrop: a gradient or plain colour (or nothing), with padding,
/// rounded corners and a soft shadow, optionally to a fixed shape for posts and slides.
/// </summary>
public static class Beautify
{
    public static readonly (string Name, string From, string To)[] Backgrounds =
    {
        ("Ocean", "#4F8CFF", "#8E7BFF"), ("Sunset", "#F97316", "#EC4899"), ("Mint", "#22C55E", "#38BFC3"),
        ("Candy", "#EC4899", "#8B5CF6"), ("Peach", "#FDBA74", "#F472B6"), ("Sky", "#7DD3FC", "#A5B4FC"),
        ("Forest", "#166534", "#0F766E"), ("Night", "#334155", "#0F172A"),
        ("White", "#FFFFFF", "#FFFFFF"), ("Grey", "#E5E7EB", "#D1D5DB"), ("Black", "#111111", "#111111"), ("None", "", ""),
    };

    public static Brush BackgroundBrush(string name)
    {
        var (_, from, to) = Backgrounds.FirstOrDefault(b => b.Name == name);
        if (string.IsNullOrEmpty(from)) return Brushes.Transparent;
        Theming.TryParseHex(from, out var a);
        Theming.TryParseHex(to, out var b);
        var brush = a == b ? (Brush)new SolidColorBrush(a) : new LinearGradientBrush(a, b, 35);
        brush.Freeze();
        return brush;
    }

    public static BitmapSource Render(BitmapSource image, BeautifyOptions o)
    {
        if (o.Frame != "None")
        {
            // the frame gives the shape, so the card itself gets no rounding of its own
            var framed = Mockup.Apply(image, o.Frame, o.FramePath);
            if (!ReferenceEquals(framed, image)) { image = framed; o = o with { Corners = 0 }; }
        }
        double iw = image.PixelWidth, ih = image.PixelHeight;
        double w = iw + 2 * o.Padding, h = ih + 2 * o.Padding;
        if (o.Aspect > 0)
        {
            if (w / h < o.Aspect) w = h * o.Aspect;
            else h = w / o.Aspect;
        }
        int pw = (int)Math.Round(w), ph = (int)Math.Round(h);

        double strength = o.Shadow / 100;
        var card = new Border
        {
            Width = iw, Height = ih,
            CornerRadius = new CornerRadius(o.Corners),
            Background = new ImageBrush(image) { Stretch = Stretch.Fill },
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = strength <= 0 ? null : new DropShadowEffect
            {
                BlurRadius = 12 + 60 * strength, ShadowDepth = 4 + 18 * strength, Direction = 270,
                Opacity = 0.18 + 0.4 * strength, Color = Colors.Black, RenderingBias = RenderingBias.Quality,
            },
        };
        var root = new Grid { Width = pw, Height = ph, Background = BackgroundBrush(o.Background) };
        root.Children.Add(card);
        root.Measure(new Size(pw, ph));
        root.Arrange(new Rect(0, 0, pw, ph));
        root.UpdateLayout();

        var rtb = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
        rtb.Freeze();
        return rtb;
    }
}

/// <summary>The Beautify dialog: a live preview and the few choices that matter.</summary>
public sealed class BeautifyWindow : Window
{
    private static BeautifyOptions _last = new();

    private readonly BitmapSource _image;
    private readonly Image _preview = new() { Stretch = Stretch.Uniform };
    private readonly Slider _padding = new() { Minimum = 0, Maximum = 200, Value = _last.Padding, Width = 170 };
    private readonly Slider _corners = new() { Minimum = 0, Maximum = 40, Value = _last.Corners, Width = 170 };
    private readonly Slider _shadow = new() { Minimum = 0, Maximum = 100, Value = _last.Shadow, Width = 170 };
    private string _background = _last.Background;
    private string _frame = _last.Frame;
    private string? _framePath = _last.FramePath;
    private double _aspect = _last.Aspect;
    private readonly WrapPanel _swatches = new() { Width = 236 };

    public BitmapSource? Result { get; private set; }

    public BeautifyWindow(BitmapSource image)
    {
        _image = image;
        Title = "Beautify";
        Width = 980;
        Height = 700;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Bg");
        SetResourceReference(ForegroundProperty, "Text");
        FontFamily = (FontFamily)FindResource("UiFont");

        var checker = new DrawingBrush
        {
            TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute,
            Drawing = new GeometryDrawing(new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80)), null,
                new GeometryGroup { Children = { new RectangleGeometry(new Rect(0, 0, 8, 8)), new RectangleGeometry(new Rect(8, 8, 8, 8)) } }),
        };
        var stage = new Border { Margin = new Thickness(18), Background = checker, Child = _preview };
        RenderOptions.SetBitmapScalingMode(_preview, BitmapScalingMode.HighQuality);

        foreach (var (name, _, _) in Beautify.Backgrounds)
        {
            var swatch = new RadioButton
            {
                Style = (Style)FindResource("Swatch"),
                GroupName = "BeautifyBg",
                Background = name == "None" ? checker : Beautify.BackgroundBrush(name),
                ToolTip = name,
                Width = 30, Height = 30,
                Margin = new Thickness(0, 0, 6, 6),
                IsChecked = name == _background,
            };
            var chosen = name;
            swatch.Checked += (_, _) => { _background = chosen; Refresh(); };
            _swatches.Children.Add(swatch);
        }

        var aspects = new WrapPanel { Width = 236 };
        foreach (var (label, ratio) in new[] { ("Fit", 0.0), ("16:9", 16 / 9.0), ("4:3", 4 / 3.0), ("1:1", 1.0), ("9:16", 9 / 16.0) })
        {
            var b = new RadioButton
            {
                Style = (Style)FindResource("Seg"), GroupName = "BeautifyAspect", Content = label,
                Margin = new Thickness(0, 0, 5, 5), IsChecked = Math.Abs(ratio - _aspect) < 0.001,
            };
            var r = ratio;
            b.Checked += (_, _) => { _aspect = r; Refresh(); };
            aspects.Children.Add(b);
        }

        var frames = new WrapPanel { Width = 236 };
        foreach (var name in Mockup.Frames)
        {
            var b = new RadioButton
            {
                Style = (Style)FindResource("Seg"), GroupName = "BeautifyFrame", Content = name == "Custom" ? "Your own…" : name,
                Margin = new Thickness(0, 0, 5, 5), IsChecked = name == _frame,
                ToolTip = name == "Custom" ? "Any frame picture with a see-through screen (a PNG), such as the device bezels makers publish for designers" : null,
            };
            var chosen = name;
            b.Checked += (_, _) =>
            {
                if (chosen == "Custom")
                {
                    var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Choose a frame picture (PNG with a see-through screen)", Filter = "PNG pictures|*.png|All pictures|*.png;*.webp;*.tif;*.tiff" };
                    if (dialog.ShowDialog(this) == true) _framePath = dialog.FileName;
                    if (_framePath == null || Mockup.Custom(new WriteableBitmap(4, 4, 96, 96, PixelFormats.Bgra32, null), _framePath) == null)
                    {
                        Toast.Show("That picture has no see-through screen to put the screenshot in.", true);
                        _frame = "None";
                        Refresh();
                        return;
                    }
                }
                _frame = chosen;
                Refresh();
            };
            frames.Children.Add(b);
        }

        foreach (var s in new[] { _padding, _corners, _shadow }) s.ValueChanged += (_, _) => Refresh();

        TextBlock Label(string text) => new() { Text = text, Style = (Style)FindResource("Label"), Margin = new Thickness(0, 14, 0, 6) };
        var apply = new Button { Style = (Style)FindResource("BtnAccent"), Content = "Apply", MinWidth = 90, IsDefault = true };
        apply.Click += (_, _) => { Result = Beautify.Render(_image, Options()); _last = Options(); DialogResult = true; };
        var cancel = new Button { Style = (Style)FindResource("Btn"), Content = "Cancel", MinWidth = 80, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };

        var side = new StackPanel { Margin = new Thickness(0, 18, 18, 18), Width = 236 };
        side.Children.Add(new TextBlock { Text = "Beautify", Style = (Style)FindResource("Heading"), FontSize = 15 });
        side.Children.Add(Label("Frame"));
        side.Children.Add(frames);
        side.Children.Add(Label("Background"));
        side.Children.Add(_swatches);
        side.Children.Add(Label("Shape"));
        side.Children.Add(aspects);
        side.Children.Add(Label("Padding"));
        side.Children.Add(_padding);
        side.Children.Add(Label("Corners"));
        side.Children.Add(_corners);
        side.Children.Add(Label("Shadow"));
        side.Children.Add(_shadow);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(apply);
        side.Children.Add(buttons);

        var panel = new Border { Child = side };
        panel.SetResourceReference(Border.BackgroundProperty, "Panel");
        panel.SetResourceReference(Border.BorderBrushProperty, "Line");
        panel.BorderThickness = new Thickness(1, 0, 0, 0);
        panel.Padding = new Thickness(18, 0, 0, 0);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(stage);
        Grid.SetColumn(panel, 1);
        grid.Children.Add(panel);
        Content = grid;

        Loaded += (_, _) => Refresh();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private BeautifyOptions Options() => new()
    {
        Background = _background, Padding = _padding.Value, Corners = _corners.Value, Shadow = _shadow.Value, Aspect = _aspect,
        Frame = _frame, FramePath = _framePath,
    };

    private void Refresh()
    {
        // the preview is drawn smaller, so dragging a slider stays smooth
        double k = Math.Min(1, 1100.0 / Math.Max(_image.PixelWidth, _image.PixelHeight));
        var o = Options();
        BitmapSource src = k < 1 ? new TransformedBitmap(_image, new ScaleTransform(k, k)) : _image;
        _preview.Source = Beautify.Render(src, o with { Padding = o.Padding * k, Corners = o.Corners * k });
    }
}
