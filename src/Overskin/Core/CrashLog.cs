using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace Overskin;

/// <summary>
/// Writes what went wrong to %LocalAppData%\Overskin\logs so a bug report can include it.
/// Only the error and versions are recorded: no file names, window titles or screen contents.
/// </summary>
public static class CrashLog
{
    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Overskin", "logs");

    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";

    /// <summary>
    /// A line in logs\capture.txt about what a capture did (sizes, steps and timings only), so a
    /// capture that does nothing can be traced. Kept under 64 KB.
    /// </summary>
    public static void Note(string line)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            string path = Path.Combine(Folder, "capture.txt");
            if (File.Exists(path) && new FileInfo(path).Length > 64 * 1024) File.Delete(path);
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {line}" + Environment.NewLine);
        }
        catch { /* only a diagnostic */ }
    }

    /// <summary>Returns the log's path, or null when it couldn't be written.</summary>
    public static string? Write(Exception error, string where)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            // keep the folder small: the 20 newest logs are plenty
            foreach (var old in new DirectoryInfo(Folder).GetFiles("crash-*.txt")
                         .OrderByDescending(f => f.CreationTimeUtc).Skip(19))
                old.Delete();

            string path = Path.Combine(Folder, $"crash-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt");
            File.WriteAllText(path,
                $"Overskin {Version}\r\n" +
                $"Windows {Environment.OSVersion.Version}  ·  .NET {Environment.Version}  ·  {(Environment.Is64BitProcess ? "x64" : "x86")}\r\n" +
                $"When: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n" +
                $"Where: {where}\r\n\r\n" +
                Scrub(error.ToString()) + "\r\n");
            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Stack traces can contain the user's profile path (and with it their name); hide it.</summary>
    private static string Scrub(string text)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return profile.Length > 3 ? text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase) : text;
    }

    public static void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Folder}\"") { UseShellExecute = true });
        }
        catch
        {
        }
    }
}
