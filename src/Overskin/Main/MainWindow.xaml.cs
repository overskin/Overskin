using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using static Overskin.NativeMethods;

namespace Overskin;

public partial class MainWindow : Window
{
    private const int HkPin = 1, HkOpacityUp = 2, HkOpacityDown = 3, HkClickThrough = 4;
    private const int HkPickEsc = 7, HkDraw = 8, HkSnip = 11;
    private const int HkRegion = 12, HkWindow = 13, HkScreen = 14, HkText = 15, HkColour = 16, HkRecent = 21, HkPinClipboard = 22, HkVideo = 23, HkRing = 26, HkSearch = 27, HkReplay = 28;
    private const double BaseWidth = 540, BaseHeight = 700, SmallestWidth = 530, SmallestHeight = 560;
    private const int TabCaptureIndex = 0, TabLibraryIndex = 1, TabPinsIndex = 2, TabSettingsIndex = 3;

    private readonly bool _ready;

    private AppSettings _settings = AppSettings.Load();
    private readonly WindowManager _manager = new();
    private readonly ObservableCollection<WindowItem> _windows = new();
    private readonly ObservableCollection<ImageOverlayWindow> _images = new();
    private readonly HashSet<IntPtr> _pickedExtras = new();

    private HotkeyManager? _hotkeys;
    private TrayIcon? _tray;
    private WindowPicker? _picker;
    private Button? _capturing;
    private int _tab;
    private bool _exiting;
    private bool _cleaned;

    // polls the grab keys while any image is pinned
    private readonly DispatcherTimer _grabTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private int _grabTicks;

    /// <summary>
    /// A page's content leaves 6 px on the right because the scroll bar sits there too. When the page
    /// is short enough that no scroll bar shows, that left the right edge narrower than the left one,
    /// so the right gap is widened to match while the bar is hidden.
    /// </summary>
    static void KeepSidesEven(System.Windows.Controls.ScrollViewer page)
    {
        void Fit()
        {
            if (page.Content is not FrameworkElement content) return;
            var m = content.Margin;
            double right = page.ComputedVerticalScrollBarVisibility == Visibility.Visible ? 6 : m.Left;
            if (m.Right != right) content.Margin = new Thickness(m.Left, m.Top, right, m.Bottom);
        }
        page.ScrollChanged += (_, _) => Fit();
        page.Loaded += (_, _) => Fit();
        page.IsVisibleChanged += (_, _) => Fit();
    }

    public MainWindow()
    {
        ApplyColours(); // before any control looks up its colours
        InitializeComponent();
        if (_settings.SeeThrough && WindowLook.GlassSupported)
        {
            _glass = true;
            WindowShadow.Visibility = Visibility.Collapsed;
            if (WindowLook.IsWindows11)
            {
                // Windows 11 draws the corners, shadow and blurred background itself
                WindowLook.PrepareGlass(this);
                WindowFrame.CornerRadius = new CornerRadius(0);
                WindowFrame.BorderThickness = new Thickness(0);
            }
            else
            {
                // Windows 10: Overskin's own rounded window, blurred inside its outline only
                WindowLook.BlurInside(this, WindowFrame);
            }
            ApplyColours();
        }
        WindowLook.AccentChanged += () => Dispatcher.BeginInvoke(() => { if (_settings.MatchWindows) ApplyTheme(); });
        if (_settings.MatchWindows) WindowLook.Watch();
        foreach (var page in new[] { PageCapture, PageSettings }) KeepSidesEven(page);
        AutoHideRow.ContextMenu = Redaction.KindsMenu();
        Redaction.KindsChanged += () => { _settings.AutoHideKinds = Redaction.Kinds; _settings.Save(); };

        WindowList.ItemsSource = _windows;
        System.Windows.Data.CollectionViewSource.GetDefaultView(_windows).Filter = MatchesFilter;
        _grabTimer.Tick += (_, _) => UpdateGrab();
        ImageList.ItemsSource = _images;
        LibraryList.ItemsSource = _library;
        _images.CollectionChanged += (_, _) =>
        {
            UpdateStatus();
            if (_images.Count > 0) _grabTimer.Start(); else _grabTimer.Stop();
        };

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version == null ? "" : $"v{version.Major}.{version.Minor}.{version.Build}";
        AboutVersion.Text = "Overskin " + VersionText.Text;

        Wheel.ColorChanged += WheelChanged;
        BuildAppearanceControls();
        ApplySettingsToControls();
        ApplyScale();
        RestorePosition();
        PinsPictures.IsChecked = true;
        RefreshLibrary();
        SetUpFolds();
        SetUpCleanUp();
        SetUpUpdates();
        Loaded += (_, _) => FadeIn();

        _ready = true;
        VideoEditorWindow.OpenPicture = (picture, name) => OpenEditor(picture, null, name);
        VideoEditorWindow.OpenGif = (clip, name, caption) => OpenGifEditor(clip, name, caption);
        SelectTab(_settings.LastTab);
        UpdateStatus();
    }

    // =====================================================================================
    //  startup / shutdown
    // =====================================================================================

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // looked up by handle: when starting hidden (--tray, --run) the window has a handle but
        // isn't shown yet, and PresentationSource.FromVisual returns null until it is
        var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);

        _hotkeys = new HotkeyManager(source);
        _tray = new TrayIcon(source, "Overskin", FillTrayMenu);
        SetUpJumpList();
        _tray.ShowRequested += ShowFromTray;
        _tray.ExitRequested += ExitApp;

        _picker = new WindowPicker(Dispatcher, _manager.IsOwnWindow);
        _picker.Finished += OnPickFinished;

        RegisterHotkeys(announceFailures: true);
        RefreshWindows();
        RestorePinnedImages();
        if (_settings.ReplayOn) StartReplay();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (_ready && _tab == TabPinsIndex && PinsWindows.IsChecked == true) RefreshWindows();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting && _settings.CloseToTray)
        {
            e.Cancel = true;
            FadeOutThenHide();
            if (!_settings.TrayHintShown)
            {
                _settings.TrayHintShown = true;
                Toast.Show("Overskin is still running. Find it in the tray, next to the clock.");
            }
            return;
        }
        _exiting = true;
        Cleanup();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Application.Current.Shutdown();
    }

    private void ShowFromTray()
    {
        bool wasHidden = !IsVisible;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        if (wasHidden) FadeIn();
    }

    // ---- motion --------------------------------------------------------------------------

    private static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };
    private bool _fadingOut;

    /// <summary>The window settles in: fades up from slightly smaller.</summary>
    private void FadeIn()
    {
        var scale = new ScaleTransform(0.97, 0.97);
        Root.RenderTransformOrigin = new Point(0.5, 0.5);
        Root.RenderTransform = scale;
        Root.Opacity = 0;
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(170)));
        var grow = new DoubleAnimation(1, TimeSpan.FromMilliseconds(240)) { EasingFunction = EaseOut };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    private void FadeOutThenHide()
    {
        if (_fadingOut) return;
        _fadingOut = true;
        var scale = new ScaleTransform(1, 1);
        Root.RenderTransformOrigin = new Point(0.5, 0.5);
        Root.RenderTransform = scale;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(130));
        fade.Completed += (_, _) =>
        {
            Hide();
            Root.BeginAnimation(OpacityProperty, null);
            Root.Opacity = 1;
            _fadingOut = false;
        };
        Root.BeginAnimation(OpacityProperty, fade);
        var shrink = new DoubleAnimation(0.97, TimeSpan.FromMilliseconds(130));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
    }

    /// <summary>A page arriving slides a little in the direction of travel as it fades in.</summary>
    private static void SlideIn(FrameworkElement page, int direction)
    {
        var move = new TranslateTransform(18 * direction, 0);
        page.RenderTransform = move;
        page.Opacity = 0;
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
        move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(260)) { EasingFunction = EaseOut });
    }

    /// <summary>Closes for good, so <see cref="App.Restart"/> can open a new copy.</summary>
    internal void ExitForRestart() => ExitApp();

    private void ExitApp()
    {
        _exiting = true;
        Close();
    }

    /// <summary>
    /// Undoes everything we did to the outside world. Safe to call more than once and from any
    /// exit path, which is why every step is isolated from the others.
    /// </summary>
    public void Cleanup()
    {
        if (_cleaned) return;
        _cleaned = true;

        Try(() => _search.Save());
        Try(() => _pictures.Save());
        Try(() => _manager.RestoreAll());    // puts other programs' windows back
        Try(() => _picker?.Dispose());
        Try(() => _tray?.Dispose());
        Try(() => _hotkeys?.Dispose());
        Try(() => _replay?.Dispose());
        Try(SaveSettings);
        Try(() =>
        {
            foreach (var image in _images.ToList()) image.Close();
            Toast.Shutdown();
        });

        static void Try(Action action)
        {
            try { action(); }
            catch { /* one failed step must not stop the rest */ }
        }
    }

    // =====================================================================================
    //  settings
    // =====================================================================================

    private void ApplySettingsToControls()
    {
        CloseToTraySwitch.IsChecked = _settings.CloseToTray;
        AlwaysOnTopSwitch.IsChecked = _settings.AlwaysOnTop;
        GrabSwitch.IsChecked = _settings.GrabWithCtrlAlt;
        RestoreImagesSwitch.IsChecked = _settings.RestoreImages;
        StartupSwitch.IsChecked = StartupRegistration.IsEnabled;
        if (StartupSwitch.IsChecked == true) StartupRegistration.Refresh();
        foreach (RadioButton chip in ScaleChoices.Children)
            chip.IsChecked = (string)chip.Tag == _settings.UiScale.ToString();
        Topmost = _settings.AlwaysOnTop;
        SyncAppearanceControls();
        ApplyCaptureSettingsToControls();

        UpdateHotkeyLabels();
        SettingsPathText.Text = AppSettings.FilePath;
    }

    private void SaveSettings()
    {
        _settings.CloseToTray = CloseToTraySwitch.IsChecked == true;
        _settings.AlwaysOnTop = AlwaysOnTopSwitch.IsChecked == true;
        _settings.GrabWithCtrlAlt = GrabSwitch.IsChecked == true;
        _settings.RestoreImages = RestoreImagesSwitch.IsChecked == true;
        _settings.PinnedImages = _settings.RestoreImages
            ? _images.Where(i => !string.IsNullOrEmpty(i.SourcePath)).Select(i => i.ToState()).ToList()
            : new List<PinnedImageState>();
        _settings.LastTab = _tab;
        if (WindowState == WindowState.Normal && !double.IsNaN(Left) && !double.IsNaN(Top))
        {
            _settings.WindowLeft = Left;
            _settings.WindowTop = Top;
        }
        _settings.Save();
        SettingsPathText.Text = AppSettings.FilePath;
    }

    private void RestorePosition()
    {
        if (_settings.WindowLeft is not { } left || _settings.WindowTop is not { } top) return;

        // only reuse the saved spot if it is still on a connected screen
        double vx = SystemParameters.VirtualScreenLeft, vy = SystemParameters.VirtualScreenTop;
        double vw = SystemParameters.VirtualScreenWidth, vh = SystemParameters.VirtualScreenHeight;
        if (left < vx - 20 || top < vy - 20 || left + Width > vx + vw + 20 || top + 80 > vy + vh) return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left;
        Top = top;
    }

    private void UpdateHotkeyLabels()
    {
        HkPinButton.Content = _settings.HotkeyPin;
        HkUpButton.Content = _settings.HotkeyOpacityUp;
        HkDownButton.Content = _settings.HotkeyOpacityDown;
        HkClickButton.Content = _settings.HotkeyClickThrough;
        HkRecPenButton.Content = _settings.HotkeyDraw;
        HkRecPauseButton.Content = _settings.HotkeyPause;
        TileVideoKey.Text = _settings.HotkeyVideo;
        RecordHint.Text = "";
        RecordHint.ToolTip = $"While recording: {_settings.HotkeyPause} pauses, {_settings.HotkeyDraw} draws";
        HkSnipButton.Content = _settings.HotkeySnip;
        HintSnipKey.Text = _settings.HotkeySnip;
        HkRegionButton.Content = _settings.HotkeyRegion;
        HkWindowButton.Content = _settings.HotkeyWindow;
        HkScreenButton.Content = _settings.HotkeyScreen;
        HkTextButton.Content = _settings.HotkeyText;
        HkColourButton.Content = _settings.HotkeyColour;
        HkGifButton.Content = _settings.HotkeyGif;
        HkRecentButton.Content = _settings.HotkeyRecent;
        HkPinClipboardButton.Content = _settings.HotkeyPinClipboard;
        HkVideoButton.Content = _settings.HotkeyVideo;
        HkRingButton.Content = _settings.HotkeyRing;
        HkSearchButton.Content = _settings.HotkeySearch;
        HkReplayButton.Content = _settings.HotkeyReplay;
        UpdateReplayStatus();
        TileGifKey.Text = _settings.HotkeyGif;
        TileRegionKey.Text = _settings.HotkeyRegion;
        TileWindowKey.Text = _settings.HotkeyWindow;
        TileScreenKey.Text = _settings.HotkeyScreen;
        ToolSnipButton.ToolTip = _settings.HotkeySnip;
        ToolTextButton.ToolTip = _settings.HotkeyText;
        ToolColourButton.ToolTip = _settings.HotkeyColour;
        HintClickKey.Text = _settings.HotkeyClickThrough;
        HintGrabKey.Visibility = HintGrabText.Visibility = _settings.GrabWithCtrlAlt ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void StartupSwitch_Click(object sender, RoutedEventArgs e)
    {
        bool want = StartupSwitch.IsChecked == true;
        if (await StartupRegistration.SetAsync(want) is { } problem)
        {
            StartupSwitch.IsChecked = StartupRegistration.IsEnabled;
            Toast.Show(problem, true);
        }
    }

    // ---- size ----------------------------------------------------------------------------

    private double UiScale => _settings.UiScale / 100.0;

    /// <summary>Scales everything inside the window and sizes the window to match, keeping it on screen.</summary>
    private void ApplyScale()
    {
        double s = UiScale;
        RootScale.ScaleX = RootScale.ScaleY = s;
        // "Display" text is hinted for its exact pixel size, so any scaling blurs it;
        // "Ideal" text is laid out as outlines and stays sharp at every size.
        TextOptions.SetTextFormattingMode(this, Math.Abs(s - 1) < 0.001 ? TextFormattingMode.Display : TextFormattingMode.Ideal);

        var area = SystemParameters.WorkArea;
        double w = Math.Clamp(_settings.WindowWidth ?? BaseWidth, SmallestWidth, Math.Max(SmallestWidth, area.Width / s));
        double h = Math.Clamp(_settings.WindowHeight ?? BaseHeight, SmallestHeight, Math.Max(SmallestHeight, area.Height / s));
        Width = w * s;
        Height = h * s;

        if (!double.IsNaN(Left) && Left + Width > area.Right) Left = Math.Max(area.Left, area.Right - Width);
        if (!double.IsNaN(Top) && Top + Height > area.Bottom) Top = Math.Max(area.Top, area.Bottom - Height);
    }

    private void Scale_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not RadioButton { Tag: string tag } || !int.TryParse(tag, out int percent)) return;
        _settings.UiScale = percent;
        ApplyScale();
        SaveSettings();
    }

    private void ResizeGrip_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        // the grip sits inside the scaled content, so its deltas are already in unscaled units
        double s = UiScale;
        var area = SystemParameters.WorkArea;
        double w = Math.Clamp(Width / s + e.HorizontalChange, SmallestWidth, Math.Max(SmallestWidth, (area.Right - Left) / s));
        double h = Math.Clamp(Height / s + e.VerticalChange, SmallestHeight, Math.Max(SmallestHeight, (area.Bottom - Top) / s));
        _settings.WindowWidth = w;
        _settings.WindowHeight = h;
        Width = w * s;
        Height = h * s;
    }

    private void ResizeGrip_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) => SaveSettings();

    private void GeneralSwitch_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings(); // reads all three switches
        Topmost = _settings.AlwaysOnTop;
        UpdateHotkeyLabels();
        if (!_settings.GrabWithCtrlAlt)
            foreach (var image in _images) image.SetGrabbed(false);
    }

    // ---- appearance ------------------------------------------------------------------------

    private bool _syncingAppearance;

    /// <summary>Creates the accent swatches and background choices from the lists in Theming.</summary>
    private void BuildAppearanceControls()
    {
        var wheelBrush = new ImageBrush(ColorWheel.CreateBitmap(48)) { Stretch = Stretch.Uniform };
        wheelBrush.Freeze();
        WheelSwatch.Background = wheelBrush;

        foreach (var (name, hex) in Theming.Accents)
        {
            Theming.TryParseHex(hex, out var color);
            var swatch = new RadioButton
            {
                Style = (Style)FindResource("Swatch"),
                GroupName = "Accent",
                Tag = hex,
                ToolTip = name,
                Background = new SolidColorBrush(color),
            };
            swatch.Checked += (_, _) => { if (_ready && !_syncingAppearance) SetAccent(hex); };
            AccentSwatches.Children.Add(swatch);
        }

    }

    // ---- themes ------------------------------------------------------------------------------

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        bool light = Theming.Find(_settings.BackgroundTone).Light;
        var menu = new ContextMenu
        {
            PlacementTarget = ThemeButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            MinWidth = ThemeButton.ActualWidth,
        };
        foreach (var theme in Theming.Themes.Where(t => t.Light == light))
        {
            var item = new MenuItem
            {
                Header = theme.Name,
                IsCheckable = false,
                IsChecked = theme.Name == _settings.BackgroundTone,
            };
            item.Click += (_, _) => PickTheme(theme);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void PickTheme(ThemePreset theme)
    {
        _settings.BackgroundTone = theme.Name;
        if (theme.Light) _settings.LastLightTheme = theme.Name;
        else _settings.LastDarkTheme = theme.Name;
        // the editor themes come with their own accent
        if (theme.Accent != null) _settings.AccentColor = theme.Accent;
        ApplyTheme();
    }

    private void ThemeMode_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || _syncingAppearance || sender is not RadioButton { Tag: string mode }) return;
        bool light = mode == "Light";
        if (Theming.Find(_settings.BackgroundTone).Light == light) return;
        PickTheme(Theming.Find(light ? _settings.LastLightTheme : _settings.LastDarkTheme));
    }

    private void SyncAppearanceControls()
    {
        _syncingAppearance = true;
        WheelSwatch.IsChecked = Theming.Accents.All(a => !string.Equals(a.Hex, _settings.AccentColor, StringComparison.OrdinalIgnoreCase));
        foreach (RadioButton swatch in AccentSwatches.Children)
            swatch.IsChecked = string.Equals((string)swatch.Tag, _settings.AccentColor, StringComparison.OrdinalIgnoreCase);
        var current = Theming.Find(_settings.BackgroundTone);
        ModeDark.IsChecked = !current.Light;
        ModeLight.IsChecked = current.Light;
        ThemeName.Text = current.Name;
        AccentHexBox.Text = _settings.AccentColor;
        MatchWindowsSwitch.IsChecked = _settings.MatchWindows;
        SeeThroughSwitch.IsChecked = _settings.SeeThrough;
        SeeThroughSwitch.IsEnabled = WindowLook.GlassSupported;
        AccentSwatches.IsEnabled = WheelSwatch.IsEnabled = AccentHexBox.IsEnabled = !_settings.MatchWindows;
        _syncingAppearance = false;
    }

    // ---- match Windows -----------------------------------------------------------------------

    /// <summary>True when this window was opened see-through.</summary>
    private bool _glass;

    /// <summary>The theme and accent, with Windows' own accent when matching Windows.</summary>
    private void ApplyColours()
    {
        string accent = _settings.AccentColor;
        if (_settings.MatchWindows && WindowLook.WindowsAccent(!Theming.Find(_settings.BackgroundTone).Light) is { } windows)
            accent = Theming.ToHex(windows);
        Theming.Apply(accent, _settings.BackgroundTone);
        if (_glass) ApplyGlassColours();
    }

    /// <summary>The window's own background and cards, see-through so the blur shows.</summary>
    private void ApplyGlassColours()
    {
        var res = Application.Current.Resources;
        Color Of(string key) => ((SolidColorBrush)res[key]).Color;
        bool light = Theming.IsLight;
        // How much of each colour covers the blur. Light themes need more, or whatever is behind
        // turns them a muddy grey; Windows 10's blur has no tint of its own, so it needs a little
        // more again. Cards sit a step more solid than the window so they still read as cards.
        double more = WindowLook.IsWindows11 ? 0 : 0.08;
        double bg = (light ? 0.74 : 0.58) + more, card = (light ? 0.82 : 0.66) + more, field = (light ? 0.9 : 0.78) + more;
        Resources["Bg"] = WindowLook.Clear(Of("Bg"), bg);
        Resources["Panel"] = WindowLook.Clear(Of("Panel"), card);
        Resources["TileFill"] = WindowLook.Clear(Of("TileFill"), card);
        Resources["MenuBg"] = WindowLook.Clear(Of("MenuBg"), Math.Min(1, card + 0.1));
        foreach (var key in new[] { "Field", "FieldHover", "ButtonFill", "ButtonFillHover" })
            Resources[key] = WindowLook.Clear(Of(key), Math.Min(1, field));
        // edges a touch stronger, so cards keep their outline over a busy background
        Resources["Line"] = WindowLook.Clear(Of(light ? "LineStrong" : "Line"), light ? 0.7 : 1);
        WindowLook.Apply(this, !light);
    }

    private void MatchWindows_Click(object sender, RoutedEventArgs e)
    {
        if (_syncingAppearance) return;
        _settings.MatchWindows = MatchWindowsSwitch.IsChecked == true;
        SaveSettings();
        if (_settings.MatchWindows) WindowLook.Watch();
        ApplyTheme();
        AccentSwatches.IsEnabled = WheelSwatch.IsEnabled = AccentHexBox.IsEnabled = !_settings.MatchWindows;
        if (_settings.MatchWindows && WindowLook.WindowsAccent(false) is { } c && !App.Quiet)
        {
            // Windows' accent can be a grey, which then colours Overskin grey too
            var (_, s, _) = Theming.ToHsv(c);
            if (s < 0.15)
                Toast.Show("Windows' accent colour is a grey, so Overskin goes grey too. Pick a colour in Windows' Personalisation, Colours, or turn this off.");
        }
    }

    private void SeeThrough_Click(object sender, RoutedEventArgs e)
    {
        if (_syncingAppearance) return;
        _settings.SeeThrough = SeeThroughSwitch.IsChecked == true;
        SaveSettings();
        bool wantGlass = _settings.SeeThrough && WindowLook.GlassSupported;
        if (wantGlass != _glass && !App.Quiet
            && AskDialog.Ask(this, "Restart Overskin?", wantGlass
                ? "The see-through window starts when Overskin opens again."
                : "The window goes back to its own look when Overskin opens again.", "IcRefreshCw",
                ("Restart now", true), ("Later", false)) == 0)
            App.Restart();
    }

    // ---- colour wheel ----------------------------------------------------------------------

    private bool _wheelSync;

    private void WheelSwatch_Click(object sender, RoutedEventArgs e)
    {
        Theming.TryParseHex(_settings.AccentColor, out var current);
        var (h, s, v) = Theming.ToHsv(current);
        _wheelSync = true;
        Wheel.SetHueSaturation(h, s);
        BrightnessSlider.Value = Math.Max(BrightnessSlider.Minimum, v * 100);
        _wheelSync = false;
        UpdateWheelPreview(current);
        WheelPopup.IsOpen = true;
    }

    private void WheelChanged()
    {
        if (_wheelSync || !_ready) return;
        var c = Theming.FromHsv(Wheel.Hue, Wheel.Saturation, BrightnessSlider.Value / 100);
        _settings.AccentColor = Theming.ToHex(c);
        ApplyColours(); // live, without saving on every move
        AccentHexBox.Text = _settings.AccentColor;
        UpdateWheelPreview(c);
    }

    private void Brightness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => WheelChanged();

    private void UpdateWheelPreview(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        WheelPreview.Background = brush;
        WheelHex.Text = Theming.ToHex(c);
    }

    private void WheelPopup_Closed(object sender, EventArgs e) => ApplyTheme(); // settles everything and saves

    private void SetAccent(string hex)
    {
        _settings.AccentColor = hex.ToUpperInvariant();
        ApplyTheme();
    }

    private void ApplyTheme()
    {
        ApplyColours();
        SyncAppearanceControls();
        ApplyCaptureSettingsToControls();
        UpdateStatus();
        SaveSettings();
    }

    private void AccentHex_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitAccentHex();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            AccentHexBox.Text = _settings.AccentColor;
        }
    }

    private void AccentHex_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitAccentHex();

    private void CommitAccentHex()
    {
        string text = AccentHexBox.Text.Trim();
        if (text.Length == 0 || string.Equals(text, _settings.AccentColor, StringComparison.OrdinalIgnoreCase))
        {
            AccentHexBox.Text = _settings.AccentColor;
            return;
        }
        if (Theming.TryParseHex(text, out var color))
        {
            SetAccent(Theming.ToHex(color));
        }
        else
        {
            AccentHexBox.Text = _settings.AccentColor;
            Toast.Show("That isn't a colour. Use hex, like #4F8CFF.");
        }
    }

    private void OpenCrashLogs_Click(object sender, RoutedEventArgs e) => CrashLog.OpenFolder();

    private void OpenSettingsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveSettings(); // make sure the file exists before pointing at it
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{AppSettings.FilePath}\"");
        }
        catch
        {
            Toast.Show("Couldn't open the folder.", true);
        }
    }

    /// <summary>Called when a second copy is launched: come to the front instead.</summary>
    public void BringForward() => ShowFromTray();

    private void RestoreAll_Click(object sender, RoutedEventArgs e)
    {
        int count = _manager.ModifiedCount;
        _manager.RestoreAll();
        RefreshWindows();
        Toast.Show(count == 0 ? "No windows were changed." : count == 1 ? "1 window restored." : $"{count} windows restored.");
    }

    private void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        var fresh = new AppSettings
        {
            TrayHintShown = _settings.TrayHintShown,
            WindowLeft = _settings.WindowLeft,
            WindowTop = _settings.WindowTop,
            LastTab = _tab,
            // size, palette and pinned pictures are not "settings" the user means to reset
            UiScale = _settings.UiScale,
            WindowWidth = _settings.WindowWidth,
            WindowHeight = _settings.WindowHeight,
            RestoreImages = _settings.RestoreImages,
            PinnedImages = _settings.PinnedImages,
            History = _settings.History,
            SaveFolder = _settings.SaveFolder,
            LastRegion = _settings.LastRegion,
        };
        _settings = fresh;
        ApplyColours();
        ApplySettingsToControls();
        RegisterHotkeys(announceFailures: true);
        SaveSettings();
        Toast.Show("Settings reset.");
    }

    // =====================================================================================
    //  chrome, tabs, status
    // =====================================================================================

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); }
        catch (InvalidOperationException) { }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private RadioButton[] TabButtons => new[] { TabCapture, TabLibrary, TabPins, TabSettings };
    private FrameworkElement[] Pages => new FrameworkElement[] { PageCapture, PageLibrary, PagePins, PageSettings };

    private void SelectTab(int index)
    {
        var tabs = TabButtons;
        tabs[Math.Clamp(index, 0, tabs.Length - 1)].IsChecked = true;
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        CancelHotkeyCapture();

        int previous = _tab;
        _tab = Math.Max(0, Array.IndexOf(TabButtons, sender as RadioButton));
        var pages = Pages;
        for (int i = 0; i < pages.Length; i++)
            pages[i].Visibility = i == _tab ? Visibility.Visible : Visibility.Collapsed;
        if (previous != _tab) SlideIn(pages[_tab], _tab > previous ? 1 : -1);

        if (_tab == TabPinsIndex && PinsWindows.IsChecked == true) RefreshWindows();
        if (_tab == TabLibraryIndex) RefreshLibrary();
    }

    private void PinsView_Checked(object sender, RoutedEventArgs e)
    {
        bool windows = PinsWindows.IsChecked == true;
        var show = windows ? PageWindows : PageImage;
        PageWindows.Visibility = windows ? Visibility.Visible : Visibility.Collapsed;
        PageImage.Visibility = windows ? Visibility.Collapsed : Visibility.Visible;
        if (!_ready) return;
        SlideIn(show, windows ? 1 : -1);
        if (windows) RefreshWindows();
    }

    private void UpdateStatus()
    {
        if (!_ready) return;
        int windows = _manager.ModifiedCount, images = _images.Count;

        var parts = new List<string>(2);
        if (windows > 0) parts.Add(windows == 1 ? "1 window changed" : $"{windows} windows changed");
        if (images > 0) parts.Add(images == 1 ? "1 image pinned" : $"{images} images pinned");

        bool active = parts.Count > 0;
        StatusText.Text = active ? string.Join("  ·  ", parts) : $"Ready  ·  {_settings.HotkeyRegion} captures a region";
        StatusDot.SetResourceReference(Border.BackgroundProperty, active ? "Accent" : "TextFaint");

        ImageCount.Text = images == 0 ? "" : images.ToString();
        ImagesEmpty.Visibility = images == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// The pin list is only as tall as its pins (so the shortcut list sits right under them,
    /// not at the bottom of the page), up to the room there is; then it scrolls.
    /// </summary>
    private void PageImage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double used = 0;
        foreach (UIElement child in PageImage.Children)
            if (child is FrameworkElement f && Grid.GetRow(f) is 0 or 2) used += f.ActualHeight + f.Margin.Top + f.Margin.Bottom;
        ImageList.MaxHeight = Math.Max(90, PageImage.ActualHeight - used - 40);
    }

    // =====================================================================================
    //  feature 1: windows
    // =====================================================================================

    private void RefreshWindows()
    {
        var infos = _manager.ListWindows();

        // windows the user picked by hand stay listed even if the normal filter would hide them
        _pickedExtras.RemoveWhere(h => !IsWindow(h));
        foreach (var extra in _pickedExtras)
            if (infos.All(i => i.Handle != extra))
                infos.Add(new WindowInfo(extra, Describe(extra), null));

        var alive = infos.Select(i => i.Handle).ToHashSet();
        for (int i = _windows.Count - 1; i >= 0; i--)
            if (!alive.Contains(_windows[i].Handle)) _windows.RemoveAt(i);

        var existing = _windows.ToDictionary(w => w.Handle);
        foreach (var info in infos)
        {
            if (existing.TryGetValue(info.Handle, out var item))
            {
                item.Title = info.Title;
                if (info.Icon != null) item.Icon = info.Icon;
                item.Sync();
            }
            else
            {
                _windows.Add(new WindowItem(_manager, info, message => Toast.Show(message, true), UpdateStatus));
            }
        }

        UpdateWindowCount();
        UpdateStatus();
    }

    // ---- filter --------------------------------------------------------------------------

    private bool MatchesFilter(object item)
    {
        string filter = FilterBox.Text.Trim();
        return filter.Length == 0 || (item is WindowItem w && w.Title.Contains(filter, StringComparison.OrdinalIgnoreCase));
    }

    private void Filter_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        FilterHint.Visibility = FilterBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        System.Windows.Data.CollectionViewSource.GetDefaultView(_windows).Refresh();
        UpdateWindowCount();
    }

    private void UpdateWindowCount()
    {
        int shown = WindowList.Items.Count;
        WindowCount.Text = shown == _windows.Count ? shown.ToString() : $"{shown} of {_windows.Count}";
        WindowsEmpty.Visibility = shown == 0 && _windows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshWindows();

    private static string Describe(IntPtr hWnd)
    {
        string title = WindowTitle(hWnd);
        return title.Length > 0 ? title : ClassName(hWnd);
    }

    private static string Shorten(string text, int max = 34) =>
        text.Length > max ? text[..(max - 1)].TrimEnd() + "…" : text;

    /// <summary>After a hotkey changed a window, bring its row up to date.</summary>
    private void SyncWindow(IntPtr hWnd)
    {
        var item = _windows.FirstOrDefault(w => w.Handle == hWnd);
        if (item != null) item.Sync();
        else if (_manager.GetState(hWnd).Modified)
        {
            _pickedExtras.Add(hWnd); // changed by hotkey but not normally listed: show it so it can be undone
            RefreshWindows();
        }
        UpdateStatus();
    }

    // ---- pick mode -----------------------------------------------------------------------

    private void Pick_Click(object sender, RoutedEventArgs e)
    {
        if (_picker == null) return;
        if (_picker.IsActive)
        {
            _picker.Cancel();
            return;
        }
        if (!_picker.Start())
        {
            Toast.Show("Couldn't start pick mode.", true);
            return;
        }
        PickLabel.Text = "Click a window…";
        _hotkeys?.Register(HkPickEsc, new Hotkey(ModifierKeys.None, Key.Escape), () => _picker.Cancel());
        Toast.ShowSticky("Click any window to select it.  Esc or right-click cancels.");
    }

    private void OnPickFinished(IntPtr hWnd)
    {
        _hotkeys?.Unregister(HkPickEsc);
        PickLabel.Text = "Pick window";
        Toast.Hide();
        if (hWnd == IntPtr.Zero) return;

        if (IsDesktopWindow(hWnd))
        {
            Toast.Show("That's the desktop, not a window.");
            return;
        }

        if (_windows.All(w => w.Handle != hWnd)) _pickedExtras.Add(hWnd);
        RefreshWindows();

        var item = _windows.FirstOrDefault(w => w.Handle == hWnd);
        if (item == null) return;

        SelectTab(TabPinsIndex);
        PinsWindows.IsChecked = true;
        if (!MatchesFilter(item)) FilterBox.Text = ""; // a picked window must not stay hidden by the filter
        WindowList.SelectedItem = item;
        WindowList.ScrollIntoView(item);
        ShowFromTray();
    }

    private static bool IsDesktopWindow(IntPtr hWnd) =>
        ClassName(hWnd) is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";

    // ---- hotkeys -------------------------------------------------------------------------

    private void RegisterHotkeys(bool announceFailures)
    {
        if (_hotkeys == null) return;
        var failed = new List<string>();

        void Bind(int id, string text, Action action, bool repeat = false)
        {
            _hotkeys.Unregister(id);
            if (!Hotkey.TryParse(text, out var hotkey) || !_hotkeys.Register(id, hotkey, action, repeat))
                failed.Add(text);
        }

        Bind(HkPin, _settings.HotkeyPin, HotkeyTogglePin);
        Bind(HkOpacityUp, _settings.HotkeyOpacityUp, () => HotkeyOpacity(+10), repeat: true);
        Bind(HkOpacityDown, _settings.HotkeyOpacityDown, () => HotkeyOpacity(-10), repeat: true);
        Bind(HkClickThrough, _settings.HotkeyClickThrough, HotkeyToggleClickThrough);
        Bind(HkDraw, _settings.HotkeyDraw, HotkeyStartDrawing);
        Bind(HkSnip, _settings.HotkeySnip, SnipAndPin);
        Bind(HkRegion, _settings.HotkeyRegion, () => CaptureRegion());
        Bind(HkWindow, _settings.HotkeyWindow, () => CaptureActiveWindow());
        Bind(HkScreen, _settings.HotkeyScreen, () => CaptureFullScreen());
        Bind(HkText, _settings.HotkeyText, TextFromScreen);
        Bind(HkColour, _settings.HotkeyColour, PickScreenColour);
        Bind(HkGif, _settings.HotkeyGif, ToggleGifRecording);
        Bind(HkRecent, _settings.HotkeyRecent, ShowRecent);
        Bind(HkPinClipboard, _settings.HotkeyPinClipboard, PinClipboard);
        Bind(HkVideo, _settings.HotkeyVideo, () => ToggleVideo(false));
        Bind(HkRing, _settings.HotkeyRing, OpenRing);
        Bind(HkSearch, _settings.HotkeySearch, OpenSearch);
        Bind(HkReplay, _settings.HotkeyReplay, SaveReplay);
        failed.AddRange(RegisterWorkflowHotkeys());

        if (failed.Count > 0 && announceFailures && !App.Quiet)
            Toast.Show($"{string.Join(", ", failed)} is already used by another program. Change it in Settings.", true);
    }

    /// <summary>What the hotkeys act on: the window under the mouse, which may be one of our images.</summary>
    private bool TryGetTarget(out IntPtr hWnd, out ImageOverlayWindow? image)
    {
        hWnd = _manager.WindowAt(CursorPos());
        IntPtr handle = hWnd;
        image = _images.FirstOrDefault(i => i.Handle == handle);
        if (image != null) return true;

        if (hWnd == IntPtr.Zero || IsDesktopWindow(hWnd) || _manager.IsOwnWindow(hWnd))
        {
            Toast.Show("Point the mouse at another program's window first.");
            return false;
        }
        return true;
    }

    private void HotkeyTogglePin()
    {
        if (!TryGetTarget(out var hWnd, out var image)) return;
        if (image != null)
        {
            Toast.Show("Pinned images are always on top.");
            return;
        }

        bool pin = !_manager.GetState(hWnd).Pinned;
        var result = _manager.SetPinned(hWnd, pin);
        if (result.Ok) Toast.Show((pin ? "Pinned  " : "Unpinned  ") + Shorten(Describe(hWnd)));
        else Toast.Show(result.Error!, true);
        SyncWindow(hWnd);
    }

    private void HotkeyOpacity(int delta)
    {
        if (!TryGetTarget(out var hWnd, out var image)) return;
        if (image != null)
        {
            image.OpacityPercent += delta;
            Toast.Show($"Opacity {image.OpacityText}", seconds: 1.2);
            return;
        }

        int target = Math.Clamp(_manager.GetState(hWnd).Opacity + delta, 10, 100);
        var result = _manager.SetOpacity(hWnd, target);
        if (result.Ok) Toast.Show($"Opacity {target}%  {Shorten(Describe(hWnd), 26)}", seconds: 1.2);
        else Toast.Show(result.Error!, true);
        SyncWindow(hWnd);
    }

    private void HotkeyToggleClickThrough()
    {
        if (!TryGetTarget(out var hWnd, out var image)) return;
        if (image != null)
        {
            image.IsClickThrough = !image.IsClickThrough;
            Toast.Show(image.IsClickThrough
                ? $"Click-through on. {_settings.HotkeyClickThrough} over the image turns it off."
                : "Click-through off.");
            return;
        }

        bool enable = !_manager.GetState(hWnd).ClickThrough;
        var result = _manager.SetClickThrough(hWnd, enable);
        if (result.Ok)
            Toast.Show(enable
                ? $"Click-through on. {_settings.HotkeyClickThrough} over the window turns it off."
                : "Click-through off  " + Shorten(Describe(hWnd), 26));
        else Toast.Show(result.Error!, true);
        SyncWindow(hWnd);
    }

    // ---- hotkey editing ------------------------------------------------------------------

    private void Hotkey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        if (ReferenceEquals(_capturing, button))
        {
            CancelHotkeyCapture();
            return;
        }
        CancelHotkeyCapture();

        _capturing = button;
        button.Content = "Press keys…";
        _hotkeys?.UnregisterAll(); // otherwise pressing a combo that is already ours would fire it instead
    }

    private void Hotkey_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (ReferenceEquals(sender, _capturing)) CancelHotkeyCapture();
    }

    private void CancelHotkeyCapture()
    {
        if (_capturing == null) return;
        _capturing = null;
        UpdateHotkeyLabels();
        RegisterHotkeys(announceFailures: false);
    }

    private void CaptureHotkey(KeyEventArgs e)
    {
        e.Handled = true;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.ImeProcessed or Key.DeadCharProcessed)
            return;

        if (key == Key.Escape)
        {
            CancelHotkeyCapture();
            return;
        }

        var mods = Keyboard.Modifiers;
        bool standalone = key is Key.PrintScreen or (>= Key.F13 and <= Key.F24) or Key.Pause;
        if (!standalone && (mods == ModifierKeys.None || mods == ModifierKeys.Shift))
        {
            Toast.Show("Include Ctrl, Alt or Win so the hotkey can't fire while you type.");
            return;
        }

        var hotkey = new Hotkey(mods, key);
        string text = hotkey.ToString();
        string property = (string)_capturing!.Tag;

        var all = new Dictionary<string, string>
        {
            ["HotkeyPin"] = _settings.HotkeyPin,
            ["HotkeyOpacityUp"] = _settings.HotkeyOpacityUp,
            ["HotkeyOpacityDown"] = _settings.HotkeyOpacityDown,
            ["HotkeyClickThrough"] = _settings.HotkeyClickThrough,
            ["HotkeyDraw"] = _settings.HotkeyDraw,
            ["HotkeyPause"] = _settings.HotkeyPause,
            ["HotkeySnip"] = _settings.HotkeySnip,
            ["HotkeyRegion"] = _settings.HotkeyRegion,
            ["HotkeyWindow"] = _settings.HotkeyWindow,
            ["HotkeyScreen"] = _settings.HotkeyScreen,
            ["HotkeyText"] = _settings.HotkeyText,
            ["HotkeyColour"] = _settings.HotkeyColour,
            ["HotkeyGif"] = _settings.HotkeyGif,
            ["HotkeyRecent"] = _settings.HotkeyRecent,
            ["HotkeyPinClipboard"] = _settings.HotkeyPinClipboard,
            ["HotkeyVideo"] = _settings.HotkeyVideo,
            ["HotkeyRing"] = _settings.HotkeyRing,
            ["HotkeySearch"] = _settings.HotkeySearch,
            ["HotkeyReplay"] = _settings.HotkeyReplay,
        };
        if (all.Any(kv => kv.Key != property && kv.Value == text) || _settings.Workflows.Any(w => w.Hotkey == text))
        {
            Toast.Show($"{text} is already used by another Overskin hotkey.");
            return;
        }

        switch (property)
        {
            case "HotkeyPin": _settings.HotkeyPin = text; break;
            case "HotkeyOpacityUp": _settings.HotkeyOpacityUp = text; break;
            case "HotkeyOpacityDown": _settings.HotkeyOpacityDown = text; break;
            case "HotkeyClickThrough": _settings.HotkeyClickThrough = text; break;
            case "HotkeyDraw": _settings.HotkeyDraw = text; break;
            case "HotkeyPause": _settings.HotkeyPause = text; break;
            case "HotkeySnip": _settings.HotkeySnip = text; break;
            case "HotkeyRegion": _settings.HotkeyRegion = text; break;
            case "HotkeyWindow": _settings.HotkeyWindow = text; break;
            case "HotkeyScreen": _settings.HotkeyScreen = text; break;
            case "HotkeyText": _settings.HotkeyText = text; break;
            case "HotkeyColour": _settings.HotkeyColour = text; break;
            case "HotkeyGif": _settings.HotkeyGif = text; break;
            case "HotkeyRecent": _settings.HotkeyRecent = text; break;
            case "HotkeyPinClipboard": _settings.HotkeyPinClipboard = text; break;
            case "HotkeyVideo": _settings.HotkeyVideo = text; break;
            case "HotkeyRing": _settings.HotkeyRing = text; break;
            case "HotkeySearch": _settings.HotkeySearch = text; break;
            case "HotkeyReplay": _settings.HotkeyReplay = text; break;
        }

        _capturing = null;
        UpdateHotkeyLabels();
        RegisterHotkeys(announceFailures: true);
        SaveSettings();
    }

    // =====================================================================================
    //  input shared by tabs: paste and drag-and-drop
    // =====================================================================================

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_capturing != null)
        {
            CaptureHotkey(e);
            return;
        }

        if (e.Key == Key.Escape && FilterBox.IsKeyboardFocused && FilterBox.Text.Length > 0)
        {
            e.Handled = true;
            FilterBox.Text = "";
            return;
        }

        // Ctrl+K searches everything (Ctrl+F stays the Library's own search)
        if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            OpenSearch();
            return;
        }
        if (e.Key == Key.Escape && SettingsFilter.IsKeyboardFocused && SettingsFilter.Text.Length > 0)
        {
            e.Handled = true;
            SettingsFilter.Text = "";
            return;
        }

        // Ctrl+V pastes an image, unless the user is typing in the filter box
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && Keyboard.FocusedElement is not TextBox)
        {
            e.Handled = true;
            PasteImage();
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    /// <summary>
    /// Pins whatever picture is on the clipboard (a copied image, or a copied picture file),
    /// centred on the mouse, from anywhere: the hotkey, the tray, the jump list or the Pins page.
    /// </summary>
    private void PinClipboard()
    {
        BitmapSource? image;
        string? name;
        try { image = ImageLoader.FromClipboard(out name); }
        catch (Exception ex) { Toast.Show(ex.Message, true); return; }
        if (image == null)
        {
            Toast.Show("There's no picture on the clipboard. Copy one first (or a picture file).");
            return;
        }
        var at = CursorPos();
        var place = new Int32Rect(at.X - image.PixelWidth / 2, at.Y - image.PixelHeight / 2, 0, 0);
        PinImage(image, name ?? "Clipboard", null, placeAt: place);
    }

    private void PinClipboard_Click(object sender, RoutedEventArgs e) => PinClipboard();

    private void PasteImage()
    {
        try
        {
            var image = ImageLoader.FromClipboard(out string? name);
            if (image == null)
            {
                Toast.Show("There's no image on the clipboard.");
                return;
            }
            AcceptImage(image, name ?? "Pasted image");
        }
        catch (Exception ex)
        {
            Toast.Show(ex.Message, true);
        }
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    protected override void OnDrop(DragEventArgs e)
    {
        e.Handled = true;
        string? file = ImageLoader.FileFromDrop(e.Data);
        if (file == null) return;
        if (TryLoadFile(file, out var image)) AcceptImage(image!, System.IO.Path.GetFileName(file), file);
    }

    private static bool TryLoadFile(string path, out BitmapSource? image)
    {
        try
        {
            image = ImageLoader.LoadFile(path);
            return true;
        }
        catch (Exception ex)
        {
            image = null;
            Toast.Show(ex is InvalidOperationException ? ex.Message : $"Couldn't open {System.IO.Path.GetFileName(path)}.", true);
            return false;
        }
    }

    /// <summary>An image arrived by drop or paste: pin it.</summary>
    private void AcceptImage(BitmapSource image, string name, string? path = null) => PinImage(image, name, path);

    private static string? BrowseForImage(string title)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = ImageLoader.FileFilter };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    // =====================================================================================
    //  feature 2: pinned images
    // =====================================================================================

    private void BrowseImage_Click(object sender, RoutedEventArgs e)
    {
        string? file = BrowseForImage("Pin image");
        if (file != null && TryLoadFile(file, out var image))
            PinImage(image!, System.IO.Path.GetFileName(file), file);
    }

    /// <summary>Pasted and snipped pictures have no file, so a copy is kept here for "bring back on start".</summary>
    private static string ImageCacheFolder =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Overskin", "Pinned");

    private static string? CacheImage(BitmapSource image)
    {
        try
        {
            System.IO.Directory.CreateDirectory(ImageCacheFolder);
            string path = System.IO.Path.Combine(ImageCacheFolder, Guid.NewGuid().ToString("N") + ".png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var file = System.IO.File.Create(path);
            encoder.Save(file);
            return path;
        }
        catch
        {
            return null; // still pinned, just not brought back next time
        }
    }

    private static bool IsCacheFile(string? path) =>
        path != null && System.IO.Path.GetFullPath(path).StartsWith(ImageCacheFolder, StringComparison.OrdinalIgnoreCase);

    private void PinImage(BitmapSource image, string name, string? path = null,
        PinnedImageState? restore = null, Int32Rect? placeAt = null)
    {
        path ??= CacheImage(image);
        var window = new ImageOverlayWindow(image, name, _settings.ImageOpacity, _manager)
        {
            SourcePath = path,
            OwnsSourceFile = IsCacheFile(path),
            RestoreState = restore,
            PlaceAt = placeAt,
        };
        window.Closed += (_, _) =>
        {
            _images.Remove(window);
            // closed by the user, not by the app exiting: the cached copy is no longer needed
            if (!_cleaned && window.OwnsSourceFile && window.SourcePath != null)
            {
                try { System.IO.File.Delete(window.SourcePath); } catch { }
            }
        };
        window.OpacityChanged += percent => _settings.ImageOpacity = percent;
        window.PropertyChanged += (_, _) => UpdateStatus();
        window.ClickThroughEnabledFromMenu += () =>
            Toast.Show($"Click-through on. Press {_settings.HotkeyClickThrough} over the image to turn it off.");

        window.Show();
        if (restore == null && placeAt == null) window.Cascade(_images.Count);
        _images.Add(window);
    }

    /// <summary>Pins last session's pictures again, and clears out cache files nothing uses any more.</summary>
    private void RestorePinnedImages()
    {
        var states = _settings.RestoreImages ? _settings.PinnedImages.ToList() : new List<PinnedImageState>();
        foreach (var state in states)
        {
            if (!System.IO.File.Exists(state.Path)) continue;
            try
            {
                var image = ImageLoader.LoadFile(state.Path);
                PinImage(image, string.IsNullOrEmpty(state.Name) ? System.IO.Path.GetFileName(state.Path) : state.Name, state.Path, state);
            }
            catch
            {
                // the file changed into something unreadable; skip it quietly
            }
        }

        try
        {
            if (!System.IO.Directory.Exists(ImageCacheFolder)) return;
            var used = new HashSet<string>(_images.Select(i => i.SourcePath ?? ""), StringComparer.OrdinalIgnoreCase);
            foreach (string file in System.IO.Directory.GetFiles(ImageCacheFolder, "*.png"))
                if (!used.Contains(file)) System.IO.File.Delete(file);
        }
        catch
        {
        }
    }

    private void Snip_Click(object sender, RoutedEventArgs e) => SnipAndPin();

    private void ImageCopy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ImageOverlayWindow window }) window.CopyToClipboard();
    }

    private void ImageSave_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ImageOverlayWindow window }) window.SaveAs();
    }

    private void ImageClose_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ImageOverlayWindow window }) window.Close();
    }

    private void ImageReset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ImageOverlayWindow window }) window.ResetView();
    }

    private void MakeImagesClickable()
    {
        int count = 0;
        foreach (var image in _images.Where(i => i.IsClickThrough))
        {
            image.IsClickThrough = false;
            count++;
        }
        Toast.Show(count == 0 ? "No image is click-through." : count == 1 ? "1 image is clickable again." : $"{count} images are clickable again.");
    }

    /// <summary>
    /// While Ctrl+Alt is held, click-through images take the mouse again so they can be dragged,
    /// scrolled and right-clicked without first turning click-through off.
    /// </summary>
    private void UpdateGrab()
    {
        if (!_settings.GrabWithCtrlAlt) return;

        static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
        bool chord = Down(0x11) && Down(0x12);  // Ctrl and Alt
        _grabTicks = chord ? _grabTicks + 1 : 0;
        bool held = _grabTicks >= 4;            // ~200 ms, so a quick Ctrl+Alt hotkey press does not count
        bool dragging = Down(0x01);             // left button: do not drop an image mid-drag

        foreach (var image in _images)
        {
            if (!image.IsClickThrough) continue;
            if (held) image.SetGrabbed(true);
            else if (!dragging) image.SetGrabbed(false);
        }
    }

    // ---- draw while recording --------------------------------------------------------------

    /// <summary>The draw hotkey: while a video records, it turns the on-screen pen on and off.</summary>
    private void HotkeyStartDrawing()
    {
        if (_video?.IsRecording == true) _video.TogglePen();
        else Toast.Show($"{_settings.HotkeyDraw} draws on the screen while a video records.");
    }

#if DEBUG
    /// <summary>Developer aid: renders each tab to a PNG so layout can be checked without a screen.</summary>
    public async Task SnapshotTabsAsync(string folder, string? sampleImage, string prefix = "")
    {
        _exiting = true;
        System.IO.Directory.CreateDirectory(folder);
        if (sampleImage != null && _images.Count == 0 && TryLoadFile(sampleImage, out var bitmap))
        {
            PinImage(bitmap!, System.IO.Path.GetFileName(sampleImage));
            _images[0].Rotation = 20;
            _images[0].GridDivisions = 4;
            ImageList.SelectedIndex = 0;
            AddToHistory(sampleImage);
            AddToHistory(sampleImage.Replace("rich.png", "test.png"));
        }
        for (int tab = 0; tab < TabButtons.Length; tab++)
        {
            SelectTab(tab);
            if (tab == TabPinsIndex) PinsPictures.IsChecked = true;
            await Task.Delay(700);
            var visual = (FrameworkElement)Content;
            var target = new RenderTargetBitmap((int)(ActualWidth * 2), (int)(ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
            target.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(target));
            using var file = System.IO.File.Create(System.IO.Path.Combine(folder, $"{prefix}tab{tab}.png"));
            encoder.Save(file);
        }

        if (prefix.Length == 0)
        {
            await WriteTidyChecksAsync(folder);
            // the Recording half of the Capture page, top and scrolled down
            SelectTab(TabCaptureIndex);
            CaptureRecordTab.IsChecked = true;
            await Task.Delay(500);
            foreach (var (name, offset) in new[] { ("capture-record.png", 0.0), ("capture-record-2.png", 600.0) })
            {
                PageCapture.ScrollToVerticalOffset(offset);
                await Task.Delay(300);
                var shot = new RenderTargetBitmap((int)(ActualWidth * 2), (int)(ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
                shot.Render((FrameworkElement)Content);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(shot));
                using var f = System.IO.File.Create(System.IO.Path.Combine(folder, name));
                enc.Save(f);
            }
            CaptureShotsTab.IsChecked = true;

            // the watermark card (bottom of Screenshots) and the workflows card (Settings)
            async Task Shot(string name)
            {
                await Task.Delay(300);
                var shot = new RenderTargetBitmap((int)(ActualWidth * 2), (int)(ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
                shot.Render((FrameworkElement)Content);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(shot));
                using var f = System.IO.File.Create(System.IO.Path.Combine(folder, name));
                enc.Save(f);
            }
            _settings.Watermark.Text = "@overskin";
            SyncWatermarkControls();
            await Task.Delay(300);
            PageCapture.ScrollToEnd();
            await Shot("capture-watermark.png");
            PageCapture.ScrollToTop();
            // the Taking screenshots card, open
            if (!_folds[0].IsOpen) _folds[0].Toggle();
            await Task.Delay(400);
            PageCapture.ScrollToVerticalOffset(380);
            await Shot("capture-shooting.png");
            _folds[0].Toggle();
            PageCapture.ScrollToTop();
            if (_settings.Workflows.Count == 0)
            {
                _settings.Workflows.Add(new Workflow { Name = "Share safely", Hotkey = "Ctrl+Alt+1", AutoHide = true, Watermark = true, Save = true, Copy = true });
                RefreshWorkflowRows();
            }
            SelectTab(TabSettingsIndex);
            await Task.Delay(300);
            WorkflowRows.BringIntoView();
            await Shot("settings-workflows.png");
            SelectTab(TabCaptureIndex);

            // the capture bar, on its own (the real capture screen shows the user's screen, so never rendered)
            {
                var bar = new CaptureToolbar(CapturePick.Region, pointer: true);
                var host = new Border { Padding = new Thickness(20), Child = bar };
                host.SetResourceReference(Border.BackgroundProperty, "Bg");
                host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                host.Arrange(new Rect(host.DesiredSize));
                host.UpdateLayout();
                var shot = new RenderTargetBitmap((int)(host.ActualWidth * 2), (int)(host.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
                shot.Render(host);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(shot));
                using var f = System.IO.File.Create(System.IO.Path.Combine(folder, "capture-bar.png"));
                enc.Save(f);
            }

            // the recording pointers, big, on a grey checker so the edges and shadow show
            foreach (var (shape, dark) in new[] { (PointerArt.Shape.Arrow, false), (PointerArt.Shape.Arrow, true), (PointerArt.Shape.Text, false) })
            {
                var s = PointerArt.Render(shape, 96, dark);
                int bw = s.Width, bh = s.Height;
                var bg = new byte[bw * bh * 4];
                for (int y = 0; y < bh; y++)
                    for (int x = 0; x < bw; x++)
                    {
                        byte v = (byte)(((x / 8 + y / 8) & 1) == 0 ? 150 : 190);
                        int o = (y * bw + x) * 4;
                        bg[o] = v; bg[o + 1] = v; bg[o + 2] = v; bg[o + 3] = 255;
                    }
                PointerArt.Blend(bg, bw, bh, s, s.HotX, s.HotY);
                PointerArt.Ring(bg, bw, bh, s.HotX, s.HotY, 30, 4, 0.9);
                var bmp = BitmapSource.Create(bw, bh, 96, 96, PixelFormats.Bgra32, null, bg, bw * 4);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                using var f = System.IO.File.Create(System.IO.Path.Combine(folder, $"pointer-{shape}-{(dark ? "black" : "white")}.png"));
                enc.Save(f);
            }
        }

        if (prefix.Length == 0 && _images.Count > 0)
        {
            // the pinned image itself, rotated, as it appears on screen
            var image = _images[0];
            var content = (FrameworkElement)image.Content;
            var shot = new RenderTargetBitmap((int)(image.ActualWidth * 2), (int)(image.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
            shot.Render(content);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(shot));
            using var overlayFile = System.IO.File.Create(System.IO.Path.Combine(folder, "overlay.png"));
            png.Save(overlayFile);


            // the colour wheel popup
            SelectTab(TabSettingsIndex);
            await Task.Delay(300);
            WheelSwatch_Click(this, new RoutedEventArgs());
            await Task.Delay(500);
            if (WheelPopup.Child is FrameworkElement popup)
            {
                var wheelShot = new RenderTargetBitmap((int)(popup.ActualWidth * 2), (int)(popup.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
                wheelShot.Render(popup);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(wheelShot));
                using var f = System.IO.File.Create(System.IO.Path.Combine(folder, "wheel.png"));
                enc.Save(f);
            }
            WheelPopup.IsOpen = false;

            // the editor with a few marks, and the after-capture card
            if (sampleImage != null && TryLoadFile(sampleImage, out var shotImg))
            {
                var ed = new EditorWindow(shotImg!, sampleImage, "Overskin 2026-11-16 14.03.22.png", new EditorHost { SaveNew = _ => null, Pin = (_, _) => { } });
                ed.Show();
                await Task.Delay(400);
                // marks placed for the sunset picture in docs: a box round the sun, a note, an arrow to the cabin
                ed.AddForTest(new Annotation { Kind = AnnotationKind.Rectangle, Points = { new Point(670, 380), new Point(850, 560) }, Color = Color.FromRgb(0xEF, 0x44, 0x44), Size = 5 });
                ed.AddForTest(new Annotation { Kind = AnnotationKind.Text, Points = { new Point(660, 300) }, Color = Color.FromRgb(0xFF, 0xFF, 0xFF), Size = 6, Text = "golden hour" });
                ed.AddForTest(new Annotation { Kind = AnnotationKind.Arrow, Points = { new Point(440, 790), new Point(330, 650) }, Color = Color.FromRgb(0xEF, 0x44, 0x44), Size = 5 });
                ed.AddForTest(new Annotation { Kind = AnnotationKind.Text, Points = { new Point(370, 800) }, Color = Color.FromRgb(0xFF, 0xFF, 0xFF), Size = 5, Text = "the cabin" });
                await Task.Delay(500);
                var edShot = new RenderTargetBitmap((int)(ed.ActualWidth * 1.5), (int)(ed.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
                edShot.Render((FrameworkElement)ed.Content);
                var edEnc = new PngBitmapEncoder();
                edEnc.Frames.Add(BitmapFrame.Create(edShot));
                using (var f = System.IO.File.Create(System.IO.Path.Combine(folder, "editor.png"))) edEnc.Save(f);
                ed.Close();

                CaptureNotice.ShowFor(shotImg!, null, "1200 × 900  ·  Saved and copied", () => { }, () => { }, () => { }, () => { });
                await Task.Delay(600);
                var notice = Application.Current.Windows.OfType<CaptureNotice>().FirstOrDefault();
                if (notice != null)
                {
                    var nShot = new RenderTargetBitmap((int)(notice.ActualWidth * 2), (int)(notice.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
                    nShot.Render((FrameworkElement)notice.Content);
                    var nEnc = new PngBitmapEncoder();
                    nEnc.Frames.Add(BitmapFrame.Create(nShot));
                    using (var f = System.IO.File.Create(System.IO.Path.Combine(folder, "notice.png"))) nEnc.Save(f);
                    notice.Close();
                }
            }

            // the GIF editor with a caption
            if (sampleImage != null && TryLoadFile(sampleImage, out var gifImg))
            {
                var gclip = GifClip.FromImage(gifImg!);
                gclip.Frames.Add(gclip.Frames[0]);
                gclip.DelaysMs.Add(500);
                var ge = new GifEditorWindow(gclip, "Recording 14.03.22  ·  2 frames", new GifEditorHost { Folder = folder, Saved = _ => { } });
                ge.Show();
                await Task.Delay(500);
                ge.TopBox.Text = "me logging off for the weekend";
                await Task.Delay(400);
                var geShot = new RenderTargetBitmap((int)(ge.ActualWidth * 1.5), (int)(ge.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
                geShot.Render((FrameworkElement)ge.Content);
                var geEnc = new PngBitmapEncoder();
                geEnc.Frames.Add(BitmapFrame.Create(geShot));
                using (var f = System.IO.File.Create(System.IO.Path.Combine(folder, "gif-editor.png"))) geEnc.Save(f);
                ge.SettingsScroll.ScrollToEnd();
                await Task.Delay(400);
                var geShot2 = new RenderTargetBitmap((int)(ge.ActualWidth * 1.5), (int)(ge.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
                geShot2.Render((FrameworkElement)ge.Content);
                var geEnc2 = new PngBitmapEncoder();
                geEnc2.Frames.Add(BitmapFrame.Create(geShot2));
                using (var f = System.IO.File.Create(System.IO.Path.Combine(folder, "gif-editor-2.png"))) geEnc2.Save(f);
                ge.SettingsScroll.ScrollToTop();
                ge.CropButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                await Task.Delay(300);
                ge.SetCropForTest(new Rect(gclip.Width * 0.15, gclip.Height * 0.2, gclip.Width * 0.6, gclip.Height * 0.55));
                await Task.Delay(300);
                var geShot3 = new RenderTargetBitmap((int)(ge.ActualWidth * 1.5), (int)(ge.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
                geShot3.Render((FrameworkElement)ge.Content);
                var geEnc3 = new PngBitmapEncoder();
                geEnc3.Frames.Add(BitmapFrame.Create(geShot3));
                using (var f = System.IO.File.Create(System.IO.Path.Combine(folder, "gif-crop.png"))) geEnc3.Save(f);
                ge.Close();
            }

            // the settings page at a bigger size
            _settings.UiScale = 115;
            ApplyScale();
            SelectTab(TabSettingsIndex);
            await Task.Delay(500);
            var big = new RenderTargetBitmap((int)(ActualWidth * 2), (int)(ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
            big.Render((FrameworkElement)Content);
            var bigEnc = new PngBitmapEncoder();
            bigEnc.Frames.Add(BitmapFrame.Create(big));
            using (var bigFile = System.IO.File.Create(System.IO.Path.Combine(folder, "settings-115.png"))) bigEnc.Save(bigFile);
            _settings.UiScale = 100;
            ApplyScale();

            // second pass in other colours, to check that theming reaches everything
            string accent = _settings.AccentColor, tone = _settings.BackgroundTone;
            _settings.AccentColor = "#48C288";
            _settings.BackgroundTone = "Black";
            ApplyTheme();
            await SnapshotTabsAsync(folder, null, "alt-");
            // put the colours back, so the next snapshots (the README's) are in the usual blue
            _settings.AccentColor = accent;
            _settings.BackgroundTone = tone;
            ApplyTheme();
            SaveSettings();
        }
    }
#endif
}
