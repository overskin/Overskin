using System.Text.Json;

namespace Overskin.Linux.Core;

/// <summary>Kept in ~/.config/overskin/settings.json (or $XDG_CONFIG_HOME).</summary>
public sealed class Settings
{
    public string SaveFolder { get; set; } = DefaultSaveFolder();
    public bool SaveAutomatically { get; set; } = true;
    public bool CopyToClipboard { get; set; } = true;
    /// <summary>"editor", "notice" or "nothing".</summary>
    public string AfterCapture { get; set; } = "editor";
    public string FileNamePattern { get; set; } = "Overskin_{0:yyyy-MM-dd_HH-mm-ss}";
    public bool ShowWindowOnStart { get; set; } = true;
    public string PenColour { get; set; } = "#EF4444";
    public double PenWidth { get; set; } = 4;
    /// <summary>What auto-hide looks for.</summary>
    public Overskin.Shared.PrivateKinds AutoHideKinds { get; set; } = Overskin.Shared.PrivateKinds.Default;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string ConfigFolder
    {
        get
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var root = !string.IsNullOrEmpty(xdg) ? xdg
                : OperatingSystem.IsLinux() ? Path.Combine(Home, ".config")
                : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(root, "overskin");
        }
    }

    public static string FilePath { get; set; } = Path.Combine(ConfigFolder, "settings.json");

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { /* a broken file just means defaults */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { }
    }

    public string NewFilePath(string extension = ".png")
    {
        Directory.CreateDirectory(SaveFolder);
        string name;
        try { name = string.Format(FileNamePattern, DateTime.Now); }
        catch (FormatException) { name = $"Overskin_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}"; }
        foreach (var bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');

        var path = Path.Combine(SaveFolder, name + extension);
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(SaveFolder, $"{name} ({i}){extension}");
        return path;
    }

    /// <summary>The XDG Pictures folder (it's translated on non-English systems), then ~/Pictures.</summary>
    public static string DefaultSaveFolder()
    {
        var pictures = Path.Combine(Home, "Pictures");
        try
        {
            var dirs = Path.Combine(ConfigFolderRoot(), "user-dirs.dirs");
            if (File.Exists(dirs))
            {
                foreach (var line in File.ReadAllLines(dirs))
                {
                    if (!line.StartsWith("XDG_PICTURES_DIR=")) continue;
                    var value = line["XDG_PICTURES_DIR=".Length..].Trim().Trim('"').Replace("$HOME", Home);
                    if (value.Length > 0) pictures = value;
                }
            }
        }
        catch { }
        if (!OperatingSystem.IsLinux()) pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        return Path.Combine(pictures, "Overskin");
    }

    private static string ConfigFolderRoot()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return !string.IsNullOrEmpty(xdg) ? xdg : Path.Combine(Home, ".config");
    }
}
