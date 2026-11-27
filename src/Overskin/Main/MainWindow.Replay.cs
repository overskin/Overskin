using System.Windows;
using System.Windows.Controls;

namespace Overskin;

/// <summary>Instant replay: always recording in the background, a hotkey saves the last bit.</summary>
public partial class MainWindow
{
    private ReplayBuffer? _replay;
    /// <summary>Instant replay was stopped for a recording and comes back when it ends.</summary>
    private bool _replayPaused;

    private void StartReplay()
    {
        if (_replay?.Running == true || App.Quiet) return;
        try { _replay = ReplayBuffer.Start(_settings.ReplaySeconds, _settings.ReplaySound); }
        catch (Exception ex) { Toast.Show("Instant replay couldn't start: " + ex.Message, true); }
        UpdateReplayStatus();
    }

    private void StopReplay()
    {
        var replay = _replay;
        _replay = null;
        if (replay != null) Task.Run(replay.Dispose);
        UpdateReplayStatus();
    }

    /// <summary>A recording is starting: instant replay steps aside (both can't read the screen the fast way at once).</summary>
    private void PauseReplay()
    {
        if (_replay?.Running != true) return;
        _replayPaused = true;
        StopReplay();
    }

    private void ResumeReplay()
    {
        if (!_replayPaused) return;
        _replayPaused = false;
        if (_settings.ReplayOn) StartReplay();
    }

    /// <summary>The hotkey: saves the last seconds as a video in the screenshots folder.</summary>
    private async void SaveReplay()
    {
        if (_replay?.Running != true)
        {
            Toast.Show(_settings.ReplayOn ? "Instant replay isn't running right now." : "Instant replay is off. Turn it on on the Recording page.");
            return;
        }
        FileNames.Context = NameInfo.At(0, 0);
        FileNames.Pattern = _settings.FileNamePattern;
        string path = NewCapturePath(".mp4");
        Toast.ShowSticky("Saving the replay…");
        try
        {
            await _replay.SaveAsync(path);
            Toast.HideNow();
            AddToHistory(path);
            Toast.Show($"Saved the last {ReplayLength(_settings.ReplaySeconds)}. It's in the Library.");
        }
        catch (Exception ex)
        {
            Toast.HideNow();
            Toast.Show("Couldn't save the replay: " + ex.Message, true);
        }
    }

    private static string ReplayLength(int seconds) =>
        seconds >= 60 ? $"{seconds / 60} minute{(seconds >= 120 ? "s" : "")}" : $"{seconds} seconds";

    // ---- the card ------------------------------------------------------------------------------

    private bool _syncingReplay;

    private void SyncReplayControls()
    {
        _syncingReplay = true;
        ReplaySwitch.IsChecked = _settings.ReplayOn;
        ReplaySoundSwitch.IsChecked = _settings.ReplaySound;
        foreach (var r in ReplayLengthChoices.Children.OfType<RadioButton>()) r.IsChecked = (string)r.Tag == _settings.ReplaySeconds.ToString();
        _syncingReplay = false;
        UpdateReplayStatus();
    }

    private void Replay_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || _syncingReplay || ReplaySwitch == null) return;
        bool wasOn = _settings.ReplayOn;
        int wasSeconds = _settings.ReplaySeconds;
        bool wasSound = _settings.ReplaySound;
        _settings.ReplayOn = ReplaySwitch.IsChecked == true;
        _settings.ReplaySound = ReplaySoundSwitch.IsChecked == true;
        var chosen = ReplayLengthChoices.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true);
        if (chosen != null && int.TryParse((string)chosen.Tag, out int s)) _settings.ReplaySeconds = s;
        SaveSettings();
        // on, off, or started again with the new length or sound
        if (!_settings.ReplayOn) StopReplay();
        else if (!wasOn || wasSeconds != _settings.ReplaySeconds || wasSound != _settings.ReplaySound)
        {
            StopReplay();
            StartReplay();
        }
        UpdateReplayStatus();
    }

    private void UpdateReplayStatus()
    {
        if (ReplayStatus == null) return;
        ReplayStatus.Text = _replay?.Running == true
            ? $"Recording in the background. {_settings.HotkeyReplay} saves the last {ReplayLength(_settings.ReplaySeconds)}."
            : _settings.ReplayOn && _replayPaused ? "Paused while another recording runs."
            : $"Off. When it's on, {_settings.HotkeyReplay} saves what just happened.";
    }

    private string ReplaySummary() => _settings.ReplayOn ? $"On · last {ReplayLength(_settings.ReplaySeconds)}" : "Off";
}
