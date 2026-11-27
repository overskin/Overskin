using System.Diagnostics;
using Avalonia.Media.Imaging;
using Overskin.Linux.Core;

namespace Overskin.Linux.Platform;

/// <summary>
/// Takes a picture of the whole desktop (every monitor).
/// X11 is read directly, which is instant and silent. Wayland doesn't let apps do that: on
/// Hyprland, Sway and friends grim does it; elsewhere the desktop portal; failing those, whichever
/// screenshot command is installed, and last of all the portal's own picker.
/// </summary>
public static class ScreenGrabber
{
    /// <summary>For development: use this picture instead of the real screen.</summary>
    public static string? DemoFile { get; set; }

    /// <summary>The method that worked last.</summary>
    public static string LastMethod { get; private set; } = "none yet";

    public static bool IsWayland =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) ||
        string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase);

    /// <summary>Hyprland, Sway, river, niri and other compositors where grim works and the portal often can't take silent screenshots.</summary>
    public static bool IsWlroots
    {
        get
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE")) ||
                !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SWAYSOCK")) ||
                !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NIRI_SOCKET"))) return true;
            var desktop = (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").ToLowerInvariant();
            return new[] { "hyprland", "sway", "river", "wlroots", "niri", "wayfire", "labwc", "dwl", "qtile" }.Any(desktop.Contains);
        }
    }

    public static string DesktopName =>
        Mac.IsMac ? "macOS"
        : !OperatingSystem.IsLinux() ? "preview"
        : (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") is { Length: > 0 } d ? d + ", " : "") + (IsWayland ? "Wayland" : "X11");

    public static async Task<Bitmap> GrabAsync()
    {
        if (DemoFile != null)
        {
            LastMethod = "demo picture";
            return new Bitmap(DemoFile);
        }
        if (Mac.IsMac) return await GrabMacAsync();
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Screen capture is only built for Linux and macOS here. Run with --demo picture.png to try the rest.");

        Log.Write("grab: " + Log.Environment_());
        var errors = new List<string>();

        async Task<Bitmap?> Tool(string tool, string args)
        {
            if (!HasCommand(tool)) return null;
            var path = Path.Combine(Path.GetTempPath(), $"overskin-{Environment.ProcessId}-{Guid.NewGuid():N}.png");
            try
            {
                var (ok, error) = await RunAsync(tool, string.Format(args, path));
                if (ok && File.Exists(path)) { LastMethod = tool; return LoadAndDelete(path, deleteAfter: true); }
                errors.Add($"{tool}: {(error.Length > 0 ? error : "failed")}");
            }
            catch (Exception ex) { errors.Add(tool + ": " + ex.Message); }
            return null;
        }

        async Task<Bitmap?> FromPortal(bool interactive, TimeSpan timeout)
        {
            var name = interactive ? "portal (picker)" : "portal";
            try
            {
                var file = await Portal.ScreenshotAsync(interactive, timeout);
                if (file == null) { errors.Add(name + ": cancelled or refused"); return null; }
                LastMethod = "desktop " + name;
                // the portal saves into the user's own screenshots folder; Overskin keeps its own copy
                return LoadAndDelete(file, deleteAfter: !interactive || file.StartsWith(Path.GetTempPath()));
            }
            catch (Exception ex) { errors.Add($"{name}: {ex.Message}"); return null; }
        }

        Bitmap? shot = null;
        if (!IsWayland)
        {
            if (X11.IsAvailable)
            {
                try { shot = X11.GrabRoot(); LastMethod = "X11"; }
                catch (Exception ex) { errors.Add("X11: " + ex.Message); }
            }
            shot ??= await Tool("maim", "\"{0}\"") ?? await Tool("import", "-window root \"{0}\"");
        }
        else
        {
            // under Wayland, X11 only sees the X11 apps (everything else comes out black), so it isn't tried
            if (IsWlroots) shot = await Tool("grim", (await FocusedOutputAsync() is { } output ? $"-o \"{output}\" " : "") + "\"{0}\"");
            shot ??= await FromPortal(false, TimeSpan.FromSeconds(12));
            shot ??= await Tool("grim", "\"{0}\"")
                  ?? await Tool("gnome-screenshot", "-f \"{0}\"")
                  ?? await Tool("spectacle", "-b -n -f -o \"{0}\"");
            // last resort: the portal's own picker, where the desktop shows its screenshot dialog
            shot ??= await FromPortal(true, TimeSpan.FromMinutes(2));
        }

        if (shot != null)
        {
            Log.Write($"grab: got {shot.PixelSize.Width}x{shot.PixelSize.Height} with {LastMethod}");
            return shot;
        }

        foreach (var e in errors) Log.Write("grab failed: " + e);
        var advice = !IsWayland
            ? "Overskin couldn't read the X11 screen."
            : IsWlroots
                ? "On Hyprland, Sway and similar desktops Overskin uses grim. Install it (Arch: sudo pacman -S grim · Debian/Ubuntu: sudo apt install grim · Fedora: sudo dnf install grim) and try again."
                : "Install xdg-desktop-portal with your desktop's portal (xdg-desktop-portal-gnome or -kde), or grim.";
        throw new InvalidOperationException(advice + "\n\n" + string.Join("\n", errors) + "\n\nDetails are in " + Log.FilePath);
    }

    /// <summary>
    /// macOS: the display the pointer is on, with the screencapture tool every Mac has, at full
    /// Retina sharpness. Without the Screen Recording permission macOS hands back only the desktop
    /// picture, so the permission is checked (and asked for) first.
    /// </summary>
    private static async Task<Bitmap> GrabMacAsync()
    {
        if (!Mac.CanReadScreen(ask: true))
            throw new InvalidOperationException(
                "Overskin needs permission to read the screen. Open System Settings › Privacy & Security › Screen Recording, " +
                "switch Overskin on, then quit Overskin and open it again.");
        var display = Mac.DisplayUnderPointer();
        var path = Path.Combine(Path.GetTempPath(), $"overskin-{Environment.ProcessId}-{Guid.NewGuid():N}.png");
        string N(double v) => Math.Round(v).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var (ok, error) = await RunAsync("/usr/sbin/screencapture", $"-x -t png -R {N(display.X)},{N(display.Y)},{N(display.Width)},{N(display.Height)} \"{path}\"");
        if (!ok || !File.Exists(path))
            throw new InvalidOperationException("macOS's screencapture didn't take the picture: " + (error.Length > 0 ? error : "no file came back") + "\n\nDetails are in " + Log.FilePath);
        // the picture comes in the display's own colours (Display P3 on most Macs); turned into
        // ordinary sRGB, so colours stay true in the editor, in saved files and in other apps
        var (converted, why) = await RunAsync("/usr/bin/sips", $"--matchTo \"/System/Library/ColorSync/Profiles/sRGB Profile.icc\" \"{path}\"");
        if (!converted) Log.Write("grab: couldn't turn the colours into sRGB: " + why);
        var shot = LoadAndDelete(path, deleteAfter: true);
        Mac.Shot = (display, shot.PixelSize.Width / Math.Max(1, display.Width));
        LastMethod = "screencapture";
        Log.Write($"grab: got {shot.PixelSize.Width}x{shot.PixelSize.Height} of the display at {N(display.X)},{N(display.Y)} (scale {Mac.Shot.Scale:0.##})");
        return shot;
    }

    /// <summary>
    /// The monitor that has focus on Hyprland or Sway, so the picture matches the full-screen
    /// picker, which opens on that monitor.
    /// </summary>
    private static async Task<string?> FocusedOutputAsync()
    {
        try
        {
            string? json = null;
            if (HasCommand("hyprctl")) json = await OutputOf("hyprctl", "monitors -j");
            else if (HasCommand("swaymsg")) json = await OutputOf("swaymsg", "-t get_outputs -r");
            if (json == null) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            foreach (var m in doc.RootElement.EnumerateArray())
                if (m.TryGetProperty("focused", out var f) && f.ValueKind == System.Text.Json.JsonValueKind.True)
                    return m.GetProperty("name").GetString();
        }
        catch (Exception ex) { Log.Write("focused monitor: " + ex.Message); }
        return null;
    }

    public static async Task<string?> OutputOf(string tool, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(tool, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true });
        if (p == null) return null;
        p.StandardInput.Close(); // nothing to type: tools that would wait for keys see the end straight away
        _ = p.StandardError.ReadToEndAsync();
        var text = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return p.ExitCode == 0 ? text : null;
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
        // Windows (where the self-test also runs) adds .exe
        return paths.Any(p => p.Length > 0 && (File.Exists(Path.Combine(p, name)) || (OperatingSystem.IsWindows() && File.Exists(Path.Combine(p, name + ".exe")))));
    }

    public static async Task<(bool Ok, string Error)> RunAsync(string tool, string args, int timeoutSeconds = 20)
    {
        using var p = Process.Start(new ProcessStartInfo(tool, args)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        });
        if (p == null) return (false, "couldn't start");
        p.StandardInput.Close(); // nothing to type: tools that would wait for keys see the end straight away
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } return (false, "took too long"); }
        await output;
        var text = (await error).Trim();
        if (p.ExitCode != 0 && text.Length == 0) text = $"{Path.GetFileName(tool)} stopped with code {p.ExitCode}";
        return (p.ExitCode == 0, text.Length > 300 ? text[^300..] : text);
    }
}
