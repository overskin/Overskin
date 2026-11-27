using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Overskin;

/// <summary>Splits words into CLIP's pieces (byte-level BPE), the way it was trained.</summary>
internal static class ClipTokenizer
{
    private const int Start = 49406, End = 49407, Longest = 77;
    private static Dictionary<string, int>? _vocab;
    private static Dictionary<(string, string), int>? _ranks;
    private static readonly object Lock = new();
    private static readonly Dictionary<string, List<string>> Pieces = new();
    private static readonly Regex Split = new(@"<\|startoftext\|>|<\|endoftext\|>|'s|'t|'re|'ve|'m|'ll|'d|[\p{L}]+|[\p{N}]|[^\s\p{L}\p{N}]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly char[] ByteChars = MakeByteChars();

    /// <summary>GPT-2's map from each byte to a printable character.</summary>
    private static char[] MakeByteChars()
    {
        var bs = new List<int>();
        for (int b = '!'; b <= '~'; b++) bs.Add(b);
        for (int b = 0xA1; b <= 0xAC; b++) bs.Add(b);
        for (int b = 0xAE; b <= 0xFF; b++) bs.Add(b);
        var map = new char[256];
        int n = 0;
        for (int b = 0; b < 256; b++)
            map[b] = bs.Contains(b) ? (char)b : (char)(256 + n++);
        return map;
    }

    private static void Load()
    {
        if (_vocab != null) return;
        using var stream = typeof(ClipTokenizer).Assembly.GetManifestResourceStream("Overskin.clip-tokenizer.json")
            ?? throw new InvalidOperationException("This copy of Overskin was built without its AI models.");
        using var doc = JsonDocument.Parse(stream);
        var model = doc.RootElement.GetProperty("model");
        var vocab = new Dictionary<string, int>();
        foreach (var p in model.GetProperty("vocab").EnumerateObject()) vocab[p.Name] = p.Value.GetInt32();
        var ranks = new Dictionary<(string, string), int>();
        int r = 0;
        foreach (var m in model.GetProperty("merges").EnumerateArray())
        {
            string? pair = m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            string[] parts = pair != null ? pair.Split(' ') : m.EnumerateArray().Select(e => e.GetString() ?? "").ToArray();
            if (parts.Length == 2) ranks.TryAdd((parts[0], parts[1]), r++);
        }
        _ranks = ranks;
        _vocab = vocab;
    }

    /// <summary>The ids for <paramref name="text"/>, with CLIP's start and end marks, at most 77.</summary>
    public static List<int> Encode(string text)
    {
        lock (Lock)
        {
            Load();
            text = Regex.Replace(text.Normalize(NormalizationForm.FormC), @"\s+", " ").Trim().ToLowerInvariant();
            var ids = new List<int> { Start };
            foreach (Match m in Split.Matches(text))
            {
                var chars = string.Concat(Encoding.UTF8.GetBytes(m.Value).Select(b => ByteChars[b]));
                foreach (var piece in Bpe(chars))
                    if (_vocab!.TryGetValue(piece, out int id)) ids.Add(id);
                if (ids.Count >= Longest - 1) break;
            }
            if (ids.Count > Longest - 1) ids.RemoveRange(Longest - 1, ids.Count - (Longest - 1));
            ids.Add(End);
            return ids;
        }
    }

    /// <summary>Joins letters into the pieces CLIP knows, best-known pairs first; the last piece ends the word.</summary>
    private static List<string> Bpe(string word)
    {
        if (Pieces.TryGetValue(word, out var known)) return known;
        var parts = word.Select(c => c.ToString()).ToList();
        parts[^1] += "</w>";
        while (parts.Count > 1)
        {
            int best = -1, bestRank = int.MaxValue;
            for (int i = 0; i < parts.Count - 1; i++)
                if (_ranks!.TryGetValue((parts[i], parts[i + 1]), out int rank) && rank < bestRank) { bestRank = rank; best = i; }
            if (best < 0) break;
            string a = parts[best], b = parts[best + 1];
            var merged = new List<string>();
            for (int i = 0; i < parts.Count; i++)
            {
                if (i < parts.Count - 1 && parts[i] == a && parts[i + 1] == b) { merged.Add(a + b); i++; }
                else merged.Add(parts[i]);
            }
            parts = merged;
        }
        Pieces[word] = parts;
        return parts;
    }
}
