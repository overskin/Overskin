using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Overskin;

/// <summary>The Watermark card on the Capture page.</summary>
public partial class MainWindow
{
    private bool _syncingWatermark;
    private BitmapSource? _watermarkSample;

    private void SyncWatermarkControls()
    {
        _syncingWatermark = true;
        var w = _settings.Watermark;
        WmShotsSwitch.IsChecked = w.OnScreenshots;
        WmVideosSwitch.IsChecked = w.OnVideos;
        WmText.IsChecked = w.Kind == "Text";
        WmImage.IsChecked = w.Kind == "Image";
        WmTextBox.Text = w.Text;
        WmSize.Value = w.Size;
        WmOpacity.Value = w.Opacity;
        WmWhite.IsChecked = w.Color == "White";
        WmBlack.IsChecked = w.Color == "Black";
        _syncingWatermark = false;
        UpdateWatermarkView();
    }

    private void ReadWatermarkControls()
    {
        if (_syncingWatermark || WmPreview == null || _settings == null) return;
        var w = _settings.Watermark;
        w.OnScreenshots = WmShotsSwitch.IsChecked == true;
        w.OnVideos = WmVideosSwitch.IsChecked == true;
        w.Kind = WmImage.IsChecked == true ? "Image" : "Text";
        w.Text = WmTextBox.Text;
        w.Size = (int)WmSize.Value;
        w.Opacity = (int)WmOpacity.Value;
        w.Color = WmBlack.IsChecked == true ? "Black" : "White";
        UpdateWatermarkView();
        if (_ready) SaveSettings();
    }

    private void Watermark_Changed(object sender, RoutedEventArgs e) => ReadWatermarkControls();
    private void WatermarkText_Changed(object sender, TextChangedEventArgs e) => ReadWatermarkControls();
    private void WatermarkSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => ReadWatermarkControls();

    private void WatermarkImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a watermark picture",
            Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|All files|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        _settings.Watermark.ImagePath = dialog.FileName;
        _settings.Watermark.Kind = "Image";
        SyncWatermarkControls();
        SaveSettings();
    }

    private void WatermarkPosition_Click(object sender, RoutedEventArgs e) =>
        ShowChoices(WmPositionButton, WatermarkOptions.Positions.Select(p => (p, WatermarkOptions.PositionName(p))), _settings.Watermark.Position, p =>
        {
            _settings.Watermark.Position = p;
            UpdateWatermarkView();
            SaveSettings();
        });

    private void UpdateWatermarkView()
    {
        var w = _settings.Watermark;
        bool image = w.Kind == "Image";
        WmTextRow.Visibility = image ? Visibility.Collapsed : Visibility.Visible;
        WmColourRow.Visibility = image ? Visibility.Collapsed : Visibility.Visible;
        WmImageRow.Visibility = image ? Visibility.Visible : Visibility.Collapsed;
        WmImageName.Text = w.ImagePath.Length == 0 ? "None chosen" : File.Exists(w.ImagePath) ? Path.GetFileName(w.ImagePath) : "Missing: " + Path.GetFileName(w.ImagePath);
        WmPositionName.Text = WatermarkOptions.PositionName(w.Position);
        WmSizeText.Text = $"{w.Size}%";
        WmOpacityText.Text = $"{w.Opacity}%";
        _watermarkSample ??= WatermarkSample();
        WmPreview.Source = Watermark.Apply(_watermarkSample, w);
    }

    /// <summary>A made-up screenshot to show the watermark on: a window on a gradient.</summary>
    private static BitmapSource WatermarkSample()
    {
        const int W = 640, H = 360;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x3A, 0x5B, 0xD9), Color.FromRgb(0x9B, 0x6B, 0xE8), 30), null, new Rect(0, 0, W, H));
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0xF6, 0xF7, 0xF9)), null, new Rect(70, 50, 500, 260), 10, 10);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0xE4, 0xE7, 0xEC)), null, new Rect(70, 50, 500, 34), 10, 10);
            var line = new SolidColorBrush(Color.FromRgb(0xD3, 0xD7, 0xDE));
            for (int i = 0; i < 6; i++) dc.DrawRoundedRectangle(line, null, new Rect(100, 110 + i * 28, i % 3 == 2 ? 260 : 420, 10), 5, 5);
        }
        var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }
}
