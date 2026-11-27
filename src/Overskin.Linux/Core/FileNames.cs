using System.Text;

namespace Overskin.Linux.Core;

/// <summary>
/// How saved captures are named, the same as on Windows: a pattern of words such as
/// "{app} {date} {time}". A / in the pattern makes folders, so "{app}/{date} {time}" keeps each
/// app's captures together. Older patterns written as "{0:yyyy-MM-dd}" still work.
/// </summary>
public static class FileNames
{
    public const string Default = "Overskin {date} {time}";

    /// <summary>What the latest capture was of (the app and its window), for {app} and {title}.</summary>
    public static (string App, string Title) Context { get; set; } = ("", "");

    /// <summary>The words that can go in a pattern, with what each one means.</summary>
    public static readonly (string Token, string Meaning)[] Tokens =
    {
        ("{date}", "2026-11-21"), ("{time}", "18.04.33"), ("{app}", "the program, such as Safari"), ("{title}", "the window's title"),
        ("{type}", "Screenshot, Video or GIF"), ("{n}", "001, 002, … (the next free number)"), ("{year}", "2026"), ("{month}", "11"), ("{day}", "21"),
    };

    /// <summary>A free path in <paramref name="folder"/> for a new file, named by the pattern.</summary>
    public static string NewPath(string folder, string extension, string? pattern, (string App, string Title)? info = null, DateTime? when = null, bool create = true)
    {
        if (string.IsNullOrWhiteSpace(pattern)) pattern = Default;
        var (app, title) = info ?? Context;
        var now = when ?? DateTime.Now;
        // an older pattern for string.Format ("Overskin_{0:yyyy-MM-dd_HH-mm-ss}")
        if (pattern.Contains("{0"))
        {
            try { pattern = string.Format(pattern, now).Replace("{", "(").Replace("}", ")"); }
            catch (FormatException) { pattern = Default; }
        }
        string type = extension.ToLowerInvariant() switch { ".mp4" or ".mov" => "Video", ".gif" => "GIF", _ => "Screenshot" };
        bool counted = pattern.Contains("{n}", StringComparison.OrdinalIgnoreCase);

        string Make(int n)
        {
            string s = pattern
                .Replace("{date}", now.ToString("yyyy-MM-dd"), StringComparison.OrdinalIgnoreCase)
                .Replace("{time}", now.ToString("HH.mm.ss"), StringComparison.OrdinalIgnoreCase)
                .Replace("{year}", now.ToString("yyyy"), StringComparison.OrdinalIgnoreCase)
                .Replace("{month}", now.ToString("MM"), StringComparison.OrdinalIgnoreCase)
                .Replace("{day}", now.ToString("dd"), StringComparison.OrdinalIgnoreCase)
                .Replace("{hour}", now.ToString("HH"), StringComparison.OrdinalIgnoreCase)
                .Replace("{minute}", now.ToString("mm"), StringComparison.OrdinalIgnoreCase)
                .Replace("{second}", now.ToString("ss"), StringComparison.OrdinalIgnoreCase)
                .Replace("{app}", Clean(app.Length > 0 ? app : "Screen"), StringComparison.OrdinalIgnoreCase)
                .Replace("{title}", Clean(title.Length > 0 ? title : app.Length > 0 ? app : "Screen"), StringComparison.OrdinalIgnoreCase)
                .Replace("{type}", type, StringComparison.OrdinalIgnoreCase)
                .Replace("{n}", n.ToString("000"), StringComparison.OrdinalIgnoreCase);
            var parts = s.Split('\\', '/').Select(Clean).Where(p => p.Length > 0).ToList();
            if (parts.Count == 0) parts.Add("Overskin " + now.ToString("yyyy-MM-dd HH.mm.ss"));
            return Path.Combine(new[] { folder }.Concat(parts).ToArray()) + extension;
        }

        string path;
        if (counted)
        {
            int n = 1;
            while (File.Exists(path = Make(n)) && n < 100000) n++;
        }
        else
        {
            path = Make(1);
            string stem = path[..^extension.Length];
            for (int i = 2; File.Exists(path); i++) path = $"{stem} ({i}){extension}";
        }
        string? dir = Path.GetDirectoryName(path);
        if (create && !string.IsNullOrEmpty(dir)) try { Directory.CreateDirectory(dir); } catch { }
        return path;
    }

    /// <summary>A name as the pattern would make it, for showing under the pattern box.</summary>
    public static string Example(string pattern, string extension = ".png")
    {
        var root = Path.Combine(Path.GetTempPath(), "x");
        var path = NewPath(root, extension, pattern, ("Safari", "Overskin - Screenshots, done right"), new DateTime(2026, 11, 21, 18, 4, 33), create: false);
        return Path.GetRelativePath(root, path);
    }

    private static string Clean(string s)
    {
        var bad = Path.GetInvalidFileNameChars().Concat(new[] { ':', '*', '?', '"', '<', '>', '|' }).ToHashSet();
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) sb.Append(bad.Contains(c) ? ' ' : c);
        string t = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim().TrimEnd('.').Trim();
        return t.Length > 80 ? t[..80].Trim() : t;
    }
}
