using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Overskin;

/// <summary>The Library's favourites, tags, and finding duplicate captures.</summary>
public partial class MainWindow
{
    private HistoryEntry? EntryOf(LibraryItem item) =>
        _settings.History.LastOrDefault(h => string.Equals(h.Path, item.Path, StringComparison.OrdinalIgnoreCase));

    private static string TagText(HistoryEntry h) => string.Join("  ", h.Tags.Select(t => "#" + t));

    /// <summary>
    /// A Library search: every word must appear in the name, date, the words read from the
    /// picture, or its tags ("#work" finds only that tag).
    /// </summary>
    private bool MatchesLibrarySearch(HistoryEntry h, string query)
    {
        var plain = new List<string>();
        foreach (var term in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (term.Length > 1 && term[0] == '#')
            {
                if (!h.Tags.Any(t => t.Equals(term[1..], StringComparison.CurrentCultureIgnoreCase))) return false;
                continue;
            }
            if (h.Tags.Any(t => t.Contains(term, StringComparison.CurrentCultureIgnoreCase))) continue;
            if (term.Equals("favourite", StringComparison.CurrentCultureIgnoreCase) && h.Favourite) continue;
            plain.Add(term);
        }
        if (plain.Count == 0) return true;
        string words = string.Join(' ', plain);
        // the words in it, or else what's in the picture
        return _search.Matches(h.Path, Friendly(h.Time), words) || PictureMatches(h.Path, words);
    }

    // ---- searching by what's in the picture -------------------------------------------------------

    private readonly PictureIndex _pictures = new();
    private string _pictureQueryText = "";
    private float[]? _pictureQuery;
    private bool _pictureQueryBusy;

    /// <summary>
    /// Whether a capture shows what the search describes. The search's own fingerprint is worked
    /// out in the background the first time; the Library refreshes when it's ready.
    /// </summary>
    private bool PictureMatches(string path, string words)
    {
        if (!AiModels.Available) return false;
        if (_pictureQueryText != words)
        {
            _pictureQueryText = words;
            _pictureQuery = null;
            if (!_pictureQueryBusy) _ = WorkOutPictureQuery();
            return false;
        }
        return _pictureQuery is { } q && _pictures.Likeness(path, q) >= PictureIndex.Match;
    }

    private async Task WorkOutPictureQuery()
    {
        _pictureQueryBusy = true;
        try
        {
            while (true)
            {
                string words = _pictureQueryText;
                float[]? print = null;
                try { print = await Task.Run(() => PictureIndex.QueryPrint(words)); }
                catch { }
                if (words != _pictureQueryText) continue; // typed more meanwhile
                _pictureQuery = print;
                break;
            }
        }
        finally { _pictureQueryBusy = false; }
        if (LibrarySearch.Text.Trim().Length > 0) RefreshLibrary();
    }

    // ---- favourites ----------------------------------------------------------------------------

    private void LibraryFavourite_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item || EntryOf(item) is not { } entry) return;
        entry.Favourite = !entry.Favourite;
        item.Favourite = entry.Favourite;
        SaveSettings();
        if (_libraryFilter == "Favourites") RefreshLibrary();
    }

    // ---- tags ----------------------------------------------------------------------------------

    /// <summary>Every tag in use, most used first.</summary>
    private List<string> AllTags() => _settings.History.SelectMany(h => h.Tags)
        .GroupBy(t => t, StringComparer.CurrentCultureIgnoreCase)
        .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
        .Select(g => g.First()).ToList();

    /// <summary>The Tags submenu on a capture: tick tags on and off, or make a new one.</summary>
    private void LibraryTags_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menu || ItemOf(menu) is not { } item || EntryOf(item) is not { } entry) return;
        if (!ReferenceEquals(e.OriginalSource, menu)) return;
        menu.Items.Clear();
        foreach (var tag in AllTags())
        {
            var t = tag;
            var mi = new MenuItem { Header = "#" + tag, IsCheckable = true, IsChecked = entry.Tags.Contains(tag, StringComparer.CurrentCultureIgnoreCase), StaysOpenOnClick = true };
            mi.Click += (_, _) => SetTag(item, entry, t, mi.IsChecked);
            menu.Items.Add(mi);
        }
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        var add = new MenuItem { Header = "New tag…" };
        add.Click += (_, _) =>
        {
            if (TextPrompt.Ask(this, "New tag", "Tag this capture as", "") is not { } name) return;
            name = name.Trim().TrimStart('#').Replace(' ', '-');
            if (name.Length > 0) SetTag(item, entry, name, true);
        };
        menu.Items.Add(add);
    }

    private void SetTag(LibraryItem item, HistoryEntry entry, string tag, bool on)
    {
        entry.Tags.RemoveAll(t => t.Equals(tag, StringComparison.CurrentCultureIgnoreCase));
        if (on) entry.Tags.Add(tag);
        item.TagText = TagText(entry);
        SaveSettings();
    }

    /// <summary>The Tags button: shows only the captures with a tag (through the search box).</summary>
    private void LibraryTagFilter_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var tags = AllTags();
        if (tags.Count == 0) menu.Items.Add(new MenuItem { Header = "No tags yet: right-click a capture → Tags", IsEnabled = false });
        foreach (var tag in tags)
        {
            int count = _settings.History.Count(h => h.Tags.Contains(tag, StringComparer.CurrentCultureIgnoreCase));
            var mi = new MenuItem { Header = $"#{tag}   ({count})", IsChecked = LibrarySearch.Text.Trim().Equals("#" + tag, StringComparison.CurrentCultureIgnoreCase) };
            var t = tag;
            mi.Click += (_, _) => LibrarySearch.Text = mi.IsChecked ? "" : "#" + t;
            menu.Items.Add(mi);
        }
        menu.IsOpen = true;
    }

    // ---- duplicates ----------------------------------------------------------------------------

    /// <summary>Path → which group of look-alikes it's in, while the Duplicates view is showing.</summary>
    private Dictionary<string, int> _duplicateGroup = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What two captures are compared by: a rough fingerprint, a small grey copy, and the shape.</summary>
    internal sealed record Fingerprint(ulong Hash, byte[] Small, double Shape);
    private static readonly Dictionary<(string, long), Fingerprint> PrintCache = new();
    private const int SmallSide = 32;

    /// <summary>
    /// Finds captures that look the same (the same screenshot taken twice, or saved again a little
    /// smaller), shows them together, and picks all but the newest of each, ready for the bin.
    /// </summary>
    private void FindDuplicates_Click(object sender, RoutedEventArgs e) => FindDuplicates();

    /// <summary>The Duplicates tab goes once no group has two captures left in it.</summary>
    private void TidyDuplicates()
    {
        if (_duplicateGroup.Count == 0 || LibraryDuplicatesTab.Visibility != Visibility.Visible) return;
        var left = _duplicateGroup.Where(d => File.Exists(d.Key)).GroupBy(d => d.Value).Where(g => g.Count() >= 2).SelectMany(g => g)
            .ToDictionary(d => d.Key, d => d.Value, StringComparer.OrdinalIgnoreCase);
        _duplicateGroup = left;
        if (left.Count > 0) return;
        LibraryDuplicatesTab.Visibility = Visibility.Collapsed;
        if (LibraryDuplicatesTab.IsChecked == true)
            foreach (var r in LibraryDuplicatesTab.Parent is Panel p ? p.Children.OfType<RadioButton>() : Enumerable.Empty<RadioButton>())
                if ((string)r.Tag == "All") { r.IsChecked = true; break; }
    }

    private async void FindDuplicates()
    {
        ShowTab(TabLibraryIndex);
        var paths = _settings.History.Select(h => h.Path).Where(p => File.Exists(p) && !IsVideo(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Toast.ShowSticky($"Looking for duplicates in {paths.Count} captures…");
        var groups = await Task.Run(() => DuplicateGroups(paths));
        Toast.HideNow();
        _duplicateGroup = new(StringComparer.OrdinalIgnoreCase);
        for (int g = 0; g < groups.Count; g++)
            foreach (var p in groups[g]) _duplicateGroup[p] = g;
        if (groups.Count == 0)
        {
            Toast.Show("No duplicates found.");
            return;
        }
        LibraryDuplicatesTab.Visibility = Visibility.Visible;
        LibraryDuplicatesTab.IsChecked = true; // shows them, grouped
        SetCombining(true);
        // keep the newest of each group; pick the others
        DateTime TimeOf(string p) => _settings.History.Last(h => h.Path.Equals(p, StringComparison.OrdinalIgnoreCase)).Time;
        foreach (var item in _library)
        {
            var newest = groups[_duplicateGroup[item.Path]].OrderByDescending(TimeOf).First();
            if (item.Path.Equals(newest, StringComparison.OrdinalIgnoreCase)) continue;
            item.IsSelected = true;
            _pickOrder.Add(item);
        }
        UpdateCombineBar();
        int extra = _library.Count(i => i.IsSelected);
        Toast.Show($"{extra} duplicate{(extra == 1 ? "" : "s")} in {groups.Count} group{(groups.Count == 1 ? "" : "s")}. The older copies are picked: the bin moves them to the Recycle Bin.");
    }

    /// <summary>Groups of two or more captures that look the same.</summary>
    internal static List<List<string>> DuplicateGroups(IReadOnlyList<string> paths)
    {
        var prints = new List<(string Path, Fingerprint Print)>();
        foreach (var p in paths)
        {
            try
            {
                var key = (p, File.GetLastWriteTimeUtc(p).Ticks);
                Fingerprint? print;
                lock (PrintCache) PrintCache.TryGetValue(key, out print);
                if (print == null)
                {
                    print = PrintOf(p);
                    lock (PrintCache) PrintCache[key] = print;
                }
                prints.Add((p, print));
            }
            catch
            {
                // a file that can't be read just isn't compared
            }
        }
        var groups = new List<List<string>>();
        var used = new bool[prints.Count];
        for (int i = 0; i < prints.Count; i++)
        {
            if (used[i]) continue;
            var group = new List<string> { prints[i].Path };
            for (int j = i + 1; j < prints.Count; j++)
                if (!used[j] && Same(prints[i].Print, prints[j].Print))
                {
                    group.Add(prints[j].Path);
                    used[j] = true;
                }
            if (group.Count > 1) groups.Add(group);
        }
        return groups;
    }

    /// <summary>
    /// The same picture: the same shape, close fingerprints, and small grey copies that match
    /// almost exactly (so two different pages of text, alike from afar, aren't mistaken).
    /// </summary>
    internal static bool Same(Fingerprint a, Fingerprint b)
    {
        if (Math.Abs(a.Shape - b.Shape) > 0.03 * a.Shape || Distance(a.Hash, b.Hash) > 8) return false;
        long diff = 0;
        int worst = 0;
        for (int k = 0; k < a.Small.Length; k++)
        {
            int d = Math.Abs(a.Small[k] - b.Small[k]);
            diff += d;
            worst = Math.Max(worst, d);
        }
        return diff / (double)a.Small.Length < 2.5 && worst < 60;
    }

    internal static Fingerprint PrintOf(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        return PrintOf(frame);
    }

    /// <summary>
    /// The fingerprint is the picture shrunk to 9 × 8 grey squares, one bit for each pair of
    /// neighbours (is the left one brighter?); the small copy is 32 × 32 grey.
    /// </summary>
    internal static Fingerprint PrintOf(BitmapSource image)
    {
        byte[] Grey(int w, int h)
        {
            var small = new TransformedBitmap(image, new ScaleTransform(w / (double)image.PixelWidth, h / (double)image.PixelHeight));
            var grey = new FormatConvertedBitmap(small, PixelFormats.Gray8, null, 0);
            var px = new byte[w * h];
            if (grey.PixelWidth != w || grey.PixelHeight != h)
            {
                // rounding gave a pixel more or less: copy what's there
                var got = new byte[grey.PixelWidth * grey.PixelHeight];
                grey.CopyPixels(got, grey.PixelWidth, 0);
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        px[y * w + x] = got[Math.Min(y, grey.PixelHeight - 1) * grey.PixelWidth + Math.Min(x, grey.PixelWidth - 1)];
                return px;
            }
            grey.CopyPixels(px, w, 0);
            return px;
        }
        var nine = Grey(9, 8);
        ulong hash = 0;
        int bit = 0;
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++, bit++)
                if (nine[y * 9 + x] > nine[y * 9 + x + 1]) hash |= 1UL << bit;
        return new Fingerprint(hash, Grey(SmallSide, SmallSide), image.PixelWidth / (double)Math.Max(1, image.PixelHeight));
    }

    private static int Distance(ulong a, ulong b) => System.Numerics.BitOperations.PopCount(a ^ b);
}
