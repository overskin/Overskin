using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Overskin;

/// <summary>
/// Copies files the way Explorer does, so a paste gets the real thing: the same name, a GIF that
/// still moves, a video. Still pictures also go on as an image, for apps that only take pixels.
/// </summary>
public static class ClipboardFiles
{
    /// <summary>Copies one or more files; false when the clipboard was busy.</summary>
    public static bool Copy(IReadOnlyList<string> paths, BitmapSource? image = null)
    {
        var data = new DataObject();
        var list = new StringCollection();
        foreach (var p in paths) list.Add(Path.GetFullPath(p));
        data.SetFileDropList(list);
        if (image != null) data.SetImage(image);
        // tells Explorer this is a copy, not a cut
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1)));
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, true);
                return true;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                Thread.Sleep(60); // another app has the clipboard open for a moment
            }
        }
        return false;
    }

    public static bool IsStill(string path) => !Thumbs.IsGif(path) && !Thumbs.IsVideo(path);
}
