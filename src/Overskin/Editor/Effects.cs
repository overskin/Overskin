using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Overskin;

/// <summary>A border, rounded corners, torn edges, a shadow and a colour filter for a picture.</summary>
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

public static class Effects
{
    public static readonly string[] TornEdges = { "None", "Bottom", "Top", "Top and bottom", "Sides", "All" };
    public static readonly string[] Filters = { "None", "Grayscale", "Sepia", "Invert" };
    public static readonly string[] BorderColours = { "#FFFFFF", "#111111", "#9CA3AF", "#EF4444", "#3B82F6", "#FACC15" };

    public static BitmapSource Render(BitmapSource image, EffectsOptions o)
    {
        var src = image.Format == PixelFormats.Bgra32 ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int w = src.PixelWidth, h = src.PixelHeight;
        var px = new byte[w * h * 4];
        src.CopyPixels(px, w * 4, 0);
        Filter(px, o.Filter);
        BitmapSource result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);

        // the border round it, with the corners rounded to match
        if (o.Border > 0 || o.Corners > 0)
        {
            double b = Math.Round(Math.Max(0, o.Border));
            int bw = (int)(w + 2 * b), bh = (int)(h + 2 * b);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                if (b > 0)
                {
                    Theming.TryParseHex(o.BorderColour, out var colour);
                    dc.DrawRoundedRectangle(new SolidColorBrush(colour), null, new Rect(0, 0, bw, bh), o.Corners + (o.Corners > 0 ? b : 0), o.Corners + (o.Corners > 0 ? b : 0));
                }
                dc.PushClip(new RectangleGeometry(new Rect(b, b, w, h), o.Corners, o.Corners));
                dc.DrawImage(result, new Rect(b, b, w, h));
                dc.Pop();
            }
            var rtb = new RenderTargetBitmap(bw, bh, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            result = new FormatConvertedBitmap(rtb, PixelFormats.Bgra32, null, 0);
            w = bw;
            h = bh;
        }

        if (o.Torn != "None")
        {
            var torn = new byte[w * h * 4];
            result.CopyPixels(torn, w * 4, 0);
            Tear(torn, w, h, o.Torn);
            result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, torn, w * 4);
        }

        if (o.Shadow > 0)
        {
            double k = o.Shadow / 100;
            // room for the shadow all round; it follows the shape, torn edges and all
            result = Beautify.Render(result, new BeautifyOptions { Background = "None", Padding = Math.Round(18 + 70 * k), Corners = 0, Shadow = o.Shadow });
        }
        result = ImageLoader.Normalize(result);
        result.Freeze();
        return result;
    }

    private static void Filter(byte[] px, string filter)
    {
        if (filter == "None") return;
        for (int i = 0; i < px.Length; i += 4)
        {
            double b = px[i], g = px[i + 1], r = px[i + 2];
            switch (filter)
            {
                case "Grayscale":
                    px[i] = px[i + 1] = px[i + 2] = (byte)Math.Round(0.299 * r + 0.587 * g + 0.114 * b);
                    break;
                case "Sepia":
                    px[i + 2] = (byte)Math.Min(255, 0.393 * r + 0.769 * g + 0.189 * b);
                    px[i + 1] = (byte)Math.Min(255, 0.349 * r + 0.686 * g + 0.168 * b);
                    px[i] = (byte)Math.Min(255, 0.272 * r + 0.534 * g + 0.131 * b);
                    break;
                case "Invert":
                    px[i] = (byte)(255 - b);
                    px[i + 1] = (byte)(255 - g);
                    px[i + 2] = (byte)(255 - r);
                    break;
            }
        }
    }

    /// <summary>
    /// Torn paper: a jagged line a little way in from the chosen edges, everything beyond it
    /// see-through, with a soft (anti-aliased) edge.
    /// </summary>
    private static void Tear(byte[] px, int w, int h, string edges)
    {
        bool top = edges is "Top" or "Top and bottom" or "All", bottom = edges is "Bottom" or "Top and bottom" or "All";
        bool sides = edges is "Sides" or "All";
        double depth = Math.Clamp(Math.Min(w, h) * 0.025, 6, 18);
        var rnd = new Random(7);
        // a zig-zag: a new point every few pixels, joined by straight lines
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
                // how far inside every torn edge this pixel is (negative: outside)
                double inside = double.MaxValue;
                if (topLine != null) inside = Math.Min(inside, y + 0.5 - topLine[x]);
                if (bottomLine != null) inside = Math.Min(inside, h - y - 0.5 - bottomLine[x]);
                if (leftLine != null) inside = Math.Min(inside, x + 0.5 - leftLine[y]);
                if (rightLine != null) inside = Math.Min(inside, w - x - 0.5 - rightLine[y]);
                if (inside >= 1) continue;
                int i = (y * w + x) * 4;
                double a = Math.Clamp(inside + 0.5, 0, 1);
                px[i + 3] = (byte)(px[i + 3] * a);
            }
    }
}

/// <summary>The Effects dialog: a live preview and the choices, like Beautify.</summary>
public sealed class EffectsWindow : Window
{
    private static EffectsOptions _last = new() { Border = 6, Shadow = 40 };

    private readonly BitmapSource _image;
    private readonly Image _preview = new() { Stretch = Stretch.Uniform };
    private readonly Slider _border = new() { Minimum = 0, Maximum = 40, Value = _last.Border, Width = 170, SmallChange = 1, IsSnapToTickEnabled = true, TickFrequency = 1 };
    private readonly Slider _corners = new() { Minimum = 0, Maximum = 40, Value = _last.Corners, Width = 170 };
    private readonly Slider _shadow = new() { Minimum = 0, Maximum = 100, Value = _last.Shadow, Width = 170 };
    private string _colour = _last.BorderColour, _torn = _last.Torn, _filter = _last.Filter;

    public BitmapSource? Result { get; private set; }

    public EffectsWindow(BitmapSource image)
    {
        _image = image;
        Title = "Effects";
        Width = 980;
        Height = 660;
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

        var colours = new WrapPanel { Width = 236 };
        foreach (string hex in Effects.BorderColours)
        {
            Theming.TryParseHex(hex, out var c);
            var swatch = new RadioButton
            {
                Style = (Style)FindResource("Swatch"), GroupName = "EffectsColour", Background = new SolidColorBrush(c),
                Width = 26, Height = 26, Margin = new Thickness(0, 0, 6, 6), IsChecked = hex == _colour, ToolTip = hex,
            };
            string chosen = hex;
            swatch.Checked += (_, _) => { _colour = chosen; Refresh(); };
            colours.Children.Add(swatch);
        }

        WrapPanel Choices(string group, string[] names, Func<string> get, Action<string> set)
        {
            var panel = new WrapPanel { Width = 236 };
            foreach (string name in names)
            {
                var b = new RadioButton { Style = (Style)FindResource("Seg"), GroupName = group, Content = name, Margin = new Thickness(0, 0, 5, 5), IsChecked = name == get() };
                string chosen = name;
                b.Checked += (_, _) => { set(chosen); Refresh(); };
                panel.Children.Add(b);
            }
            return panel;
        }

        foreach (var s in new[] { _border, _corners, _shadow }) s.ValueChanged += (_, _) => Refresh();

        TextBlock Label(string text) => new() { Text = text, Style = (Style)FindResource("Label"), Margin = new Thickness(0, 14, 0, 6) };
        var apply = new Button { Style = (Style)FindResource("BtnAccent"), Content = "Apply", MinWidth = 90, IsDefault = true };
        apply.Click += (_, _) => { _last = Options(); Result = Effects.Render(_image, _last); DialogResult = true; };
        var cancel = new Button { Style = (Style)FindResource("Btn"), Content = "Cancel", MinWidth = 80, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };

        var side = new StackPanel { Margin = new Thickness(0, 18, 18, 18), Width = 236 };
        side.Children.Add(new TextBlock { Text = "Effects", Style = (Style)FindResource("Heading"), FontSize = 15 });
        side.Children.Add(Label("Border"));
        side.Children.Add(_border);
        side.Children.Add(new Border { Height = 8 });
        side.Children.Add(colours);
        side.Children.Add(Label("Corners"));
        side.Children.Add(_corners);
        side.Children.Add(Label("Torn edge"));
        side.Children.Add(Choices("EffectsTorn", Effects.TornEdges, () => _torn, v => _torn = v));
        side.Children.Add(Label("Shadow"));
        side.Children.Add(_shadow);
        side.Children.Add(Label("Colour"));
        side.Children.Add(Choices("EffectsFilter", Effects.Filters, () => _filter, v => _filter = v));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(apply);
        side.Children.Add(buttons);

        var scroll = new ScrollViewer { Content = side, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        var panel = new Border { Child = scroll };
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

    private EffectsOptions Options() => new()
    {
        Border = _border.Value, BorderColour = _colour, Corners = _corners.Value, Torn = _torn, Shadow = _shadow.Value, Filter = _filter,
    };

    private void Refresh()
    {
        // drawn smaller for the preview, so the sliders stay smooth
        double k = Math.Min(1, 1100.0 / Math.Max(_image.PixelWidth, _image.PixelHeight));
        var o = Options();
        BitmapSource src = k < 1 ? new TransformedBitmap(_image, new ScaleTransform(k, k)) : _image;
        _preview.Source = Effects.Render(src, o with { Border = o.Border > 0 ? Math.Max(1, Math.Round(o.Border * k)) : 0, Corners = o.Corners * k });
    }
}
