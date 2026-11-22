using System.Diagnostics;
using System.Threading;
using Microsoft.Win32;

namespace OverskinSetup;

public sealed class InstallOptions
{
    public string Folder = Setup.DefaultFolder;
    public bool StartMenu = true;
    public bool Desktop;
    /// <summary>null leaves the Run entry as it is.</summary>
    public bool? Startup;
}

/// <summary>
/// The actual work: copying, shortcuts, the Run entry and the uninstall entry. Everything is
/// per-user (HKCU and the user's own folders), so no administrator rights are needed.
/// Only files Overskin itself owns are ever deleted; folders are removed only once empty.
/// </summary>
public static class Setup
{
    public const string AppName = "Overskin";
    public const string ExeName = "Overskin.exe";
    public const string UninstallerName = "Uninstall.exe";
    public const string SettingsName = "overskin.settings.json";

    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Overskin";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Overskin";
    private const string TrayArgument = "--tray";
    // must match the name in the app's App.xaml.cs
    private const string SingleInstanceMutex = "Overskin.SingleInstance.6f0b7b1e";

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);

    public static string StartMenuLink =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");

    public static string DesktopLink =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");

    /// <summary>Folder of an earlier install, if there is one.</summary>
    public static string? InstalledFolder
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
                return key?.GetValue("InstallLocation") is string dir && Directory.Exists(dir) ? dir : null;
            }
            catch
            {
                return null;
            }
        }
    }

    public static bool StartupEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(RunValue) is string;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>True when a copy of Overskin is running anywhere on this desktop.</summary>
    public static bool RunningAnywhere
    {
        get
        {
            try
            {
                if (!Mutex.TryOpenExisting(SingleInstanceMutex, out var m)) return false;
                m.Dispose();
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>True when Overskin.exe in this folder is running, so it can't be replaced.</summary>
    public static bool InUse(string folder)
    {
        string exe;
        try { exe = Path.Combine(folder, ExeName); }
        catch { return false; }
        if (!File.Exists(exe)) return false;
        try
        {
            using var fs = new FileStream(exe, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Returns a problem with the folder, or null if it's usable.</summary>
    public static string? CheckFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return "Choose a folder";
        folder = folder.Trim();
        try
        {
            if (folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return "Not a valid path";
            string root = Path.GetPathRoot(folder) ?? "";
            bool drive = root.Length >= 3 && root[1] == ':';
            bool unc = root.StartsWith(@"\\", StringComparison.Ordinal);
            if (!drive && !unc) return "Use a full path, like C:\\Apps\\Overskin";
            Path.GetFullPath(folder);
        }
        catch
        {
            return "Not a valid path";
        }
        if (File.Exists(folder)) return "That's a file";
        return null;
    }

    public static string Normalize(string folder) => Path.GetFullPath(folder.Trim()).TrimEnd('\\', '/');

    // ---------------------------------------------------------------- install

    /// <summary>Installs. <paramref name="report"/> gets progress 0–1 and a short step name.</summary>
    public static void Install(InstallOptions o, string? payloadOverride, Action<double, string> report)
    {
        string folder = Normalize(o.Folder);
        string exe = Path.Combine(folder, ExeName);
        string uninstaller = Path.Combine(folder, UninstallerName);

        report(0, "Copying Overskin");
        Directory.CreateDirectory(folder);
        if (InUse(folder)) throw new IOException("Overskin is running from this folder. Close it and try again.");
        Payload.Extract(exe, payloadOverride, p => report(p * 0.86, "Copying Overskin"));

        report(0.88, "Adding the uninstaller");
        Payload.WriteEngine(uninstaller);

        report(0.92, "Shortcuts");
        SetShortcut(StartMenuLink, exe, o.StartMenu);
        SetShortcut(DesktopLink, exe, o.Desktop);
        Native.NotifyShell();

        report(0.95, "Registering");
        if (o.Startup.HasValue) SetStartup(exe, o.Startup.Value);
        Register(folder, exe, uninstaller);

        report(1, "Done");
    }

    /// <summary>True when that shortcut exists and opens Overskin in this folder.</summary>
    public static bool HasShortcut(string lnk, string folder)
    {
        try { return PointsInto(Native.ShortcutTarget(lnk), Path.Combine(Normalize(folder), ExeName)); }
        catch { return false; }
    }

    private static void SetShortcut(string lnk, string exe, bool wanted)
    {
        if (wanted)
        {
            Native.CreateShortcut(lnk, exe, "Screenshots, pinning and tracing");
        }
        else if (PointsInto(Native.ShortcutTarget(lnk), exe))
        {
            // turned off on a reinstall: remove ours, never somebody else's
            TryDelete(lnk);
        }
    }

    private static void SetStartup(string exe, bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(RunValue, $"\"{exe}\" {TrayArgument}");
        else key.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    private static void Register(string folder, string exe, string uninstaller)
    {
        string version = FileVersionInfo.GetVersionInfo(exe).ProductVersion ?? "";
        int plus = version.IndexOf('+');
        if (plus > 0) version = version.Substring(0, plus);
        long sizeKb = (new FileInfo(exe).Length + new FileInfo(uninstaller).Length) / 1024;

        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", AppName);
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", AppName);
        key.SetValue("DisplayIcon", exe);
        key.SetValue("InstallLocation", folder);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        key.SetValue("UninstallString", $"\"{uninstaller}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{uninstaller}\" --uninstall --quiet");
        key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, sizeKb), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    // ---------------------------------------------------------------- uninstall

    public static void Uninstall(string folder, bool removeSettings, Action<double, string> report)
    {
        folder = Normalize(folder);
        string exe = Path.Combine(folder, ExeName);
        if (!IsInstallFolder(folder)) throw new InvalidOperationException($"No Overskin install in {folder}");

        report(0.1, "Removing Overskin");
        if (InUse(folder)) throw new IOException("Overskin is running. Close it and try again.");
        if (File.Exists(exe)) File.Delete(exe);

        report(0.4, "Shortcuts");
        foreach (var lnk in new[] { StartMenuLink, DesktopLink })
            if (PointsInto(Native.ShortcutTarget(lnk), exe)) TryDelete(lnk);
        Native.NotifyShell();

        report(0.6, "Registry");
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (run?.GetValue(RunValue) is string cmd && cmd.IndexOf(exe, StringComparison.OrdinalIgnoreCase) >= 0)
                run.DeleteValue(RunValue, throwOnMissingValue: false);
        }
        catch { }
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
            string? registered = key?.GetValue("InstallLocation") as string;
            key?.Dispose();
            if (registered == null || string.Equals(Normalize(registered), folder, StringComparison.OrdinalIgnoreCase))
                Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        }
        catch { }

        if (removeSettings)
        {
            report(0.8, "Settings");
            TryDelete(Path.Combine(folder, SettingsName));
            string roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);
            TryDelete(Path.Combine(roaming, SettingsName));
            TryRemoveEmpty(roaming);
            // what Overskin learnt about your captures (the text it read for search)
            string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
            TryDelete(Path.Combine(local, "search-index.json"));
        }

        // the uninstaller may be the running exe; App deletes it after exit if so
        string uninstaller = Path.Combine(folder, UninstallerName);
        if (!SamePath(uninstaller, Payload.SelfPath)) TryDelete(uninstaller);
        TryRemoveEmpty(folder);

        report(1, "Done");
    }

    /// <summary>
    /// Only a folder this setup installed into counts: it has Uninstall.exe, or it's the one in
    /// the uninstall entry. A portable copy of Overskin.exe elsewhere is never touched.
    /// </summary>
    public static bool IsInstallFolder(string folder)
    {
        folder = Normalize(folder);
        if (File.Exists(Path.Combine(folder, UninstallerName))) return true;
        string? registered = InstalledFolder;
        return registered != null && string.Equals(Normalize(registered), folder, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- helpers

    private static bool PointsInto(string? target, string exe) =>
        target != null && SamePath(target, exe);

    public static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); } catch { }
    }

    public static void TryRemoveEmpty(string folder)
    {
        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
        }
        catch { }
    }
}
