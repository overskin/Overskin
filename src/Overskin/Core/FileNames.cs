using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Overskin;

/// <summary>What a capture was of, for its file name: the app and window, and what kind of file.</summary>
public sealed record NameInfo(string App, string Title)
{
    public static readonly NameInfo None = new("", "");

    [DllImport("user32.dll")] private static extern IntPtr GetTopWindow(IntPtr parent);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint cmd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeMethods.RECT rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int max);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

    /// <summary>The top window (not Overskin's own) at a screen point.</summary>
    public static NameInfo At(int x, int y)
    {
        try
        {
            uint own = (uint)Environment.ProcessId;
            for (var w = GetTopWindow(IntPtr.Zero); w != IntPtr.Zero; w = GetWindow(w, 2 /* GW_HWNDNEXT */))
            {
                if (!IsWindowVisible(w) || !GetWindowRect(w, out var r)) continue;
                if (x < r.Left || x >= r.Right || y < r.Top || y >= r.Bottom) continue;
                if (DwmGetWindowAttribute(w, 14 /* DWMWA_CLOAKED */, out int cloaked, 4) == 0 && cloaked != 0) continue;
                // the see-through layers Medal, Discord, NVIDIA and Game Bar keep over everything aren't what was captured
                if (CaptureOverlay.IsOverlay(w)) continue;
                GetWindowThreadProcessId(w, out uint pid);
                if (pid == own) continue;
                var info = Of(w);
                if (info.App.Length == 0 && info.Title.Length == 0) continue;
                return info;
            }
        }
        catch { }
        return None;
    }

    /// <summary>The window in front.</summary>
    public static NameInfo Front()
    {
        try { return Of(GetForegroundWindow()); } catch { return None; }
    }

    private static NameInfo Of(IntPtr window)
    {
        if (window == IntPtr.Zero) return None;
        var cls = new StringBuilder(64);
        GetClassName(window, cls, cls.Capacity);
        if (cls.ToString() is "Progman" or "WorkerW") return new NameInfo("Desktop", "Desktop");
        var text = new StringBuilder(512);
        GetWindowText(window, text, text.Capacity);
        string title = text.ToString().Trim();
        string app = "";
        GetWindowThreadProcessId(window, out uint pid);
        try
        {
            using var p = Process.GetProcessById((int)pid);
            app = p.ProcessName;
            try
            {
                // "Google Chrome" reads better than "chrome"
                string? described = p.MainModule?.FileVersionInfo.FileDescription;
                if (!string.IsNullOrWhiteSpace(described) && described.Length <= 40) app = described.Trim();
            }
            catch { }
        }
        catch { }
        // apps from the Store all run inside one host; their window title is the better name
        if (app.Equals("Application Frame Host", StringComparison.OrdinalIgnoreCase) || app.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
            app = title;
        if (app is "Explorer" or "explorer" or "Windows Explorer" && title.Length == 0) app = "Desktop";
        return new NameInfo(app, title);
    }
}

/// <summary>
/// File names from a pattern the user can change, such as "{app} {date} {time}". A \ in the
/// pattern makes folders, so "{app}\{date} {time}" keeps each app's captures together.
/// </summary>
public static class FileNames
{
    public const string Default = "Overskin {date} {time}";

    /// <summary>The pattern in use (from the settings).</summary>
    public static string Pattern { get; set; } = Default;

    /// <summary>What the latest capture was of; used for its name.</summary>
    public static NameInfo Context { get; set; } = NameInfo.None;

    /// <summary>The words that can go in a pattern, with what each one means.</summary>
    public static readonly (string Token, string Label, string Meaning)[] Tokens =
    {
        ("{date}", "Date", "2026-11-21"),
        ("{time}", "Time", "18.04.33"),
        ("{app}", "App", "the program, such as Discord"),
        ("{title}", "Window title", "the window's title"),
        ("{type}", "Type", "Screenshot, Video or GIF"),
        ("{n}", "Counter", "001, 002, … (the next free number)"),
        ("{year}", "Year", "2026"),
        ("{month}", "Month", "11"),
        ("{day}", "Day", "21"),
    };

    /// <summary>A free path in <paramref name="folder"/> for a new file, named by the pattern.</summary>
    public static string NewPath(string folder, string extension, string? pattern = null, NameInfo? info = null, DateTime? when = null, bool create = true)
    {
        pattern ??= Pattern;
        if (string.IsNullOrWhiteSpace(pattern)) pattern = Default;
        info ??= Context;
        var now = when ?? DateTime.Now;
        string type = extension.ToLowerInvariant() switch { ".mp4" => "Video", ".gif" => "GIF", _ => "Screenshot" };
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
                .Replace("{app}", Clean(info.App.Length > 0 ? info.App : "Screen"), StringComparison.OrdinalIgnoreCase)
                .Replace("{title}", Clean(info.Title.Length > 0 ? info.Title : info.App.Length > 0 ? info.App : "Screen"), StringComparison.OrdinalIgnoreCase)
                .Replace("{type}", type, StringComparison.OrdinalIgnoreCase)
                .Replace("{n}", n.ToString("000"), StringComparison.OrdinalIgnoreCase);
            // each folder and the name itself made safe
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
            string first = Make(1);
            path = first;
            string stem = first[..^extension.Length];
            for (int i = 2; File.Exists(path); i++) path = $"{stem} ({i}){extension}";
        }
        string? dir = Path.GetDirectoryName(path);
        if (create && !string.IsNullOrEmpty(dir)) try { Directory.CreateDirectory(dir); } catch { }
        return path;
    }

    /// <summary>A name as the pattern would make it now, for showing under the pattern box.</summary>
    public static string Example(string pattern, string extension = ".png")
    {
        string path = NewPath(@"C:\x", extension, pattern, new NameInfo("Discord", "general - My Server"), new DateTime(2026, 11, 21, 18, 4, 33), create: false);
        return path[(@"C:\x".Length + 1)..];
    }

    private static string Clean(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) sb.Append(bad.Contains(c) ? ' ' : c);
        string t = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim().TrimEnd('.').Trim();
        return t.Length > 80 ? t[..80].Trim() : t;
    }
}
