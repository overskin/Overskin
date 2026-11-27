using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Overskin;

/// <summary>
/// Search everything (Ctrl+K), the search box on the Settings page, and the ring menu.
/// </summary>
public partial class MainWindow
{
    // ===================================================================================
    //  search everything
    // ===================================================================================

    private void OpenSearch() => CommandPalette.Open(PaletteItems(), this);

    /// <summary>Every action, every page and every setting, for the search.</summary>
    internal List<PaletteItem> PaletteItems()
    {
        var keys = new Dictionary<string, string>
        {
            ["region"] = _settings.HotkeyRegion, ["window"] = _settings.HotkeyWindow, ["screen"] = _settings.HotkeyScreen,
            ["gif"] = _settings.HotkeyGif, ["video"] = _settings.HotkeyVideo, ["snip"] = _settings.HotkeySnip,
            ["text"] = _settings.HotkeyText, ["colour"] = _settings.HotkeyColour, ["recent"] = _settings.HotkeyRecent,
            ["pinclipboard"] = _settings.HotkeyPinClipboard, ["pinwindow"] = _settings.HotkeyPin, ["ring"] = _settings.HotkeyRing,
        };
        var words = new Dictionary<string, string>
        {
            ["region"] = "screenshot area capture snip", ["screen"] = "screenshot whole monitor", ["window"] = "screenshot app",
            ["scrolling"] = "long page web stitch", ["gif"] = "animation record", ["video"] = "record mp4 screen recording",
            ["zoomvideo"] = "record mp4", ["text"] = "ocr copy words read qr barcode", ["colour"] = "color picker hex eyedropper",
            ["ruler"] = "measure pixels distance", ["recent"] = "history last", ["library"] = "captures gallery",
            ["combinegrid"] = "grid", ["compare"] = "difference wipe", ["ring"] = "radial wheel quick capture",
        };
        var items = new List<PaletteItem>();
        foreach (var (group, _, commands) in Commands())
            foreach (var c in commands.Where(c => c.Id != "search"))
                items.Add(new PaletteItem(c.Header, group, c.Icon, keys.GetValueOrDefault(c.Id), c.Run, words.GetValueOrDefault(c.Id, "")));
        items.Add(new PaletteItem("Capture page", "Go to", "IcCamera", null, () => ShowTab(TabCaptureIndex)));
        items.Add(new PaletteItem("Recording settings", "Go to", "IcCircleDot", null, () =>
        {
            ShowTab(TabCaptureIndex);
            CaptureRecordTab.IsChecked = true;
        }, "video gif"));
        items.Add(new PaletteItem("Pins page", "Go to", "IcPin", null, () => ShowTab(TabPinsIndex)));
        foreach (var (row, title, heading) in SettingRows())
        {
            var target = row;
            items.Add(new PaletteItem(title, "Setting · " + heading, "IcSettings", null, () => GoToSetting(target), ToolTipText(row)));
        }
        return items;
    }

    /// <summary>
    /// Every labelled setting row on the Capture and Settings pages: the row, its label, and the
    /// heading of the card it's in.
    /// </summary>
    private List<(FrameworkElement Row, string Title, string Heading)> SettingRows()
    {
        var label = (Style)FindResource("Label");
        var heading = (Style)FindResource("Heading");
        var card = (Style)FindResource("Card");
        var found = new List<(FrameworkElement, string, string)>();
        var seen = new HashSet<string>();
        foreach (var page in new FrameworkElement[] { PageCapture, PageSettings })
            foreach (var block in Logical<TextBlock>(page))
            {
                if (block.Style != label || block.Parent is not FrameworkElement row || string.IsNullOrWhiteSpace(block.Text)) continue;
                var box = Ancestor<Border>(row, b => b.Style == card);
                string head = box == null ? "" : Logical<TextBlock>(box).FirstOrDefault(t => t.Style == heading)?.Text ?? "";
                if (seen.Add(head + "|" + block.Text)) found.Add((row, block.Text, head));
            }
        return found;
    }

    private static string ToolTipText(FrameworkElement e) => e.ToolTip as string ?? "";

    /// <summary>Everything of type <typeparamref name="T"/> below <paramref name="root"/>, folded away or not.</summary>
    private static IEnumerable<T> Logical<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T t) yield return t;
            foreach (var deeper in Logical<T>(child)) yield return deeper;
        }
    }

    private static T? Ancestor<T>(DependencyObject? e, Func<T, bool> match) where T : DependencyObject
    {
        for (var p = e; p != null; p = LogicalTreeHelper.GetParent(p) ?? (p is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(p) : null))
            if (p is T t && match(t)) return t;
        return null;
    }

    private static bool IsInside(DependencyObject e, DependencyObject container) =>
        Ancestor<DependencyObject>(e, d => ReferenceEquals(d, container)) != null;

    /// <summary>Shows the setting: its page, its card opened, scrolled to, and briefly lit up.</summary>
    private void GoToSetting(FrameworkElement row)
    {
        if (IsInside(row, PageSettings)) ShowTab(TabSettingsIndex);
        else
        {
            ShowTab(TabCaptureIndex);
            if (IsInside(row, CaptureRecord)) CaptureRecordTab.IsChecked = true;
            else CaptureShotsTab.IsChecked = true;
        }
        SettingsFilter.Text = "";
        foreach (var fold in _folds)
            if (IsInside(row, fold.Card)) fold.Open();
        Dispatcher.BeginInvoke(() =>
        {
            row.BringIntoView(new Rect(0, -60, row.ActualWidth, row.ActualHeight + 120));
            Flash(row);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>A soft accent glow behind a row that fades away.</summary>
    private void Flash(FrameworkElement row)
    {
        if (row is not Panel panel) return;
        var old = panel.Background;
        var accent = ((SolidColorBrush)FindResource("Accent")).Color;
        var glow = new SolidColorBrush(Color.FromArgb(90, accent.R, accent.G, accent.B));
        panel.Background = glow;
        var fade = new ColorAnimation(Color.FromArgb(0, accent.R, accent.G, accent.B), TimeSpan.FromMilliseconds(1600))
        {
            BeginTime = TimeSpan.FromMilliseconds(500),
        };
        fade.Completed += (_, _) => panel.Background = old;
        glow.BeginAnimation(SolidColorBrush.ColorProperty, fade);
    }

    // ===================================================================================
    //  search box on the Settings page
    // ===================================================================================

    private readonly Dictionary<UIElement, Visibility> _hiddenBySearch = new();

    private void SettingsFilter_Changed(object sender, TextChangedEventArgs e)
    {
        if (SettingsFilterHint == null) return;
        string q = SettingsFilter.Text.Trim();
        SettingsFilterHint.Visibility = SettingsFilter.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        FilterSettings(q);
    }

    /// <summary>
    /// Leaves only the settings whose name, description or choices match: rows that don't are
    /// hidden, and cards with nothing left are hidden. A card whose heading matches shows whole.
    /// </summary>
    internal int FilterSettings(string query)
    {
        // put back everything an earlier search hid
        foreach (var (element, was) in _hiddenBySearch) element.Visibility = was;
        _hiddenBySearch.Clear();
        int shown = 0;
        var cardStyle = (Style)FindResource("Card");
        var heading = (Style)FindResource("Heading");
        var cards = SettingsCards.Children.OfType<Border>().Where(b => b.Style == cardStyle).ToList();
        if (query.Length == 0)
        {
            SettingsNoMatch.Visibility = Visibility.Collapsed;
            return cards.Count;
        }
        void Hide(UIElement e)
        {
            if (e.Visibility == Visibility.Collapsed) return;
            _hiddenBySearch[e] = e.Visibility;
            e.Visibility = Visibility.Collapsed;
        }
        var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool Matches(DependencyObject e)
        {
            var text = string.Join(" ", Words(e)).ToLowerInvariant();
            return words.All(text.Contains);
        }
        foreach (var card in cards)
        {
            if (card.Child is not StackPanel panel) continue;
            var head = Logical<TextBlock>(card).FirstOrDefault(t => t.Style == heading);
            if (head != null && Matches(head)) { shown++; continue; }
            int keep = 0;
            // the rows are the card's own children (and those of a group panel inside it)
            foreach (var row in Rows(panel))
            {
                if (row is Border { Style: var s } && s == (Style)FindResource("Rule")) { Hide(row); continue; }
                if (row is StackPanel { Orientation: Orientation.Horizontal } && !Logical<FrameworkElement>(row).Any(x => x is ButtonBase or TextBox)) { Hide(row); continue; } // group labels
                if (Matches(row)) keep++;
                else Hide(row);
            }
            if (keep == 0) Hide(card);
            else shown++;
        }
        SettingsNoMatch.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
        return shown;

        IEnumerable<UIElement> Rows(StackPanel panel)
        {
            foreach (var child in panel.Children.OfType<UIElement>().Skip(1))
            {
                // a plain panel of rows (not a row of its own) is looked into
                if (child is StackPanel { Orientation: Orientation.Vertical } inner && inner.Children.OfType<Grid>().Any())
                {
                    foreach (var r in Rows(inner)) yield return r;
                    continue;
                }
                yield return child;
            }
        }
    }

    /// <summary>All the words a part of the page shows or explains: labels, buttons, choices and tooltips.</summary>
    private static IEnumerable<string> Words(DependencyObject root)
    {
        if (root is FrameworkElement { ToolTip: string tip }) yield return tip;
        if (root is TextBlock t) yield return t.Text;
        if (root is ContentControl { Content: string s }) yield return s;
        foreach (var e in Logical<FrameworkElement>(root))
        {
            if (e.ToolTip is string tt) yield return tt;
            if (e is TextBlock tb) yield return tb.Text;
            else if (e is ContentControl { Content: string c }) yield return c;
        }
    }

    // ===================================================================================
    //  ring menu
    // ===================================================================================

    private void OpenRing() => RingMenu.Open(new RingItem[]
    {
        new("Region", "IcSquareDashed", () => CaptureRegion()),
        new("Window", "IcAppWindow", () => CaptureWindowPick()),
        new("Full screen", "IcMonitor", () => CaptureFullScreen()),
        new("Scrolling page", "IcArrowUpDown", CaptureScrolling),
        new("Record a video", "IcCircleDot", () => ToggleVideo(false)),
        new("Record a GIF", "IcFilm", ToggleGifRecording),
        new("Text from screen", "IcScanText", TextFromScreen),
        new("Snip and pin", "IcScissors", SnipAndPin),
    });
}
