#pragma warning disable CS0618 // DataObject still works in 11.3 and is the simplest way to offer a PNG
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Overskin.Linux.Core;

namespace Overskin.Linux.Platform;

/// <summary>Clipboard, notifications and opening folders, the Linux way.</summary>
public static class Desktop
{
    /// <summary>
    /// Puts the picture on the clipboard as a PNG. wl-copy and xclip keep serving it after we move on,
    /// which Avalonia's own clipboard can't do for pictures on every desktop, so they come first.
    /// </summary>
    public static async Task<bool> CopyImageAsync(Bitmap image, TopLevel? top)
    {
        var png = Imaging.Png(image);
        if (OperatingSystem.IsLinux())
        {
            if (ScreenGrabber.IsWayland && ScreenGrabber.HasCommand("wl-copy") && await PipeAsync("wl-copy", "--type image/png", png))
                return true;
            if (ScreenGrabber.HasCommand("xclip") && await PipeAsync("xclip", "-selection clipboard -t image/png -i", png))
                return true;
        }

        var clipboard = top?.Clipboard;
        if (clipboard == null) return false;
        try
        {
            var data = new DataObject();
            data.Set("image/png", png);
            data.Set("PNG", png);
            await clipboard.SetDataObjectAsync(data);
            await clipboard.FlushAsync();
            return true;
        }
        catch { return false; }
    }

    public static async Task CopyTextAsync(string text, TopLevel? top)
    {
        if (top?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }

    private static async Task<bool> PipeAsync(string tool, string args, byte[] input)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(tool, args)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
            });
            if (p == null) return false;
            await p.StandardInput.BaseStream.WriteAsync(input);
            p.StandardInput.Close();
            // both fork into the background to keep the clipboard alive; the first process exits straight away
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await p.WaitForExitAsync(cts.Token); } catch (OperationCanceledException) { return true; }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    public static void Notify(string title, string body, string? iconPath = null)
    {
        if (!OperatingSystem.IsLinux() || !ScreenGrabber.HasCommand("notify-send")) return;
        var args = new List<string> { "-a", "Overskin" };
        if (iconPath != null && File.Exists(iconPath)) { args.Add("-i"); args.Add(iconPath); }
        args.Add(title);
        args.Add(body);
        Start("notify-send", args);
    }

    public static void Open(string pathOrFolder)
    {
        if (OperatingSystem.IsLinux()) Start("xdg-open", new[] { pathOrFolder });
        else if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(pathOrFolder) { UseShellExecute = true });
        else Start("open", new[] { pathOrFolder });
    }

    private static void Start(string tool, IEnumerable<string> args)
    {
        try
        {
            var info = new ProcessStartInfo(tool) { UseShellExecute = false };
            foreach (var a in args) info.ArgumentList.Add(a);
            Process.Start(info)?.Dispose();
        }
        catch { }
    }
}
