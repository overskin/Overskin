using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Win32;

namespace Overskin;

/// <summary>A release on GitHub: its version, notes page and installer.</summary>
public sealed record Release(Version Version, string Tag, string Page, string InstallerUrl, long InstallerSize);

/// <summary>
/// Looks for a newer Overskin on GitHub, downloads its installer, and installs it over this copy:
/// Overskin closes, the installer runs without a window (keeping your shortcuts, settings and
/// start-with-Windows choice), then opens the new version. Only Overskin's own GitHub releases
/// are used, nothing is sent but an ordinary request for the latest release, and the download is
/// checked against the release before it runs.
/// </summary>
public static class Updater
{
    public const string Repository = "overskin/Overskin";
    private const string InstallerName = "OverskinSetup.exe";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Overskin";

    private static readonly HttpClient Http = MakeClient();

    private static HttpClient MakeClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Overskin-Updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>This copy's version (major.minor.build).</summary>
    public static Version Current
    {
        get
        {
            var v = typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    /// <summary>"v3.2.0" or "3.2" → 3.2.0; null when it isn't a version.</summary>
    internal static Version? ParseTag(string tag)
    {
        var text = tag.Trim().TrimStart('v', 'V');
        int dash = text.IndexOfAny(new[] { '-', '+' });
        if (dash >= 0) text = text[..dash];
        if (!Version.TryParse(text, out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    /// <summary>The latest release, from GitHub's own answer (kept separate so it can be tested).</summary>
    internal static Release? ReadRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (ParseTag(tag) is not { } version) return null;
        string page = root.TryGetProperty("html_url", out var html) ? html.GetString() ?? "" : "";
        if (!root.TryGetProperty("assets", out var assets)) return null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (!string.Equals(asset.GetProperty("name").GetString(), InstallerName, StringComparison.OrdinalIgnoreCase)) continue;
            string url = asset.GetProperty("browser_download_url").GetString() ?? "";
            long size = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
            // only ever GitHub's own download address for this repository
            if (!url.StartsWith($"https://github.com/{Repository}/releases/download/", StringComparison.OrdinalIgnoreCase)) return null;
            return new Release(version, tag, page, url, size);
        }
        return null;
    }

    /// <summary>The newest release when it's newer than this copy; null when up to date or offline.</summary>
    public static async Task<Release?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            string json = await Http.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/latest", ct);
            var release = ReadRelease(json);
            return release != null && release.Version > Current ? release : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CrashLog.Note("update check: " + ex.GetType().Name + " " + ex.Message);
            return null;
        }
    }

    /// <summary>Downloads the installer to a temporary file and checks it; returns its path.</summary>
    public static async Task<string> DownloadAsync(Release release, IProgress<double>? progress, CancellationToken ct = default)
    {
        string folder = Path.Combine(Path.GetTempPath(), "Overskin", "Update");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, $"OverskinSetup-{release.Version}.exe");
        string part = path + ".part";

        using (var response = await Http.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            // GitHub sends the file from its own download servers
            string host = response.RequestMessage?.RequestUri?.Host ?? "";
            if (!(host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("The download came from somewhere other than GitHub.");
            long total = response.Content.Headers.ContentLength ?? release.InstallerSize;
            await using var from = await response.Content.ReadAsStreamAsync(ct);
            await using var to = File.Create(part);
            var buffer = new byte[1 << 16];
            long done = 0;
            int n;
            while ((n = await from.ReadAsync(buffer, ct)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report(Math.Min(1, done / (double)total));
            }
        }

        // the file has to be the size GitHub listed, and be the version it says it is
        long length = new FileInfo(part).Length;
        if (release.InstallerSize > 0 && length != release.InstallerSize)
        {
            File.Delete(part);
            throw new InvalidOperationException("The download didn't arrive whole. Try again.");
        }
        var info = FileVersionInfo.GetVersionInfo(part);
        if (ParseTag(info.ProductVersion ?? "") is not { } got || got != release.Version)
        {
            File.Delete(part);
            throw new InvalidOperationException("The downloaded installer isn't the version GitHub listed.");
        }
        File.Move(part, path, overwrite: true);
        return path;
    }

    /// <summary>The folder Overskin is installed in, if it was installed with the installer.</summary>
    public static string? InstalledFolder
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
                return key?.GetValue("InstallLocation") as string;
            }
            catch { return null; }
        }
    }

    /// <summary>True when this running copy is the installed one, so it can be updated in place.</summary>
    public static bool IsInstalledCopy
    {
        get
        {
            try
            {
                if (InstalledFolder is not { } folder || Environment.ProcessPath is not { } exe) return false;
                return string.Equals(Path.GetFullPath(Path.GetDirectoryName(exe)!).TrimEnd('\\'), Path.GetFullPath(folder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// Starts the installer. For the installed copy it runs without a window once this process has
    /// closed, then opens the new version (the caller then closes Overskin); for any other copy
    /// the installer opens as usual. True when the caller should close now.
    /// </summary>
    public static bool StartInstaller(string installer)
    {
        if (IsInstalledCopy)
        {
            string log = Path.Combine(Path.GetTempPath(), "Overskin", "Update", "install.log");
            var args = $"--quiet --update --launch --wait-for {Environment.ProcessId} --log \"{log}\"";
            Process.Start(new ProcessStartInfo(installer, args) { UseShellExecute = false });
            return true;
        }
        Process.Start(new ProcessStartInfo(installer) { UseShellExecute = true });
        return false;
    }
}
