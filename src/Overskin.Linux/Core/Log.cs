namespace Overskin.Linux.Core;

/// <summary>
/// What Overskin did, for bug reports: ~/.cache/overskin/overskin.log (the last few hundred lines).
/// Holds no pictures and nothing typed, only steps and errors.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();

    public static string FilePath
    {
        get
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            var root = !string.IsNullOrEmpty(xdg) ? xdg
                : OperatingSystem.IsLinux() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache")
                : Path.GetTempPath();
            return Path.Combine(root, "overskin", "overskin.log");
        }
    }

    public static void Write(string line)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 256 * 1024)
                {
                    var keep = File.ReadAllLines(FilePath).TakeLast(400).ToArray();
                    File.WriteAllLines(FilePath, keep);
                }
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {line}\n");
            }
        }
        catch { }
    }

    /// <summary>The desktop details that matter for capture problems.</summary>
    public static string Environment_()
    {
        string Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : "-";
        return $"session {Env("XDG_SESSION_TYPE")}, desktop {Env("XDG_CURRENT_DESKTOP")}, " +
               $"WAYLAND_DISPLAY {Env("WAYLAND_DISPLAY")}, DISPLAY {Env("DISPLAY")}";
    }

    public static string Tail(int lines = 80)
    {
        try { return File.Exists(FilePath) ? string.Join("\n", File.ReadAllLines(FilePath).TakeLast(lines)) : ""; }
        catch { return ""; }
    }
}
