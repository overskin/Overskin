using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Overskin;

/// <summary>The Library's pick-several mode, renaming, and sorting.</summary>
public partial class MainWindow
{
    /// <summary>Picked captures, in the order they were clicked.</summary>
    private List<LibraryItem> Picked() =>
        _pickOrder.Where(i => i.IsSelected && _library.Contains(i))
            .Concat(_library.Where(i => i.IsSelected && !_pickOrder.Contains(i)))
            .Where(i => File.Exists(i.Path)).ToList();

    private bool AnyPicked(out List<LibraryItem> picked)
    {
        picked = Picked();
        if (picked.Count > 0) return true;
        Toast.Show("Click some captures first.");
        return false;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        bool all = _library.All(i => i.IsSelected);
        foreach (var item in _library)
        {
            item.IsSelected = !all;
            if (!all && !_pickOrder.Contains(item)) _pickOrder.Add(item);
        }
        UpdateCombineBar();
    }

    private void SelectCopy_Click(object sender, RoutedEventArgs e)
    {
        if (!AnyPicked(out var picked)) return;
        Toast.Show(ClipboardFiles.Copy(picked.Select(i => i.Path).ToList())
            ? $"Copied {picked.Count} file{(picked.Count == 1 ? "" : "s")}." : "The clipboard is busy. Try again.");
    }

    private void SelectCombine_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = SelectCombineButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var (label, icon, layout) in new[] { ("Side by side", "IcArrowLeftRight", "SideBySide"), ("Stacked", "IcArrowUpDown", "Stacked"), ("Grid", "IcGrid2x2", "Grid") })
        {
            var item = new MenuItem { Header = label, Tag = layout, Icon = MenuIcon(icon) };
            item.Click += Combine_Click;
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void SelectConvert_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = SelectConvertButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var target in Converter.Targets)
        {
            var item = new MenuItem { Header = target == "MP4" ? "MP4 video" : target };
            item.Click += async (_, _) => await ConvertPicked(target);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private async Task ConvertPicked(string target)
    {
        if (!AnyPicked(out var picked)) return;
        int made = 0, skipped = 0, failed = 0;
        for (int i = 0; i < picked.Count; i++)
        {
            Toast.ShowSticky($"Converting to {target}… {i + 1} of {picked.Count}");
            if (Converter.Kind(picked[i].Path) == target) { skipped++; continue; }
            try
            {
                AddToHistory(await Converter.ConvertAsync(picked[i].Path, target));
                made++;
            }
            catch { failed++; }
        }
        SetCombining(false);
        string message = $"Made {made} {target} file{(made == 1 ? "" : "s")}.";
        if (skipped > 0) message += $" {skipped} already {(skipped == 1 ? "was" : "were")} {target}.";
        if (failed > 0) message += $" {failed} couldn't be converted.";
        Toast.Show(message, failed > 0);
    }

    private void SelectRename_Click(object sender, RoutedEventArgs e)
    {
        if (!AnyPicked(out var picked)) return;
        if (RenameFiles(picked)) SetCombining(false);
    }

    private void SelectDelete_Click(object sender, RoutedEventArgs e)
    {
        if (!AnyPicked(out var picked)) return;
        int answer = AskDialog.Ask(this, $"Move {picked.Count} capture{(picked.Count == 1 ? "" : "s")} to the Recycle Bin?",
            "You can get them back from the Recycle Bin.", "IcTrash", ("Cancel", false), ("Move to the Recycle Bin", true));
        if (answer != 1) return;
        int moved = 0;
        foreach (var item in picked)
        {
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(item.Path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                moved++;
            }
            catch { }
        }
        SetCombining(false);
        RefreshLibrary();
        Toast.Show($"Moved {moved} to the Recycle Bin.");
    }

    // ---- renaming --------------------------------------------------------------------------------

    private void LibraryRename_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item && File.Exists(item.Path)) RenameFiles(new List<LibraryItem> { item });
    }

    private void LibraryCopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        try
        {
            Clipboard.SetText(item.Path);
            Toast.Show("Copied the file's path.");
        }
        catch
        {
            Toast.Show("The clipboard is busy. Try again.", true);
        }
    }

    /// <summary>Shows the rename window and renames on disk, in the history, the search index and pinned pictures.</summary>
    private bool RenameFiles(List<LibraryItem> items)
    {
        var dialog = new RenameWindow(items.Select(i => (i.Path, i.Time)).ToList()) { Owner = this };
        if (dialog.ShowDialog() != true) return false;

        // two steps through temporary names, so swapping names between files works too
        var moves = dialog.Result
            .Select(r => (From: r.Path, To: Path.Combine(Path.GetDirectoryName(r.Path)!, r.NewName)))
            .Where(m => !string.Equals(m.From, m.To, StringComparison.Ordinal)).ToList();
        int done = 0;
        var temps = new List<(string Temp, string From, string To)>();
        try
        {
            foreach (var (from, to) in moves)
            {
                string temp = from + ".renaming";
                File.Move(from, temp);
                temps.Add((temp, from, to));
            }
            foreach (var (temp, from, to) in temps)
            {
                File.Move(temp, to);
                Renamed(from, to);
                done++;
            }
        }
        catch (Exception ex)
        {
            // put back whatever was left half way
            foreach (var (temp, from, _) in temps)
                if (File.Exists(temp)) { try { File.Move(temp, from); } catch { } }
            Toast.Show("Couldn't rename: " + ex.Message, true);
        }
        SaveSettings();
        RefreshLibrary();
        if (done > 0) Toast.Show(done == 1 ? $"Renamed to {Path.GetFileName(moves[0].To)}." : $"Renamed {done} captures.");
        return done > 0;
    }

    private void Renamed(string from, string to)
    {
        foreach (var h in _settings.History.Where(h => string.Equals(h.Path, from, StringComparison.OrdinalIgnoreCase))) h.Path = to;
        foreach (var p in _settings.PinnedImages.Where(p => string.Equals(p.Path, from, StringComparison.OrdinalIgnoreCase))) p.Path = to;
        _search.Renamed(from, to);
        var item = _library.FirstOrDefault(i => string.Equals(i.Path, from, StringComparison.OrdinalIgnoreCase));
        if (item != null) _library[_library.IndexOf(item)] = new LibraryItem { Path = to, Time = item.Time, Caption = item.Caption, Details = item.Details, Thumb = item.Thumb };
    }

    // ---- sorting -----------------------------------------------------------------------------------

    private static readonly (string Key, string Label)[] Sorts = { ("Newest", "Newest first"), ("Oldest", "Oldest first"), ("Name", "Name"), ("Largest", "Largest first") };

    private void LibrarySort_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = LibrarySortButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var (key, label) in Sorts)
        {
            var item = new MenuItem { Header = label, IsChecked = _settings.LibrarySort == key };
            item.Click += (_, _) => { _settings.LibrarySort = key; SaveSettings(); RefreshLibrary(); };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private IEnumerable<HistoryEntry> Sorted(IEnumerable<HistoryEntry> newestFirst) => _settings.LibrarySort switch
    {
        "Oldest" => newestFirst.Reverse(),
        "Name" => newestFirst.OrderBy(h => Path.GetFileName(h.Path), StringComparer.CurrentCultureIgnoreCase),
        "Largest" => newestFirst.OrderByDescending(h => { try { return new FileInfo(h.Path).Length; } catch { return 0L; } }),
        _ => newestFirst,
    };
}
