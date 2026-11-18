using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Overskin;

/// <summary>Combining several pictures into one, from the Library or from files.</summary>
public partial class MainWindow
{
    private bool _combining;

    private void CombineMode_Click(object sender, RoutedEventArgs e) => SetCombining(!_combining);

    private void SetCombining(bool on)
    {
        _combining = on;
        foreach (var item in _library) item.IsSelected = false;
        _pickOrder.Clear();
        CombineBar.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        LibraryScroll.Margin = new Thickness(0, on ? 92 : 42, 0, 0);
        CombineBar.Margin = new Thickness(0, 42, 0, 0);
        UpdateCombineBar();
    }

    private void UpdateCombineBar()
    {
        int count = _library.Count(i => i.IsSelected);
        CombineText.Text = count switch
        {
            0 => "Click captures to pick them",
            1 => "1 picked",
            _ => $"{count} picked",
        };
    }

    // the order pictures were picked in
    private readonly List<LibraryItem> _pickOrder = new();

    private void Combine_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse<CombineLayout>(tag, out var layout)) return;
        var picked = Picked().Where(i => ClipboardFiles.IsStill(i.Path)).ToList();
        if (picked.Count < 2)
        {
            Toast.Show("Pick at least two pictures.");
            return;
        }
        var images = new List<BitmapSource>();
        foreach (var item in picked)
            if (LoadLibraryImage(item) is { } image) images.Add(image);
        if (images.Count < 2) return;
        SetCombining(false);
        OpenCombined(images, layout);
    }

    /// <summary>From the tray: pick picture files, then combine them.</summary>
    private void CombineFiles(CombineLayout layout)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Pick the pictures to combine",
            Filter = ImageLoader.FileFilter,
            Multiselect = true,
        };
        if (dialog.ShowDialog() != true) return;
        if (dialog.FileNames.Length < 2)
        {
            Toast.Show("Pick at least two pictures.");
            return;
        }
        var images = new List<BitmapSource>();
        foreach (var file in dialog.FileNames)
            if (TryLoadFile(file, out var image)) images.Add(image!);
        if (images.Count >= 2) OpenCombined(images, layout);
    }

    private void OpenCombined(List<BitmapSource> images, CombineLayout layout)
    {
        try
        {
            var combined = ImageCombiner.Combine(images, layout, Colors.White);
            OpenEditor(combined, null, "Combined");
        }
        catch (Exception ex)
        {
            Toast.Show("Couldn't combine them: " + ex.Message, true);
        }
    }
}
