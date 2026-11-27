using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Overskin;

/// <summary>One choice on the ring menu.</summary>
internal sealed record RingItem(string Title, string Icon, Action Run);

/// <summary>
/// One hotkey, then a ring of capture choices round the pointer: move the mouse towards one and
/// click (or press its number). Esc, a right-click or clicking the middle closes it.
/// </summary>
internal sealed class RingMenu : Window
{
    // the disc, the ring of choices on it, and each choice: even room between the choices, to
    // the disc's edge, and round the name in the middle
    private const double Size = 324, Disc = 300, Radius = 106, Dot = 54;

    private static RingMenu? _open;

    private readonly IReadOnlyList<RingItem> _items;
    private readonly List<Border> _dots = new();
    private readonly TextBlock _name;
    private int _chosen = -1;
    private bool _closing;

    private RingMenu(IReadOnlyList<RingItem> items)
    {
        _items = items;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Width = Height = Size;
        FontFamily = (FontFamily)FindResource("UiFont");
        SetResourceReference(ForegroundProperty, "Text");
        UseLayoutRounding = true;

        var canvas = new Canvas { Width = Size, Height = Size, Background = Brushes.Transparent };
        var disc = new Ellipse { Width = Disc, Height = Disc, StrokeThickness = 1, Opacity = 0.96 };
        disc.SetResourceReference(Shape.FillProperty, "Panel");
        disc.SetResourceReference(Shape.StrokeProperty, "LineStrong");
        disc.Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 4, Direction = 270, Opacity = 0.45, Color = Colors.Black };
        Canvas.SetLeft(disc, (Size - Disc) / 2);
        Canvas.SetTop(disc, (Size - Disc) / 2);
        canvas.Children.Add(disc);

        // a faint inner circle the choices sit round, with the name centred in it
        double inner = (Radius - Dot / 2 - 12) * 2;
        var hub = new Border { Width = inner, Height = inner, CornerRadius = new CornerRadius(inner / 2), BorderThickness = new Thickness(1) };
        hub.SetResourceReference(Border.BorderBrushProperty, "Line");
        hub.SetResourceReference(Border.BackgroundProperty, "Bg");
        Canvas.SetLeft(hub, Size / 2 - inner / 2);
        Canvas.SetTop(hub, Size / 2 - inner / 2);
        canvas.Children.Add(hub);
        _name = new TextBlock
        {
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, FontSize = 12, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, LineHeight = 15,
        };
        _name.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        hub.Child = _name;

        for (int i = 0; i < items.Count; i++)
        {
            double a = -Math.PI / 2 + i * 2 * Math.PI / items.Count;
            var icon = new Overskin.Icon { Data = (Geometry)FindResource(items[i].Icon), Width = 19, Height = 19, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            icon.SetResourceReference(Overskin.Icon.ForegroundProperty, "Text");
            var dot = new Border { Width = Dot, Height = Dot, CornerRadius = new CornerRadius(Dot / 2), Child = icon, BorderThickness = new Thickness(1), ToolTip = $"{i + 1}  {items[i].Title}" };
            dot.SetResourceReference(Border.BackgroundProperty, "ButtonFill");
            dot.SetResourceReference(Border.BorderBrushProperty, "Line");
            Canvas.SetLeft(dot, Size / 2 + Radius * Math.Cos(a) - Dot / 2);
            Canvas.SetTop(dot, Size / 2 + Radius * Math.Sin(a) - Dot / 2);
            canvas.Children.Add(dot);
            _dots.Add(dot);
        }
        Content = canvas;

        MouseMove += (_, e) => Choose(e.GetPosition(canvas));
        MouseLeftButtonUp += (_, e) =>
        {
            Choose(e.GetPosition(canvas));
            if (_chosen >= 0) Run(_chosen); else SafeClose();
        };
        MouseRightButtonUp += (_, _) => SafeClose();
        Deactivated += (_, _) => SafeClose();
        Loaded += (_, _) =>
        {
            Activate();
            Focus();
            var grow = new DoubleAnimation(0.85, 1, TimeSpan.FromMilliseconds(130)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            canvas.RenderTransformOrigin = new Point(0.5, 0.5);
            canvas.RenderTransform = new ScaleTransform();
            canvas.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            canvas.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(110)));
        };
        Highlight();
    }

    /// <summary>Shows the ring centred on the pointer.</summary>
    public static void Open(IReadOnlyList<RingItem> items)
    {
        _open?.SafeClose();
        var ring = new RingMenu(items);
        _open = ring;
        ring.Closed += (_, _) => { if (_open == ring) _open = null; };
        NativeMethods.GetCursorPos(out var p);
        ring.WindowStartupLocation = WindowStartupLocation.Manual;
        ring.Left = -10000;
        ring.Top = -10000;
        ring.SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(ring).Handle;
            uint dpi = GetDpiForWindowSafe(h);
            // move onto the pointer's monitor first, then centre with that monitor's scale
            NativeMethods.SetWindowPos(h, NativeMethods.HWND_TOPMOST, p.X, p.Y, 0, 0, NativeMethods.SWP_NOSIZE);
            dpi = GetDpiForWindowSafe(h);
            int side = (int)Math.Round(Size * dpi / 96.0);
            NativeMethods.SetWindowPos(h, NativeMethods.HWND_TOPMOST, p.X - side / 2, p.Y - side / 2, 0, 0, NativeMethods.SWP_NOSIZE);
        };
        ring.Show();
        ring.Activate();
    }

    private static uint GetDpiForWindowSafe(IntPtr h)
    {
        try { uint d = GetDpiForWindow(h); return d == 0 ? 96 : d; }
        catch { return 96; }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>The item in the direction of <paramref name="at"/>, once the mouse is away from the middle.</summary>
    private void Choose(Point at)
    {
        double dx = at.X - Size / 2, dy = at.Y - Size / 2;
        int chosen = -1;
        if (dx * dx + dy * dy > 26 * 26 && dx * dx + dy * dy < (Size / 2 + 40) * (Size / 2 + 40))
        {
            double a = Math.Atan2(dy, dx) + Math.PI / 2;           // 0 at the top, clockwise
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
            _dots[i].SetResourceReference(Border.BackgroundProperty, on ? "AccentFill" : "ButtonFill");
            _dots[i].SetResourceReference(Border.BorderBrushProperty, on ? "Accent" : "Line");
            if (_dots[i].Child is Overskin.Icon icon) icon.SetResourceReference(Overskin.Icon.ForegroundProperty, on ? "AccentInk" : "Text");
        }
        _name.Text = _chosen >= 0 ? _items[_chosen].Title : "Capture";
    }

    private void Run(int index)
    {
        var item = _items[index];
        SafeClose();
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
        if (e.Key == Key.Escape) { SafeClose(); e.Handled = true; return; }
        int n = e.Key is >= Key.D1 and <= Key.D9 ? e.Key - Key.D1 : e.Key is >= Key.NumPad1 and <= Key.NumPad9 ? e.Key - Key.NumPad1 : -1;
        if (n >= 0 && n < _items.Count) { Run(n); e.Handled = true; return; }
        if (e.Key == Key.Enter && _chosen >= 0) { Run(_chosen); e.Handled = true; return; }
        base.OnPreviewKeyDown(e);
    }
}
