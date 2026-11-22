using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace Overskin;

/// <summary>
/// Cropping a GIF: while cropping, the whole frame shows with the box over it; once done, the
/// preview shows the GIF as it will be saved, caption and all, sized to the cropped picture.
/// </summary>
public partial class GifEditorWindow
{
    private CropBox? _cropBox;

    private CropBox Cropper
    {
        get
        {
            if (_cropBox != null) return _cropBox;
            _cropBox = new CropBox(CropCanvas, CropFit, () => new Size(_clip.Width, _clip.Height));
            _cropBox.Changed += () =>
            {
                CropText.Text = CropArea is { } c ? $"{c.Width} × {c.Height}" : "";
                Rebuild();
            };
            return _cropBox;
        }
    }

    private Int32Rect? CropArea => _cropBox?.Crop;

    /// <summary>The picture's width and height after cropping.</summary>
    private int PictureWidth => CropArea?.Width ?? _clip.Width;
    private int PictureHeight => CropArea?.Height ?? _clip.Height;

    /// <summary>A frame as it goes into the GIF: cropped, when there's a crop.</summary>
    private BitmapSource Picture(int frame)
    {
        var bmp = _clip.FrameBitmap(frame);
        if (CropArea is not { } c) return bmp;
        var cut = new CroppedBitmap(bmp, c);
        cut.Freeze();
        return cut;
    }

    /// <summary>Where the whole frame sits in the preview while cropping (the preview shows it with no caption then).</summary>
    private (double Scale, double X, double Y) CropFit()
    {
        double aw = CropCanvas.ActualWidth, ah = CropCanvas.ActualHeight;
        if (aw < 2 || ah < 2) return (0, 0, 0);
        double k = Math.Min(aw / _clip.Width, ah / _clip.Height);
        k = Math.Min(k, Math.Max(1, 520.0 / _clip.Width)); // as GifPreview does
        return (k, (aw - _clip.Width * k) / 2, (ah - _clip.Height * k) / 2);
    }

    private bool Cropping => _cropBox?.Active == true;

    private void CropMode_Click(object sender, RoutedEventArgs e) => SetCropping(!Cropping);

    private void SetCropping(bool on)
    {
        Cropper.Active = on;
        CropCanvas.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        CropButton.Content = on ? "Done" : "Draw the crop";
        Rebuild();
        ShowFrame(ShownFrame);
        Cropper.Draw();
    }

    private void CropReset_Click(object sender, RoutedEventArgs e)
    {
        Cropper.Reset();
        if (CropShapes.Children[0] is RadioButton free) free.IsChecked = true;
        ShowFrame(ShownFrame);
    }

    private void CropShape_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || _clip == null || sender is not RadioButton { Tag: string tag }) return;
        double ratio = VideoEditorWindow.Ratio(tag);
        if (ratio <= 0 && _cropBox == null) return;
        Cropper.SetRatio(ratio);
        if (ratio > 0 && !Cropping) SetCropping(true);
    }

#if DEBUG
    internal void SetCropForTest(Rect box) => Cropper.SetForTest(box);
    internal Size OutputSizeForTest => CaptionRenderer.Layout(PictureWidth, PictureHeight, OutputWidth, Caption, out _);
    internal (int W, int H) RenderFrameForTest(int frame)
    {
        CaptionRenderer.Render(Picture(frame), OutputWidth, Caption, out int w, out int h);
        return (w, h);
    }
#endif
}
