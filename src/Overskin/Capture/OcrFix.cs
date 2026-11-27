using System.Text;

namespace Overskin;

/// <summary>
/// Fixes the letters and numbers Windows' text reader mixes up, from what's round them: an O in
/// a number is a 0 ("2O26" → "2026"), a 0 in a word is an O ("HELL0" → "HELLO", "w0rld" →
/// "world"). Anything that might really be a code (a key, a username, an address) is left alone.
/// </summary>
internal static class OcrFix
{
    /// <summary>One recognised word, fixed where its neighbours make the mix-up clear.</summary>
    public static string Word(string word)
    {
        if (word.Length < 2 || word.Contains('@') || word.Contains("://") || word.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            return word;
        int digits = 0, upper = 0, lower = 0;
        foreach (char c in word)
        {
            if (char.IsDigit(c)) digits++;
            else if (char.IsUpper(c)) upper++;
            else if (char.IsLower(c)) lower++;
        }
        if (digits == 0 && upper + lower == 0) return word;
        // a number with an O in it, commas and points included ($1,2O0.5O): every O is a 0
        if (digits >= 2 && word.All(c => char.IsDigit(c) || c is 'O' or 'o' or ',' or '.' or '$' or '€' or '£' or '%' or '+' or '-' or ':'))
            return new string(word.Select(c => c is 'O' or 'o' ? '0' : c).ToArray());
        // capitals mixed with numbers that look like no letter is a code (AB0C-1D2E): leave it be
        if (upper >= 2 && lower == 0 && word.Any(c => char.IsDigit(c) && c is not ('0' or '1' or '5' or '8'))) return word;

        // each run of letters and numbers on its own: "C0VID-19" is "C0VID" and "19"
        var sb = new StringBuilder(word.Length);
        int i = 0;
        while (i < word.Length)
        {
            if (!char.IsLetterOrDigit(word[i]) && word[i] != '|')
            {
                sb.Append(word[i++]);
                continue;
            }
            int start = i;
            while (i < word.Length && (char.IsLetterOrDigit(word[i]) || word[i] == '|')) i++;
            sb.Append(Run(word.Substring(start, i - start)));
        }
        return sb.ToString();
    }

    private static bool LooksLikeDigit(char c) => c is 'O' or 'o' or 'l' or 'I' or '|';

    private static string Run(string run)
    {
        if (run.Length < 2) return run;
        int digits = run.Count(char.IsDigit), letters = run.Count(char.IsLetter);
        var chars = run.ToCharArray();

        // mostly a number: the odd letter that looks like a digit becomes one
        if (digits >= 1 && run.All(c => char.IsDigit(c) || LooksLikeDigit(c)) && digits >= run.Length - digits)
        {
            for (int k = 0; k < chars.Length; k++)
                if (!char.IsDigit(chars[k]))
                    chars[k] = chars[k] is 'O' or 'o' ? '0' : '1';
            return new string(chars);
        }

        // mostly a word: a digit with letters beside it becomes the letter it looks like. A
        // number at the start stays ("1st", "10am", "3D"), and so does a word that's half numbers,
        // unless those are all 0s between capitals (B00K, G00GLE).
        bool zerosInCaps = letters >= 2 && !run.Any(char.IsLower) && char.IsLetter(run[0]) && char.IsLetter(run[^1])
                           && run.All(c => char.IsUpper(c) || c == '0');
        if (letters < 2 || (letters <= digits && !zerosInCaps)) return run;
        int lead = 0;
        while (lead < chars.Length && char.IsDigit(chars[lead])) lead++;
        for (int k = lead; k < chars.Length; k++)
        {
            char c = run[k];
            if (!char.IsDigit(c) && c != '|') continue;
            bool before = k > 0 && char.IsLetter(run[k - 1]), after = k + 1 < run.Length && char.IsLetter(run[k + 1]);
            bool caps = (k == 0 || !char.IsLetter(run[k - 1]) || char.IsUpper(run[k - 1]))
                        && (k + 1 >= run.Length || !char.IsLetter(run[k + 1]) || char.IsUpper(run[k + 1]))
                        && run.Any(char.IsUpper) && !run.Any(char.IsLower);
            char? to = c switch
            {
                '0' when before || after => caps ? 'O' : 'o',
                '1' or '|' when before && after => caps ? 'I' : 'l',
                '1' or '|' when before && !after && caps => 'I',
                '5' when before && after => caps ? 'S' : 's',
                '8' when before && after && caps => 'B',
                _ => null,
            };
            if (to is { } t) chars[k] = t;
        }
        return new string(chars);
    }
}
