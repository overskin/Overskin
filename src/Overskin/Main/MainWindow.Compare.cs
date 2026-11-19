using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Overskin;

/// <summary>Comparing two pictures in a pinned before/after window.</summary>
public partial class MainWindow
{
    private void OpenCompare(BitmapSource before, BitmapSource after, string beforeName, string afterName) =>
        new CompareWindow(before, after, beforeName, afterName).Show();

    /// <summary>Library: the two picked pictures, in the order they were picked.</summary>
    private void SelectCompare_Click(object sender, RoutedEventArgs e)
    {
        var picked = Picked().Where(i => ClipboardFiles.IsStill(i.Path)).ToList();
        if (picked.Count != 2)
        {
            Toast.Show("Pick exactly two pictures to compare.");
            return;
        }
        if (LoadLibraryImage(picked[0]) is not { } a || LoadLibraryImage(picked[1]) is not { } b) return;
        OpenCompare(a, b, Path.GetFileName(picked[0].Path), Path.GetFileName(picked[1].Path));
    }

    /// <summary>Tray: choose two picture files.</summary>
    private void CompareFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose two pictures to compare",
            Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff|All files|*.*",
            Multiselect = true,
            InitialDirectory = Directory.Exists(_settings.SaveFolder) ? _settings.SaveFolder : null,
        };
        if (dialog.ShowDialog() != true) return;
        var files = dialog.FileNames.ToList();
        if (files.Count == 1)
        {
            // one at a time is fine too
            dialog.Title = "Now choose the second picture";
            dialog.Multiselect = false;
            if (dialog.ShowDialog() != true) return;
            files.Add(dialog.FileName);
        }
        if (files.Count != 2)
        {
            Toast.Show("Choose two pictures.");
            return;
        }
        if (!TryLoadFile(files[0], out var a) || !TryLoadFile(files[1], out var b)) return;
        OpenCompare(a!, b!, Path.GetFileName(files[0]), Path.GetFileName(files[1]));
    }
}
