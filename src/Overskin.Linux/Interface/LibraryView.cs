using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Overskin.Linux.Core;
using Overskin.Linux.Platform;

namespace Overskin.Linux.Interface;

/// <summary>
/// Everything in the screenshots folder: search (names, and the text in pictures when Tesseract is
/// installed), sort, select several, and open, edit, pin, copy, rename, convert, combine, drag out or trash.
/// </summary>
public sealed class LibraryView : DockPanel
{
    private readonly TextBox _search = new() { Watermark = "Search names and the text in pictures", MinWidth = 240 };
    private readonly ComboBox _sort = new() { ItemsSource = new[] { "Newest first", "Oldest first", "Name", "Biggest first" } };
    private readonly WrapPanel _grid = new() { ItemSpacing = 10, LineSpacing = 10 };
    private readonly TextBlock _status = Ui.Label("", "dim");
    private readonly HashSet<string> _selected = new();
    private readonly Dictionary<string, Border> _tiles = new();
    private List<FileInfo> _files = new();
    private string? _anchor;
    private static readonly ConcurrentDictionary<string, Bitmap?> Thumbs = new();
    private readonly SearchIndex _index = new();
    private CancellationTokenSource? _indexing;

    public LibraryView()
    {
        _sort.SelectedIndex = App.Settings.LibrarySort switch { "oldest" => 1, "name" => 2, "size" => 3, _ => 0 };
        _sort.SelectionChanged += (_, _) =>
        {
            App.Settings.LibrarySort = _sort.SelectedIndex switch { 1 => "oldest", 2 => "name", 3 => "size", _ => "newest" };
            App.Settings.Save();
            Refresh();
        };
        _search.TextChanged += (_, _) => Show();

        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { _sort, Ui.Button("Refresh", Refresh), Ui.Button("Open folder", App.Current.OpenFolder, Icons.Folder) },
        };
        DockPanel.SetDock(right, Dock.Right);
        right.Margin = new Thickness(8, 0, 0, 0);
        top.Children.Add(right);
        top.Children.Add(_search);

        var footer = new Border { Padding = new Thickness(0, 10, 0, 0), Child = _status };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        Children.Add(top);
        Children.Add(footer);
        Children.Add(new ScrollViewer { Content = _grid });

        Focusable = true;
        KeyDown += OnKey;
        App.Current.LibraryChanged += () => Dispatcher.UIThread.Post(Refresh);
        AttachedToVisualTree += (_, _) => Refresh();
    }

    public void Refresh()
    {
        var folder = App.Settings.SaveFolder;
        _files = Directory.Exists(folder)
            ? new DirectoryInfo(folder).EnumerateFiles().Where(f => Converter.IsPicture(f.FullName) || Converter.IsMoving(f.FullName)).ToList()
            : new List<FileInfo>();
        _files = App.Settings.LibrarySort switch
        {
            "oldest" => _files.OrderBy(f => f.LastWriteTimeUtc).ToList(),
            "name" => _files.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            "size" => _files.OrderByDescending(f => f.Length).ToList(),
            _ => _files.OrderByDescending(f => f.LastWriteTimeUtc).ToList(),
        };
        _selected.RemoveWhere(p => !File.Exists(p));
        Show();
        StartIndexing();
    }

    private IEnumerable<FileInfo> Visible()
    {
        var q = _search.Text?.Trim();
        if (string.IsNullOrEmpty(q)) return _files;
        return _files.Where(f => f.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) || _index.Matches(f.FullName, q));
    }

    private void Show()
    {
        _grid.Children.Clear();
        _tiles.Clear();
        var visible = Visible().ToList();
        foreach (var f in visible) _grid.Children.Add(Tile(f));
        UpdateStatus(visible.Count);
    }

    private void UpdateStatus(int? shown = null)
    {
        shown ??= _grid.Children.Count;
        var text = _files.Count == 0
            ? "Nothing here yet. Screenshots and recordings you save show up here."
            : $"{shown} of {_files.Count}";
        if (_selected.Count > 0) text += $"  ·  {_selected.Count} selected (right-click for what to do with them)";
        if (!Ocr.IsAvailable && _files.Count > 0) text += "  ·  Install Tesseract to search the text in pictures";
        _status.Text = text;
    }

    private Border Tile(FileInfo file)
    {
        var path = file.FullName;
        var image = new Image { Width = 196, Height = 124, Stretch = Stretch.Uniform };
        var badge = Converter.IsMoving(path)
            ? new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)), CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 1),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6),
                Child = new TextBlock { Text = Path.GetExtension(path).TrimStart('.').ToUpperInvariant(), FontSize = 10, FontWeight = FontWeight.Bold, Foreground = Brushes.White },
            }
            : null;
        var picture = new Grid { Children = { image } };
        if (badge != null) picture.Children.Add(badge);
        var tile = new Border
        {
            Width = 212,
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Colours.Panel),
            BorderThickness = new Thickness(2),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new Border { CornerRadius = new CornerRadius(6), ClipToBounds = true, Background = new SolidColorBrush(Color.Parse("#0C0D0F")), Child = picture },
                    new TextBlock { Text = file.Name, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = $"{file.LastWriteTime:d MMM yyyy, HH:mm} · {Size(file.Length)}", FontSize = 11, Classes = { "dim" } },
                },
            },
        };
        ToolTip.SetTip(tile, path);
        _tiles[path] = tile;
        Paint(path);
        _ = LoadThumbAsync(path, image);

        tile.PointerPressed += (_, e) =>
        {
            var point = e.GetCurrentPoint(tile);
            Focus();
            if (point.Properties.IsRightButtonPressed)
            {
                if (!_selected.Contains(path)) Select(path, e.KeyModifiers);
                tile.ContextMenu = Menu();
                return;
            }
            if (e.ClickCount == 2) { Open(path, edit: true); return; }
            Select(path, e.KeyModifiers);
            _dragFrom = e.GetPosition(this);
            _dragging = path;
        };
        tile.PointerMoved += async (_, e) =>
        {
            if (_dragging != path || !e.GetCurrentPoint(tile).Properties.IsLeftButtonPressed) return;
            var d = e.GetPosition(this) - _dragFrom;
            if (Math.Abs(d.X) + Math.Abs(d.Y) < 8) return;
            _dragging = null;
            await DragOutAsync(e);
        };
        tile.PointerReleased += (_, _) => _dragging = null;
        return tile;
    }

    private Point _dragFrom;
    private string? _dragging;

    /// <summary>Drags the selected files into another app (a chat, an email, a folder).</summary>
    private async Task DragOutAsync(PointerEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var items = new List<IStorageItem>();
        foreach (var p in _selected)
            if (await top.StorageProvider.TryGetFileFromPathAsync(p) is { } f) items.Add(f);
        if (items.Count == 0) return;
#pragma warning disable CS0618 // DataObject still works in 11.3
        var data = new DataObject();
        data.Set(DataFormats.Files, items);
        await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy);
#pragma warning restore CS0618
    }

    private static string Size(long bytes) =>
        bytes > 1 << 20 ? $"{bytes / 1048576.0:0.0} MB" : $"{Math.Max(1, bytes / 1024)} KB";

    private static async Task LoadThumbAsync(string path, Image target)
    {
        var key = path + "|" + File.GetLastWriteTimeUtc(path).Ticks;
        if (!Thumbs.TryGetValue(key, out var thumb))
        {
            thumb = await Task.Run(async () =>
            {
                try
                {
                    if (Converter.IsPicture(path) || Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase))
                    {
                        await using var s = File.OpenRead(path);
                        return Bitmap.DecodeToWidth(s, 392);
                    }
                    return await Converter.FirstFrameAsync(path, 392);
                }
                catch { return null; }
            });
            Thumbs[key] = thumb;
        }
        if (thumb != null) target.Source = thumb;
    }

    private void Select(string path, KeyModifiers mods)
    {
        if (mods.HasFlag(KeyModifiers.Shift) && _anchor != null)
        {
            var order = Visible().Select(f => f.FullName).ToList();
            int a = order.IndexOf(_anchor), b = order.IndexOf(path);
            if (a >= 0 && b >= 0)
            {
                if (!mods.HasFlag(KeyModifiers.Control)) _selected.Clear();
                for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++) _selected.Add(order[i]);
            }
        }
        else if (mods.HasFlag(KeyModifiers.Control))
        {
            if (!_selected.Remove(path)) _selected.Add(path);
            _anchor = path;
        }
        else
        {
            _selected.Clear();
            _selected.Add(path);
            _anchor = path;
        }
        foreach (var p in _tiles.Keys) Paint(p);
        UpdateStatus();
    }

    private void Paint(string path)
    {
        if (_tiles.TryGetValue(path, out var tile))
            tile.BorderBrush = _selected.Contains(path) ? new SolidColorBrush(Colours.Accent) : Brushes.Transparent;
    }

    private List<string> Selection() => Visible().Select(f => f.FullName).Where(_selected.Contains).ToList();

    private void OnKey(object? sender, KeyEventArgs e)
    {
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        switch (e.Key)
        {
            case Key.A when ctrl:
                foreach (var f in Visible()) _selected.Add(f.FullName);
                foreach (var p in _tiles.Keys) Paint(p);
                UpdateStatus();
                break;
            case Key.C when ctrl: _ = CopyAsync(); break;
            case Key.Delete: _ = TrashAsync(); break;
            case Key.F2: _ = RenameAsync(); break;
            case Key.Enter when _selected.Count == 1: Open(_selected.First(), edit: true); break;
            case Key.Escape:
                _selected.Clear();
                foreach (var p in _tiles.Keys) Paint(p);
                UpdateStatus();
                break;
            default: return;
        }
        e.Handled = true;
    }

    private ContextMenu Menu()
    {
        var sel = Selection();
        var menu = new ContextMenu();
        MenuItem Item(string text, Action click, ItemsControl? into = null)
        {
            var m = new MenuItem { Header = text };
            m.Click += (_, _) => click();
            (into ?? menu).Items.Add(m);
            return m;
        }
        if (sel.Count == 1)
        {
            var one = sel[0];
            Item(Converter.IsPicture(one) ? "Edit" : "Play", () => Open(one, edit: true));
            if (Converter.IsPicture(one)) Item("Pin on top", () => App.Current.PinFile(one));
            Item("Open with the default app", () => Desktop.Open(one));
            if (Converter.IsMoving(one)) Item("Edit as a GIF (trim, speed, caption)", () => _ = GifEditor.OpenAsync(one));
        }
        Item(sel.Count == 1 ? "Copy" : $"Copy {sel.Count} files", () => _ = CopyAsync());
        Item(sel.Count == 1 ? "Copy the path" : "Copy the paths", () => _ = Desktop.CopyTextAsync(string.Join("\n", sel), TopLevel.GetTopLevel(this)));
        Item(sel.Count == 1 ? "Rename…" : $"Rename {sel.Count} files…", () => _ = RenameAsync());

        var convert = new MenuItem { Header = "Convert to" };
        var targets = sel.Select(Converter.TargetsFor).Aggregate((a, b) => a.Intersect(b).ToArray());
        foreach (var t in targets) Item(t.TrimStart('.').ToUpperInvariant(), () => _ = ConvertAsync(sel, t), convert);
        if (targets.Length > 0) menu.Items.Add(convert);

        var pictures = sel.Where(Converter.IsPicture).ToList();
        if (pictures.Count >= 2)
        {
            var combine = new MenuItem { Header = $"Combine {pictures.Count} pictures" };
            Item("Side by side", () => _ = CombineAsync(pictures, Converter.Layout.SideBySide), combine);
            Item("One above the other", () => _ = CombineAsync(pictures, Converter.Layout.Stacked), combine);
            Item("In a grid", () => _ = CombineAsync(pictures, Converter.Layout.Grid), combine);
            menu.Items.Add(combine);
        }
        menu.Items.Add(new Separator());
        if (sel.Count == 1) Item("Show in the file manager", () => Desktop.ShowInFolder(sel[0]));
        Item(sel.Count == 1 ? "Move to the trash" : $"Move {sel.Count} files to the trash", () => _ = TrashAsync());
        return menu;
    }

    private void Open(string path, bool edit)
    {
        if (edit && Converter.IsPicture(path)) App.Current.OpenInEditor(path);
        else Desktop.Open(path);
    }

    private async Task CopyAsync()
    {
        var sel = Selection();
        if (sel.Count == 0) return;
        bool ok = await Desktop.CopyFilesAsync(sel, TopLevel.GetTopLevel(this));
        _status.Text = ok ? $"Copied {(sel.Count == 1 ? Path.GetFileName(sel[0]) : sel.Count + " files")}" : "Couldn't reach the clipboard. Install wl-clipboard (Wayland) or xclip (X11).";
    }

    private async Task TrashAsync()
    {
        var sel = Selection();
        if (sel.Count == 0) return;
        try { await Desktop.TrashAsync(sel); }
        catch (Exception ex) { _status.Text = "Couldn't move it to the trash: " + ex.Message; return; }
        _selected.Clear();
        Refresh();
        _status.Text = $"Moved {(sel.Count == 1 ? Path.GetFileName(sel[0]) : sel.Count + " files")} to the trash";
    }

    private async Task RenameAsync()
    {
        var sel = Selection();
        if (sel.Count == 0 || TopLevel.GetTopLevel(this) is not Window owner) return;
        var dialog = new RenameWindow(sel);
        await dialog.ShowDialog(owner);
        if (dialog.Renamed.Count > 0)
        {
            _selected.Clear();
            foreach (var p in dialog.Renamed) _selected.Add(p);
            foreach (var (from, to) in sel.Zip(dialog.Renamed)) _index.Renamed(from, to);
            Refresh();
        }
    }

    private async Task ConvertAsync(List<string> paths, string extension)
    {
        int done = 0;
        foreach (var p in paths)
        {
            _status.Text = $"Converting {Path.GetFileName(p)} to {extension.TrimStart('.').ToUpperInvariant()}…";
            try { await Converter.ConvertAsync(p, extension); done++; }
            catch (Exception ex) { _status.Text = ex.Message; return; }
        }
        Refresh();
        _status.Text = $"Converted {done} file{(done == 1 ? "" : "s")}";
    }

    private async Task CombineAsync(List<string> paths, Converter.Layout layout)
    {
        try
        {
            var images = paths.Select(p => new Bitmap(p)).ToList();
            var combined = Converter.Combine(images, layout, 16, Color.FromRgb(255, 255, 255));
            await Task.Yield();
            var path = App.Settings.NewFilePath();
            Imaging.SavePng(combined, path);
            Refresh();
            App.Current.OpenInEditor(path);
        }
        catch (Exception ex) { _status.Text = "Couldn't combine them: " + ex.Message; }
    }

    /// <summary>Reads the text in every picture in the background, for search.</summary>
    private void StartIndexing()
    {
        if (!Ocr.IsAvailable) return;
        _indexing?.Cancel();
        var cts = _indexing = new CancellationTokenSource();
        var todo = _files.Where(f => Converter.IsPicture(f.FullName) && !_index.Has(f.FullName, f.LastWriteTimeUtc)).Select(f => f.FullName).ToList();
        if (todo.Count == 0) return;
        _ = Task.Run(async () =>
        {
            foreach (var path in todo)
            {
                if (cts.IsCancellationRequested) return;
                try
                {
                    using var bitmap = new Bitmap(path);
                    var text = await Ocr.ReadTextAsync(bitmap) ?? "";
                    _index.Set(path, File.GetLastWriteTimeUtc(path), text);
                }
                catch { _index.Set(path, File.GetLastWriteTimeUtc(path), ""); }
            }
            _index.Save();
        });
    }
}

/// <summary>The text found in each picture, kept in ~/.cache/overskin/search-index.json.</summary>
public sealed class SearchIndex
{
    private sealed record Entry(long Ticks, string Text);
    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private static string FilePath => Path.Combine(Path.GetDirectoryName(Log.FilePath)!, "search-index.json");

    public SearchIndex()
    {
        try
        {
            if (File.Exists(FilePath) && System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FilePath)) is { } d)
                foreach (var (k, v) in d) _entries[k] = v;
        }
        catch { }
    }

    public bool Has(string path, DateTime modified) => _entries.TryGetValue(path, out var e) && e.Ticks == modified.Ticks;
    public void Set(string path, DateTime modified, string text) => _entries[path] = new Entry(modified.Ticks, text);
    public bool Matches(string path, string query) => _entries.TryGetValue(path, out var e) && e.Text.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    public void Renamed(string from, string to)
    {
        if (_entries.TryRemove(from, out var e)) _entries[to] = e with { Ticks = File.GetLastWriteTimeUtc(to).Ticks };
        Save();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, System.Text.Json.JsonSerializer.Serialize(_entries.Where(kv => File.Exists(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value)));
        }
        catch { }
    }
}
