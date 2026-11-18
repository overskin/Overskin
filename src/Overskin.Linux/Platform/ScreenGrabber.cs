using System.Diagnostics;
using Avalonia.Media.Imaging;

namespace Overskin.Linux.Platform;

/// <summary>
/// Takes a picture of the whole desktop (every monitor).
/// X11 is read directly, which is instant and silent. Wayland doesn't let apps do that, so it goes
/// through the desktop portal (GNOME, KDE and wlroots desktops all have one), and failing that,
/// whichever screenshot command is installed.
/// </summary>
public static class ScreenGrabber
{
    /// <summary>For development: use this picture instead of the real screen.</summary>
    public static string? DemoFile { get; set; }

    /// <summary>The method that worked last, shown in the About section.</summary>
    public static string LastMethod { get; private set; } = "none yet";

    public static bool IsWayland =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) ||
        string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase);

    public static async Task<Bitmap> GrabAsync()
    {
        if (DemoFile != null)
        {
            LastMethod = "demo picture";
            return new Bitmap(DemoFile);
        }
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Screen capture is only built for Linux here. Run with --demo picture.png to try the rest.");

        var errors = new List<string>();

        // Under Wayland, X11 only sees the X11 apps (everything else comes out black), so skip it there
        if (!IsWayland && X11.IsAvailable)
        {
            try { var b = X11.GrabRoot(); LastMethod = "X11"; return b; }
            catch (Exception ex) { errors.Add("X11: " + ex.Message); }
        }

        try
        {
            var file = await Portal.ScreenshotAsync(interactive: false);
            if (file != null)
            {
                LastMethod = "desktop portal";
                return LoadAndDelete(file, deleteAfter: file.StartsWith(Path.GetTempPath()));
            }
            errors.Add("portal: cancelled");
        }
        catch (Exception ex) { errors.Add("portal: " + ex.Message); }

        foreach (var (tool, args) in Tools())
        {
            var path = Path.Combine(Path.GetTempPath(), $"overskin-{Environment.ProcessId}-{Guid.NewGuid():N}.png");
            try
            {
                if (!await RunAsync(tool, string.Format(args, path)) || !File.Exists(path)) continue;
                LastMethod = tool;
                return LoadAndDelete(path, deleteAfter: true);
            }
            catch (Exception ex) { errors.Add(tool + ": " + ex.Message); }
        }

        throw new InvalidOperationException(
            "Couldn't take a screenshot. On Wayland, install xdg-desktop-portal and your desktop's portal " +
            "(xdg-desktop-portal-gnome, -kde or -wlr), or grim.\n\n" + string.Join("\n", errors));
    }

    private static IEnumerable<(string Tool, string Args)> Tools()
    {
        if (IsWayland) yield return ("grim", "\"{0}\"");
        yield return ("gnome-screenshot", "-f \"{0}\"");
        yield return ("spectacle", "-b -n -f -o \"{0}\"");
        if (!IsWayland)
        {
            yield return ("maim", "\"{0}\"");
            yield return ("import", "-window root \"{0}\"");
        }
    }

    private static Bitmap LoadAndDelete(string path, bool deleteAfter)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return new Bitmap(stream);
        }
        finally
        {
            if (deleteAfter) try { File.Delete(path); } catch { }
        }
    }

    public static bool HasCommand(string name)
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        return paths.Any(p => p.Length > 0 && File.Exists(Path.Combine(p, name)));
    }

    private static async Task<bool> RunAsync(string tool, string args)
    {
        if (!HasCommand(tool)) return false;
        using var p = Process.Start(new ProcessStartInfo(tool, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        if (p == null) return false;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(); } catch { } return false; }
        return p.ExitCode == 0;
    }
}
