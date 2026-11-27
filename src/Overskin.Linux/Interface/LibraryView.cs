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
    private readonly TextBox _search = new() { Watermark = "Search names, the words in pictures, and what's in them", MinWidth = 280 };
    private readonly ComboBox _sort = new() { ItemsSource = new[] { "Newest first", "Oldest first", "Name", "Biggest first" } };
    // which captures show: 0 all, 1 pictures, 2 GIFs and videos, 3 favourites, 4 duplicates, 5 GIFs, 6 videos
    private readonly ComboBox _filter = new() { ItemsSource = new[] { "Everything", "Pictures", "GIFs and videos", "Favourites", "Duplicates", "GIFs", "Videos" }, SelectedIndex = 0 };
    /// <summary>Path → which group of look-alikes it's in, after "Find duplicates".</summary>
    private Dictionary<string, int> _duplicateGroup = new();
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
        _filter.SelectionChanged += (_, _) => Show();

        // as on Windows: kind chips and the tools on one line, the search under them, then the count
        int[] kinds = { 0, 1, 5, 6, 3 };
        var chips = new List<Avalonia.Controls.Primitives.ToggleButton>();
        var kindRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        string[] names = { "All", "Pictures", "GIFs", "Videos", "" };
        for (int i = 0; i < kinds.Length; i++)
        {
            int kind = kinds[i];
            var chip = new Avalonia.Controls.Primitives.ToggleButton { Classes = { "seg" }, IsChecked = kind == 0, Content = names[i].Length > 0 ? names[i] : Look.Icon(Lucide.Star, 13) };
            if (kind == 3) { chip.MinWidth = 38; ToolTip.SetTip(chip, "Favourites"); }
            chip.Click += (_, _) => _filter.SelectedIndex = kind;
            chips.Add(chip);
            kindRow.Children.Add(chip);
        }
        _filter.SelectionChanged += (_, _) =>
        {
            for (int i = 0; i < kinds.Length; i++) chips[i].IsChecked = kinds[i] == _filter.SelectedIndex;
        };
        Button Tool(string icon, string tip, Action click)
        {
            var b = new Button { Content = Look.Icon(icon, 15), Padding = new Thickness(0), Width = 32, Height = 28, MinHeight = 0, HorizontalContentAlignment = HorizontalAlignment.Center };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => click();
            return b;
        }
        var tools = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Children =
            {
                Tool(Lucide.Hash, "Tags: search with # (say #work)", () => { _search.Text = "#"; _search.Focus(); _search.CaretIndex = 1; }),
                Tool(Lucide.Copy, "Find duplicates", () => _ = FindDuplicatesAsync()),
                Tool(Lucide.RefreshCw, "Refresh", Refresh),
                Tool(Lucide.FolderOpen, "Open the folder", App.Current.OpenFolder),
            },
        };
        DockPanel.SetDock(tools, Dock.Right);
        var first = new DockPanel { Margin = new Thickness(0, 0, 0, 10), Children = { tools, kindRow } };

        // the search, with the order in a menu at its right end (the filter icon)
        var order = new Button { Classes = { "icon" }, Content = Look.Icon(Lucide.ListFilter, 15), Margin = new Thickness(0, 0, 4, 0) };
        ToolTip.SetTip(order, "Order");
        var menu = new MenuFlyout();
        string[] orders = { "Newest first", "Oldest first", "Name", "Biggest first" };
        for (int i = 0; i < orders.Length; i++)
        {
            int index = i;
            var item = new MenuItem { Header = orders[i] };
            item.Click += (_, _) => _sort.SelectedIndex = index;
            menu.Items.Add(item);
        }
        order.Flyout = menu;
        _search.Height = 34;
        _search.Watermark = "Search the words, tags and what's in your captures";
        _search.InnerLeftContent = new Border { Margin = new Thickness(10, 0, 0, 0), Child = Look.Icon(Lucide.Search, 15, Look.Brush(Colours.Dim)) };
        _search.InnerRightContent = order;

        _status.Margin = new Thickness(2, 8, 0, 8);
        _status.TextWrapping = TextWrapping.NoWrap;
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        var top = new StackPanel { Children = { first, _search, _status } };
        DockPanel.SetDock(top, Dock.Top);
        Children.Add(top);
        Children.Add(new ScrollViewer { Content = _grid, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });

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
        IEnumerable<FileInfo> files = _filter.SelectedIndex switch
        {
            1 => _files.Where(f => Converter.IsPicture(f.FullName)),
            2 => _files.Where(f => Converter.IsMoving(f.FullName)),
            5 => _files.Where(f => f.Extension.Equals(".gif", StringComparison.OrdinalIgnoreCase)),
            6 => _files.Where(f => Converter.IsMoving(f.FullName) && !f.Extension.Equals(".gif", StringComparison.OrdinalIgnoreCase)),
            3 => _files.Where(f => LibraryMarks.IsFavourite(f.FullName)),
            // look-alikes together, newest of each group first
            4 => _files.Where(f => _duplicateGroup.ContainsKey(f.FullName)).OrderBy(f => _duplicateGroup[f.FullName]).ThenByDescending(f => f.LastWriteTimeUtc),
            _ => _files,
        };
        var q = _search.Text?.Trim();
        if (string.IsNullOrEmpty(q)) return files;
        // "#work": captures tagged work
        if (q.StartsWith('#'))
        {
            var tags = LibraryMarks.Parse(q);
            return files.Where(f => tags.All(t => LibraryMarks.TagsOf(f.FullName).Contains(t, StringComparer.OrdinalIgnoreCase)));
        }
        return Searched(files, q);
    }

    private IEnumerable<FileInfo> Searched(IEnumerable<FileInfo> files, string q)
    {
        var print = PictureQuery(q);
        return files.Where(f => f.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) || _index.Matches(f.FullName, q)
                                 || LibraryMarks.TagsOf(f.FullName).Any(t => t.Equals(q, StringComparison.OrdinalIgnoreCase))
                                 || (print != null && _pictures.Likeness(f.FullName, print) >= PictureIndex.Match));
    }

    // ---- searching by what's in the picture ----

    private readonly PictureIndex _pictures = new();
    private string _queryText = "";
    private float[]? _queryPrint;
    private bool _queryBusy;

    /// <summary>The search's fingerprint, worked out in the background the first time; the list refreshes when it's ready.</summary>
    private float[]? PictureQuery(string words)
    {
        if (!Ai.Available) return null;
        if (_queryText != words)
        {
            _queryText = words;
            _queryPrint = null;
            if (!_queryBusy) _ = WorkOutQueryAsync();
        }
        return _queryPrint;
    }

    private async Task WorkOutQueryAsync()
    {
        _queryBusy = true;
        try
        {
            while (true)
            {
                var words = _queryText;
                float[]? print = null;
                try { print = await Task.Run(() => Ai.QueryPrint(words)); }
                catch (Exception ex) { Log.Write("picture search: " + ex.Message); }
                if (words != _queryText) continue; // typed more meanwhile
                _queryPrint = print;
                break;
            }
        }
        finally { _queryBusy = false; }
        Show();
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
        long bytes = _files.Sum(f => { try { return f.Length; } catch { return 0L; } });
        var text = _files.Count == 0
            ? "Nothing here yet. Screenshots and recordings you save show up here."
            : shown == _files.Count ? $"{shown} capture{(shown == 1 ? "" : "s")} · {Size(bytes)}" : $"{shown} of {_files.Count} captures · {Size(bytes)}";
        if (_selected.Count > 0) text += $"  ·  {_selected.Count} selected (right-click for what to do with them)";
        if (!Ocr.IsAvailable && _files.Count > 0) text += "  ·  Install Tesseract to search the text in pictures";
        _status.Text = text;
    }

    private Border Tile(FileInfo file)
    {
        var path = file.FullName;
        var image = new Image { Width = 136, Height = 88, Stretch = Stretch.Uniform };
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
        if (LibraryMarks.IsFavourite(path))
            picture.Children.Add(new TextBlock
            {
                Text = "★", FontSize = 16, Foreground = new SolidColorBrush(Color.Parse("#FACC15")), Margin = new Thickness(6, 2),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            });
        var tile = new Border
        {
            Width = 150,
            Padding = new Thickness(6, 6, 6, 7),
            CornerRadius = new CornerRadius(13, 2, 13, 2),
            Background = new SolidColorBrush(Colours.Panel),
            BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new Border { CornerRadius = new CornerRadius(9, 2, 9, 2), ClipToBounds = true, Background = new SolidColorBrush(Colours.Bg), Child = picture, Margin = new Thickness(0, 0, 0, 5) },
                    new TextBlock { Text = file.LastWriteTime.ToString(file.LastWriteTime.Year == DateTime.Now.Year ? "d MMM, HH:mm" : "d MMM yyyy"), FontSize = 12, Margin = new Thickness(2, 0) },
                    new TextBlock { Text = $"{Size(file.Length)} · {file.Extension.TrimStart('.').ToUpperInvariant()}", FontSize = 11, Classes = { "dim" }, Margin = new Thickness(2, 0), TextTrimming = TextTrimming.CharacterEllipsis },
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
            tile.BorderBrush = _selected.Contains(path) ? new SolidColorBrush(Colours.Accent) : new SolidColorBrush(Colours.Line);
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
        bool allFavourite = sel.All(LibraryMarks.IsFavourite);
        Item(allFavourite ? "Remove from favourites" : "Add to favourites ★", () => { LibraryMarks.SetFavourite(sel, !allFavourite); Show(); });
        Item(sel.Count == 1 ? "Tags…" : $"Tag {sel.Count} files…", () => _ = TagAsync(sel));
        if (sel.Count == 2 && sel.All(Converter.IsPicture)) Item("Compare the two (before and after)", () => new CompareWindow(sel[0], sel[1]).Show());
        if (sel.Count == 1) Item("Send to phone…", () => PhoneWindow.Show(sel[0]));
        menu.Items.Add(new Separator());
        if (sel.Count == 1)
        {
            var one = sel[0];
            Item(Converter.IsPicture(one) ? "View" : "Play", () => View(one));
            if (Converter.IsPicture(one)) Item("Edit", () => Open(one, edit: true));
            if (Converter.IsPicture(one)) Item("Pin on top", () => App.Current.PinFile(one));
            Item("Open with the default app", () => Desktop.Open(one));
            if (Converter.IsMoving(one) && !one.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
                Item("Edit the video (trim, cut, crop, speed, text, subtitles)", () => _ = VideoEditor.OpenAsync(one));
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

    /// <summary>Tags for the selected files: typed as words, with or without #.</summary>
    private async Task TagAsync(List<string> sel)
    {
        var current = sel.Count == 1 ? string.Join(" ", LibraryMarks.TagsOf(sel[0]).Select(t => "#" + t)) : "";
        var known = string.Join(" ", LibraryMarks.AllTags.Take(12).Select(t => "#" + t));
        var text = await PromptWindow.AskAsync(TopLevel.GetTopLevel(this) as Window, sel.Count == 1 ? "Tags" : $"Tags for {sel.Count} files",
            "Words to find these by, like #work #receipts. Search the Library for #work to see them." + (known.Length > 0 ? "\nUsed so far: " + known : ""), current);
        if (text == null) return;
        var tags = LibraryMarks.Parse(text);
        foreach (var p in sel)
            LibraryMarks.SetTags(p, sel.Count == 1 ? tags : LibraryMarks.TagsOf(p).Concat(tags));
        Show();
    }

    /// <summary>Finds captures that look the same, shows them together, and picks all but the newest of each.</summary>
    private async Task FindDuplicatesAsync()
    {
        var pictures = _files.Where(f => Converter.IsPicture(f.FullName)).Select(f => f.FullName).ToList();
        _status.Text = $"Looking for duplicates in {pictures.Count} pictures…";
        var groups = await Task.Run(() => Duplicates.Groups(pictures));
        _duplicateGroup = new Dictionary<string, int>();
        for (int g = 0; g < groups.Count; g++) foreach (var p in groups[g]) _duplicateGroup[p] = g;
        if (groups.Count == 0) { _status.Text = "No duplicates found."; return; }
        _selected.Clear();
        foreach (var group in groups)
            foreach (var p in group.OrderByDescending(File.GetLastWriteTimeUtc).Skip(1)) _selected.Add(p);
        _filter.SelectedIndex = 4;
        Show();
        _status.Text = $"{_selected.Count} duplicate{(_selected.Count == 1 ? "" : "s")} in {groups.Count} group{(groups.Count == 1 ? "" : "s")}. " +
                       "The older copies are selected: right-click one to move them to the trash.";
    }

    private void Open(string path, bool edit)
    {
        if (edit && Converter.IsPicture(path)) App.Current.OpenInEditor(path);
        else View(path);
    }

    /// <summary>The viewer, going through what the Library shows (pictures, GIFs and videos play in it).</summary>
    private void View(string path) =>
        new ViewerWindow(_files.Select(f => f.FullName).Where(f => Converter.IsPicture(f) || Converter.IsMoving(f)).ToList(), path).Show();

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
        foreach (var f in sel.Where(Converter.IsMoving)) ZoomTrack.DeleteFor(f);
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
            foreach (var (from, to) in sel.Zip(dialog.Renamed)) { _index.Renamed(from, to); _pictures.Renamed(from, to); LibraryMarks.Renamed(from, to); }
            _pictures.Save();
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
        IndexPictures(cts);
    }

    /// <summary>Looks at every picture once in the background (newest first), for searching by what's in it.</summary>
    private void IndexPictures(CancellationTokenSource cts)
    {
        if (!Ai.Available) return;
        var todo = _files.Where(f => Converter.IsPicture(f.FullName) && !_pictures.Has(f.FullName, f.LastWriteTimeUtc)).Select(f => f.FullName).ToList();
        if (todo.Count == 0) return;
        _ = Task.Run(() =>
        {
            int done = 0;
            foreach (var path in todo)
            {
                if (cts.IsCancellationRequested) break;
                try
                {
                    using var bitmap = new Bitmap(path);
                    _pictures.Set(path, File.GetLastWriteTimeUtc(path), Ai.PictureFingerprint(Ai.Of(bitmap)));
                }
                catch (Exception ex) { Log.Write("picture index: " + Path.GetFileName(path) + ": " + ex.Message); }
                if (++done % 20 == 0) _pictures.Save();
            }
            _pictures.Save();
            Dispatcher.UIThread.Post(() => { if (!string.IsNullOrWhiteSpace(_search.Text)) Show(); });
        });
    }
}

/// <summary>
/// What's in every picture, as a CLIP fingerprint worked out once on this computer, kept beside the
/// log (picture-index.bin), so the Library finds "cat", "chart" or "minecraft" in pictures without
/// those words in them. The same as on Windows.
/// </summary>
public sealed class PictureIndex
{
    private const int Size = 512;
    /// <summary>A picture this alike to the search counts as a match (as on Windows).</summary>
    public const float Match = 0.245f;
    private readonly ConcurrentDictionary<string, (long Ticks, float[] Print)> _entries = new();
    private static string FilePath => Path.Combine(Path.GetDirectoryName(Log.FilePath)!, "picture-index.bin");

    public PictureIndex()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            using var r = new BinaryReader(File.OpenRead(FilePath));
            int count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                string path = r.ReadString();
                long ticks = r.ReadInt64();
                var print = new float[Size];
                for (int k = 0; k < Size; k++) print[k] = r.ReadSingle();
                _entries[path] = (ticks, print);
            }
        }
        catch { _entries.Clear(); } // a damaged index is simply rebuilt
    }

    public bool Has(string path, DateTime modified) => _entries.TryGetValue(path, out var e) && e.Ticks == modified.Ticks;
    public void Set(string path, DateTime modified, float[] print) => _entries[path] = (modified.Ticks, print);
    public float Likeness(string path, float[] query) => _entries.TryGetValue(path, out var e) ? Ai.Likeness(e.Print, query) : 0;

    public void Renamed(string from, string to)
    {
        if (_entries.TryRemove(from, out var e)) _entries[to] = (File.Exists(to) ? File.GetLastWriteTimeUtc(to).Ticks : e.Ticks, e.Print);
    }

    public void Save()
    {
        try
        {
            var all = _entries.Where(e => File.Exists(e.Key)).ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string temp = FilePath + ".tmp";
            using (var w = new BinaryWriter(File.Create(temp)))
            {
                w.Write(all.Length);
                foreach (var (path, (ticks, print)) in all)
                {
                    w.Write(path);
                    w.Write(ticks);
                    foreach (float f in print) w.Write(f);
                }
            }
            File.Move(temp, FilePath, overwrite: true);
        }
        catch { }
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
