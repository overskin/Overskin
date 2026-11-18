using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using static Overskin.NativeMethods;

namespace Overskin;

/// <summary>
/// The latest captures in a strip along the bottom of the screen: drag one straight into
/// another app, or click it to open it. Esc or clicking elsewhere puts it away.
/// </summary>
public sealed class RecentStrip : Window
{
    private static RecentStrip? _open;
    private readonly Action<string> _open_;

    public static void Toggle(IReadOnlyList<string> paths, Action<string> open)
    {
        if (_open != null)
        {
            _open.Close();
            return;
        }
        if (paths.Count == 0)
        {
            Toast.Show("No captures yet.");
            return;
        }
        _open = new RecentStrip(paths, open);
        _open.Closed += (_, _) => _open = null;
        _open.Show();
        _open.Activate();
    }

    private RecentStrip(IReadOnlyList<string> paths, Action<string> open)
    {
        _open_ = open;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = (FontFamily)FindResource("UiFont");
        UseLayoutRounding = true;
        Opacity = 0;

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var path in paths)
            row.Children.Add(Tile(path));

        var title = new TextBlock { Text = "Recent captures", Style = (Style)FindResource("Heading") };
        var hint = new TextBlock
        {
            Text = "Drag one into any app, or click to open.  Esc closes.", Style = (Style)FindResource("Dim"),
            Margin = new Thickness(12, 0, 0, 0), FontSize = 11.5,
        };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 0, 10) };
        head.Children.Add(title);
        head.Children.Add(hint);

        var panel = new StackPanel();
        panel.Children.Add(head);
        panel.Children.Add(row);

        var card = new Border { Child = panel, Padding = new Thickness(14, 12, 6, 12), BorderThickness = new Thickness(1), Margin = new Thickness(12) };
        card.SetResourceReference(Border.BackgroundProperty, "Panel");
        card.SetResourceReference(Border.BorderBrushProperty, "LineStrong");
        card.SetResourceReference(Border.CornerRadiusProperty, "RadiusCard");
        card.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.35, Direction = 270 };
        Content = card;

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Deactivated += (_, _) => { if (!DragOut.IsDragging && IsLoaded) Close(); };
    }

    private FrameworkElement Tile(string path)
    {
        var image = new Image { Stretch = Stretch.UniformToFill, Width = 156, Height = 100 };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        _ = Fill(image, path);

        var frame = new Grid { Width = 156, Height = 100, ClipToBounds = true };
        frame.Children.Add(image);
        if (Thumbs.IsVideo(path) || Thumbs.IsGif(path))
        {
            var badge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xC0, 0, 0, 0)), CornerRadius = new CornerRadius(3),
                Padding = new Thickness(5, 1, 5, 2), Margin = new Thickness(5),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
                Child = new TextBlock { Text = Thumbs.IsVideo(path) ? "Video" : "GIF", Foreground = Brushes.White, FontSize = 10.5, FontWeight = FontWeights.SemiBold },
            };
            frame.Children.Add(badge);
        }

        var tile = new Border
        {
            Child = frame, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand,
            ToolTip = Path.GetFileName(path),
        };
        tile.SetResourceReference(Border.BorderBrushProperty, "Line");
        tile.SetResourceReference(Border.CornerRadiusProperty, "RadiusSmall");
        tile.SetResourceReference(Border.BackgroundProperty, "Bg");
        tile.MouseEnter += (_, _) => tile.SetResourceReference(Border.BorderBrushProperty, "Accent");
        tile.MouseLeave += (_, _) => tile.SetResourceReference(Border.BorderBrushProperty, "Line");

        DragOut.Attach(tile, () => path, () => Thumbs.IsVideo(path) || Thumbs.IsGif(path) ? null : LoadFull(path), () =>
        {
            Close();
            _open_(path);
        });
        return tile;
    }

    private static BitmapSource? LoadFull(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private static async Task Fill(Image image, string path) => image.Source = await Thumbs.LoadAsync(path, 320);

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        // bottom middle of the screen the mouse is on, just above the taskbar
        var handle = new WindowInteropHelper(this).Handle;
        GetWindowRect(handle, out var r);
        var work = MonitorWorkArea(CursorPos());
        int x = work.Left + (work.Width - r.Width) / 2, y = work.Bottom - r.Height - 8;
        SetWindowPos(handle, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE);
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)));
    }
}
