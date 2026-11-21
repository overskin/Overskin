using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Overskin;

/// <summary>
/// Playing videos in the viewer like a video site: a bar along the bottom with play and pause, the
/// time, a scrubber and the volume, which tucks away while it plays and the mouse is still; a
/// play or pause sign that flashes in the middle on every click; and zooming like a picture.
/// </summary>
public sealed partial class ViewerWindow
{
    private static double _volume = 0.8;
    private static bool _muted;

    private Size? _videoSize;
    private Border _controls = null!;
    private Button _playButton = null!, _muteButton = null!;
    private Slider _scrub = null!, _volumeSlider = null!;
    private TextBlock _clock = null!;
    private Border _flash = null!;
    private Overskin.Icon _flashIcon = null!;
    private readonly DispatcherTimer _videoTick = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _idle = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private bool _scrubbing, _resumeAfterScrub;

    /// <summary>The size of what's shown, picture or video, for fitting and zooming.</summary>
    private Size? ContentSize => _shown != null ? new Size(_shown.PixelWidth, _shown.PixelHeight) : _video.Visibility == Visibility.Visible ? _videoSize : null;

    private void BuildVideoControls()
    {
        Brush white = Brushes.White;
        _playButton = OverlayButton("IcPause", "Pause  (Space)", TogglePlay);
        _muteButton = OverlayButton("IcVolume", "Mute  (M)", ToggleMute);
        _clock = new TextBlock { Foreground = white, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 12, 0), MinWidth = 86 };
        _scrub = new Slider { Minimum = 0, Maximum = 1, VerticalAlignment = VerticalAlignment.Center, IsMoveToPointEnabled = true, ToolTip = "Drag to move through the video  (J and L skip 5 seconds)" };
        _scrub.SetResourceReference(StyleProperty, typeof(Slider));
        _scrub.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => BeginScrub()));
        _scrub.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => EndScrub()));
        _scrub.PreviewMouseLeftButtonDown += (_, _) => BeginScrub();
        _scrub.PreviewMouseLeftButtonUp += (_, _) => EndScrub();
        _scrub.ValueChanged += (_, e) =>
        {
            if (_scrubbing) { _video.Position = TimeSpan.FromSeconds(e.NewValue); ShowClock(); }
        };
        _volumeSlider = new Slider { Minimum = 0, Maximum = 1, Width = 90, Value = _volume, VerticalAlignment = VerticalAlignment.Center, IsMoveToPointEnabled = true, ToolTip = "Volume  (Up and Down)" };
        _volumeSlider.ValueChanged += (_, e) =>
        {
            _volume = e.NewValue;
            if (_volume > 0 && _muted) _muted = false;
            ApplyVolume();
        };

        var row = new Grid { Margin = new Thickness(10, 22, 14, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(_playButton);
        Grid.SetColumn(_clock, 1);
        row.Children.Add(_clock);
        Grid.SetColumn(_scrub, 2);
        row.Children.Add(_scrub);
        Grid.SetColumn(_muteButton, 3);
        _muteButton.Margin = new Thickness(12, 0, 2, 0);
        row.Children.Add(_muteButton);
        Grid.SetColumn(_volumeSlider, 4);
        row.Children.Add(_volumeSlider);

        // a soft dark fade behind the bar, so it reads over any video
        _controls = new Border
        {
            Child = row, VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Collapsed,
            Background = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Color.FromArgb(0xB0, 0, 0, 0), 90),
        };
        _stage.Children.Add(_controls);

        _flashIcon = new Overskin.Icon { Data = (Geometry)FindResource("IcPlay"), Width = 30, Height = 30, Foreground = white, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _flash = new Border
        {
            Width = 72, Height = 72, CornerRadius = new CornerRadius(36), Background = new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0)),
            Child = _flashIcon, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0, IsHitTestVisible = false, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1),
        };
        _stage.Children.Add(_flash);

        _video.ScrubbingEnabled = true;
        _video.MediaOpened += (_, _) =>
        {
            if (_video.NaturalVideoWidth > 0) _videoSize = new Size(_video.NaturalVideoWidth, _video.NaturalVideoHeight);
            _content.Width = _videoSize?.Width ?? 0;
            _content.Height = _videoSize?.Height ?? 0;
            _scrub.Maximum = _video.NaturalDuration.HasTimeSpan ? Math.Max(0.1, _video.NaturalDuration.TimeSpan.TotalSeconds) : 1;
            if (_videoSize is { } s) _details.Text = $"{s.Width:0} × {s.Height:0}  ·  " + _details.Text;
            FitNow(animate: false);
            ShowClock();
        };
        _videoTick.Tick += (_, _) =>
        {
            if (_video.Visibility != Visibility.Visible || _scrubbing) return;
            _scrub.Value = Math.Min(_scrub.Maximum, _video.Position.TotalSeconds);
            ShowClock();
        };
        _idle.Tick += (_, _) =>
        {
            _idle.Stop();
            // tucked away while it plays and the mouse rests, like a video site
            if (_playing && !_controls.IsMouseOver && !_scrubbing) FadeControls(false);
        };
        _stage.MouseMove += (_, _) => WakeControls();
        _stage.MouseLeave += (_, _) => { if (_playing) FadeControls(false); };
        ApplyVolume();
    }

    private Button OverlayButton(string icon, string tip, Action act)
    {
        var b = new Button { Width = 34, Height = 32, ToolTip = tip, Content = new Overskin.Icon { Data = (Geometry)FindResource(icon), Width = 18, Height = 18, Foreground = Brushes.White } };
        b.SetResourceReference(StyleProperty, "BtnIcon");
        b.Click += (_, _) => act();
        return b;
    }

    /// <summary>A video is about to be shown: the bar comes up and the zoom waits for its size.</summary>
    private void StartVideo()
    {
        _videoSize = null;
        _scrub.Value = 0;
        _controls.Visibility = Visibility.Visible;
        _controls.Opacity = 1;
        _videoTick.Start();
        SetPlayIcon();
        WakeControls();
    }

    private void StopVideoControls()
    {
        _videoTick.Stop();
        _idle.Stop();
        _controls.Visibility = Visibility.Collapsed;
        _flash.Opacity = 0;
    }

    private void TogglePlay()
    {
        if (_video.Visibility != Visibility.Visible) return;
        if (_playing) _video.Pause(); else _video.Play();
        _playing = !_playing;
        SetPlayIcon();
        Flash(_playing ? "IcPlay" : "IcPause");
        WakeControls();
    }

    private void SetPlayIcon()
    {
        ((Overskin.Icon)_playButton.Content).Data = (Geometry)FindResource(_playing ? "IcPause" : "IcPlay");
        _playButton.ToolTip = (_playing ? "Pause" : "Play") + "  (Space)";
    }

    /// <summary>The sign in the middle: pops up a little larger and fades away.</summary>
    private void Flash(string icon)
    {
        _flashIcon.Data = (Geometry)FindResource(icon);
        var time = TimeSpan.FromMilliseconds(650);
        _flash.BeginAnimation(OpacityProperty, new DoubleAnimationUsingKeyFrames
        {
            KeyFrames = { new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)), new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180))), new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(time), new QuadraticEase()) },
        });
        var grow = new DoubleAnimation(0.85, 1.25, time) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var scale = (ScaleTransform)_flash.RenderTransform;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    private void Skip(double seconds)
    {
        if (_video.Visibility != Visibility.Visible) return;
        double to = Math.Clamp(_video.Position.TotalSeconds + seconds, 0, _scrub.Maximum);
        _video.Position = TimeSpan.FromSeconds(to);
        _scrub.Value = to;
        ShowClock();
        WakeControls();
    }

    private void ChangeVolume(double by)
    {
        _volumeSlider.Value = Math.Clamp(_volumeSlider.Value + by, 0, 1);
        WakeControls();
    }

    private void ToggleMute()
    {
        _muted = !_muted;
        ApplyVolume();
        WakeControls();
    }

    private void ApplyVolume()
    {
        _video.Volume = _muted ? 0 : _volume;
        _video.IsMuted = _muted;
        string icon = _muted || _volume <= 0.001 ? "IcVolumeX" : "IcVolume";
        if (_muteButton != null)
        {
            ((Overskin.Icon)_muteButton.Content).Data = (Geometry)FindResource(icon);
            _muteButton.ToolTip = (_muted ? "Unmute" : "Mute") + "  (M)";
        }
    }

    private void BeginScrub()
    {
        if (_scrubbing) return;
        _scrubbing = true;
        _resumeAfterScrub = _playing;
        if (_playing) _video.Pause();
    }

    private void EndScrub()
    {
        if (!_scrubbing) return;
        _scrubbing = false;
        _video.Position = TimeSpan.FromSeconds(_scrub.Value);
        if (_resumeAfterScrub) _video.Play();
        ShowClock();
    }

    private void ShowClock()
    {
        static string T(double s) => s >= 3600 ? TimeSpan.FromSeconds(s).ToString(@"h\:mm\:ss") : TimeSpan.FromSeconds(s).ToString(@"m\:ss");
        double now = _scrubbing ? _scrub.Value : _video.Position.TotalSeconds;
        _clock.Text = $"{T(now)} / {T(_scrub.Maximum)}";
    }

    /// <summary>The bar shows again whenever the mouse moves, and tucks away after a rest.</summary>
    private void WakeControls()
    {
        if (_video.Visibility != Visibility.Visible) return;
        FadeControls(true);
        _idle.Stop();
        _idle.Start();
    }

    private void FadeControls(bool show)
    {
        _controls.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(show ? 120 : 400)));
        _stage.Cursor = show || !_playing ? null : Cursors.None;
    }

#if DEBUG
    internal bool PlayingForTest => _playing;
    internal void TogglePlayForTest() => TogglePlay();
    internal string ZoomTextForTest => _zoomText.Text;
    internal double FlashOpacityForTest => _flash.Opacity;
    internal bool ControlsShownForTest => _controls.Visibility == Visibility.Visible;
#endif
}
