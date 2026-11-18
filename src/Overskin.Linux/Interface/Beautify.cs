using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Overskin.Linux.Core;

namespace Overskin.Linux.Interface;

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

    public static readonly (string Name, double Aspect)[] Shapes =
    {
        ("Fit the picture", 0), ("Square 1:1", 1), ("Wide 16:9", 16.0 / 9), ("Post 4:5", 4.0 / 5), ("Slide 4:3", 4.0 / 3),
    };

    public static IBrush? BackgroundBrush(string name)
    {
        var (_, from, to) = Backgrounds.FirstOrDefault(b => b.Name == name);
        if (string.IsNullOrEmpty(from)) return null;
        var a = Color.Parse(from);
        var b = Color.Parse(to);
        if (a == b) return new SolidColorBrush(a);
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(a, 0), new GradientStop(b, 1) },
        };
    }

    public static Bitmap Render(Bitmap image, BeautifyOptions o)
    {
        double iw = image.PixelSize.Width, ih = image.PixelSize.Height;
        double w = iw + 2 * o.Padding, h = ih + 2 * o.Padding;
        if (o.Aspect > 0)
        {
            if (w / h < o.Aspect) w = h * o.Aspect;
            else h = w / o.Aspect;
        }
        int pw = (int)Math.Round(w), ph = (int)Math.Round(h);
        var card = new Rect((pw - iw) / 2, (ph - ih) / 2, iw, ih);
        var rounded = new RoundedRect(card, o.Corners);
        double s = o.Shadow / 100;

        return Imaging.Render(new PixelSize(pw, ph), ctx =>
        {
            if (BackgroundBrush(o.Background) is { } back) ctx.FillRectangle(back, new Rect(0, 0, pw, ph));
            if (s > 0)
            {
                var shadow = new BoxShadows(new BoxShadow
                {
                    OffsetY = 4 + 18 * s,
                    Blur = 12 + 60 * s,
                    Color = Color.FromArgb((byte)(255 * (0.18 + 0.4 * s)), 0, 0, 0),
                });
                ctx.DrawRectangle(Brushes.White, null, rounded, shadow);
            }
            using (ctx.PushClip(rounded))
                ctx.DrawImage(image, new Rect(image.Size), card);
        });
    }
}

/// <summary>Choose a backdrop, padding, corners, shadow and shape, with a live preview.</summary>
public sealed class BeautifyWindow : Window
{
    private readonly Bitmap _image;
    private Bitmap? _small;
    private double _scale = 1;
    private readonly Image _preview = new() { Stretch = Stretch.Uniform };
    private BeautifyOptions _options = new();
    public Bitmap? Result { get; private set; }

    public BeautifyWindow(Bitmap image)
    {
        _image = image;
        Title = "Beautify";
        Icon = App.Icon;
        Width = 900;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var swatches = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
        foreach (var (name, from, to) in Beautify.Backgrounds)
        {
            var b = new Button
            {
                Width = 34, Height = 34, Padding = new Thickness(0), CornerRadius = new CornerRadius(17),
                Background = Beautify.BackgroundBrush(name) ?? new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                Content = name == "None" ? Ui.Icon(Icons.Close, 14) : null,
            };
            ToolTip.SetTip(b, name);
            b.Click += (_, _) => Update(_options with { Background = name });
            swatches.Children.Add(b);
        }

        Slider Slide(double min, double max, double value, Func<double, BeautifyOptions> set)
        {
            var sl = new Slider { Minimum = min, Maximum = max, Value = value };
            sl.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) Update(set(sl.Value)); };
            return sl;
        }

        var shape = new ComboBox { ItemsSource = Beautify.Shapes.Select(x => x.Name).ToList(), SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        shape.SelectionChanged += (_, _) => Update(_options with { Aspect = Beautify.Shapes[Math.Max(0, shape.SelectedIndex)].Aspect });

        var apply = Ui.Button("Apply", () => { Result = Beautify.Render(_image, _options); Close(); }, cls: "primary");
        var cancel = Ui.Button("Cancel", Close);

        var side = new StackPanel
        {
            Width = 250,
            Spacing = 10,
            Margin = new Thickness(16),
            Children =
            {
                Ui.Label("Background", "h2"), swatches,
                Ui.Label("Padding", "dim"), Slide(0, 200, _options.Padding, v => _options with { Padding = v }),
                Ui.Label("Corners", "dim"), Slide(0, 48, _options.Corners, v => _options with { Corners = v }),
                Ui.Label("Shadow", "dim"), Slide(0, 100, _options.Shadow, v => _options with { Shadow = v }),
                Ui.Label("Shape", "dim"), shape,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 12, 0, 0), Children = { apply, cancel } },
            },
        };
        var root = new DockPanel();
        DockPanel.SetDock(side, Dock.Right);
        root.Children.Add(side);
        root.Children.Add(new Border { Background = new SolidColorBrush(Color.Parse("#0C0D0F")), Padding = new Thickness(20), Child = _preview });
        Content = root;
        Update(_options);
    }

    private void Update(BeautifyOptions o)
    {
        _options = o;
        // the preview is drawn from a smaller copy, so sliders stay smooth on big screenshots
        if (_small == null)
        {
            _scale = Math.Min(1, 900.0 / Math.Max(_image.PixelSize.Width, _image.PixelSize.Height));
            _small = _scale < 1 ? Effects.Resize(_image, (int)(_image.PixelSize.Width * _scale), (int)(_image.PixelSize.Height * _scale)) : _image;
        }
        _preview.Source = Beautify.Render(_small, o with { Padding = o.Padding * _scale, Corners = o.Corners * _scale });
    }
}
