using System.Net;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Overskin.Linux.Core;
using Overskin.Linux.Platform;

namespace Overskin.Linux.Interface;

/// <summary>
/// The step recorder: every click becomes a numbered step with a screenshot and a ring where you
/// clicked, written out as a how-to guide (HTML and Markdown) when you finish. On X11 clicks are
/// noticed by themselves; on Wayland, press a key bound to "overskin --step" for each step.
/// </summary>
public static class Steps
{
    private sealed record Step(Bitmap Picture, Point? Click, string Window);

    private static List<Step>? _steps;
    private static StepsBar? _bar;
    private static CancellationTokenSource? _watch;

    public static bool Running => _steps != null;
    public static bool SeesClicks => Mac.IsMac || (OperatingSystem.IsLinux() && !ScreenGrabber.IsWayland && X11.IsAvailable);

    public static async Task ToggleAsync()
    {
        if (Running) { await FinishAsync(); return; }
        _steps = new List<Step>();
        _bar = new StepsBar();
        _bar.Show();
        Log.Write("steps: started");
        if (SeesClicks) Watch();
        else _bar.Say("Press your “overskin --step” key at each step");
    }

    /// <summary>Adds a step now (the Wayland way, or the bar's button).</summary>
    public static async Task AddStepAsync(Point? click = null)
    {
        if (_steps == null) { await ToggleAsync(); if (SeesClicks) return; }
        try
        {
            _bar?.Hide();
            await Task.Delay(120);
            // on a Mac the click is kept in global points, since the next picture may be of another display
            Mac.CGPoint? clickedAt = Mac.IsMac && click is { } c ? Mac.FromPicture(c) : null;
            var shot = await ScreenGrabber.GrabAsync();
            var at = clickedAt is { } g ? Mac.ToPicture(g) : click ?? (await Input.PointerAsync())?.At;
            string window = "";
            if (at is { } p)
                foreach (var (title, r) in await Input.WindowsAsync())
                    if (p.X >= r.X && p.X < r.Right && p.Y >= r.Y && p.Y < r.Bottom) { window = title; break; }
            _steps!.Add(new Step(shot, at, window));
            _bar?.Say($"{_steps.Count} step{(_steps.Count == 1 ? "" : "s")} so far");
        }
        catch (Exception ex) { Log.Write("steps: " + ex.Message); }
        finally { _bar?.Show(); }
    }

    /// <summary>X11: notices each press of the left button.</summary>
    private static void Watch()
    {
        _watch = new CancellationTokenSource();
        var token = _watch.Token;
        _ = Task.Run(async () =>
        {
            bool wasDown = false;
            while (!token.IsCancellationRequested)
            {
                var pointer = await Input.PointerAsync();
                bool down = pointer?.LeftDown == true;
                if (down && !wasDown && pointer is { } p && !(_bar?.Contains(p.At) ?? false))
                {
                    // the picture is taken as the click lands, before the app reacts to it
                    var at = p.At;
                    await Dispatcher.UIThread.InvokeAsync(() => AddStepAsync(at));
                }
                wasDown = down;
                await Task.Delay(25, token).ContinueWith(_ => { });
            }
        });
    }

    public static async Task FinishAsync()
    {
        var steps = _steps;
        _steps = null;
        _watch?.Cancel();
        _bar?.Close();
        _bar = null;
        if (steps == null || steps.Count == 0) return;

        // check each step, reword it or leave it out, then make the guide (as on Windows)
        var guide = steps.Select(s => new GuideStep
        {
            Picture = s.Click is { } c ? Ring(s.Picture, c) : s.Picture,
            Caption = s.Click == null ? "Next" : string.IsNullOrWhiteSpace(s.Window) ? "Click here" : $"Click in “{s.Window}”",
        }).ToList();
        Log.Write($"steps: {steps.Count} recorded");
        new StepsWindow(guide).Show();
        await Task.CompletedTask;
    }

    /// <summary>The picture with a ring where the click was.</summary>
    private static Bitmap Ring(Bitmap picture, Point at) => Imaging.Render(picture.PixelSize, ctx =>
    {
        ctx.DrawImage(picture, new Rect(picture.Size), new Rect(0, 0, picture.PixelSize.Width, picture.PixelSize.Height));
        double r = Math.Max(18, picture.PixelSize.Height / 45.0);
        ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb(50, 239, 68, 68)), new Pen(new SolidColorBrush(Color.Parse("#EF4444")), r / 5), at, r, r);
    });

    /// <summary>A small bar while recording steps.</summary>
    private sealed class StepsBar : Window
    {
        private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center };

        public StepsBar()
        {
            Title = "Overskin steps";
            SystemDecorations = SystemDecorations.None;
            Topmost = true;
            ShowInTaskbar = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Background = new SolidColorBrush(Colours.Panel);
            _text.Text = "Recording steps: each click becomes one";
            Content = new Border
            {
                Padding = new Thickness(12, 6, 6, 6),
                BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(Color.Parse("#EF4444")), VerticalAlignment = VerticalAlignment.Center },
                        _text,
                        Ui.Button("Add a step", () => _ = AddStepAsync()),
                        Ui.Button("Finish", () => _ = FinishAsync(), cls: "primary"),
                    },
                },
            };
            Opened += (_, _) =>
            {
                var area = Screens.Primary?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
                var scale = Screens.Primary?.Scaling ?? 1;
                Position = new PixelPoint(area.X + (int)((area.Width - Bounds.Width * scale) / 2), area.Bottom - (int)((Bounds.Height + 40) * scale));
            };
        }

        public void Say(string text) => _text.Text = text;

        /// <summary>Clicks on the bar itself aren't steps.</summary>
        public bool Contains(Point screen)
        {
            if (Mac.IsMac)
            {
                var g = Mac.FromPicture(screen);
                return IsVisible && Mac.FrameOf(Mac.NSWindowOf(TryGetPlatformHandle())).Contains(g.X, g.Y);
            }
            var scale = Screens.Primary?.Scaling ?? 1;
            return IsVisible && screen.X >= Position.X && screen.Y >= Position.Y &&
                   screen.X < Position.X + Bounds.Width * scale && screen.Y < Position.Y + Bounds.Height * scale;
        }
    }
}
