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

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        void Tool(OverlayTool tool, string icon, string tip)
        {
            var b = new ToggleButton { Content = Ui.Icon(icon, 17), Classes = { "tool" } };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => Pick(tool);
            _tools[tool] = b;
            bar.Children.Add(b);
        }
        void Separator() => bar.Children.Add(new Border { Width = 1, Height = 22, Margin = new Thickness(6, 0), Background = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)) });

        Tool(OverlayTool.Area, Icons.Area, "Area: drag out a part, or click a window (A)");
        Tool(OverlayTool.Window, Icons.Window, "Window: click one (W)");
        var screenButton = Ui.IconButton(Icons.Screen, "The whole screen (Enter)", () => Finish(new PixelRect(shot.PixelSize)));
        bar.Children.Add(screenButton);
        if (!recording)
        {
            Separator();
            Tool(OverlayTool.Pen, Icons.Pen, "Draw on the screen first (P)");
            Tool(OverlayTool.Arrow, Icons.Arrow, "Arrow (R)");
            Tool(OverlayTool.Rectangle, Icons.Rectangle, "Box (B)");
            Tool(OverlayTool.Highlighter, Icons.Highlighter, "Highlighter (H)");
            bar.Children.Add(Ui.IconButton(Icons.Undo, "Undo the last drawing (Ctrl+Z)", _surface.UndoMark));
        }
        Separator();
        bar.Children.Add(Ui.IconButton(Icons.Close, "Cancel (Esc)", () => Finish(null)));

        var toolbar = new Border
        {
            Background = new SolidColorBrush(Colours.Panel, 0.96),
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(13, 2, 13, 2),
            Padding = new Thickness(6, 4),
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
            bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
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
            }
        };
        Closed += (_, _) => _result.TrySetResult(null);
        Opened += async (_, _) => _surface.Windows = await Input.WindowsAsync();
    }

    private void Pick(OverlayTool tool)
    {
        _surface.Tool = tool;
        foreach (var (t, b) in _tools) b.IsChecked = t == tool;
        _surface.Cursor = new Cursor(tool is OverlayTool.Area or OverlayTool.Window ? StandardCursorType.Cross : StandardCursorType.Hand);
        _surface.InvalidateVisual();
    }

    private void CoverTheDesktop()
    {
        var screens = Screens.All;
        if (screens.Count <= 1 || ScreenGrabber.IsWayland)
        {
            // one monitor (on Wayland the picture is of the focused monitor, where this opens)
            WindowState = WindowState.FullScreen;
            return;
        }
        // several monitors on X11: one window over all of them, matching the picture of the whole desktop
        var all = screens.Select(s => s.Bounds).Aggregate((a, b) => a.Union(b));
        var scaling = screens.FirstOrDefault(s => s.IsPrimary)?.Scaling ?? 1;
        Position = all.Position;
        Width = all.Width / scaling;
        Height = all.Height / scaling;
    }

    private void Finish(PixelRect? rect)
    {
        _result.TrySetResult(rect is { } r ? new OverlayPick(r, _surface.Marks.ToList()) : null);
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
        bool dragged = Math.Abs(end.X - start.X) > 3 || Math.Abs(end.Y - start.Y) > 3;
        if (dragged && Tool == OverlayTool.Area) { Picked?.Invoke(ToPixels(start, end)); return; }
        // a click: the window under it, or the whole screen when no window is known there
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
        if (_start is { } start && Tool == OverlayTool.Area) lit = new Rect(start, _pointer).Normalize();
        else if (!Drawing && _pointerSeen && WindowAt(_pointer) is { } w) lit = new Rect(w.X / Scale, w.Y / Scale, w.Width / Scale, w.Height / Scale);

        if (lit is { } sel)
        {
            var outside = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(bounds), new RectangleGeometry(sel));
            ctx.DrawGeometry(Shade, null, outside);
            ctx.DrawRectangle(null, Edge, sel);
            var px = new PixelRect((int)(sel.X * Scale), (int)(sel.Y * Scale), (int)(sel.Width * Scale), (int)(sel.Height * Scale));
            DrawLabel(ctx, $"{px.Width} × {px.Height}", new Point(sel.X, sel.Y - 30 < 70 ? sel.Bottom + 6 : sel.Y - 30));
        }
        else
        {
            if (!Drawing) ctx.DrawRectangle(Shade, null, bounds);
            if (_pointerSeen && !Drawing)
            {
                ctx.DrawLine(Guide, new Point(_pointer.X, 0), new Point(_pointer.X, bounds.Height));
                ctx.DrawLine(Guide, new Point(0, _pointer.Y), new Point(bounds.Width, _pointer.Y));
            }
        }

        var hint = Hint ?? (Drawing ? "Draw on the screen, then pick Area or Window to capture it"
            : Tool == OverlayTool.Window ? "Click a window  ·  Esc to cancel"
            : Recording ? "Drag over the area to record  ·  click a window or the screen  ·  Esc to cancel"
            : "Drag out an area  ·  click a window  ·  Enter for the whole screen  ·  Esc to cancel");
        var text = Text(hint, 13, Brushes.White);
        DrawLabel(ctx, hint, new Point((bounds.Width - text.Width) / 2 - 10, 68));

        if (_pointerSeen && !Drawing) DrawMagnifier(ctx, bounds);
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
