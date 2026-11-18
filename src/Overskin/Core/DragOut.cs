using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace Overskin;

/// <summary>
/// Lets a capture be dragged out of Overskin into any app: Discord, a browser upload box,
/// Explorer, an email. It goes as a file (written to a temporary PNG when it hasn't been saved)
/// and, for pictures, as an image too. A plain click still does the element's normal thing.
/// </summary>
public static class DragOut
{
    /// <summary>Set while a drag is running, so popups know not to close themselves.</summary>
    public static bool IsDragging { get; private set; }

    public static void Attach(FrameworkElement element, Func<string?> path, Func<BitmapSource?>? image, Action click)
    {
        Point? down = null;
        bool dragged = false;
        element.PreviewMouseLeftButtonDown += (_, e) => { down = e.GetPosition(element); dragged = false; };
        element.PreviewMouseMove += (_, e) =>
        {
            if (down is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
            var now = e.GetPosition(element);
            if (Math.Abs(now.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(now.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            dragged = true;
            down = null;
            Start(element, path(), image?.Invoke());
        };
        element.PreviewMouseLeftButtonUp += (_, e) =>
        {
            bool wasClick = down != null && !dragged;
            down = null;
            if (wasClick)
            {
                e.Handled = true;
                click();
            }
        };
    }

    private static void Start(DependencyObject source, string? path, BitmapSource? image)
    {
        if (path == null && image == null) return;
        if (path == null || !File.Exists(path))
        {
            path = Path.Combine(Path.GetTempPath(), "Overskin", $"Overskin {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png");
            if (!ImageFiles.TrySave(image!, path, out _)) return;
        }
        var data = new DataObject();
        data.SetFileDropList(new StringCollection { path });
        if (image != null) data.SetImage(image);
        IsDragging = true;
        try { DragDrop.DoDragDrop(source, data, DragDropEffects.Copy); }
        catch { }
        finally { IsDragging = false; }
    }
}
