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

public sealed partial class EditorWindow : Window
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
        _canvas.SelectPressed = ToolPress;
        _canvas.SelectDragged = ToolDrag;
        _canvas.SelectReleased = ToolRelease;
        _canvas.SelectHover = ToolCursor;
        _canvas.RightPressed = RightPress;
        _canvas.Overlay = ctx => { DrawTextMarks(ctx); DrawMarkSelection(ctx); };
        _canvas.Changed += () =>
        {
            _savedSinceChange = false;
            // a new picture (crop, erase, turn, undo): its words are read again
            if (!ReferenceEquals(_wordsFor, _canvas.Picture)) PictureChanged();
            if (_selected != null && !_canvas.Marks.Contains(_selected)) _selected = null;
            _textLayer.Width = _canvas.Width;
            _textLayer.Height = _canvas.Height;
            _status.Text = $"{_canvas.PixelSize.Width} × {_canvas.PixelSize.Height}";
            UpdateButtons();
        };

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
        AddTool(Tool.Select, Icons.Select, "Select (V): pick up and move what you've drawn, or select the text in the picture to copy it, type into it or delete letters");
        AddTool(Tool.Move, Icons.Move, "Move (M): lift a word, an icon or a picture off the screenshot and drag it somewhere else");
        AddTool(Tool.Arrow, Icons.Arrow, "Arrow (A)");
        AddTool(Tool.Line, Icons.Line, "Line (L)");
        AddTool(Tool.Rectangle, Icons.Rectangle, "Box (R)");
        AddTool(Tool.Ellipse, Icons.Ellipse, "Circle (E)");
        AddTool(Tool.Pen, Icons.Pen, "Pen (P)");
        AddTool(Tool.Highlighter, Icons.Highlighter, "Highlighter (H)");
        AddTool(Tool.Text, Icons.Text, "Text (T)");
        AddTool(Tool.Counter, Icons.Counter, "Numbered steps (N)");
        AddTool(Tool.Hide, Icons.Pixelate, "Hide: blur, pixelate or mosaic (B)");
        AddTool(Tool.Spotlight, Icons.Spotlight, "Spotlight: darken everything except the boxes you draw (S)");
        AddTool(Tool.Erase, Icons.Eraser, "Smart erase: paint over something to remove it (X)");
        AddTool(Tool.Crop, Icons.Crop, "Crop (C)");

        // the font for text, each name shown in its own look
        var fontPicker = new ComboBox { MinWidth = 150, VerticalAlignment = VerticalAlignment.Center };
        foreach (var f in FontChoices.All)
            fontPicker.Items.Add(new ComboBoxItem { Content = f.Name, FontFamily = f.Family, FontWeight = f.Weight, Tag = f.Name });
        fontPicker.SelectedIndex = Math.Max(0, FontChoices.All.ToList().FindIndex(f => f.Name == App.Settings.TextFont));
        ToolTip.SetTip(fontPicker, "The font for text (T)");
        fontPicker.SelectionChanged += (_, _) =>
        {
            if (fontPicker.SelectedItem is not ComboBoxItem { Tag: string name }) return;
            App.Settings.TextFont = name;
            App.Settings.Save();
            if (_typing != null) { var f = FontChoices.Find(name); _typing.FontFamily = f.Family; _typing.FontWeight = f.Weight; _typing.Tag = f.Name; }
        };
        tools.Children.Add(fontPicker);

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
        colours.Children.Add(AnyColour());

        var width = new Slider { Minimum = 1, Maximum = 20, Value = _canvas.PenWidth, Width = 110, VerticalAlignment = VerticalAlignment.Center };
        width.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            _canvas.PenWidth = width.Value;
            _canvas.HideStrength = Math.Clamp(width.Value * 0.6, 1, 12);
            App.Settings.PenWidth = width.Value;
            _canvas.InvalidateVisual();
        };
        ToolTip.SetTip(width, "Thickness (also how strong Hide is and how big the eraser is)");
        _canvas.HideStrength = Math.Clamp(width.Value * 0.6, 1, 12);

        _hideMode = new ComboBox { ItemsSource = new[] { "Pixelate", "Blur", "Mosaic" }, SelectedIndex = 0, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
        _hideMode.SelectionChanged += (_, _) => _canvas.HideMode = _hideMode.SelectedIndex switch { 1 => HideMode.Blur, 2 => HideMode.Mosaic, _ => HideMode.Pixelate };
        var autoHide = Ui.Button("Auto-hide", () => _ = AutoRedactAsync(), Icons.Text);
        ToolTip.SetTip(autoHide, "Finds private details and hides them. Right-click to choose which.");
        autoHide.ContextMenu = PrivateMenu.Build();

        _undo = Ui.IconButton(Icons.Undo, "Undo (Ctrl+Z)", _canvas.Undo);
        _redo = Ui.IconButton(Icons.Redo, "Redo (Ctrl+Y)", _canvas.Redo);

        // as on Windows: the actions along the bottom, Save the one solid button
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                Look.Button("Pin", PinIt, Lucide.Pin, tip: "Pin on top of everything"),
                Look.Button("Copy", () => _ = CopyAsync(), Lucide.Copy, tip: OperatingSystem.IsMacOS() ? "Copy  (⌘C)" : "Copy  (Ctrl+C)"),
                Look.Button("Save as…", () => _ = SaveAsAsync(), null),
                Look.Button("Save", () => SaveQuick(), Lucide.Save, "accent", OperatingSystem.IsMacOS() ? "Save  (⌘S)" : "Save  (Ctrl+S)"),
            },
        };

        // the toolbar: the tools, then colour and size, then the picture's own buttons, wrapping in a narrow window
        var bar = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 14, LineSpacing = 8 };
        bar.Children.Add(tools);
        bar.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { colours, width, _hideMode } });
        bar.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { autoHide, ImageMenu() } });
        bar.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { _undo, _redo } });

        var top = new Border
        {
            Padding = new Thickness(12, 8), BorderBrush = new SolidColorBrush(Colours.Line), BorderThickness = new Thickness(0, 0, 0, 1),
            Child = bar,
        };

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
        var stage = new Border { Background = new SolidColorBrush(Color.Parse("#0A0A0C")), Child = view, ClipToBounds = true };

        _status.Text = $"{picture.PixelSize.Width} × {picture.PixelSize.Height}" + (savedPath != null ? "  ·  saved to " + savedPath : "");
        var footerRow = new DockPanel();
        var typeBar = TypeBar();
        typeBar.Margin = new Thickness(0, 0, 12, 0);
        DockPanel.SetDock(actions, Dock.Right);
        DockPanel.SetDock(typeBar, Dock.Right);
        footerRow.Children.Add(actions);
        footerRow.Children.Add(typeBar);
        footerRow.Children.Add(_status);
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        _status.TextWrapping = TextWrapping.NoWrap;
        var footer = new Border
        {
            Padding = new Thickness(14, 8), BorderBrush = new SolidColorBrush(Colours.Line), BorderThickness = new Thickness(0, 1, 0, 0),
            Child = footerRow,
        };

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(footer);
        root.Children.Add(stage);
        Content = root;

        Pick(Tool.Select);
        SetColour(Color.TryParse(App.Settings.PenColour, out _) ? App.Settings.PenColour : Palette[0]);
        UpdateButtons();
        KeyDown += OnKey;
        TextInput += OnTextInput;
        Opened += (_, _) => { _ = ReadWordsAsync(); _canvas.Focus(); };
        Closing += (_, _) => App.Settings.Save();
    }

    private readonly ComboBox _hideMode;

    private void Pick(Tool tool)
    {
        _canvas.Tool = tool;
        foreach (var (t, b) in _tools) b.IsChecked = t == tool;
        _hideMode.IsVisible = tool == Tool.Hide;
        if (tool != Tool.Select) ClearTextSelection();
        if (tool is not (Tool.Select or Tool.Move)) Select(null);
        _status.Text = tool switch
        {
            Tool.Select => Ocr.IsAvailable
                ? "Drag over text in the picture to select it, or click in it to type · click what you've drawn to move it"
                : "Click what you've drawn to move it or stretch it · " + Ocr.InstallHint,
            Tool.Move => "Drag a word, an icon or a picture to move it · or drag a box round anything to lift it off",
            Tool.Crop => "Drag over the part to keep",
            Tool.Erase => "Paint over what should disappear: it's filled in from what's around it",
            _ => _status.Text,
        };
        CommitText();
        UpdateTypeBar();
    }

    private Button ImageMenu()
    {
        var menu = new MenuFlyout();
        void Item(string text, Action click)
        {
            var m = new MenuItem { Header = text };
            m.Click += (_, _) => click();
            menu.Items.Add(m);
        }
        Item("Beautify…", () => _ = BeautifyAsync());
        Item("Effects: border, corners, torn edge, shadow, colours…", () => _ = EffectsAsync());
        Item("Cut out the subject (background see-through)", () => _ = CutOutAsync());
        Item("Copy the text in the picture", () => _ = CopyTextAsync());
        menu.Items.Add(new Separator());
        Item("Turn left", () => _canvas.ReplacePicture(p => Effects.Rotate(p, clockwise: false), flatten: true));
        Item("Turn right", () => _canvas.ReplacePicture(p => Effects.Rotate(p, clockwise: true), flatten: true));
        Item("Flip left to right", () => _canvas.ReplacePicture(p => Effects.Flip(p, horizontal: true), flatten: true));
        Item("Flip upside down", () => _canvas.ReplacePicture(p => Effects.Flip(p, horizontal: false), flatten: true));
        Item("Half size", () => _canvas.ReplacePicture(p => Effects.Resize(p, p.PixelSize.Width / 2, p.PixelSize.Height / 2), flatten: true));
        Item("Double size", () => _canvas.ReplacePicture(p => Effects.Resize(p, p.PixelSize.Width * 2, p.PixelSize.Height * 2), flatten: true));
        return new Button { Content = "Image", Flyout = menu };
    }

    private async Task BeautifyAsync()
    {
        CommitText();
        var dialog = new BeautifyWindow(_canvas.Export());
        await dialog.ShowDialog(this);
        if (dialog.Result is { } result) _canvas.ReplacePicture(_ => result, flatten: true);
    }

    /// <summary>Keeps the person, pet or thing in front and makes the background see-through (worked out on this computer).</summary>
    private async Task CutOutAsync()
    {
        if (!Ai.Available) { _status.Text = "This copy of Overskin was built without its AI models."; return; }
        CommitText();
        _status.Text = "Lifting out the subject…";
        try
        {
            var pixels = Ai.Of(_canvas.Export());
            var cut = await Task.Run(() => Ai.CutOut(pixels));
            if (cut is not { } c) { _status.Text = "Nothing stands out enough in this picture to lift out."; return; }
            var picture = Imaging.FromPixels(c.Data, new PixelSize(c.Width, c.Height));
            _canvas.ReplacePicture(_ => picture, flatten: true);
            _status.Text = "Lifted out. It saves and copies with the see-through background.";
        }
        catch (Exception ex)
        {
            Log.Write("cut out: " + ex);
            _status.Text = "Couldn't lift it out: " + ex.Message;
        }
    }

    private async Task EffectsAsync()
    {
        CommitText();
        var dialog = new EffectsWindow(_canvas.Export());
        await dialog.ShowDialog(this);
        if (dialog.Result is { } result) _canvas.ReplacePicture(_ => result, flatten: true);
    }

    private async Task AutoRedactAsync()
    {
        if (!Ocr.IsAvailable) { _status.Text = Ocr.InstallHint; return; }
        _status.Text = "Looking for private details…";
        var found = await PrivateMenu.FindAsync(_canvas.Picture);
        if (found.Count == 0) { _status.Text = "Nothing private found. Right-click Auto-hide to choose what it looks for."; return; }
        _canvas.AddRange(found.Select(f => new PatchMark { Area = new Rect(f.X, f.Y, f.Width, f.Height), Mode = _canvas.HideMode, Strength = Math.Max(4, _canvas.HideStrength) }));
        _status.Text = $"Hid {Overskin.Shared.PrivateDetails.Describe(found.Select(f => f.Kind))}. Undo brings them back.";
    }

    private async Task CopyTextAsync()
    {
        if (!Ocr.IsAvailable) { _status.Text = Ocr.InstallHint; return; }
        _status.Text = "Reading the text…";
        var text = await Ocr.ReadTextAsync(_canvas.Export());
        if (string.IsNullOrWhiteSpace(text)) { _status.Text = "No text found"; return; }
        await Desktop.CopyTextAsync(text, this);
        _status.Text = $"Copied {text.Split('\n').Length} lines of text";
    }

    private void SetColour(string hex)
    {
        _canvas.Colour = Color.Parse(hex);
        App.Settings.PenColour = hex;
        foreach (var s in _swatches)
            s.BorderBrush = (string?)s.Tag == hex ? Brushes.White : new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));
        if (_anyColour != null)
            _anyColour.BorderBrush = Palette.Contains(hex) ? new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)) : Brushes.White;
    }

    private Border? _anyColour;
    private ColourWheel? _wheel;
    private Slider? _brightness;
    private bool _wheelSync;

    /// <summary>The last swatch: any colour at all, from a colour wheel and a brightness slider.</summary>
    private Control AnyColour()
    {
        _wheel = new ColourWheel { Width = 176, Height = 176 };
        _brightness = new Slider { Minimum = 10, Maximum = 100, Value = 100, Margin = new Thickness(0, 12, 0, 0) };
        _wheel.ColourChanged += WheelChanged;
        _brightness.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) WheelChanged(); };
        var flyout = new Flyout
        {
            Placement = PlacementMode.Bottom,
            Content = new StackPanel { Margin = new Thickness(6), Children = { _wheel, _brightness } },
        };
        _anyColour = new Border
        {
            Width = 20, Height = 20, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(2),
            Background = new ImageBrush(ColourWheel.CreateBitmap(40)) { Stretch = Stretch.UniformToFill },
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(_anyColour, "Any colour");
        _anyColour.PointerPressed += (_, _) =>
        {
            var (h, sat, v) = ColourWheel.ToHsv(_canvas.Colour);
            _wheelSync = true;
            _wheel.SetHueSaturation(h, sat);
            _brightness.Value = Math.Max(10, v * 100);
            _wheelSync = false;
            flyout.ShowAt(_anyColour);
        };
        return _anyColour;
    }

    private void WheelChanged()
    {
        if (_wheelSync || _wheel == null || _brightness == null) return;
        SetColour(ColourWheel.ToHex(ColourWheel.FromHsv(_wheel.Hue, _wheel.Saturation, _brightness.Value / 100)));
    }

    /// <summary>For the self-test: a click on the colour wheel.</summary>
    internal Color PickWheelForTest(Point p) { _wheel!.PickForTest(p); return _canvas.Colour; }

    private void UpdateButtons()
    {
        _undo.IsEnabled = _canvas.CanUndo;
        _redo.IsEnabled = _canvas.CanRedo;
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (FocusManager?.GetFocusedElement() is TextBox) return;
        // Ctrl on Windows and Linux, ⌘ on a Mac
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (TextKey(e, ctrl)) { e.Handled = true; return; }
        if (_selected != null && e.Key is Key.Delete or Key.Back) { DeleteSelected(); e.Handled = true; return; }
        if (_selected != null && e.Key == Key.Escape) { Select(null); e.Handled = true; return; }
        // with a text cursor, letters type rather than switch tools (Esc first to use the shortcuts)
        if (!ctrl && (HasTextCaret || HasTextSelection) && e.Key is >= Key.A and <= Key.Z) return;
        switch (e.Key)
        {
            case Key.Z when ctrl && e.KeyModifiers.HasFlag(KeyModifiers.Shift): _canvas.Redo(); break;
            case Key.Z when ctrl: _canvas.Undo(); break;
            case Key.Y when ctrl: _canvas.Redo(); break;
            case Key.C when ctrl: _ = CopyAsync(); break;
            case Key.S when ctrl && e.KeyModifiers.HasFlag(KeyModifiers.Shift): _ = SaveAsAsync(); break;
            case Key.S when ctrl: SaveQuick(); break;
            case Key.Escape: Close(); break;
            case Key.V when !ctrl: Pick(Tool.Select); break;
            case Key.M when !ctrl: Pick(Tool.Move); break;
            case Key.A: Pick(Tool.Arrow); break;
            case Key.R: Pick(Tool.Rectangle); break;
            case Key.E: Pick(Tool.Ellipse); break;
            case Key.P: Pick(Tool.Pen); break;
            case Key.H: Pick(Tool.Highlighter); break;
            case Key.T: Pick(Tool.Text); break;
            case Key.N: Pick(Tool.Counter); break;
            case Key.B: Pick(Tool.Hide); break;
            case Key.L: Pick(Tool.Line); break;
            case Key.S: Pick(Tool.Spotlight); break;
            case Key.X: Pick(Tool.Erase); break;
            case Key.C: Pick(Tool.Crop); break;
            default: return;
        }
        e.Handled = true;
    }

    // text: a box on top of the picture until Enter or a click elsewhere
    private TextBox? _typing;
    private Point _typingAt;
    /// <summary>The text mark being edited (taken out while its box is open), and where it was in the list.</summary>
    private (TextMark Mark, int Index)? _editing;

    /// <summary>Double-click (or right-click › Edit) on text that was added: its box opens again.</summary>
    private void EditText(TextMark mark)
    {
        CommitText();
        int index = _canvas.Marks.IndexOf(mark);
        if (index < 0) return;
        Select(null);
        _canvas.RemoveQuietly(mark);
        BeginText(mark.At, mark);
        _editing = (mark, index);
    }

    private void BeginText(Point at) => BeginText(at, null);

    private void BeginText(Point at, TextMark? from)
    {
        CommitText();
        double size = from?.Size ?? Math.Max(16, _canvas.PenWidth * 6);
        _typingAt = at;
        var font = FontChoices.Find(from?.FontName ?? App.Settings.TextFont);
        _typing = new TextBox
        {
            Text = from?.Text ?? "",
            FontSize = size,
            FontFamily = font.Family,
            FontWeight = font.Weight,
            Tag = font.Name,
            Foreground = new SolidColorBrush(from?.Colour ?? _canvas.Colour),
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
            else if (e.Key == Key.Escape) { CancelText(); e.Handled = true; }
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
        var colour = box.Foreground is ISolidColorBrush b ? b.Color : _canvas.Colour;
        var mark = string.IsNullOrWhiteSpace(box.Text) ? null
            : new TextMark { At = _typingAt, Text = box.Text!, Size = box.FontSize, Colour = colour, FontName = box.Tag as string ?? App.Settings.TextFont };
        if (_editing is { } editing)
        {
            // the old text goes back where it was, then is swapped (or removed) as one undo step
            _editing = null;
            _canvas.InsertQuietly(editing.Index, editing.Mark);
            if (mark == null) _canvas.Remove(editing.Mark);
            else if (mark.Text != editing.Mark.Text || mark.FontName != editing.Mark.FontName || mark.Colour != editing.Mark.Colour) _canvas.Replace(editing.Mark, mark);
            return;
        }
        if (mark != null) _canvas.Add(mark);
    }

    private void CancelText()
    {
        _textLayer.Children.Clear();
        _typing = null;
        if (_editing is { } editing) _canvas.InsertQuietly(editing.Index, editing.Mark);
        _editing = null;
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
