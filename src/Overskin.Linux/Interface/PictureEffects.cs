using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Overskin.Linux.Core;

namespace Overskin.Linux.Interface;

/// <summary>A border, rounded corners, torn edges, a shadow and a colour filter for a picture (as on Windows).</summary>
public sealed record EffectsOptions
{
    /// <summary>Border width in pixels; 0 for none.</summary>
    public double Border { get; init; }
    public string BorderColour { get; init; } = "#FFFFFF";
    public double Corners { get; init; }
    /// <summary>None, Bottom, Top, Top and bottom, Sides, All.</summary>
    public string Torn { get; init; } = "None";
    /// <summary>0 to 100.</summary>
    public double Shadow { get; init; }
    /// <summary>None, Grayscale, Sepia, Invert.</summary>
    public string Filter { get; init; } = "None";

    public bool IsEmpty => Border <= 0 && Corners <= 0 && Torn == "None" && Shadow <= 0 && Filter == "None";
}

public static class PictureEffects
{
    public static readonly string[] TornEdges = { "None", "Bottom", "Top", "Top and bottom", "Sides", "All" };
    public static readonly string[] Filters = { "None", "Grayscale", "Sepia", "Invert" };
    public static readonly string[] BorderColours = { "#FFFFFF", "#111111", "#9CA3AF", "#EF4444", "#3B82F6", "#FACC15" };

    public static Bitmap Render(Bitmap image, EffectsOptions o)
    {
        int w = image.PixelSize.Width, h = image.PixelSize.Height;
        var px = Imaging.Pixels(image, out _);
        Filter(px, o.Filter);
        Bitmap result = Imaging.FromPixels(px, image.PixelSize);

        // the border round it, with the corners rounded to match
        if (o.Border > 0 || o.Corners > 0)
        {
            double b = Math.Round(Math.Max(0, o.Border));
            int bw = (int)(w + 2 * b), bh = (int)(h + 2 * b);
            var inner = result;
            result = Imaging.Render(new PixelSize(bw, bh), ctx =>
            {
                if (b > 0)
                {
                    double outer = o.Corners + (o.Corners > 0 ? b : 0);
                    ctx.DrawRectangle(new SolidColorBrush(Imaging.ParseColour(o.BorderColour, Colors.White)), null, new RoundedRect(new Rect(0, 0, bw, bh), outer));
                }
                using (ctx.PushClip(new RoundedRect(new Rect(b, b, w, h), o.Corners)))
                    ctx.DrawImage(inner, new Rect(inner.Size), new Rect(b, b, w, h));
            });
            w = bw;
            h = bh;
        }

        if (o.Torn != "None")
        {
            var torn = Imaging.Pixels(result, out _);
            Tear(torn, w, h, o.Torn);
            result = Imaging.FromPixels(torn, new PixelSize(w, h));
        }

        if (o.Shadow > 0)
        {
            double k = o.Shadow / 100;
            // room for the shadow all round; it follows the shape, torn edges and all
            result = Beautify.Render(result, new BeautifyOptions { Background = "None", Padding = Math.Round(18 + 70 * k), Corners = 0, Shadow = o.Shadow });
        }
        return result;
    }

    /// <summary>Colour filters on premultiplied BGRA (each colour already scaled by how see-through it is).</summary>
    private static void Filter(byte[] px, string filter)
    {
        if (filter == "None") return;
        for (int i = 0; i < px.Length; i += 4)
        {
            double b = px[i], g = px[i + 1], r = px[i + 2], a = px[i + 3];
            switch (filter)
            {
                case "Grayscale":
                    px[i] = px[i + 1] = px[i + 2] = (byte)Math.Round(0.299 * r + 0.587 * g + 0.114 * b);
                    break;
                case "Sepia":
                    px[i + 2] = (byte)Math.Min(a, 0.393 * r + 0.769 * g + 0.189 * b);
                    px[i + 1] = (byte)Math.Min(a, 0.349 * r + 0.686 * g + 0.168 * b);
                    px[i] = (byte)Math.Min(a, 0.272 * r + 0.534 * g + 0.131 * b);
                    break;
                case "Invert":
                    px[i] = (byte)(a - b);
                    px[i + 1] = (byte)(a - g);
                    px[i + 2] = (byte)(a - r);
                    break;
            }
        }
    }

    /// <summary>
    /// Torn paper: a jagged line a little way in from the chosen edges, everything beyond it
    /// see-through, with a soft edge. The same tear as on Windows (same shape for the same size).
    /// </summary>
    private static void Tear(byte[] px, int w, int h, string edges)
    {
        bool top = edges is "Top" or "Top and bottom" or "All", bottom = edges is "Bottom" or "Top and bottom" or "All";
        bool sides = edges is "Sides" or "All";
        double depth = Math.Clamp(Math.Min(w, h) * 0.025, 6, 18);
        var rnd = new Random(7);
        double[] Edge(int length)
        {
            var line = new double[length];
            int step = Math.Max(4, (int)(depth * 0.7));
            double prev = depth * 0.5, next = rnd.NextDouble() * depth;
            for (int i = 0; i < length; i++)
            {
                if (i % step == 0) { prev = next; next = depth * (0.15 + 0.85 * rnd.NextDouble()); }
                double t = (i % step) / (double)step;
                line[i] = prev + (next - prev) * t;
            }
            return line;
        }
        var topLine = top ? Edge(w) : null;
        var bottomLine = bottom ? Edge(w) : null;
        var leftLine = sides ? Edge(h) : null;
        var rightLine = sides ? Edge(h) : null;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double inside = double.MaxValue;
                if (topLine != null) inside = Math.Min(inside, y + 0.5 - topLine[x]);
                if (bottomLine != null) inside = Math.Min(inside, h - y - 0.5 - bottomLine[x]);
                if (leftLine != null) inside = Math.Min(inside, x + 0.5 - leftLine[y]);
                if (rightLine != null) inside = Math.Min(inside, w - x - 0.5 - rightLine[y]);
                if (inside >= 1) continue;
                int i = (y * w + x) * 4;
                double a = Math.Clamp(inside + 0.5, 0, 1);
                // premultiplied: the colours fade with the alpha
                for (int k = 0; k < 4; k++) px[i + k] = (byte)(px[i + k] * a);
            }
    }
}

/// <summary>The Effects dialog: a live preview and the choices, like Beautify.</summary>
public sealed class EffectsWindow : Window
{
    private static EffectsOptions _last = new() { Border = 6, Shadow = 40 };

    private readonly Bitmap _image;
    private Bitmap? _small;
    private double _scale = 1;
    private readonly Image _preview = new() { Stretch = Stretch.Uniform };
    private EffectsOptions _options = _last;
    public Bitmap? Result { get; private set; }

    public EffectsWindow(Bitmap image)
    {
        _image = image;
        Title = "Effects";
        Icon = App.Icon;
        Width = 960;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        Slider Slide(double max, double value, Func<double, EffectsOptions> set)
        {
            var sl = new Slider { Minimum = 0, Maximum = max, Value = value };
            sl.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) Update(set(sl.Value)); };
            return sl;
        }

        WrapPanel Choices(string[] names, Func<string> get, Func<string, EffectsOptions> set)
        {
            var panel = new WrapPanel { ItemSpacing = 5, LineSpacing = 5 };
            var buttons = new List<ToggleButton>();
            foreach (var name in names)
            {
                var b = new ToggleButton { Content = name, IsChecked = name == get(), Padding = new Thickness(9, 4) };
                b.Click += (_, _) => { foreach (var o in buttons) o.IsChecked = o == b; Update(set(name)); };
                buttons.Add(b);
                panel.Children.Add(b);
            }
            return panel;
        }

        var colours = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
        foreach (var hex in PictureEffects.BorderColours)
        {
            var b = new Button { Width = 26, Height = 26, Padding = new Thickness(0), CornerRadius = new CornerRadius(13), Background = new SolidColorBrush(Color.Parse(hex)) };
            ToolTip.SetTip(b, hex);
            b.Click += (_, _) => Update(_options with { BorderColour = hex });
            colours.Children.Add(b);
        }

        var apply = Ui.Button("Apply", () => { _last = _options; Result = PictureEffects.Render(_image, _options); Close(); }, cls: "primary");
        var side = new StackPanel
        {
            Width = 260,
            Spacing = 9,
            Margin = new Thickness(16),
            Children =
            {
                Ui.Label("Effects", "h2"),
                Ui.Label("Border", "dim"), Slide(40, _options.Border, v => _options with { Border = Math.Round(v) }), colours,
                Ui.Label("Corners", "dim"), Slide(40, _options.Corners, v => _options with { Corners = v }),
                Ui.Label("Torn edge", "dim"), Choices(PictureEffects.TornEdges, () => _options.Torn, v => _options with { Torn = v }),
                Ui.Label("Shadow", "dim"), Slide(100, _options.Shadow, v => _options with { Shadow = v }),
                Ui.Label("Colour", "dim"), Choices(PictureEffects.Filters, () => _options.Filter, v => _options with { Filter = v }),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 12, 0, 0), Children = { apply, Ui.Button("Cancel", Close) } },
            },
        };
        var root = new DockPanel();
        var scroll = new ScrollViewer { Content = side };
        DockPanel.SetDock(scroll, Dock.Right);
        root.Children.Add(scroll);
        root.Children.Add(new Border { Background = new SolidColorBrush(Color.Parse("#0C0D0F")), Padding = new Thickness(20), Child = _preview });
        Content = root;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Update(_options);
    }

    private void Update(EffectsOptions o)
    {
        _options = o;
        // drawn smaller for the preview, so the sliders stay smooth
        if (_small == null)
        {
            _scale = Math.Min(1, 1000.0 / Math.Max(_image.PixelSize.Width, _image.PixelSize.Height));
            _small = _scale < 1 ? Effects.Resize(_image, (int)(_image.PixelSize.Width * _scale), (int)(_image.PixelSize.Height * _scale)) : _image;
        }
        _preview.Source = PictureEffects.Render(_small, o with { Border = o.Border > 0 ? Math.Max(1, Math.Round(o.Border * _scale)) : 0, Corners = o.Corners * _scale });
    }
}
