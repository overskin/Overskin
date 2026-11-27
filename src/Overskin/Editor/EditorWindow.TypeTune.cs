using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Overskin;

/// <summary>
/// The bar over the picture while the text cursor is in it: the font, size and weight new letters
/// are typed in. They start as worked out from the picture; this is for when that's a little off
/// (a touch too bold, say). Each line keeps its own.
/// </summary>
public partial class EditorWindow
{
    /// <summary>What each line matched before it was changed here, for the reset button.</summary>
    private readonly Dictionary<int, TextStyle?> _matchedStyles = new();

    private int TuneLine => HasTextCaret || HasTextSelection ? _wordLine[_focus.W] : -1;

    private void UpdateTypeBar()
    {
        if (TypeBar == null) return;
        int line = TuneLine;
        TypeBar.Visibility = line >= 0 ? Visibility.Visible : Visibility.Collapsed;
        if (line < 0) return;
        if (_lineStyles.TryGetValue(line, out var s) && s != null)
        {
            TypeFontText.Text = FontLibrary.NameOf(s.Face.FontFamily);
            TypeSizeText.Text = $"{s.Size:0.#} px";
            TypeWeightText.Text = WeightName(s.Face.Weight);
        }
        else
        {
            TypeFontText.Text = "Matched to the picture";
            TypeSizeText.Text = "auto";
            TypeWeightText.Text = "auto";
        }
    }

    private static string WeightName(FontWeight w) =>
        w.ToOpenTypeWeight() switch { <= 350 => "Light", <= 450 => "Regular", <= 550 => "Medium", <= 650 => "Semibold", _ => "Bold" };

    private static readonly (string Name, FontWeight Weight)[] Weights =
        { ("Light", FontWeights.Light), ("Regular", FontWeights.Normal), ("Medium", FontWeights.Medium), ("Semibold", FontWeights.SemiBold), ("Bold", FontWeights.Bold) };

    /// <summary>Changes the current line's style (working it out from the picture first, if need be).</summary>
    private void Retune(Func<TextStyle, TextStyle> change)
    {
        int line = TuneLine;
        if (line < 0) return;
        var s = StyleFor(line);
        if (s == null)
        {
            Toast.Show("Typing needs text on a plain background. This line is on a picture or a gradient.", true);
            return;
        }
        if (!_matchedStyles.ContainsKey(line)) _matchedStyles[line] = s;
        var changed = change(s);
        _lineStyles[line] = changed with { Name = FontLibrary.NameOf(changed.Face.FontFamily) + (changed.Face.Weight == FontWeights.Normal ? "" : " " + WeightName(changed.Face.Weight)) };
        UpdateTypeBar();
        ShowTextHint();
        Focus(); // typing carries on into the picture
        _ = RestyleAsync();
    }

    /// <summary>
    /// Letters already there take the new style too: the selected ones, or else the word the
    /// cursor is in. They're taken out and typed again in the new style, then selected again.
    /// </summary>
    private async Task RestyleAsync()
    {
        if (_typing || _textEditBusy) return;
        if (!HasTextSelection)
        {
            if (!HasTextCaret || SpaceCaret != null) return;
            int w = _focus.W;
            if (_words[w].Text.Length == 0) return;
            _anchor = (w, 0);
            _focus = (w, _words[w].Text.Length);
        }
        var parts = SelectedParts();
        if (parts.Count == 0) return;
        if (parts.Select(p => _wordLine[p.Word]).Distinct().Count() > 1)
        {
            Toast.Show("Restyling works on one line at a time. The new style is used for what you type next.");
            return;
        }
        string text = SelectedText();
        if (string.IsNullOrWhiteSpace(text)) return;
        // a whole word after another: taking it out closes the gap, so the space is typed back too
        var first = Compare(_anchor, _focus) <= 0 ? _anchor : _focus;
        bool wordStart = first.K == 0 && first.W > 0 && _wordLine[first.W - 1] == _wordLine[first.W];
        _typing = true;
        try
        {
            await DeleteTextAsync(back: false);
            if (!HasTextCaret) return;
            if (wordStart && _focus.K == _words[_focus.W].Text.Length && SpaceCaret == null) text = " " + text;
            int line = _wordLine[_focus.W];
            double startX = Edges(_focus.W)[_focus.K];
            foreach (char c in text) await TypeAsync(c);
            // select what was retyped (found by where it starts), so another change applies to it too
            if (HasTextCaret)
            {
                (int W, int K) best = _focus;
                double gap = double.MaxValue;
                for (int i = 0; i < _words.Count; i++)
                {
                    if (_wordLine[i] != line) continue;
                    var e = Edges(i);
                    for (int k = 0; k < e.Length; k++)
                        if (Math.Abs(e[k] - startX) < gap - 0.01 && Compare((i, k), _focus) < 0) { gap = Math.Abs(e[k] - startX); best = (i, k); }
                }
                // from the end of a word, the selection starts at the next one
                if (best.K == _words[best.W].Text.Length && best.W + 1 < _words.Count && _wordLine[best.W + 1] == line) best = (best.W + 1, 0);
                _anchor = best;
                ShowTextSelection();
            }
        }
        finally { _typing = false; }
        ShowTextHint();
    }

    private static Typeface Face(FontFamily family, FontWeight weight) => new(family, FontStyles.Normal, weight, FontStretches.Normal);

    private void TypeFont_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var installed = new HashSet<string>(Fonts.SystemFontFamilies.Select(f => f.Source), StringComparer.OrdinalIgnoreCase);
        string? current = _lineStyles.TryGetValue(TuneLine, out var s) && s != null ? FontLibrary.NameOf(s.Face.FontFamily) : null;
        var choices = CommonFonts.Where(installed.Contains).Select(n => (Name: n, Family: new FontFamily(n)))
            .Concat(FontLibrary.MatchingFonts.Where(f => !installed.Contains(f.Name)).Select(f => (f.Name, Family: FontLibrary.Bundled(f.Name))))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, family) in choices)
        {
            var item = new MenuItem { Header = name, FontFamily = family, IsChecked = name == current };
            item.Click += (_, _) => Retune(st => st with { Face = Face(family, st.Face.Weight) });
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void TypeWeight_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        string? current = _lineStyles.TryGetValue(TuneLine, out var s) && s != null ? WeightName(s.Face.Weight) : null;
        foreach (var (name, weight) in Weights)
        {
            var item = new MenuItem { Header = name, FontWeight = weight, IsChecked = name == current };
            item.Click += (_, _) => Retune(st => st with { Face = Face(st.Face.FontFamily, weight) });
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void TypeSmaller_Click(object sender, RoutedEventArgs e) => Retune(st => st with { Size = Math.Max(4, st.Size - Step(st.Size)) });
    private void TypeBigger_Click(object sender, RoutedEventArgs e) => Retune(st => st with { Size = Math.Min(400, st.Size + Step(st.Size)) });

    /// <summary>Half a pixel for small text, more for big.</summary>
    private static double Step(double size) => size < 20 ? 0.5 : size < 60 ? 1 : 2;

    private void TypeReset_Click(object sender, RoutedEventArgs e)
    {
        int line = TuneLine;
        if (line < 0 || !_matchedStyles.Remove(line, out var matched)) return;
        _lineStyles[line] = matched;
        UpdateTypeBar();
        ShowTextHint();
        Focus();
        _ = RestyleAsync();
    }

    /// <summary>For the self-test: waits for a restyle to finish.</summary>
    internal async Task RestyleDoneForTest()
    {
        for (int i = 0; i < 200 && (_typing || _textEditBusy); i++) await Task.Delay(20);
    }

    internal string TextForTest => string.Join(" ", _words.Select(w => w.Text));

    /// <summary>For the self-test: the style typing on this word's line uses.</summary>
    internal TextStyle? TypingStyleForTest(int word) => _lineStyles.TryGetValue(_wordLine[word], out var s) ? s : null;

    /// <summary>For the self-test: the bar's buttons.</summary>
    internal void TuneForTest(string what)
    {
        switch (what)
        {
            case "bigger": TypeBigger_Click(this, new RoutedEventArgs()); break;
            case "regular": Retune(st => st with { Face = Face(st.Face.FontFamily, FontWeights.Normal) }); break;
            case "georgia": Retune(st => st with { Face = Face(new FontFamily("Georgia"), st.Face.Weight) }); break;
            case "reset": TypeReset_Click(this, new RoutedEventArgs()); break;
        }
    }

    internal bool TypeBarShownForTest => TypeBar.Visibility == Visibility.Visible;
    internal bool CheckerShownForTest => Checker.Visibility == Visibility.Visible;
}
