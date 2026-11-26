using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using static Overskin.NativeMethods;

namespace Overskin;

/// <summary>
/// Records a screen rectangle into frames on a background thread. A red frame marks the area and a
/// small bar shows the time with Stop and Cancel; both are kept out of the recording itself.
/// </summary>
public interface IRecorder
{
    TimeSpan Elapsed { get; }
    /// <summary>Longest recording, or null for no limit.</summary>
    TimeSpan? Limit { get; }
    void Stop();
    void Cancel();
}

public sealed class GifRecorder : IRecorder
{
    /// <summary>Frames per second for this recording (set when it starts).</summary>
    public int Fps { get; private set; } = 30;
    public const int MaxSeconds = 30;

    /// <summary>15, 30 or 50. GIF times are in hundredths of a second, and Discord and browsers play
    /// anything under two hundredths slowly, so 50 is the smoothest a GIF can be.</summary>
    public static int ValidFps(int fps) => fps >= 50 ? 50 : fps >= 30 ? 30 : 15;
    private const long MaxBytes = 900L * 1024 * 1024;
    private const int MaxWidth = 960;

    private readonly Dispatcher _ui;
    private Thread? _thread;
    private volatile bool _stop, _cancel;
    private RecordFrameWindow? _frame;
    private RecordBarWindow? _bar;
    private readonly Stopwatch _clock = new();

    public bool IsRecording { get; private set; }

    /// <summary>Raised on the UI thread: the clip, or null when cancelled or nothing was recorded.</summary>
    public event Action<GifClip?>? Finished;

    public GifRecorder(Dispatcher ui) => _ui = ui;

    public TimeSpan Elapsed => _clock.Elapsed;
    /// <summary>How long this recording can run: 30 seconds, or less for a big area at a high frame rate (memory).</summary>
    public TimeSpan? Limit => TimeSpan.FromSeconds(_limitSeconds);
    private double _limitSeconds = MaxSeconds;

    /// <summary>Most frames a recording of this area can hold, at <see cref="Fps"/>.</summary>
    private int MaxFrames(Int32Rect area)
    {
        int k = Math.Max(1, (int)Math.Ceiling(area.Width / (double)MaxWidth));
        long frameBytes = (long)(area.Width / k) * (area.Height / k) * 4;
        return (int)Math.Min(Fps * MaxSeconds, Math.Max(Fps, MaxBytes / Math.Max(1, frameBytes)));
    }

    public void Start(Int32Rect area, bool cursor, int fps = 30)
    {
        if (IsRecording) return;
        IsRecording = true;
        Fps = ValidFps(fps);
        _limitSeconds = MaxFrames(area) / (double)Fps;
        _stop = _cancel = false;

        _frame = new RecordFrameWindow(area);
        _frame.Show();
        _bar = new RecordBarWindow(this, area);
        _bar.Show();

        _clock.Restart();
        _thread = new Thread(() => Run(area, cursor)) { IsBackground = true, Name = "Overskin recorder" };
        _thread.Start();
    }

    /// <summary>Stops and keeps what was recorded.</summary>
    public void Stop() => _stop = true;

    /// <summary>Stops and throws the recording away.</summary>
    public void Cancel() => _cancel = true;

    private void Run(Int32Rect area, bool cursor)
    {
        // big areas are shrunk while recording: GIFs that size are unusable anyway, and memory stays sane
        int k = Math.Max(1, (int)Math.Ceiling(area.Width / (double)MaxWidth));
        int outW = area.Width / k, outH = area.Height / k;
        int maxFrames = MaxFrames(area);

        var frames = new List<byte[]>();
        var stamps = new List<long>();
        bool highRes = timeBeginPeriod(1) == 0;
        try
        {
            using var grabber = new ScreenGrabber(area.X, area.Y, area.Width, area.Height);
            int n = 0;
            while (!_stop && !_cancel && frames.Count < maxFrames)
            {
                long due = n * 1000L / Fps;
                long now = _clock.ElapsedMilliseconds;
                if (now < due)
                {
                    Thread.Sleep((int)Math.Min(due - now, 20));
                    continue;
                }
                var raw = grabber.Grab(cursor);
                frames.Add(k > 1 ? Shrink(raw, area.Width, area.Height, k) : raw);
                stamps.Add(_clock.ElapsedMilliseconds);
                n = (int)(_clock.ElapsedMilliseconds * Fps / 1000) + 1; // skip ahead rather than fall behind
            }
        }
        catch
        {
            _cancel = frames.Count == 0;
        }
        finally
        {
            if (highRes) timeEndPeriod(1);
            _clock.Stop();
        }

        GifClip? clip = null;
        if (!_cancel && frames.Count > 0)
        {
            clip = new GifClip { Width = outW, Height = outH };
            for (int i = 0; i < frames.Count; i++)
            {
                clip.Frames.Add(frames[i]);
                clip.DelaysMs.Add(i + 1 < stamps.Count ? (int)(stamps[i + 1] - stamps[i]) : 1000 / Fps);
            }
        }

        _ui.BeginInvoke(() =>
        {
            IsRecording = false;
            _frame?.Close();
            _bar?.Close();
            _frame = null;
            _bar = null;
            Finished?.Invoke(clip);
        });
    }

    /// <summary>Box-averages every k×k block into one pixel.</summary>
    private static byte[] Shrink(byte[] src, int w, int h, int k)
    {
        int ow = w / k, oh = h / k;
        var dst = new byte[ow * oh * 4];
        int area = k * k;
        for (int y = 0; y < oh; y++)
        {
            for (int x = 0; x < ow; x++)
            {
                int b = 0, g = 0, r = 0;
                for (int yy = 0; yy < k; yy++)
                {
                    int row = ((y * k + yy) * w + x * k) * 4;
                    for (int xx = 0; xx < k; xx++)
                    {
                        int p = row + xx * 4;
                        b += src[p]; g += src[p + 1]; r += src[p + 2];
                    }
                }
                int d = (y * ow + x) * 4;
                dst[d] = (byte)(b / area); dst[d + 1] = (byte)(g / area); dst[d + 2] = (byte)(r / area); dst[d + 3] = 255;
            }
        }
        return dst;
    }

    /// <summary>Positions a window at a physical screen point.</summary>
    internal static void PlacePhysical(Window w, int x, int y)
    {
        var h = new WindowInteropHelper(w).Handle;
        SetWindowPos(h, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
    }
}

/// <summary>
/// The red outline around the area being recorded: four thin solid strips just outside it.
/// Click-through and never captured (a single see-through frame window can't be kept out of
/// captures, solid strips can).
/// </summary>
internal sealed class RecordFrameWindow
{
    private readonly List<GuardedWindow> _strips = new();

    public RecordFrameWindow(Int32Rect area)
    {
        const int t = 2, gap = 1;
        int x = area.X - t - gap, y = area.Y - t - gap, w = area.Width + 2 * (t + gap), h = area.Height + 2 * (t + gap);
        foreach (var r in new[]
        {
            new Int32Rect(x, y, w, t), new Int32Rect(x, y + h - t, w, t),
            new Int32Rect(x, y + t, t, h - 2 * t), new Int32Rect(x + w - t, y + t, t, h - 2 * t),
        })
        {
            var strip = new GuardedWindow(clickThrough: true)
            {
                Background = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),
                Width = 1, Height = 1, Left = -10000, Top = -10000,
            };
            var rect = r;
            strip.SourceInitialized += (_, _) => Placement.MoveTo(new WindowInteropHelper(strip).Handle, rect, true);
            _strips.Add(strip);
        }
    }

    public void Show()
    {
        foreach (var s in _strips) s.Show();
    }

    /// <summary>Amber while the recording is paused, red while it records.</summary>
    public void SetPaused(bool paused)
    {
        var colour = paused ? Color.FromRgb(0xF5, 0x9E, 0x0B) : Color.FromRgb(0xEF, 0x44, 0x44);
        foreach (var s in _strips) s.Background = new SolidColorBrush(colour);
    }

    public void Close()
    {
        foreach (var s in _strips) s.Close();
        _strips.Clear();
    }
}

/// <summary>Timer, Stop and Cancel, just below (or above) the recorded area.</summary>
internal sealed class RecordBarWindow : GuardedWindow
{
    private readonly IRecorder _recorder;
    private readonly Int32Rect _area;
    private readonly TextBlock _time = new();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly Ellipse _dot;
    private readonly DoubleAnimation _blink = new(1, 0.25, TimeSpan.FromMilliseconds(700)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
    private Button? _pause, _pen, _colour;
    private Icon? _pauseIcon;
    private readonly List<(ScreenPen.Tool Tool, Button Button)> _tools = new();
    private StackPanel? _toolRow;

    public RecordBarWindow(IRecorder recorder, Int32Rect area) : base(clickThrough: false)
    {
        _recorder = recorder;
        _area = area;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = (FontFamily)FindResource("UiFont");
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        var dot = _dot = new Ellipse { Width = 9, Height = 9, Fill = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)), Margin = new Thickness(2, 0, 9, 0) };
        dot.BeginAnimation(OpacityProperty, _blink);
        _time.Foreground = (Brush)FindResource("Text");
        _time.FontSize = 12.5;
        _time.FontWeight = FontWeights.SemiBold;
        _time.VerticalAlignment = VerticalAlignment.Center;
        _time.MinWidth = 92;

        var stop = new Button { Style = (Style)FindResource("BtnAccent"), Height = 26, Margin = new Thickness(6, 0, 4, 0), ToolTip = "Stop and edit  (Esc)" };
        var stopContent = new StackPanel { Orientation = Orientation.Horizontal };
        stopContent.Children.Add(new Border { Width = 9, Height = 9, Background = (Brush)FindResource("AccentInk"), CornerRadius = new CornerRadius(1.5), Margin = new Thickness(0, 0, 7, 0) });
        stopContent.Children.Add(new TextBlock { Text = "Stop" });
        stop.Content = stopContent;
        stop.Click += (_, _) => _recorder.Stop();

        var cancel = new Button { Style = (Style)FindResource("BtnIcon"), ToolTip = "Cancel, don't keep it" };
        cancel.Content = new Icon { Data = (Geometry)FindResource("IcX"), Width = 13, Height = 13 };
        cancel.Click += (_, _) => _recorder.Cancel();

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(dot);
        row.Children.Add(_time);
        if (recorder is VideoRecorder video) AddVideoButtons(row, video);
        row.Children.Add(stop);
        row.Children.Add(cancel);

        Content = new Border
        {
            Child = row,
            Background = (Brush)FindResource("Panel"),
            BorderBrush = (Brush)FindResource("LineStrong"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 6, 6, 6),
            CornerRadius = (CornerRadius)FindResource("RadiusCard"),
        };
        Corners = (CornerRadius)FindResource("RadiusCard");

        _tick.Tick += (_, _) =>
        {
            UpdateTime();
            if (_recorder is VideoRecorder { Pen.On: true }) RaiseAbove();
        };
        UpdateTime();
    }

    private Button IconButton(string icon, string tip, Action click)
    {
        var b = new Button { Style = (Style)FindResource("BtnIcon"), ToolTip = tip, Margin = new Thickness(1, 0, 1, 0) };
        b.Content = new Icon { Data = (Geometry)FindResource(icon), Width = 14, Height = 14 };
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>Pause, and drawing on the screen with its tools and colour.</summary>
    private void AddVideoButtons(StackPanel row, VideoRecorder video)
    {
        _pause = IconButton("IcPause", "Pause  (" + Hotkeys.Pause + ")", video.TogglePause);
        _pauseIcon = (Icon)_pause.Content;
        _pen = IconButton("IcPencil", "Draw on the screen  (" + Hotkeys.Pen + ")\nWhat you draw is recorded and fades after a few seconds; the pointer is hidden meanwhile. Right-click puts the pen away.", video.TogglePen);
        row.Children.Add(_pause);
        row.Children.Add(_pen);

        _toolRow = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed, Margin = new Thickness(2, 0, 0, 0) };
        foreach (var (tool, icon, tip) in new[]
        {
            (ScreenPen.Tool.Pen, "IcPencil", "Pen"), (ScreenPen.Tool.Arrow, "IcArrowUpRight", "Arrow"),
            (ScreenPen.Tool.Box, "IcSquare", "Box"), (ScreenPen.Tool.Highlight, "IcHighlighter", "Highlighter"),
        })
        {
            var t = tool;
            var b = IconButton(icon, tip, () => { if (video.Pen is { } p) { p.Current = t; Refresh(); } });
            ((Icon)b.Content).Width = ((Icon)b.Content).Height = 13;
            _tools.Add((t, b));
            _toolRow.Children.Add(b);
        }
        _colour = new Button { Style = (Style)FindResource("BtnIcon"), ToolTip = "Colour (click for the next one)", Margin = new Thickness(1, 0, 4, 0) };
        _colour.Content = new Ellipse { Width = 13, Height = 13, Stroke = (Brush)FindResource("LineStrong"), StrokeThickness = 1 };
        _colour.Click += (_, _) => { if (video.Pen is { } p) { p.ColourIndex = (p.ColourIndex + 1) % ScreenPen.Colours.Length; Refresh(); } };
        _toolRow.Children.Add(_colour);
        row.Children.Add(_toolRow);
        Refresh();
    }

    /// <summary>
    /// Puts the bar back on top of everything, the drawing sheet included, so Pause, Stop and the
    /// pen stay clickable while drawing (a click on the sheet can lift it above the bar).
    /// </summary>
    public void RaiseAbove()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero) SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>Shows whether the recording is paused and which drawing tool is picked.</summary>
    public void Refresh()
    {
        if (_recorder is not VideoRecorder video || _pause == null) return;
        bool paused = video.IsPaused, drawing = video.Pen?.On == true;
        _pauseIcon!.Data = (Geometry)FindResource(paused ? "IcPlay" : "IcPause");
        _pause.ToolTip = (paused ? "Carry on recording" : "Pause") + "  (" + Hotkeys.Pause + ")";
        _dot.Fill = new SolidColorBrush(paused ? Color.FromRgb(0xF5, 0x9E, 0x0B) : Color.FromRgb(0xEF, 0x44, 0x44));
        if (paused) { _dot.BeginAnimation(OpacityProperty, null); _dot.Opacity = 1; }
        else _dot.BeginAnimation(OpacityProperty, _blink);
        _pen!.IsEnabled = !paused;
        Mark(_pen, drawing);
        _toolRow!.Visibility = drawing ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (tool, button) in _tools) Mark(button, video.Pen?.Current == tool);
        if (video.Pen != null) ((Ellipse)_colour!.Content).Fill = new SolidColorBrush(video.Pen.Colour);
        UpdateTime();
    }

    private void Mark(Button b, bool on)
    {
        if (on)
        {
            b.SetResourceReference(BackgroundProperty, "AccentSoft");
            if (b.Content is Icon i) i.SetResourceReference(Overskin.Icon.ForegroundProperty, "Accent");
        }
        else
        {
            b.ClearValue(BackgroundProperty);
            if (b.Content is Icon i) i.ClearValue(Overskin.Icon.ForegroundProperty);
        }
    }

    /// <summary>The keys shown in the bar's tooltips (set by the main window from the settings).</summary>
    internal static class Hotkeys
    {
        public static string Pause = "Ctrl+Alt+P";
        public static string Pen = "Ctrl+Alt+D";
    }

    private void UpdateTime()
    {
        var t = _recorder.Elapsed;
        if (_recorder is VideoRecorder { IsPaused: true })
        {
            _time.Text = $"Paused  {(int)t.TotalMinutes}:{t.Seconds:00}";
            return;
        }
        _time.Text = _recorder.Limit is { } limit
            ? $"{(int)t.TotalMinutes}:{t.Seconds:00}  /  {(int)limit.TotalMinutes}:{limit.Seconds:00}"
            : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        GetWindowRect(new WindowInteropHelper(this).Handle, out var r);
        var mon = MonitorBounds(new POINT(_area.X + _area.Width / 2, _area.Y + _area.Height / 2));
        int x = _area.X + (_area.Width - r.Width) / 2;
        int y = _area.Y + _area.Height + 10;                                  // below the area
        if (y + r.Height > mon.Bottom) y = _area.Y - r.Height - 10;           // or above it
        if (y < mon.Top) y = _area.Y + _area.Height - r.Height - 12;          // or inside, which is fine: it's never captured
        x = Math.Clamp(x, mon.Left + 4, Math.Max(mon.Left + 4, mon.Right - r.Width - 4));
        GifRecorder.PlacePhysical(this, x, y);
        _tick.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _tick.Stop();
        base.OnClosed(e);
    }
}
