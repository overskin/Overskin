using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Overskin;

/// <summary>Screen recording to MP4, plain or with auto-zoom.</summary>
public partial class MainWindow
{
    private const int HkVideoEsc = 20, HkVideoPause = 21;

    private VideoRecorder? _video;
    private bool _videoWasShown;

    private void RecordVideo_Click(object sender, RoutedEventArgs e) => ToggleVideo(_settings.Video.AutoZoom);

    // ---- step recorder -----------------------------------------------------------------------

    private StepRecorder? _steps;
    private bool _stepsWasShown;

    private void RecordSteps_Click(object sender, RoutedEventArgs e) => ToggleSteps();

    private void ToggleSteps()
    {
        if (_steps?.IsRecording == true)
        {
            _steps.Stop();
            return;
        }
        if (_video?.IsRecording == true || _recorder?.IsRecording == true) return;
        if (_steps == null)
        {
            _steps = new StepRecorder(Dispatcher, _manager.IsOwnWindow);
            _steps.Finished += OnStepsFinished;
        }
        _stepsWasShown = IsVisible && WindowState != WindowState.Minimized;
        Hide();
        if (!_steps.Start())
        {
            Reappear(_stepsWasShown);
            Toast.Show("Couldn't start recording steps.", true);
        }
    }

    private async void OnStepsFinished(List<RecordedStep>? steps)
    {
        Reappear(_stepsWasShown);
        if (steps == null)
        {
            Toast.Show("Step recording cancelled.");
            return;
        }
        await Task.Delay(400); // the last names of what was clicked are still being looked up
        var accent = ((System.Windows.Media.SolidColorBrush)FindResource("Accent")).Color;
        // check and reword the steps, then make the guide as a picture, HTML, Markdown or PDF
        new StepsWindow(steps, accent, _settings.SaveFolder,
            (guide, title) => OpenEditor(guide, null, $"{title}  ·  {steps.Count} step{(steps.Count == 1 ? "" : "s")}"),
            _ => { }).Show();
    }

    // ---- Screenshots / Recording -------------------------------------------------------------

    private void CaptureMode_Checked(object sender, RoutedEventArgs e)
    {
        if (CaptureShots == null || CaptureRecord == null) return;
        bool record = CaptureRecordTab.IsChecked == true;
        CaptureShots.Visibility = record ? Visibility.Collapsed : Visibility.Visible;
        CaptureRecord.Visibility = record ? Visibility.Visible : Visibility.Collapsed;
        if (PageCapture is System.Windows.Controls.ScrollViewer sv) sv.ScrollToTop();
    }

    // ---- video settings ----------------------------------------------------------------------

    private bool _syncingVideo;

    private void SyncVideoControls()
    {
        _syncingVideo = true;
        var v = _settings.Video;
        VidFps30.IsChecked = v.Fps != 60;
        VidFps60.IsChecked = v.Fps == 60;
        VidQualityStandard.IsChecked = !v.HighQuality;
        VidQualityHigh.IsChecked = v.HighQuality;
        VidPointerSwitch.IsChecked = v.Pointer;
        VidStyleWhite.IsChecked = v.PointerStyle == "White";
        VidStyleBlack.IsChecked = v.PointerStyle == "Black";
        VidStyleSystem.IsChecked = v.PointerStyle == "System";
        VidPointerSize.Value = v.PointerSize;
        VidSmoothing.Value = v.PointerSmoothing;
        VidSmoothCursorSwitch.IsChecked = v.SmoothCursor;
        VidMotionBlur.Value = v.MotionBlur;
        VidKeysSwitch.IsChecked = v.ShowKeys;
        VidRingsSwitch.IsChecked = v.ClickRings;
        VideoZoomSwitch.IsChecked = v.AutoZoom;
        VidEditableZoomsSwitch.IsChecked = v.EditableZooms;
        VidZoom.Value = v.Zoom;
        VidZoomSpeed.Value = v.ZoomSpeed;
        VidFollow.Value = v.FollowSmoothing;
        VidHold.Value = v.ZoomHold;
        VidSystemSoundSwitch.IsChecked = v.SystemSound;
        VidMicSwitch.IsChecked = v.Microphone;
        VidWebcamSwitch.IsChecked = v.Webcam;
        foreach (var r in new[] { CamTopLeft, CamTopRight, CamBottomLeft, CamBottomRight }) r.IsChecked = (string)r.Tag == v.WebcamCorner;
        CamCircle.IsChecked = v.WebcamShape == "Circle";
        CamRounded.IsChecked = v.WebcamShape == "Rounded";
        VidCamSize.Value = v.WebcamSize;
        _syncingVideo = false;
        UpdateVideoLabels();
        _ = UpdateDeviceNamesAsync();
    }

    /// <summary>Shows which microphone and camera are chosen, by name.</summary>
    private async Task UpdateDeviceNamesAsync()
    {
        var v = _settings.Video;
        var mics = await Task.Run(AudioSource.Microphones);
        MicName.Text = mics.FirstOrDefault(m => m.Id == v.MicrophoneId)?.Name ?? (mics.Count == 0 ? "No microphone found" : "Windows default");
        var cams = await Webcam.CamerasAsync();
        CameraName.Text = cams.FirstOrDefault(c => c.Id == v.WebcamId)?.Name ?? (cams.Count == 0 ? "No camera found" : cams[0].Name);
    }

    /// <summary>A dropdown menu under <paramref name="button"/>: a list of names, the chosen one ticked.</summary>
    private void ShowChoices(Button button, IEnumerable<(string Id, string Name)> choices, string chosen, Action<string> pick)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = button,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            MinWidth = button.ActualWidth,
        };
        foreach (var (id, name) in choices)
        {
            var item = new MenuItem { Header = name, IsChecked = id == chosen };
            item.Click += (_, _) => pick(id);
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "Nothing found", IsEnabled = false });
        menu.IsOpen = true;
    }

    private async void MicButton_Click(object sender, RoutedEventArgs e)
    {
        var mics = await Task.Run(AudioSource.Microphones);
        var choices = new List<(string, string)> { ("", "Windows default") };
        choices.AddRange(mics.Select(m => (m.Id, m.Name)));
        ShowChoices(MicButton, choices, _settings.Video.MicrophoneId, id =>
        {
            _settings.Video.MicrophoneId = id;
            SaveSettings();
            _ = UpdateDeviceNamesAsync();
        });
    }

    /// <summary>The backdrops as a menu of colour swatches.</summary>
    private void BackdropButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = BackdropButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            MinWidth = BackdropButton.ActualWidth,
        };
        foreach (var (name, _, _) in Beautify.Backgrounds)
        {
            var item = new MenuItem
            {
                Header = name == "None" ? "None (just the screen)" : name,
                IsChecked = name == _settings.Video.Backdrop,
                Icon = BackdropChip(name),
            };
            string chosen = name;
            item.Click += (_, _) =>
            {
                _settings.Video.Backdrop = chosen;
                SaveSettings();
                UpdateVideoLabels();
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    /// <summary>A small rounded swatch of a backdrop (a crossed-out box for none).</summary>
    private Border BackdropChip(string name)
    {
        var chip = new Border { Width = 22, Height = 14, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1) };
        chip.SetResourceReference(Border.BorderBrushProperty, "LineStrong");
        chip.Background = name == "None" ? Brushes.Transparent : Beautify.BackgroundBrush(name);
        return chip;
    }

    private void VidZoomMore_Click(object sender, RoutedEventArgs e)
    {
        bool show = VidZoomMore.Visibility != Visibility.Visible;
        VidZoomMore.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        VidZoomMoreButton.Content = show ? "Less" : "More";
    }

    private async void CameraButton_Click(object sender, RoutedEventArgs e)
    {
        var cams = await Webcam.CamerasAsync();
        string chosen = cams.Any(c => c.Id == _settings.Video.WebcamId) ? _settings.Video.WebcamId : cams.FirstOrDefault()?.Id ?? "";
        ShowChoices(CameraButton, cams.Select(c => (c.Id, c.Name)), chosen, id =>
        {
            _settings.Video.WebcamId = id;
            SaveSettings();
            _ = UpdateDeviceNamesAsync();
        });
    }

    private void UpdateVideoLabels()
    {
        var v = _settings.Video;
        VidPointerSizeText.Text = $"{v.PointerSize:0.##}×";
        VidSmoothingText.Text = v.PointerSmoothing == 0 ? "Off" : $"{v.PointerSmoothing}";
        VidMotionBlurText.Text = v.MotionBlur == 0 ? "Off" : $"{v.MotionBlur}";
        BackdropName.Text = v.Backdrop == "None" ? "None" : v.Backdrop;
        BackdropSwatch.Background = v.Backdrop == "None" ? Brushes.Transparent : Beautify.BackgroundBrush(v.Backdrop);
        VidZoomText.Text = $"{v.Zoom:0.0}×";
        VidZoomSpeedText.Text = $"{v.ZoomSpeed}";
        VidFollowText.Text = $"{v.FollowSmoothing}";
        VidHoldText.Text = $"{v.ZoomHold:0.#} s";
        foreach (var c in new FrameworkElement[] { VidZoom, VidZoomSpeed, VidFollow, VidHold }) c.IsEnabled = v.AutoZoom;
        foreach (var c in new FrameworkElement[] { VidStyleWhite, VidStyleBlack, VidStyleSystem, VidPointerSize, VidSmoothing, VidSmoothCursorSwitch }) c.IsEnabled = v.Pointer;
        VidMotionBlur.IsEnabled = v.Pointer && v.SmoothCursor;
        MicButton.IsEnabled = v.Microphone;
        VidCamSizeText.Text = $"{v.WebcamSize}%";
        foreach (var c in new FrameworkElement[] { CameraButton, CamTopLeft, CamTopRight, CamBottomLeft, CamBottomRight, CamCircle, CamRounded, VidCamSize }) c.IsEnabled = v.Webcam;
    }

    private void VideoSetting_Click(object sender, RoutedEventArgs e) => ReadVideoControls();

    private void VideoSetting_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => ReadVideoControls();

    private void ReadVideoControls()
    {
        if (_syncingVideo || VidHoldText == null || VidHold == null) return;
        var v = _settings.Video;
        v.Fps = VidFps60.IsChecked == true ? 60 : 30;
        v.HighQuality = VidQualityHigh.IsChecked == true;
        v.Pointer = VidPointerSwitch.IsChecked == true;
        v.PointerStyle = VidStyleBlack.IsChecked == true ? "Black" : VidStyleSystem.IsChecked == true ? "System" : "White";
        v.PointerSize = VidPointerSize.Value;
        v.PointerSmoothing = (int)VidSmoothing.Value;
        v.SmoothCursor = VidSmoothCursorSwitch.IsChecked == true;
        v.MotionBlur = (int)VidMotionBlur.Value;
        v.ShowKeys = VidKeysSwitch.IsChecked == true;
        v.ClickRings = VidRingsSwitch.IsChecked == true;
        v.AutoZoom = VideoZoomSwitch.IsChecked == true;
        v.EditableZooms = VidEditableZoomsSwitch.IsChecked == true;
        v.Zoom = Math.Round(VidZoom.Value, 1);
        v.ZoomSpeed = (int)VidZoomSpeed.Value;
        v.FollowSmoothing = (int)VidFollow.Value;
        v.ZoomHold = VidHold.Value;
        v.SystemSound = VidSystemSoundSwitch.IsChecked == true;
        v.Microphone = VidMicSwitch.IsChecked == true;
        v.Webcam = VidWebcamSwitch.IsChecked == true;
        v.WebcamCorner = new[] { CamTopLeft, CamTopRight, CamBottomLeft, CamBottomRight }.FirstOrDefault(r => r.IsChecked == true)?.Tag as string ?? "BottomRight";
        v.WebcamShape = CamRounded.IsChecked == true ? "Rounded" : "Circle";
        v.WebcamSize = (int)VidCamSize.Value;
        UpdateVideoLabels();
        if (_ready) SaveSettings();
    }

    /// <summary>Starts a recording, or stops the one that's running.</summary>
    private void ToggleVideo(bool autoZoom)
    {
        if (_video?.IsRecording == true)
        {
            _video.Stop();
            return;
        }
        if (_recorder?.IsRecording == true) return;

        Guarded(async () =>
        {
            var chosen = await Choose(OverlayMode.Region,
                "Drag over what to record, or click a window or the desktop for the whole screen.  Esc cancels.", useDelay: false);
            if (chosen is not { } c) return;
            if (c.Overlay.Selection is not { } area || area.Width < 16 || area.Height < 16)
            {
                Reappear(c.WasShown);
                return;
            }

            _videoWasShown = c.WasShown;
            if (_video == null)
            {
                _video = new VideoRecorder(Dispatcher);
                _video.Finished += OnVideoFinished;
            }
            _hotkeys?.Register(HkVideoEsc, new Hotkey(ModifierKeys.None, Key.Escape), () => _video.Stop());
            // the pause key pauses the recording; the start-drawing key draws on the screen
            if (Hotkey.TryParse(_settings.HotkeyPause, out var pause)) _hotkeys?.Register(HkVideoPause, pause, () => _video.TogglePause());
            RecordBarWindow.Hotkeys.Pause = _settings.HotkeyPause;
            RecordBarWindow.Hotkeys.Pen = _settings.HotkeyDraw;
            FileNames.Context = NameInfo.At(area.X + area.Width / 2, area.Y + area.Height / 2);
            string path = NewCapturePath(".mp4");
            var mark = _settings.Watermark.OnVideos && _settings.Watermark.HasContent ? _settings.Watermark.Copy() : null;
            _video.Start(area, path, _settings.Video.With(autoZoom), mark);
        });
    }

    private void OnVideoFinished(string? path, string? error)
    {
        _hotkeys?.Unregister(HkVideoEsc);
        _hotkeys?.Unregister(HkVideoPause);
        Reappear(_videoWasShown);
        if (error != null)
        {
            Toast.Show("The recording failed: " + error, true);
            return;
        }
        if (path == null)
        {
            Toast.Show("Recording cancelled.");
            return;
        }
        AddToHistory(path);
        if (_video?.Notice is { } notice) Toast.Show("Video saved, but: " + notice, true);
        else Toast.Show("Video saved. Click it in the Library to play or trim it.");
    }

    // ---- videos in the library ---------------------------------------------------------------

    private static bool IsVideo(string path) =>
        Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase);

    /// <summary>Plays a video in Overskin, where it can be trimmed.</summary>
    private void OpenVideo(string path)
    {
        if (!File.Exists(path))
        {
            Toast.Show("That file has been moved or deleted.");
            return;
        }
        VideoEditorWindow.OpenPicture ??= (picture, name) => OpenEditor(picture, null, name);
        new VideoEditorWindow(path, AddToHistory).Show();
    }

    private static void OpenWithDefaultApp(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { Toast.Show("Couldn't open the video.", true); }
    }

    /// <summary>Windows' own thumbnail for a video, and its length.</summary>
    private static async Task LoadVideoThumbAsync(LibraryItem item)
    {
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(item.Path);
            using var thumb = await file.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.VideosView, 300);
            var props = await file.Properties.GetVideoPropertiesAsync();
            var bitmap = new BitmapImage();
            using (var stream = thumb.AsStreamForRead())
            {
                var copy = new MemoryStream();
                await stream.CopyToAsync(copy);
                copy.Position = 0;
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = copy;
                bitmap.EndInit();
                bitmap.Freeze();
            }
            long bytes = new FileInfo(item.Path).Length;
            var d = props.Duration;
            item.Thumb = bitmap;
            item.Details = $"Video  ·  {(int)d.TotalMinutes}:{d.Seconds:00}  ·  {(bytes >= 1 << 20 ? $"{bytes / (1024.0 * 1024):0.#} MB" : $"{Math.Max(1, bytes / 1024)} KB")}";
            item.Raise(nameof(LibraryItem.Details));
        }
        catch
        {
            item.Details = "Video";
            item.Raise(nameof(LibraryItem.Details));
        }
    }
}
