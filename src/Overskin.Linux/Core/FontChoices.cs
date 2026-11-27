using Avalonia.Media;

namespace Overskin.Linux.Core;

/// <summary>
/// The fonts for text on pictures and GIF captions: the ones that come with Overskin (the same
/// files as on Windows), then common ones this computer has.
/// </summary>
public static class FontChoices
{
    public sealed record Choice(string Name, FontFamily Family, FontWeight Weight);

    private static FontFamily Bundled(string family) => new($"avares://overskin/Assets/Fonts#{family}");

    private static List<Choice>? _all;

    public static IReadOnlyList<Choice> All => _all ??= Build();

    private static List<Choice> Build()
    {
        var list = new List<Choice>
        {
            new("Inter", new FontFamily("Inter"), FontWeight.Bold),
            new("Anton", Bundled("Anton"), FontWeight.Normal),
            new("Bebas Neue", Bundled("Bebas Neue"), FontWeight.Normal),
            new("Oswald", Bundled("Oswald"), FontWeight.Bold),
            new("Bangers", Bundled("Bangers"), FontWeight.Normal),
            new("Permanent Marker", Bundled("Permanent Marker"), FontWeight.Normal),
            new("Comic Neue", Bundled("Comic Neue"), FontWeight.Bold),
            new("Roboto", Bundled("Roboto"), FontWeight.Bold),
        };
        // the computer's own, when it has them (names differ a little between Mac and Linux)
        HashSet<string> installed;
        try { installed = new HashSet<string>(FontManager.Current.SystemFonts.Select(f => f.Name), StringComparer.OrdinalIgnoreCase); }
        catch { installed = new(); }
        foreach (var (name, weight) in new[]
                 {
                     ("Helvetica Neue", FontWeight.Bold), ("Helvetica", FontWeight.Bold), ("Arial", FontWeight.Bold), ("Arial Black", FontWeight.Normal),
                     ("Impact", FontWeight.Normal), ("Futura", FontWeight.Bold), ("Georgia", FontWeight.Bold), ("Times New Roman", FontWeight.Bold),
                     ("Comic Sans MS", FontWeight.Bold), ("Menlo", FontWeight.Bold), ("Courier New", FontWeight.Bold),
                     ("DejaVu Sans", FontWeight.Bold), ("Noto Sans", FontWeight.Bold), ("Liberation Sans", FontWeight.Bold), ("Ubuntu", FontWeight.Bold),
                 })
            if (installed.Contains(name)) list.Add(new(name, new FontFamily(name), weight));
        return list;
    }

    /// <summary>The font with this name, or Inter.</summary>
    public static Choice Find(string? name) => All.FirstOrDefault(c => c.Name == name) ?? All[0];

    public static Typeface Face(string? name)
    {
        var c = Find(name);
        return new Typeface(c.Family, FontStyle.Normal, c.Weight);
    }
}
