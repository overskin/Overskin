using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Overskin.Linux.Core;

namespace Overskin.Linux.Platform;

/// <summary>
/// Records the screen to MP4, the way each desktop allows: ffmpeg reading X11 directly,
/// wf-recorder on Hyprland, Sway and other wlroots desktops, and on GNOME or KDE under Wayland
/// the desktop's screen-cast portal recorded with GStreamer. GIFs are made from the MP4 with ffmpeg.
/// </summary>
public sealed class Recorder
{
    private Process? _process;
    private Portal.ScreenCast? _cast;
    private readonly string _videoPath;
    public string OutputPath { get; }
    public bool Gif { get; }
    public string Method { get; private set; } = "";
    public DateTime Started { get; private set; }

    private Recorder(string outputPath, bool gif)
    {
        OutputPath = outputPath;
        Gif = gif;
        _videoPath = gif ? Path.Combine(Path.GetTempPath(), $"overskin-rec-{Environment.ProcessId}-{Guid.NewGuid():N}.mp4") : outputPath;
    }

    /// <summary>What's missing to record on this desktop, or null if it can.</summary>
    public static string? Missing(bool gif)
    {
        if (!OperatingSystem.IsLinux()) return "Recording is only built for Linux.";
        string? need = null;
        if (!ScreenGrabber.IsWayland) need = Need("ffmpeg");
        else if (ScreenGrabber.IsWlroots) need = Need("wf-recorder");
        else need = Need("gst-launch-1.0");
        if (need == null && gif) need = Need("ffmpeg");
        return need == null ? null
            : $"Recording here needs {need}. Install it with your package manager " +
              $"(Arch: sudo pacman -S {Package(need)} · Debian/Ubuntu: sudo apt install {Package(need, debian: true)} · Fedora: sudo dnf install {Package(need)}).";

        static string? Need(string tool) => ScreenGrabber.HasCommand(tool) ? null : tool;
        static string Package(string tool, bool debian = false) => tool switch
        {
            "gst-launch-1.0" => debian ? "gstreamer1.0-tools gstreamer1.0-pipewire gstreamer1.0-plugins-ugly" : "gstreamer gst-plugin-pipewire gst-plugins-ugly",
            _ => tool,
        };
    }

    /// <summary>
    /// Starts recording. <paramref name="area"/> is in the pixels of the screenshot the area was
    /// picked on (null for the whole screen); on GNOME and KDE under Wayland the desktop asks which
    /// screen or window instead.
    /// </summary>
    public static async Task<Recorder?> StartAsync(PixelRect? area, string outputPath, bool gif, bool pointer, int fps)
    {
        var r = new Recorder(outputPath, gif);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        if (area is { } a)
            area = new PixelRect(a.X, a.Y, a.Width / 2 * 2, a.Height / 2 * 2); // H.264 wants even sizes

        if (!ScreenGrabber.IsWayland)
        {
            var display = Environment.GetEnvironmentVariable("DISPLAY") ?? ":0";
            var where = area is { } x ? $"-video_size {x.Width}x{x.Height} -i {display}+{x.X},{x.Y}" : $"-i {display}";
            r.Start("ffmpeg", $"-hide_banner -loglevel error -y -f x11grab -framerate {fps} -draw_mouse {(pointer ? 1 : 0)} {where} " +
                              $"-c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p \"{r._videoPath}\"");
            r.Method = "ffmpeg (X11)";
        }
        else if (ScreenGrabber.IsWlroots)
        {
            var geometry = area is { } x ? await LayoutGeometryAsync(x) : null;
            r.Start("wf-recorder", $"{(geometry != null ? $"-g \"{geometry}\" " : "")}-r {fps} -c libx264 -p preset=veryfast -p crf=20 -x yuv420p -y -f \"{r._videoPath}\"");
            r.Method = "wf-recorder";
        }
        else
        {
            var cast = await Portal.StartScreenCastAsync(pointer);
            if (cast == null) return null;
            r._cast = cast;
            r.Start("gst-launch-1.0", $"-e pipewiresrc path={cast.Node} do-timestamp=true keepalive-time=1000 always-copy=true ! " +
                                      $"videoconvert ! videorate ! video/x-raw,framerate={fps}/1 ! videoconvert ! " +
                                      "x264enc speed-preset=veryfast tune=zerolatency qp-max=28 ! video/x-h264,profile=high ! " +
                                      $"mp4mux ! filesink location=\"{r._videoPath}\"");
            r.Method = "screen-cast portal + GStreamer";
        }
        r.Started = DateTime.Now;
        Log.Write($"record: started with {r.Method} to {r.OutputPath}");
        await Task.Delay(400);
        if (r._process is { HasExited: true } p)
        {
            var error = await p.StandardError.ReadToEndAsync();
            r._cast?.Dispose();
            throw new InvalidOperationException($"{r.Method} stopped straight away: {error.Trim()}");
        }
        return r;
    }

    private void Start(string tool, string args)
    {
        Log.Write($"record: {tool} {args}");
        _process = Process.Start(new ProcessStartInfo(tool, args)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        }) ?? throw new InvalidOperationException("couldn't start " + tool);
        _process.OutputDataReceived += (_, _) => { };
        _process.BeginOutputReadLine();
    }

    /// <summary>Ends the recording and finishes the file (and the GIF). Returns the file, or null if cancelled.</summary>
    public async Task<string?> StopAsync(bool keep, int gifFps, int gifWidth)
    {
        var p = _process;
        if (p != null && !p.HasExited)
        {
            // each tool finishes its file cleanly on its own signal: ffmpeg on "q", the others on Ctrl+C
            if (Method.StartsWith("ffmpeg"))
            {
                try { await p.StandardInput.WriteAsync("q"); await p.StandardInput.FlushAsync(); } catch { }
            }
            else Interrupt(p.Id);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } }
        }
        _cast?.Dispose();
        _cast = null;
        Log.Write($"record: stopped after {(DateTime.Now - Started).TotalSeconds:0.0}s, keep={keep}");

        if (!keep || !File.Exists(_videoPath) || new FileInfo(_videoPath).Length == 0)
        {
            try { File.Delete(_videoPath); } catch { }
            if (keep) throw new InvalidOperationException("The recording came out empty. Details are in " + Log.FilePath);
            return null;
        }
        if (!Gif) return OutputPath;

        try
        {
            await Converter.VideoToGifAsync(_videoPath, OutputPath, gifFps, gifWidth);
            return OutputPath;
        }
        finally
        {
            try { File.Delete(_videoPath); } catch { }
        }
    }

    private static void Interrupt(int pid)
    {
        try { Process.Start(new ProcessStartInfo("kill", $"-INT {pid}") { UseShellExecute = false })?.WaitForExit(2000); }
        catch { }
    }

    /// <summary>
    /// An area picked on grim's picture of the focused monitor, as wf-recorder's "x,y wxh" in the
    /// desktop's layout (logical pixels, offset by where that monitor sits).
    /// </summary>
    private static async Task<string?> LayoutGeometryAsync(PixelRect area)
    {
        double ox = 0, oy = 0, scale = 1;
        try
        {
            string? json = ScreenGrabber.HasCommand("hyprctl") ? await ScreenGrabber.OutputOf("hyprctl", "monitors -j")
                         : ScreenGrabber.HasCommand("swaymsg") ? await ScreenGrabber.OutputOf("swaymsg", "-t get_outputs -r") : null;
            if (json != null)
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                foreach (var m in doc.RootElement.EnumerateArray())
                {
                    if (!(m.TryGetProperty("focused", out var f) && f.ValueKind == System.Text.Json.JsonValueKind.True)) continue;
                    if (m.TryGetProperty("rect", out var rect)) // sway
                    {
                        ox = rect.GetProperty("x").GetDouble(); oy = rect.GetProperty("y").GetDouble();
                    }
                    else
                    {
                        ox = m.GetProperty("x").GetDouble(); oy = m.GetProperty("y").GetDouble();
                    }
                    if (m.TryGetProperty("scale", out var s)) scale = s.GetDouble();
                }
            }
        }
        catch (Exception ex) { Log.Write("record: monitor layout: " + ex.Message); }
        string N(double v) => Math.Round(v).ToString(CultureInfo.InvariantCulture);
        return $"{N(ox + area.X / scale)},{N(oy + area.Y / scale)} {N(area.Width / scale)}x{N(area.Height / scale)}";
    }
}
