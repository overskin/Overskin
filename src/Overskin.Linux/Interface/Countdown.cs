using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Overskin.Linux.Core;

namespace Overskin.Linux.Interface;

/// <summary>
/// 3, 2, 1 before recording, the same look as on Windows: the screen darkens except a clear circle
/// in the middle of the area about to be recorded, with the number in it and a ring that runs down
/// each second. Esc calls it off. It's gone before the first frame.
/// </summary>
public sealed class Countdown : Window
{
    private readonly TaskCompletionSource<bool> _result = new();
    private readonly Face _face;

    private Countdown(PixelRect area, PixelSize picture)
    {
        Title = "Overskin countdown";
        SystemDecorations = SystemDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        WindowStartupLocation = WindowStartupLocation.Manual;
        _face = new Face(area, picture);
        Content = _face;
        CaptureOverlay.Cover(this);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { _result.TrySetResult(false); Close(); } };
        Closed += (_, _) => _result.TrySetResult(false);
    }

    /// <summary>Counts down from 3 over the area (picture pixels of the last screenshot); false if called off.</summary>
    public static async Task<bool> RunAsync(PixelRect? area, PixelRect? whole, int seconds = 3)
    {
        var all = whole ?? new PixelRect(0, 0, 1920, 1080);
        var w = new Countdown(area ?? all, all.Size);
        w.Show();
        w.Activate();
        var clock = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            double t = clock.Elapsed.TotalSeconds;
            if (t >= seconds) { timer.Stop(); w._result.TrySetResult(true); w.Close(); return; }
            w._face.Set(seconds - (int)t, 1 - (t - (int)t));
        };
        timer.Start();
        bool go = await w._result.Task;
        timer.Stop();
        await Task.Delay(60); // let it leave the screen before the first frame
        return go;
    }

    private sealed class Face : Control
    {
        private readonly PixelRect _area;
        private readonly PixelSize _picture;
        private int _number = 3;
        private double _left = 1;

        public Face(PixelRect area, PixelSize picture)
        {
            _area = area;
            _picture = picture;
        }

        public void Set(int number, double left)
        {
            _number = number;
            _left = left;
            InvalidateVisual();
        }

        public override void Render(DrawingContext ctx)
        {
            var size = Bounds.Size;
            if (size.Width < 2) return;
            // the area's middle, from picture pixels to this window
            double k = size.Width / Math.Max(1, _picture.Width);
            var centre = new Point((_area.X + _area.Width / 2.0) * k, (_area.Y + _area.Height / 2.0) * k);
            double radius = Math.Clamp(Math.Min(_area.Width, _area.Height) * k * 0.16, 56, 130);
            double thickness = Math.Max(4, radius * 0.07);
            var scrim = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(size)), new EllipseGeometry(new Rect(centre.X - radius, centre.Y - radius, radius * 2, radius * 2)));
            ctx.DrawGeometry(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), null, scrim);
            double r = radius - thickness / 2;
            ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), thickness), centre, r, r);
            // what's left of this second, clockwise from the top
            double left = Math.Clamp(_left, 0.0001, 0.9999), angle = left * 2 * Math.PI;
            var arc = new StreamGeometry();
            using (var g = arc.Open())
            {
                g.BeginFigure(new Point(centre.X, centre.Y - r), false);
                g.ArcTo(new Point(centre.X + r * Math.Sin(angle), centre.Y - r * Math.Cos(angle)), new Size(r, r), 0, angle > Math.PI, SweepDirection.Clockwise);
                g.EndFigure(false);
            }
            ctx.DrawGeometry(null, new Pen(new SolidColorBrush(Colours.Accent), thickness) { LineCap = PenLineCap.Round }, arc);
            var text = new FormattedText(_number.ToString(), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Inter", FontStyle.Normal, FontWeight.SemiBold), radius * 1.05, Brushes.White);
            var at = new Point(centre.X - text.Width / 2, centre.Y - text.Height / 2);
            var shadow = text.BuildGeometry(at + new Point(0, 2));
            if (shadow != null) ctx.DrawGeometry(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), null, shadow);
            ctx.DrawText(text, at);
        }
    }
}
