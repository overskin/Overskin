using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;

namespace Overskin;

/// <summary>What a clickable piece of text in a screenshot does.</summary>
public enum TextActionKind { Link, Email, Phone }

/// <summary>A web address, email address or phone number found in a picture, and where it is.</summary>
public sealed record TextAction(TextActionKind Kind, string Value, Rect Bounds)
{
    /// <summary>What clicking it does, for a tooltip.</summary>
    public string Hint => Kind switch
    {
        TextActionKind.Link => $"Open {Value}",
        TextActionKind.Email => $"Write an email to {Value}",
        _ => $"Copy {Value}",
    };
}

/// <summary>
/// Finds the links, email addresses and phone numbers in a screenshot's words, so they can be
/// clicked: links open in the browser, email addresses start an email, numbers are copied.
/// </summary>
public static partial class TextActions
{
    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"(?<![@\w.])(?:https?://[^\s<>""'()]+|www\.[^\s<>""'()]+|(?:[a-z0-9](?:[a-z0-9\-]*[a-z0-9])?\.)+(?:com|org|net|io|dev|gg|co|uk|app|ai|me|tv|edu|gov|info|xyz|ly|be|de|fr|ca|au|us|nl|it|es|jp|ru|so|sh|to|fm|link|site|page)\b(?:/[^\s<>""'()]*)?)", RegexOptions.IgnoreCase)]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"(?<![\w.])\+?\(?\d[\d\s().\-]{7,}\d(?![\w.])")]
    private static partial Regex PhonePattern();

    /// <summary>Everything clickable in <paramref name="words"/>, which come in lines (<paramref name="lineOf"/> gives each word's line).</summary>
    public static List<TextAction> Find(IReadOnlyList<OcrWord> words, IReadOnlyList<int> lineOf)
    {
        var found = new List<TextAction>();
        if (words.Count == 0 || lineOf.Count != words.Count) return found;
        foreach (var line in Enumerable.Range(0, words.Count).GroupBy(i => lineOf[i]))
        {
            // the line as text, remembering where each word sits in it
            var parts = line.OrderBy(i => words[i].Bounds.Left).ToList();
            var starts = new List<int>();
            var text = new System.Text.StringBuilder();
            foreach (int i in parts)
            {
                if (text.Length > 0) text.Append(' ');
                starts.Add(text.Length);
                text.Append(words[i].Text);
            }
            string s = text.ToString();
            var taken = new bool[s.Length];

            void Add(TextActionKind kind, Match m, string value)
            {
                for (int k = m.Index; k < m.Index + m.Length; k++)
                    if (taken[k]) return;
                for (int k = m.Index; k < m.Index + m.Length; k++) taken[k] = true;
                Rect? box = null;
                for (int w = 0; w < parts.Count; w++)
                {
                    int a = starts[w], b = a + words[parts[w]].Text.Length;
                    if (b <= m.Index || a >= m.Index + m.Length) continue;
                    var r = words[parts[w]].Bounds;
                    box = box is { } x ? Rect.Union(x, r) : r;
                }
                if (box is { } bounds) found.Add(new TextAction(kind, value, bounds));
            }

            foreach (Match m in EmailPattern().Matches(s)) Add(TextActionKind.Email, m, m.Value.TrimEnd('.', ','));
            foreach (Match m in LinkPattern().Matches(s))
            {
                string v = m.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')');
                if (v.Length >= 4) Add(TextActionKind.Link, m, v);
            }
            foreach (Match m in PhonePattern().Matches(s))
            {
                int digits = m.Value.Count(char.IsDigit);
                // phone-sized, and not a date, a time or a plain big number
                if (digits is >= 9 and <= 15 && (m.Value.Contains(' ') || m.Value.Contains('-') || m.Value.Contains('(') || m.Value.StartsWith('+')))
                    Add(TextActionKind.Phone, m, m.Value.Trim());
            }
        }
        return found;
    }

    /// <summary>Does what the text is for: opens the link, starts an email, or copies the number.</summary>
    public static void Run(TextAction action)
    {
        try
        {
            switch (action.Kind)
            {
                case TextActionKind.Link:
                    string url = action.Value.Contains("://") ? action.Value : "https://" + action.Value;
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
                    Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                    break;
                case TextActionKind.Email:
                    Process.Start(new ProcessStartInfo("mailto:" + action.Value) { UseShellExecute = true });
                    break;
                case TextActionKind.Phone:
                    Clipboard.SetText(action.Value);
                    Toast.Show($"Copied {action.Value}");
                    break;
            }
        }
        catch
        {
            Toast.Show("Couldn't open that.", true);
        }
    }
}
