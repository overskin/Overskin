using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Overskin.Linux.Core;
using Overskin.Linux.Platform;

namespace Overskin.Linux.Interface;

/// <summary>One thing the command search can find: something to do, or a page to go to.</summary>
public sealed record PaletteItem(string Title, string Group, string? Icon, Action Run, string Words = "");

/// <summary>
/// Search everything (its hotkey from anywhere): type a few letters and every action and page that
/// matches is listed. Up and Down choose, Enter runs it, Esc or clicking elsewhere closes it.
/// The same scoring as on Windows.
/// </summary>
public sealed class CommandSearch : Window
{
    private static CommandSearch? _open;
    private readonly IReadOnlyList<PaletteItem> _all;
    private readonly TextBox _box = new() { Watermark = "Search for anything Overskin does", FontSize = 15, Padding = new Thickness(12, 8) };
    private readonly StackPanel _list = new() { Spacing = 2 };
    private List<PaletteItem> _shown = new();
    private int _chosen;
    private bool _closing;

    private CommandSearch(IReadOnlyList<PaletteItem> items)
    {
        _all = items;
        Title = "Overskin search";
        SystemDecorations = SystemDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        Width = 600;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Colours.Panel);
        _box.TextChanged += (_, _) => Filter();
        Content = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10),
            Child = new StackPanel { Spacing = 8, Children = { _box, new ScrollViewer { MaxHeight = 420, Content = _list } } },
        };
        KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Escape: Shut(); e.Handled = true; break;
                case Key.Down: Move(1); e.Handled = true; break;
                case Key.Up: Move(-1); e.Handled = true; break;
                case Key.Enter: if (_shown.Count > 0) Run(_shown[_chosen]); e.Handled = true; break;
            }
        };
        Deactivated += (_, _) => Shut();
        Opened += (_, _) => { Activate(); _box.Focus(); };
        Filter();
    }

    public static void Open(IReadOnlyList<PaletteItem> items)
    {
        if (_open != null) { _open.Shut(); return; }
        _open = new CommandSearch(items);
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }

    /// <summary>How well an item matches what's typed; 0 when it doesn't. As on Windows.</summary>
    public static int Score(PaletteItem item, string query)
    {
        var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return 1;
        string title = item.Title.ToLowerInvariant();
        string all = $"{title} {item.Group} {item.Words}".ToLowerInvariant();
        int score = 0;
        foreach (var w in words)
        {
            int at = title.IndexOf(w, StringComparison.Ordinal);
            if (at == 0) score += 30;
            else if (at > 0 && title[at - 1] == ' ') score += 20;
            else if (at > 0) score += 10;
            else if (all.Contains(w)) score += 4;
            else return 0;
        }
        return score;
    }

    private void Filter()
    {
        string q = (_box.Text ?? "").Trim();
        _shown = _all.Select((item, i) => (item, i, score: Score(item, q))).Where(x => x.score > 0)
            .OrderByDescending(x => x.score).ThenBy(x => x.i).Take(60).Select(x => x.item).ToList();
        _chosen = 0;
        _list.Children.Clear();
        for (int i = 0; i < _shown.Count; i++) _list.Children.Add(Row(_shown[i], i));
        if (_shown.Count == 0) _list.Children.Add(Ui.Label("Nothing matches. Try other words.", "dim"));
        Highlight();
    }

    private Control Row(PaletteItem item, int index)
    {
        var row = new Border
        {
            Padding = new Thickness(10, 7),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new DockPanel
            {
                Children =
                {
                    Docked(Ui.Label(item.Group, "dim"), Dock.Right),
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 10,
                        Children = { Ui.Icon(item.Icon ?? Icons.Area, 15), new TextBlock { Text = item.Title, VerticalAlignment = VerticalAlignment.Center } },
                    },
                },
            },
        };
        row.PointerEntered += (_, _) => { _chosen = index; Highlight(); };
        row.PointerPressed += (_, _) => Run(item);
        return row;
    }

    private static Control Docked(Control c, Dock dock)
    {
        DockPanel.SetDock(c, dock);
        return c;
    }

    private void Move(int by)
    {
        if (_shown.Count == 0) return;
        _chosen = (_chosen + by + _shown.Count) % _shown.Count;
        Highlight();
    }

    private void Highlight()
    {
        for (int i = 0; i < _list.Children.Count; i++)
            if (_list.Children[i] is Border b) b.Background = i == _chosen ? new SolidColorBrush(Colours.Raised) : Brushes.Transparent;
    }

    private void Run(PaletteItem item)
    {
        Shut();
        Avalonia.Threading.Dispatcher.UIThread.Post(item.Run, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void Shut()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }
}

/// <summary>One choice on the ring menu.</summary>
public sealed record RingItem(string Title, string Icon, Action Run);

/// <summary>
/// One hotkey, then a ring of capture choices round the pointer: move the mouse towards one and
/// click (or press its number). Esc, a right-click or clicking the middle closes it. As on Windows.
/// </summary>
public sealed class RingMenu : Window
{
    private const double Size = 324, Disc = 300, Radius = 106, Dot = 54;
    private static RingMenu? _open;
    private readonly IReadOnlyList<RingItem> _items;
    private readonly List<Border> _dots = new();
    private readonly TextBlock _name = new() { TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, FontSize = 12, FontWeight = FontWeight.SemiBold };
    private int _chosen = -1;
    private bool _closing;

    private RingMenu(IReadOnlyList<RingItem> items)
    {
        _items = items;
        Title = "Overskin ring";
        SystemDecorations = SystemDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        Width = Height = Size;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        WindowStartupLocation = WindowStartupLocation.Manual;

        var canvas = new Canvas { Width = Size, Height = Size, Background = Brushes.Transparent };
        var disc = new Ellipse { Width = Disc, Height = Disc, Fill = new SolidColorBrush(Colours.Panel, 0.97), Stroke = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), StrokeThickness = 1 };
        Canvas.SetLeft(disc, (Size - Disc) / 2);
        Canvas.SetTop(disc, (Size - Disc) / 2);
        canvas.Children.Add(disc);
        double inner = (Radius - Dot / 2 - 12) * 2;
        var hub = new Border
        {
            Width = inner, Height = inner, CornerRadius = new CornerRadius(inner / 2), BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), Background = new SolidColorBrush(Colours.Bg),
            Child = _name, Padding = new Thickness(12, 0),
        };
        _name.VerticalAlignment = VerticalAlignment.Center;
        _name.Foreground = new SolidColorBrush(Colours.Dim);
        Canvas.SetLeft(hub, Size / 2 - inner / 2);
        Canvas.SetTop(hub, Size / 2 - inner / 2);
        canvas.Children.Add(hub);
        for (int i = 0; i < items.Count; i++)
        {
            double a = -Math.PI / 2 + i * 2 * Math.PI / items.Count;
            var dot = new Border
            {
                Width = Dot, Height = Dot, CornerRadius = new CornerRadius(Dot / 2), BorderThickness = new Thickness(1),
                Child = Ui.Icon(items[i].Icon, 19),
            };
            ToolTip.SetTip(dot, $"{i + 1}  {items[i].Title}");
            Canvas.SetLeft(dot, Size / 2 + Radius * Math.Cos(a) - Dot / 2);
            Canvas.SetTop(dot, Size / 2 + Radius * Math.Sin(a) - Dot / 2);
            canvas.Children.Add(dot);
            _dots.Add(dot);
        }
        Content = canvas;
        PointerMoved += (_, e) => Choose(e.GetPosition(canvas));
        PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Right) { Shut(); return; }
            Choose(e.GetPosition(canvas));
            if (_chosen >= 0) Run(_chosen); else Shut();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Shut(); return; }
            int n = e.Key is >= Key.D1 and <= Key.D9 ? e.Key - Key.D1 : e.Key is >= Key.NumPad1 and <= Key.NumPad9 ? e.Key - Key.NumPad1 : -1;
            if (n >= 0 && n < _items.Count) Run(n);
            else if (e.Key == Key.Enter && _chosen >= 0) Run(_chosen);
        };
        Deactivated += (_, _) => Shut();
        Highlight();
    }

    /// <summary>Shows the ring centred on the pointer.</summary>
    public static void Open(IReadOnlyList<RingItem> items)
    {
        _open?.Shut();
        var ring = new RingMenu(items);
        _open = ring;
        ring.Closed += (_, _) => { if (_open == ring) _open = null; };
        if (Mac.IsMac)
        {
            var at = Mac.PointerPoints();
            ring.Opened += (_, _) => Mac.Place(Mac.NSWindowOf(ring.TryGetPlatformHandle()), new Mac.CGRect(at.X - Size / 2, at.Y - Size / 2, Size, Size));
        }
        else
        {
            var scale = ring.Screens.Primary?.Scaling ?? 1;
            if (Task.Run(() => Input.PointerAsync()).GetAwaiter().GetResult() is { } p && OperatingSystem.IsLinux() && !ScreenGrabber.IsWayland)
                ring.Position = new PixelPoint((int)(p.At.X - Size * scale / 2), (int)(p.At.Y - Size * scale / 2));
            else ring.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        ring.Show();
        ring.Activate();
    }

    private void Choose(Point at)
    {
        double dx = at.X - Size / 2, dy = at.Y - Size / 2;
        int chosen = -1;
        if (dx * dx + dy * dy > 26 * 26 && dx * dx + dy * dy < (Size / 2 + 40) * (Size / 2 + 40))
        {
            double a = Math.Atan2(dy, dx) + Math.PI / 2;
            double step = 2 * Math.PI / _items.Count;
            chosen = (int)Math.Round((a < 0 ? a + 2 * Math.PI : a) / step) % _items.Count;
        }
        if (chosen == _chosen) return;
        _chosen = chosen;
        Highlight();
    }

    private void Highlight()
    {
        for (int i = 0; i < _dots.Count; i++)
        {
            bool on = i == _chosen;
            _dots[i].Background = new SolidColorBrush(on ? Colours.Accent : Colours.Raised);
            _dots[i].BorderBrush = new SolidColorBrush(on ? Colours.Accent : Color.FromArgb(50, 255, 255, 255));
        }
        _name.Text = _chosen >= 0 ? _items[_chosen].Title : "Capture";
    }

    private void Run(int index)
    {
        var item = _items[index];
        Shut();
        Avalonia.Threading.Dispatcher.UIThread.Post(item.Run, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void Shut()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }
}
