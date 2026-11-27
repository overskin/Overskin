using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Overskin;

/// <summary>What the GIF editor asks of the rest of the app.</summary>
public sealed class GifEditorHost
{
    public required string Folder { get; init; }
    /// <summary>Called after a save, so the library can show the new file.</summary>
    public required Action<string> Saved { get; init; }
}

/// <summary>Trims, speeds up, loops and captions a clip, then saves or copies it as a GIF.</summary>
public partial class GifEditorWindow : Window
{
    private readonly GifClip _clip;
    private readonly GifEditorHost _host;
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render);
    private List<(int Frame, int DelayMs)> _sequence = new();
    private int _position;
    private bool _playing = true;
    private bool _busy;
    private bool _syncing;
    private bool _resumeAfterAdjust;
    private readonly DispatcherTimer _settle = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _kept; // saved or copied at least once
    private string? _savedPath;

    public GifEditorWindow(GifClip clip, string name, GifEditorHost host)
    {
        InitializeComponent();
        _clip = clip;
        _host = host;
        TitleText.Text = name;

        _syncing = true;
        Scrubber.Maximum = TrimStart.Maximum = TrimEnd.Maximum = Math.Max(0, clip.Count - 1);
        TrimEnd.Value = clip.Count - 1;
        StyleCaption.IsChecked = true;
        FontLibrary.ShowName(FontName, ShownFont);
        _syncing = false;

        // a single picture doesn't need the timeline
        if (clip.Count == 1) PlayButton.IsEnabled = false;

        _timer.Tick += (_, _) => Advance();
        _settle.Tick += Settle_Tick;
        Rebuild();
        ShowFrame(_sequence.Count > 0 ? _sequence[0].Frame : 0);
        if (clip.Count > 1) Play(true);

        Loaded += (_, _) =>
        {
            Frame.Opacity = 0;
            Frame.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
            TopBox.Focus();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        int round = 2;
        NativeMethods.DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 33, ref round, sizeof(int));
    }

    // ---- settings ------------------------------------------------------------------------

    private static string Chosen(Panel panel) =>
        panel.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string ?? "";

    private CaptionStyle CapStyle =>
        StyleMeme.IsChecked == true ? CaptionStyle.Meme : StyleNone.IsChecked == true ? CaptionStyle.None
        : StyleBubble.IsChecked == true ? CaptionStyle.Bubble : CaptionStyle.Caption;

    /// <summary>The chosen font; empty for the style's usual one. Remembered for the next GIF.</summary>
    private static string _font = "";

    private Color? CapColour =>
        Chosen(ColourChoices) is { Length: > 0 } hex ? (Color)ColorConverter.ConvertFromString(hex) : null;

    private CaptionSettings Caption => new(CapStyle, TopBox.Text, BottomBox.Text, TextSize.Value,
        _font, CapColour, Chosen(BarChoices) == "Below", Chosen(TailChoices) is { Length: > 0 } tail ? tail : "Left");

    /// <summary>The font in use, by name, for the font button.</summary>
    private string ShownFont => _font.Length > 0 ? _font : CapStyle == CaptionStyle.Meme ? "Impact" : FontLibrary.CaptionDefault;

    private void Font_Click(object sender, RoutedEventArgs e) =>
        FontLibrary.ShowMenu(FontButton, ShownFont, name =>
        {
            _font = name;
            FontLibrary.ShowName(FontName, ShownFont);
            Changed();
        });

    /// <summary>"Normal", "Grey", "Sepia" or "Vivid".</summary>
    internal string Look => Chosen(LookChoices);
    internal bool Mirrored => MirrorSwitch.IsChecked == true;

    /// <summary>How many times the GIF plays: 0 for forever.</summary>
    private int Repeats => int.TryParse(Chosen(RepeatChoices), out int n) ? n : 0;

    private void Option_Click(object sender, RoutedEventArgs e) => Changed();

    private double Speed => double.TryParse(Chosen(SpeedChoices), System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out double s) ? s : 1;

    private int OutputWidth
    {
        get
        {
            int w = int.TryParse(Chosen(WidthChoices), out int v) ? v : 0;
            return w <= 0 || w > PictureWidth ? PictureWidth : w;
        }
    }

    /// <summary>Which frames play, in what order, for how long.</summary>
    private void Rebuild()
    {
        int a = (int)Math.Min(TrimStart.Value, TrimEnd.Value), b = (int)Math.Max(TrimStart.Value, TrimEnd.Value);
        var forward = Enumerable.Range(a, b - a + 1).ToList();
        var order = Chosen(LoopChoices) switch
        {
            "Reverse" => Enumerable.Reverse(forward).ToList(),
            "Boomerang" => forward.Concat(Enumerable.Reverse(forward).Skip(1).Take(Math.Max(0, forward.Count - 2))).ToList(),
            _ => forward,
        };
        double speed = Speed;
        var timed = order.Select(f => (Frame: f, Ms: (double)_clip.DelaysMs[f])).ToList();
        if (FewerSwitch.IsChecked == true && timed.Count > 2)
        {
            // every other frame, each shown for as long as the pair it stands for
            var half = new List<(int Frame, double Ms)>();
            for (int i = 0; i < timed.Count; i += 2)
                half.Add((timed[i].Frame, timed[i].Ms + (i + 1 < timed.Count ? timed[i + 1].Ms : 0)));
            timed = half;
        }
        _sequence = timed.Select(t => (t.Frame, Math.Max(20, (int)Math.Round(t.Ms / speed)))).ToList();
        _position = Math.Min(_position, Math.Max(0, _sequence.Count - 1));

        int total = _sequence.Sum(s => s.DelayMs);
        var size = CaptionRenderer.Layout(PictureWidth, PictureHeight, OutputWidth, Caption, out _);
        OutputText.Text = $"{(int)size.Width} × {(int)size.Height}";
        TrimText.Text = $"{_sequence.Count} frames  ·  {total / 1000.0:0.0} s";
    }

    private void Style_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        BottomPanel.Visibility = CapStyle == CaptionStyle.Meme ? Visibility.Visible : Visibility.Collapsed;
        TopLabel.Text = CapStyle == CaptionStyle.Meme ? "Top text" : "Caption";
        bool any = CapStyle is not (CaptionStyle.None or CaptionStyle.Bubble);
        TopBox.IsEnabled = FontButton.IsEnabled = ColourChoices.IsEnabled = any;
        // text size sets the bubble's depth too
        TextSize.IsEnabled = CapStyle != CaptionStyle.None;
        // the bar's colour for a white-bar caption, the letters' colour on the picture
        ColourLabel.Text = CapStyle == CaptionStyle.Meme ? "Text" : "Bar";
        BarPlace.Visibility = CapStyle is CaptionStyle.Meme or CaptionStyle.Bubble ? Visibility.Collapsed : Visibility.Visible;
        TailPlace.Visibility = CapStyle == CaptionStyle.Bubble ? Visibility.Visible : Visibility.Collapsed;
        FontLibrary.ShowName(FontName, ShownFont);
        Changed();
    }

    private void Caption_Changed(object sender, TextChangedEventArgs e) => Changed();
    private void Option_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => Changed();
    private void Option_Checked(object sender, RoutedEventArgs e) => Changed();

    private void Trim_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing || !IsInitialized) return;
        // Playback is paused while a handle moves: otherwise the timer and the drag both draw
        // frames and the preview flickers between them.
        BeginAdjust();

        // the handles can't pass each other
        _syncing = true;
        if (sender == TrimStart && TrimStart.Value > TrimEnd.Value) TrimStart.Value = TrimEnd.Value;
        if (sender == TrimEnd && TrimEnd.Value < TrimStart.Value) TrimEnd.Value = TrimStart.Value;
        _syncing = false;

        Rebuild();
        // show the edge being dragged, and carry on playing from there
        int edge = (int)((Slider)sender).Value;
        int at = _sequence.FindIndex(s => s.Frame == edge);
        if (at >= 0) _position = at;
        ShowFrame(edge);
    }

    /// <summary>Pauses playback while the trim handles or the scrubber move, and resumes it once
    /// they've been let go (if it was playing).</summary>
    private void BeginAdjust(bool resume = true)
    {
        if (_playing)
        {
            Play(false);
            _resumeAfterAdjust = resume;
        }
        else if (!resume) _resumeAfterAdjust = false;
        _settle.Stop();
        _settle.Start();
    }

    /// <summary>Whether a handle is still being held (the self-test stands in for a real mouse).</summary>
    internal static Func<bool> MouseHeld = () => Mouse.LeftButton == MouseButtonState.Pressed;

    private void Settle_Tick(object? sender, EventArgs e)
    {
        // still holding a handle: wait for the release
        if (MouseHeld()) return;
        _settle.Stop();
        if (_resumeAfterAdjust)
        {
            _resumeAfterAdjust = false;
            Play(true);
        }
    }

    /// <summary>The trimmed range, first and last frame.</summary>
    internal (int First, int Last) TrimRange =>
        ((int)Math.Min(TrimStart.Value, TrimEnd.Value), (int)Math.Max(TrimStart.Value, TrimEnd.Value));

    /// <summary>The frame on screen (for the self-test).</summary>
    internal int ShownFrame { get; private set; }
    internal bool IsPlaying => _playing;

    private void Changed()
    {
        if (_syncing || !IsInitialized || _clip == null) return;
        Rebuild();
        // redraw straight away, playing or not, so typing shows up as you type
        if (_sequence.Count > 0) ShowFrame(_sequence[_position].Frame);
    }

    // ---- playback ------------------------------------------------------------------------

    private void ShowFrame(int frame)
    {
        frame = Math.Clamp(frame, 0, _clip.Count - 1);
        ShownFrame = frame;
        // while cropping, the whole frame with no caption, under the crop box; otherwise the GIF as it will be
        if (Cropping) Preview.Show(_clip.FrameBitmap(frame), new CaptionSettings(CaptionStyle.None, "", "", 1), _clip.Width);
        else Preview.Show(Picture(frame), Caption, OutputWidth);
        _syncing = true;
        Scrubber.Value = frame;
        _syncing = false;
        int ms = 0;
        for (int i = 0; i < frame; i++) ms += _clip.DelaysMs[i];
        TimeText.Text = $"{ms / 1000.0:0.0} / {_clip.TotalMs / 1000.0:0.0} s";
    }

    private void Advance()
    {
        if (_sequence.Count == 0) return;
        _position = (_position + 1) % _sequence.Count;
        var (frame, delay) = _sequence[_position];
        ShowFrame(frame);
        _timer.Interval = TimeSpan.FromMilliseconds(delay);
    }

    private void Play(bool on)
    {
        _playing = on && _clip.Count > 1;
        PlayIcon.Data = (Geometry)FindResource(_playing ? "IcPause" : "IcPlay");
        if (_playing)
        {
            _timer.Interval = TimeSpan.FromMilliseconds(_sequence.Count > 0 ? _sequence[_position].DelayMs : 66);
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    private void Play_Click(object sender, RoutedEventArgs e) => Play(!_playing);

    private void Scrubber_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing || !IsInitialized) return;
        // scrubbing pauses, as before, and stays paused when let go
        BeginAdjust(resume: false);
        // only the frames that will be in the GIF
        var (first, last) = TrimRange;
        int frame = Math.Clamp((int)Scrubber.Value, first, last);
        int at = _sequence.FindIndex(s => s.Frame == frame);
        if (at >= 0) _position = at;
        ShowFrame(frame);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (CropKey(e.Key))
        {
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Space && Keyboard.FocusedElement is not TextBox)
        {
            e.Handled = true;
            Play(!_playing);
        }
        base.OnPreviewKeyDown(e);
    }

    // ---- export --------------------------------------------------------------------------

    /// <summary>Renders every frame with its caption and writes the GIF. Shows progress as it goes.</summary>
    private async Task<bool> ExportTo(string path)
    {
        if (_busy) return false;
        _busy = true;
        Actions.IsEnabled = false;
        ExportProgress.Visibility = Visibility.Visible;
        var sequence = _sequence.ToList();
        var caption = Caption;
        int outW = OutputWidth;
        int repeats = Repeats;
        string temp = path + ".part";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var file = File.Create(temp))
            {
                GifWriter? writer = null;
                for (int i = 0; i < sequence.Count; i++)
                {
                    var (frame, delay) = sequence[i];
                    byte[] px = CaptionRenderer.Render(Picture(frame), outW, caption, out int w, out int h);
                    writer ??= new GifWriter(file, w, h, repeats, seeThrough: caption.Style == CaptionStyle.Bubble);
                    var wr = writer;
                    await Task.Run(() => wr.AddFrame(px, delay));
                    ExportProgress.Value = (i + 1.0) / sequence.Count;
                    StatusText.Text = $"Making the GIF…  {i + 1} / {sequence.Count}";
                }
                writer?.Dispose();
            }
            File.Move(temp, path, overwrite: true);
            long bytes = new FileInfo(path).Length;
            StatusText.Text = $"{Path.GetFileName(path)}  ·  {(bytes >= 1 << 20 ? $"{bytes / 1048576.0:0.0} MB" : $"{bytes / 1024} KB")}";
            _kept = true;
            return true;
        }
        catch (Exception ex)
        {
            try { File.Delete(temp); } catch { }
            StatusText.Text = "";
            Toast.Show("Couldn't make the GIF: " + ex.Message, true);
            return false;
        }
        finally
        {
            ExportProgress.Visibility = Visibility.Collapsed;
            Actions.IsEnabled = true;
            _busy = false;
        }
    }

    private static string TempGif() =>
        Path.Combine(Path.GetTempPath(), "Overskin", $"Overskin {DateTime.Now:yyyy-MM-dd HH.mm.ss}.gif");

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        string path = _savedPath ?? ImageFiles.NewPath(_host.Folder, ".gif");
        if (!await ExportTo(path)) return;
        _savedPath = path;
        _host.Saved(path);
        Toast.Show("Saved " + Path.GetFileName(path));
    }

    private async void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save GIF",
            Filter = "GIF animation|*.gif",
            FileName = "Overskin clip",
            InitialDirectory = _host.Folder,
        };
        if (dialog.ShowDialog(this) != true) return;
        if (!await ExportTo(dialog.FileName)) return;
        _host.Saved(dialog.FileName);
        Toast.Show("Saved " + Path.GetFileName(dialog.FileName));
    }

    /// <summary>Copies the GIF as a file: Discord, browsers and Explorer all accept it with Ctrl+V.</summary>
    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        string path = TempGif();
        if (!await ExportTo(path)) return;
        try
        {
            var files = new System.Collections.Specialized.StringCollection { path };
            Clipboard.SetFileDropList(files);
            Toast.Show("GIF copied. Paste it into Discord with Ctrl+V.");
        }
        catch
        {
            Toast.Show("The clipboard is busy. Try again.", true);
        }
    }

    // ---- window --------------------------------------------------------------------------

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        Frame.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        // the same rounded shape as the main window, square when it fills the screen
        if (WindowState == WindowState.Maximized) Frame.CornerRadius = new CornerRadius(0);
        else Frame.SetResourceReference(System.Windows.Controls.Border.CornerRadiusProperty, "RadiusWindow");
        MaxIcon.Data = (Geometry)FindResource(WindowState == WindowState.Maximized ? "IcCopy" : "IcSquare");
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_busy) { e.Cancel = true; return; }
        if (!_kept && _clip.Count > 1 && !App.Quiet)
        {
            int answer = AskDialog.Ask(this, "Throw this clip away?", "It hasn't been saved or copied.", "IcTrash",
                ("Keep it", false), ("Throw away", true));
            if (answer != 1) { e.Cancel = true; return; }
        }
        _timer.Stop();
        _settle.Stop();
        base.OnClosing(e);
    }
}
