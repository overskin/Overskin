using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Overskin.Linux.Core;

/// <summary>A release on GitHub: its version, notes page, and the download for this computer.</summary>
public sealed record Release(Version Version, string Tag, string Page, string DownloadUrl, long DownloadSize);

/// <summary>
/// Looks for a newer Overskin on GitHub (as on Windows). On a Mac or Linux the new copy is
/// downloaded from its release page rather than installed from here: a Mac only opens an app it
/// has been told to trust, which is a step for you, not for Overskin. Nothing is sent but an
/// ordinary request for the latest release.
/// </summary>
public static class Updater
{
    public const string Repository = "overskin/Overskin";

    private static readonly HttpClient Http = MakeClient();

    private static HttpClient MakeClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
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
    public static Version? ParseTag(string tag)
    {
        var text = tag.Trim().TrimStart('v', 'V');
        int dash = text.IndexOfAny(new[] { '-', '+' });
        if (dash >= 0) text = text[..dash];
        if (!Version.TryParse(text, out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    /// <summary>Whether a release file is the one for this computer: the Mac build for this kind of Mac, or the Linux one.</summary>
    public static bool IsForThisComputer(string name, bool mac, bool arm)
    {
        name = name.ToLowerInvariant();
        if (mac)
            return name.Contains("mac") && name.EndsWith(".zip") && (arm ? name.Contains("applesilicon") || name.Contains("arm64") : name.Contains("intel") || name.Contains("x64"));
        return name.Contains("linux") && (name.EndsWith(".tar.gz") || name.EndsWith(".appimage") || name.EndsWith(".zip"));
    }

    /// <summary>The latest release that has a download for this computer, from GitHub's own answer (kept separate so it can be tested).</summary>
    public static Release? ReadRelease(string json, bool mac, bool arm)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;
        string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        if (ParseTag(tag) is not { } version) return null;
        string page = root.TryGetProperty("html_url", out var html) ? html.GetString() ?? "" : "";
        // only ever this repository's own pages
        if (!page.StartsWith($"https://github.com/{Repository}/releases/", StringComparison.OrdinalIgnoreCase)) return null;
        if (!root.TryGetProperty("assets", out var assets)) return null;
        foreach (var asset in assets.EnumerateArray())
        {
            string name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (!IsForThisComputer(name, mac, arm)) continue;
            string url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
            if (!url.StartsWith($"https://github.com/{Repository}/releases/download/", StringComparison.OrdinalIgnoreCase)) continue;
            long size = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
            return new Release(version, tag, page, url, size);
        }
        return null; // this release has nothing for this computer (yet)
    }

    /// <summary>The newest release when it's newer than this copy and has a download for this computer; null when up to date or offline.</summary>
    public static async Task<Release?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            string json = await Http.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/latest", ct);
            var release = ReadRelease(json, OperatingSystem.IsMacOS(), RuntimeInformation.ProcessArchitecture == Architecture.Arm64);
            return release != null && release.Version > Current ? release : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Write("update check: " + ex.GetType().Name + " " + ex.Message);
            return null;
        }
    }
}
