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
        // the message can't stay up while the screen is read: Overskin keeps its own windows out of
        // captures, and the way scrolling reads the screen shows them as a black box
        Toast.Show("Scrolling…  Leave the mouse still.  Esc stops.");
        try
        {
            await Task.Delay(700);
            Toast.HideNow();
            await Task.Delay(150); // let the overlay and the message disappear
            using var grabber = new ScreenGrabber(r.X, r.Y, r.Width, r.Height);
            SetCursorPos(r.X + r.Width / 2, r.Y + r.Height / 2);

            // down the page first; if it doesn't move (a chat already at its newest message), up instead
            var (image, lost, up) = await ScrollAndStitch(grabber, r, down: true);
            if (image == null)
                (image, lost, up) = await ScrollAndStitch(grabber, r, down: false);
            image ??= new ScrollStitcher(grabber.Grab(false), r.Width, r.Height).Result();

            Reappear(c.WasShown);
            if (lost) Toast.Show("Scrolled too far to line up; kept what matched.");
            else if (up) Toast.Show("It was at the end already, so it went up the page instead.");
            Finish(image, new Int32Rect(r.X, r.Y, r.Width, Math.Min(r.Height, image.PixelHeight)));
        }
        finally
        {
            _hotkeys?.Unregister(HkScrollEsc);
        }
    });

    /// <summary>
    /// Scrolls one way and stitches every new strip on. Going up is the same as going down with
    /// every frame turned upside down, so the one stitcher does both. Null when nothing moved at all.
    /// </summary>
    private async Task<(System.Windows.Media.Imaging.BitmapSource? Image, bool Lost, bool Up)> ScrollAndStitch(ScreenGrabber grabber, Int32Rect r, bool down)
    {
        byte[] Grab()
        {
            var frame = grabber.Grab(false);
            return down ? frame : Flip(frame, r.Width, r.Height);
        }
        var stitcher = new ScrollStitcher(Grab(), r.Width, r.Height);
        int notches = 2, still = 0, moved = 0;
        bool lost = false;
        for (int step = 0; step < MaxScrollSteps && !_stopScroll && !stitcher.Full; step++)
        {
            ScrollWheel((down ? -120 : 120) * notches);
            await Task.Delay(320); // smooth scrolling needs a moment to settle
            var result = stitcher.Add(Grab());
            // the page may still be gliding, or something on it changed for a moment: look again
            for (int retry = 0; retry < 2 && result == ScrollStitcher.Step.Lost && !_stopScroll; retry++)
            {
                await Task.Delay(280);
                result = stitcher.Add(Grab());
            }
            if (result == ScrollStitcher.Step.Lost)
            {
                lost = true;
                break;
            }
            if (result == ScrollStitcher.Step.Same)
            {
                // an app that loads more as you go (a chat's history) can pause a moment at the edge
                if (++still >= (moved == 0 ? 2 : 3)) break;
                await Task.Delay(still * 250);
                continue;
            }
            still = 0;
            moved++;

            // scroll as far as safely overlaps: about 60% of the area per step
            double perNotch = stitcher.LastShift / (double)notches;
            if (perNotch > 0) notches = Math.Clamp((int)(stitcher.ScrollArea * 0.6 / perNotch), 1, 10);
        }
        if (moved == 0) return (null, false, !down);
        var image = stitcher.Result();
        if (!down) image = FlipImage(image);
        return (image, lost, !down);
    }

    /// <summary>A BGRA frame upside down.</summary>
    private static byte[] Flip(byte[] frame, int w, int h)
    {
        var flipped = new byte[frame.Length];
        int stride = w * 4;
        for (int y = 0; y < h; y++) Buffer.BlockCopy(frame, y * stride, flipped, (h - 1 - y) * stride, stride);
        return flipped;
    }

    private static System.Windows.Media.Imaging.BitmapSource FlipImage(System.Windows.Media.Imaging.BitmapSource image)
    {
        int w = image.PixelWidth, h = image.PixelHeight;
        var px = new byte[w * h * 4];
        new System.Windows.Media.Imaging.FormatConvertedBitmap(image, System.Windows.Media.PixelFormats.Bgra32, null, 0).CopyPixels(px, w * 4, 0);
        var flipped = System.Windows.Media.Imaging.BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, Flip(px, w, h), w * 4);
        flipped.Freeze();
        return flipped;
    }

    private static void ScrollWheel(int delta)
    {
        var input = new INPUT[1];
        input[0].type = INPUT_MOUSE;
        input[0].u.mi = new MOUSEINPUT { mouseData = unchecked((uint)delta), dwFlags = MOUSEEVENTF_WHEEL };
        SendInput(1, input, Marshal.SizeOf<INPUT>());
    }
}
