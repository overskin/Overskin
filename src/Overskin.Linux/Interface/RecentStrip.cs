using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Overskin.Linux.Platform;

namespace Overskin.Linux.Interface;

/// <summary>
/// The latest captures in a strip along the bottom of the screen: click one to copy it,
/// double-click to open it, drag it into another app, right-click for more. Esc closes it.
/// </summary>
public sealed class RecentStrip : Window
{
    public RecentStrip()
    {
        Title = "Overskin recent";
        SystemDecorations = SystemDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;

        var folder = App.Settings.SaveFolder;
        var files = Directory.Exists(folder)
            ? new DirectoryInfo(folder).EnumerateFiles().Where(f => Converter.IsPicture(f.FullName) || Converter.IsMoving(f.FullName))
                .OrderByDescending(f => f.LastWriteTimeUtc).Take(10).Select(f => f.FullName).ToList()
            : new List<string>();

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var f in files) row.Children.Add(Tile(f));
        var hint = Ui.Label(files.Count == 0 ? "Nothing captured yet" : "Click to copy · double-click to open · drag into any app · Esc to close", "dim");
        Content = new Border
        {
            Background = new SolidColorBrush(Colours.Panel),
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 10),
            Child = new StackPanel { Spacing = 8, Children = { hint, row } },
        };

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Deactivated += (_, _) => Close();
        Opened += (_, _) =>
        {
            var area = Screens.Primary?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
            var scale = Screens.Primary?.Scaling ?? 1;
            Position = new PixelPoint(area.X + (int)((area.Width - Bounds.Width * scale) / 2), area.Bottom - (int)((Bounds.Height + 24) * scale));
            Activate();
        };
    }

    private Control Tile(string path)
    {
        var image = new Image { Width = 150, Height = 96, Stretch = Stretch.Uniform };
        _ = Task.Run(async () =>
        {
            try
            {
                Bitmap? thumb;
                if (Converter.IsPicture(path) || path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
                {
                    await using var s = File.OpenRead(path);
                    thumb = Bitmap.DecodeToWidth(s, 300);
                }
                else thumb = await Converter.FirstFrameAsync(path, 300);
                if (thumb != null) Avalonia.Threading.Dispatcher.UIThread.Post(() => image.Source = thumb);
            }
            catch { }
        });
        var tile = new Border
        {
            CornerRadius = new CornerRadius(8, 2, 8, 2),
            ClipToBounds = true,
            Background = new SolidColorBrush(Color.Parse("#0C0D0F")),
            Child = image,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(tile, Path.GetFileName(path));

        Point from = default;
        bool down = false;
        tile.PointerPressed += async (_, e) =>
        {
            var p = e.GetCurrentPoint(tile);
            if (p.Properties.IsRightButtonPressed) { tile.ContextMenu = Menu(path); return; }
            if (e.ClickCount == 2)
            {
                if (Converter.IsPicture(path)) App.Current.OpenInEditor(path); else Desktop.Open(path);
                Close();
                return;
            }
            down = true;
            from = e.GetPosition(this);
            await Task.CompletedTask;
        };
        tile.PointerMoved += async (_, e) =>
        {
            if (!down) return;
            var d = e.GetPosition(this) - from;
            if (Math.Abs(d.X) + Math.Abs(d.Y) < 8) return;
            down = false;
            if (await StorageProvider.TryGetFileFromPathAsync(path) is not { } file) return;
#pragma warning disable CS0618 // DataObject still works in 11.3
            var data = new DataObject();
            data.Set(DataFormats.Files, new IStorageItem[] { file });
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy);
#pragma warning restore CS0618
        };
        tile.PointerReleased += async (_, _) =>
        {
            if (!down) return;
            down = false;
            await Desktop.CopyFilesAsync(new[] { path }, this);
            Desktop.Notify("Copied", Path.GetFileName(path), Converter.IsPicture(path) ? path : App.IconFile);
            Close();
        };
        return tile;
    }

    private ContextMenu Menu(string path)
    {
        var menu = new ContextMenu();
        void Item(string text, Action click)
        {
            var m = new MenuItem { Header = text };
            m.Click += (_, _) => { click(); Close(); };
            menu.Items.Add(m);
        }
        if (Converter.IsPicture(path))
        {
            Item("Edit", () => App.Current.OpenInEditor(path));
            Item("Pin on top", () => App.Current.PinFile(path));
        }
        Item("Open with the default app", () => Desktop.Open(path));
        Item("Show in the file manager", () => Desktop.ShowInFolder(path));
        return menu;
    }
}
