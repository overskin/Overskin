using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Overskin;

/// <summary>
/// The fonts offered for captions and text: free fonts that come with Overskin (open licences,
/// see THIRD-PARTY-NOTICES.md), a few good ones Windows has, and any font file the user adds.
/// Added fonts are kept in %AppData%\Overskin\Fonts so they're there next time.
/// </summary>
public static class FontLibrary
{
    public sealed record Choice(string Name, FontFamily Family, FontWeight Weight, FontStretch Stretch)
    {
        public Typeface Face => new(Family, FontStyles.Normal, Weight, Stretch);
    }

    /// <summary>The paid font the white-bar caption memes use. Used when it's installed.</summary>
    public const string Futura = "Futura Condensed ExtraBold";

    private static List<Choice>? _all;

    public static string AddedFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Overskin", "Fonts");

    public static IReadOnlyList<Choice> All => _all ??= Build();

    private static FontFamily Bundled(string family) => new(new Uri("pack://application:,,,/"), "./Fonts/#" + family);

    private static List<Choice> Build()
    {
        var list = new List<Choice>();
        var futura = FindFutura();
        if (futura != null) list.Add(futura);
        list.Add(new("Anton", Bundled("Anton"), FontWeights.Normal, FontStretches.Normal));
        list.Add(new("Impact", new FontFamily("Impact"), FontWeights.Normal, FontStretches.Normal));
        list.Add(new("Bebas Neue", Bundled("Bebas Neue"), FontWeights.Normal, FontStretches.Normal));
        list.Add(new("Oswald", Bundled("Oswald"), FontWeights.Bold, FontStretches.Normal));
        list.Add(new("Bangers", Bundled("Bangers"), FontWeights.Normal, FontStretches.Normal));
        list.Add(new("Permanent Marker", Bundled("Permanent Marker"), FontWeights.Normal, FontStretches.Normal));
        list.Add(new("Comic Neue", Bundled("Comic Neue"), FontWeights.Bold, FontStretches.Normal));
        var installed = new HashSet<string>(Fonts.SystemFontFamilies.Select(f => f.Source), StringComparer.OrdinalIgnoreCase);
        foreach (var (name, weight, stretch) in new[]
                 {
                     ("Segoe UI", FontWeights.Bold, FontStretches.Normal),
                     ("Arial Black", FontWeights.Normal, FontStretches.Normal),
                     ("Bahnschrift", FontWeights.Bold, FontStretches.Condensed),
                     ("Arial", FontWeights.Bold, FontStretches.Normal),
                     ("Georgia", FontWeights.Bold, FontStretches.Normal),
                     ("Times New Roman", FontWeights.Bold, FontStretches.Normal),
                     ("Comic Sans MS", FontWeights.Bold, FontStretches.Normal),
                     ("Consolas", FontWeights.Bold, FontStretches.Normal),
                 })
            if (installed.Contains(name)) list.Add(new(name, new FontFamily(name), weight, stretch));
        try
        {
            if (Directory.Exists(AddedFolder))
                foreach (var file in Directory.GetFiles(AddedFolder))
                    foreach (var c in Load(file)) if (!list.Any(x => x.Name == c.Name)) list.Add(c);
        }
        catch { }
        return list;
    }

    private static Choice? FindFutura()
    {
        foreach (var name in new[] { Futura, "Futura Condensed Extra Bold", "Futura Extra Black Condensed", "Futura Std Condensed ExtraBold" })
        {
            var face = new Typeface(new FontFamily(name), FontStyles.Normal, FontWeights.ExtraBold, FontStretches.Condensed);
            if (face.TryGetGlyphTypeface(out var glyph) && glyph.FamilyNames.Values.Any(n => n.StartsWith("Futura", StringComparison.OrdinalIgnoreCase)))
                return new(Futura, new FontFamily(name), FontWeights.ExtraBold, FontStretches.Condensed);
        }
        return null;
    }

    /// <summary>The families in a font file, ready to use.</summary>
    private static IEnumerable<Choice> Load(string file)
    {
        string ext = Path.GetExtension(file).ToLowerInvariant();
        if (ext is not (".ttf" or ".otf" or ".ttc")) return Array.Empty<Choice>();
        var folder = new Uri(Path.GetDirectoryName(Path.GetFullPath(file))! + Path.DirectorySeparatorChar);
        return Fonts.GetTypefaces(new Uri(folder, Path.GetFileName(file)))
            .GroupBy(t => t.FontFamily.Source)
            .Select(g =>
            {
                var t = g.OrderBy(x => Math.Abs(x.Weight.ToOpenTypeWeight() - 700)).First();
                string name = t.FontFamily.FamilyNames.Values.FirstOrDefault() ?? Path.GetFileNameWithoutExtension(file);
                return new Choice(name, t.FontFamily, t.Weight, t.Stretch);
            })
            .ToList();
    }

    /// <summary>The font with this name, or <paramref name="fallback"/>'s, or the first.</summary>
    public static Choice Find(string? name, string fallback = "Anton") =>
        All.FirstOrDefault(c => c.Name.Equals(name ?? "", StringComparison.OrdinalIgnoreCase))
        ?? All.FirstOrDefault(c => c.Name == fallback)
        ?? All[0];

    /// <summary>The white-bar caption font: Futura when it's installed, else Anton, the nearest free one.</summary>
    public static string CaptionDefault => All.Any(c => c.Name == Futura) ? Futura : "Anton";

    /// <summary>Copies a font file into Overskin's own font folder and adds it. Returns its name, or null.</summary>
    public static string? Add(string file)
    {
        try
        {
            Directory.CreateDirectory(AddedFolder);
            string to = Path.Combine(AddedFolder, Path.GetFileName(file));
            if (!File.Exists(to)) File.Copy(file, to);
            var found = Load(to).ToList();
            if (found.Count == 0) return null;
            foreach (var c in found)
                if (!All.Any(x => x.Name == c.Name)) _all!.Add(c);
            return found[0].Name;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Opens a menu under <paramref name="anchor"/> with every font, each shown in its own look,
    /// then "Add a font…". <paramref name="picked"/> gets the chosen name (a new file is added first).
    /// </summary>
    public static void ShowMenu(FrameworkElement anchor, string current, Action<string> picked)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = anchor,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            MinWidth = anchor.ActualWidth,
            MaxHeight = 420,
        };
        string now = Find(current).Name;
        foreach (var c in All)
        {
            var item = new MenuItem
            {
                Header = new TextBlock { Text = c.Name, FontFamily = c.Family, FontWeight = c.Weight, FontStretch = c.Stretch, FontSize = 14 },
                IsChecked = c.Name == now,
            };
            string name = c.Name;
            item.Click += (_, _) => picked(name);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var add = new MenuItem { Header = "Add a font…" };
        add.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Add a font", Filter = "Fonts|*.ttf;*.otf;*.ttc" };
            if (dialog.ShowDialog(Window.GetWindow(anchor)) != true) return;
            if (Add(dialog.FileName) is { } added) picked(added);
            else Toast.Show("That file isn't a font Overskin can use.", true);
        };
        menu.Items.Add(add);
        menu.IsOpen = true;
    }

    /// <summary>Shows a font's name in that font, for the button that opens <see cref="ShowMenu"/>.</summary>
    public static void ShowName(TextBlock text, string name)
    {
        var c = Find(name);
        text.Text = c.Name;
        text.FontFamily = c.Family;
        text.FontWeight = c.Weight;
        text.FontStretch = c.Stretch;
    }
}
