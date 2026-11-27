using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Overskin.Linux.Core;
using Overskin.Linux.Platform;

namespace Overskin.Linux.Interface;

/// <summary>
/// Scrolling capture: pick the part of a window that scrolls, and Overskin scrolls it and stitches
/// the page together. Where it can't move the mouse (Wayland without ydotool), you scroll and it
/// keeps taking pictures until you press Done.
/// </summary>
public static class ScrollCapture
{
    public static async Task<Bitmap?> RunAsync()
    {
        var still = await ScreenGrabber.GrabAsync();
        var pick = await CaptureOverlay.PickAsync(still, recording: true, hint: "Drag over the part of the window that scrolls  ·  Esc to cancel");
        if (pick == null) return null;
        var area = pick.Area;
        if (area.Width < 40 || area.Height < 40) throw new InvalidOperationException("That area is too small to scroll.");

        bool automatic = Input.CannotDrive() == null;
        var bar = new ScrollBar(automatic);
        bar.Show();
        try
        {
            await Task.Delay(250);
            var first = Px.From(Imaging.Crop(await ScreenGrabber.GrabAsync(), area));
            var stitcher = new ScrollStitcher(first);
            int still_ = 0;

            if (automatic)
            {
                // the wheel only scrolls what's under the pointer
                await Input.MoveAsync(new Point(area.X + area.Width / 2.0, area.Y + area.Height / 2.0));
                for (int i = 0; i < 400 && !bar.Done && !stitcher.Full && !Input.EscapeDown(); i++)
                {
                    await Input.ScrollAsync(3);
                    await Task.Delay(220);
                    var step = stitcher.Add(Px.From(Imaging.Crop(await ScreenGrabber.GrabAsync(), area)));
                    still_ = step == ScrollStitcher.Step.Same ? still_ + 1 : 0;
                    bar.Show(stitcher.Height, step == ScrollStitcher.Step.Lost);
                    if (still_ >= 3) break; // reached the bottom
                }
            }
            else
            {
                while (!bar.Done && !stitcher.Full)
                {
                    await Task.Delay(350);
                    var step = stitcher.Add(Px.From(Imaging.Crop(await ScreenGrabber.GrabAsync(), area)));
                    bar.Show(stitcher.Height, step == ScrollStitcher.Step.Lost);
                }
            }
            Log.Write($"scroll: stitched {stitcher.Height} px tall");
            return stitcher.Result().ToBitmap();
        }
        finally
        {
            bar.Close();
        }
    }

    /// <summary>The bar shown while scrolling: how tall the page is so far, and Done.</summary>
    private sealed class ScrollBar : Window
    {
        private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center };
        public bool Done { get; private set; }

        public ScrollBar(bool automatic)
        {
            Title = "Overskin scrolling capture";
            SystemDecorations = SystemDecorations.None;
            Topmost = true;
            ShowInTaskbar = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Background = new SolidColorBrush(Colours.Panel);
            _text.Text = automatic ? "Scrolling… (Esc or Done to stop)" : "Scroll the page slowly, then press Done";
            Content = new Border
            {
                Padding = new Thickness(12, 6, 6, 6),
                BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _text, Ui.Button("Done", () => Done = true, cls: "primary") } },
            };
            Opened += (_, _) =>
            {
                var area = Screens.Primary?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
                var scale = Screens.Primary?.Scaling ?? 1;
                Position = new PixelPoint(area.Right - (int)((Bounds.Width + 24) * scale), area.Bottom - (int)((Bounds.Height + 24) * scale));
            };
        }

        public void Show(int height, bool lost) =>
            _text.Text = lost ? $"{height} px so far · lost track: scroll back a little" : $"{height} px so far";
    }
}
