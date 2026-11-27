using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using static Overskin.NativeMethods;

namespace Overskin;

/// <summary>
/// 3, 2, 1 before recording: the area about to be recorded darkens except a clear circle in its
/// middle, with the number in it and a ring that runs down each second. It's gone before the
/// first frame, and it lets clicks through.
/// </summary>
internal static class RecordCountdown
{
    private static bool _running;
    private static bool _cancelled;

    /// <summary>Calls off the countdown that is showing; <see cref="Run"/> then returns false.</summary>
    public static void Cancel()
    {
        if (_running) _cancelled = true;
    }

    /// <summary>Counts down from <paramref name="seconds"/>; false if it was called off.</summary>
    public static async Task<bool> Run(Int32Rect area, int seconds)
    {
        _running = true;
        _cancelled = false;
        var scrim = new Path { Fill = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)) };
        var track = new Ellipse { Stroke = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)) };
        var ring = new Path { StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        ring.SetResourceReference(Shape.StrokeProperty, "Accent");
        var number = new TextBlock
        {
            FontWeight = FontWeights.SemiBold, Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1),
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 0, Opacity = 0.85, Color = Colors.Black },
        };
        number.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
        var canvas = new Grid();
        canvas.Children.Add(scrim);
        canvas.Children.Add(track);
        canvas.Children.Add(ring);
        canvas.Children.Add(number);

        var window = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ShowInTaskbar = false, Topmost = true, ShowActivated = false, ResizeMode = ResizeMode.NoResize,
            Left = -10000, Top = -10000, Width = 10, Height = 10, Content = canvas, Opacity = 0,
        };
        window.SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(window).Handle;
            TrySetExStyle(h, GetExStyle(h) | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, out _);
            Placement.MoveTo(h, area, true);
        };

        // the hole, ring and number, sized to the area once it's in place
        double radius = 0, thickness = 0;
        Point centre = default;
        void Layout()
        {
            double w = canvas.ActualWidth, h = canvas.ActualHeight;
            if (w < 2 || h < 2) return;
            radius = Math.Clamp(Math.Min(w, h) * 0.16, 56, 130);
            thickness = Math.Max(4, radius * 0.07);
            centre = new Point(w / 2, h / 2);
            scrim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, w, h)),
                new EllipseGeometry(centre, radius, radius));
            track.Width = track.Height = (radius - thickness / 2) * 2;
            track.StrokeThickness = ring.StrokeThickness = thickness;
            number.FontSize = radius * 1.05;
        }
        canvas.SizeChanged += (_, _) => Layout();

        void Ring(double left)
        {
            // what's left of this second, clockwise from the top
            double r = radius - thickness / 2;
            if (r <= 0) return;
            left = Math.Clamp(left, 0.0001, 0.9999);
            double angle = left * 2 * Math.PI;
            var start = new Point(centre.X, centre.Y - r);
            var end = new Point(centre.X + r * Math.Sin(angle), centre.Y - r * Math.Cos(angle));
            var g = new StreamGeometry();
            using (var dc = g.Open())
            {
                dc.BeginFigure(start, false, false);
                dc.ArcTo(end, new Size(r, r), 0, angle > Math.PI, SweepDirection.Clockwise, true, false);
            }
            g.Freeze();
            ring.Data = g;
        }

        try
        {
            window.Show();
            window.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
            for (int count = seconds; count > 0; count--)
            {
                number.Text = count.ToString();
                var pop = new DoubleAnimation(1.25, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                number.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
                number.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < 1000)
                {
                    if (_cancelled) return false;
                    Ring(1 - clock.ElapsedMilliseconds / 1000.0);
                    await Task.Delay(16);
                }
            }
            return !_cancelled;
        }
        finally
        {
            window.Close();
            _running = false;
            await Task.Delay(40); // let it leave the screen before the first frame
        }
    }
}
