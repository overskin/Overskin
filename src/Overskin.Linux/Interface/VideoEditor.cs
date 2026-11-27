using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Overskin.Linux.Core;
using Overskin.Linux.Platform;

namespace Overskin.Linux.Interface;

/// <summary>
/// The video editor (as on Windows): trim the ends, cut out parts in the middle (or every idle
/// stretch at once), crop to any shape, change the speed, put text and arrows on it, blur private
/// details (found on the frame, or dragged over), change the auto-zooms, and make subtitles from
/// what's said (Whisper, on this computer) in four looks, whole lines or word by word. Plays with
/// sound; saves an MP4 (with an .srt beside it), a copy small enough to share, or a GIF of part of
/// it; grabs a still frame. Everything is made with ffmpeg.
/// </summary>
public sealed class VideoEditor : Window
{
    private readonly string _source;
    private readonly VideoProject _project;
    private readonly List<string> _frames;
    private readonly double _frameRate;
    private readonly string _work;
    private readonly MediaPlayer? _player;
    private readonly ZoomTrack? _zooms;
    private bool _zoomsChanged;
    private readonly Dictionary<int, Bitmap> _cache = new();

    private readonly VideoStage _stage;
    private readonly Slider _scrub = new() { Minimum = 0, VerticalAlignment = VerticalAlignment.Center };
    private readonly Canvas _bands = new() { Height = 10, IsHitTestVisible = false };
    private readonly Slider _start = new() { Minimum = 0 }, _end = new() { Minimum = 0 };
    private readonly TextBlock _time = Ui.Label("", "dim"), _status = Ui.Label("", "dim"), _keptText = Ui.Label("", "dim"), _cutsText = Ui.Label("", "dim");
    private readonly TextBlock _startText = Ui.Label("", "dim"), _endText = Ui.Label("", "dim"), _cropText = Ui.Label("", "dim");
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, IsVisible = false, Height = 4 };
    private readonly ComboBox _speed = new() { ItemsSource = new[] { "0.5×", "0.75×", "1×", "1.25×", "1.5×", "2×", "3×" }, SelectedIndex = 2 };
    private readonly ComboBox _look = new() { ItemsSource = CaptionLook.Looks.Select(l => l.Name).ToList(), SelectedIndex = 0 };
    private readonly ComboBox _words = new() { ItemsSource = CaptionLook.WordModes.Select(w => w.Label).ToList(), SelectedIndex = 0 };
    private readonly TextBox _text = new() { Watermark = "Words to show on the video" };
    private readonly StackPanel _overlayRows = new() { Spacing = 6 }, _blurRows = new() { Spacing = 6 }, _zoomRows = new() { Spacing = 6 }, _captionRows = new() { Spacing = 4 };
    private readonly Slider _shareSize = new() { Minimum = 5, Maximum = 100, Value = 10, Width = 150 };
    private readonly TextBlock _shareText = Ui.Label("10 MB", "dim");
    private readonly Button _playButton, _cutButton, _arrowButton, _blurButton, _cropButton, _idleButton, _findButton;
    private readonly StackPanel _cropShapes = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly Control _sidePanel;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(40) };

    private static readonly double[] Speeds = { 0.5, 0.75, 1, 1.25, 1.5, 2, 3 };
    private double _now;
    private bool _playing, _syncing, _busy;
    private double? _cutFrom;

    private VideoEditor(string source, VideoProject project, List<string> frames, double frameRate, string work, MediaPlayer? player)
    {
        _source = source;
        _project = project;
        _frames = frames;
        _frameRate = frameRate;
        _work = work;
        _player = player;
        _zooms = ZoomTrack.Load(source);
        Title = System.IO.Path.GetFileName(source) + " — video editor";
        Icon = App.Icon;
        Width = 1240;
        Height = 800;
        MinWidth = 900;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _stage = new VideoStage(this);
        foreach (var s in new[] { _scrub, _start, _end }) s.Maximum = project.Duration;
        _end.Value = project.Duration;
        _scrub.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty && !_syncing) { Play(false); ShowAt(_scrub.Value); } };
        _start.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty && !_syncing) TrimChanged(_start); };
        _end.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty && !_syncing) TrimChanged(_end); };
        _speed.SelectionChanged += (_, _) => { _project.Speed = Speeds[Math.Max(0, _speed.SelectedIndex)]; UpdateTexts(); };
        _look.SelectionChanged += (_, _) => { foreach (var o in Captions) o.Look = CaptionLook.Looks[Math.Max(0, _look.SelectedIndex)].Name; _stage.InvalidateVisual(); };
        _words.SelectionChanged += (_, _) => { foreach (var o in Captions) o.Words = CaptionLook.WordModes[Math.Max(0, _words.SelectedIndex)].Name; _stage.InvalidateVisual(); };
        _shareSize.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) _shareText.Text = $"{_shareSize.Value:0} MB"; };
        _text.KeyDown += (_, e) => { if (e.Key == Key.Enter) { AddText(); e.Handled = true; } };
        _tick.Tick += (_, _) => Follow();
        if (_player != null)
            _player.FrameShown += () => { if (_playing) _stage.InvalidateVisual(); };

        _playButton = Ui.IconButton(Icons.Play, "Play / pause  (Space)", () => Play(!_playing));
        _cutButton = Ui.Button("Cut from here", Cut);
        ToolTip.SetTip(_cutButton, "Click here, play or scrub to where the cut should end, then click again  (X)");
        _arrowButton = Ui.Button("Add an arrow", ToggleArrow);
        ToolTip.SetTip(_arrowButton, "Then drag on the video from the tail to the point");
        _blurButton = Ui.Button("Add a blur box", ToggleBlur);
        ToolTip.SetTip(_blurButton, "Then drag over what to hide. It's blurred from here to the end; drag it to move it");
        _cropButton = Ui.Button("Crop", () => SetCropping(!_stage.Cropping));
        ToolTip.SetTip(_cropButton, "Then drag on the picture. Drag inside the box to move it, or its edges and corners to resize it");
        _idleButton = Ui.Button("Cut idle parts", () => _ = CutIdleAsync());
        ToolTip.SetTip(_idleButton, "Finds the stretches where nothing on screen moves for 2 seconds or more, and cuts them");
        _findButton = Ui.Button("Find private details", () => _ = FindPrivateAsync());
        ToolTip.SetTip(_findButton, "Reads the frame showing now and blurs every email, phone number, card number, address, IP and key on it, from here to the end");
        foreach (var (label, ratio, tip) in new[] { ("Free", "0", "Any shape"), ("16:9", "16:9", "Widescreen, like YouTube"), ("1:1", "1:1", "Square"), ("4:3", "4:3", "4:3"), ("9:16", "9:16", "Tall, like TikTok, Shorts and Reels") })
        {
            var r = new RadioButton { Content = label, GroupName = "VideoCropShape", IsChecked = ratio == "0" };
            ToolTip.SetTip(r, tip);
            var value = Ratio(ratio);
            r.IsCheckedChanged += (_, _) => { if (r.IsChecked == true) { _stage.SetRatio(value); if (value > 0 && !_stage.Cropping) SetCropping(true); } };
            _cropShapes.Children.Add(r);
        }

        Control Card(string title, params Control[] parts)
        {
            var stack = new StackPanel { Spacing = 8 };
            stack.Children.Add(Ui.Label(title, "h2"));
            foreach (var p in parts) stack.Children.Add(p);
            return Ui.Card(stack);
        }
        Control Wrap(params Control[] items)
        {
            var w = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
            foreach (var i in items) w.Children.Add(i);
            return w;
        }
        Control Labelled(TextBlock label, Slider slider) => new DockPanel { Children = { Docked(label, Dock.Right), slider } };

        var side = new StackPanel
        {
            Width = 360, Spacing = 10, Margin = new Thickness(12),
            Children =
            {
                Card("Trim", Labelled(_startText, _start), Labelled(_endText, _end),
                    Wrap(Tip(Ui.Button("Start here", StartHere), "Start where the video is now  (I)"), Tip(Ui.Button("End here", EndHere), "End where the video is now  (O)")), _keptText),
                Card("Cut", Wrap(_cutButton, Ui.Button("Clear cuts", ClearCuts), _idleButton), _cutsText),
                Card("Crop", Wrap(_cropButton, Tip(Ui.Button("Reset", ResetCrop), "Keep the whole picture")), _cropShapes, _cropText),
                Card("Speed", _speed),
                Card("Text and arrows", _text, Wrap(Tip(Ui.Button("Add text", AddText), "Shows the words over the video from here for 3 seconds; drag them where you like"), _arrowButton), _overlayRows),
                Card("Blur", Wrap(_blurButton, _findButton), _blurRows),
                ZoomCard(),
                Card("Subtitles",
                    Tip(Ui.Button("Make subtitles", () => _ = MakeSubtitlesAsync(), Icons.Text, "primary"), "Listens to what's said in the video and writes it out as subtitles, on this computer"),
                    new DockPanel { Children = { Docked(Ui.Label("Look", "dim"), Dock.Left, new Thickness(0, 0, 8, 0)), _look } },
                    new DockPanel { Children = { Docked(Ui.Label("Words", "dim"), Dock.Left, new Thickness(0, 0, 8, 0)), _words } },
                    Tip(Ui.Button("Remove all", () => { _project.Overlays.RemoveAll(o => o.Kind == VideoOverlay.Kinds.Caption); Rebuild(); }), "Takes every subtitle line off the video"),
                    _captionRows),
                Card("Save",
                    Wrap(Tip(Ui.Button("Save", () => _ = SaveToAsync(TrimmedName(_source)), Icons.Save, "primary"), "Save the edited video as a new file next to this one"),
                         Ui.Button("Save as…", () => _ = SaveAsAsync()),
                         Tip(Ui.Button("Make a GIF", () => _ = MakeGifAsync(), Icons.Record), "Turns the trimmed part (or 5 seconds from here) into a GIF, with what's said in it as the caption")),
                    Ui.Label("Small enough to share", "dim"),
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _shareSize, _shareText } },
                    Tip(Ui.Button("Save a small copy", () => _ = SaveToAsync(SmallName(), (long)(_shareSize.Value * 1024 * 1024))),
                        "Saves a copy with your edits that fits in this size (Discord's free limit is 10 MB): the quality and, if need be, the size are chosen to suit"),
                    Wrap(Tip(Ui.Button("Grab this frame", () => _ = GrabFrameAsync(), Icons.Picture), "Copies the frame showing now as a picture and opens it in the editor"),
                         Ui.Button("Open in your video player", () => Desktop.Open(_source))),
                    _progress, _status),
            },
        };
        foreach (var t in side.GetLogicalDescendants().OfType<TextBlock>()) t.TextWrapping = TextWrapping.Wrap;
        _sidePanel = side;

        var scrubArea = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto"), Children = { _bands, _scrub } };
        Grid.SetRow(_scrub, 1);
        _bands.SizeChanged += (_, _) => DrawBands();
        var bottom = new DockPanel { Margin = new Thickness(16, 4, 16, 12) };
        bottom.Children.Add(Docked(_playButton, Dock.Left, new Thickness(0, 0, 12, 0)));
        bottom.Children.Add(Docked(_time, Dock.Right, new Thickness(12, 0, 0, 0)));
        bottom.Children.Add(scrubArea);
        var root = new DockPanel();
        var scroll = new ScrollViewer { Content = side, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        DockPanel.SetDock(scroll, Dock.Right);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(scroll);
        root.Children.Add(bottom);
        root.Children.Add(new Border { Background = new SolidColorBrush(Color.Parse("#0C0D0F")), Padding = new Thickness(16), Child = _stage });
        Content = root;
        KeyDown += OnKey;
        Closing += (_, e) => { if (_busy) e.Cancel = true; };
        Closed += (_, _) =>
        {
            _tick.Stop();
            _player?.Dispose();
            try { Directory.Delete(_work, true); } catch { }
        };
        Rebuild();
        ShowAt(0);
    }

    private static Control Tip(Control c, string tip) { ToolTip.SetTip(c, tip); return c; }

    private static Control Docked(Control c, Dock dock, Thickness? margin = null)
    {
        DockPanel.SetDock(c, dock);
        if (margin is { } m) c.Margin = m;
        return c;
    }

    private IEnumerable<VideoOverlay> Captions => _project.Overlays.Where(o => o.Kind == VideoOverlay.Kinds.Caption);
    private int W => _project.Width;
    private int H => _project.Height;

    // ---- opening -------------------------------------------------------------------------

    /// <summary>Opens a video: works out its size and length, and reads frames to scrub through.</summary>
    public static async Task OpenAsync(string source)
    {
        var editor = await LoadAsync(source);
        editor?.Show();
    }

    /// <summary>The editor for a video, ready to show (null when it can't be read; the reason was shown).</summary>
    internal static async Task<VideoEditor?> LoadAsync(string source, bool quiet = false)
    {
        void Say(string title, string body) { if (!quiet) MessageWindow.Show(title, body); else Log.Write($"video editor: {title}: {body}"); }
        if (!ScreenGrabber.HasCommand("ffmpeg") || !ScreenGrabber.HasCommand("ffprobe")) { Say("The video editor needs ffmpeg", Converter.FfmpegHint); return null; }
        var probe = await ScreenGrabber.OutputOf("ffprobe", $"-v error -show_entries stream=codec_type,width,height:format=duration -of json \"{source}\"");
        if (probe == null) { Say("Couldn't read the video", "ffprobe couldn't read " + System.IO.Path.GetFileName(source) + "."); return null; }
        int w = 0, h = 0;
        double duration = 0;
        bool sound = false;
        using (var doc = System.Text.Json.JsonDocument.Parse(probe))
        {
            foreach (var s in doc.RootElement.GetProperty("streams").EnumerateArray())
            {
                var type = s.GetProperty("codec_type").GetString();
                if (type == "video" && w == 0) { w = s.GetProperty("width").GetInt32(); h = s.GetProperty("height").GetInt32(); }
                if (type == "audio") sound = true;
            }
            if (doc.RootElement.TryGetProperty("format", out var f) && f.TryGetProperty("duration", out var d))
                double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
        }
        if (w == 0 || duration <= 0) { Say("Couldn't read the video", "It has no picture, or its length can't be told."); return null; }
        var work = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"overskin-video-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        // frames for scrubbing: enough to move through smoothly, at most about 900 of them
        double rate = Math.Clamp(900 / duration, 1, 12);
        var (ok, error) = await ScreenGrabber.RunAsync("ffmpeg",
            $"-hide_banner -loglevel error -i \"{source}\" -vf \"fps={rate.ToString("0.###", CultureInfo.InvariantCulture)},scale='min(960,iw)':-2\" \"{System.IO.Path.Combine(work, "f%05d.jpg")}\"", 900);
        var frames = Directory.GetFiles(work, "f*.jpg").OrderBy(x => x).ToList();
        if (!ok || frames.Count == 0)
        {
            try { Directory.Delete(work, true); } catch { }
            Say("Couldn't read the video", error);
            return null;
        }
        var project = new VideoProject { Duration = duration, Width = w, Height = h, HasSound = sound, End = duration };
        var player = await MediaPlayer.OpenAsync(source);
        return new VideoEditor(source, project, frames, rate, work, player);
    }

    // ---- playing and scrubbing ----------------------------------------------------------------

    private (double Start, double End) Range => (Math.Min(_start.Value, _end.Value), Math.Max(_start.Value, _end.Value));

    private void Play(bool on)
    {
        if (on == _playing) return;
        _playing = on;
        _playButton.Content = Ui.Icon(on ? Icons.Pause : Icons.Play, 18);
        if (on)
        {
            var (start, end) = Range;
            if (_now < start - 0.05 || _now >= end - 0.05) _now = start;
            if (_player != null) { _player.Seek(_now); _player.Play(); }
            _tick.Start();
        }
        else
        {
            _player?.Pause();
            _tick.Stop();
            ShowAt(_now);
        }
    }

    /// <summary>While playing: the scrubber follows, cut parts are skipped, and it goes round the trimmed part.</summary>
    private void Follow()
    {
        var (start, end) = Range;
        double now = _player?.Position ?? _now + _tick.Interval.TotalSeconds;
        if (now >= end - 0.02 || (_player != null && !_player.Playing))
        {
            now = start;
            if (_player != null) { _player.Seek(start); _player.Play(); }
        }
        foreach (var (a, b) in _project.Cuts)
            if (now >= a && now < b - 0.02) { now = b; _player?.Seek(b); break; }
        _now = now;
        _syncing = true;
        _scrub.Value = Math.Clamp(now, 0, _scrub.Maximum);
        _syncing = false;
        ShowTime();
        _stage.InvalidateVisual();
    }

    private void Seek(double t)
    {
        Play(false);
        ShowAt(t);
    }

    /// <summary>Shows the moment <paramref name="t"/> (seconds of the original) with what's on it then.</summary>
    private void ShowAt(double t)
    {
        _now = Math.Clamp(t, 0, _project.Duration);
        _syncing = true;
        _scrub.Value = _now;
        _syncing = false;
        ShowTime();
        _stage.InvalidateVisual();
    }

    private void ShowTime()
    {
        bool cut = _project.ToOutput(_now) == null;
        _time.Text = $"{Clock(_now, true)} / {Clock(_project.Duration)}" + (cut ? "  ·  cut out" : "");
    }

    /// <summary>The frame to show: the playing video's, or the scrubbing copy nearest the moment.</summary>
    internal Bitmap? CurrentFrame()
    {
        if (_playing && _player?.Frame is { } live) return live;
        if (_frames.Count == 0) return null;
        int i = Math.Clamp((int)Math.Round(_now * _frameRate), 0, _frames.Count - 1);
        if (!_cache.TryGetValue(i, out var b))
        {
            if (_cache.Count > 120) _cache.Clear();
            b = _cache[i] = new Bitmap(_frames[i]);
        }
        return b;
    }

    internal double Now => _now;
    internal PixelSize VideoSize => new(W, H);

    private static string Clock(double seconds, bool tenths = false)
    {
        seconds = Math.Max(0, seconds);
        var t = TimeSpan.FromSeconds(seconds);
        return tenths ? $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds / 100}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    // ---- trim and cuts -------------------------------------------------------------------

    private void TrimChanged(Slider which)
    {
        _syncing = true;
        if (which == _start && _start.Value > _end.Value) _start.Value = _end.Value;
        if (which == _end && _end.Value < _start.Value) _end.Value = _start.Value;
        _syncing = false;
        _project.Start = Math.Min(_start.Value, _end.Value);
        _project.End = Math.Max(_start.Value, _end.Value);
        UpdateTexts();
        Seek(which.Value);
    }

    private void StartHere() => _start.Value = Math.Min(_now, _end.Value);
    private void EndHere() => _end.Value = Math.Max(_now, _start.Value);

    private void Cut()
    {
        if (_cutFrom is not { } from)
        {
            _cutFrom = _now;
            _cutButton.Content = "End the cut here";
            _status.Text = $"Cutting from {Clock(_now, true)}: go to where it should end and click again.";
            return;
        }
        _cutFrom = null;
        _cutButton.Content = "Cut from here";
        _status.Text = "";
        AddCut(Math.Min(from, _now), Math.Max(from, _now));
    }

    private void AddCut(double a, double b)
    {
        if (b - a < 0.1) return;
        _project.Cuts.Add((a, b));
        // joined up where they overlap
        _project.Cuts.Sort((x, y) => x.From.CompareTo(y.From));
        for (int i = _project.Cuts.Count - 1; i > 0; i--)
            if (_project.Cuts[i].From <= _project.Cuts[i - 1].To)
            {
                _project.Cuts[i - 1] = (_project.Cuts[i - 1].From, Math.Max(_project.Cuts[i - 1].To, _project.Cuts[i].To));
                _project.Cuts.RemoveAt(i);
            }
        UpdateTexts();
    }

    private void ClearCuts()
    {
        _project.Cuts.Clear();
        _cutFrom = null;
        _cutButton.Content = "Cut from here";
        UpdateTexts();
    }

    private async Task CutIdleAsync()
    {
        if (_busy) return;
        _busy = true;
        Play(false);
        _idleButton.IsEnabled = false;
        _progress.IsVisible = true;
        _status.Text = "Looking for idle parts…";
        try
        {
            var idle = await FindIdleAsync(_source, 2.0, new Progress<double>(p => _progress.Value = p));
            foreach (var (s, e) in idle) AddCut(s, e);
            _status.Text = idle.Count == 0 ? "No idle parts: something moves all the way through."
                : $"Cut {idle.Count} idle part{(idle.Count == 1 ? "" : "s")}, {Clock(idle.Sum(i => i.End - i.Start), true)} in all.";
        }
        catch (Exception ex) { _status.Text = "Couldn't look through the video: " + ex.Message; }
        finally
        {
            _progress.IsVisible = false;
            _idleButton.IsEnabled = true;
            _busy = false;
        }
    }

    /// <summary>
    /// Stretches of at least <paramref name="minimum"/> seconds where the picture barely changes
    /// (no pointer moving, nothing typed or scrolling), less a short margin each side so cuts
    /// don't feel abrupt. The video is looked at four times a second, small.
    /// </summary>
    public static async Task<List<(double Start, double End)>> FindIdleAsync(string path, double minimum = 2.0, IProgress<double>? progress = null)
    {
        const double Step = 0.25;
        const int Fw = 160, Fh = 90;
        double length = 0;
        var probe = await ScreenGrabber.OutputOf("ffprobe", $"-v error -show_entries format=duration -of csv=p=0 \"{path}\"");
        double.TryParse(probe?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out length);
        var idle = new List<(double, double)>();
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ffmpeg",
            $"-nostdin -hide_banner -loglevel error -i \"{path}\" -vf \"fps=4,scale={Fw}:{Fh}\" -f rawvideo -pix_fmt bgra -")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        _ = p.StandardError.ReadToEndAsync();
        var stream = p.StandardOutput.BaseStream;
        byte[]? previous = null;
        double stillFrom = -1, t = 0;
        await Task.Run(async () =>
        {
            for (int n = 0; ; n++)
            {
                var px = new byte[Fw * Fh * 4];
                if (!await AutoZoom.ReadFull(stream, px)) break;
                t = n * Step;
                bool still = previous != null && Changed(previous, px) < 0.002;
                if (still && stillFrom < 0) stillFrom = t - Step;
                if (!still && stillFrom >= 0)
                {
                    Close(stillFrom, t - Step);
                    stillFrom = -1;
                }
                previous = px;
                if (n % 20 == 0 && length > 0) progress?.Report(Math.Min(1, t / length));
            }
        });
        await p.WaitForExitAsync();
        if (stillFrom >= 0) Close(stillFrom, length > 0 ? length : t);
        return idle;

        void Close(double from, double to)
        {
            const double Margin = 0.4;
            if (to - from >= minimum) idle.Add((from + Margin, to - Margin));
        }
    }

    /// <summary>The share of pixels that changed noticeably between two small frames.</summary>
    private static double Changed(byte[] a, byte[] b)
    {
        int changed = 0, n = a.Length / 4;
        for (int i = 0; i < a.Length; i += 4)
            if (Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]) > 24) changed++;
        return changed / (double)n;
    }

    /// <summary>The red bands under the scrubber for cuts, and the zooms in the accent colour above them.</summary>
    private void DrawBands()
    {
        _bands.Children.Clear();
        double width = _bands.Bounds.Width, length = _project.Duration;
        if (width <= 0 || length <= 0) return;
        if (_zooms != null)
            foreach (var z in _zooms.Zooms)
            {
                var band = new Rectangle { Height = 3, Width = Math.Max(2, (z.End - z.Start) / length * width), RadiusX = 1.5, RadiusY = 1.5, Fill = new SolidColorBrush(Colours.Accent), Opacity = 0.85 };
                Canvas.SetLeft(band, z.Start / length * width);
                Canvas.SetTop(band, 0);
                _bands.Children.Add(band);
            }
        foreach (var (s, e) in _project.Cuts)
        {
            var band = new Rectangle { Height = 4, Width = Math.Max(2, (e - s) / length * width), RadiusX = 2, RadiusY = 2, Fill = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)) };
            Canvas.SetLeft(band, s / length * width);
            Canvas.SetTop(band, 5);
            _bands.Children.Add(band);
        }
    }

    private void UpdateTexts()
    {
        var (start, end) = Range;
        _startText.Text = Clock(start, true);
        _endText.Text = Clock(end, true);
        _keptText.Text = $"{Clock(_project.OutputLength, true)} kept" + (_project.Speed != 1 ? $" (at {_project.Speed:0.##}×)" : "");
        _cutsText.Text = _project.Cuts.Count == 0 ? "" : $"{_project.Cuts.Count} cut{(_project.Cuts.Count == 1 ? "" : "s")}: " +
                         string.Join(", ", _project.Cuts.Select(c => $"{Clock(c.From, true)}–{Clock(c.To, true)}"));
        DrawBands();
        ShowTime();
    }

    // ---- crop ----------------------------------------------------------------------------

    private void SetCropping(bool on)
    {
        if (on) Play(false);
        _stage.Cropping = on;
        _cropButton.Content = on ? "Done" : "Crop";
    }

    private void ResetCrop()
    {
        _stage.ResetCrop();
        if (_cropShapes.Children[0] is RadioButton free) free.IsChecked = true;
    }

    /// <summary>"16:9" → 1.777…; "0" → 0 (any shape).</summary>
    internal static double Ratio(string tag)
    {
        var parts = tag.Split(':');
        return parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double a)
               && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double b) && b > 0 ? a / b : 0;
    }

    /// <summary>The crop changed on the picture: into the project, as fractions.</summary>
    internal void CropChanged(PixelRect? crop)
    {
        _project.Crop = crop is { } c ? new Rect(c.X / (double)W, c.Y / (double)H, c.Width / (double)W, c.Height / (double)H) : null;
        _cropText.Text = crop is { } k ? $"{k.Width} × {k.Height}" : "";
        UpdateTexts();
    }

    // ---- text, arrows and blurs on the picture --------------------------------------------

    internal VideoOverlay? Chosen { get; private set; }
    internal VideoBlur? ChosenBlur { get; private set; }
    internal bool ArrowMode { get; private set; }
    internal bool BlurMode { get; private set; }

    private void AddText()
    {
        string text = (_text.Text ?? "").Trim();
        if (text.Length == 0) { _status.Text = "Type the words first, then Add text."; _text.Focus(); return; }
        var o = new VideoOverlay { Kind = VideoOverlay.Kinds.Text, Text = text, At = new Point(0.5, 0.82), Start = _now, End = Math.Min(_project.Duration, _now + 3) };
        _project.Overlays.Add(o);
        Chosen = o;
        _text.Text = "";
        Rebuild();
    }

    private void ToggleArrow()
    {
        ArrowMode = !ArrowMode;
        BlurMode = false;
        _arrowButton.Content = ArrowMode ? "Drag on the video…" : "Add an arrow";
        _blurButton.Content = "Add a blur box";
        if (ArrowMode) Play(false);
    }

    private void ToggleBlur()
    {
        BlurMode = !BlurMode;
        ArrowMode = false;
        _blurButton.Content = BlurMode ? "Drag over the video…" : "Add a blur box";
        _arrowButton.Content = "Add an arrow";
        if (BlurMode) Play(false);
    }

    /// <summary>Overlays due now (and the one being worked on), with the word being said lit.</summary>
    internal IEnumerable<(VideoOverlay O, bool Due)> Showing()
    {
        foreach (var o in _project.Overlays)
        {
            bool due = _now >= o.Start && _now < o.End;
            if (!due && o != Chosen) continue;
            if (o.Kind == VideoOverlay.Kinds.Caption && o.Words != "Line") o.Highlight = due ? CaptionLook.WordAt(o, _now) : -1;
            yield return (o, due);
        }
    }

    internal IEnumerable<(VideoBlur B, bool Due)> BlursShowing() =>
        _project.Blurs.Select(b => (b, _now >= b.Start && _now < b.End)).Where(x => x.Item2 || x.b == ChosenBlur);

    /// <summary>Where an overlay is on the frame, in video pixels (subtitles sit on what's kept).</summary>
    internal Rect OverlayBox(VideoOverlay o)
    {
        if (o.Kind != VideoOverlay.Kinds.Caption) return CaptionLook.Bounds(o, W, H);
        var c = _project.CropPixels();
        return CaptionLook.Bounds(o, c.Width, c.Height).Translate(new Vector(c.X, c.Y));
    }

    private VideoOverlay? OverlayAt(Point video)
    {
        foreach (var (o, _) in Showing().Reverse())
            if (OverlayBox(o).Contains(video)) return o;
        return null;
    }

    private VideoBlur? BlurAt(Point video)
    {
        foreach (var (b, _) in BlursShowing().Reverse())
            if (new Rect(b.Area.X * W, b.Area.Y * H, b.Area.Width * W, b.Area.Height * H).Contains(video)) return b;
        return null;
    }

    private VideoOverlay? _grabbed;
    private VideoBlur? _blurGrabbed;
    private Point _grabFrom;
    private bool _dragged;

    /// <summary>A press on the picture (video pixels): a new arrow or blur, or one picked up to move; otherwise play or pause on release.</summary>
    internal void Pressed(Point p)
    {
        _grabFrom = p;
        _dragged = false;
        _grabbed = null;
        _blurGrabbed = null;
        if (BlurMode)
        {
            _blurGrabbed = new VideoBlur { Area = new Rect(p.X / W, p.Y / H, 0, 0), Start = _now, End = _project.Duration };
            _project.Blurs.Add(_blurGrabbed);
            ChosenBlur = _blurGrabbed;
            return;
        }
        if (ArrowMode)
        {
            _grabbed = new VideoOverlay { Kind = VideoOverlay.Kinds.Arrow, At = new Point(p.X / W, p.Y / H), To = new Point(p.X / W, p.Y / H), Start = _now, End = Math.Min(_project.Duration, _now + 3) };
            _project.Overlays.Add(_grabbed);
            Chosen = _grabbed;
            return;
        }
        // labels and arrows sit on top of blur boxes
        _grabbed = OverlayAt(p);
        if (_grabbed != null) { Chosen = _grabbed; Play(false); return; }
        _blurGrabbed = BlurAt(p);
        if (_blurGrabbed != null) { ChosenBlur = _blurGrabbed; Play(false); }
    }

    internal void Dragged(Point p)
    {
        if (!_dragged && Math.Abs(p.X - _grabFrom.X) + Math.Abs(p.Y - _grabFrom.Y) < 3) return;
        _dragged = true;
        if (BlurMode && _blurGrabbed != null)
        {
            var r = new Rect(_grabFrom, p).Normalize().Intersect(new Rect(0, 0, W, H));
            _blurGrabbed.Area = new Rect(r.X / W, r.Y / H, r.Width / W, r.Height / H);
        }
        else if (_blurGrabbed != null)
        {
            var d = p - _grabFrom;
            var a = _blurGrabbed.Area;
            _blurGrabbed.Area = new Rect(Math.Clamp(a.X + d.X / W, 0, 1 - a.Width), Math.Clamp(a.Y + d.Y / H, 0, 1 - a.Height), a.Width, a.Height);
            _grabFrom = p;
        }
        else if (ArrowMode && _grabbed != null) _grabbed.To = new Point(p.X / W, p.Y / H);
        else if (_grabbed != null)
        {
            var d = p - _grabFrom;
            if (_grabbed.Kind == VideoOverlay.Kinds.Caption)
            {
                // dragging one subtitle moves them all, so they stay in one place through the video
                var c = _project.CropPixels();
                foreach (var o in Captions) o.At = new Point(o.At.X, Math.Clamp(o.At.Y + d.Y / c.Height, 0.2, 0.97));
            }
            else
            {
                _grabbed.At = new Point(_grabbed.At.X + d.X / W, _grabbed.At.Y + d.Y / H);
                _grabbed.To = new Point(_grabbed.To.X + d.X / W, _grabbed.To.Y + d.Y / H);
            }
            _grabFrom = p;
        }
        _stage.InvalidateVisual();
    }

    internal void Released()
    {
        if (BlurMode)
        {
            if (_blurGrabbed != null && (_blurGrabbed.Area.Width * W < 6 || _blurGrabbed.Area.Height * H < 6)) { _project.Blurs.Remove(_blurGrabbed); ChosenBlur = null; }
            ToggleBlur();
        }
        else if (ArrowMode)
        {
            if (_grabbed != null)
            {
                var d = new Point((_grabbed.To.X - _grabbed.At.X) * W, (_grabbed.To.Y - _grabbed.At.Y) * H);
                if (Math.Sqrt(d.X * d.X + d.Y * d.Y) < 12) { _project.Overlays.Remove(_grabbed); Chosen = null; }
            }
            ToggleArrow();
        }
        else if (!_dragged && _grabbed == null && _blurGrabbed == null)
        {
            // a click that didn't pick anything up plays or pauses, as on the video itself
            Chosen = null;
            ChosenBlur = null;
            Play(!_playing);
        }
        _grabbed = null;
        _blurGrabbed = null;
        Rebuild();
    }

    /// <summary>Reads the frame showing now and blurs every private detail on it, from here to the end.</summary>
    private async Task FindPrivateAsync()
    {
        if (_busy) return;
        Play(false);
        if (!Ocr.IsAvailable) { _status.Text = Ocr.InstallHint; return; }
        double now = _now;
        _findButton.IsEnabled = false;
        _status.Text = "Reading this frame…";
        try
        {
            var frame = await FrameAtAsync(now);
            if (frame == null) { _status.Text = "Couldn't read this frame."; return; }
            var found = await PrivateMenu.FindAsync(frame);
            int added = 0;
            foreach (var f in found)
            {
                double px = Math.Max(4, f.Height * 0.25), py = Math.Max(3, f.Height * 0.2);
                var padded = new Rect(f.X - px, f.Y - py, f.Width + 2 * px, f.Height + 2 * py).Intersect(new Rect(0, 0, W, H));
                // a detail that's already covered at this moment isn't added twice
                if (_project.Blurs.Any(b => now >= b.Start && now < b.End && new Rect(b.Area.X * W, b.Area.Y * H, b.Area.Width * W, b.Area.Height * H).Contains(padded))) continue;
                _project.Blurs.Add(new VideoBlur { Area = new Rect(padded.X / W, padded.Y / H, padded.Width / W, padded.Height / H), Start = now, End = _project.Duration, Label = Overskin.Shared.PrivateDetails.Describe(new[] { f.Kind }) });
                added++;
            }
            _status.Text = added == 0 ? "Nothing private found on this frame." : $"Blurred {added} private detail{(added == 1 ? "" : "s")} from here on. Use From and To to change when.";
            Rebuild();
        }
        catch (Exception ex) { _status.Text = "Couldn't read this frame: " + ex.Message; }
        finally { _findButton.IsEnabled = true; }
    }

    /// <summary>The frame at a moment at full size, read with ffmpeg.</summary>
    private async Task<Bitmap?> FrameAtAsync(double t)
    {
        var file = System.IO.Path.Combine(_work, $"still-{Guid.NewGuid():N}.png");
        var (ok, _) = await ScreenGrabber.RunAsync("ffmpeg", $"-hide_banner -loglevel error -y -ss {t.ToString("0.###", CultureInfo.InvariantCulture)} -i \"{_source}\" -frames:v 1 \"{file}\"", 60);
        if (!ok || !File.Exists(file)) return null;
        var b = new Bitmap(file);
        return Imaging.FromPixels(Imaging.Pixels(b, out _), b.PixelSize);
    }

    /// <summary>The frame showing now as a picture: copied, and opened in the screenshot editor.</summary>
    private async Task GrabFrameAsync()
    {
        Play(false);
        try
        {
            var frame = await FrameAtAsync(_now);
            if (frame == null) { _status.Text = "Couldn't grab the frame."; return; }
            await Desktop.CopyImageAsync(frame, this);
            new EditorWindow(frame, null).Show();
            _status.Text = "Frame copied and opened in the editor.";
        }
        catch (Exception ex) { _status.Text = "Couldn't grab the frame: " + ex.Message; }
    }

    // ---- zooms ----------------------------------------------------------------------------

    private Control ZoomCard()
    {
        var card = Ui.Card(new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Ui.Label("Zooms", "h2"),
                Ui.Label("Where it zooms in, following the pointer. Change them and save: the video is made again from the plain recording.", "dim"),
                _zoomRows,
                Tip(Ui.Button("Add a zoom here", AddZoom), "Zooms in from where the video is now for 3 seconds, following the pointer"),
            },
        });
        card.IsVisible = _zooms != null;
        return card;
    }

    private void AddZoom()
    {
        if (_zooms == null) return;
        _zooms.Zooms.Add(new ZoomSegment { Start = _now, End = Math.Min(_project.Duration, _now + 3), Zoom = _zooms.Zooms.LastOrDefault()?.Zoom ?? 1.8 });
        ZoomsChanged();
    }

    private void ZoomsChanged()
    {
        _zoomsChanged = true;
        _zooms!.Zooms.Sort((a, b) => a.Start.CompareTo(b.Start));
        Rebuild();
    }

    // ---- the lists ------------------------------------------------------------------------

    private Control Row(string title, string when, bool chosen, Action pick, params (string Label, string Tip, Action Act)[] buttons)
    {
        var text = new StackPanel
        {
            Cursor = new Cursor(StandardCursorType.Hand),
            Children =
            {
                new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = chosen ? FontWeight.SemiBold : FontWeight.Normal },
                new TextBlock { Text = when, FontSize = 11, Opacity = 0.65 },
            },
        };
        text.PointerReleased += (_, _) => pick();
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (label, tip, act) in buttons)
        {
            var b = new Button { Content = label, Padding = new Thickness(7, 2), FontSize = 11.5 };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => act();
            row.Children.Add(b);
        }
        return new DockPanel { Children = { Docked(row, Dock.Right), text } };
    }

    /// <summary>The rows for text and arrows, blurs, zooms and subtitles; the bands; the picture.</summary>
    private void Rebuild()
    {
        _overlayRows.Children.Clear();
        foreach (var o in _project.Overlays.Where(o => o.Kind != VideoOverlay.Kinds.Caption).ToList())
            _overlayRows.Children.Add(Row(o.Kind == VideoOverlay.Kinds.Arrow ? "Arrow" : $"“{o.Text}”", $"{Clock(o.Start, true)} to {Clock(o.End, true)}", o == Chosen,
                () => { Chosen = o; Seek(o.Start); Rebuild(); },
                ("From", "Starts where the video is now", () => { o.Start = Math.Min(_now, o.End - 0.1); Rebuild(); }),
                ("To", "Ends where the video is now", () => { o.End = Math.Max(_now, o.Start + 0.1); Rebuild(); }),
                ("✕", "Remove", () => { _project.Overlays.Remove(o); if (Chosen == o) Chosen = null; Rebuild(); })));

        _blurRows.Children.Clear();
        int n = 0;
        foreach (var b in _project.Blurs.ToList())
        {
            n++;
            _blurRows.Children.Add(Row(b.Label.Length > 0 ? "Blur: " + b.Label : $"Blur {n}", $"{Clock(b.Start, true)} to {Clock(b.End, true)}", b == ChosenBlur,
                () => { ChosenBlur = b; Seek(b.Start); Rebuild(); },
                ("From", "Starts where the video is now", () => { b.Start = Math.Min(_now, b.End - 0.1); Rebuild(); }),
                ("To", "Ends where the video is now", () => { b.End = Math.Max(_now, b.Start + 0.1); Rebuild(); }),
                ("✕", "Remove", () => { _project.Blurs.Remove(b); if (ChosenBlur == b) ChosenBlur = null; Rebuild(); })));
        }

        _zoomRows.Children.Clear();
        if (_zooms != null)
        {
            if (_zooms.Zooms.Count == 0) _zoomRows.Children.Add(Ui.Label("No zooms. Add one where the video is now.", "dim"));
            foreach (var z in _zooms.Zooms.ToList())
                _zoomRows.Children.Add(Row($"{z.Zoom:0.0}× zoom", $"{Clock(z.Start, true)} to {Clock(z.End, true)}", false, () => Seek(z.Start),
                    ("−", "Zoom in less", () => { z.Zoom = Math.Max(1.2, Math.Round(z.Zoom - 0.2, 1)); ZoomsChanged(); }),
                    ("+", "Zoom in more", () => { z.Zoom = Math.Min(3.5, Math.Round(z.Zoom + 0.2, 1)); ZoomsChanged(); }),
                    ("From", "Starts where the video is now", () => { z.Start = Math.Min(_now, z.End - 0.2); ZoomsChanged(); }),
                    ("To", "Ends where the video is now", () => { z.End = Math.Max(_now, z.Start + 0.2); ZoomsChanged(); }),
                    ("✕", "Remove this zoom", () => { _zooms.Zooms.Remove(z); ZoomsChanged(); })));
        }

        _captionRows.Children.Clear();
        foreach (var o in Captions.OrderBy(o => o.Start).ToList())
        {
            var remove = Ui.IconButton(Icons.Close, "Remove this line", () => { _project.Overlays.Remove(o); Rebuild(); });
            var when = Ui.Label($"{Clock(o.Start, true)} ", "dim");
            when.Cursor = new Cursor(StandardCursorType.Hand);
            when.PointerPressed += (_, _) => Seek(o.Start);
            var box = new TextBox { Text = o.Text, FontSize = 12 };
            box.TextChanged += (_, _) => { o.Text = box.Text ?? ""; _stage.InvalidateVisual(); };
            _captionRows.Children.Add(new DockPanel { Children = { Docked(remove, Dock.Right), Docked(when, Dock.Left), box } });
        }
        UpdateTexts();
        _stage.InvalidateVisual();
    }

    // ---- subtitles ------------------------------------------------------------------------

    private async Task MakeSubtitlesAsync()
    {
        if (!Overskin.Subtitles.Available) { _status.Text = "This copy of Overskin was built without the speech model."; return; }
        if (!_project.HasSound) { _status.Text = "This video has no sound to listen to."; return; }
        try
        {
            _progress.IsVisible = true;
            var progress = new Progress<double>(p => { _status.Text = $"Listening… {p * 100:0}%"; _progress.Value = p; });
            var lines = await Overskin.Subtitles.MakeAsync(_source, progress);
            _project.Overlays.RemoveAll(o => o.Kind == VideoOverlay.Kinds.Caption);
            foreach (var l in lines)
                _project.Overlays.Add(new VideoOverlay
                {
                    Kind = VideoOverlay.Kinds.Caption, Text = l.Text, Start = l.Start, End = l.End,
                    Look = CaptionLook.Looks[Math.Max(0, _look.SelectedIndex)].Name, Words = CaptionLook.WordModes[Math.Max(0, _words.SelectedIndex)].Name,
                });
            _status.Text = lines.Count == 0 ? "Nobody speaks in this video." : $"{lines.Count} lines. Fix any word by typing over it; drag them on the picture to move them.";
            Rebuild();
        }
        catch (Exception ex) { _status.Text = "Couldn't make subtitles: " + ex.Message; }
        finally { _progress.IsVisible = false; }
    }

    // ---- saving ---------------------------------------------------------------------------

    /// <summary>"name trimmed.mp4" beside the original (2, 3… when taken).</summary>
    internal static string TrimmedName(string path)
    {
        string dir = System.IO.Path.GetDirectoryName(path) ?? "", name = System.IO.Path.GetFileNameWithoutExtension(path);
        string candidate = System.IO.Path.Combine(dir, name + " trimmed.mp4");
        for (int i = 2; File.Exists(candidate); i++) candidate = System.IO.Path.Combine(dir, $"{name} trimmed {i}.mp4");
        return candidate;
    }

    private string SmallName()
    {
        string dir = System.IO.Path.GetDirectoryName(_source) ?? "", name = System.IO.Path.GetFileNameWithoutExtension(_source);
        string output = System.IO.Path.Combine(dir, $"{name} {_shareSize.Value:0} MB.mp4");
        for (int i = 2; File.Exists(output); i++) output = System.IO.Path.Combine(dir, $"{name} {_shareSize.Value:0} MB {i}.mp4");
        return output;
    }

    private async Task SaveAsAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "Save video",
            SuggestedFileName = System.IO.Path.GetFileName(TrimmedName(_source)),
            DefaultExtension = "mp4",
            FileTypeChoices = new[] { new Avalonia.Platform.Storage.FilePickerFileType("MP4 video") { Patterns = new[] { "*.mp4" } } },
        });
        var path = file != null ? Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(file) : null;
        if (path == null) return;
        if (System.IO.Path.GetFullPath(path).Equals(System.IO.Path.GetFullPath(_source), StringComparison.OrdinalIgnoreCase))
        {
            _status.Text = "Pick a different name: the original is still open here.";
            return;
        }
        await SaveToAsync(path);
    }

    private bool Untouched =>
        _project.Start <= 0.05 && (_project.End <= 0 || _project.End >= _project.Duration - 0.05) && _project.Cuts.Count == 0 && _project.Overlays.Count == 0
        && _project.Blurs.Count == 0 && _project.Crop == null && Math.Abs(_project.Speed - 1) < 0.001 && !_zoomsChanged;

    /// <summary>Makes the finished video at <paramref name="output"/> (and its .srt), within <paramref name="maxBytes"/> when given.</summary>
    internal async Task<bool> SaveToAsync(string output, long? maxBytes = null)
    {
        if (_busy) return false;
        if (maxBytes == null && Untouched) { _status.Text = "Trim, cut, crop or add something first: this is the whole video as it is."; return false; }
        if (_project.Kept().Count == 0) { _status.Text = "Nothing is left to save: everything is trimmed or cut."; return false; }
        _busy = true;
        Play(false);
        _sidePanel.IsEnabled = false;
        _progress.IsVisible = true;
        _progress.Value = 0;
        string work = System.IO.Path.Combine(_work, "save-" + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            Directory.CreateDirectory(work);
            string input = _source;
            if (_zooms != null && _zoomsChanged)
            {
                // edited zooms: the zoomed video is made afresh from the plain recording first
                _status.Text = "Making the zooms…";
                string zoomed = System.IO.Path.Combine(work, "zoomed.mp4");
                await ZoomTrack.RenderAsync(ZoomTrack.RawPath(_source), zoomed, _zooms, new Progress<double>(p => _progress.Value = p * 0.5));
                input = zoomed;
            }
            _status.Text = "Saving…";
            string temp = output + ".part.mp4";
            await MakeAsync(input, _project, temp, work, maxBytes);
            File.Move(temp, output, overwrite: true);
            if (Captions.Any()) await File.WriteAllTextAsync(System.IO.Path.ChangeExtension(output, ".srt"), _project.Srt());
            App.Current.NotifyLibraryChanged();
            long size = new FileInfo(output).Length;
            _status.Text = $"Saved {System.IO.Path.GetFileName(output)} ({(size >= 1 << 20 ? $"{size / 1048576.0:0.0} MB" : $"{Math.Max(1, size / 1024)} KB")})" +
                           (Captions.Any() ? ", with an .srt beside it" : "");
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("video save: " + ex);
            _status.Text = "Couldn't save the video: " + ex.Message;
            return false;
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { }
            _progress.IsVisible = false;
            _sidePanel.IsEnabled = true;
            _busy = false;
        }
    }

    /// <summary>For the self-test: the file size after each pass of the last size-limited save.</summary>
    internal static List<long> LastPasses { get; } = new();

    /// <summary>
    /// Makes the finished video: each overlay (each word of word-by-word subtitles) drawn as a
    /// see-through picture the size of the finished video, then one ffmpeg pass. With
    /// <paramref name="maxBytes"/>, the picture quality (and if need be its size) is chosen so it
    /// fits, and it's made again with fewer bits until it does. Must start on the UI thread (the
    /// overlays are drawn there).
    /// </summary>
    public static async Task MakeAsync(string source, VideoProject project, string video, string workDir, long? maxBytes = null)
    {
        LastPasses.Clear();
        Directory.CreateDirectory(workDir);
        var crop = project.CropPixels();
        var inputs = new List<string>();
        var times = new List<(double, double)>();
        foreach (var o in CaptionLook.Expand(project.Overlays.Where(o => o.Kind != VideoOverlay.Kinds.Caption || o.Text.Trim().Length > 0)))
        {
            if (project.OutputSpan(o.Start, o.End) is not { } span) continue;
            var file = System.IO.Path.Combine(workDir, $"o{inputs.Count:0000}.png");
            var picture = Imaging.Render(new PixelSize(crop.Width, crop.Height), ctx =>
            {
                // text and arrows were placed on the whole frame: they move with the crop
                if (o.Kind == VideoOverlay.Kinds.Caption) CaptionLook.Draw(ctx, o, crop.Width, crop.Height);
                else
                    using (ctx.PushTransform(Matrix.CreateTranslation(-crop.X, -crop.Y)))
                        CaptionLook.Draw(ctx, o, project.Width, project.Height);
            });
            Imaging.SavePng(picture, file);
            inputs.Add(file);
            times.Add(span);
        }

        long bitrate = 0;
        if (maxBytes is { } limit)
        {
            // what the size allows per second, less the sound and a margin for the container
            double seconds = Math.Max(0.5, project.OutputLength);
            long audio = project.HasSound ? 96_000 : 0;
            bitrate = (long)Math.Clamp(limit * 8 * 0.9 / seconds - audio, 150_000, 12_000_000);
            // too few bits for this many pixels looks blocky: make the picture smaller to suit
            double needed = crop.Width * (double)crop.Height * 30 * 0.07;
            project.ScaleTo = null;
            if (bitrate < needed)
            {
                double k = Math.Max(0.3, Math.Sqrt(bitrate / needed));
                project.ScaleTo = new PixelSize(Math.Max(320, (int)(crop.Width * k) & ~1), Math.Max(180, (int)(crop.Height * k) & ~1));
            }
        }

        try
        {
            for (int pass = 0; ; pass++)
            {
                var args = new System.Text.StringBuilder($"-hide_banner -loglevel error -y -i \"{source}\"");
                foreach (var i in inputs) args.Append($" -i \"{i}\"");
                args.Append($" -filter_complex \"{project.Filter(times)}\" -map \"[out]\"");
                if (project.HasSound) args.Append($" -map \"[a]\" -c:a aac -b:a {(maxBytes != null ? "96k" : "160k")}");
                args.Append(maxBytes != null
                    ? $" -c:v libx264 -preset medium -b:v {bitrate} -maxrate {bitrate * 6 / 5} -bufsize {bitrate * 2} -movflags +faststart \"{video}\""
                    : $" -c:v libx264 -preset veryfast -crf 20 -movflags +faststart \"{video}\"");
                var (ok, error) = await ScreenGrabber.RunAsync("ffmpeg", args.ToString(), 3600);
                if (!ok || !File.Exists(video)) throw new InvalidOperationException(error.Length > 0 ? error : "ffmpeg made nothing");
                if (maxBytes is not { } cap) return;
                long size = new FileInfo(video).Length;
                LastPasses.Add(size);
                if (size <= cap || pass >= 3) return;
                // the encoder can overshoot (very busy pictures especially): again with fewer bits
                // and, if it's well over, fewer pixels, until it fits
                double over = size / (double)cap;
                bitrate = (long)Math.Max(100_000, bitrate / over * 0.85);
                if (over > 1.15)
                {
                    var now = project.ScaleTo ?? new PixelSize(crop.Width, crop.Height);
                    double k = Math.Max(0.5, Math.Sqrt(1 / over) * 0.95);
                    project.ScaleTo = new PixelSize(Math.Max(320, (int)(now.Width * k) & ~1), Math.Max(180, (int)(now.Height * k) & ~1));
                }
            }
        }
        finally { project.ScaleTo = null; }
    }

    // ---- a GIF of part of it ------------------------------------------------------------------

    private const double GifLongest = 15;

    /// <summary>The trimmed part (or five seconds from here), opened in the GIF editor with what's said in it as the caption.</summary>
    private async Task MakeGifAsync()
    {
        if (_busy) return;
        var (start, end) = Range;
        if (end - start > GifLongest || end - start < 0.3)
        {
            start = Math.Clamp(_now, 0, Math.Max(0, _project.Duration - 0.5));
            end = Math.Min(_project.Duration, start + 5);
        }
        _busy = true;
        Play(false);
        _status.Text = "Making the GIF…";
        try
        {
            var clip = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"overskin-gif-part-{Guid.NewGuid():N}.mp4");
            var (ok, error) = await ScreenGrabber.RunAsync("ffmpeg",
                $"-hide_banner -loglevel error -y -ss {start.ToString("0.###", CultureInfo.InvariantCulture)} -to {end.ToString("0.###", CultureInfo.InvariantCulture)} -i \"{_source}\" -an -c:v libx264 -preset veryfast -crf 18 -pix_fmt yuv420p \"{clip}\"", 300);
            if (!ok) { _status.Text = "Couldn't read that part of the video: " + error; return; }
            string said = string.Join(" ", Captions.Where(c => c.End > start + 0.1 && c.Start < end - 0.1).OrderBy(c => c.Start).Select(c => c.Text.Trim()));
            await GifEditor.OpenAsync(clip, said.Length > 0 ? said : null);
            _status.Text = "";
        }
        catch (Exception ex) { _status.Text = "Couldn't make the GIF: " + ex.Message; }
        finally { _busy = false; }
    }

    // ---- keys -----------------------------------------------------------------------------

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (FocusManager?.GetFocusedElement() is TextBox) return;
        switch (e.Key)
        {
            case Key.Space: Play(!_playing); break;
            case Key.I: StartHere(); break;
            case Key.O: EndHere(); break;
            case Key.X: Cut(); break;
            case Key.Left: Seek(_now - 1); break;
            case Key.Right: Seek(_now + 1); break;
            case Key.Escape when _stage.Cropping: SetCropping(false); break;
            default: return;
        }
        e.Handled = true;
    }

    // ---- for the self-test ----

    internal VideoProject ProjectForTest => _project;
    internal ZoomTrack? ZoomsForTest => _zooms;
    internal void SeekForTest(double t) => Seek(t);
    internal void CutForTest() => Cut();
    internal void PressForTest(Point a, Point b) { Pressed(a); Dragged(b); Released(); }
    internal void ArrowModeForTest() => ToggleArrow();
    internal void BlurModeForTest() => ToggleBlur();
    internal void SetCropForTest(PixelRect r) => _stage.SetCropForTest(r);
    internal void AddZoomForTest() => AddZoom();
    internal string StatusForTest => _status.Text ?? "";

    /// <summary>The frame, fitted, with blurs, text, arrows and subtitles over it, and the crop box.</summary>
    private sealed class VideoStage : Control
    {
        private readonly VideoEditor _ed;
        private PixelRect? _crop;
        private double _ratio;
        private int _handle = -1;
        private Point _from;
        private PixelRect _startCrop;
        private bool _pressed;

        public VideoStage(VideoEditor ed)
        {
            _ed = ed;
            ClipToBounds = true;
        }

        public bool Cropping
        {
            get => _cropping;
            set { _cropping = value; InvalidateVisual(); }
        }
        private bool _cropping;

        /// <summary>How the video sits on the stage: scale and where its top-left is.</summary>
        private (double Scale, double X, double Y) Fit()
        {
            var v = _ed.VideoSize;
            double s = Math.Min(Bounds.Width / Math.Max(1, v.Width), Bounds.Height / Math.Max(1, v.Height));
            return (s, (Bounds.Width - v.Width * s) / 2, (Bounds.Height - v.Height * s) / 2);
        }

        private Point ToVideo(Point p)
        {
            var (s, x, y) = Fit();
            return s <= 0 ? p : new Point((p.X - x) / s, (p.Y - y) / s);
        }

        public void SetRatio(double ratio)
        {
            _ratio = ratio;
            if (ratio > 0)
            {
                // the largest box of that shape, in the middle (or kept where the old box was)
                var v = _ed.VideoSize;
                var basis = _crop ?? new PixelRect(v);
                double w = basis.Width, h = w / ratio;
                if (h > basis.Height) { h = basis.Height; w = h * ratio; }
                if (_crop == null) { w = Math.Min(v.Width, v.Height * ratio); h = w / ratio; }
                int iw = Math.Max(16, (int)w & ~1), ih = Math.Max(16, (int)h & ~1);
                var c = new Point(basis.X + basis.Width / 2.0, basis.Y + basis.Height / 2.0);
                Set(new PixelRect((int)Math.Clamp(c.X - iw / 2.0, 0, v.Width - iw), (int)Math.Clamp(c.Y - ih / 2.0, 0, v.Height - ih), iw, ih));
            }
        }

        public void ResetCrop()
        {
            _ratio = 0;
            Set(null);
        }

        private void Set(PixelRect? crop)
        {
            if (crop is { } c)
            {
                var v = _ed.VideoSize;
                c = c.Intersect(new PixelRect(v));
                // H.264 needs even sizes
                crop = c.Width < 16 || c.Height < 16 ? null : new PixelRect(c.X, c.Y, c.Width & ~1, c.Height & ~1);
                if (crop is { } k && k.Width >= v.Width - 1 && k.Height >= v.Height - 1) crop = null;
            }
            _crop = crop;
            _ed.CropChanged(crop);
            InvalidateVisual();
        }

        internal void SetCropForTest(PixelRect r) => Set(r);

        private IReadOnlyList<Point> Handles(PixelRect r) => new Point[]
        {
            new(r.X, r.Y), new(r.X + r.Width / 2.0, r.Y), new(r.Right, r.Y), new(r.Right, r.Y + r.Height / 2.0),
            new(r.Right, r.Bottom), new(r.X + r.Width / 2.0, r.Bottom), new(r.X, r.Bottom), new(r.X, r.Y + r.Height / 2.0),
        };

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            Focus();
            var p = ToVideo(e.GetPosition(this));
            _pressed = true;
            e.Pointer.Capture(this);
            if (!_cropping) { _ed.Pressed(p); return; }
            // the crop box: a handle stretches it, inside moves it, outside starts a new one
            var (s, _, _) = Fit();
            _from = p;
            _handle = -1;
            if (_crop is { } c)
            {
                var hs = Handles(c);
                for (int i = 0; i < hs.Count; i++)
                    if (Math.Abs(hs[i].X - p.X) * s <= 8 && Math.Abs(hs[i].Y - p.Y) * s <= 8) { _handle = i; break; }
                if (_handle < 0 && new Rect(c.X, c.Y, c.Width, c.Height).Contains(p)) _handle = 8; // move
            }
            if (_handle < 0) { _handle = 9; _startCrop = new PixelRect((int)p.X, (int)p.Y, 0, 0); } // a new box
            else _startCrop = _crop!.Value;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            var p = ToVideo(e.GetPosition(this));
            if (!_pressed) return;
            if (!_cropping) { _ed.Dragged(p); return; }
            var v = _ed.VideoSize;
            double x0 = _startCrop.X, y0 = _startCrop.Y, x1 = _startCrop.Right, y1 = _startCrop.Bottom;
            switch (_handle)
            {
                case 8:
                {
                    double dx = Math.Clamp(p.X - _from.X, -x0, v.Width - x1), dy = Math.Clamp(p.Y - _from.Y, -y0, v.Height - y1);
                    Set(new PixelRect((int)(x0 + dx), (int)(y0 + dy), _startCrop.Width, _startCrop.Height));
                    return;
                }
                case 9:
                    x0 = _from.X; y0 = _from.Y; x1 = p.X; y1 = p.Y;
                    if (_ratio > 0)
                    {
                        double w = Math.Abs(x1 - x0), h = w / _ratio;
                        y1 = y0 + Math.Sign(y1 - y0 == 0 ? 1 : y1 - y0) * h;
                    }
                    break;
                default:
                    // corners: 0,2,4,6; sides: 1,3,5,7
                    if (_handle is 0 or 6 or 7) x0 = p.X;
                    if (_handle is 2 or 3 or 4) x1 = p.X;
                    if (_handle is 0 or 1 or 2) y0 = p.Y;
                    if (_handle is 4 or 5 or 6) y1 = p.Y;
                    if (_ratio > 0)
                    {
                        // keeping the shape: the width leads, the height follows (sides: whichever was pulled)
                        if (_handle is 1 or 5) { double w = Math.Abs(y1 - y0) * _ratio, cx = (x0 + x1) / 2; x0 = cx - w / 2; x1 = cx + w / 2; }
                        else
                        {
                            double h = Math.Abs(x1 - x0) / _ratio;
                            if (_handle is 0 or 1 or 2) y0 = y1 - h; else if (_handle is 4 or 5 or 6) y1 = y0 + h;
                            else { double cy = (y0 + y1) / 2; y0 = cy - h / 2; y1 = cy + h / 2; }
                        }
                    }
                    break;
            }
            var r = new Rect(new Point(x0, y0), new Point(x1, y1)).Normalize().Intersect(new Rect(0, 0, v.Width, v.Height));
            Set(new PixelRect((int)Math.Round(r.X), (int)Math.Round(r.Y), (int)Math.Round(r.Width), (int)Math.Round(r.Height)));
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            if (!_pressed) return;
            _pressed = false;
            e.Pointer.Capture(null);
            if (!_cropping) _ed.Released();
            _handle = -1;
        }

        public override void Render(DrawingContext ctx)
        {
            ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
            var (s, ox, oy) = Fit();
            if (s <= 0) return;
            var v = _ed.VideoSize;
            var place = new Rect(ox, oy, v.Width * s, v.Height * s);
            if (_ed.CurrentFrame() is { } frame)
                using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
                    ctx.DrawImage(frame, new Rect(frame.Size), place);
            using (ctx.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation(ox, oy)))
            {
                // blur boxes: the preview only covers them; the saved video is properly blurred
                foreach (var (b, due) in _ed.BlursShowing())
                {
                    var r = new Rect(b.Area.X * v.Width, b.Area.Y * v.Height, b.Area.Width * v.Width, b.Area.Height * v.Height);
                    ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(due ? (byte)236 : (byte)90, 0x8A, 0x8F, 0x98)),
                        b == _ed.ChosenBlur ? new Pen(new SolidColorBrush(Colours.Accent), 1.5 / s, new DashStyle(new double[] { 4, 3 }, 0)) : null, r, 3 / s, 3 / s);
                    if (due && r.Width * s > 50 && r.Height * s > 18)
                    {
                        var label = new FormattedText("Blurred", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Inter"), 11 / s, Brushes.White);
                        ctx.DrawText(label, new Point(r.X + (r.Width - label.Width) / 2, r.Y + (r.Height - label.Height) / 2));
                    }
                }
                var crop = _ed._project.CropPixels();
                foreach (var (o, due) in _ed.Showing())
                {
                    using (ctx.PushOpacity(due ? 1 : 0.45))
                    {
                        if (o.Kind == VideoOverlay.Kinds.Caption)
                            using (ctx.PushTransform(Matrix.CreateTranslation(crop.X, crop.Y)))
                                CaptionLook.Draw(ctx, o, crop.Width, crop.Height);
                        else CaptionLook.Draw(ctx, o, v.Width, v.Height);
                    }
                    if (o == _ed.Chosen)
                        ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Colours.Accent), 1 / s, new DashStyle(new double[] { 4, 3 }, 0)), _ed.OverlayBox(o).Inflate(3 / s));
                }
                // the crop: everything outside it darkened; while cropping, its handles and size
                if (_crop is { } c)
                {
                    var keep = new Rect(c.X, c.Y, c.Width, c.Height);
                    var outside = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, v.Width, v.Height)), new RectangleGeometry(keep));
                    ctx.DrawGeometry(new SolidColorBrush(Color.FromArgb(_cropping ? (byte)150 : (byte)200, 0, 0, 0)), null, outside);
                    ctx.DrawRectangle(null, new Pen(Brushes.White, 1.5 / s), keep);
                    if (_cropping)
                    {
                        double hs = 9 / s;
                        foreach (var h in Handles(c))
                            ctx.DrawRectangle(Brushes.White, new Pen(new SolidColorBrush(Colours.Accent), 1.5 / s), new Rect(h.X - hs / 2, h.Y - hs / 2, hs, hs));
                    }
                }
            }
            if (_cropping)
            {
                string text = _crop is { } k ? $"{k.Width} × {k.Height}" : "Drag on the picture to crop it";
                var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Inter"), 12, Brushes.White);
                ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(220, 18, 19, 22)), null, new Rect(place.X + 8, place.Y + 8, ft.Width + 16, ft.Height + 8), 4, 4);
                ctx.DrawText(ft, new Point(place.X + 16, place.Y + 12));
            }
        }
    }
}
