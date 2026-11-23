using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Overskin;

/// <summary>What the editor can ask the rest of the app to do with the finished picture.</summary>
public sealed class EditorHost
{
    /// <summary>Saves into the screenshots folder and returns the new file's path (or null on failure).</summary>
    public required Func<BitmapSource, string?> SaveNew { get; init; }
    public required Action<BitmapSource, string> Pin { get; init; }
    /// <summary>Called after any save, so the library can refresh.</summary>
    public Action<string>? Saved { get; init; }
    public string Folder { get; init; } = "";
}

/// <summary>Marks up a screenshot: arrows, boxes, text, steps, pixelate and crop.</summary>
public partial class EditorWindow : Window
{
    private enum Tool { Select, Arrow, Rectangle, Ellipse, Line, Pen, Highlighter, Text, Pixelate, Erase, Crop, Move }

    private static readonly (string Name, string Hex)[] Colours =
    {
        ("Red", "#EF4444"), ("Orange", "#F97316"), ("Yellow", "#FACC15"), ("Green", "#22C55E"),
        ("Blue", "#3B82F6"), ("Purple", "#A855F7"), ("Pink", "#EC4899"), ("Black", "#111111"), ("White", "#FFFFFF"),
    };

    // remembered for the next editor in this session
    private static string _lastColour = "#EF4444";
    private static double _lastSize = 3;
    private static double _lastOpacity = 1;
    private double _opacity = 1;
    private static Tool _lastTool = Tool.Arrow;
    private static HideMode _lastHide = HideMode.Pixelate;

    private readonly EditorHost _host;
    private BitmapSource _base;
    private List<Annotation> _items = new();
    private readonly Stack<(BitmapSource Base, List<Annotation> Items)> _undo = new();
    private readonly Stack<(BitmapSource Base, List<Annotation> Items)> _redo = new();

    private Tool _tool;
    private Color _colour;
    private double _size;
    private bool _syncing;

    private bool _down;
    private Point _start, _last;
    private Annotation? _drawing;
    private Annotation? _selected;
    private bool _moved;
    private Rectangle? _cropBox;
    private TextBox? _textBox;
    private Annotation? _textOriginal;
    private bool _dirty;

    public string? FilePath { get; private set; }

    public EditorWindow(BitmapSource image, string? path, string name, EditorHost host)
    {
        InitializeComponent();
        AutoHideButton.ContextMenu = Redaction.KindsMenu();
        _host = host;
        _base = image;
        FilePath = path;
        TitleText.Text = name;

        foreach (var (cname, hex) in Colours)
        {
            Theming.TryParseHex(hex, out var c);
            var swatch = new RadioButton { Style = (Style)FindResource("Pal"), Background = new SolidColorBrush(c), Tag = hex, ToolTip = cname };
            swatch.Checked += (_, _) => { if (!_syncing) SetColour(hex); };
            Palette.Children.Add(swatch);
        }

        _syncing = true;
        SizeSlider.Value = _lastSize;
        OpacitySlider.Value = Math.Round(_lastOpacity * 100);
        OpacityText.Text = $"{OpacitySlider.Value:0}%";
        _syncing = false;
        _size = _lastSize;
        _opacity = _lastOpacity;
        SetColour(_lastColour);
        SelectTool(Tool.Select); // opens ready to click into the text and edit it
        ApplyBase();
        Render();

        SizeChanged += (_, _) => UpdateZoom();
        Wheel.ColorChanged += WheelChanged;
        WheelSwatch.Background = new ImageBrush(ColorWheel.CreateBitmap(48));
        Loaded += (_, _) =>
        {
            UpdateZoom();
            // settle in: the canvas fades up as the window appears
            Frame.Opacity = 0;
            Frame.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // rounded corners on Windows 11; ignored elsewhere
        int round = 2;
        NativeMethods.DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 33, ref round, sizeof(int));
    }

    // ---- document ------------------------------------------------------------------------

    private void ApplyBase()
    {
        BaseImage.Source = _base;
        Doc.Width = Shadow.Width = _base.PixelWidth;
        Doc.Height = Shadow.Height = _base.PixelHeight;
        SizeText.Text = $"{_base.PixelWidth} × {_base.PixelHeight}";
        UpdateZoom();
        if (_wordsFor != _base) PictureChanged();
    }

    private double _zoom = 1;
    private bool _fit = true;

    private double Zoom => _zoom;

    /// <summary>Largest zoom (up to 100%) at which the whole picture fits.</summary>
    private double FitZoom
    {
        get
        {
            double w = Surface.ActualWidth - 60, h = Surface.ActualHeight - 60;
            if (w <= 0 || h <= 0) return 1;
            return Math.Min(1, Math.Min(w / _base.PixelWidth, h / _base.PixelHeight));
        }
    }

    private void SetZoom(double zoom, bool fit)
    {
        _fit = fit;
        _zoom = Math.Clamp(zoom, 0.05, 8);
        StageScale.ScaleX = StageScale.ScaleY = _zoom;
        ZoomText.Text = $"{Math.Round(_zoom * 100)}%";
        ShowSelection();
    }

    private void UpdateZoom()
    {
        if (_fit) SetZoom(FitZoom, true);
        else SetZoom(_zoom, false);
    }

    /// <summary>Fit and 100%, back and forth.</summary>
    private void ZoomButton_Click(object sender, RoutedEventArgs e)
    {
        if (_fit) SetZoom(1, false);
        else SetZoom(FitZoom, true);
    }

    /// <summary>Ctrl+scroll zooms around the mouse; plain scroll scrolls.</summary>
    private void Surface_Wheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        e.Handled = true;
        var under = e.GetPosition(Stage);            // picture point under the mouse
        var mouse = e.GetPosition(Scroller);
        SetZoom(_zoom * (e.Delta > 0 ? 1.15 : 1 / 1.15), false);
        Stage.UpdateLayout();
        var now = Stage.TranslatePoint(under, Scroller);
        Scroller.ScrollToHorizontalOffset(Scroller.HorizontalOffset + now.X - mouse.X);
        Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset + now.Y - mouse.Y);
    }

    /// <summary>Redraws every mark, plus the one being drawn.</summary>
    private void Render()
    {
        Ink.Children.Clear();
        foreach (var a in _items) Ink.Children.Add(a.ToElement());
        if (_drawing != null && _drawing.Points.Count > 0) Ink.Children.Add(_drawing.ToElement());
        UndoButton.IsEnabled = _undo.Count > 0;
        RedoButton.IsEnabled = _redo.Count > 0;
    }

    private void PushUndo()
    {
        _undo.Push((_base, _items.Select(a => a.Clone()).ToList()));
        _redo.Clear();
        _dirty = true;
    }

    private void Undo()
    {
        CommitText();
        if (_undo.Count == 0) return;
        _redo.Push((_base, _items.Select(a => a.Clone()).ToList()));
        (_base, _items) = _undo.Pop();
        Restored();
    }

    private void Redo()
    {
        CommitText();
        if (_redo.Count == 0) return;
        _undo.Push((_base, _items.Select(a => a.Clone()).ToList()));
        (_base, _items) = _redo.Pop();
        Restored();
    }

    private void Restored()
    {
        _selected = null;
        _dirty = true;
        ApplyBase();
        Render();
        ShowSelection();
    }

    // ---- tools, colour, size -------------------------------------------------------------

    private RadioButton ToolButton(Tool t) => t switch
    {
        Tool.Select => ToolSelect, Tool.Arrow => ToolArrow, Tool.Rectangle => ToolRectangle, Tool.Ellipse => ToolEllipse,
        Tool.Line => ToolLine, Tool.Pen => ToolPen, Tool.Highlighter => ToolHighlighter, Tool.Text => ToolText,
        Tool.Pixelate => ToolPixelate, Tool.Erase => ToolErase, Tool.Move => ToolMove, _ => ToolCrop,
    };

    private void SelectTool(Tool t) => ToolButton(t).IsChecked = true;

    private void Tool_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag } || !Enum.TryParse(tag, out Tool tool)) return;
        CommitText();
        _tool = _lastTool = tool;
        if (tool != Tool.Select) { _selected = null; ShowSelection(); }
        UpdateHideOptions();
        Doc.Cursor = tool switch
        {
            Tool.Select => Cursors.Arrow,
            Tool.Text => Cursors.IBeam,
            _ => Cursors.Cross,
        };
        HintText.Text = tool switch
        {
            Tool.Select => "Drag over text to select it (Ctrl+C copies, Delete erases it). Drag a mark to move it. Right-click for more.",
            Tool.Arrow or Tool.Line => "Drag to draw. Hold Shift for straight angles.",
            Tool.Rectangle or Tool.Ellipse => "Drag to draw. Hold Shift for a square or circle.",
            Tool.Pen => "Drag to draw freehand.",
            Tool.Highlighter => "Drag over text to highlight it.",
            Tool.Text => "Click where the text should go. Enter to finish, Shift+Enter for a new line.",
            Tool.Pixelate => "Drag over anything private to hide it. Auto-hide finds emails, numbers and keys for you.",
            Tool.Erase => "Paint over text or an object to remove it. Works best on plain backgrounds. The slider sets the brush.",
            Tool.Move => "Click a word or a picture, or drag a box around anything, to lift it off and move it. The gap fills in.",
            _ => "Drag the area to keep.",
        };
    }

    private void SetColour(string hex)
    {
        if (!Theming.TryParseHex(hex, out var c)) return;
        _colour = c;
        _lastColour = hex;
        _syncing = true;
        foreach (RadioButton swatch in Palette.Children) swatch.IsChecked = (string)swatch.Tag == hex;
        WheelSwatch.IsChecked = Palette.Children.OfType<RadioButton>().All(s => (string)s.Tag != hex);
        _syncing = false;
        if (_selected != null && _selected.Kind != AnnotationKind.Pixelate && !_down)
        {
            PushUndo();
            _selected.Color = c;
            Render();
        }
    }

    private void Opacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized) return;
        OpacityText.Text = $"{OpacitySlider.Value:0}%";
        if (_syncing) return;
        _opacity = _lastOpacity = OpacitySlider.Value / 100.0;
        if (_selected != null && _selected.Kind != AnnotationKind.Pixelate && !_down)
        {
            PushUndo();
            _selected.Opacity = _opacity;
            Render();
        }
    }

    private void Size_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing || !IsInitialized) return;
        _size = _lastSize = SizeSlider.Value;
        if (_selected != null && !_down)
        {
            PushUndo();
            _selected.Size = _size;
            if (_selected.Kind == AnnotationKind.Pixelate) RemakeHidden(_selected);
            Render();
            ShowSelection();
        }
    }

    // ---- mouse ---------------------------------------------------------------------------

    private Point Clamp(Point p) =>
        new(Math.Clamp(p.X, 0, _base.PixelWidth), Math.Clamp(p.Y, 0, _base.PixelHeight));

    /// <summary>Shift: 45° steps for lines, squares and circles for boxes.</summary>
    private Point Constrain(Point a, Point b)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return b;
        var v = b - a;
        if (_tool is Tool.Arrow or Tool.Line)
        {
            double angle = Math.Round(Math.Atan2(v.Y, v.X) / (Math.PI / 4)) * (Math.PI / 4);
            return a + new Vector(Math.Cos(angle), Math.Sin(angle)) * v.Length;
        }
        if (_tool is Tool.Rectangle or Tool.Ellipse)
        {
            double side = Math.Max(Math.Abs(v.X), Math.Abs(v.Y));
            return new Point(a.X + Math.Sign(v.X == 0 ? 1 : v.X) * side, a.Y + Math.Sign(v.Y == 0 ? 1 : v.Y) * side);
        }
        return b;
    }

    private Annotation? HitTest(Point p)
    {
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            var b = _items[i].Bounds();
            b.Inflate(4, 4);
            if (b.Contains(p)) return _items[i];
        }
        return null;
    }

    private void Doc_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(Doc);
        if (_textBox != null)
        {
            CommitText();
            if (_tool == Tool.Text) return;
        }
        Keyboard.Focus(this);

        // double-click a mark with any tool: pick it up to edit
        if (e.ClickCount == 2 && _tool != Tool.Select && HitTest(p) is { } picked)
        {
            SelectTool(Tool.Select);
            if (picked.Kind == AnnotationKind.Text) { BeginText(picked.Points[0], picked); return; }
            _selected = picked;
            ShowSelection();
            return;
        }

        if (_tool == Tool.Move)
        {
            MoveToolDown(p);
            return;
        }

        switch (_tool)
        {
            case Tool.Select:
            {
                // grab a resize handle of the selected mark
                int handle = HandleAt(p);
                if (handle >= 0)
                {
                    _handle = handle;
                    _down = true;
                    _moved = false;
                    _last = p;
                    Doc.CaptureMouse();
                    return;
                }
                var hit = HitTest(p);
                if (hit != null && hit.Kind == AnnotationKind.Text && e.ClickCount == 2)
                {
                    BeginText(hit.Points[0], hit);
                    return;
                }
                if (hit == null && TryBeginTextSelect(p, e.ClickCount))
                {
                    _selected = null;
                    ShowSelection();
                    return;
                }
                ClearTextSelection();
                _selected = hit;
                ShowSelection();
                if (hit != null)
                {
                    _down = true;
                    _moved = false;
                    _last = p;
                    Doc.CaptureMouse();
                }
                return;
            }
            case Tool.Text:
                BeginText(p, null);
                return;
        }

        _down = true;
        _start = p;
        Doc.CaptureMouse();
        if (_tool == Tool.Crop)
        {
            _cropBox = new Rectangle
            {
                Stroke = (Brush)FindResource("Accent"), StrokeThickness = 1.5 / Zoom,
                StrokeDashArray = new DoubleCollection { 5, 3 }, Fill = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            };
            Adorn.Children.Add(_cropBox);
            UpdateBox(_cropBox, p, p);
            return;
        }

        if (_tool == Tool.Erase)
        {
            _drawing = new Annotation { Kind = AnnotationKind.Pen, Color = Color.FromArgb(0x70, 0xFF, 0x40, 0x40), Size = EraseRadius * 2 };
            _drawing.Points.Add(p);
            return;
        }

        var kind = _tool switch
        {
            Tool.Arrow => AnnotationKind.Arrow, Tool.Rectangle => AnnotationKind.Rectangle, Tool.Ellipse => AnnotationKind.Ellipse,
            Tool.Line => AnnotationKind.Line, Tool.Pen => AnnotationKind.Pen, Tool.Highlighter => AnnotationKind.Highlighter,
            _ => AnnotationKind.Pixelate,
        };
        _drawing = new Annotation { Kind = kind, Color = _colour, Size = _size, Opacity = _opacity };
        _drawing.Points.Add(p);
        if (kind is not (AnnotationKind.Pen or AnnotationKind.Highlighter)) _drawing.Points.Add(p);
        if (kind == AnnotationKind.Pixelate)
        {
            _drawing.Color = Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF);
            _drawing.Mode = _lastHide;
        }
    }

    private void Doc_MouseMove(object sender, MouseEventArgs e)
    {
        if (_textDragging) { DragTextSelect(e.GetPosition(Doc)); return; }
        if (_tool == Tool.Move && !_down) { MoveToolMove(e.GetPosition(Doc)); return; }
        if (!_down)
        {
            // over a word, the Select tool shows the text cursor
            if (_tool == Tool.Select) Doc.Cursor = WordAt(e.GetPosition(Doc)) >= 0 && HitTest(e.GetPosition(Doc)) == null ? Cursors.IBeam : Cursors.Arrow;
            return;
        }
        var p = e.GetPosition(Doc);

        if (_tool is Tool.Select or Tool.Move)
        {
            if (_selected == null) return;
            if (!_moved)
            {
                if ((p - _last).Length < 2) return;
                PushUndo();
                _moved = true;
            }
            if (_handle >= 0)
            {
                // a lifted piece scales in proportion from the opposite corner unless Shift is held
                if (_selected.Kind == AnnotationKind.Piece && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                    p = KeepShape(_selected, _handle, p);
                MoveHandle(_selected, _handle, p);
                Render();
                ShowSelection();
                return;
            }
            _selected.Translate(p.X - _last.X, p.Y - _last.Y);
            _last = p;
            Render();
            ShowSelection();
            return;
        }

        if (_tool == Tool.Crop && _cropBox != null)
        {
            UpdateBox(_cropBox, Clamp(_start), Clamp(p));
            return;
        }

        if (_drawing == null) return;
        if (_drawing.Kind is AnnotationKind.Pen or AnnotationKind.Highlighter)
        {
            if ((p - _drawing.Points[^1]).Length >= 1.2) _drawing.Points.Add(p);
        }
        else if (_drawing.Kind == AnnotationKind.Pixelate)
        {
            _drawing.Points[1] = Clamp(p);
            _drawing.Points[0] = Clamp(_start);
        }
        else
        {
            _drawing.Points[1] = Constrain(_start, p);
        }

        if (_drawing.Kind == AnnotationKind.Pixelate)
        {
            // while dragging, show a frosted box; the pixels are made on release
            Render();
            var box = new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)) };
            UpdateBox(box, _drawing.Points[0], _drawing.Points[1]);
            Ink.Children.Remove(Ink.Children[^1]);
            Ink.Children.Add(box);
        }
        else
        {
            Render();
        }
    }

    private void Doc_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_textDragging) { EndTextSelect(); return; }
        if (_tool == Tool.Move && !_down) { MoveToolUp(e.GetPosition(Doc)); return; }
        if (!_down) return;
        _down = false;
        Doc.ReleaseMouseCapture();
        var p = e.GetPosition(Doc);

        if (_tool is Tool.Select or Tool.Move)
        {
            // a resized pixelate box needs its blocky picture remade
            if (_handle >= 0 && _selected is { Kind: AnnotationKind.Pixelate } px)
            {
                var r = new Rect(Clamp(px.Points[0]), Clamp(px.Points[1]));
                px.Points[0] = new Point(Math.Floor(r.X), Math.Floor(r.Y));
                px.Points[1] = new Point(Math.Ceiling(r.Right), Math.Ceiling(r.Bottom));
                RemakeHidden(px);
                Render();
                ShowSelection();
            }
            _handle = -1;
            return;
        }

        if (_tool == Tool.Crop)
        {
            if (_cropBox != null) Adorn.Children.Remove(_cropBox);
            _cropBox = null;
            var r = new Rect(Clamp(_start), Clamp(p));
            if (r.Width >= 4 && r.Height >= 4) ApplyCrop(r);
            return;
        }

        var a = _drawing;
        _drawing = null;
        if (a == null) { Render(); return; }

        if (_tool == Tool.Erase)
        {
            Render();
            if (a.Points.Count > 0) _ = EraseAsync(a.Points.ToList());
            return;
        }

        bool keep = a.Kind switch
        {
            AnnotationKind.Pen or AnnotationKind.Highlighter => a.Points.Count >= 2,
            AnnotationKind.Line or AnnotationKind.Arrow => (a.Points[1] - a.Points[0]).Length >= 3,
            _ => Math.Abs(a.Points[1].X - a.Points[0].X) >= 3 && Math.Abs(a.Points[1].Y - a.Points[0].Y) >= 3,
        };
        if (keep && a.Kind == AnnotationKind.Pixelate)
        {
            var r = new Rect(a.Points[0], a.Points[1]);
            var x0 = Math.Floor(r.X); var y0 = Math.Floor(r.Y);
            var x1 = Math.Ceiling(r.Right); var y1 = Math.Ceiling(r.Bottom);
            a.Points[0] = new Point(x0, y0);
            a.Points[1] = new Point(x1, y1);
            RemakeHidden(a);
            keep = a.Pixels != null;
        }
        if (keep)
        {
            PushUndo();
            _items.Add(a);
        }
        Render();
    }

    private static void UpdateBox(FrameworkElement box, Point a, Point b)
    {
        var r = new Rect(a, b);
        Canvas.SetLeft(box, r.X);
        Canvas.SetTop(box, r.Y);
        box.Width = r.Width;
        box.Height = r.Height;
    }

    // ---- handles ---------------------------------------------------------------------------

    private int _handle = -1;

    /// <summary>Where a mark can be stretched from: line ends, or box corners.</summary>
    private static List<Point> Handles(Annotation a)
    {
        if (a.Points.Count < 2) return new();
        var p0 = a.Points[0]; var p1 = a.Points[^1];
        return a.Kind switch
        {
            AnnotationKind.Line or AnnotationKind.Arrow => new() { p0, p1 },
            AnnotationKind.Rectangle or AnnotationKind.Ellipse or AnnotationKind.Pixelate or AnnotationKind.Piece =>
                new() { p0, new Point(p1.X, p0.Y), p1, new Point(p0.X, p1.Y) },
            _ => new(),
        };
    }

    private int HandleAt(Point p)
    {
        if (_selected == null) return -1;
        var hs = Handles(_selected);
        double reach = 8 / Zoom;
        for (int i = 0; i < hs.Count; i++)
            if ((hs[i] - p).Length <= reach) return i;
        return -1;
    }

    /// <summary>Where a dragged corner goes so the box keeps its shape, scaled from the opposite corner.</summary>
    private static Point KeepShape(Annotation a, int handle, Point p)
    {
        var r = new Rect(a.Points[0], a.Points[1]);
        if (r.Width < 1 || r.Height < 1) return p;
        var corners = new[] { r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft };
        var fixedCorner = corners[(handle + 2) % 4];
        var dragged = corners[handle];
        double sx = (p.X - fixedCorner.X) / (dragged.X - fixedCorner.X), sy = (p.Y - fixedCorner.Y) / (dragged.Y - fixedCorner.Y);
        double k = Math.Max(0.02, (Math.Abs(sx) + Math.Abs(sy)) / 2);
        return new Point(fixedCorner.X + (dragged.X - fixedCorner.X) * k, fixedCorner.Y + (dragged.Y - fixedCorner.Y) * k);
    }

    private static void MoveHandle(Annotation a, int handle, Point p)
    {
        if (a.Kind is AnnotationKind.Line or AnnotationKind.Arrow)
        {
            a.Points[handle == 0 ? 0 : a.Points.Count - 1] = p;
            return;
        }
        var p0 = a.Points[0]; var p1 = a.Points[1];
        switch (handle)
        {
            case 0: a.Points[0] = p; break;
            case 1: a.Points[0] = new Point(p0.X, p.Y); a.Points[1] = new Point(p.X, p1.Y); break;
            case 2: a.Points[1] = p; break;
            case 3: a.Points[0] = new Point(p.X, p0.Y); a.Points[1] = new Point(p1.X, p.Y); break;
        }
    }

    // ---- right-click menu ------------------------------------------------------------------

    protected override void OnPreviewMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseRightButtonUp(e);
        if (!Doc.IsMouseOver) return;
        if (ShowTextMenu(e.GetPosition(Doc))) { e.Handled = true; return; }
        var hit = HitTest(e.GetPosition(Doc));
        if (hit == null)
        {
            if (ShowPictureMenu(e.GetPosition(Doc))) e.Handled = true;
            return;
        }
        e.Handled = true;
        CommitText();
        if (_tool != Tool.Select) SelectTool(Tool.Select);
        _selected = hit;
        ShowSelection();

        var menu = new ContextMenu();
        void Item(string header, Action act, bool check = false)
        {
            var mi = new MenuItem { Header = header, IsChecked = check };
            mi.Click += (_, _) => act();
            menu.Items.Add(mi);
        }
        if (hit.Kind == AnnotationKind.Text) Item("Edit text", () => BeginText(hit.Points[0], hit));
        if (hit.Kind is AnnotationKind.Rectangle or AnnotationKind.Ellipse)
            Item("Filled", () => { PushUndo(); hit.Filled = !hit.Filled; Render(); }, hit.Filled);
        if (hit.Kind is not (AnnotationKind.Pixelate or AnnotationKind.Piece))
            Item("Use the current colour, size and opacity", () => { PushUndo(); hit.Color = _colour; hit.Size = _size; hit.Opacity = _opacity; Render(); ShowSelection(); });
        Item("Duplicate", () =>
        {
            PushUndo();
            var copy = hit.Clone();
            copy.Translate(14, 14);
            _items.Add(copy);
            _selected = copy;
            Render();
            ShowSelection();
        });
        menu.Items.Add(new Separator());
        Item("Bring to front", () => { PushUndo(); _items.Remove(hit); _items.Add(hit); Render(); });
        Item("Send to back", () => { PushUndo(); _items.Remove(hit); _items.Insert(0, hit); Render(); });
        menu.Items.Add(new Separator());
        Item("Delete", DeleteSelected);
        menu.IsOpen = true;
    }

    // ---- colour wheel ----------------------------------------------------------------------

    private bool _wheelSync;

    private void WheelSwatch_Click(object sender, RoutedEventArgs e)
    {
        var (h, s, v) = Theming.ToHsv(_colour);
        _wheelSync = true;
        Wheel.SetHueSaturation(h, s);
        WheelBrightness.Value = Math.Max(10, v * 100);
        _wheelSync = false;
        WheelPopup.IsOpen = true;
    }

    private void WheelChanged()
    {
        if (_wheelSync) return;
        SetColour(Theming.ToHex(Theming.FromHsv(Wheel.Hue, Wheel.Saturation, WheelBrightness.Value / 100)));
    }

    private void WheelBrightness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IsInitialized) WheelChanged();
    }

    private void ShowSelection()
    {
        if (IsInitialized) UpdateHideOptions();
        foreach (var old in Adorn.Children.OfType<FrameworkElement>().Where(r => r.Tag as string is "selection" or "handle").ToList())
            Adorn.Children.Remove(old);
        if (_selected == null) return;
        var b = _selected.Bounds();
        double z = Zoom;
        b.Inflate(4 / z, 4 / z);
        var rect = new Rectangle
        {
            Tag = "selection",
            Stroke = (Brush)FindResource("Accent"),
            StrokeThickness = 1.2 / z,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            IsHitTestVisible = false,
        };
        UpdateBox(rect, b.TopLeft, b.BottomRight);
        Adorn.Children.Add(rect);

        double size = 9 / z;
        foreach (var h in Handles(_selected))
        {
            var dot = new Rectangle
            {
                Tag = "handle", Width = size, Height = size, Fill = Brushes.White,
                Stroke = (Brush)FindResource("Accent"), StrokeThickness = 1.5 / z, IsHitTestVisible = false,
            };
            Canvas.SetLeft(dot, h.X - size / 2);
            Canvas.SetTop(dot, h.Y - size / 2);
            Adorn.Children.Add(dot);
        }
    }

    private void ApplyCrop(Rect r)
    {
        PushUndo();
        var rect = new Int32Rect((int)Math.Floor(r.X), (int)Math.Floor(r.Y), 0, 0);
        rect.Width = Math.Min(_base.PixelWidth - rect.X, (int)Math.Ceiling(r.Right) - rect.X);
        rect.Height = Math.Min(_base.PixelHeight - rect.Y, (int)Math.Ceiling(r.Bottom) - rect.Y);
        _base = ImageLoader.Normalize(new CroppedBitmap(_base, rect));
        foreach (var a in _items) a.Translate(-rect.X, -rect.Y);
        _selected = null;
        ApplyBase();
        Render();
        ShowSelection();
    }

    // ---- text ----------------------------------------------------------------------------

    private void BeginText(Point at, Annotation? existing)
    {
        if (existing != null)
        {
            PushUndo();
            _items.Remove(existing);
            Render();
        }
        _textOriginal = existing;
        var colour = existing?.Color ?? _colour;
        double size = existing?.Size ?? _size;
        string style = existing?.TextStyle ?? _textStyle;
        var probe = new Annotation { Size = size, TextStyle = style };

        _textBox = new TextBox
        {
            Text = existing?.Text ?? "",
            FontSize = probe.FontSize,
            FontWeight = probe.TextWeight,
            FontStyle = probe.TextSlant,
            FontFamily = (FontFamily)FindResource("UiFont"),
            Foreground = new SolidColorBrush(colour),
            CaretBrush = new SolidColorBrush(colour),
            Background = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)),
            BorderBrush = (Brush)FindResource("Accent"),
            BorderThickness = new Thickness(1 / Zoom),
            Padding = new Thickness(0),
            MinWidth = 40,
            AcceptsReturn = true,
            Tag = (colour, size, style),
        };
        Canvas.SetLeft(_textBox, at.X - 1);
        Canvas.SetTop(_textBox, at.Y - 1);
        Adorn.Children.Add(_textBox);
        _textBox.Focus();
        _textBox.CaretIndex = _textBox.Text.Length;
        _start = at;
        UpdateHideOptions();
    }

    private void CommitText()
    {
        if (_textBox == null) return;
        var box = _textBox;
        _textBox = null;
        Adorn.Children.Remove(box);
        var (colour, size, style) = ((Color, double, string))box.Tag;
        string text = box.Text.TrimEnd();
        if (text.Length > 0)
        {
            if (_textOriginal == null) PushUndo();
            _items.Add(new Annotation { Kind = AnnotationKind.Text, Points = { _start }, Color = colour, Size = size, Text = text, TextStyle = style, Opacity = _textOriginal?.Opacity ?? _opacity });
        }
        _textOriginal = null;
        Render();
    }

    private void CancelText()
    {
        if (_textBox == null) return;
        Adorn.Children.Remove(_textBox);
        _textBox = null;
        if (_textOriginal != null) _items.Add(_textOriginal);
        _textOriginal = null;
        Render();
    }

    // ---- keyboard ------------------------------------------------------------------------

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        if (_textBox != null && _textBox.IsKeyboardFocused)
        {
            if (e.Key == Key.Enter && !mods.HasFlag(ModifierKeys.Shift)) { e.Handled = true; CommitText(); }
            else if (e.Key == Key.Escape) { e.Handled = true; CancelText(); }
            return;
        }

        bool ctrl = mods.HasFlag(ModifierKeys.Control), shift = mods.HasFlag(ModifierKeys.Shift);
        Action? action = e.Key switch
        {
            Key.Z when ctrl && shift => Redo,
            Key.Z when ctrl => Undo,
            Key.Y when ctrl => Redo,
            Key.C when ctrl && HasTextSelection => CopySelectedText,
            Key.C when ctrl && shift => () => CopyAllText_Click(this, new RoutedEventArgs()),
            Key.C when ctrl => () => Copy_Click(this, new RoutedEventArgs()),
            Key.A when ctrl && _tool == Tool.Select => SelectAllText,
            Key.Delete or Key.Back when (HasTextSelection || HasTextCaret) && _selected == null => () => _ = DeleteTextAsync(e.Key == Key.Back),
            Key.Left or Key.Right when (HasTextSelection || HasTextCaret) && _selected == null && !ctrl => () => MoveCaret(e.Key == Key.Right ? 1 : -1, shift),
            Key.S when ctrl && shift => () => SaveAs_Click(this, new RoutedEventArgs()),
            Key.S when ctrl => () => Save_Click(this, new RoutedEventArgs()),
            Key.Delete or Key.Back when _selected != null => DeleteSelected,
            Key.Escape => () => { _selected = null; ShowSelection(); ClearTextSelection(); },
            Key.D0 or Key.NumPad0 when ctrl => () => SetZoom(FitZoom, true),
            Key.D1 or Key.NumPad1 when ctrl => () => SetZoom(1, false),
            Key.OemPlus or Key.Add when ctrl => () => SetZoom(_zoom * 1.25, false),
            Key.OemMinus or Key.Subtract when ctrl => () => SetZoom(_zoom / 1.25, false),
            _ when ctrl || mods.HasFlag(ModifierKeys.Alt) => null,
            Key.Space when (HasTextCaret || HasTextSelection) && _selected == null && !ctrl => () => TypeKey(' '),
            // with a text cursor, letters type rather than switch tools (Esc first to use the shortcuts)
            _ when (HasTextCaret || HasTextSelection) && _selected == null => null,
            Key.V => () => SelectTool(Tool.Select),
            Key.A => () => SelectTool(Tool.Arrow),
            Key.R => () => SelectTool(Tool.Rectangle),
            Key.E => () => SelectTool(Tool.Ellipse),
            Key.L => () => SelectTool(Tool.Line),
            Key.P => () => SelectTool(Tool.Pen),
            Key.H => () => SelectTool(Tool.Highlighter),
            Key.T => () => SelectTool(Tool.Text),
            Key.B => () => SelectTool(Tool.Pixelate),
            Key.X => () => SelectTool(Tool.Erase),
            Key.M => () => SelectTool(Tool.Move),
            Key.C => () => SelectTool(Tool.Crop),
            _ => null,
        };
        if (action == null) return;
        e.Handled = true;
        action();
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();

    private void DeleteSelected()
    {
        if (_selected == null) return;
        PushUndo();
        _items.Remove(_selected);
        _selected = null;
        Render();
        ShowSelection();
    }

    // ---- rotate, flip, resize -------------------------------------------------------------------

    private void ImageMenu_Click(object sender, RoutedEventArgs e)
    {
        CommitText();
        var menu = new ContextMenu { PlacementTarget = ImageMenuButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        void Item(string header, string icon, Action act)
        {
            var mi = new MenuItem { Header = header, Icon = new Icon { Data = (Geometry)FindResource(icon), Width = 14, Height = 14 } };
            mi.Click += (_, _) => act();
            menu.Items.Add(mi);
        }
        Item("Rotate right", "IcRotateCw", () => Transform(new RotateTransform(90), true));
        Item("Rotate left", "IcRotateCcw", () => Transform(new RotateTransform(-90), true));
        Item("Flip left to right", "IcMoveHorizontal", () => Transform(new ScaleTransform(-1, 1), false));
        Item("Flip upside down", "IcMoveVertical", () => Transform(new ScaleTransform(1, -1), false));
        menu.Items.Add(new Separator());
        foreach (int percent in new[] { 50, 75, 150, 200 })
        {
            int p = percent;
            Item($"Resize to {p}%", "IcMaximize2", () => Resize(p / 100.0));
        }
        menu.IsOpen = true;
    }

    /// <summary>Rotates or flips the picture, marks included (they're burned in first, so nothing lands in the wrong place).</summary>
    private void Transform(Transform transform, bool turns)
    {
        var flat = Export();
        var result = ImageLoader.Normalize(new TransformedBitmap(flat, transform));
        Replace(result);
    }

    private void Resize(double scale)
    {
        var flat = Export();
        int w = Math.Max(1, (int)Math.Round(flat.PixelWidth * scale)), h = Math.Max(1, (int)Math.Round(flat.PixelHeight * scale));
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen()) dc.DrawImage(flat, new Rect(0, 0, w, h));
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        Replace(ImageLoader.Normalize(rtb));
    }

    private void Replace(BitmapSource image)
    {
        PushUndo();
        _base = image;
        _items = new List<Annotation>();
        _selected = null;
        ApplyBase();
        Render();
        ShowSelection();
        SetZoom(FitZoom, true);
    }

    // ---- hiding and erasing -------------------------------------------------------------------

    private double EraseRadius => 3 + _size * 2.5;

    private void RemakeHidden(Annotation a)
    {
        var r = new Rect(a.Points[0], a.Points[^1]);
        a.Pixels = Redaction.Render(_base, r, a.Mode, a.Size) ?? a.Pixels;
    }

    /// <summary>Normal, bold or italic, for new text and the selected text.</summary>
    private string _textStyle = "Bold";

    private void TextStyle_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncing || sender is not RadioButton { Tag: string style }) return;
        _textStyle = style;
        if (_textBox != null)
        {
            // the text being typed changes at once
            var (colour, size, _) = ((Color, double, string))_textBox.Tag;
            var probe = new Annotation { TextStyle = style };
            _textBox.FontWeight = probe.TextWeight;
            _textBox.FontStyle = probe.TextSlant;
            _textBox.Tag = (colour, size, style);
        }
        else if (_selected is { Kind: AnnotationKind.Text } t && t.TextStyle != style)
        {
            PushUndo();
            t.TextStyle = style;
            Render();
            ShowSelection();
        }
    }

    private void UpdateHideOptions()
    {
        bool text = _tool == Tool.Text || _selected?.Kind == AnnotationKind.Text || _textBox != null;
        TextStyleOptions.Visibility = text ? Visibility.Visible : Visibility.Collapsed;
        string current = _selected is { Kind: AnnotationKind.Text } sel ? sel.TextStyle : _textStyle;
        _syncing = true;
        TextNormal.IsChecked = current == "Normal";
        TextBold.IsChecked = current == "Bold";
        TextItalic.IsChecked = current == "Italic";
        _syncing = false;
        bool show = _tool == Tool.Pixelate || _selected?.Kind == AnnotationKind.Pixelate;
        HideOptions.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        var mode = _selected is { Kind: AnnotationKind.Pixelate } s ? s.Mode : _lastHide;
        _syncing = true;
        HideBlur.IsChecked = mode == HideMode.Blur;
        HidePixelate.IsChecked = mode == HideMode.Pixelate;
        HideMosaic.IsChecked = mode == HideMode.Mosaic;
        _syncing = false;
    }

    private void HideMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncing || sender is not RadioButton { Tag: string tag } || !Enum.TryParse(tag, out HideMode mode)) return;
        _lastHide = mode;
        if (_selected is { Kind: AnnotationKind.Pixelate } s)
        {
            PushUndo();
            s.Mode = mode;
            RemakeHidden(s);
            Render();
        }
    }

    /// <summary>Reads the text in the picture and hides every private detail it finds.</summary>
    private async void AutoHide_Click(object sender, RoutedEventArgs e)
    {
        AutoHideButton.IsEnabled = false;
        HintText.Text = "Looking for private details…";
        try
        {
            var found = await Redaction.FindPrivateAsync(_base);
            if (found == null)
            {
                Toast.Show("Windows has no text-reading language installed, so Auto-hide can't read the picture.", true);
                return;
            }
            if (found.Count == 0)
            {
                Toast.Show("No emails, phone numbers, card numbers, IP addresses or keys found.");
                return;
            }
            PushUndo();
            foreach (var (area, _) in found)
            {
                var r = Rect.Intersect(area, new Rect(0, 0, _base.PixelWidth, _base.PixelHeight));
                if (r.IsEmpty) continue;
                var mark = new Annotation
                {
                    Kind = AnnotationKind.Pixelate, Mode = _lastHide, Size = _size, Color = Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF),
                    Points = { new Point(Math.Floor(r.X), Math.Floor(r.Y)), new Point(Math.Ceiling(r.Right), Math.Ceiling(r.Bottom)) },
                };
                RemakeHidden(mark);
                if (mark.Pixels != null) _items.Add(mark);
            }
            Render();
            Toast.Show("Hid " + Redaction.Describe(found.Select(f => f.Kind)) + ".");
        }
        catch (Exception ex)
        {
            Toast.Show("Auto-hide failed: " + ex.Message, true);
        }
        finally
        {
            AutoHideButton.IsEnabled = true;
            HintText.Text = "";
        }
    }

    /// <summary>Fills the brushed area from its surroundings.</summary>
    private async Task EraseAsync(List<Point> stroke)
    {
        var image = _base;
        double radius = EraseRadius;
        HintText.Text = "Erasing…";
        IsHitTestVisible = false;
        try
        {
            var filled = await Task.Run(() =>
            {
                var mask = Inpaint.StrokeMask(stroke, radius, image.PixelWidth, image.PixelHeight);
                return Inpaint.Fill(image, mask);
            });
            PushUndo();
            _base = filled;
            // hidden areas show what's under them, which just changed
            foreach (var a in _items.Where(a => a.Kind == AnnotationKind.Pixelate)) RemakeHidden(a);
            ApplyBase();
            Render();
        }
        finally
        {
            IsHitTestVisible = true;
            HintText.Text = "";
        }
    }

    internal BitmapSource EraseForTest(List<Point> stroke, double radius) =>
        Inpaint.Fill(_base, Inpaint.StrokeMask(stroke, radius, _base.PixelWidth, _base.PixelHeight));

    // ---- output --------------------------------------------------------------------------

    /// <summary>The picture with every mark burned in, at its own pixel size.</summary>
    public BitmapSource Export()
    {
        CommitText();
        int w = _base.PixelWidth, h = _base.PixelHeight;
        Doc.UpdateLayout();
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var brush = new VisualBrush(Doc)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, w, h),
                Stretch = Stretch.Fill,
            };
            dc.DrawRectangle(brush, null, new Rect(0, 0, w, h));
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        return ImageLoader.Normalize(rtb);
    }

    /// <summary>Adds marks directly; used by the self-test.</summary>
    internal void AddForTest(Annotation a)
    {
        _items.Add(a);
        Render();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetImage(Export());
            Toast.Show("Copied to the clipboard.");
        }
        catch
        {
            Toast.Show("The clipboard is busy. Try again.", true);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var image = Export();
        if (FilePath != null)
        {
            if (!ImageFiles.TrySave(image, FilePath, out string? error))
            {
                Toast.Show("Couldn't save: " + error, true);
                return;
            }
        }
        else
        {
            FilePath = _host.SaveNew(image);
            if (FilePath == null) return;
        }
        _dirty = false;
        TitleText.Text = System.IO.Path.GetFileName(FilePath);
        _host.Saved?.Invoke(FilePath);
        Toast.Show("Saved " + System.IO.Path.GetFileName(FilePath));
    }

    private void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save image",
            Filter = "PNG image|*.png|JPEG image|*.jpg",
            FileName = FilePath != null ? System.IO.Path.GetFileNameWithoutExtension(FilePath) : "Screenshot",
            InitialDirectory = FilePath != null ? System.IO.Path.GetDirectoryName(FilePath) : _host.Folder,
        };
        if (dialog.ShowDialog(this) != true) return;
        if (!ImageFiles.TrySave(Export(), dialog.FileName, out string? error))
        {
            Toast.Show("Couldn't save: " + error, true);
            return;
        }
        FilePath = dialog.FileName;
        _dirty = false;
        TitleText.Text = System.IO.Path.GetFileName(FilePath);
        _host.Saved?.Invoke(FilePath);
        Toast.Show("Saved " + System.IO.Path.GetFileName(FilePath));
    }

    private void Pin_Click(object sender, RoutedEventArgs e) =>
        _host.Pin(Export(), FilePath != null ? System.IO.Path.GetFileName(FilePath) : TitleText.Text);

    private void Beautify_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new BeautifyWindow(Export()) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } result) return;
        // the marks are part of the beautified picture now; Undo brings them back as marks
        PushUndo();
        _base = ImageLoader.Normalize(result);
        _items = new List<Annotation>();
        _selected = null;
        ApplyBase();
        Render();
        ShowSelection();
    }

    private void Effects_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new EffectsWindow(Export()) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } result) return;
        // like Beautify: the marks become part of the picture; Undo brings them back
        PushUndo();
        _base = ImageLoader.Normalize(result);
        _items = new List<Annotation>();
        _selected = null;
        ApplyBase();
        Render();
        ShowSelection();
    }

    // ---- window --------------------------------------------------------------------------

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        // a maximised chrome-less window hangs over the screen edge by its resize border
        Frame.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        // the same rounded shape as the main window, square when it fills the screen
        if (WindowState == WindowState.Maximized) Frame.CornerRadius = new CornerRadius(0);
        else Frame.SetResourceReference(System.Windows.Controls.Border.CornerRadiusProperty, "RadiusWindow");
        MaxIcon.Data = (Geometry)FindResource(WindowState == WindowState.Maximized ? "IcCopy" : "IcSquare");
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        CommitText();
        if (_dirty && _items.Count > 0 || _dirty && _undo.Count > 0)
        {
            int answer = AskDialog.Ask(this, "Save your changes?", "The marks and edits on this picture haven't been saved yet.", "IcSave",
                ("Don't save", false), ("Cancel", false), ("Save", true));
            if (answer is 1 or -1) { e.Cancel = true; return; }
            if (answer == 2)
            {
                Save_Click(this, new RoutedEventArgs());
                if (_dirty) { e.Cancel = true; return; }
            }
        }
        base.OnClosing(e);
    }
}
