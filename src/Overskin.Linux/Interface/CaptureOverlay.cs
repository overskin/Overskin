using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Overskin.Linux.Platform;

namespace Overskin.Linux.Interface;

/// <summary>What was picked on the frozen screen: the area, and anything drawn on it first.</summary>
public sealed record OverlayPick(PixelRect Area, List<Mark> Marks);

/// <summary>
/// Shows the frozen screenshot full screen. Drag out an area, click a window, or take the whole
/// screen; the toolbar can also draw on the screen before it's captured. Esc or right-click cancels.
/// </summary>
public sealed class CaptureOverlay : Window
{
    private readonly TaskCompletionSource<OverlayPick?> _result = new();
    private readonly SelectionSurface _surface;
    private readonly Dictionary<OverlayTool, ToggleButton> _tools = new();

    public CaptureOverlay(Bitmap shot, bool recording = false, string? hint = null)
    {
        Title = "Overskin capture";
        SystemDecorations = SystemDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        Background = Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _surface = new SelectionSurface(shot) { Recording = recording, Hint = hint };
        _surface.Picked += r => Finish(r);

        // laid out as the Windows capture bar: the modes as labelled chips, the size, then the drawing
        // tools, then the switches, each tool in an outlined square
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        void Mode(OverlayTool tool, string icon, string text, string tip)
        {
            var b = new ToggleButton
            {
                Classes = { "seg" }, Height = 28,
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Children = { Ui.Icon(icon, 15), new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 } } },
            };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => Pick(tool);
            _tools[tool] = b;
            bar.Children.Add(b);
        }
        void Tool(OverlayTool tool, string icon, string tip)
        {
            var b = new ToggleButton { Content = Ui.Icon(icon, 16), Classes = { "bartool" } };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => Pick(tool);
            _tools[tool] = b;
            bar.Children.Add(b);
        }
        void Separator() => bar.Children.Add(new Border { Width = 1, Height = 22, Margin = new Thickness(6, 0), Background = new SolidColorBrush(Colours.LineStrong), VerticalAlignment = VerticalAlignment.Center });

        Mode(OverlayTool.Area, Icons.Area, "Area", "Area: drag out a part, or click a window (A)");
        Mode(OverlayTool.Window, Icons.Window, "Window", "Window: click one (W)");
        var screenButton = new Button
        {
            Classes = { "barchip" },
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Children = { Ui.Icon(Icons.Screen, 15), new TextBlock { Text = "Screen", VerticalAlignment = VerticalAlignment.Center, FontSize = 12 } } },
        };
        ToolTip.SetTip(screenButton, "The whole screen (Enter)");
        screenButton.Click += (_, _) => Finish(new PixelRect(shot.PixelSize));
        bar.Children.Add(screenButton);
        if (!recording)
        {
            bar.Children.Add(ShapeButton());
            Separator();
            Tool(OverlayTool.Pen, Icons.Pen, "Draw on the screen first (P)");
            Tool(OverlayTool.Arrow, Icons.Arrow, "Arrow (R)");
            Tool(OverlayTool.Rectangle, Icons.Rectangle, "Box (B)");
            Tool(OverlayTool.Highlighter, Icons.Highlighter, "Highlighter (H)");
            bar.Children.Add(ColourButton());
            var undo = Ui.IconButton(Icons.Undo, OperatingSystem.IsMacOS() ? "Undo the last drawing (⌘Z)" : "Undo the last drawing (Ctrl+Z)", _surface.UndoMark);
            undo.Width = 30; undo.Height = 28;
            bar.Children.Add(undo);
            Separator();
            bar.Children.Add(Toggle(Icons.Select, "Include the mouse pointer", () => App.Settings.IncludePointer, v => App.Settings.IncludePointer = v));
        }
        bar.Children.Add(Toggle(Icons.ZoomIn, "Magnifier (M)", () => App.Settings.Magnifier, v => App.Settings.Magnifier = v));
        bar.Children.Add(_tightenButton = Toggle(Icons.Crop, "Fit the box to what's inside it: drag roughly, the plain edges are trimmed (T)", () => App.Settings.Tighten, v => App.Settings.Tighten = v));
        Separator();
        var close = Ui.IconButton(Icons.Close, "Cancel (Esc)", () => Finish(null));
        close.Width = 30; close.Height = 28;
        bar.Children.Add(close);

        var toolbar = new Border
        {
            Background = new SolidColorBrush(Colours.Bg, 0.97),
            BorderBrush = new SolidColorBrush(Colours.LineStrong),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(13, 2, 13, 2),
            Padding = new Thickness(7, 7),
            BoxShadow = BoxShadows.Parse("0 6 24 0 #73000000"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 14, 0, 0),
            Child = bar,
            Cursor = new Cursor(StandardCursorType.Arrow),
        };
        Content = new Grid { Children = { _surface, toolbar } };
        Pick(OverlayTool.Area);

        CoverTheDesktop();
        KeyDown += (_, e) =>
        {
            bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
            switch (e.Key)
            {
                case Key.Escape: Finish(null); break;
                case Key.Enter or Key.Space: Finish(new PixelRect(shot.PixelSize)); break;
                case Key.Z when ctrl: _surface.UndoMark(); break;
                case Key.A: Pick(OverlayTool.Area); break;
                case Key.W: Pick(OverlayTool.Window); break;
                case Key.P when !recording: Pick(OverlayTool.Pen); break;
                case Key.R when !recording: Pick(OverlayTool.Arrow); break;
                case Key.B when !recording: Pick(OverlayTool.Rectangle); break;
                case Key.H when !recording: Pick(OverlayTool.Highlighter); break;
                case Key.T:
                    App.Settings.Tighten = !App.Settings.Tighten;
                    App.Settings.Save();
                    if (_tightenButton != null) _tightenButton.IsChecked = App.Settings.Tighten;
                    _surface.InvalidateVisual();
                    break;
                case Key.M:
                    App.Settings.Magnifier = !App.Settings.Magnifier;
                    App.Settings.Save();
                    _surface.InvalidateVisual();
                    break;
            }
        };
        Closed += (_, _) => _result.TrySetResult(null);
        Opened += async (_, _) => _surface.Windows = await Input.WindowsAsync();
        _ = NotePointerAsync();
    }

    private ToggleButton? _tightenButton;

    private ToggleButton Toggle(string icon, string tip, Func<bool> get, Action<bool> set)
    {
        var b = new ToggleButton { Content = Ui.Icon(icon, 16), Classes = { "bartool" }, IsChecked = get() };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) =>
        {
            set(b.IsChecked == true);
            App.Settings.Save();
            _surface.InvalidateVisual();
        };
        return b;
    }

    /// <summary>The colour for drawing on the screen: a few to pick from, or any from the wheel (as on Windows).</summary>
    private Control ColourButton()
    {
        var swatch = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(8), BorderBrush = Brushes.White, BorderThickness = new Thickness(1.5) };
        void Show() => swatch.Background = new SolidColorBrush(Color.TryParse(App.Settings.PenColour, out var c) ? c : Colors.Red);
        Show();
        var button = new Button { Content = swatch, Classes = { "tool" } };
        ToolTip.SetTip(button, "Colour for drawing");
        var swatches = new WrapPanel { ItemSpacing = 6, LineSpacing = 6, Width = 170 };
        var flyout = new Flyout();
        void Set(string hex) { App.Settings.PenColour = hex; App.Settings.Save(); Show(); }
        foreach (var hex in new[] { "#EF4444", "#F97316", "#FACC15", "#22C55E", "#3B82F6", "#A855F7", "#EC4899", "#FFFFFF", "#111111" })
        {
            var s = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = new SolidColorBrush(Color.Parse(hex)), Cursor = new Cursor(StandardCursorType.Hand) };
            s.PointerPressed += (_, _) => { Set(hex); flyout.Hide(); };
            swatches.Children.Add(s);
        }
        var wheel = new ColourWheel { Width = 160, Height = 160 };
        wheel.ColourChanged += () => Set(ColourWheel.ToHex(ColourWheel.FromHsv(wheel.Hue, wheel.Saturation, 1)));
        flyout.Content = new StackPanel { Spacing = 10, Margin = new Thickness(4), Children = { swatches, wheel } };
        button.Click += (_, _) => flyout.ShowAt(button);
        return button;
    }

    private static readonly (string Label, double Ratio, PixelSize? Size)[] Shapes =
    {
        ("Any size", 0, null), ("16:9", 16 / 9.0, null), ("4:3", 4 / 3.0, null), ("1:1", 1, null), ("9:16", 9 / 16.0, null),
        ("1920 × 1080", 0, new PixelSize(1920, 1080)), ("1280 × 720", 0, new PixelSize(1280, 720)),
        ("1080 × 1080", 0, new PixelSize(1080, 1080)), ("800 × 600", 0, new PixelSize(800, 600)), ("512 × 512", 0, new PixelSize(512, 512)),
    };

    /// <summary>Keeps the box to a shape (16:9, 1:1…) or an exact size, as on Windows.</summary>
    private Control ShapeButton()
    {
        var label = new TextBlock { Text = "Any size", VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        var button = new Button
        {
            Classes = { "barchip" },
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Children = { Ui.Icon(Lucide.Ruler, 15), label } },
        };
        ToolTip.SetTip(button, "Keep the area to a shape (16:9, 1:1...) or an exact size");
        var menu = new ContextMenu();
        foreach (var (text, ratio, size) in Shapes)
        {
            if (text == "1920 × 1080") menu.Items.Add(new Separator());
            var item = new MenuItem { Header = text };
            item.Click += (_, _) =>
            {
                _surface.Ratio = ratio;
                _surface.FixedSize = size;
                label.Text = text;
                Pick(OverlayTool.Area);
            };
            menu.Items.Add(item);
        }
        button.Click += (_, _) => menu.Open(button);
        return button;
    }

    private void Pick(OverlayTool tool)
    {
        _surface.Tool = tool;
        foreach (var (t, b) in _tools) b.IsChecked = t == tool;
        _surface.Cursor = new Cursor(tool is OverlayTool.Area or OverlayTool.Window ? StandardCursorType.Cross : StandardCursorType.Hand);
        _surface.InvalidateVisual();
    }

    private void CoverTheDesktop() => Cover(this);

    /// <summary>Where the pointer is now, in the screenshot's pixels (taken just before this opened).</summary>
    private async Task NotePointerAsync()
    {
        try
        {
            if (Mac.IsMac) { _surface.PointerAtStart = Mac.ToPicture(Mac.PointerPoints()); return; }
            if ((await Input.PointerAsync())?.At is { } p) _surface.PointerAtStart = p;
        }
        catch { }
    }

    /// <summary>
    /// Puts a window exactly over what the last screenshot shows: the captured display on a Mac and
    /// under Wayland, the whole desktop on X11.
    /// </summary>
    internal static void Cover(Window w)
    {
        if (Mac.IsMac)
        {
            // macOS's own full screen slides into a new Space; instead the window is put exactly over
            // the display that was captured, above the menu bar and the Dock
            var d = Mac.Shot.Display;
            w.Width = d.Width;
            w.Height = d.Height;
            w.Opened += (_, _) => Mac.CoverDisplay(Mac.NSWindowOf(w.TryGetPlatformHandle()), d);
            return;
        }
        var screens = w.Screens.All;
        if (screens.Count <= 1 || ScreenGrabber.IsWayland)
        {
            // one monitor (on Wayland the picture is of the focused monitor, where this opens)
            w.WindowState = WindowState.FullScreen;
            return;
        }
        // several monitors on X11: one window over all of them, matching the picture of the whole desktop
        var all = screens.Select(s => s.Bounds).Aggregate((a, b) => a.Union(b));
        var scaling = screens.FirstOrDefault(s => s.IsPrimary)?.Scaling ?? 1;
        w.Position = all.Position;
        w.Width = all.Width / scaling;
        w.Height = all.Height / scaling;
    }

    private void Finish(PixelRect? rect)
    {
        var marks = _surface.Marks.ToList();
        // the pointer, where it was when the screen was frozen, drawn into the picture
        if (rect != null && App.Settings.IncludePointer && _surface.PointerAtStart is { } at)
            marks.Add(new PointerMark { At = at, Scale = Mac.IsMac ? Math.Max(1, Mac.Shot.Scale) : _surface.Screens()?.Primary?.Scaling ?? 1 });
        _result.TrySetResult(rect is { } r ? new OverlayPick(r, marks) : null);
        Close();
    }

    public static async Task<OverlayPick?> PickAsync(Bitmap shot, bool recording = false, string? hint = null)
    {
        var overlay = new CaptureOverlay(shot, recording, hint);
        overlay.Show();
        overlay.Activate();
        overlay.Focus();
        return await overlay._result.Task;
    }
}

public enum OverlayTool { Area, Window, Pen, Arrow, Rectangle, Highlighter }

/// <summary>Draws the screenshot, the dimmed area outside the selection, window outlines, marks, a size label and a magnifier.</summary>
public sealed class SelectionSurface : Control
{
    private readonly Bitmap _shot;
    private Point? _start;
    private Point _pointer;
    private bool _pointerSeen;
    private Mark? _drawing;

    public event Action<PixelRect>? Picked;
    public bool Recording { get; init; }
    public string? Hint { get; init; }
    public OverlayTool Tool { get; set; } = OverlayTool.Area;
    /// <summary>Width over height the box keeps (0: any).</summary>
    public double Ratio { get; set; }
    /// <summary>An exact size the box has (follows the pointer; a click takes it), or null.</summary>
    public PixelSize? FixedSize { get; set; }
    /// <summary>Where the pointer was when the screen was frozen, in picture pixels.</summary>
    public Point? PointerAtStart { get; set; }

    public Avalonia.Controls.Screens? Screens() => (VisualRoot as Window)?.Screens;

    /// <summary>The fixed-size box round the pointer, kept on the screen (picture pixels).</summary>
    private PixelRect FixedAt(Point pointer, PixelSize size)
    {
        var p = ToPicture(pointer);
        int w = Math.Min(size.Width, _shot.PixelSize.Width), h = Math.Min(size.Height, _shot.PixelSize.Height);
        int x = (int)Math.Clamp(p.X - w / 2.0, 0, _shot.PixelSize.Width - w), y = (int)Math.Clamp(p.Y - h / 2.0, 0, _shot.PixelSize.Height - h);
        return new PixelRect(x, y, w, h);
    }

    internal PixelRect FixedForTest(Point p, PixelSize size) => FixedAt(p, size);
    internal Point ShapedForTest(Point a, Point b) => Shaped(a, b);

    /// <summary>The end of a drag, kept to the chosen shape.</summary>
    private Point Shaped(Point a, Point b)
    {
        if (Ratio <= 0) return b;
        double dx = b.X - a.X, dy = b.Y - a.Y, w = Math.Abs(dx), h = Math.Abs(dy);
        if (w / Math.Max(1, h) > Ratio) h = w / Ratio; else w = h * Ratio;
        return new Point(a.X + w * (dx < 0 ? -1 : 1), a.Y + h * (dy < 0 ? -1 : 1));
    }
    /// <summary>Windows on screen (top first) in picture pixels, for clicking one.</summary>
    public List<(string Title, PixelRect Bounds)> Windows { get; set; } = new();
    /// <summary>Drawn on the screen before capturing, in picture pixels.</summary>
    public List<Mark> Marks { get; } = new();

    private static readonly IBrush Shade = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0));
    private static IPen Edge => new Pen(new SolidColorBrush(Colours.Accent), 1.5);
    private static readonly IPen Guide = new Pen(new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)), 1, new DashStyle(new double[] { 4, 4 }, 0));
    private static readonly IBrush LabelBack = new SolidColorBrush(Color.FromArgb(220, 18, 19, 22));
    private static readonly Typeface Font = new("Inter");

    public SelectionSurface(Bitmap shot)
    {
        _shot = shot;
        Focusable = true;
        ClipToBounds = true;
        FindPictures();
    }

    // ---- pictures inside the screen (as on Windows) ----
    //
    // A photo someone sent, an image on a web page, a video thumbnail: found in the background
    // once the screen is frozen, then hovering over one lights just the picture and a click takes
    // it. Holding Ctrl (or Option on a Mac) takes the window instead.

    private List<FoundImage>? _pictures;
    private readonly CancellationTokenSource _pictureSearch = new();
    private KeyModifiers _keys;

    private void FindPictures()
    {
        var shot = _shot;
        var token = _pictureSearch.Token;
        DetachedFromVisualTree += (_, _) => _pictureSearch.Cancel();
        Task.Run(() =>
        {
            var px = Core.Imaging.Pixels(shot, out _);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var found = ImageFinder.Find(px, shot.PixelSize.Width, shot.PixelSize.Height, token);
            Core.Log.Write($"capture screen: {found.Count} pictures found in {sw.ElapsedMilliseconds} ms");
            return found;
        }, token).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully || token.IsCancellationRequested) return;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { _pictures = t.Result; InvalidateVisual(); });
        }, token);
    }

    /// <summary>The picture under the pointer when it's smaller than the window there, in picture pixels.</summary>
    private FoundImage? PictureAt(Point p)
    {
        if (_pictures is not { Count: > 0 } pictures || Tool != OverlayTool.Area || Recording) return null;
        if (_keys.HasFlag(KeyModifiers.Control) || _keys.HasFlag(KeyModifiers.Alt)) return null;
        var px = ToPicture(p);
        if (ImageFinder.At(pictures, (int)px.X, (int)px.Y) is not { } found) return null;
        var window = WindowAt(p) ?? new PixelRect(_shot.PixelSize);
        if ((long)found.Bounds.Width * found.Bounds.Height >= (long)window.Width * window.Height * 0.9) return null;
        return found;
    }

    private static PixelRect ToPixelRect(System.Windows.Int32Rect r) => new(r.X, r.Y, r.Width, r.Height);

    /// <summary>For the self-test: waits for the picture search, then what a click at (x, y) in picture pixels would take.</summary>
    internal async Task<(int Count, PixelRect? Picture)> PictureForTestAsync(int x, int y)
    {
        for (int i = 0; i < 500 && _pictures == null; i++) await Task.Delay(10);
        var p = new Point(x / Scale, y / Scale);
        return (_pictures?.Count ?? -1, PictureAt(p) is { } f ? ToPixelRect(f.Bounds) : null);
    }

    /// <summary>Picture pixels per unit on screen.</summary>
    private double Scale => Bounds.Width > 0 ? _shot.PixelSize.Width / Bounds.Width : 1;

    private Point ToPicture(Point p) => new(p.X * Scale, p.Y * Scale);

    /// <summary>The selection from two points, in picture pixels (for tests too).</summary>
    public PixelRect ToPixels(Point a, Point b)
    {
        double s = Scale;
        int x0 = (int)Math.Floor(Math.Min(a.X, b.X) * s), y0 = (int)Math.Floor(Math.Min(a.Y, b.Y) * s);
        int x1 = (int)Math.Ceiling(Math.Max(a.X, b.X) * s), y1 = (int)Math.Ceiling(Math.Max(a.Y, b.Y) * s);
        return new PixelRect(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0)).Intersect(new PixelRect(_shot.PixelSize));
    }

    private byte[]? _pixels;

    /// <summary>The box fitted to what's inside it (as on Windows), when that's switched on.</summary>
    public PixelRect Fitted(PixelRect box)
    {
        if (!App.Settings.Tighten || box.Width < 8 || box.Height < 8) return box;
        _pixels ??= Core.Imaging.Pixels(_shot, out _);
        var (l, t, r, b) = Overskin.Tighten.Fit(_pixels, _shot.PixelSize.Width, box.X, box.Y, box.Right, box.Bottom);
        return new PixelRect(l, t, r - l, b - t);
    }

    /// <summary>The window under the pointer, in picture pixels.</summary>
    private PixelRect? WindowAt(Point p)
    {
        var px = ToPicture(p);
        foreach (var (_, r) in Windows)
            if (px.X >= r.X && px.X < r.Right && px.Y >= r.Y && px.Y < r.Bottom)
                return r.Intersect(new PixelRect(_shot.PixelSize));
        return null;
    }

    /// <summary>For snapshots: show a selection without a mouse.</summary>
    public void Pretend(Point start, Point pointer)
    {
        _start = start;
        _pointer = pointer;
        _pointerSeen = true;
        InvalidateVisual();
    }

    public void UndoMark()
    {
        if (Marks.Count > 0) Marks.RemoveAt(Marks.Count - 1);
        InvalidateVisual();
    }

    private bool Drawing => Tool is OverlayTool.Pen or OverlayTool.Arrow or OverlayTool.Rectangle or OverlayTool.Highlighter;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsRightButtonPressed)
        {
            if (_start != null || _drawing != null) { _start = null; _drawing = null; InvalidateVisual(); }
            else (VisualRoot as Window)?.Close();
            return;
        }
        _pointer = point.Position;
        if (Drawing)
        {
            var at = ToPicture(point.Position);
            var red = Color.Parse(App.Settings.PenColour);
            double width = Math.Max(3, App.Settings.PenWidth) * Scale;
            _drawing = Tool switch
            {
                OverlayTool.Pen => new StrokeMark { Colour = red, Width = width, Points = { at } },
                OverlayTool.Highlighter => new StrokeMark { Colour = Color.Parse("#FACC15"), Width = width, Highlighter = true, Points = { at } },
                OverlayTool.Arrow => new ShapeMark { Kind = Interface.Tool.Arrow, From = at, To = at, Colour = red, Width = width },
                _ => new ShapeMark { Kind = Interface.Tool.Rectangle, From = at, To = at, Colour = red, Width = width },
            };
        }
        else _start = point.Position;
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        _keys = e.KeyModifiers;
        _pointer = e.GetPosition(this);
        _pointerSeen = true;
        switch (_drawing)
        {
            case StrokeMark s: s.Points.Add(ToPicture(_pointer)); break;
            case ShapeMark sh: sh.To = ToPicture(_pointer); break;
        }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        if (_drawing != null)
        {
            Marks.Add(_drawing);
            _drawing = null;
            InvalidateVisual();
            return;
        }
        if (_start is not { } start) return;
        var end = e.GetPosition(this);
        _start = null;
        if (Tool == OverlayTool.Area && FixedSize is { } fixedSize) { Picked?.Invoke(FixedAt(end, fixedSize)); return; }
        end = Shaped(start, end);
        bool dragged = Math.Abs(end.X - start.X) > 3 || Math.Abs(end.Y - start.Y) > 3;
        if (dragged && Tool == OverlayTool.Area) { Picked?.Invoke(Ratio > 0 ? ToPixels(start, end) : Fitted(ToPixels(start, end))); return; }
        // a click: a picture inside a window takes just the picture; otherwise the window under it,
        // or the whole screen when no window is known there
        _keys = e.KeyModifiers;
        if (PictureAt(end) is { } picture) { Picked?.Invoke(ToPixelRect(picture.Bounds)); return; }
        Picked?.Invoke(WindowAt(end) ?? new PixelRect(_shot.PixelSize));
    }

    public override void Render(DrawingContext ctx)
    {
        var bounds = new Rect(Bounds.Size);
        ctx.DrawImage(_shot, new Rect(_shot.Size), bounds);
        if (Marks.Count > 0 || _drawing != null)
        {
            using (ctx.PushTransform(Matrix.CreateScale(1 / Scale, 1 / Scale)))
            {
                foreach (var m in Marks) m.Draw(ctx);
                _drawing?.Draw(ctx);
            }
        }

        Rect? lit = null;
        double round = 0;
        FoundImage? snapped = null;
        if (Tool == OverlayTool.Area && FixedSize is { } fs && _pointerSeen && !Drawing)
        {
            var f = FixedAt(_pointer, fs);
            lit = new Rect(f.X / Scale, f.Y / Scale, f.Width / Scale, f.Height / Scale);
        }
        else if (_start is { } start && Tool == OverlayTool.Area) lit = new Rect(start, Shaped(start, _pointer)).Normalize();
        else if (!Drawing && _pointerSeen && PictureAt(_pointer) is { } picture)
        {
            snapped = picture;
            var b = picture.Bounds;
            lit = new Rect(b.X / Scale, b.Y / Scale, b.Width / Scale, b.Height / Scale);
            round = picture.Radius / Scale;
        }
        else if (!Drawing && _pointerSeen && WindowAt(_pointer) is { } w) lit = new Rect(w.X / Scale, w.Y / Scale, w.Width / Scale, w.Height / Scale);

        if (lit is { } sel)
        {
            var hole = new RectangleGeometry(sel, round, round);
            var outside = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(bounds), hole);
            ctx.DrawGeometry(Shade, null, outside);
            ctx.DrawGeometry(null, Edge, hole);
            var px = new PixelRect((int)(sel.X * Scale), (int)(sel.Y * Scale), (int)(sel.Width * Scale), (int)(sel.Height * Scale));
            string size = snapped != null
                ? $"Picture   {snapped.Value.Bounds.Width} × {snapped.Value.Bounds.Height}   ·   {(OperatingSystem.IsMacOS() ? "Option" : "Ctrl")} for the window"
                : $"{px.Width} × {px.Height}";
            DrawLabel(ctx, size, new Point(sel.X, sel.Y - 30 < 70 ? sel.Bottom + 6 : sel.Y - 30));
        }
        else
        {
            if (!Drawing) ctx.DrawRectangle(Shade, null, bounds);
            if (_pointerSeen && !Drawing && App.Settings.Crosshair)
            {
                ctx.DrawLine(Guide, new Point(_pointer.X, 0), new Point(_pointer.X, bounds.Height));
                ctx.DrawLine(Guide, new Point(0, _pointer.Y), new Point(bounds.Width, _pointer.Y));
            }
        }

        var hint = Hint ?? (Drawing ? "Draw on the screen, then pick Area or Window to capture it"
            : Tool == OverlayTool.Window ? "Click a window  ·  Esc to cancel"
            : Recording ? "Drag over the area to record  ·  click a window or the screen  ·  Esc to cancel"
            : "Drag out an area  ·  click a window  ·  Enter for the whole screen  ·  Esc to cancel")
            + (Tool == OverlayTool.Area && !Drawing ? (App.Settings.Tighten ? "  ·  T: the box fits what's inside (on)" : "  ·  T: fit the box (off)") : "");
        var text = Text(hint, 13, Brushes.White);
        DrawLabel(ctx, hint, new Point((bounds.Width - text.Width) / 2 - 10, 68));

        if (_pointerSeen && !Drawing && App.Settings.Magnifier) DrawMagnifier(ctx, bounds);
    }

    private void DrawMagnifier(DrawingContext ctx, Rect bounds)
    {
        const int Cells = 15, Zoom = 8;
        double size = Cells * Zoom;
        double s = Scale;
        int cx = (int)(_pointer.X * s), cy = (int)(_pointer.Y * s);
        var src = new Rect(cx - Cells / 2, cy - Cells / 2, Cells, Cells);

        var at = new Point(_pointer.X + 24, _pointer.Y + 24);
        if (at.X + size > bounds.Width) at = at.WithX(_pointer.X - 24 - size);
        if (at.Y + size + 28 > bounds.Height) at = at.WithY(_pointer.Y - 24 - size - 28);
        var dest = new Rect(at, new Size(size, size));

        ctx.DrawRectangle(Brushes.Black, null, dest);
        using (ctx.PushClip(dest))
        using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None }))
            ctx.DrawImage(_shot, src, dest);
        var mid = new Rect(dest.X + Cells / 2 * Zoom, dest.Y + Cells / 2 * Zoom, Zoom, Zoom);
        ctx.DrawRectangle(null, new Pen(Brushes.White, 1), mid);
        ctx.DrawRectangle(null, Edge, dest);
        DrawLabel(ctx, $"{cx}, {cy}", new Point(dest.X, dest.Bottom + 4), small: true);
    }

    private static FormattedText Text(string s, double size, IBrush brush) =>
        new(s, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Font, size, brush);

    private static void DrawLabel(DrawingContext ctx, string s, Point at, bool small = false)
    {
        var text = Text(s, small ? 11 : 13, Brushes.White);
        double padX = small ? 6 : 10, padY = small ? 3 : 5;
        var back = new Rect(at.X, at.Y, text.Width + padX * 2, text.Height + padY * 2);
        ctx.DrawRectangle(LabelBack, null, back, 6, 6);
        ctx.DrawText(text, new Point(at.X + padX, at.Y + padY));
    }
}
