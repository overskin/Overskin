using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace OverskinSetup;

public partial class SetupWindow : Window
{
    private enum Step { Folder, Options, Remove, Progress, Done }

    private readonly App.Args _args;
    private readonly bool _uninstall;
    private readonly long _payloadSize;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<Border> _marks = new();
    private readonly Step[] _flow;

    private Step _step;
    private FrameworkElement? _page;
    private bool _busy, _failed, _removed, _closing;
    private string _folder = "";

    public SetupWindow(App.Args args)
    {
        InitializeComponent();
        _args = args;
        _uninstall = args.Uninstall;
        _flow = _uninstall
            ? new[] { Step.Remove, Step.Progress, Step.Done }
            : new[] { Step.Folder, Step.Options, Step.Progress, Step.Done };

        foreach (var _ in _flow)
        {
            var mark = new Border { Height = 4, Width = 6, CornerRadius = new CornerRadius(2), Margin = new Thickness(3, 0, 0, 0) };
            mark.SetResourceReference(Border.BackgroundProperty, "LineStrong");
            _marks.Add(mark);
            Steps.Children.Add(mark);
        }

        if (_uninstall)
        {
            Title = "Remove Overskin";
            _folder = Setup.Normalize(args.Dir ?? Setup.DefaultFolder);
            RemoveFolderText.Text = _folder;
            RemoveFolderText.ToolTip = _folder;
        }
        else
        {
            _payloadSize = Payload.Size(args.Payload);
            string? existing = Setup.InstalledFolder;
            FolderBox.Text = args.Dir ?? existing ?? Setup.DefaultFolder;

            // a reinstall keeps what's there now; a first install starts from sensible defaults
            if (existing != null)
            {
                string exe = Path.Combine(existing, Setup.ExeName);
                OptStartMenu.IsChecked = Setup.SamePath(Native.ShortcutTarget(Setup.StartMenuLink) ?? "", exe);
                OptDesktop.IsChecked = Setup.SamePath(Native.ShortcutTarget(Setup.DesktopLink) ?? "", exe);
            }
            else
            {
                OptStartMenu.IsChecked = true;
                OptDesktop.IsChecked = false;
            }
            OptStartup.IsChecked = Setup.StartupEnabled;
        }

        _poll.Tick += (_, _) => Refresh();
        Loaded += OnLoaded;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !_busy) { e.Handled = true; FadeAndClose(); }
        };
    }

    // ---------------------------------------------------------------- motion

    private static readonly IEasingFunction EaseOut = new QuinticEase { EasingMode = EasingMode.EaseOut };
    private static readonly IEasingFunction EaseSoft = new CubicEase { EasingMode = EasingMode.EaseOut };
    private static readonly IEasingFunction EaseIn = new CubicEase { EasingMode = EasingMode.EaseIn };

    private static DoubleAnimation Anim(double to, int ms, int delay = 0, IEasingFunction? ease = null, double? from = null)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease ?? EaseSoft };
        if (from.HasValue) a.From = from.Value;
        if (delay > 0) a.BeginTime = TimeSpan.FromMilliseconds(delay);
        return a;
    }

    private static TranslateTransform Shift(UIElement e)
    {
        if (e.RenderTransform is TranslateTransform t) return t;
        t = new TranslateTransform();
        e.RenderTransform = t;
        return t;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        BeginAnimation(OpacityProperty, Anim(1, 220));
        ShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(1, 420, ease: EaseOut));
        ShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(1, 420, ease: EaseOut));
        var settle = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 };
        FrontLeafShift.BeginAnimation(TranslateTransform.XProperty, Anim(0, 560, 160, settle));
        FrontLeafShift.BeginAnimation(TranslateTransform.YProperty, Anim(0, 560, 160, settle));

        Go(_flow[0], forward: true);
    }

    /// <summary>Slides the current page out and the next one in, its rows following one by one.</summary>
    private void Go(Step step, bool forward)
    {
        _step = step;
        var next = PageFor(step);
        var old = _page;
        _page = next;
        int dir = forward ? 1 : -1;

        if (old != null && old != next)
        {
            old.IsHitTestVisible = false;
            var fade = Anim(0, 170, ease: EaseIn);
            fade.Completed += (_, _) =>
            {
                if (_page == old) return;
                old.Visibility = Visibility.Collapsed;
                old.IsHitTestVisible = true;
                old.BeginAnimation(OpacityProperty, null);
                Shift(old).BeginAnimation(TranslateTransform.XProperty, null);
            };
            old.BeginAnimation(OpacityProperty, fade);
            Shift(old).BeginAnimation(TranslateTransform.XProperty, Anim(-22 * dir, 190, ease: EaseIn));
        }

        next.BeginAnimation(OpacityProperty, null);
        Shift(next).BeginAnimation(TranslateTransform.XProperty, null);
        next.Opacity = 1;
        next.Visibility = Visibility.Visible;

        int i = 0;
        int start = old == null ? 140 : 120;
        foreach (UIElement child in ((Panel)next).Children)
        {
            if (child.Visibility != Visibility.Visible) continue;
            int delay = start + i * 50;
            child.Opacity = 0;
            child.BeginAnimation(OpacityProperty, Anim(1, 300, delay, from: 0));
            var t = Shift(child);
            if (step == Step.Done)
                t.BeginAnimation(TranslateTransform.YProperty, Anim(0, 460, delay, EaseOut, from: 12));
            else
                t.BeginAnimation(TranslateTransform.XProperty, Anim(0, 460, delay, EaseOut, from: 30 * dir));
            i++;
        }

        UpdateMarks();
        UpdateButtons();
        Refresh();

        if (step is Step.Folder or Step.Remove) _poll.Start(); else _poll.Stop();
        if (step == Step.Folder) Dispatcher.BeginInvoke(new Action(() => { FolderBox.Focus(); FolderBox.CaretIndex = FolderBox.Text.Length; }), DispatcherPriority.Input);
        if (step == Step.Done) PlayDone();
    }

    private FrameworkElement PageFor(Step step) => step switch
    {
        Step.Folder => PageFolder,
        Step.Options => PageOptions,
        Step.Remove => PageRemove,
        Step.Progress => PageProgress,
        _ => PageDone,
    };

    private void UpdateMarks()
    {
        int at = Array.IndexOf(_flow, _step);
        for (int i = 0; i < _marks.Count; i++)
        {
            var m = _marks[i];
            m.BeginAnimation(WidthProperty, Anim(i == at ? 20 : 6, 380, ease: EaseOut));
            m.SetResourceReference(Border.BackgroundProperty, i == at ? "Accent" : i < at ? "AccentLine" : "LineStrong");
        }
    }

    private void PlayDone()
    {
        var pop = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 };
        DoneDiscScale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(1, 520, 80, pop, from: 0.5));
        DoneDiscScale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(1, 520, 80, pop, from: 0.5));
        DoneCheck.BeginAnimation(Shape_StrokeDashOffset, Anim(0, 420, 300, EaseSoft, from: 14));
    }

    private static readonly DependencyProperty Shape_StrokeDashOffset = System.Windows.Shapes.Shape.StrokeDashOffsetProperty;

    private void ShowWarning(Border box, TextBlock text, string? message)
    {
        bool show = message != null;
        if (show) text.Text = message;
        bool visible = box.Visibility == Visibility.Visible && box.Tag as string != "hiding";
        if (show == visible) return;

        if (show)
        {
            box.Tag = null;
            box.Visibility = Visibility.Visible;
            box.BeginAnimation(OpacityProperty, Anim(1, 220));
            Shift(box).BeginAnimation(TranslateTransform.YProperty, Anim(0, 320, ease: EaseOut, from: -6));
        }
        else
        {
            box.Tag = "hiding";
            var fade = Anim(0, 160);
            fade.Completed += (_, _) => { if (box.Tag as string == "hiding") box.Visibility = Visibility.Collapsed; };
            box.BeginAnimation(OpacityProperty, fade);
        }
    }

    // ---------------------------------------------------------------- state

    /// <summary>Checks the folder (and whether Overskin is running from it) and enables Next to match.</summary>
    private void Refresh()
    {
        if (_step == Step.Folder)
        {
            string text = FolderBox.Text;
            string? problem = _payloadSize == 0 ? "No Overskin.exe in this setup" : Setup.CheckFolder(text);
            if (problem == null && Setup.InUse(Setup.Normalize(text)))
                problem = "Overskin is running from this folder. Close it to continue.";
            FolderNote.Text = _payloadSize > 0 ? $"{_payloadSize / 1024.0 / 1024.0:0} MB" : "";
            ShowWarning(FolderWarning, FolderWarningText, problem);
            NextButton.IsEnabled = problem == null;
        }
        else if (_step == Step.Remove)
        {
            string? problem = !Setup.IsInstallFolder(_folder) ? "Overskin isn't installed here"
                : Setup.InUse(_folder) ? "Overskin is running. Close it to continue."
                : null;
            ShowWarning(RemoveWarning, RemoveWarningText, problem);
            NextButton.IsEnabled = problem == null;
        }
    }

    private void UpdateButtons()
    {
        BackButton.Visibility = Visibility.Visible;
        NextButton.Visibility = Visibility.Visible;
        BackButton.IsEnabled = true;
        NextButton.IsEnabled = true;
        CloseButton.IsEnabled = !_busy;

        switch (_step)
        {
            case Step.Folder:
                BackButton.Visibility = Visibility.Hidden;
                NextButton.Content = "Next";
                break;
            case Step.Options:
                BackButton.Content = "Back";
                NextButton.Content = File.Exists(Path.Combine(_folder, Setup.ExeName)) ? "Update" : "Install";
                break;
            case Step.Remove:
                BackButton.Content = "Cancel";
                NextButton.Content = "Remove";
                break;
            case Step.Progress:
                BackButton.Content = "Back";
                BackButton.Visibility = _failed && !_uninstall ? Visibility.Visible : Visibility.Hidden;
                NextButton.Content = "Try again";
                NextButton.Visibility = _failed ? Visibility.Visible : Visibility.Hidden;
                break;
            case Step.Done:
                if (_uninstall || Setup.RunningAnywhere)
                {
                    BackButton.Visibility = Visibility.Hidden;
                    NextButton.Content = "Close";
                }
                else
                {
                    BackButton.Content = "Close";
                    NextButton.Content = "Launch Overskin";
                }
                break;
        }
    }

    // ---------------------------------------------------------------- actions

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !NextButton.IsEnabled || NextButton.Visibility != Visibility.Visible) return;
        switch (_step)
        {
            case Step.Folder:
                _folder = Setup.Normalize(FolderBox.Text);
                Go(Step.Options, true);
                break;
            case Step.Options:
            case Step.Remove:
                _ = Run();
                break;
            case Step.Progress:
                _ = Run();
                break;
            case Step.Done:
                if (!_uninstall && !Setup.RunningAnywhere)
                {
                    try { App.LaunchApp(_folder); }
                    catch (Exception ex) { MessageBox.Show(this, ex.Message, "Overskin", MessageBoxButton.OK, MessageBoxImage.Error); }
                }
                FadeAndClose();
                break;
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        switch (_step)
        {
            case Step.Options: Go(Step.Folder, false); break;
            case Step.Progress: _failed = false; Go(Step.Options, false); break;
            case Step.Remove:
            case Step.Done: FadeAndClose(); break;
        }
    }

    private async Task Run()
    {
        _busy = true;
        _failed = false;
        ProgressTitle.Text = _uninstall ? "Removing" : File.Exists(Path.Combine(_folder, Setup.ExeName)) ? "Updating" : "Installing";
        ProgressStatus.ClearValue(TextBlock.ForegroundProperty);
        ProgressFill.SetResourceReference(Border.BackgroundProperty, "Accent");
        ProgressStatus.Text = "";
        ProgressPercent.Text = "0%";
        ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ProgressScale.ScaleX = 0;
        if (_step != Step.Progress) Go(Step.Progress, true);
        else UpdateButtons();

        var options = new InstallOptions
        {
            Folder = _folder,
            StartMenu = OptStartMenu.IsChecked == true,
            Desktop = OptDesktop.IsChecked == true,
            Startup = OptStartup.IsChecked == true,
        };
        bool removeSettings = OptRemoveSettings.IsChecked == true;
        string? payload = _args.Payload;

        void Report(double p, string status) => Dispatcher.BeginInvoke(new Action(() => SetProgress(p, status)));

        try
        {
            // the work is quick; a short floor keeps the bar readable instead of a flash
            var work = Task.Run(() =>
            {
                if (_uninstall) Setup.Uninstall(_folder, removeSettings, Report);
                else Setup.Install(options, payload, Report);
            });
            await Task.WhenAll(work, Task.Delay(1100));
            SetProgress(1, "Done");
            await Task.Delay(450);
            _busy = false;
            _removed = _uninstall;
            DoneTitle.Text = _uninstall ? "Overskin is removed" : "Overskin is installed";
            DoneNote.Text = _uninstall ? "" : Setup.RunningAnywhere ? "Overskin is already running" : _folder;
            Go(Step.Done, true);
        }
        catch (Exception ex)
        {
            _busy = false;
            _failed = true;
            ProgressTitle.Text = _uninstall ? "Couldn't remove Overskin" : "Couldn't install";
            ProgressStatus.SetResourceReference(TextBlock.ForegroundProperty, "Warn");
            ProgressFill.SetResourceReference(Border.BackgroundProperty, "Warn");
            ProgressStatus.Text = ex.Message;
            ProgressPercent.Text = "";
            UpdateButtons();
        }
    }

    private void SetProgress(double p, string status)
    {
        if (!_busy && p < 1) return;
        ProgressStatus.Text = status;
        ProgressPercent.Text = $"{Math.Round(p * 100):0}%";
        ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(p, 320, ease: EaseSoft));
    }

    private void FolderBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded) Refresh();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        string? picked = Native.PickFolder(new WindowInteropHelper(this).Handle, FolderBox.Text);
        if (picked == null) return;
        if (!Path.GetFileName(picked.TrimEnd('\\')).Equals(Setup.AppName, StringComparison.OrdinalIgnoreCase))
            picked = Path.Combine(picked, Setup.AppName);
        FolderBox.Text = picked;
        FolderBox.CaretIndex = picked.Length;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy) FadeAndClose();
    }

    private void FadeAndClose()
    {
        if (_closing) return;
        _closing = true;
        _poll.Stop();
        var fade = Anim(0, 160, ease: EaseIn);
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
        ShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(0.975, 160, ease: EaseIn));
        ShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(0.975, 160, ease: EaseIn));
    }

    /// <summary>Developer check: renders each page to PNG, offscreen, without touching anything.</summary>
    internal async Task Snapshot(string dir)
    {
        Directory.CreateDirectory(dir);
        async Task Shot(string name, int wait = 900)
        {
            await Task.Delay(wait);
            var root = (FrameworkElement)Content;
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)ActualWidth * 2, (int)ActualHeight * 2, 192, 192, PixelFormats.Pbgra32);
            bmp.Render(root);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using var fs = File.Create(Path.Combine(dir, name + ".png"));
            enc.Save(fs);
        }

        await Shot(_uninstall ? "1-remove" : "1-folder", 1200);
        if (_uninstall)
        {
            ShowWarning(RemoveWarning, RemoveWarningText, "Overskin is running. Close it to continue.");
            await Shot("2-remove-running", 500);
        }
        else
        {
            ShowWarning(FolderWarning, FolderWarningText, "Overskin is running from this folder. Close it to continue.");
            NextButton.IsEnabled = false;
            await Shot("2-folder-running", 500);
            _poll.Stop();
            ShowWarning(FolderWarning, FolderWarningText, null);
            _folder = Setup.Normalize(FolderBox.Text);
            Go(Step.Options, true);
            await Shot("3-options-mid", 160);
            await Shot("4-options");
        }
        _busy = true;
        ProgressTitle.Text = _uninstall ? "Removing" : "Installing";
        Go(Step.Progress, true);
        SetProgress(0.62, _uninstall ? "Removing Overskin" : "Copying Overskin");
        await Shot("5-progress");
        _busy = false;
        _failed = true;
        ProgressTitle.Text = "Couldn't install";
        ProgressStatus.SetResourceReference(TextBlock.ForegroundProperty, "Warn");
        ProgressStatus.Text = "Overskin is running from this folder. Close it and try again.";
        ProgressPercent.Text = "";
        UpdateButtons();
        await Shot("6-failed", 300);
        _failed = false;
        DoneTitle.Text = _uninstall ? "Overskin is removed" : "Overskin is installed";
        DoneNote.Text = _uninstall ? "" : _folder;
        Go(Step.Done, true);
        await Shot("7-done-mid", 330);
        await Shot("8-done");
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_busy) { e.Cancel = true; return; }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        // running as the temporary uninstaller copy: clean up behind us
        if (_uninstall && _args.Relaunched) App.CleanUpAfterUninstall(_args, _removed);
    }
}
