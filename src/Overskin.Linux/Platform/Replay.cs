using System.Diagnostics;
using System.Globalization;
using Overskin.Linux.Core;

namespace Overskin.Linux.Platform;

/// <summary>
/// Instant replay (as on Windows): the main screen is recorded all the time in the background in
/// short pieces, keeping only the newest few, so a hotkey can save the last 15 seconds to
/// 2 minutes after something happens. One ffmpeg process writes the pieces in turn (MPEG-TS, which
/// can be read even while it's being written).
/// </summary>
public sealed class Replay : IDisposable
{
    private readonly Process _process;
    private readonly string _folder;
    public int Seconds { get; }
    public static string Folder => Path.Combine(Path.GetDirectoryName(Log.FilePath)!, "replay");

    /// <summary>Seconds in each piece: about a third of what's kept.</summary>
    public static int PieceSeconds(int seconds) => Math.Clamp(seconds / 3, 5, 20);

    private Replay(Process process, string folder, int seconds)
    {
        _process = process;
        _folder = folder;
        Seconds = seconds;
    }

    public bool Running => !_process.HasExited;

    /// <summary>What's missing to keep a replay here, or null.</summary>
    public static string? Missing()
    {
        if (!ScreenGrabber.HasCommand("ffmpeg")) return Converter.FfmpegHint;
        if (OperatingSystem.IsLinux() && ScreenGrabber.IsWayland) return "Instant replay needs X11 here: Wayland doesn't let apps record the screen in the background.";
        if (!Mac.IsMac && !OperatingSystem.IsLinux()) return "Instant replay is built for macOS and Linux here.";
        return null;
    }

    /// <summary>The ffmpeg arguments that keep the pieces (for tests too).</summary>
    public static string Args(string input, string? sound, int seconds, int fps, string folder)
    {
        int piece = PieceSeconds(seconds);
        int count = (int)Math.Ceiling(seconds / (double)piece) + 2; // enough, plus the one being written
        return $"-hide_banner -loglevel error -y {input}{(sound != null ? " " + sound : "")} -map 0:v{(sound != null ? " -map 1:a -c:a aac -b:a 128k" : "")} " +
               $"-vf \"scale='min(1920,iw)':-2\" -c:v libx264 -preset veryfast -crf 23 -pix_fmt yuv420p -g {fps * 2} -force_key_frames \"expr:gte(t,n_forced*2)\" " +
               $"-f segment -segment_time {piece} -segment_wrap {count} -reset_timestamps 1 \"{Path.Combine(folder, "piece%03d.ts")}\"";
    }

    /// <summary>Starts keeping the last <paramref name="seconds"/> of the main screen.</summary>
    public static async Task<Replay> StartAsync(int seconds, bool sound)
    {
        var folder = Folder;
        Directory.CreateDirectory(folder);
        foreach (var f in Directory.GetFiles(folder, "piece*.ts")) try { File.Delete(f); } catch { }
        const int fps = 30;
        string input;
        string? soundInput = null;
        if (Mac.IsMac)
        {
            var list = await Devices.MacAsync();
            var screen = Devices.Screen(list, 0) ?? throw new InvalidOperationException("ffmpeg can't see the screen. Check Screen Recording for Overskin in System Settings.");
            input = $"-f avfoundation -framerate {fps} -capture_cursor 1 -i \"{screen.Index}:none\"";
            if (sound && Devices.Loopback(list) is { } loop) soundInput = $"-f avfoundation -i \":{loop.Index}\"";
        }
        else
        {
            input = $"-f x11grab -framerate {fps} -draw_mouse 1 -i {Environment.GetEnvironmentVariable("DISPLAY") ?? ":0"}";
            if (sound) soundInput = "-f pulse -i @DEFAULT_MONITOR@";
        }
        var p = Process.Start(new ProcessStartInfo("ffmpeg", Args(input, soundInput, seconds, fps, folder))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true,
        }) ?? throw new InvalidOperationException("couldn't start ffmpeg");
        p.OutputDataReceived += (_, _) => { };
        p.BeginOutputReadLine();
        _ = p.StandardError.ReadToEndAsync().ContinueWith(t => { if (t.Result.Length > 0) Log.Write("replay: " + t.Result.Trim()); });
        await Task.Delay(500);
        if (p.HasExited) throw new InvalidOperationException("The background recording stopped straight away. Details are in " + Log.FilePath);
        Log.Write($"replay: keeping the last {seconds}s");
        return new Replay(p, folder, seconds);
    }

    /// <summary>
    /// Saves the last <see cref="Seconds"/> as one MP4: the pieces joined in order (the newest still
    /// being written included), then only the end kept.
    /// </summary>
    public async Task SaveAsync(string output) => await SaveFromAsync(_folder, Seconds, output);

    /// <summary>Joins the pieces in a folder and keeps the last <paramref name="seconds"/> (for tests too).</summary>
    public static async Task SaveFromAsync(string folder, int seconds, string output)
    {
        var pieces = Directory.GetFiles(folder, "piece*.ts").Select(f => new FileInfo(f)).Where(f => f.Length > 0)
            .OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.Name).Select(f => f.FullName).ToList();
        if (pieces.Count == 0) throw new InvalidOperationException("Nothing has been recorded yet.");
        // copies, so recording can carry on writing over the oldest meanwhile
        var copies = new List<string>();
        for (int i = 0; i < pieces.Count; i++)
        {
            var copy = Path.Combine(Path.GetTempPath(), $"overskin-replay-{Guid.NewGuid():N}-{i}.ts");
            try { File.Copy(pieces[i], copy, overwrite: true); copies.Add(copy); } catch { }
        }
        var joined = Path.Combine(Path.GetTempPath(), $"overskin-replay-{Guid.NewGuid():N}.ts");
        try
        {
            var (ok, error) = await ScreenGrabber.RunAsync("ffmpeg", $"-hide_banner -loglevel error -y -i \"concat:{string.Join("|", copies)}\" -c copy \"{joined}\"", 300);
            if (!ok) throw new InvalidOperationException("Couldn't join the replay: " + error);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            (ok, error) = await ScreenGrabber.RunAsync("ffmpeg",
                $"-hide_banner -loglevel error -y -sseof -{seconds.ToString(CultureInfo.InvariantCulture)} -i \"{joined}\" -c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p -c:a aac -movflags +faststart \"{output}\"", 600);
            if (!ok || !File.Exists(output)) throw new InvalidOperationException("Couldn't save the replay: " + error);
        }
        finally
        {
            foreach (var c in copies) try { File.Delete(c); } catch { }
            try { File.Delete(joined); } catch { }
        }
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.StandardInput.Write("q");
                _process.StandardInput.Flush();
                if (!_process.WaitForExit(5000)) _process.Kill(true);
            }
        }
        catch { }
        _process.Dispose();
    }
}
