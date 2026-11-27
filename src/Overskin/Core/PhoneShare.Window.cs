using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Overskin;

/// <summary>Sending to a phone: the Windows parts (the QR code as a picture, and its window).</summary>
public sealed partial class PhoneShare
{
    /// <summary>A QR code for <paramref name="text"/>, black on white, each module several pixels.</summary>
    public static BitmapSource QrCode(string text, int size = 260)
    {
        var matrix = new ZXing.QrCode.QRCodeWriter().encode(text, ZXing.BarcodeFormat.QR_CODE, size, size,
            new Dictionary<ZXing.EncodeHintType, object> { [ZXing.EncodeHintType.MARGIN] = 2 });
        int w = matrix.Width, h = matrix.Height;
        var px = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                px[y * w + x] = matrix[x, y] ? (byte)0 : (byte)255;
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Gray8, null, px, w);
        bmp.Freeze();
        return bmp;
    }

    // ---- the window ----------------------------------------------------------------------------

    /// <summary>Shows the QR code for a capture until it's closed.</summary>
    public static void Show(string path, Window? owner)
    {
        if (!File.Exists(path))
        {
            Toast.Show("That file has been moved or deleted.");
            return;
        }
        PhoneShare? share;
        try { share = Start(path); }
        catch (Exception ex)
        {
            Toast.Show("Couldn't start sharing: " + ex.Message, true);
            return;
        }
        if (share == null)
        {
            Toast.Show("This PC isn't on a home network, so there's no way for the phone to reach it.", true);
            return;
        }

        var window = new Window
        {
            Title = "Send to your phone",
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is { IsVisible: true } ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            ShowInTaskbar = true,
            Topmost = true,
        };
        if (owner is { IsVisible: true }) window.Owner = owner;
        window.SetResourceReference(Window.BackgroundProperty, "Panel");
        window.SetResourceReference(Window.ForegroundProperty, "Text");
        window.FontFamily = (FontFamily)window.FindResource("UiFont");

        var code = new Image { Source = QrCode(share.Url), Width = 240, Height = 240, Margin = new Thickness(0, 0, 0, 12) };
        RenderOptions.SetBitmapScalingMode(code, BitmapScalingMode.NearestNeighbor);
        var status = new TextBlock { Text = "Point your phone's camera at the code.", FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, 4) };
        var hint = new TextBlock
        {
            Text = "The phone needs to be on the same Wi-Fi as this PC. If Windows asks whether Overskin may use the network, allow it on private networks.",
            TextWrapping = TextWrapping.Wrap, MaxWidth = 260, TextAlignment = TextAlignment.Center, FontSize = 11.5,
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        var address = new TextBox { Text = share.Url, IsReadOnly = true, Margin = new Thickness(0, 10, 0, 0), FontSize = 11, BorderThickness = new Thickness(0), Background = Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Center };
        address.SetResourceReference(Control.ForegroundProperty, "TextFaint");
        var done = new Button { Content = "Done", MinWidth = 90, Height = 30, Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, IsCancel = true, IsDefault = true };
        done.SetResourceReference(FrameworkElement.StyleProperty, "Btn");
        done.Click += (_, _) => window.Close();

        var white = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(10), Padding = new Thickness(6), Child = code, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 12) };
        code.Margin = new Thickness(0);
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(white);
        panel.Children.Add(status);
        panel.Children.Add(hint);
        panel.Children.Add(address);
        panel.Children.Add(done);
        window.Content = panel;

        share.Sent += () => window.Dispatcher.BeginInvoke(() =>
        {
            status.Text = "Sent ✓";
            status.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        });
        window.Closed += (_, _) => share.Dispose();
        window.Show();
    }
}
