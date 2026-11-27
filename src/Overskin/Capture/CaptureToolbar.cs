using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Overskin;

/// <summary>What a click or drag on the frozen screen does.</summary>
public enum CapturePick { Region, Window, Screen, AllScreens }

/// <summary>Marks that can be drawn on the frozen screen before choosing what to capture.</summary>
public enum CaptureTool { None, Pen, Arrow, Box, Highlighter }

/// <summary>
/// The bar across the top of the capture screen: what to capture (an area, a window, this
/// screen or every screen), a few marks to draw first, the pointer on or off, the magnifier,
/// and close. Keys: 1 to 4 pick, P/A/B/H draw, C cycles the colour, Ctrl+Z undoes, M magnifier.
/// </summary>
public sealed class CaptureToolbar : Border
{
    /// <summary>The same colours as the editor's, plus a few more.</summary>
    public static readonly Color[] Colours =
    {
        Color.FromRgb(0xEF, 0x44, 0x44), Color.FromRgb(0xF9, 0x73, 0x16), Color.FromRgb(0xFA, 0xCC, 0x15),
        Color.FromRgb(0x22, 0xC5, 0x5E), Color.FromRgb(0x14, 0xB8, 0xA6), Color.FromRgb(0x3B, 0x82, 0xF6),
        Color.FromRgb(0xA8, 0x55, 0xF7), Color.FromRgb(0xEC, 0x48, 0x99), Color.FromRgb(0x92, 0x40, 0x0E),
        Color.FromRgb(0x6B, 0x72, 0x80), Color.FromRgb(0x11, 0x11, 0x11), Colors.White,
    };

    private readonly Dictionary<CapturePick, RadioButton> _picks = new();
    private readonly Dictionary<CaptureTool, ToggleButton> _tools = new();
    private readonly ToggleButton _pointer, _magnifier, _tighten;
    private readonly Border _swatch;
    private readonly Button _undo;
    private int _colour;
    private Color _current = Colours[0];
    /// <summary>Remembered for the next capture in this session.</summary>
    private static Color? _lastColour;
    private readonly System.Windows.Controls.Primitives.Popup _colourPopup = new();
    private ColorWheel? _wheel;
    private Slider? _brightness;
    private TextBox? _hex;
    private bool _syncing;

    public CapturePick Pick { get; private set; }
    /// <summary>Width / height that a dragged area keeps, or 0 for any shape.</summary>
    public double Ratio { get; private set; }
    /// <summary>An exact size in pixels that follows the pointer, or null.</summary>
    public Size? FixedSize { get; private set; }
    private readonly Button? _shape;
    private readonly TextBlock _shapeText = new() { Text = "Any size", VerticalAlignment = VerticalAlignment.Center };
    public CaptureTool Tool { get; private set; }
    public Color Colour => _current;

    /// <summary>The "pick from the screen" button: the next click on the frozen screen takes its colour.</summary>
    public event Action? PickFromScreenRequested;
    public bool Pointer => _pointer.IsChecked == true;
    public bool Magnifier => _magnifier.IsChecked == true;
    /// <summary>Fit a dragged box to what's inside it.</summary>
    public bool Tighten => _tighten.IsChecked == true;

    /// <summary>Anything on the bar changed.</summary>
    public event Action? Changed;
    public event Action? UndoRequested;
    public event Action? CloseRequested;

    public CaptureToolbar(CapturePick pick, bool pointer, bool showPicks = true)
    {
        Pick = pick;
        SetResourceReference(BackgroundProperty, "Panel");
        SetResourceReference(BorderBrushProperty, "LineStrong");
        SetResourceReference(CornerRadiusProperty, "RadiusControl");
        BorderThickness = new Thickness(1);
        Padding = new Thickness(6, 5, 5, 5);
        Cursor = Cursors.Arrow;
        SetResourceReference(TextElement.ForegroundProperty, "Text");
        TextElement.SetFontFamily(this, (FontFamily)Application.Current.Resources["UiFont"]);
        TextElement.SetFontSize(this, 12);

        var row = new StackPanel { Orientation = Orientation.Horizontal };

        if (showPicks)
        {
            foreach (var (p, icon, text, tip) in new[]
            {
                (CapturePick.Region, "IcSquareDashed", "Area", "Drag over an area, or click a window  (1)"),
                (CapturePick.Window, "IcAppWindow", "Window", "Click a window  (2)"),
                (CapturePick.Screen, "IcMonitor", "Screen", "Click a screen  (3)"),
                (CapturePick.AllScreens, "IcLayers", "All", "Every screen at once  (4)"),
            })
            {
                var button = new RadioButton
                {
                    Style = (Style)Application.Current.FindResource("Seg"),
                    GroupName = "CapturePick" + GetHashCode(),
                    Content = Labelled(icon, text),
                    ToolTip = tip,
                    Margin = new Thickness(_picks.Count == 0 ? 0 : 4, 0, 0, 0),
                    IsChecked = p == pick,
                };
                var chosen = p;
                button.Checked += (_, _) => { if (!_syncing) SetPick(chosen); };
                _picks[p] = button;
                row.Children.Add(button);
            }
            _shape = new Button
            {
                Style = (Style)Application.Current.FindResource("Btn"), Height = 23, Padding = new Thickness(9, 0, 9, 0),
                Margin = new Thickness(4, 0, 0, 0), ToolTip = "Keep the area to a shape (16:9, 1:1...) or an exact size",
            };
            var shapeRow = new StackPanel { Orientation = Orientation.Horizontal };
            shapeRow.Children.Add(new Icon { Data = Geometry("IcRuler"), Width = 13, Height = 13, Margin = new Thickness(0, 0, 6, 0) });
            shapeRow.Children.Add(_shapeText);
            _shape.Content = shapeRow;
            _shape.Click += (_, _) => ShowShapes();
            row.Children.Add(_shape);
            row.Children.Add(Divider());
        }

        foreach (var (t, icon, tip) in new[]
        {
            (CaptureTool.Pen, "IcPencil", "Draw  (P)"),
            (CaptureTool.Arrow, "IcArrowUpRight", "Arrow  (A)"),
            (CaptureTool.Box, "IcSquare", "Box  (B)"),
            (CaptureTool.Highlighter, "IcHighlighter", "Highlight  (H)"),
        })
        {
            var button = new ToggleButton
            {
                Style = (Style)Application.Current.FindResource("Chip"),
                Content = new Icon { Data = Geometry(icon), Width = 14, Height = 14 },
                ToolTip = tip,
                Margin = new Thickness(0, 0, 4, 0),
                Padding = new Thickness(0),
            };
            var tool = t;
            button.Click += (_, _) => SetTool(button.IsChecked == true ? tool : CaptureTool.None);
            _tools[t] = button;
            row.Children.Add(button);
        }

        _swatch = new Border { Width = 14, Height = 14, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3) };
        _swatch.SetResourceReference(Border.BorderBrushProperty, "LineStrong");
        var colour = new Button { Style = (Style)Application.Current.FindResource("BtnIcon"), Content = _swatch, ToolTip = "Colour: palette, wheel or pick from the screen  (C cycles)" };
        colour.Click += (_, _) => OpenColours(colour);
        row.Children.Add(colour);
        if (_lastColour is { } last) _current = last;

        _undo = new Button
        {
            Style = (Style)Application.Current.FindResource("BtnIcon"),
            Content = new Icon { Data = Geometry("IcUndo2"), Width = 14, Height = 14 },
            ToolTip = "Undo the last mark  (Ctrl+Z)",
            IsEnabled = false,
        };
        _undo.Click += (_, _) => UndoRequested?.Invoke();
        row.Children.Add(_undo);
        row.Children.Add(Divider());

        _pointer = new ToggleButton
        {
            Style = (Style)Application.Current.FindResource("Chip"),
            Content = new Icon { Data = Geometry("IcMousePointer"), Width = 14, Height = 14 },
            ToolTip = "Include the mouse pointer",
            IsChecked = pointer,
            Margin = new Thickness(0, 0, 4, 0),
        };
        _pointer.Click += (_, _) => Changed?.Invoke();
        row.Children.Add(_pointer);

        _magnifier = new ToggleButton
        {
            Style = (Style)Application.Current.FindResource("Chip"),
            Content = new Icon { Data = Geometry("IcZoomIn"), Width = 14, Height = 14 },
            ToolTip = "Magnifier  (M)",
            IsChecked = CaptureOverlay.MagnifierOn,
            Margin = new Thickness(0, 0, 4, 0),
        };
        _magnifier.Click += (_, _) => Changed?.Invoke();
        row.Children.Add(_magnifier);

        _tighten = new ToggleButton
        {
            Style = (Style)Application.Current.FindResource("Chip"),
            Content = new Icon { Data = Geometry("IcScan"), Width = 14, Height = 14 },
            ToolTip = "Fit the box to what's inside it: drag roughly, the plain edges are trimmed  (T)",
            IsChecked = CaptureOverlay.TightenOn,
            Margin = new Thickness(0, 0, 4, 0),
        };
        _tighten.Click += (_, _) => Changed?.Invoke();
        row.Children.Add(_tighten);

        var close = new Button
        {
            Style = (Style)Application.Current.FindResource("BtnIcon"),
            Content = new Icon { Data = Geometry("IcX"), Width = 13, Height = 13 },
            ToolTip = "Cancel  (Esc)",
        };
        close.Click += (_, _) => CloseRequested?.Invoke();
        row.Children.Add(close);

        Child = row;
        UpdateSwatch();
    }

    private static Geometry Geometry(string key) => (Geometry)Application.Current.FindResource(key);

    private static FrameworkElement Labelled(string icon, string text)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new Icon { Data = Geometry(icon), Width = 13, Height = 13, Margin = new Thickness(0, 0, 6, 0) });
        panel.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    private static FrameworkElement Divider()
    {
        var line = new Border { Width = 1, Height = 18, Margin = new Thickness(8, 0, 8, 0) };
        line.SetResourceReference(Border.BackgroundProperty, "Line");
        return line;
    }

    public void SetPick(CapturePick pick)
    {
        Pick = pick;
        SetTool(CaptureTool.None);
        _syncing = true;
        if (_picks.TryGetValue(pick, out var button)) button.IsChecked = true;
        _syncing = false;
        Changed?.Invoke();
    }

    public void SetTool(CaptureTool tool)
    {
        Tool = tool;
        foreach (var (t, button) in _tools) button.IsChecked = t == tool;
        Changed?.Invoke();
    }

    public void NextColour()
    {
        _colour = (Array.IndexOf(Colours, _current) + 1) % Colours.Length;
        SetColour(Colours[_colour]);
    }

    public void SetColour(Color colour)
    {
        _current = colour;
        _lastColour = colour;
        UpdateSwatch();
        if (_hex != null && !_hex.IsKeyboardFocused) _hex.Text = Theming.ToHex(colour);
        Changed?.Invoke();
    }

    /// <summary>The colour popup: palette, a colour wheel with brightness, a hex box and the screen picker.</summary>
    private void OpenColours(UIElement target)
    {
        if (_wheel == null)
        {
            var palette = new WrapPanel { Width = 180, Margin = new Thickness(0, 0, 0, 10) };
            foreach (var c in Colours)
            {
                var swatch = new Button
                {
                    Width = 22, Height = 22, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(0),
                    Background = new SolidColorBrush(c), BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand, ToolTip = Theming.ToHex(c),
                    Template = SwatchTemplate(),
                };
                swatch.SetResourceReference(Control.BorderBrushProperty, "LineStrong");
                var chosen = c;
                swatch.Click += (_, _) => { SetColour(chosen); _colourPopup.IsOpen = false; };
                palette.Children.Add(swatch);
            }

            _wheel = new ColorWheel { Width = 180, Height = 180 };
            _brightness = new Slider { Minimum = 10, Maximum = 100, Value = 100, Margin = new Thickness(0, 10, 0, 0), ToolTip = "Brightness" };
            void FromWheel() => SetColour(Theming.FromHsv(_wheel.Hue, _wheel.Saturation, _brightness.Value / 100));
            _wheel.ColorChanged += FromWheel;
            _brightness.ValueChanged += (_, _) => { if (_brightness.IsMouseOver || _brightness.IsKeyboardFocusWithin) FromWheel(); };

            _hex = new TextBox { Text = Theming.ToHex(_current), Width = 92, ToolTip = "A colour code like #FF8800, then Enter" };
            _hex.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                if (Theming.TryParseHex(_hex.Text, out var typed)) SetColour(typed);
                e.Handled = true;
            };
            var pick = new Button
            {
                Style = (Style)Application.Current.FindResource("Btn"),
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children = { new Icon { Data = Geometry("IcPipette"), Width = 13, Height = 13, Margin = new Thickness(0, 0, 6, 0) }, new TextBlock { Text = "Pick" } },
                },
                ToolTip = "Pick a colour from the screen: click anywhere after this",
                Margin = new Thickness(8, 0, 0, 0),
            };
            pick.Click += (_, _) => { _colourPopup.IsOpen = false; PickFromScreenRequested?.Invoke(); };

            var border = new Border
            {
                Padding = new Thickness(14),
                Margin = new Thickness(8),
                BorderThickness = new Thickness(1),
                Child = new StackPanel
                {
                    Children =
                    {
                        palette, _wheel, _brightness,
                        new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0), Children = { _hex, pick } },
                    },
                },
            };
            border.SetResourceReference(Border.BackgroundProperty, "Panel");
            border.SetResourceReference(Border.BorderBrushProperty, "LineStrong");
            border.SetResourceReference(Border.CornerRadiusProperty, "RadiusCard");
            _colourPopup.Child = border;
            _colourPopup.AllowsTransparency = true;
            _colourPopup.StaysOpen = false;
            _colourPopup.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            _colourPopup.VerticalOffset = 6;
        }
        var (h, s, v) = Theming.ToHsv(_current);
        _wheel.SetHueSaturation(h, s);
        _brightness!.Value = Math.Max(10, v * 100);
        _hex!.Text = Theming.ToHex(_current);
        _colourPopup.PlacementTarget = target;
        _colourPopup.IsOpen = true;
    }

    /// <summary>A plain round swatch, without the button's own look.</summary>
    private static ControlTemplate SwatchTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(11));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        template.VisualTree = border;
        return template;
    }

    public void ToggleTighten()
    {
        _tighten.IsChecked = !Tighten;
        Changed?.Invoke();
    }

    public void ToggleMagnifier()
    {
        _magnifier.IsChecked = !Magnifier;
        Changed?.Invoke();
    }

    public void SetCanUndo(bool canUndo) => _undo.IsEnabled = canUndo;

    private static readonly (string Label, double Ratio, Size? Size)[] Shapes =
    {
        ("Any size", 0, null), ("16:9", 16 / 9.0, null), ("4:3", 4 / 3.0, null), ("1:1", 1, null), ("9:16", 9 / 16.0, null),
        ("1920 × 1080", 0, new Size(1920, 1080)), ("1280 × 720", 0, new Size(1280, 720)),
        ("1080 × 1080", 0, new Size(1080, 1080)), ("800 × 600", 0, new Size(800, 600)), ("512 × 512", 0, new Size(512, 512)),
    };

    private void ShowShapes()
    {
        var menu = new ContextMenu { PlacementTarget = _shape, Placement = PlacementMode.Bottom };
        foreach (var (label, ratio, size) in Shapes)
        {
            if (label == "1920 × 1080") menu.Items.Add(new Separator());
            var item = new MenuItem { Header = label, IsChecked = _shapeText.Text == label };
            item.Click += (_, _) =>
            {
                Ratio = ratio;
                FixedSize = size;
                _shapeText.Text = label;
                SetPick(CapturePick.Region);
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    /// <summary>For the self-test: choose a shape without the menu.</summary>
    internal void SetShapeForTest(double ratio, Size? size)
    {
        Ratio = ratio;
        FixedSize = size;
    }

    private void UpdateSwatch() => _swatch.Background = new SolidColorBrush(Colour);
}
