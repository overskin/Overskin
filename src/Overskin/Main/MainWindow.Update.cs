using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace Overskin;

/// <summary>The banner under the tabs when a newer Overskin is on GitHub, and installing it.</summary>
public partial class MainWindow
{
    private Release? _update;
    private Version? _dismissedUpdate;
    private bool _updating;
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(12) };

    private void SetUpUpdates()
    {
        UpdateCheckSwitch.IsChecked = _settings.CheckForUpdates;
        UpdateStatusText.Text = $"You have Overskin {Updater.Current.ToString(3)}.";
        if (App.Quiet) return; // developer runs never reach out
        _updateTimer.Tick += async (_, _) => await CheckForUpdateAsync(quietly: true);
        _updateTimer.Start();
        // a few seconds after starting, once everything else has settled
        var first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        first.Tick += async (_, _) => { first.Stop(); await CheckForUpdateAsync(quietly: true); };
        first.Start();
    }

    /// <summary>Asks GitHub for the latest release; <paramref name="quietly"/> skips it when turned off and says nothing when up to date.</summary>
    private async Task CheckForUpdateAsync(bool quietly)
    {
        if (quietly && !_settings.CheckForUpdates) return;
        if (_updating) return;
        if (!quietly) UpdateStatusText.Text = "Checking…";
        var release = await Updater.CheckAsync();
        if (release == null)
        {
            if (!quietly) UpdateStatusText.Text = $"You have Overskin {Updater.Current.ToString(3)}, the latest.";
            return;
        }
        UpdateStatusText.Text = $"Overskin {release.Version.ToString(3)} is out. You have {Updater.Current.ToString(3)}.";
        if (quietly && _dismissedUpdate != null && release.Version <= _dismissedUpdate) return;
        ShowUpdate(release);
    }

    private void ShowUpdate(Release release)
    {
        _update = release;
        UpdateTitle.Text = $"Overskin {release.Version.ToString(3)} is out";
        UpdateDetail.Text = $"You have {Updater.Current.ToString(3)}. Your settings and captures stay.";
        UpdateBanner.ToolTip = Updater.IsInstalledCopy
            ? "Download and install fetches it from GitHub, installs it in a few seconds and opens Overskin again. Your settings, pins and captures stay."
            : "This copy wasn't installed with the installer, so Download and install opens the installer for you.";
        UpdateProgress.Visibility = Visibility.Collapsed;
        UpdateInstallButton.IsEnabled = UpdateNotesButton.IsEnabled = UpdateCloseButton.IsEnabled = true;
        UpdateInstallText.Text = "Download and install";
        if (UpdateBanner.Visibility != Visibility.Visible)
        {
            UpdateBanner.Visibility = Visibility.Visible;
            UpdateBanner.Opacity = 0;
            UpdateBanner.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
        }
    }

    private void UpdateNotes_Click(object sender, RoutedEventArgs e)
    {
        if (_update == null) return;
        try { Process.Start(new ProcessStartInfo(_update.Page) { UseShellExecute = true }); }
        catch { Toast.Show("Couldn't open the browser.", true); }
    }

    private void UpdateDismiss_Click(object sender, RoutedEventArgs e)
    {
        _dismissedUpdate = _update?.Version;
        UpdateBanner.Visibility = Visibility.Collapsed;
    }

    private async void UpdateInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_update is not { } release || _updating) return;
        _updating = true;
        UpdateInstallButton.IsEnabled = UpdateNotesButton.IsEnabled = UpdateCloseButton.IsEnabled = false;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateProgress.Value = 0;
        UpdateInstallText.Text = "Downloading…";
        UpdateDetail.Text = $"Downloading Overskin {release.Version.ToString(3)} from GitHub…";
        try
        {
            string installer = await Updater.DownloadAsync(release, new Progress<double>(p =>
            {
                UpdateProgress.Value = p;
                UpdateDetail.Text = $"Downloading Overskin {release.Version.ToString(3)} from GitHub…  {p:0%}";
            }));
            UpdateProgress.IsIndeterminate = true;
            UpdateInstallText.Text = "Installing…";
            UpdateDetail.Text = "Installing. Overskin closes and opens again in a moment.";
            await Task.Delay(700);
            if (Updater.StartInstaller(installer))
            {
                CrashLog.Note($"update: installing {release.Version} and closing");
                ExitApp();
                return;
            }
            UpdateDetail.Text = "The installer is open: follow it to finish.";
            UpdateProgress.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            UpdateDetail.Text = "Couldn't update: " + ex.Message;
            UpdateProgress.Visibility = Visibility.Collapsed;
            UpdateInstallText.Text = "Try again";
            UpdateInstallButton.IsEnabled = UpdateNotesButton.IsEnabled = UpdateCloseButton.IsEnabled = true;
        }
        finally
        {
            UpdateProgress.IsIndeterminate = false;
            _updating = false;
        }
    }

    private void UpdateCheck_Click(object sender, RoutedEventArgs e)
    {
        _settings.CheckForUpdates = UpdateCheckSwitch.IsChecked == true;
        _settings.Save();
    }

    private async void CheckNow_Click(object sender, RoutedEventArgs e) => await CheckForUpdateAsync(quietly: false);

#if DEBUG
    /// <summary>For the self-test: the banner as it would show for a release.</summary>
    internal (bool Shown, string Title, string Detail) ShowUpdateForTest(Release release)
    {
        ShowUpdate(release);
        return (UpdateBanner.Visibility == Visibility.Visible, UpdateTitle.Text, UpdateDetail.Text);
    }
#endif
}
