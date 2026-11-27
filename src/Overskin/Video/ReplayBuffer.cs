using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using static Overskin.NativeMethods;

namespace Overskin;

/// <summary>
/// Instant replay: the screen is recorded all the time in the background, in short pieces kept
/// on disk (only the newest few), so a hotkey can save the last 30 seconds (or however long was
/// chosen) as a video after something happens, like the replay in game overlays.
/// </summary>
public sealed class ReplayBuffer : IDisposable
{
    private const int Fps = 30;
    /// <summary>Biggest frame kept: the screen is scaled down to this, which keeps the PC's load low.</summary>
    private const int MaxWidth = 1920, MaxHeight = 1080;

    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Overskin", "replay");

    private readonly Int32Rect _area;
    private readonly int _seconds;
    private readonly bool _sound;
    private readonly Thread _thread;
    private volatile bool _stop;
    private volatile bool _cutNow;
    private readonly object _lock = new();
    /// <summary>Finished pieces, oldest first: the file and how long it is.</summary>
    private readonly List<(string Path, double Seconds)> _pieces = new();
    private TaskCompletionSource? _cut;
    private int _counter;

    public bool Running => !_stop;
    public int Seconds => _seconds;
    /// <summary>Why it stopped by itself, if it did.</summary>
    public string? Problem { get; private set; }

    private ReplayBuffer(Int32Rect area, int seconds, bool sound)
    {
        _area = area;
        _seconds = seconds;
        _sound = sound;
        try { if (Directory.Exists(Folder)) foreach (var f in Directory.GetFiles(Folder, "piece-*.mp4")) File.Delete(f); } catch { }
        Directory.CreateDirectory(Folder);
        _thread = new Thread(Run) { IsBackground = true, Name = "Overskin instant replay", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>Starts keeping the last <paramref name="seconds"/> of the main screen.</summary>
    public static ReplayBuffer Start(int seconds, bool sound)
    {
        var screen = MonitorBounds(new POINT(0, 0)); // the main screen holds the point (0, 0)
        return new ReplayBuffer(new Int32Rect(screen.Left, screen.Top, screen.Width, screen.Height), seconds, sound);
    }

    /// <summary>Each piece is about a third of what's kept, so throwing the oldest away loses little.</summary>
    private double PieceSeconds => Math.Clamp(_seconds / 3.0, 5, 20);

    private void Run()
    {
        double fit = Math.Min(1, Math.Min(MaxWidth / (double)_area.Width, MaxHeight / (double)_area.Height));
        int w = Math.Max(2, (int)(_area.Width * fit) & ~1), h = Math.Max(2, (int)(_area.Height * fit) & ~1);
        IFrameGrabber? grabber = null;
        AudioSource? speakers = null;
        var pointers = new SystemPointers();
        var encodings = new List<Task>();
        bool highRes = timeBeginPeriod(1) == 0;
        try
        {
            grabber = FrameGrabbers.For(_area, w, h);
            if (_sound)
            {
                try { speakers = AudioSource.Open(loopback: true); }
                catch { speakers = null; } // no sound device: pictures only
            }
            int pointerSize = Math.Max(16, (int)Math.Round(32 * fit));
            while (!_stop)
            {
                // one piece: its own file, clock and sound
                string path = Path.Combine(Folder, $"piece-{++_counter:D5}.mp4");
                var queue = new BlockingCollection<VideoFrame>(boundedCapacity: 6);
                var clock = Stopwatch.StartNew();
                BlockingCollection<AudioChunk>? sound = null;
                AudioMixer? mixer = null;
                if (speakers != null)
                {
                    sound = new BlockingCollection<AudioChunk>(boundedCapacity: 400);
                    mixer = new AudioMixer(clock, new[] { (speakers, 1f) }, sound);
                }
                var encoding = Task.Run(() => VideoEncoder.EncodeAsync(path, w, h, Fps, queue, highQuality: false, sound));
                int n = 0;
                while (!_stop && !_cutNow && clock.Elapsed.TotalSeconds < PieceSeconds && !encoding.IsCompleted)
                {
                    long due = n * 1000L / Fps, now = clock.ElapsedMilliseconds;
                    if (now < due) { Thread.Sleep((int)Math.Min(due - now, 5)); continue; }
                    GetCursorPos(out var p);
                    double px = (p.X - _area.X) * fit, py = (p.Y - _area.Y) * fit;
                    bool inside = p.X >= _area.X && p.Y >= _area.Y && p.X < _area.X + _area.Width && p.Y < _area.Y + _area.Height;
                    byte[] pixels;
                    try
                    {
                        pixels = grabber.Grab(_area.X, _area.Y, _area.Width, _area.Height, dc => { if (inside) pointers.Draw(dc, px, py, pointerSize); });
                    }
                    catch when (grabber is not ScaledGrabber)
                    {
                        // the fast way stopped (a driver reset, HDR switched on): carry on the old way
                        grabber.Dispose();
                        grabber = new ScaledGrabber(w, h);
                        continue;
                    }
                    queue.TryAdd(new VideoFrame(pixels, clock.Elapsed));
                    n = (int)(clock.ElapsedMilliseconds * Fps / 1000) + 1; // skip ahead rather than fall behind
                }
                double length = clock.Elapsed.TotalSeconds;
                // the piece is finished in the background while the next one starts recording
                mixer?.Finish(); // not Dispose: the speakers carry on into the next piece
                queue.CompleteAdding();
                var cut = _cut;
                _cutNow = false;
                encodings.RemoveAll(t => t.IsCompleted);
                var finishing = encoding.ContinueWith(t =>
                {
                    lock (_lock)
                    {
                        if (t.IsCompletedSuccessfully && File.Exists(path)) _pieces.Add((path, length));
                        else try { File.Delete(path); } catch { }
                        // keep just enough to cover what's wanted, plus the piece being written
                        double kept = 0;
                        for (int i = _pieces.Count - 1; i >= 0; i--)
                        {
                            if (kept >= _seconds)
                            {
                                try { File.Delete(_pieces[i].Path); } catch { }
                                _pieces.RemoveAt(i);
                                continue;
                            }
                            kept += _pieces[i].Seconds;
                        }
                    }
                    cut?.TrySetResult();
                }, TaskScheduler.Default);
                encodings.Add(finishing);
            }
        }
        catch (Exception ex)
        {
            Problem = ex.Message;
            _stop = true;
        }
        finally
        {
            if (highRes) timeEndPeriod(1);
            grabber?.Dispose();
            pointers.Dispose();
            try { Task.WaitAll(encodings.ToArray(), 10000); } catch { }
            speakers?.Dispose();
            _cut?.TrySetResult();
        }
    }

    /// <summary>
    /// Saves what's kept (up to the last <see cref="Seconds"/>) as one video at <paramref name="output"/>.
    /// The piece being recorded is finished first, so it runs right up to now.
    /// </summary>
    public async Task SaveAsync(string output)
    {
        if (_stop) throw new InvalidOperationException(Problem ?? "Instant replay isn't running.");
        var cut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _cut = cut;
        _cutNow = true;
        await Task.WhenAny(cut.Task, Task.Delay(15000));
        _cut = null;
        List<(string Path, double Seconds)> pieces;
        lock (_lock) pieces = _pieces.ToList();
        if (pieces.Count == 0) throw new InvalidOperationException("Nothing has been recorded yet.");

        // the newest pieces covering the length wanted; the oldest one trimmed at its start
        var used = new List<(string Path, double Seconds)>();
        double total = 0;
        for (int i = pieces.Count - 1; i >= 0 && total < _seconds; i--)
        {
            used.Insert(0, pieces[i]);
            total += pieces[i].Seconds;
        }
        // copied first, so recording can carry on throwing old pieces away meanwhile
        var copies = new List<string>();
        foreach (var (path, _) in used)
        {
            string copy = Path.Combine(Folder, "saving-" + Path.GetFileName(path));
            File.Copy(path, copy, overwrite: true);
            copies.Add(copy);
        }
        try
        {
            var composition = new MediaComposition();
            for (int i = 0; i < copies.Count; i++)
            {
                var clip = await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(copies[i]));
                if (i == 0 && total > _seconds)
                    clip.TrimTimeFromStart = TimeSpan.FromSeconds(Math.Min(total - _seconds, clip.OriginalDuration.TotalSeconds - 0.5));
                composition.Clips.Add(clip);
            }
            var profile = await MediaEncodingProfile.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(copies[^1]));
            if (profile.Video != null) profile.Video.Bitrate = Math.Max(profile.Video.Bitrate, 6_000_000);
            string folder = Path.GetDirectoryName(Path.GetFullPath(output))!;
            Directory.CreateDirectory(folder);
            var target = await (await StorageFolder.GetFolderFromPathAsync(folder)).CreateFileAsync(Path.GetFileName(output), CreationCollisionOption.ReplaceExisting);
            var result = await composition.RenderToFileAsync(target, MediaTrimmingPreference.Precise, profile);
            if (result != TranscodeFailureReason.None)
            {
                try { await target.DeleteAsync(); } catch { }
                throw new InvalidOperationException("Windows couldn't save the replay (" + result + ").");
            }
        }
        finally
        {
            foreach (var c in copies) try { File.Delete(c); } catch { }
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(15000);
        lock (_lock)
        {
            foreach (var (path, _) in _pieces) try { File.Delete(path); } catch { }
            _pieces.Clear();
        }
    }
}
