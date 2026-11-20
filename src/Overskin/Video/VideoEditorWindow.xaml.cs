using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Overskin;

/// <summary>Plays a video and trims it: drag Start and End, then save the part in between as a new file.</summary>
public partial class VideoEditorWindow : Window
{
    private readonly string _path;
    private readonly Action<string> _saved;
    private readonly DispatcherTimer _tick = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly DispatcherTimer _settle = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _playing, _resumeAfterAdjust, _syncing, _busy, _opened;
    private double _length;

    public VideoEditorWindow(string path, Action<string> saved)
    {
        InitializeComponent();
        _path = path;
        _saved = saved;
        TitleText.Text = Path.GetFileName(path);
        _tick.Tick += (_, _) => Follow();
        _settle.Tick += Settle_Tick;
        Player.Source = new Uri(Path.GetFullPath(path));
        // show the first frame without playing
        Player.Play();
        Player.Pause();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        int round = 2;
        NativeMethods.DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 33, ref round, sizeof(int));
    }

    // ---- loading -------------------------------------------------------------------------

    private void Player_Opened(object sender, RoutedEventArgs e)
    {
        _opened = true;
        _length = Player.NaturalDuration.HasTimeSpan ? Player.NaturalDuration.TimeSpan.TotalSeconds : 0;
        if (_length <= 0) _ = LoadLengthAsync();
        else SetLength(_length);
        long bytes = new FileInfo(_path).Length;
        InfoText.Text = $"{Player.NaturalVideoWidth} × {Player.NaturalVideoHeight}  ·  {Clock(_length)}  ·  " +
                        (bytes >= 1 << 20 ? $"{bytes / 1048576.0:0.0} MB" : $"{Math.Max(1, bytes / 1024)} KB");
    }

    private async Task LoadLengthAsync()
    {
        try { SetLength((await VideoTrim.DurationAsync(_path)).TotalSeconds); } catch { }
    }

    private void SetLength(double seconds)
    {
        _length = seconds;
        _syncing = true;
        Scrubber.Maximum = TrimStart.Maximum = TrimEnd.Maximum = Math.Max(0.1, seconds);
        TrimStart.Value = 0;
        TrimEnd.Value = seconds;
        _syncing = false;
        UpdateTexts();
    }

    private void Player_Failed(object? sender, ExceptionRoutedEventArgs e)
    {
        PlayerMessage.Text = "This video can't be shown here. It can still be trimmed, or opened in your video player.";
        PlayerMessage.Visibility = Visibility.Visible;
        _ = LoadLengthAsync();
    }

    // ---- playback ------------------------------------------------------------------------

    private (double Start, double End) Range => (Math.Min(TrimStart.Value, TrimEnd.Value), Math.Max(TrimStart.Value, TrimEnd.Value));

    internal (double Start, double End) TrimRange => Range;

    private void Play(bool on)
    {
        if (!_opened) on = false;
        _playing = on;
        PlayIcon.Data = (Geometry)FindResource(on ? "IcPause" : "IcPlay");
        if (on)
        {
            var (start, end) = Range;
            double now = Player.Position.TotalSeconds;
            if (now < start - 0.05 || now >= end - 0.05) Seek(start);
            Player.Play();
            _tick.Start();
        }
        else
        {
            Player.Pause();
            _tick.Stop();
        }
    }

    private void Seek(double seconds)
    {
        Player.Position = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, Math.Max(0, _length)));
        ShowTime(seconds);
    }

    /// <summary>While playing: move the scrubber along, and loop back at the end of the trimmed part.</summary>
    private void Follow()
    {
        var (start, end) = Range;
        double now = Player.Position.TotalSeconds;
        if (now >= end - 0.02)
        {
            Seek(start);
            now = start;
        }
        // cut parts are skipped while playing
        if (CutAt(now) is { } cut)
        {
            Seek(cut.End);
            now = cut.End;
        }
        ShowTime(now);
    }

    private void ShowTime(double seconds)
    {
        _syncing = true;
        Scrubber.Value = Math.Clamp(seconds, 0, Scrubber.Maximum);
        _syncing = false;
        TimeText.Text = $"{Clock(seconds)} / {Clock(_length)}";
        DrawOverlays();
    }

    private void Play_Click(object sender, RoutedEventArgs e) => Play(!_playing);

    private void Scrubber_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing || !IsInitialized) return;
        BeginAdjust(resume: false);
        var (start, end) = Range;
        Seek(Math.Clamp(Scrubber.Value, start, end));
    }

    private void Trim_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing || !IsInitialized) return;
        // pause while a handle moves, so playback and the drag don't both move the picture
        BeginAdjust();
        _syncing = true;
        if (sender == TrimStart && TrimStart.Value > TrimEnd.Value) TrimStart.Value = TrimEnd.Value;
        if (sender == TrimEnd && TrimEnd.Value < TrimStart.Value) TrimEnd.Value = TrimStart.Value;
        _syncing = false;
        UpdateTexts();
        Seek(((Slider)sender).Value);
    }

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

    private void Settle_Tick(object? sender, EventArgs e)
    {
        if (Mouse.LeftButton == MouseButtonState.Pressed) return;
        _settle.Stop();
        if (_resumeAfterAdjust)
        {
            _resumeAfterAdjust = false;
            Seek(Range.Start);
            Play(true);
        }
    }

    private void StartHere_Click(object sender, RoutedEventArgs e) => TrimStart.Value = Math.Min(Player.Position.TotalSeconds, TrimEnd.Value);
    private void EndHere_Click(object sender, RoutedEventArgs e) => TrimEnd.Value = Math.Max(Player.Position.TotalSeconds, TrimStart.Value);

    private void UpdateTexts()
    {
        var (start, end) = Range;
        StartText.Text = Clock(start, tenths: true);
        EndText.Text = Clock(end, tenths: true);
        TrimText.Text = $"{Clock(end - start, tenths: true)} kept";
        UpdateCuts();
    }

    private static string Clock(double seconds, bool tenths = false)
    {
        seconds = Math.Max(0, seconds);
        var t = TimeSpan.FromSeconds(seconds);
        return tenths ? $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds / 100}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // typing a label isn't a shortcut
        if (Keyboard.FocusedElement is TextBox) { base.OnPreviewKeyDown(e); return; }
        switch (e.Key)
        {
            case Key.Space: Play(!_playing); e.Handled = true; break;
            case Key.I: StartHere_Click(this, e); e.Handled = true; break;
            case Key.O: EndHere_Click(this, e); e.Handled = true; break;
            case Key.X: Cut_Click(this, e); e.Handled = true; break;
            case Key.Left: BeginAdjust(false); Seek(Player.Position.TotalSeconds - 1); e.Handled = true; break;
            case Key.Right: BeginAdjust(false); Seek(Player.Position.TotalSeconds + 1); e.Handled = true; break;
        }
        base.OnPreviewKeyDown(e);
    }

    // ---- saving --------------------------------------------------------------------------

    private async Task<bool> SaveTo(string output)
    {
        if (_busy) return false;
        var (start, end) = Range;
        if (start <= 0.05 && end >= _length - 0.05 && _cuts.Count == 0 && _overlays.Count == 0)
        {
            Toast.Show("Trim, cut or add something first: this is the whole video as it is.");
            return false;
        }
        _busy = true;
        Play(false);
        Actions.IsEnabled = false;
        SaveProgress.Visibility = Visibility.Visible;
        StatusText.Text = "Saving…";
        try
        {
            // writing over the file that's open in the player would fail, so render beside it first
            string temp = output + ".part.mp4";
            var progress = new Progress<double>(p => SaveProgress.Value = p);
            if (_cuts.Count == 0 && _overlays.Count == 0)
                await VideoTrim.TrimAsync(_path, temp, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), progress);
            else
                await VideoEdit.RenderAsync(_path, temp, VideoEdit.Keep(start, end, _cuts), _overlays.Select(o => o.Clone()).ToList(),
                    Math.Max(2, Player.NaturalVideoHeight), progress);
            File.Move(temp, output, overwrite: true);
            StatusText.Text = Path.GetFileName(output);
            _saved(output);
            Toast.Show("Saved " + Path.GetFileName(output));
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = "";
            Toast.Show("Couldn't save the video: " + ex.Message, true);
            return false;
        }
        finally
        {
            SaveProgress.Visibility = Visibility.Collapsed;
            Actions.IsEnabled = true;
            _busy = false;
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveTo(VideoTrim.TrimmedName(_path));

    private async void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save video",
            Filter = "MP4 video|*.mp4",
            FileName = Path.GetFileName(VideoTrim.TrimmedName(_path)),
            InitialDirectory = Path.GetDirectoryName(_path),
        };
        if (dialog.ShowDialog(this) != true) return;
        if (Path.GetFullPath(dialog.FileName).Equals(Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase))
        {
            Toast.Show("Pick a different name: the original is still open here.", true);
            return;
        }
        await SaveTo(dialog.FileName);
    }

    private void OpenOutside_Click(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_path) { UseShellExecute = true }); }
        catch { Toast.Show("Couldn't open the video.", true); }
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
        if (WindowState == WindowState.Maximized) Frame.CornerRadius = new CornerRadius(0);
        else Frame.SetResourceReference(Border.CornerRadiusProperty, "RadiusWindow");
        MaxIcon.Data = (Geometry)FindResource(WindowState == WindowState.Maximized ? "IcCopy" : "IcSquare");
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_busy) { e.Cancel = true; return; }
        _tick.Stop();
        _settle.Stop();
        Player.Close();
        base.OnClosing(e);
    }
}
