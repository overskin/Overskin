using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using static Overskin.NativeMethods;

namespace Overskin;

/// <summary>
/// Scrolling capture: pick an area of a scrollable window, then Overskin scrolls it with the
/// mouse wheel and stitches every new strip onto one tall picture until the end of the page.
/// </summary>
public partial class MainWindow
{
    private const int HkScrollEsc = 19;
    private const int MaxScrollSteps = 200;
    private bool _stopScroll;

    private void CaptureScrolling_Click(object sender, RoutedEventArgs e) => CaptureScrolling();

    private void CaptureScrolling() => Guarded(async () =>
    {
        var chosen = await Choose(OverlayMode.Region,
            "Drag over the part of the window that scrolls (not its title bar).  Esc cancels.", useDelay: true);
        if (chosen is not { } c) return;
        if (c.Overlay.Selection is not { } r || r.Height < 40 || r.Width < 40)
        {
            Reappear(c.WasShown);
            return;
        }

        _stopScroll = false;
        _hotkeys?.Register(HkScrollEsc, new Hotkey(ModifierKeys.None, Key.Escape), () => _stopScroll = true);
        Toast.ShowSticky("Scrolling…  Leave the mouse still.  Esc stops.");
        try
        {
            await Task.Delay(150); // let the overlay disappear
            using var grabber = new ScreenGrabber(r.X, r.Y, r.Width, r.Height);
            SetCursorPos(r.X + r.Width / 2, r.Y + r.Height / 2);

            var stitcher = new ScrollStitcher(grabber.Grab(false), r.Width, r.Height);
            int notches = 2, still = 0;
            bool lost = false;
            for (int step = 0; step < MaxScrollSteps && !_stopScroll && !stitcher.Full; step++)
            {
                ScrollWheel(-120 * notches);
                await Task.Delay(320); // smooth scrolling needs a moment to settle
                var result = stitcher.Add(grabber.Grab(false));
                if (result == ScrollStitcher.Step.Lost)
                {
                    lost = true;
                    break;
                }
                if (result == ScrollStitcher.Step.Same)
                {
                    if (++still >= 2) break; // reached the bottom
                    continue;
                }
                still = 0;

                // scroll as far as safely overlaps: about 60% of the area per step
                double perNotch = stitcher.LastShift / (double)notches;
                if (perNotch > 0) notches = Math.Clamp((int)(stitcher.ScrollArea * 0.6 / perNotch), 1, 10);
            }

            Toast.HideNow();
            var image = stitcher.Result();
            Reappear(c.WasShown);
            if (lost) Toast.Show("Scrolled too far to line up; kept what matched.");
            Finish(image, new Int32Rect(r.X, r.Y, r.Width, Math.Min(r.Height, image.PixelHeight)));
        }
        finally
        {
            _hotkeys?.Unregister(HkScrollEsc);
        }
    });

    private static void ScrollWheel(int delta)
    {
        var input = new INPUT[1];
        input[0].type = INPUT_MOUSE;
        input[0].u.mi = new MOUSEINPUT { mouseData = unchecked((uint)delta), dwFlags = MOUSEEVENTF_WHEEL };
        SendInput(1, input, Marshal.SizeOf<INPUT>());
    }
}
