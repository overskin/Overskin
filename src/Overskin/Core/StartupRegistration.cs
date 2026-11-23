using Microsoft.Win32;

namespace Overskin;

#if STORE
/// <summary>
/// "Start with Windows" for the Microsoft Store copy: the package's startup task
/// (windows.startupTask "OverskinStartup" in Package.appxmanifest), which Windows lists in
/// Task Manager → Startup apps. The Run registry key isn't used, because a packaged app's
/// exe path changes with every update.
/// </summary>
public static class StartupRegistration
{
    public const string TrayArgument = "--tray";
    private const string TaskId = "OverskinStartup";

    private static Windows.ApplicationModel.StartupTask? Find()
    {
        try
        {
            // off the UI thread, so waiting for it can't deadlock
            return Task.Run(async () => await Windows.ApplicationModel.StartupTask.GetAsync(TaskId)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            CrashLog.Note("startup task: " + ex.GetType().Name + " " + ex.Message);
            return null;
        }
    }

    public static bool IsEnabled =>
        Find()?.State is Windows.ApplicationModel.StartupTaskState.Enabled or Windows.ApplicationModel.StartupTaskState.EnabledByPolicy;

    /// <summary>Turns it on or off. Returns null when it worked, or what to tell the user.</summary>
    public static async Task<string?> SetAsync(bool enabled)
    {
        try
        {
            var task = await Windows.ApplicationModel.StartupTask.GetAsync(TaskId);
            if (!enabled)
            {
                if (task.State == Windows.ApplicationModel.StartupTaskState.EnabledByPolicy)
                    return "Your organisation's settings start Overskin with Windows, so it can't be turned off here.";
                task.Disable();
                return null;
            }
            var state = await task.RequestEnableAsync();
            return state switch
            {
                Windows.ApplicationModel.StartupTaskState.Enabled or Windows.ApplicationModel.StartupTaskState.EnabledByPolicy => null,
                Windows.ApplicationModel.StartupTaskState.DisabledByUser =>
                    "Overskin was turned off in Task Manager → Startup apps. Turn it on there and it starts with Windows again.",
                Windows.ApplicationModel.StartupTaskState.DisabledByPolicy =>
                    "Your organisation's settings don't allow Overskin to start with Windows.",
                _ => "Windows didn't allow changing the startup setting.",
            };
        }
        catch (Exception ex)
        {
            CrashLog.Note("startup task: " + ex.GetType().Name + " " + ex.Message);
            return "Windows didn't allow changing the startup setting.";
        }
    }

    /// <summary>Nothing to keep up to date: Windows starts the package, wherever it is.</summary>
    public static void Refresh() { }

    /// <summary>
    /// True when Windows started Overskin through the startup task at sign-in. That start has no
    /// --tray argument, so this is how it knows to stay in the tray.
    /// </summary>
    public static bool LaunchedAtSignIn
    {
        get
        {
            try
            {
                return Windows.ApplicationModel.AppInstance.GetActivatedEventArgs()?.Kind
                       == Windows.ApplicationModel.Activation.ActivationKind.StartupTask;
            }
            catch
            {
                return false;
            }
        }
    }
}
#else
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

    /// <summary>Turns it on or off. Returns null when it worked, or what to tell the user.</summary>
    public static Task<string?> SetAsync(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled) key.SetValue(ValueName, Command);
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
            return Task.FromResult<string?>(null);
        }
        catch
        {
            return Task.FromResult<string?>("Windows didn't allow changing the startup setting.");
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

    /// <summary>Only the Store copy is started by a startup task; this one gets --tray instead.</summary>
    public static bool LaunchedAtSignIn => false;
}
#endif
