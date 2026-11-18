using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Overskin.Linux.Interface;

/// <summary>
/// Shows the frozen screenshot full screen and lets you drag out the part you want.
/// A click without dragging takes the whole screen; Esc or right-click cancels.
/// </summary>
public sealed class CaptureOverlay : Window
{
    private readonly TaskCompletionSource<PixelRect?> _result = new();
    private readonly SelectionSurface _surface;

    public CaptureOverlay(Bitmap shot, bool recording = false)
    {
        Title = "Overskin capture";
        SystemDecorations = SystemDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        Background = Brushes.Black;
        Cursor = new Cursor(StandardCursorType.Cross);
        WindowStartupLocation = WindowStartupLocation.Manual;

        _surface = new SelectionSurface(shot) { Recording = recording };
        _surface.Picked += r => Finish(r);
        Content = _surface;

        CoverTheDesktop(shot.PixelSize);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Finish(null);
            else if (e.Key is Key.Enter or Key.Space) Finish(new PixelRect(shot.PixelSize));
        };
        Closed += (_, _) => _result.TrySetResult(null);
    }

    private void CoverTheDesktop(PixelSize shotSize)
    {
        var screens = Screens.All;
        if (screens.Count <= 1)
        {
            WindowState = WindowState.FullScreen;
            return;
        }
        // several monitors: one window over all of them, matching the picture of the whole desktop
        var all = screens.Select(s => s.Bounds).Aggregate((a, b) => a.Union(b));
        var scaling = screens.FirstOrDefault(s => s.IsPrimary)?.Scaling ?? 1;
        Position = all.Position;
        Width = all.Width / scaling;
        Height = all.Height / scaling;
    }

    private void Finish(PixelRect? rect)
    {
        _result.TrySetResult(rect);
        Close();
    }

    public static async Task<PixelRect?> PickAsync(Bitmap shot, bool recording = false)
    {
        var overlay = new CaptureOverlay(shot, recording);
        overlay.Show();
        overlay.Activate();
        overlay.Focus();
        return await overlay._result.Task;
    }
}

/// <summary>Draws the screenshot, the dimmed area outside the selection, a size label and a magnifier.</summary>
public sealed class SelectionSurface : Control
{
    private readonly Bitmap _shot;
    private Point? _start;
    private Point _pointer;
    private bool _pointerSeen;

    public event Action<PixelRect>? Picked;
    public bool Recording { get; init; }

    private static readonly IBrush Shade = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0));
    private static readonly IPen Edge = new Pen(new SolidColorBrush(Colours.Accent), 1.5);
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

    /// <summary>The selection from two points, in picture pixels (for tests too).</summary>
    public PixelRect ToPixels(Point a, Point b)
    {
        double s = Scale;
        int x0 = (int)Math.Floor(Math.Min(a.X, b.X) * s), y0 = (int)Math.Floor(Math.Min(a.Y, b.Y) * s);
        int x1 = (int)Math.Ceiling(Math.Max(a.X, b.X) * s), y1 = (int)Math.Ceiling(Math.Max(a.Y, b.Y) * s);
        return new PixelRect(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0)).Intersect(new PixelRect(_shot.PixelSize));
    }

    /// <summary>For snapshots: show a selection without a mouse.</summary>
    public void Pretend(Point start, Point pointer)
    {
        _start = start;
        _pointer = pointer;
        _pointerSeen = true;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsRightButtonPressed)
        {
            if (_start != null) { _start = null; InvalidateVisual(); }
            else (VisualRoot as Window)?.Close();
            return;
        }
        _start = point.Position;
        _pointer = point.Position;
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        _pointer = e.GetPosition(this);
        _pointerSeen = true;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_start is not { } start) return;
        e.Pointer.Capture(null);
        var end = e.GetPosition(this);
        _start = null;
        bool dragged = Math.Abs(end.X - start.X) > 3 || Math.Abs(end.Y - start.Y) > 3;
        Picked?.Invoke(dragged ? ToPixels(start, end) : new PixelRect(_shot.PixelSize));
    }

    public override void Render(DrawingContext ctx)
    {
        var bounds = new Rect(Bounds.Size);
        ctx.DrawImage(_shot, new Rect(_shot.Size), bounds);

        if (_start is { } start)
        {
            var sel = new Rect(start, _pointer).Normalize();
            var outside = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(bounds), new RectangleGeometry(sel));
            ctx.DrawGeometry(Shade, null, outside);
            ctx.DrawRectangle(null, Edge, sel);

            var px = ToPixels(start, _pointer);
            DrawLabel(ctx, $"{px.Width} × {px.Height}", new Point(sel.X, sel.Y - 30 < 4 ? sel.Bottom + 6 : sel.Y - 30));
        }
        else
        {
            ctx.DrawRectangle(Shade, null, bounds);
            if (_pointerSeen)
            {
                ctx.DrawLine(Guide, new Point(_pointer.X, 0), new Point(_pointer.X, bounds.Height));
                ctx.DrawLine(Guide, new Point(0, _pointer.Y), new Point(bounds.Width, _pointer.Y));
            }
            var hint = Recording
                ? "Drag over the area to record  ·  click for the whole screen  ·  Esc to cancel"
                : "Drag to capture  ·  click for the whole screen  ·  Esc to cancel";
            var text = Text(hint, 14, Brushes.White);
            DrawLabel(ctx, hint, new Point((bounds.Width - text.Width) / 2 - 12, 24));
        }

        if (_pointerSeen) DrawMagnifier(ctx, bounds);
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
        using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = Avalonia.Media.Imaging.BitmapInterpolationMode.None }))
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
