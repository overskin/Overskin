using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Overskin;

/// <summary>One thing the command search can find: something to do, or a setting to go to.</summary>
internal sealed record PaletteItem(string Title, string Group, string? Icon, string? Keys, Action Run, string Words = "");

/// <summary>
/// Search everything (Ctrl+K in Overskin, or its hotkey from anywhere): type a few letters and
/// every action, page and setting that matches is listed. Up and Down choose, Enter runs it,
/// Esc or clicking elsewhere closes it.
/// </summary>
internal sealed class CommandPalette : Window
{
    private static CommandPalette? _open;

    private readonly IReadOnlyList<PaletteItem> _all;
    private readonly TextBox _box;
    private readonly StackPanel _list;
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _empty;
    private List<PaletteItem> _shown = new();
    private int _chosen;
    private bool _closing;

    private CommandPalette(IReadOnlyList<PaletteItem> items)
    {
        _all = items;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Width = 600;
        SizeToContent = SizeToContent.Height;
        FontFamily = (FontFamily)FindResource("UiFont");
        FontSize = 13;
        SetResourceReference(ForegroundProperty, "Text");
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        _box = new TextBox { Height = 36, FontSize = 14, Padding = new Thickness(34, 0, 10, 0), VerticalContentAlignment = VerticalAlignment.Center };
        _box.SetResourceReference(StyleProperty, "TextField");
        _box.TextChanged += (_, _) => Filter();
        var glass = new Overskin.Icon { Data = (Geometry)FindResource("IcSearch"), Width = 15, Height = 15, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(11, 0, 0, 0), IsHitTestVisible = false };
        glass.SetResourceReference(Overskin.Icon.ForegroundProperty, "TextFaint");
        var hint = new TextBlock { Text = "Search for anything Overskin does, or a setting", Margin = new Thickness(36, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextFaint");
        _box.TextChanged += (_, _) => hint.Visibility = _box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var field = new Grid();
        field.Children.Add(_box);
        field.Children.Add(glass);
        field.Children.Add(hint);

        _list = new StackPanel();
        _scroll = new ScrollViewer { Content = _list, MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 8, 0, 0), Focusable = false };
        _empty = new TextBlock { Text = "Nothing matches.", Margin = new Thickness(10, 10, 0, 6), Visibility = Visibility.Collapsed };
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");

        var body = new StackPanel { Margin = new Thickness(12) };
        body.Children.Add(field);
        body.Children.Add(_scroll);
        body.Children.Add(_empty);

        var card = new Border { BorderThickness = new Thickness(1), Child = body, Margin = new Thickness(16) };
        card.SetResourceReference(Border.BackgroundProperty, "Panel");
        card.SetResourceReference(Border.BorderBrushProperty, "LineStrong");
        card.SetResourceReference(Border.CornerRadiusProperty, "RadiusWindow");
        card.Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 6, Direction = 270, Opacity = 0.5, Color = Colors.Black };
        Content = card;

        Deactivated += (_, _) => SafeClose();
        Loaded += (_, _) =>
        {
            Activate();
            _box.Focus();
            Keyboard.Focus(_box);
        };
        Filter();
    }

    /// <summary>Opens the search near the top of the screen the mouse is on (or brings it back).</summary>
    public static void Open(IReadOnlyList<PaletteItem> items, Window? over = null)
    {
        if (_open != null)
        {
            _open.Activate();
            return;
        }
        var palette = new CommandPalette(items);
        _open = palette;
        palette.Closed += (_, _) => _open = null;
        // on the monitor with the mouse, a little above the middle
        NativeMethods.GetCursorPos(out var p);
        var work = MonitorWork(p);
        palette.WindowStartupLocation = WindowStartupLocation.Manual;
        palette.SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(palette).Handle;
            double scale = VisualTreeHelper.GetDpi(palette).DpiScaleX;
            int w = (int)(palette.Width * scale);
            int x = work.Left + (work.Right - work.Left - w) / 2;
            int y = work.Top + (work.Bottom - work.Top) / 6;
            if (over is { IsVisible: true, WindowState: not WindowState.Minimized })
            {
                // over Overskin's own window, when it's open
                var ow = new WindowInteropHelper(over).Handle;
                if (NativeMethods.GetWindowRect(ow, out var r))
                {
                    x = r.Left + (r.Right - r.Left - w) / 2;
                    y = r.Top + 50;
                }
            }
            NativeMethods.SetWindowPos(h, NativeMethods.HWND_TOPMOST, x, y, 0, 0, NativeMethods.SWP_NOSIZE);
        };
        palette.Show();
        palette.Activate();
    }

    private static NativeMethods.RECT MonitorWork(NativeMethods.POINT p)
    {
        var mon = MonitorFromPoint(p, 2);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        return GetMonitorInfo(mon, ref info) ? info.rcWork : new NativeMethods.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public NativeMethods.RECT rcMonitor, rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(NativeMethods.POINT pt, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    // ---- matching ------------------------------------------------------------------------------

    /// <summary>How well an item matches: higher is better, 0 is no match. Every typed word must appear.</summary>
    internal static int Score(PaletteItem item, string query)
    {
        var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return 1;
        string title = item.Title.ToLowerInvariant();
        string all = $"{title} {item.Group} {item.Words}".ToLowerInvariant();
        int score = 0;
        foreach (var w in words)
        {
            int at = title.IndexOf(w, StringComparison.Ordinal);
            if (at == 0) score += 30;                                     // the title starts with it
            else if (at > 0 && title[at - 1] == ' ') score += 20;         // a word in the title starts with it
            else if (at > 0) score += 10;
            else if (all.Contains(w)) score += 4;                          // the group or extra words
            else return 0;
        }
        return score;
    }

    private void Filter()
    {
        string q = _box.Text.Trim();
        _shown = _all.Select((item, i) => (item, i, score: Score(item, q)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score).ThenBy(x => x.i)
            .Take(60).Select(x => x.item).ToList();
        _chosen = 0;
        _list.Children.Clear();
        for (int i = 0; i < _shown.Count; i++) _list.Children.Add(Row(_shown[i], i));
        _empty.Visibility = _shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _scroll.Visibility = _shown.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        Highlight();
    }

    private Border Row(PaletteItem item, int index)
    {
        var grid = new Grid { Height = 34 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (item.Icon != null && TryFindResource(item.Icon) is Geometry g)
        {
            var icon = new Overskin.Icon { Data = g, Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            icon.SetResourceReference(Overskin.Icon.ForegroundProperty, "TextDim");
            grid.Children.Add(icon);
        }
        var title = new TextBlock { Text = item.Title, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);
        var right = new TextBlock { Text = item.Keys is { Length: > 0 } k ? $"{item.Group}   {k}" : item.Group, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0) };
        right.SetResourceReference(TextBlock.ForegroundProperty, "TextFaint");
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        var row = new Border { Child = grid, CornerRadius = new CornerRadius(6), Padding = new Thickness(2, 0, 8, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        row.MouseEnter += (_, _) => { _chosen = index; Highlight(); };
        row.MouseLeftButtonUp += (_, _) => Run(index);
        return row;
    }

    private void Highlight()
    {
        for (int i = 0; i < _list.Children.Count; i++)
        {
            var row = (Border)_list.Children[i];
            if (i == _chosen) row.SetResourceReference(Border.BackgroundProperty, "MenuHover");
            else row.Background = Brushes.Transparent;
        }
        if (_chosen < _list.Children.Count) ((FrameworkElement)_list.Children[_chosen]).BringIntoView();
    }

    private void Run(int index)
    {
        if (index < 0 || index >= _shown.Count) return;
        var item = _shown[index];
        SafeClose();
        // after this window has gone, so a capture doesn't see it
        Dispatcher.BeginInvoke(item.Run, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                SafeClose();
                e.Handled = true;
                return;
            case Key.Down:
                if (_shown.Count > 0) _chosen = (_chosen + 1) % _shown.Count;
                Highlight();
                e.Handled = true;
                return;
            case Key.Up:
                if (_shown.Count > 0) _chosen = (_chosen - 1 + _shown.Count) % _shown.Count;
                Highlight();
                e.Handled = true;
                return;
            case Key.Enter:
                Run(_chosen);
                e.Handled = true;
                return;
        }
        base.OnPreviewKeyDown(e);
    }
}
