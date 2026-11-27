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
/// <summary>How a recording is finished (as on Windows): the pointer, clicks, shortcuts, backdrop and zooms.</summary>
public sealed record RecordLook
{
    /// <summary>Overskin draws a sharp pointer that glides, instead of recording the system's.</summary>
    public bool SmoothPointer { get; init; } = true;
    public string PointerStyle { get; init; } = "White";
    public double PointerSize { get; init; } = 1.5;
    public int PointerSmoothing { get; init; } = 45;
    public int MotionBlur { get; init; } = 50;
    public bool ClickRings { get; init; } = true;
    public bool ShowKeys { get; init; }
    public string Backdrop { get; init; } = "None";
    /// <summary>The plain recording is kept so the zooms can be changed in the video editor.</summary>
    public bool EditableZooms { get; init; } = true;
    public double ZoomRate { get; init; } = 1.6 * Math.Sqrt(7);
    public double PanRate { get; init; } = 12 * Math.Sqrt(0.12);
    public double ZoomHold { get; init; } = ZoomTrack.Hold;
    public string WebcamCorner { get; init; } = "BottomRight";
    public string WebcamShape { get; init; } = "Circle";
    public int WebcamSize { get; init; } = 24;
    public string Microphone { get; init; } = "";
    public string Camera { get; init; } = "";
}

public sealed class Recorder
{
    private Process? _process;
    private Portal.ScreenCast? _cast;
    private PointerTrack? _track;
    private PixelRect _trackArea;
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
        if (Mac.IsMac)
            return gif && !ScreenGrabber.HasCommand("ffmpeg")
                ? "GIFs are made with ffmpeg. Install it with Homebrew (brew install ffmpeg), then try again. Videos work without it."
                : null;
        if (!OperatingSystem.IsLinux()) return "Recording is only built for Linux and macOS.";
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
    public static async Task<Recorder?> StartAsync(PixelRect? area, string outputPath, bool gif, bool pointer, int fps, PixelRect? whole = null, double autoZoom = 0, bool dim = false,
        bool mic = false, bool systemSound = false, bool webcam = false, RecordLook? look = null)
    {
        var r = new Recorder(outputPath, gif);
        look ??= new RecordLook { SmoothPointer = false, ClickRings = false };
        r._look = look;
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        if (area is { } a)
            area = new PixelRect(a.X, a.Y, a.Width / 2 * 2, a.Height / 2 * 2); // H.264 wants even sizes

        // where the pointer goes, for zooming, dimming, drawing a smooth pointer and click rings:
        // followed from the start, so whether the system pointer is recorded can depend on it
        r._wall.Start();
        bool wantTrack = autoZoom > 1 || dim || (pointer && look.SmoothPointer) || look.ClickRings;
        if (wantTrack && (area ?? whole) is { } followed && ScreenGrabber.HasCommand("ffmpeg"))
        {
            r._track = await PointerTrack.StartAsync();
            r._trackArea = followed;
            if (r._track == null) Log.Write("record: the desktop doesn't say where the pointer is: no zoom, smooth pointer or click rings");
        }
        // Overskin's own pointer, drawn afterwards, takes the place of the system's
        r._ownPointer = pointer && r._track != null && (look.SmoothPointer || autoZoom > 1);
        pointer = pointer && !r._ownPointer;
        r._pointScale = Mac.IsMac ? Math.Max(1, Mac.Shot.Scale) : 1;
        if (look.ShowKeys)
        {
            if (MacKeys.Allowed(ask: true)) r._keys = new MacKeys(r._wall.Elapsed.TotalSeconds);
            else Desktop.Notify("Keys can't be shown", Mac.IsMac
                ? "macOS needs Input Monitoring for Overskin: System Settings › Privacy & Security › Input Monitoring."
                : "Showing shortcuts in recordings works on a Mac.", App.IconFile);
        }

        if (Mac.IsMac && ScreenGrabber.HasCommand("ffmpeg"))
        {
            // with ffmpeg: the screen, webcam and sound together, through macOS's own capture (AVFoundation)
            if (!Mac.CanReadScreen(ask: true))
                throw new InvalidOperationException("Overskin needs permission to record the screen. Open System Settings › Privacy & Security › " +
                                                    "Screen Recording, switch Overskin on, then quit Overskin and open it again.");
            var list = await Devices.MacAsync();
            int display = Math.Max(0, Mac.Displays().FindIndex(d => d.X == Mac.Shot.Display.X && d.Y == Mac.Shot.Display.Y));
            var screen = Devices.Screen(list, display) ?? throw new InvalidOperationException("ffmpeg can't see the screen. Check Screen Recording for Overskin in System Settings.");
            var inputs = $"-f avfoundation -framerate {fps} -capture_cursor {(pointer ? 1 : 0)} -i \"{screen.Index}:none\"";
            var sounds = new List<string>();
            if (systemSound)
            {
                if (Devices.Loopback(list) is { } loop) sounds.Add($"-f avfoundation -i \":{loop.Index}\"");
                else Desktop.Notify("No computer sound", "macOS needs a loopback device such as BlackHole (free) to record what the Mac plays. Recording without it.", App.IconFile);
            }
            if (mic && Devices.Microphone(list, look.Microphone) is { } m) sounds.Add($"-f avfoundation -i \":{m.Index}\"");
            var camera = webcam ? Devices.Camera(list, look.Camera) : null;
            if (webcam && camera == null) Desktop.Notify("No webcam found", "Recording without the webcam.", App.IconFile);
            r.StartFfmpeg(inputs, camera != null ? $"-f avfoundation -framerate 30 -i \"{camera.Index}:none\"" : null, sounds, area, fps, whole?.Height ?? 1080);
            r.Method = "ffmpeg (macOS)";
        }
        else if (Mac.IsMac)
        {
            // macOS records with its own screencapture tool, to a .mov that's made into an MP4 when it stops
            if (!Mac.CanReadScreen(ask: true))
                throw new InvalidOperationException("Overskin needs permission to record the screen. Open System Settings › Privacy & Security › " +
                                                    "Screen Recording, switch Overskin on, then quit Overskin and open it again.");
            r._movPath = Path.Combine(Path.GetTempPath(), $"overskin-rec-{Environment.ProcessId}-{Guid.NewGuid():N}.mov");
            var d = Mac.Shot.Display;
            var (x, y, w, h) = area is { } a2
                ? (d.X + a2.X / Mac.Shot.Scale, d.Y + a2.Y / Mac.Shot.Scale, a2.Width / Mac.Shot.Scale, a2.Height / Mac.Shot.Scale)
                : (d.X, d.Y, d.Width, d.Height);
            string N(double v) => Math.Round(v).ToString(CultureInfo.InvariantCulture);
            // -g: the microphone (screencapture can't record what the Mac plays)
            r.Start("/usr/sbin/screencapture", $"-v {(pointer ? "-C " : "")}{(mic ? "-g " : "")}-R {N(x)},{N(y)},{N(w)},{N(h)} \"{r._movPath}\"");
            r.Method = "screencapture (macOS)";
        }
        else if (!ScreenGrabber.IsWayland)
        {
            var display = Environment.GetEnvironmentVariable("DISPLAY") ?? ":0";
            var where = area is { } x ? $"-video_size {x.Width}x{x.Height} -i {display}+{x.X},{x.Y}" : $"-i {display}";
            var sounds = new List<string>();
            // PulseAudio (or PipeWire's stand-in): the speakers' monitor, and the microphone
            if (systemSound) sounds.Add("-f pulse -i @DEFAULT_MONITOR@");
            if (mic) sounds.Add("-f pulse -i default");
            string? camera = webcam && File.Exists("/dev/video0") ? "-f v4l2 -i /dev/video0" : null;
            r.StartFfmpeg($"-f x11grab -framerate {fps} -draw_mouse {(pointer ? 1 : 0)} {where}", camera, sounds, null, fps, area?.Height ?? whole?.Height ?? 1080);
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
        r._dim = dim;
        r._zoom = autoZoom;
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

    private double _zoom;
    private bool _dim;
    private RecordLook _look = new();
    private bool _ownPointer;
    private double _pointScale = 1;
    private MacKeys? _keys;

    // pausing (as on Windows): ffmpeg is stopped and started again into a new piece, and the pieces
    // are joined at the end; the pointer's and the keys' times skip the paused stretches
    private string? _ffmpegArgs;
    private readonly List<string> _segments = new();
    private readonly List<(double From, double To)> _pauses = new();
    private readonly Stopwatch _wall = new();
    private double _firstFrame, _pausedAt;
    private const string Out = "\u0001OUT\u0001";

    /// <summary>Whether this recording can be paused (it's made with ffmpeg).</summary>
    public bool CanPause => _ffmpegArgs != null;
    public bool IsPaused { get; private set; }

    /// <summary>Pauses the recording, or carries on. False when this kind of recording can't pause.</summary>
    public async Task<bool> TogglePauseAsync()
    {
        if (!CanPause) return false;
        if (!IsPaused)
        {
            await EndProcessAsync();
            _pausedAt = _wall.Elapsed.TotalSeconds;
            IsPaused = true;
            Log.Write($"record: paused at {_pausedAt:0.0}s");
        }
        else
        {
            // the new piece starts a moment after ffmpeg does
            _pauses.Add((_pausedAt, _wall.Elapsed.TotalSeconds + 0.25));
            StartSegment();
            IsPaused = false;
            Log.Write("record: carrying on");
        }
        return true;
    }

    /// <summary>Where a moment of the finished video was on the clock the pointer was followed by.</summary>
    private double TrackTime(double videoT)
    {
        double wall = _firstFrame + videoT;
        foreach (var (from, to) in _pauses)
            if (wall >= from) wall += to - from;
        return wall;
    }

    /// <summary>A moment on that clock in the finished video, or null when it was paused then.</summary>
    private double? VideoTime(double wall)
    {
        double paused = 0;
        foreach (var (from, to) in _pauses)
        {
            if (wall >= to) paused += to - from;
            else if (wall >= from) return null;
        }
        double t = wall - _firstFrame - paused;
        return t >= 0 ? t : null;
    }

    private void StartSegment()
    {
        var part = _videoPath + $".part{_segments.Count}.mp4";
        _segments.Add(part);
        Start("ffmpeg", _ffmpegArgs!.Replace(Out, part));
    }

    // for the self-test
    internal static Recorder ForTest(string video) => new(video, gif: false);
    internal void PauseForTest(double firstFrame, params (double From, double To)[] pauses) { _firstFrame = firstFrame; _pauses.AddRange(pauses); }
    internal double TrackTimeForTest(double t) => TrackTime(t);
    internal double? VideoTimeForTest(double wall) => VideoTime(wall);
    internal Task JoinForTest(IEnumerable<string> parts) { _segments.AddRange(parts); return JoinSegmentsAsync(keep: true); }

    /// <summary>Stops the recording tool and waits for it to finish its file.</summary>
    private async Task EndProcessAsync()
    {
        var p = _process;
        if (p == null || p.HasExited) return;
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
    /// <summary>On a Mac, where screencapture records before it's made into the MP4.</summary>
    private string? _movPath;

    /// <summary>
    /// One ffmpeg recording: the screen (cropped to <paramref name="crop"/> when given), the webcam
    /// in a round bubble in the bottom-right corner, and the sound sources mixed together.
    /// </summary>
    private void StartFfmpeg(string screenInput, string? cameraInput, List<string> sounds, PixelRect? crop, int fps, int height)
    {
        _ffmpegArgs = "-hide_banner -loglevel error -y " + FfmpegArgs(screenInput, cameraInput, sounds, crop, fps, Out, height, _look.WebcamCorner, _look.WebcamShape, _look.WebcamSize);
        _firstFrame = _wall.Elapsed.TotalSeconds + 0.25;
        StartSegment();
    }

    /// <summary>The ffmpeg command line for a recording (for tests too).</summary>
    /// <param name="height">How tall the recording is (pixels), for the size of the webcam bubble.</param>
    public static string FfmpegArgs(string screenInput, string? cameraInput, IReadOnlyList<string> sounds, PixelRect? crop, int fps, string output, int height = 1080,
        string corner = "BottomRight", string shape = "Circle", int sizePercent = 25)
    {
        var inputs = new List<string> { screenInput };
        var filters = new List<string>();
        string video = "[0:v]";
        var steps = new List<string>();
        if (crop is { } c) steps.Add($"crop={c.Width}:{c.Height}:{c.X}:{c.Y}");
        steps.Add($"fps={fps}");
        filters.Add($"{video}{string.Join(",", steps)}[scr]");
        video = "[scr]";
        int next = 1;
        if (cameraInput != null)
        {
            inputs.Add(cameraInput);
            // a round bubble a quarter of the height, in the bottom-right corner
            int bubble = Math.Max(96, (crop?.Height ?? height) * Math.Clamp(sizePercent, 12, 45) / 100 / 2 * 2);
            // a circle, or a square with rounded corners
            string alpha = shape == "Rounded"
                ? "if(gt(abs(X-W/2),W/2-W*0.18)*gt(abs(Y-H/2),H/2-H*0.18),if(lt(hypot(abs(X-W/2)-(W/2-W*0.18),abs(Y-H/2)-(H/2-H*0.18)),W*0.18),255,0),255)"
                : "if(lt(hypot(X-W/2,Y-H/2),W/2-1),255,0)";
            filters.Add($"[{next}:v]crop='min(iw,ih)':'min(iw,ih)',scale={bubble}:{bubble},format=rgba," +
                        $"geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='{alpha}'[cam]");
            string where = corner switch { "TopLeft" => "24:24", "TopRight" => "W-w-24:24", "BottomLeft" => "24:H-h-24", _ => "W-w-24:H-h-24" };
            filters.Add($"{video}[cam]overlay={where}[withcam]");
            video = "[withcam]";
            next++;
        }
        var (soundInputs, mix, label) = Devices.SoundInputs(sounds, next);
        if (soundInputs.Length > 0) inputs.Add(soundInputs);
        if (mix.Length > 0) filters.Add(mix);
        var args = string.Join(" ", inputs) + $" -filter_complex \"{string.Join(";", filters)}\" -map \"{video}\"";
        if (label != null) args += $" -map \"{label}\" -c:a aac -b:a 160k";
        return args + $" -c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p \"{output}\"";
    }

    private void Start(string tool, string args)
    {
        Log.Write($"record: {tool} {args}");
        _process = Process.Start(new ProcessStartInfo(tool, args)
        {
            UseShellExecute = false, CreateNoWindow = true,
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
        if (!IsPaused) await EndProcessAsync();
        if (_segments.Count > 0) await JoinSegmentsAsync(keep);
        _cast?.Dispose();
        _cast = null;
        _track?.Stop();
        _keys?.Dispose();
        Log.Write($"record: stopped after {(DateTime.Now - Started).TotalSeconds:0.0}s, keep={keep}");
        if (_movPath != null)
        {
            try
            {
                if (keep && File.Exists(_movPath) && new FileInfo(_movPath).Length > 0) await MovToMp4Async(_movPath, _videoPath);
            }
            finally { try { File.Delete(_movPath); } catch { } }
        }

        if (!keep || !File.Exists(_videoPath) || new FileInfo(_videoPath).Length == 0)
        {
            try { File.Delete(_videoPath); } catch { }
            if (keep) throw new InvalidOperationException("The recording came out empty. Details are in " + Log.FilePath);
            return null;
        }
        if (_track != null && keep && _dim)
        {
            // dimmed first, on the plain recording, so auto-zoom then zooms the dimmed picture
            try { await AutoZoom.DimAsync(_videoPath, _track, _trackArea, 0.25, CanPause ? TrackTime : null); }
            catch (Exception ex) { Log.Write("dimming failed, keeping the plain recording: " + ex.Message); }
        }
        if (keep && (_track != null || _keys != null || _look.Backdrop != "None"))
        {
            try
            {
                var finish = new RecordFinish
                {
                    Zoom = _track != null ? _zoom : 0, DrawPointer = _ownPointer, PointScale = _pointScale, Look = _look,
                    Keys = _keys?.Pressed.Select(k => (Time: CanPause ? VideoTime(k.T) : k.T - 0.25, k.Label)).Where(k => k.Time is >= 0)
                        .Select(k => new KeyPress { T = k.Time!.Value, Label = k.Label }).ToList() ?? new(),
                    Editable = _look.EditableZooms && !Gif && _zoom > 1 && _track != null,
                };
                if (finish.Zoom > 1) Desktop.Notify("Adding the auto-zoom…", "This takes a little while for long recordings.", App.IconFile);
                await AutoZoom.FinishAsync(_videoPath, _track, _trackArea, 0.25, finish, CanPause ? TrackTime : null);
            }
            catch (Exception ex) { Log.Write("finishing the recording failed, keeping it as it was: " + ex.Message); }
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

    /// <summary>The pieces recorded between pauses, joined into the one video (or the one piece, moved into place).</summary>
    private async Task JoinSegmentsAsync(bool keep)
    {
        var parts = _segments.Where(s => File.Exists(s) && new FileInfo(s).Length > 0).ToList();
        try
        {
            if (!keep || parts.Count == 0) return;
            if (parts.Count == 1) { File.Move(parts[0], _videoPath, overwrite: true); return; }
            var list = _videoPath + ".parts.txt";
            await File.WriteAllLinesAsync(list, parts.Select(s => $"file '{s.Replace("'", "'\\''")}'"));
            var (ok, error) = await ScreenGrabber.RunAsync("ffmpeg", $"-hide_banner -loglevel error -y -f concat -safe 0 -i \"{list}\" -c copy -movflags +faststart \"{_videoPath}\"", 600);
            try { File.Delete(list); } catch { }
            if (!ok) throw new InvalidOperationException("Couldn't join the parts of the recording: " + error);
        }
        finally
        {
            foreach (var s in _segments) try { if (File.Exists(s)) File.Delete(s); } catch { }
        }
    }

    /// <summary>
    /// The Mac's recording as an MP4: with ffmpeg when it's installed, otherwise with avconvert,
    /// which comes with macOS. If neither manages it, the recording is kept as it came (QuickTime).
    /// </summary>
    private static async Task MovToMp4Async(string mov, string mp4)
    {
        if (ScreenGrabber.HasCommand("ffmpeg"))
        {
            var (ok, error) = await ScreenGrabber.RunAsync("ffmpeg", $"-hide_banner -loglevel error -y -i \"{mov}\" -c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p -c:a aac -movflags +faststart \"{mp4}\"", 900);
            if (ok && File.Exists(mp4)) return;
            Log.Write("record: ffmpeg couldn't make the MP4: " + error);
        }
        var (done, why) = await ScreenGrabber.RunAsync("/usr/bin/avconvert", $"--source \"{mov}\" --output \"{mp4}\" --preset PresetHighestQuality --replace", 900);
        if (done && File.Exists(mp4)) return;
        Log.Write("record: avconvert couldn't make the MP4: " + why);
        File.Copy(mov, Path.ChangeExtension(mp4, ".mov"), overwrite: true);
        throw new InvalidOperationException("The recording was saved as a QuickTime movie (.mov) beside where the MP4 would be: macOS couldn't make it into an MP4.");
    }

    private static void Interrupt(int pid)
    {
        try { Process.Start(new ProcessStartInfo("kill", $"-INT {pid}") { UseShellExecute = false, CreateNoWindow = true })?.WaitForExit(2000); }
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
