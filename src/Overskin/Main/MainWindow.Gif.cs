using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace Overskin;

/// <summary>GIF recording and the GIF editor.</summary>
public partial class MainWindow
{
    private const int HkGif = 17, HkGifEsc = 18;

    private GifRecorder? _recorder;
    private bool _recordingWasShown;

    // ---- recording -----------------------------------------------------------------------

    private void GifFps_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not RadioButton { Tag: string tag } || !int.TryParse(tag, out int fps)) return;
        _settings.GifFps = fps;
        SaveSettings();
    }

    private void SyncGifControls()
    {
        int fps = GifRecorder.ValidFps(_settings.GifFps);
        foreach (var r in GifFpsChoices.Children.OfType<RadioButton>()) r.IsChecked = (string)r.Tag == fps.ToString();
    }

    private void RecordGif_Click(object sender, RoutedEventArgs e) => ToggleGifRecording();

    /// <summary>Starts a recording, or stops the one that's running (same button, same hotkey).</summary>
    private void ToggleGifRecording()
    {
        if (_recorder?.IsRecording == true)
        {
            _recorder.Stop();
            return;
        }

        Guarded(async () =>
        {
            var chosen = await Choose(OverlayMode.Region, "Drag over what to record, or click a window.  Esc cancels.", useDelay: false);
            if (chosen is not { } c) return;
            if (c.Overlay.Selection is not { } area)
            {
                Reappear(c.WasShown);
                return;
            }

            // the main window stays hidden while recording so it can't end up in the clip
            _recordingWasShown = c.WasShown;
            if (_recorder == null)
            {
                _recorder = new GifRecorder(Dispatcher);
                _recorder.Finished += OnRecordingFinished;
            }
            _hotkeys?.Register(HkGifEsc, new Hotkey(ModifierKeys.None, Key.Escape), () => _recorder.Stop());
            FileNames.Context = NameInfo.At(area.X + area.Width / 2, area.Y + area.Height / 2);
            FileNames.Pattern = _settings.FileNamePattern;
            _recorder.Start(area, _settings.AfterCursor, GifRecorder.ValidFps(_settings.GifFps));
        });
    }

    private void OnRecordingFinished(GifClip? clip)
    {
        _hotkeys?.Unregister(HkGifEsc);
        Reappear(_recordingWasShown);
        if (clip == null)
        {
            Toast.Show("Recording cancelled.");
            return;
        }
        OpenGifEditor(clip, $"Recording {DateTime.Now:HH.mm.ss}  ·  {clip.Count} frames");
    }

    // ---- GIF editor ----------------------------------------------------------------------

    private void OpenGifEditor(GifClip clip, string name)
    {
        var editor = new GifEditorWindow(clip, name, new GifEditorHost
        {
            Folder = _settings.SaveFolder,
            Saved = AddToHistory,
        });
        editor.Show();
        editor.Activate();
    }

    /// <summary>GIFs open frame by frame; still pictures become a one-frame clip that can be captioned.</summary>
    private static GifClip LoadClip(string path) =>
        Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase)
            ? GifReader.Read(path)
            : GifClip.FromImage(ImageLoader.LoadFile(path));

    private void CaptionGif_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Pick a GIF or picture to caption",
            Filter = "GIFs and pictures|*.gif;*.png;*.jpg;*.jpeg;*.bmp;*.webp|All files|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        OpenClipFile(dialog.FileName);
    }

    private void OpenClipFile(string path)
    {
        try
        {
            OpenGifEditor(LoadClip(path), Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            Toast.Show("Couldn't open it: " + (ex is InvalidOperationException ? ex.Message : "the file isn't a readable picture."), true);
        }
    }

}
