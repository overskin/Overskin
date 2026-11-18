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
    public static readonly Color[] Colours =
    {
        Color.FromRgb(0xEF, 0x44, 0x44), Color.FromRgb(0xFA, 0xCC, 0x15), Color.FromRgb(0x3B, 0x82, 0xF6),
        Color.FromRgb(0x22, 0xC5, 0x5E), Color.FromRgb(0x11, 0x11, 0x11), Colors.White,
    };

    private readonly Dictionary<CapturePick, RadioButton> _picks = new();
    private readonly Dictionary<CaptureTool, ToggleButton> _tools = new();
    private readonly ToggleButton _pointer, _magnifier;
    private readonly Border _swatch;
    private readonly Button _undo;
    private int _colour;
    private bool _syncing;

    public CapturePick Pick { get; private set; }
    /// <summary>Width / height that a dragged area keeps, or 0 for any shape.</summary>
    public double Ratio { get; private set; }
    /// <summary>An exact size in pixels that follows the pointer, or null.</summary>
    public Size? FixedSize { get; private set; }
    private readonly Button? _shape;
    private readonly TextBlock _shapeText = new() { Text = "Any size", VerticalAlignment = VerticalAlignment.Center };
    public CaptureTool Tool { get; private set; }
    public Color Colour => Colours[_colour];
    public bool Pointer => _pointer.IsChecked == true;
    public bool Magnifier => _magnifier.IsChecked == true;

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
        var colour = new Button { Style = (Style)Application.Current.FindResource("BtnIcon"), Content = _swatch, ToolTip = "Colour  (C)" };
        colour.Click += (_, _) => NextColour();
        row.Children.Add(colour);

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
            IsChecked = true,
            Margin = new Thickness(0, 0, 4, 0),
        };
        _magnifier.Click += (_, _) => Changed?.Invoke();
        row.Children.Add(_magnifier);

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
        _colour = (_colour + 1) % Colours.Length;
        UpdateSwatch();
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
