using System.Windows;
using System.Windows.Controls;

namespace Overskin;

/// <summary>Cropping a video: a box over the player, saved with the blur pass.</summary>
public partial class VideoEditorWindow
{
    private CropBox? _cropBox;

    private CropBox Cropper
    {
        get
        {
            if (_cropBox != null) return _cropBox;
            _cropBox = new CropBox(CropCanvas, Fit, () => new Size(Math.Max(2, Player.NaturalVideoWidth), Math.Max(2, Player.NaturalVideoHeight))) { Even = true };
            _cropBox.Changed += () => CropText.Text = CropArea is { } c ? $"{c.Width} × {c.Height}" : "";
            return _cropBox;
        }
    }

    /// <summary>The part of the video kept, in its pixels; null for all of it.</summary>
    private Int32Rect? CropArea => _cropBox?.Crop;

    private void CropMode_Click(object sender, RoutedEventArgs e) => SetCropping(!Cropper.Active);

    private void SetCropping(bool on)
    {
        if (on) Play(false);
        Cropper.Active = on;
        CropButton.Content = on ? "Done" : "Draw the crop";
    }

    private void CropReset_Click(object sender, RoutedEventArgs e)
    {
        Cropper.Reset();
        if (CropShapes.Children[0] is RadioButton free) free.IsChecked = true;
    }

    private void CropShape_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || sender is not RadioButton { Tag: string tag }) return;
        if (!_opened) return;
        Cropper.SetRatio(Ratio(tag));
        if (Ratio(tag) > 0 && !Cropper.Active) SetCropping(true);
    }

    /// <summary>"16:9" → 1.777…; "0" → 0 (any shape).</summary>
    internal static double Ratio(string tag)
    {
        var parts = tag.Split(':');
        return parts.Length == 2 && double.TryParse(parts[0], out double a) && double.TryParse(parts[1], out double b) && b > 0 ? a / b : 0;
    }

#if DEBUG
    internal void SetCropForTest(Rect box) => Cropper.SetForTest(box);
    internal Int32Rect? CropForTest => CropArea;
    internal void ActivateCropForTest() => SetCropping(true);
#endif
}
