using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Overskin.Linux.Interface;

/// <summary>
/// One outline icon (24 x 24 grid, as on Windows), drawn as a thin round-capped stroke that stays
/// the same thickness at any size. Its colour follows the text colour around it, so an icon inside a
/// button follows the button, as the Windows app's Icon does.
/// </summary>
public sealed class LineIcon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty = AvaloniaProperty.Register<LineIcon, Geometry?>(nameof(Data));
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<LineIcon>();
    public static readonly StyledProperty<double> StrokeWidthProperty = AvaloniaProperty.Register<LineIcon, double>(nameof(StrokeWidth), 1.45);

    static LineIcon()
    {
        AffectsRender<LineIcon>(DataProperty, ForegroundProperty, StrokeWidthProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<LineIcon>(false);
    }

    public Geometry? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double StrokeWidth { get => GetValue(StrokeWidthProperty); set => SetValue(StrokeWidthProperty, value); }

    private static readonly Dictionary<string, Geometry> Parsed = new();

    /// <summary>The geometry for an icon's path text, parsed once.</summary>
    public static Geometry Shape(string data)
    {
        lock (Parsed)
        {
            if (!Parsed.TryGetValue(data, out var g)) Parsed[data] = g = StreamGeometry.Parse(data);
            return g;
        }
    }

    public override void Render(DrawingContext context)
    {
        if (Data == null) return;
        double size = Math.Min(Bounds.Width, Bounds.Height);
        if (size < 1) return;
        double scale = size / 24;
        var pen = new Pen(Foreground ?? Brushes.White, StrokeWidth / scale) { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation((Bounds.Width - size) / 2, (Bounds.Height - size) / 2)))
            context.DrawGeometry(null, pen, Data);
    }
}

/// <summary>The Windows app's switch: a track with a knob that sits left (off) or slides right (on).</summary>
public sealed class OnOff : Control
{
    private bool _on, _hover;
    private double _knob; // 0 off … 1 on, eased between
    private DispatcherTimer? _slide;

    public event Action<bool>? Toggled;

    public OnOff(bool on)
    {
        _on = on;
        _knob = on ? 1 : 0;
        Width = 34;
        Height = 22;
        Cursor = new Cursor(StandardCursorType.Hand);
        VerticalAlignment = VerticalAlignment.Center;
        PointerEntered += (_, _) => { _hover = true; InvalidateVisual(); };
        PointerExited += (_, _) => { _hover = false; InvalidateVisual(); };
        PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left || !IsEnabled) return;
            if (!new Rect(Bounds.Size).Contains(e.GetPosition(this))) return;
            IsOn = !IsOn;
            Toggled?.Invoke(IsOn);
        };
    }

    public bool IsOn
    {
        get => _on;
        set
        {
            if (value == _on) return;
            _on = value;
            _slide?.Stop();
            var start = DateTime.Now;
            double from = _knob, to = value ? 1 : 0;
            _slide = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
            _slide.Tick += (_, _) =>
            {
                double t = Math.Min(1, (DateTime.Now - start).TotalMilliseconds / 160);
                double eased = 1 - Math.Pow(1 - t, 3);
                _knob = from + (to - from) * eased;
                InvalidateVisual();
                if (t >= 1) _slide?.Stop();
            };
            _slide.Start();
        }
    }

    public override void Render(DrawingContext context)
    {
        double y = (Bounds.Height - 18) / 2;
        var track = new RoundedRect(new Rect(0.5, y + 0.5, 33, 17), new CornerRadius(9, 2, 9, 2));
        var fill = _on ? Colours.Accent : Colours.Field;
        var line = _on ? Colours.Accent : Colours.LineStrong;
        context.DrawRectangle(new SolidColorBrush(fill), new Pen(new SolidColorBrush(line), 1), track);
        var knobColour = _on ? Colours.AccentInk : _hover ? Colours.Text : Colours.Dim;
        double x = 3 + 16 * _knob;
        context.DrawRectangle(new SolidColorBrush(knobColour), null, new RoundedRect(new Rect(x, y + 3, 12, 12), new CornerRadius(6, 1, 6, 1)));
        if (!IsEnabled) Opacity = 0.4;
    }
}

/// <summary>
/// The Windows app's building blocks, the same sizes, colours and shapes: cards (that fold away to
/// their heading and a summary), rows with a switch or a choice on the right, either/or chips,
/// capture tiles and tool rows.
/// </summary>
public static class Look
{
    /// <summary>Raised when a setting changes through one of these rows, so folded cards' summaries stay current.</summary>
    public static event Action? Changed;

    public static void NoteChanged() => Changed?.Invoke();

    public static LineIcon Icon(string data, double size = 15, IBrush? colour = null)
    {
        var icon = new LineIcon { Data = LineIcon.Shape(data), Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        if (colour != null) icon.Foreground = colour;
        return icon;
    }

    public static IBrush Brush(Color c) => new SolidColorBrush(c);

    /// <summary>A card's heading: a quiet icon, then the words.</summary>
    public static Control Heading(string icon, string text) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Children =
        {
            new Border { Width = 14, Height = 14, Margin = new Thickness(0, 0, 8, 0), Child = Icon(icon, 14, Brush(Colours.Dim)) },
            new TextBlock { Text = text, Classes = { "heading" } },
        },
    };

    /// <summary>The thin line under a card's heading.</summary>
    public static Border Rule() => new() { Height = 1, Background = Brush(Colours.Line), Margin = new Thickness(0, 9, 0, 8) };

    /// <summary>A card: heading, a rule, then its rows.</summary>
    public static Border Card(string icon, string title, params Control[] rows)
    {
        var stack = new StackPanel { Children = { Heading(icon, title), Rule() } };
        foreach (var r in rows) stack.Children.Add(r);
        return new Border { Classes = { "card" }, Child = stack };
    }

    private static readonly Dictionary<string, bool> OpenCards = new();

    /// <summary>
    /// A card that folds away to its heading with a short summary of its settings and a chevron,
    /// as on Windows. Click the heading to open or close it.
    /// </summary>
    public static Border Fold(string icon, string title, Func<string> summary, params Control[] rows)
    {
        bool open = OpenCards.TryGetValue(title, out var o) && o;
        var said = new TextBlock
        {
            FontSize = 11.5, Foreground = Brush(Colours.Faint), VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(12, 0, 10, 0), HorizontalAlignment = HorizontalAlignment.Right,
        };
        var chevron = Icon(Lucide.ChevronRight, 13, Brush(Colours.Dim));
        chevron.RenderTransform = new RotateTransform(open ? 90 : 0);
        var head = new DockPanel { Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand), Height = 22 };
        DockPanel.SetDock(chevron, Dock.Right);
        head.Children.Add(chevron);
        var title_ = Heading(icon, title);
        DockPanel.SetDock(title_, Dock.Left);
        head.Children.Add(title_);
        head.Children.Add(said);
        var body = new StackPanel { IsVisible = open, Children = { Rule() } };
        foreach (var r in rows) body.Children.Add(r);
        void Describe() { try { said.Text = body.IsVisible ? "" : summary(); } catch { said.Text = ""; } }
        Describe();
        head.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            body.IsVisible = !body.IsVisible;
            OpenCards[title] = body.IsVisible;
            chevron.RenderTransform = new RotateTransform(body.IsVisible ? 90 : 0);
            Describe();
        };
        var card = new Border { Classes = { "card" }, Child = new StackPanel { Children = { head, body } } };
        void OnChanged() => Dispatcher.UIThread.Post(Describe);
        card.AttachedToVisualTree += (_, _) => Changed += OnChanged;
        card.DetachedFromVisualTree += (_, _) => Changed -= OnChanged;
        return card;
    }

    /// <summary>A row: the label on the left, its control on the right (31 high, as on Windows).</summary>
    public static Control Row(string label, Control control, string? tip = null)
    {
        var row = new DockPanel { MinHeight = 31 };
        control.VerticalAlignment = VerticalAlignment.Center;
        control.HorizontalAlignment = HorizontalAlignment.Right;
        DockPanel.SetDock(control, Dock.Right);
        row.Children.Add(control);
        row.Children.Add(new TextBlock { Text = label, Classes = { "label" }, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0) });
        if (tip != null) ToolTip.SetTip(row, tip);
        return row;
    }

    /// <summary>A setting that's on or off: the words, and a switch on the right.</summary>
    public static Control Switch(string label, bool value, Action<bool> set, string? tip = null)
    {
        var sw = new OnOff(value);
        sw.Toggled += v => { set(v); App.Settings.Save(); NoteChanged(); };
        return Row(label, sw, tip);
    }

    /// <summary>A quieter line of explanation under a row.</summary>
    public static TextBlock Note(string text) => new()
    {
        Text = text, Classes = { "dim" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6), FontSize = 11.5,
    };

    /// <summary>An either/or group of chips (one is chosen), as on Windows.</summary>
    public static StackPanel Segs(IReadOnlyList<string> items, int selected, Action<int> set, IReadOnlyList<string?>? icons = null, bool save = true)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        var buttons = new List<ToggleButton>();
        for (int i = 0; i < items.Count; i++)
        {
            int index = i;
            object content = items[i];
            if (icons != null && i < icons.Count && icons[i] is { } ic)
                content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { Icon(ic, 13), new TextBlock { Text = items[i], VerticalAlignment = VerticalAlignment.Center } } };
            var b = new ToggleButton { Content = content, Classes = { "seg" }, IsChecked = i == selected };
            b.Click += (_, _) =>
            {
                foreach (var other in buttons) other.IsChecked = other == b;
                set(index);
                if (save) App.Settings.Save();
                NoteChanged();
            };
            buttons.Add(b);
            panel.Children.Add(b);
        }
        return panel;
    }

    /// <summary>A drop-down choice that saves when changed.</summary>
    public static ComboBox Choice(IReadOnlyList<string> items, int selected, Action<int> set, double width = 190)
    {
        var c = new ComboBox { ItemsSource = items, SelectedIndex = Math.Max(0, selected), Width = width };
        c.SelectionChanged += (_, _) => { if (c.SelectedIndex >= 0) { set(c.SelectedIndex); App.Settings.Save(); NoteChanged(); } };
        return c;
    }

    /// <summary>A slider that saves when moved.</summary>
    public static Slider Slider(double min, double max, double value, Action<int> set, double width = 190)
    {
        var s = new Slider { Minimum = min, Maximum = max, Value = value, Width = width };
        s.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            set((int)Math.Round(s.Value));
            App.Settings.Save();
            NoteChanged();
        };
        return s;
    }

    /// <summary>A big capture tile: an icon in a tinted square, the name, and its hotkey under it.</summary>
    public static Button Tile(string icon, string title, string? sub, Action click, string? tip = null)
    {
        var b = new Button
        {
            Classes = { "tile" },
            Content = new StackPanel
            {
                Children =
                {
                    new Border
                    {
                        Width = 36, Height = 36, HorizontalAlignment = HorizontalAlignment.Left,
                        Background = Brush(Colours.AccentSoft), BorderBrush = Brush(Colours.AccentLine), BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(9, 2, 9, 2),
                        Child = Icon(icon, 17, Brush(Colours.Accent)),
                    },
                    new TextBlock { Text = title, Classes = { "heading" }, Margin = new Thickness(0, 12, 0, 0) },
                    new TextBlock { Text = sub ?? "", Classes = { "dim" }, FontSize = 11, Margin = new Thickness(0, 2, 0, 0), MinHeight = 14 },
                },
            },
        };
        if (tip != null) ToolTip.SetTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>One tool in a card's grid of tools: an accent icon and its name.</summary>
    public static Button ToolRow(string icon, string title, Action click, string? tip = null)
    {
        var b = new Button
        {
            Classes = { "toolrow" },
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { new Border { Margin = new Thickness(0, 0, 10, 0), Child = Icon(icon, 16, Brush(Colours.Accent)) }, new TextBlock { Text = title, Classes = { "label" }, FontWeight = FontWeight.SemiBold } },
            },
        };
        if (tip != null) ToolTip.SetTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>A button with an icon before its words.</summary>
    public static Button Button(string text, Action click, string? icon = null, string? cls = null, string? tip = null)
    {
        object content = text;
        if (icon != null)
            content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { Icon(icon, 13), new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } } };
        var b = new Button { Content = content };
        if (cls != null) b.Classes.Add(cls);
        if (tip != null) ToolTip.SetTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>A square icon-only button (quiet until hovered).</summary>
    public static Button IconButton(string icon, string tip, Action click, double size = 14)
    {
        var b = new Button { Content = Icon(icon, size), Classes = { "icon" } };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>A key chip, as in the Pins page's list of shortcuts.</summary>
    public static Border Key(string text) => new()
    {
        Background = Brush(Colours.Field), CornerRadius = new CornerRadius(6, 1, 6, 1), Padding = new Thickness(7, 3),
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 11.5, FontWeight = FontWeight.SemiBold },
    };

    /// <summary>The app mark: one leaf-shaped sheet sitting on another, as in the Windows title bar.</summary>
    public static Control Mark() => new Canvas
    {
        Width = 17, Height = 17, VerticalAlignment = VerticalAlignment.Center,
        Children =
        {
            new Border { Width = 11, Height = 11, BorderBrush = Brush(Colours.Dim), BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(5, 1, 5, 1) },
            Placed(new Border
            {
                Width = 12.5, Height = 12.5, Background = Brush(Colours.Accent), BorderBrush = Brush(Colours.Bg),
                BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(6, 1.5, 6, 1.5),
            }, 4.5, 4.5),
        },
    };

    private static Control Placed(Control c, double x, double y)
    {
        Canvas.SetLeft(c, x);
        Canvas.SetTop(c, y);
        return c;
    }
}
