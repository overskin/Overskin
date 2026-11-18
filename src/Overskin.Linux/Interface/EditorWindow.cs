using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Overskin.Linux.Core;
using Overskin.Linux.Platform;

namespace Overskin.Linux.Interface;

public sealed class EditorWindow : Window
{
    private static readonly string[] Palette = { "#EF4444", "#F59E0B", "#22C55E", "#4F8CFF", "#A855F7", "#FFFFFF", "#111111" };

    private readonly EditorCanvas _canvas;
    private readonly Canvas _textLayer = new();
    private readonly Dictionary<Tool, ToggleButton> _tools = new();
    private readonly List<Border> _swatches = new();
    private readonly TextBlock _status = Ui.Label("", "dim");
    private readonly Button _undo, _redo;
    private string? _savedPath;
    private bool _savedSinceChange;

    public EditorCanvas Canvas => _canvas;

    public EditorWindow(Bitmap picture, string? savedPath)
    {
        _savedPath = savedPath;
        _savedSinceChange = savedPath != null;
        Title = savedPath != null ? Path.GetFileName(savedPath) + " — Overskin" : "Overskin editor";
        Icon = App.Icon;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var screen = Screens.Primary?.WorkingArea.Size ?? new PixelSize(1600, 900);
        double scaling = Screens.Primary?.Scaling ?? 1;
        Width = Math.Clamp(picture.PixelSize.Width / scaling + 80, 760, screen.Width / scaling * 0.85);
        Height = Math.Clamp(picture.PixelSize.Height / scaling + 150, 480, screen.Height / scaling * 0.85);

        _canvas = new EditorCanvas(picture)
        {
            Colour = Imaging.ParseColour(App.Settings.PenColour, Color.Parse(Palette[0])),
            PenWidth = App.Settings.PenWidth,
        };
        _canvas.TextRequested += BeginText;
        _canvas.Changed += () => { _savedSinceChange = false; UpdateButtons(); };

        // tools
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        void AddTool(Tool tool, string icon, string tip)
        {
            var b = new ToggleButton { Content = Ui.Icon(icon), Classes = { "tool" } };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => Pick(tool);
            _tools[tool] = b;
            tools.Children.Add(b);
        }
        AddTool(Tool.Arrow, Icons.Arrow, "Arrow (A)");
        AddTool(Tool.Rectangle, Icons.Rectangle, "Box (R)");
        AddTool(Tool.Ellipse, Icons.Ellipse, "Circle (E)");
        AddTool(Tool.Pen, Icons.Pen, "Pen (P)");
        AddTool(Tool.Highlighter, Icons.Highlighter, "Highlighter (H)");
        AddTool(Tool.Text, Icons.Text, "Text (T)");
        AddTool(Tool.Counter, Icons.Counter, "Numbered steps (N)");
        AddTool(Tool.Pixelate, Icons.Pixelate, "Pixelate (X)");

        // colours
        var colours = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        foreach (var hex in Palette)
        {
            var swatch = new Border
            {
                Width = 20, Height = 20, CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(Color.Parse(hex)),
                BorderThickness = new Thickness(2),
                Cursor = new Cursor(StandardCursorType.Hand),
                Tag = hex,
            };
            swatch.PointerPressed += (_, _) => SetColour(hex);
            _swatches.Add(swatch);
            colours.Children.Add(swatch);
        }

        var width = new Slider { Minimum = 1, Maximum = 20, Value = _canvas.PenWidth, Width = 110, VerticalAlignment = VerticalAlignment.Center };
        width.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            _canvas.PenWidth = width.Value;
            _canvas.PixelBlock = (int)Math.Round(4 + width.Value * 1.5);
            App.Settings.PenWidth = width.Value;
        };
        ToolTip.SetTip(width, "Thickness (and pixel size)");

        _undo = Ui.IconButton(Icons.Undo, "Undo (Ctrl+Z)", _canvas.Undo);
        _redo = Ui.IconButton(Icons.Redo, "Redo (Ctrl+Y)", _canvas.Redo);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                Ui.Button("Pin", PinIt, Icons.Pin),
                Ui.Button("Save as…", () => _ = SaveAsAsync(), null),
                Ui.Button("Save", () => SaveQuick(), Icons.Save),
                Ui.Button("Copy", () => _ = CopyAsync(), Icons.Copy, "primary"),
            },
        };

        var bar = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 14, LineSpacing = 8 };
        bar.Children.Add(tools);
        bar.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { colours, width } });
        bar.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { _undo, _redo } });

        var top = new DockPanel { Margin = new Thickness(12, 10) };
        DockPanel.SetDock(actions, Dock.Right);
        top.Children.Add(actions);
        top.Children.Add(bar);

        var stack = new Grid { Children = { _canvas, _textLayer } };
        _textLayer.Width = _canvas.Width;
        _textLayer.Height = _canvas.Height;
        var view = new Viewbox
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            Child = new Border
            {
                Child = stack,
                BoxShadow = BoxShadows.Parse("0 6 24 0 #66000000"),
            },
            Margin = new Thickness(20),
        };
        var stage = new Border { Background = new SolidColorBrush(Color.Parse("#0C0D0F")), Child = view, ClipToBounds = true };

        _status.Text = $"{picture.PixelSize.Width} × {picture.PixelSize.Height}" + (savedPath != null ? "  ·  saved to " + savedPath : "");
        var footer = new Border { Padding = new Thickness(14, 6), Child = _status };

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(footer);
        root.Children.Add(stage);
        Content = root;

        Pick(Tool.Arrow);
        SetColour(Palette.Contains(App.Settings.PenColour) ? App.Settings.PenColour : Palette[0]);
        UpdateButtons();
        KeyDown += OnKey;
        Closing += (_, _) => App.Settings.Save();
    }

    private void Pick(Tool tool)
    {
        _canvas.Tool = tool;
        foreach (var (t, b) in _tools) b.IsChecked = t == tool;
        CommitText();
    }

    private void SetColour(string hex)
    {
        _canvas.Colour = Color.Parse(hex);
        App.Settings.PenColour = hex;
        foreach (var s in _swatches)
            s.BorderBrush = (string?)s.Tag == hex ? Brushes.White : new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));
    }

    private void UpdateButtons()
    {
        _undo.IsEnabled = _canvas.CanUndo;
        _redo.IsEnabled = _canvas.CanRedo;
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (FocusManager?.GetFocusedElement() is TextBox) return;
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        switch (e.Key)
        {
            case Key.Z when ctrl && e.KeyModifiers.HasFlag(KeyModifiers.Shift): _canvas.Redo(); break;
            case Key.Z when ctrl: _canvas.Undo(); break;
            case Key.Y when ctrl: _canvas.Redo(); break;
            case Key.C when ctrl: _ = CopyAsync(); break;
            case Key.S when ctrl && e.KeyModifiers.HasFlag(KeyModifiers.Shift): _ = SaveAsAsync(); break;
            case Key.S when ctrl: SaveQuick(); break;
            case Key.Escape: Close(); break;
            case Key.A: Pick(Tool.Arrow); break;
            case Key.R: Pick(Tool.Rectangle); break;
            case Key.E: Pick(Tool.Ellipse); break;
            case Key.P: Pick(Tool.Pen); break;
            case Key.H: Pick(Tool.Highlighter); break;
            case Key.T: Pick(Tool.Text); break;
            case Key.N: Pick(Tool.Counter); break;
            case Key.X: Pick(Tool.Pixelate); break;
            default: return;
        }
        e.Handled = true;
    }

    // text: a box on top of the picture until Enter or a click elsewhere
    private TextBox? _typing;
    private Point _typingAt;

    private void BeginText(Point at)
    {
        CommitText();
        double size = Math.Max(16, _canvas.PenWidth * 6);
        _typingAt = at;
        _typing = new TextBox
        {
            FontSize = size,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(_canvas.Colour),
            Background = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)),
            BorderThickness = new Thickness(1),
            MinWidth = 120,
            Padding = new Thickness(0),
            AcceptsReturn = false,
        };
        Avalonia.Controls.Canvas.SetLeft(_typing, at.X);
        Avalonia.Controls.Canvas.SetTop(_typing, at.Y - size * 0.15);
        _typing.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitText(); e.Handled = true; }
            else if (e.Key == Key.Escape) { _textLayer.Children.Clear(); _typing = null; e.Handled = true; }
        };
        _typing.LostFocus += (_, _) => CommitText();
        _textLayer.Children.Add(_typing);
        _typing.Focus();
    }

    private void CommitText()
    {
        if (_typing == null) return;
        var box = _typing;
        _typing = null;
        _textLayer.Children.Clear();
        if (!string.IsNullOrWhiteSpace(box.Text))
            _canvas.Add(new TextMark { At = _typingAt, Text = box.Text!, Size = box.FontSize, Colour = _canvas.Colour });
    }

    public Bitmap Result()
    {
        CommitText();
        return _canvas.Export();
    }

    private async Task CopyAsync()
    {
        bool ok = await Desktop.CopyImageAsync(Result(), this);
        _status.Text = ok ? "Copied to the clipboard" : "Couldn't reach the clipboard. Install wl-clipboard (Wayland) or xclip (X11).";
    }

    private void SaveQuick()
    {
        try
        {
            var path = _savedPath ?? App.Settings.NewFilePath();
            Imaging.SavePng(Result(), path);
            _savedPath = path;
            _savedSinceChange = true;
            Title = Path.GetFileName(path) + " — Overskin";
            _status.Text = "Saved to " + path;
        }
        catch (Exception ex) { _status.Text = "Couldn't save: " + ex.Message; }
    }

    private async Task SaveAsAsync()
    {
        var path = await Ui.PickSavePathAsync(this, Path.GetFileName(_savedPath ?? App.Settings.NewFilePath()));
        if (path == null) return;
        _savedPath = path;
        SaveQuick();
    }

    private void PinIt() => new PinWindow(Result()).Show();
}
