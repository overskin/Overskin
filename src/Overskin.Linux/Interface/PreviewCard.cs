using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Overskin.Linux.Core;
using Overskin.Linux.Platform;

namespace Overskin.Linux.Interface;

/// <summary>
/// A small card in the corner after a capture: the picture, and Edit, Pin, Copy, View and Show in
/// folder. It goes by itself after a few seconds (not while the pointer is on it). Swipe it right to
/// dismiss it, left to open it in the viewer, as on Windows.
/// </summary>
public sealed class PreviewCard : Window
{
    private static PreviewCard? _current;
    /// <summary>How far the card has to travel to count as a swipe.</summary>
    public const double SwipeDistance = 80;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(6) };
    private readonly string? _path;
    private readonly TranslateTransform _slide = new();
    private Point? _grab;
    private bool _closing;

    private PreviewCard(Bitmap picture, string? path)
    {
        _path = path;
        Title = "Overskin capture";
        SystemDecorations = SystemDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        Width = 320;
        SizeToContent = SizeToContent.Height;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        WindowStartupLocation = WindowStartupLocation.Manual;

        var image = new Image { Source = picture, Stretch = Stretch.Uniform, MaxHeight = 170 };
        var actions = new WrapPanel { ItemSpacing = 4, LineSpacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
        void Act(string icon, string tip, Action run, bool close = true)
        {
            actions.Children.Add(Ui.IconButton(icon, tip, () => { run(); if (close) FadeAway(); }));
        }
        Act(Icons.Pen, "Edit", () => new EditorWindow(picture, path).Show());
        Act(Icons.Pin, "Pin", () => new PinWindow(picture).Show());
        Act(Icons.Copy, "Copy", () => _ = Desktop.CopyImageAsync(picture, this), close: false);
        if (path != null)
        {
            Act(Icons.Picture, "View", OpenViewer);
            Act(Icons.Folder, "Show in folder", () => Desktop.ShowInFolder(path));
        }
        Act(Icons.Close, "Close", () => { });
        var card = new Border
        {
            Background = new SolidColorBrush(Colours.Panel),
            BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(13, 2, 13, 2),
            Padding = new Thickness(10, 10, 10, 8),
            Margin = new Thickness(12),
            RenderTransform = _slide,
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    image,
                    Ui.Label(path != null ? "Saved · swipe left to view, right to dismiss" : "Copied · swipe right to dismiss", "dim"),
                    actions,
                },
            },
        };
        Content = card;

        // the card itself (not its buttons) swipes
        card.PointerPressed += (_, e) =>
        {
            if (e.Source is Button || !e.GetCurrentPoint(card).Properties.IsLeftButtonPressed) return;
            _grab = e.GetPosition(this);
            _timer.Stop();
            e.Pointer.Capture(card);
        };
        card.PointerMoved += (_, e) =>
        {
            if (_grab is not { } from) return;
            double dx = e.GetPosition(this).X - from.X;
            if (dx < 0 && _path == null) dx = -Math.Min(Math.Sqrt(-dx) * 3, 30); // nothing to open: it only leans
            _slide.X = dx;
            card.Opacity = dx > 0 ? Math.Clamp(1 - dx / 280, 0.15, 1) : _path == null ? 1 : LeftFade(-dx);
        };
        card.PointerReleased += (_, e) =>
        {
            if (_grab == null) return;
            _grab = null;
            e.Pointer.Capture(null);
            double dx = _slide.X;
            if (dx >= SwipeDistance) Swipe(+1);
            else if (dx <= -SwipeDistance && _path != null) { OpenViewer(); Swipe(-1); }
            else { _slide.X = 0; card.Opacity = 1; if (!IsPointerOver) _timer.Start(); }
        };
        _timer.Tick += (_, _) => { if (_grab == null) FadeAway(); };
        PointerEntered += (_, _) => _timer.Stop();
        PointerExited += (_, _) => { if (!_closing && _grab == null) _timer.Start(); };
        Opened += (_, _) =>
        {
            Place();
            _timer.Start();
        };
        Closed += (_, _) => { _timer.Stop(); if (ReferenceEquals(_current, this)) _current = null; };
    }

    /// <summary>How visible the card is this far left: fully until a quarter of the way, gone at one and a half times (as on Windows).</summary>
    public static double LeftFade(double distance)
    {
        double t = Math.Clamp((distance - SwipeDistance * 0.25) / (SwipeDistance * 1.25), 0, 1);
        return 1 - t * t * (3 - 2 * t);
    }

    public static void Show(Bitmap picture, string? path)
    {
        _current?.FadeAway();
        _current = new PreviewCard(picture, path);
        _current.Show();
    }

    private void OpenViewer()
    {
        if (_path == null) return;
        var folder = Path.GetDirectoryName(_path)!;
        var pictures = Directory.EnumerateFiles(folder).Where(f => Converter.IsPicture(f) || Converter.IsMoving(f)).OrderByDescending(File.GetLastWriteTimeUtc).ToList();
        new ViewerWindow(pictures, _path).Show();
    }

    /// <summary>Bottom-right of the main screen, above the Dock or taskbar.</summary>
    private void Place()
    {
        var area = Screens.Primary?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        var scale = Screens.Primary?.Scaling ?? 1;
        Position = new PixelPoint(area.Right - (int)((Bounds.Width + 4) * scale), area.Bottom - (int)((Bounds.Height + 4) * scale));
    }

    /// <summary>Carries on the way it was swiped, fading out, then closes.</summary>
    private async void Swipe(int direction)
    {
        if (_closing) return;
        _closing = true;
        for (int i = 1; i <= 12; i++)
        {
            _slide.X += direction * 14;
            if (Content is Border b) b.Opacity = Math.Max(0, b.Opacity - 0.09);
            await Task.Delay(16);
        }
        Close();
    }

    private async void FadeAway()
    {
        if (_closing) return;
        _closing = true;
        _timer.Stop();
        for (int i = 1; i <= 10; i++)
        {
            Opacity = 1 - i / 10.0;
            await Task.Delay(16);
        }
        Close();
    }
}
