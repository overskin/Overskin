using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using static Overskin.NativeMethods;

namespace Overskin;

/// <summary>
/// Records a screen area to MP4. In auto-zoom mode each frame is cut from the area by the
/// <see cref="SmartCamera"/>, the pointer is drawn larger at its smoothed position, and clicks
/// get a ring, all while recording, so the file is ready the moment you stop.
/// </summary>
public sealed class VideoRecorder : IRecorder
{
    public const int Fps = 30;
    /// <summary>Biggest frame the H.264 encoder takes everywhere; larger areas are scaled down.</summary>
    private const int MaxWidth = 3840, MaxHeight = 2160;

    private readonly Dispatcher _ui;
    private readonly Stopwatch _clock = new();
    private volatile bool _stop, _cancel;
    private RecordFrameWindow? _frame;
    private RecordBarWindow? _bar;

    public bool IsRecording { get; private set; }
    public TimeSpan Elapsed => _clock.Elapsed;
    public TimeSpan? Limit => null;

    /// <summary>Raised on the UI thread: the saved file, or null when cancelled; the error if it failed.</summary>
    public event Action<string?, string?>? Finished;

    public VideoRecorder(Dispatcher ui) => _ui = ui;

    public void Stop() => _stop = true;
    public void Cancel() => _cancel = true;

    public void Start(Int32Rect area, string path, VideoOptions options, WatermarkOptions? watermark = null)
    {
        if (IsRecording) return;
        IsRecording = true;
        _stop = _cancel = false;

        var sprites = options.Pointer && options.PointerStyle != "System" ? DrawPointers(area, options) : null;

        _frame = new RecordFrameWindow(area);
        _frame.Show();
        _bar = new RecordBarWindow(this, area);
        _bar.Show();

        _clock.Reset(); // started by the recording thread once the camera and sound are ready
        // the watermark is drawn here, on the UI thread, once: each frame only blends it in
        double fit = Fit(area);
        int ow = Math.Max(2, (int)(area.Width * fit) & ~1), oh = Math.Max(2, (int)(area.Height * fit) & ~1);
        byte[]? mark = watermark != null ? Watermark.Layer(watermark, ow, oh) : null;
        var thread = new Thread(() => Run(area, path, options, sprites, mark)) { IsBackground = true, Name = "Overskin video recorder" };
        thread.Start();
    }

    private static double Fit(Int32Rect area) =>
        Math.Min(1, Math.Min(MaxWidth / (double)area.Width, MaxHeight / (double)area.Height));

    /// <summary>The pointer at every size it can appear at, drawn now on the UI thread.</summary>
    internal static Dictionary<(PointerArt.Shape, int), PointerSprite> DrawPointers(Int32Rect area, VideoOptions o)
    {
        var sprites = new Dictionary<(PointerArt.Shape, int), PointerSprite>();
        double baseSize = PointerBase(area, o);
        int min = (int)Math.Floor(baseSize), max = (int)Math.Ceiling(baseSize * Math.Sqrt(o.AutoZoom ? o.Zoom : 1)) + 1;
        bool dark = o.PointerStyle == "Black";
        for (int size = min; size <= max; size++)
            foreach (var shape in new[] { PointerArt.Shape.Arrow, PointerArt.Shape.Text })
                sprites[(shape, size)] = PointerArt.Render(shape, size, dark);
        return sprites;
    }

    /// <summary>Pointer height in output pixels at no zoom.</summary>
    private static double PointerBase(Int32Rect area, VideoOptions o) => Math.Max(12, 26 * o.PointerSize * Fit(area));

    /// <summary>Something worth telling the user after the recording, such as a microphone that couldn't be opened.</summary>
    public string? Notice { get; private set; }
    private readonly List<string> _problems = new();

    /// <summary>How the screen is being read: "DXGI" (fast) or "GDI".</summary>
    public string GrabberKind { get; private set; } = "";

    /// <summary>The speakers and/or microphone, mixed. Nothing when sound is off or no device would open.</summary>
    private (AudioMixer?, BlockingCollection<AudioChunk>?) OpenSound(VideoOptions o)
    {
        var sources = new List<(AudioSource, float)>();
        if (o.SystemSound)
        {
            try { sources.Add((AudioSource.Open(loopback: true), 1f)); }
            catch (Exception ex) { _problems.Add("Couldn't record the system sound: " + ex.Message); }
        }
        if (o.Microphone)
        {
            try { sources.Add((AudioSource.Open(loopback: false, o.MicrophoneId), 1f)); }
            catch (Exception ex) { _problems.Add("Couldn't record the microphone: " + ex.Message); }
        }
        if (sources.Count == 0) return (null, null);
        var sound = new BlockingCollection<AudioChunk>(boundedCapacity: 400);
        return (new AudioMixer(_clock, sources, sound), sound);
    }

    private void Run(Int32Rect area, string path, VideoOptions o, Dictionary<(PointerArt.Shape, int), PointerSprite>? sprites, byte[]? mark)
    {
        double fit = Fit(area);
        int w = Math.Max(2, (int)(area.Width * fit) & ~1), h = Math.Max(2, (int)(area.Height * fit) & ~1);
        int fps = o.Fps;

        var queue = new BlockingCollection<VideoFrame>(boundedCapacity: 8);
        _problems.Clear();
        Notice = null;
        Webcam? cam = null;
        if (o.Webcam)
        {
            try { cam = Webcam.OpenAsync(o.WebcamId).GetAwaiter().GetResult(); }
            catch (Exception ex) { _problems.Add(ex.Message); }
        }
        var (mixer, sound) = OpenSound(o);
        if (_problems.Count > 0) Notice = string.Join(" ", _problems);
        _clock.Restart();
        var encoding = Task.Run(() => VideoEncoder.EncodeAsync(path, w, h, fps, queue, o.HighQuality, sound));
        string? error = null;
        bool highRes = timeBeginPeriod(1) == 0;
        int frames = 0;
        var systemPointers = new SystemPointers();
        IFrameGrabber? grabber = null;
        try
        {
            grabber = FrameGrabbers.For(area, w, h);
            GrabberKind = grabber.Kind;
            var camera = new SmartCamera(area.Width, area.Height)
            {
                ZoomIn = o.AutoZoom ? o.Zoom : 1,
                Linger = o.ZoomHold,
                ZoomRate = o.ZoomRate,
                PanRate = o.PanRate,
                PointerRate = o.PointerRate,
            };
            var rings = new List<(Point At, double Age)>();
            bool wasDown = false;
            double lastTime = 0, pointerBase = PointerBase(area, o);
            int n = 0;
            while (!_stop && !_cancel && !encoding.IsCompleted)
            {
                long due = n * 1000L / fps;
                long now = _clock.ElapsedMilliseconds;
                if (now < due)
                {
                    Thread.Sleep((int)Math.Min(due - now, 5));
                    continue;
                }
                double t = _clock.Elapsed.TotalSeconds, dt = Math.Clamp(t - lastTime, 0.001, 0.25);
                lastTime = t;

                GetCursorPos(out var p);
                var pointer = new Point(p.X - area.X, p.Y - area.Y);
                bool down = (GetAsyncKeyState(0x01) & 0x8000) != 0;
                bool clicked = down && !wasDown;
                wasDown = down;

                camera.Update(dt, pointer, clicked);
                if (clicked && o.ClickRings) rings.Add((pointer, 0));
                for (int i = rings.Count - 1; i >= 0; i--)
                {
                    rings[i] = (rings[i].At, rings[i].Age + dt);
                    if (rings[i].Age > 0.5) rings.RemoveAt(i);
                }

                var view = camera.View();
                double k = w / view.Width; // area pixels -> output pixels
                var c = camera.Cursor;
                double px = (c.X - view.X) * k, py = (c.Y - view.Y) * k;
                int size = (int)Math.Round(pointerBase * Math.Sqrt(camera.Zoom));
                var kind = o.Pointer ? SystemPointers.Current() : SystemPointers.Kind.None;
                PointerSprite? sprite = null;
                if (sprites != null && kind is SystemPointers.Kind.Arrow or SystemPointers.Kind.Text)
                {
                    var shape = kind == SystemPointers.Kind.Arrow ? PointerArt.Shape.Arrow : PointerArt.Shape.Text;
                    if (!sprites.TryGetValue((shape, size), out sprite))
                        sprite = sprites.Where(s => s.Key.Item1 == shape).OrderBy(s => Math.Abs(s.Key.Item2 - size)).First().Value;
                }

                void Overlay(IntPtr dc)
                {
                    // any other pointer (hand, resize, an app's own) is the Windows picture, loaded at the size needed
                    if (kind != SystemPointers.Kind.None && sprite == null) systemPointers.Draw(dc, px, py, size);
                }
                byte[] pixels;
                try
                {
                    pixels = grabber.Grab(area.X + view.X, area.Y + view.Y, view.Width, view.Height, Overlay);
                }
                catch when (grabber is not ScaledGrabber)
                {
                    // the fast way stopped working (HDR switched on, a driver reset): carry on the old way
                    grabber.Dispose();
                    grabber = new ScaledGrabber(w, h);
                    GrabberKind = grabber.Kind;
                    pixels = grabber.Grab(area.X + view.X, area.Y + view.Y, view.Width, view.Height, Overlay);
                }
                foreach (var (at, age) in rings)
                {
                    double grow = 1 - Math.Pow(1 - age / 0.5, 3);
                    PointerArt.Ring(pixels, w, h, (at.X - view.X) * k, (at.Y - view.Y) * k,
                        (8 + 26 * grow) * k * fit, Math.Max(1.5, 2.6 * k * fit), 1 - age / 0.5);
                }
                if (sprite != null) PointerArt.Blend(pixels, w, h, sprite, px, py);
                if (cam?.Latest() is { } face) WebcamBubble.Draw(pixels, w, h, face.Pixels, face.Width, face.Height, o);
                if (mark != null) Watermark.Blend(pixels, mark);

                if (queue.TryAdd(new VideoFrame(pixels, _clock.Elapsed))) frames++;
                n = (int)(_clock.ElapsedMilliseconds * fps / 1000) + 1; // skip ahead rather than fall behind
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        finally
        {
            if (highRes) timeEndPeriod(1);
            grabber?.Dispose();
            mixer?.Finish();
            _clock.Stop();
            queue.CompleteAdding();
            mixer?.Dispose();
            cam?.Dispose();
            systemPointers.Dispose();
        }

        try
        {
            encoding.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            error ??= ex.Message;
        }

        if (_cancel || frames == 0 || error != null)
        {
            try { File.Delete(path); } catch { }
        }
        string? saved = _cancel || frames == 0 || error != null ? null : path;
        _ui.BeginInvoke(() =>
        {
            IsRecording = false;
            _frame?.Close();
            _bar?.Close();
            _frame = null;
            _bar = null;
            Finished?.Invoke(saved, _cancel ? null : error);
        });
    }
}

/// <summary>Which standard pointer is showing, and the Windows pictures of them at any size.</summary>
internal sealed class SystemPointers : IDisposable
{
    public enum Kind { None, Arrow, Text, Other }

    private static readonly int[] Standard = { 32512, 32513, 32514, 32515, 32642, 32643, 32644, 32645, 32646, 32648, 32649, 32650 };
    private static readonly Dictionary<IntPtr, int> Handles = Standard
        .Select(id => (Handle: LoadCursor(IntPtr.Zero, (IntPtr)id), Id: id))
        .Where(x => x.Handle != IntPtr.Zero)
        .GroupBy(x => x.Handle).ToDictionary(g => g.Key, g => g.First().Id);
    private readonly Dictionary<(int Id, int Size), IntPtr> _loaded = new();

    public static Kind Current()
    {
        var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref info) || (info.flags & CURSOR_SHOWING) == 0 || info.hCursor == IntPtr.Zero) return Kind.None;
        return Handles.TryGetValue(info.hCursor, out int id) ? id switch { 32512 => Kind.Arrow, 32513 => Kind.Text, _ => Kind.Other } : Kind.Other;
    }

    /// <summary>Draws the pointer that is showing, tip at (x, y), <paramref name="size"/> pixels.</summary>
    public void Draw(IntPtr dc, double x, double y, int size)
    {
        var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref info) || info.hCursor == IntPtr.Zero) return;
        if (!Handles.TryGetValue(info.hCursor, out int id))
        {
            DrawCursorAt(dc, x, y, size); // an app's own pointer: scale the one bitmap there is
            return;
        }
        if (!_loaded.TryGetValue((id, size), out var big))
        {
            big = LoadAtSize(id, size);
            _loaded[(id, size)] = big;
        }
        if (big == IntPtr.Zero)
        {
            DrawCursorAt(dc, x, y, size);
            return;
        }
        int hx = 0, hy = 0;
        if (GetIconInfo(big, out var ii))
        {
            hx = ii.xHotspot;
            hy = ii.yHotspot;
            if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
            if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
        }
        DrawIconEx(dc, (int)Math.Round(x - hx), (int)Math.Round(y - hy), big, size, size, 0, IntPtr.Zero, DI_NORMAL);
    }

    // registry names of the pointers in the user's scheme, and the files Windows uses by default
    private static readonly Dictionary<int, (string Name, string File)> Scheme = new()
    {
        [32512] = ("Arrow", "aero_arrow.cur"), [32513] = ("IBeam", ""), [32514] = ("Wait", "aero_busy.ani"),
        [32515] = ("Crosshair", ""), [32642] = ("SizeNWSE", "aero_nwse.cur"), [32643] = ("SizeNESW", "aero_nesw.cur"),
        [32644] = ("SizeWE", "aero_ew.cur"), [32645] = ("SizeNS", "aero_ns.cur"), [32646] = ("SizeAll", "aero_move.cur"),
        [32648] = ("No", "aero_unavail.cur"), [32649] = ("Hand", "aero_link.cur"), [32650] = ("AppStarting", "aero_working.ani"),
    };

    /// <summary>
    /// The pointer from the user's own cursor scheme, read from its file at the size asked for:
    /// Windows' pointer files hold several sizes, so the big one is sharp. Zero when there's no file.
    /// </summary>
    public static IntPtr LoadAtSize(int id, int size)
    {
        if (!Scheme.TryGetValue(id, out var entry)) return IntPtr.Zero;
        string? path = null;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Cursors");
            path = key?.GetValue(entry.Name) as string;
        }
        catch { }
        if (string.IsNullOrWhiteSpace(path) && entry.File.Length > 0)
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Cursors", entry.File);
        if (string.IsNullOrWhiteSpace(path)) return IntPtr.Zero;
        path = Environment.ExpandEnvironmentVariables(path);
        if (!File.Exists(path)) return IntPtr.Zero;
        return LoadImageFile(IntPtr.Zero, path, IMAGE_CURSOR, size, size, LR_LOADFROMFILE);
    }

    public void Dispose()
    {
        foreach (var handle in _loaded.Values)
            if (handle != IntPtr.Zero) DestroyIcon(handle);
        _loaded.Clear();
    }
}

/// <summary>Copies any screen rectangle into a fixed-size bitmap, scaling with GDI's smooth filter.</summary>
internal sealed class ScaledGrabber : IFrameGrabber
{
    private readonly int _w, _h;
    private readonly IntPtr _screen, _mem, _dib, _old, _bits;

    public ScaledGrabber(int width, int height)
    {
        _w = width;
        _h = height;
        _screen = GetDC(IntPtr.Zero);
        _mem = CreateCompatibleDC(_screen);
        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // top-down rows
            biPlanes = 1,
            biBitCount = 32,
        };
        _dib = CreateDIBSection(_mem, ref header, 0, out _bits, IntPtr.Zero, 0);
        _old = SelectObject(_mem, _dib);
        SetStretchBltMode(_mem, HALFTONE);
        SetBrushOrgEx(_mem, 0, 0, IntPtr.Zero);
    }

    public string Kind => "GDI";

    public byte[] Grab(double x, double y, double width, double height, Action<IntPtr>? overlay)
    {
        int sx = (int)Math.Round(x), sy = (int)Math.Round(y);
        int sw = Math.Max(1, (int)Math.Round(width)), sh = Math.Max(1, (int)Math.Round(height));
        if (sw == _w && sh == _h) BitBlt(_mem, 0, 0, _w, _h, _screen, sx, sy, SRCCOPY);
        else StretchBlt(_mem, 0, 0, _w, _h, _screen, sx, sy, sw, sh, SRCCOPY);
        overlay?.Invoke(_mem);
        GdiFlush();
        var buffer = new byte[_w * _h * 4];
        Marshal.Copy(_bits, buffer, 0, buffer.Length); // alpha is left as is: the encoder ignores it
        return buffer;
    }

    public void Dispose()
    {
        SelectObject(_mem, _old);
        DeleteObject(_dib);
        DeleteDC(_mem);
        ReleaseDC(IntPtr.Zero, _screen);
    }
}
