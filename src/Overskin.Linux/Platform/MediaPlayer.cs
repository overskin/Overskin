using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Overskin.Linux.Core;

namespace Overskin.Linux.Platform;

/// <summary>
/// Plays a video or an animated GIF in Overskin's own windows (the viewer), with ffmpeg: one ffmpeg
/// reads the frames at the video's own pace and they're shown as they come; the sound plays
/// alongside through ffplay (which comes with ffmpeg). Pausing stops both and remembers where;
/// playing or moving to another time starts them again from there. GIFs go round and round.
/// </summary>
public sealed class MediaPlayer : IDisposable
{
    public string Path { get; }
    /// <summary>The video's own size, in pixels.</summary>
    public PixelSize Size { get; }
    public double Duration { get; }
    public bool HasSound { get; }
    /// <summary>GIFs start again when they end.</summary>
    public bool Loop { get; }
    /// <summary>The frame being shown (a smaller copy for big videos), or null before the first.</summary>
    public WriteableBitmap? Frame { get; private set; }
    public bool Playing { get; private set; }
    public double Volume { get; private set; } = 0.8;
    public bool Muted { get; private set; }

    /// <summary>A new frame is showing (on the interface's thread).</summary>
    public event Action? FrameShown;
    /// <summary>It played to the end (not for GIFs, which loop).</summary>
    public event Action? Ended;

    private readonly PixelSize _decode;
    private double _from;
    private readonly Stopwatch _clock = new();
    private Process? _video, _audio;
    private int _run;
    private bool _disposed;
    private byte[]? _pending;
    private readonly object _gate = new();

    private MediaPlayer(string path, PixelSize size, double duration, bool sound, bool loop)
    {
        Path = path;
        Size = size;
        Duration = duration;
        HasSound = sound;
        Loop = loop;
        // big videos are shown from a smaller copy: plenty for a window, and far less to move per frame
        double scale = Math.Min(1, 1600.0 / Math.Max(1, size.Width));
        _decode = new PixelSize(Math.Max(2, (int)(size.Width * scale) / 2 * 2), Math.Max(2, (int)(size.Height * scale) / 2 * 2));
    }

    /// <summary>Where it's up to, in seconds.</summary>
    public double Position => Math.Clamp(_from + (Playing ? _clock.Elapsed.TotalSeconds : 0), 0, Math.Max(0, Duration));

    /// <summary>Whether sound can be played here (ffplay).</summary>
    public static bool CanPlaySound => ScreenGrabber.HasCommand("ffplay");

    /// <summary>Reads what the file is (size, length, whether it has sound); null when ffmpeg can't.</summary>
    public static async Task<MediaPlayer?> OpenAsync(string path)
    {
        if (!ScreenGrabber.HasCommand("ffmpeg") || !ScreenGrabber.HasCommand("ffprobe")) return null;
        var probe = await ScreenGrabber.OutputOf("ffprobe", $"-v error -show_entries stream=codec_type,width,height:format=duration -of csv=p=0 \"{path}\"");
        if (probe == null) return null;
        int w = 0, h = 0;
        double duration = 0;
        bool sound = false;
        foreach (var line in probe.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(',');
            if (parts[0] == "video" && parts.Length >= 3 && w == 0) { int.TryParse(parts[1], out w); int.TryParse(parts[2], out h); }
            else if (parts[0] == "audio") sound = true;
            else if (parts.Length == 1 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) duration = d;
        }
        if (w <= 0 || h <= 0) return null;
        bool gif = System.IO.Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase);
        return new MediaPlayer(path, new PixelSize(w, h), duration, sound && !gif, gif);
    }

    public void Play()
    {
        if (Playing || _disposed) return;
        if (!Loop && Position >= Duration - 0.05) _from = 0; // at the end: from the start again
        Start(_from);
    }

    public void Pause()
    {
        if (!Playing) return;
        _from = Position;
        StopProcesses();
        Playing = false;
        _clock.Reset();
    }

    /// <summary>Moves to a time: carries on playing from there if it was playing, otherwise shows that frame.</summary>
    public void Seek(double seconds)
    {
        seconds = Math.Clamp(seconds, 0, Math.Max(0, Duration - 0.01));
        if (Playing) Start(seconds);
        else
        {
            _from = seconds;
            _ = ShowFrameAtAsync(seconds);
        }
    }

    public void SetVolume(double volume, bool muted)
    {
        Volume = Math.Clamp(volume, 0, 1);
        Muted = muted;
        // ffplay's loudness is set when it starts: start the sound again from where the picture is
        if (Playing && HasSound) StartSound(Position, _run);
    }

    /// <summary>Shows the first frame without playing (for a paused start, and tests).</summary>
    public Task ShowFirstFrameAsync() => ShowFrameAtAsync(_from);

    private void Start(double from)
    {
        StopProcesses();
        _from = from;
        int run = ++_run;
        Playing = true;
        _clock.Restart();
        StartPictures(from, run);
        if (HasSound) StartSound(from, run);
    }

    private string Seconds(double s) => s.ToString("0.###", CultureInfo.InvariantCulture);

    private void StartPictures(double from, int run)
    {
        var p = Process.Start(new ProcessStartInfo("ffmpeg",
            $"-hide_banner -loglevel error -nostdin -re {(from > 0 ? $"-ss {Seconds(from)} " : "")}-i \"{Path}\" -an -vf \"scale={_decode.Width}:{_decode.Height}\" -pix_fmt bgra -f rawvideo -")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        });
        if (p == null) { Playing = false; return; }
        _video = p;
        _ = p.StandardError.ReadToEndAsync();
        int frameBytes = _decode.Width * _decode.Height * 4;
        _ = Task.Run(async () =>
        {
            var stream = p.StandardOutput.BaseStream;
            var buffer = new byte[frameBytes];
            try
            {
                while (true)
                {
                    int got = 0;
                    while (got < frameBytes)
                    {
                        int n = await stream.ReadAsync(buffer.AsMemory(got, frameBytes - got));
                        if (n <= 0) break;
                        got += n;
                    }
                    if (got < frameBytes) break;
                    if (run != _run) return;
                    Hand(buffer);
                }
            }
            catch { return; }
            // the end of the file (or it was stopped)
            if (run != _run || _disposed) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (run != _run || _disposed) return;
                if (Loop) { Start(0); return; }
                Playing = false;
                _clock.Reset();
                _from = Duration;
                StopProcesses();
                Ended?.Invoke();
            });
        });
    }

    private void StartSound(double from, int run)
    {
        try { if (_audio is { HasExited: false }) _audio.Kill(); } catch { }
        _audio = null;
        if (Muted || Volume <= 0.001 || !CanPlaySound || run != _run) return;
        _audio = Process.Start(new ProcessStartInfo("ffplay",
            $"-hide_banner -loglevel quiet -nodisp -autoexit -vn {(from > 0 ? $"-ss {Seconds(from)} " : "")}-volume {(int)Math.Round(Volume * 100)} \"{Path}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        });
    }

    /// <summary>Takes a frame from the reader; only the newest waits to be shown, so a busy window drops frames rather than falling behind.</summary>
    private void Hand(byte[] frame)
    {
        bool post;
        lock (_gate)
        {
            post = _pending == null;
            _pending ??= new byte[frame.Length];
            Buffer.BlockCopy(frame, 0, _pending, 0, frame.Length);
        }
        if (post) Dispatcher.UIThread.Post(ShowPending, DispatcherPriority.Render);
    }

    private void ShowPending()
    {
        byte[]? frame;
        lock (_gate) { frame = _pending; _pending = null; }
        if (frame == null || _disposed) return;
        Frame ??= new WriteableBitmap(_decode, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = Frame.Lock())
        {
            int stride = _decode.Width * 4;
            for (int y = 0; y < _decode.Height; y++) Marshal.Copy(frame, y * stride, fb.Address + y * fb.RowBytes, stride);
        }
        FrameShown?.Invoke();
    }

    private int _still;

    /// <summary>One frame at a time, for scrubbing while paused (only the newest asked for is shown).</summary>
    private async Task ShowFrameAtAsync(double at)
    {
        int ask = ++_still;
        var (ok, bytes) = await Task.Run(async () =>
        {
            using var p = Process.Start(new ProcessStartInfo("ffmpeg",
                $"-hide_banner -loglevel error -nostdin {(at > 0 ? $"-ss {Seconds(at)} " : "")}-i \"{Path}\" -frames:v 1 -an -vf \"scale={_decode.Width}:{_decode.Height}\" -pix_fmt bgra -f rawvideo -")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            });
            if (p == null) return (false, Array.Empty<byte>());
            _ = p.StandardError.ReadToEndAsync();
            using var ms = new MemoryStream();
            await p.StandardOutput.BaseStream.CopyToAsync(ms);
            await p.WaitForExitAsync();
            return (ms.Length >= _decode.Width * _decode.Height * 4, ms.ToArray());
        });
        if (!ok || ask != _still || Playing || _disposed) return;
        Hand(bytes);
    }

    private void StopProcesses()
    {
        _run++;
        foreach (var p in new[] { _video, _audio })
        {
            try { if (p is { HasExited: false }) p.Kill(); } catch { }
            p?.Dispose();
        }
        _video = _audio = null;
    }

    public void Dispose()
    {
        _disposed = true;
        Playing = false;
        StopProcesses();
    }
}
