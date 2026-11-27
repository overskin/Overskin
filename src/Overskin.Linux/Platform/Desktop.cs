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
        if (Mac.IsMac)
        {
            try { if (Mac.CopyPng(png)) return true; }
            catch (Exception ex) { Log.Write("clipboard: " + ex.Message); }
        }
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

    /// <summary>
    /// Puts files on the clipboard the way file managers do, so pasting into a chat or a folder gives
    /// the real file (a GIF stays a GIF). A single picture also goes on as a picture.
    /// </summary>
    public static async Task<bool> CopyFilesAsync(IReadOnlyList<string> paths, TopLevel? top)
    {
        if (paths.Count == 0) return false;
        var uris = string.Join("\r\n", paths.Select(p => new Uri(Path.GetFullPath(p)).AbsoluteUri));
        var bytes = System.Text.Encoding.UTF8.GetBytes(uris + "\r\n");
        if (Mac.IsMac)
        {
            try { if (Mac.CopyFiles(paths)) return true; }
            catch (Exception ex) { Log.Write("clipboard: " + ex.Message); }
        }
        if (OperatingSystem.IsLinux())
        {
            if (paths.Count == 1 && Converter.IsPicture(paths[0]) && Path.GetExtension(paths[0]).ToLowerInvariant() == ".png")
            {
                // pasting a single PNG into an app wants the picture itself
                if (ScreenGrabber.IsWayland && ScreenGrabber.HasCommand("wl-copy") && await PipeAsync("wl-copy", "--type image/png", await File.ReadAllBytesAsync(paths[0])))
                    return true;
            }
            if (ScreenGrabber.IsWayland && ScreenGrabber.HasCommand("wl-copy") && await PipeAsync("wl-copy", "--type text/uri-list", bytes))
                return true;
            if (ScreenGrabber.HasCommand("xclip") && await PipeAsync("xclip", "-selection clipboard -t text/uri-list -i", bytes))
                return true;
        }
        var clipboard = top?.Clipboard;
        if (clipboard == null) return false;
        try
        {
            var data = new DataObject();
            data.Set("text/uri-list", bytes);
            data.Set(DataFormats.Text, string.Join("\n", paths));
            await clipboard.SetDataObjectAsync(data);
            await clipboard.FlushAsync();
            return true;
        }
        catch { return false; }
    }

    /// <summary>The picture on the clipboard (or a copied picture file), or null when there's none.</summary>
    public static async Task<Bitmap?> PastePictureAsync(TopLevel? top)
    {
        byte[]? png = null;
        try
        {
            if (Mac.IsMac) png = Mac.PastePicture();
            else if (OperatingSystem.IsLinux())
            {
                if (ScreenGrabber.IsWayland && ScreenGrabber.HasCommand("wl-paste")) png = await ReadAsync("wl-paste", "--no-newline --type image/png");
                if (png == null && ScreenGrabber.HasCommand("xclip")) png = await ReadAsync("xclip", "-selection clipboard -t image/png -o");
            }
            if (png == null && top?.Clipboard is { } clipboard)
            {
                foreach (var format in new[] { "image/png", "PNG" })
                    if (await clipboard.GetDataAsync(format) is byte[] bytes && bytes.Length > 0) { png = bytes; break; }
                if (png == null && await clipboard.GetTextAsync() is { } text && File.Exists(text.Trim()) && Converter.IsPicture(text.Trim()))
                    png = await File.ReadAllBytesAsync(text.Trim());
            }
            if (png == null || png.Length == 0) return null;
            using var stream = new MemoryStream(png);
            return new Bitmap(stream);
        }
        catch (Exception ex)
        {
            Log.Write("paste picture: " + ex.Message);
            return null;
        }
    }

    private static async Task<byte[]?> ReadAsync(string tool, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(tool, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
            if (p == null) return null;
            _ = p.StandardError.ReadToEndAsync();
            using var ms = new MemoryStream();
            await p.StandardOutput.BaseStream.CopyToAsync(ms);
            await p.WaitForExitAsync();
            return p.ExitCode == 0 && ms.Length > 0 ? ms.ToArray() : null;
        }
        catch { return null; }
    }

    public static async Task CopyTextAsync(string text, TopLevel? top)
    {
        if (Mac.IsMac)
        {
            try { if (Mac.CopyText(text)) return; }
            catch (Exception ex) { Log.Write("clipboard: " + ex.Message); }
        }
        if (top?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }

    private static async Task<bool> PipeAsync(string tool, string args, byte[] input)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(tool, args)
            {
                UseShellExecute = false, CreateNoWindow = true,
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
        if (Mac.IsMac)
        {
            // AppleScript's own notification: no permission needed, shown under Script Editor's name on some macOS versions
            static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            Start("/usr/bin/osascript", new[] { "-e", $"display notification {Quote(body)} with title {Quote("Overskin")} subtitle {Quote(title)}" });
            return;
        }
        if (!OperatingSystem.IsLinux() || !ScreenGrabber.HasCommand("notify-send")) return;
        var args = new List<string> { "-a", "Overskin" };
        if (iconPath != null && File.Exists(iconPath)) { args.Add("-i"); args.Add(iconPath); }
        args.Add(title);
        args.Add(body);
        Start("notify-send", args);
    }

    /// <summary>Shows the file selected in the file manager (or opens its folder).</summary>
    public static void ShowInFolder(string path)
    {
        if (Mac.IsMac) { Start("/usr/bin/open", new[] { "-R", Path.GetFullPath(path) }); return; }
        if (OperatingSystem.IsLinux() && ScreenGrabber.HasCommand("dbus-send"))
        {
            Start("dbus-send", new[] { "--session", "--dest=org.freedesktop.FileManager1", "--type=method_call",
                "/org/freedesktop/FileManager1", "org.freedesktop.FileManager1.ShowItems",
                "array:string:" + new Uri(Path.GetFullPath(path)).AbsoluteUri, "string:" });
            return;
        }
        Open(Path.GetDirectoryName(path)!);
    }

    /// <summary>Into the desktop's trash (gio), so it can be got back; deleted for good only where there's no trash.</summary>
    public static async Task TrashAsync(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (Mac.IsMac)
            {
                bool trashed = false;
                try { trashed = Mac.Trash(path); } catch (Exception ex) { Log.Write("trash: " + ex.Message); }
                if (trashed) continue;
            }
            if (OperatingSystem.IsLinux() && ScreenGrabber.HasCommand("gio"))
            {
                var (ok, _) = await ScreenGrabber.RunAsync("gio", $"trash \"{path}\"");
                if (ok) continue;
            }
            if (OperatingSystem.IsLinux() && ScreenGrabber.HasCommand("trash-put"))
            {
                var (ok, _) = await ScreenGrabber.RunAsync("trash-put", $"\"{path}\"");
                if (ok) continue;
            }
            File.Delete(path);
        }
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
            var info = new ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) info.ArgumentList.Add(a);
            Process.Start(info)?.Dispose();
        }
        catch { }
    }
}
