using Microsoft.Win32;

namespace Overskin;

/// <summary>"Start with Windows": a per-user Run entry, so no administrator rights are needed.</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Overskin";
    public const string TrayArgument = "--tray";

    private static string Command => $"\"{Environment.ProcessPath}\" {TrayArgument}";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Turns it on or off. Returns false if Windows refused.</summary>
    public static bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled) key.SetValue(ValueName, Command);
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>If it is on but points at an old location of the exe, points it here instead.</summary>
    public static void Refresh()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(ValueName) is string current && current != Command)
                key.SetValue(ValueName, Command);
        }
        catch
        {
            // not worth bothering the user about
        }
    }
}
