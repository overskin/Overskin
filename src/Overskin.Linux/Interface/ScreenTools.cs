using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Overskin.Linux.Core;

namespace Overskin.Linux.Interface;

public enum ScreenTool { Colour, Ruler }

/// <summary>
/// The colour picker and the ruler, over a still of the screen. The colour picker shows a
/// magnified loupe with the colour under the pointer; a click copies it (Shift+click keeps going).
/// The ruler: drag from one point to another for the distance, width and height in pixels.
/// Esc closes either.
/// </summary>
public sealed class ScreenTools : Window
{
    private readonly TaskCompletionSource<string?> _result = new();

    private ScreenTools(Bitmap shot, ScreenTool tool)
    {
        Title = tool == ScreenTool.Colour ? "Overskin colour picker" : "Overskin ruler";
        SystemDecorations = SystemDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        Background = Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.Manual;
        var surface = new ToolSurface(shot, tool) { Cursor = new Cursor(StandardCursorType.Cross) };
        surface.Done += text => { _result.TrySetResult(text); Close(); };
        Content = surface;
        CaptureOverlay.Cover(this);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { _result.TrySetResult(null); Close(); } };
        Closed += (_, _) => _result.TrySetResult(null);
    }

    /// <summary>Shows the tool; the colour copied (as #RRGGBB) or the measurement, or null if closed.</summary>
    public static Task<string?> RunAsync(Bitmap shot, ScreenTool tool)
    {
        var w = new ScreenTools(shot, tool);
        w.Show();
        w.Activate();
        w.Focus();
        return w._result.Task;
    }

    /// <summary>A colour as #RRGGBB.</summary>
    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private sealed class ToolSurface : Control
    {
        private readonly Bitmap _shot;
        private readonly byte[] _px;
        private readonly int _stride;
        private readonly ScreenTool _tool;
        private Point _pointer;
        private bool _seen;
        private Point? _from;
        private readonly List<string> _picked = new();
        public event Action<string>? Done;

        public ToolSurface(Bitmap shot, ScreenTool tool)
        {
            _shot = shot;
            _tool = tool;
            _px = Imaging.Pixels(shot, out _stride);
            Focusable = true;
        }

        private double Scale => Bounds.Width > 0 ? _shot.PixelSize.Width / Bounds.Width : 1;
        private PixelPoint ToPicture(Point p) => new(
            Math.Clamp((int)(p.X * Scale), 0, _shot.PixelSize.Width - 1),
            Math.Clamp((int)(p.Y * Scale), 0, _shot.PixelSize.Height - 1));

        private Color ColourAt(PixelPoint p)
        {
            int i = p.Y * _stride + p.X * 4;
            return Color.FromRgb(_px[i + 2], _px[i + 1], _px[i]);
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            _pointer = e.GetPosition(this);
            _seen = true;
            InvalidateVisual();
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            _pointer = e.GetPosition(this);
            if (_tool == ScreenTool.Ruler) { _from = _pointer; InvalidateVisual(); return; }
            var hex = Hex(ColourAt(ToPicture(_pointer)));
            _picked.Add(hex);
            // Shift+click collects several colours; a plain click finishes
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Shift)) Done?.Invoke(string.Join(" ", _picked));
            else InvalidateVisual();
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            if (_tool != ScreenTool.Ruler || _from is not { } from) return;
            var a = ToPicture(from);
            var b = ToPicture(e.GetPosition(this));
            if (Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) < 2) { _from = null; return; }
            Done?.Invoke(Measure(a, b));
        }

        private static string Measure(PixelPoint a, PixelPoint b)
        {
            int w = Math.Abs(b.X - a.X), h = Math.Abs(b.Y - a.Y);
            double length = Math.Sqrt((double)w * w + (double)h * h);
            return $"{length.ToString("0", CultureInfo.InvariantCulture)} px  ({w} × {h})";
        }

        public override void Render(DrawingContext ctx)
        {
            var size = Bounds.Size;
            ctx.DrawImage(_shot, new Rect(_shot.Size), new Rect(size));
            if (!_seen) return;
            var white = new SolidColorBrush(Colors.White);
            var shade = new SolidColorBrush(Color.FromArgb(225, 18, 19, 22));
            var face = new Typeface(FontFamily.Default);

            if (_tool == ScreenTool.Ruler)
            {
                var end = _pointer;
                var start = _from ?? _pointer;
                var pen = new Pen(new SolidColorBrush(Colours.Accent), 2);
                // crosshair through the pointer, faint, to line things up
                var faint = new Pen(new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)), 1);
                ctx.DrawLine(faint, new Point(0, end.Y), new Point(size.Width, end.Y));
                ctx.DrawLine(faint, new Point(end.X, 0), new Point(end.X, size.Height));
                if (_from != null)
                {
                    ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(120, Colours.Accent.R, Colours.Accent.G, Colours.Accent.B)), 1, DashStyle.Dash),
                        new Rect(start, end).Normalize());
                    ctx.DrawLine(pen, start, end);
                }
                var label = _from != null ? Measure(ToPicture(start), ToPicture(end)) : $"{ToPicture(end).X}, {ToPicture(end).Y}  ·  drag to measure";
                Caption(ctx, label, end, face, shade, white);
                return;
            }

            // the loupe: 11 × 11 pixels round the pointer, magnified, with the middle one outlined
            var at = ToPicture(_pointer);
            const int cells = 11, cell = 11;
            double side = cells * cell;
            var origin = new Point(_pointer.X + 22, _pointer.Y + 22);
            if (origin.X + side + 12 > size.Width) origin = origin.WithX(_pointer.X - 22 - side);
            if (origin.Y + side + 46 > size.Height) origin = origin.WithY(_pointer.Y - 22 - side - 34);
            ctx.DrawRectangle(shade, null, new Rect(origin.X - 4, origin.Y - 4, side + 8, side + 38), 8);
            for (int dy = 0; dy < cells; dy++)
                for (int dx = 0; dx < cells; dx++)
                {
                    int x = Math.Clamp(at.X + dx - cells / 2, 0, _shot.PixelSize.Width - 1);
                    int y = Math.Clamp(at.Y + dy - cells / 2, 0, _shot.PixelSize.Height - 1);
                    ctx.FillRectangle(new SolidColorBrush(ColourAt(new PixelPoint(x, y))), new Rect(origin.X + dx * cell, origin.Y + dy * cell, cell, cell));
                }
            ctx.DrawRectangle(null, new Pen(white, 1.5), new Rect(origin.X + cells / 2 * cell, origin.Y + cells / 2 * cell, cell, cell));
            var colour = ColourAt(at);
            ctx.FillRectangle(new SolidColorBrush(colour), new Rect(origin.X, origin.Y + side + 8, 22, 22), 4);
            var text = new FormattedText(Hex(colour) + (_picked.Count > 0 ? $"  (+{_picked.Count})" : ""), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, face, 13, white);
            ctx.DrawText(text, new Point(origin.X + 30, origin.Y + side + 10));
        }

        private static void Caption(DrawingContext ctx, string label, Point at, Typeface face, IBrush back, IBrush fore)
        {
            var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 13, fore);
            var box = new Rect(at.X + 16, at.Y + 16, text.Width + 16, text.Height + 10);
            ctx.DrawRectangle(back, null, box, 6);
            ctx.DrawText(text, new Point(box.X + 8, box.Y + 5));
        }
    }
}
