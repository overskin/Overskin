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
    private const int HkAbort = 5, HkAbortEsc = 6, HkPickEsc = 7, HkDraw = 8, HkPause = 9, HkPaletteEsc = 10, HkSnip = 11;
    private const int HkRegion = 12, HkWindow = 13, HkScreen = 14, HkText = 15, HkColour = 16, HkRecent = 21;
    private const double BaseWidth = 540, BaseHeight = 700, SmallestWidth = 530, SmallestHeight = 560;
    private const int TabCaptureIndex = 0, TabLibraryIndex = 1, TabPinsIndex = 2, TabTraceIndex = 3, TabSettingsIndex = 4;

    private readonly bool _loading = true;
    private readonly bool _ready;

    private AppSettings _settings = AppSettings.Load();
    private readonly WindowManager _manager = new();
    private readonly MouseDrawer _drawer = new();
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

    // auto-trace state
    private readonly DispatcherTimer _traceDebounce = new() { Interval = TimeSpan.FromMilliseconds(90) };
    private TraceSource? _traceImage;
    private BitmapSource? _traceSource;
    private TraceMode _mode = TraceMode.Outline;
    private PaletteCapture? _paletteCapture;
    private TraceResult? _trace;
    private CancellationTokenSource? _traceCts;
    private Int32Rect? _area;
    private GhostWindow? _ghost;
    private bool _ghostSuppressed;
    private bool _minimizedForDrawing;
    private string _lastHud = "";

    // polls the grab keys while any image is pinned
    private readonly DispatcherTimer _grabTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private int _grabTicks;

    public MainWindow()
    {
        Theming.Apply(_settings.AccentColor, _settings.BackgroundTone); // before any control looks up its colours
        InitializeComponent();
        AutoHideRow.ContextMenu = Redaction.KindsMenu();
        Redaction.KindsChanged += () => { _settings.AutoHideKinds = Redaction.Kinds; _settings.Save(); };

        WindowList.ItemsSource = _windows;
        System.Windows.Data.CollectionViewSource.GetDefaultView(_windows).Filter = MatchesFilter;
        _grabTimer.Tick += (_, _) => UpdateGrab();
        ImageList.ItemsSource = _images;
        LibraryList.ItemsSource = _library;
        CatalogList.ItemsSource = _catalog;
        _images.CollectionChanged += (_, _) =>
        {
            UpdateStatus();
            if (_images.Count > 0) _grabTimer.Start(); else _grabTimer.Stop();
        };
        _traceDebounce.Tick += (_, _) => { _traceDebounce.Stop(); RunTrace(); };

        _drawer.Progress += p => Dispatcher.BeginInvoke(() => OnDrawProgress(p));
        _drawer.Finished += (outcome, message) => Dispatcher.BeginInvoke(() => OnDrawFinished(outcome, message));

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
        RefreshCatalog();
        Loaded += (_, _) => FadeIn();

        _loading = false;
        _ready = true;
        SelectTab(_settings.LastTab);
        UpdateTraceLabels();
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

        _paletteCapture = new PaletteCapture(Dispatcher, _manager.IsOwnWindow);
        _paletteCapture.Picked += OnPaletteColourPicked;
        _paletteCapture.Ended += OnPaletteCaptureEnded;

        RegisterHotkeys(announceFailures: true);
        RefreshWindows();
        RestorePinnedImages();
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
            _ghost?.Hide();
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
        UpdateGhost();
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

        Try(() => _drawer.Shutdown());       // releases the mouse button
        Try(() => _search.Save());
        Try(() => _manager.RestoreAll());    // puts other programs' windows back
        Try(() => _picker?.Dispose());
        Try(() => _paletteCapture?.Dispose());
        Try(() => _tray?.Dispose());
        Try(() => _hotkeys?.Dispose());
        Try(() => _traceCts?.Cancel());
        Try(SaveSettings);
        Try(() =>
        {
            foreach (var image in _images.ToList()) image.Close();
            _ghost?.Close();
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
        ThresholdSlider.Value = _settings.Threshold;
        ScaleSlider.Value = _settings.ScalePercent;
        SpacingSlider.Value = _settings.FillSpacing;
        InvertSwitch.IsChecked = _settings.Invert;
        SelectMode(_settings.TraceMode);
        BuildPaletteChips();
        foreach (RadioButton chip in SpeedChoices.Children) chip.IsChecked = (string)chip.Tag == _settings.TraceSpeed;
        foreach (RadioButton chip in CountdownChoices.Children) chip.IsChecked = (string)chip.Tag == _settings.CountdownSeconds.ToString();
        UpdateSpeedHint();
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
        _settings.Threshold = (int)ThresholdSlider.Value;
        _settings.ScalePercent = (int)ScaleSlider.Value;
        _settings.FillSpacing = (int)SpacingSlider.Value;
        _settings.Invert = InvertSwitch.IsChecked == true;
        _settings.TraceMode = _mode.ToString();
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
        HkAbortButton.Content = _settings.HotkeyAbort;
        HkDrawButton.Content = _settings.HotkeyDraw;
        HkPauseButton.Content = _settings.HotkeyPause;
        HkSnipButton.Content = _settings.HotkeySnip;
        HintSnipKey.Text = _settings.HotkeySnip;
        HkRegionButton.Content = _settings.HotkeyRegion;
        HkWindowButton.Content = _settings.HotkeyWindow;
        HkScreenButton.Content = _settings.HotkeyScreen;
        HkTextButton.Content = _settings.HotkeyText;
        HkColourButton.Content = _settings.HotkeyColour;
        HkGifButton.Content = _settings.HotkeyGif;
        HkRecentButton.Content = _settings.HotkeyRecent;
        TileGifKey.Text = _settings.HotkeyGif;
        TileRegionKey.Text = _settings.HotkeyRegion;
        TileWindowKey.Text = _settings.HotkeyWindow;
        TileScreenKey.Text = _settings.HotkeyScreen;
        ToolSnipButton.ToolTip = _settings.HotkeySnip;
        ToolTextButton.ToolTip = _settings.HotkeyText;
        ToolColourButton.ToolTip = _settings.HotkeyColour;
        HintClickKey.Text = _settings.HotkeyClickThrough;
        HintGrabKey.Visibility = HintGrabText.Visibility = _settings.GrabWithCtrlAlt ? Visibility.Visible : Visibility.Collapsed;
        SafetyText.Text = $"Stop any time with Esc or {_settings.HotkeyAbort}, or by pushing the mouse into the top-left corner. " +
                          $"{_settings.HotkeyPause} pauses. {_settings.HotkeyDraw} starts drawing from inside the game, no need to switch back.";
    }

    private void StartupSwitch_Click(object sender, RoutedEventArgs e)
    {
        bool want = StartupSwitch.IsChecked == true;
        if (!StartupRegistration.Set(want))
        {
            StartupSwitch.IsChecked = !want;
            Toast.Show("Windows didn't allow changing the startup setting.", true);
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
        _syncingAppearance = false;
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
        Theming.Apply(_settings.AccentColor, _settings.BackgroundTone); // live, without saving on every move
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
        Theming.Apply(_settings.AccentColor, _settings.BackgroundTone);
        SyncAppearanceControls();
        ApplyCaptureSettingsToControls();
        _ghost?.Close(); // it holds the old accent brush; rebuilt on demand
        _ghost = null;
        UpdateStatus();
        UpdateGhost();
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
            Palette = _settings.Palette,
            RestoreImages = _settings.RestoreImages,
            PinnedImages = _settings.PinnedImages,
            History = _settings.History,
            SaveFolder = _settings.SaveFolder,
            LastRegion = _settings.LastRegion,
        };
        _settings = fresh;
        Theming.Apply(_settings.AccentColor, _settings.BackgroundTone);
        _ghost?.Close();
        _ghost = null;
        ApplySettingsToControls();
        RegisterHotkeys(announceFailures: true);
        UpdateTraceLabels();
        ScheduleTrace();
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

    private RadioButton[] TabButtons => new[] { TabCapture, TabLibrary, TabPins, TabTrace, TabSettings };
    private FrameworkElement[] Pages => new FrameworkElement[] { PageCapture, PageLibrary, PagePins, PageTrace, PageSettings };

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
        UpdateGhost();
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
        Bind(HkWindow, _settings.HotkeyWindow, CaptureActiveWindow);
        Bind(HkScreen, _settings.HotkeyScreen, () => CaptureFullScreen());
        Bind(HkText, _settings.HotkeyText, TextFromScreen);
        Bind(HkColour, _settings.HotkeyColour, PickScreenColour);
        Bind(HkGif, _settings.HotkeyGif, ToggleGifRecording);
        Bind(HkRecent, _settings.HotkeyRecent, ShowRecent);

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
            ["HotkeyAbort"] = _settings.HotkeyAbort,
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
        };
        if (all.Any(kv => kv.Key != property && kv.Value == text))
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
            case "HotkeyAbort": _settings.HotkeyAbort = text; break;
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

        // Ctrl+V pastes an image, unless the user is typing in the filter box
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && Keyboard.FocusedElement is not TextBox)
        {
            e.Handled = true;
            PasteImage();
            return;
        }
        base.OnPreviewKeyDown(e);
    }

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

    /// <summary>An image arrived by drop or paste: trace it on the trace tab, pin it everywhere else.</summary>
    private void AcceptImage(BitmapSource image, string name, string? path = null)
    {
        if (_tab == TabTraceIndex) LoadTraceImage(image);
        else PinImage(image, name, path);
    }

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
        window.SendToTraceRequested += w =>
        {
            SelectTab(TabTraceIndex);
            LoadTraceImage(w.Original);
            ShowFromTray();
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

    private void ImageTrace_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ImageOverlayWindow window }) window.RequestTrace();
    }

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

    // =====================================================================================
    //  feature 3: auto-trace
    // =====================================================================================

    private static readonly Dictionary<TraceMode, string> ModeHints = new()
    {
        [TraceMode.Outline] = "Traces around the edge of every dark shape. Clean, and quick to draw.",
        [TraceMode.Lines] = "One stroke along the middle of each line. Best for line art, doodles and text.",
        [TraceMode.Sketch] = "Finds edges by contrast instead of darkness, so it works on photos and colourful pictures.",
        [TraceMode.Hatch] = "Fills dark areas with parallel strokes. Set the spacing to your brush size for solid fills.",
        [TraceMode.Shade] = "Hatching that builds up in darker tones, for a shaded pencil look.",
        [TraceMode.Colour] = "Paints each area in the closest colour from the game's palette, switching colours as it goes.",
    };

    private void LoadTrace_Click(object sender, RoutedEventArgs e)
    {
        string? file = BrowseForImage("Image to trace");
        if (file != null && TryLoadFile(file, out var image)) LoadTraceImage(image!);
    }

    private void LoadTraceImage(BitmapSource image)
    {
        _currentCatalogPath = TraceCatalog.Add(image);
        RefreshCatalog();
        _traceSource = image;
        _traceImage = TraceEngine.Prepare(image);
        TraceEmpty.Visibility = Visibility.Collapsed;
        TraceStats.Text = "Tracing…";
        _ghostSuppressed = false;
        _traceDebounce.Stop();
        RunTrace();
    }

    private void TraceParam_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || !_ready) return;
        UpdateTraceLabels();
        ScheduleTrace();
    }

    private void TraceSwitch_Click(object sender, RoutedEventArgs e) => ScheduleTrace();

    // ---- style ---------------------------------------------------------------------------

    private void SelectMode(string name)
    {
        if (!Enum.TryParse(name, out TraceMode mode)) mode = TraceMode.Outline;
        _mode = mode;
        foreach (RadioButton chip in ModeChoices.Children)
        {
            chip.IsChecked = (string)chip.Tag == mode.ToString();
            if (Enum.TryParse((string)chip.Tag, out TraceMode m)) chip.ToolTip = ModeHints[m];
        }
        UpdateModeUi();
    }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag } || !Enum.TryParse(tag, out TraceMode mode)) return;
        _mode = mode;
        UpdateModeUi();
        if (_loading || !_ready) return;
        ScheduleTrace();
    }

    /// <summary>Shows only the settings that mean something for the chosen style.</summary>
    private void UpdateModeUi()
    {
        static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
        bool fills = _mode is TraceMode.Hatch or TraceMode.Shade or TraceMode.Colour;

        ThresholdRow.Visibility = Show(_mode != TraceMode.Colour);
        ThresholdLabel.Text = _mode == TraceMode.Sketch ? "Sensitivity" : "Threshold";
        ThresholdRow.ToolTip = _mode == TraceMode.Sketch
            ? "Higher finds fainter edges"
            : "Pixels darker than this are drawn";
        AutoThresholdButton.Visibility = Show(_mode != TraceMode.Sketch);
        bool bucketMode = _mode is TraceMode.Outline or TraceMode.Colour;
        BucketRow.Visibility = Show(bucketMode);
        // with the bucket, Outline needs to know the brush size too, to keep clicks off the outline
        SpacingRow.Visibility = Show(fills || (bucketMode && _settings.BucketFill));
        // with the bucket on, the slider is the game's brush size: outlines and clicks are planned around it
        SpacingLabel.Text = _mode == TraceMode.Outline || (bucketMode && _settings.BucketFill) ? "Brush size" : "Line spacing";
        UpdateToolsLabel();
        InvertRow.Visibility = Show(_mode is not (TraceMode.Sketch or TraceMode.Colour));
        PaletteCard.Visibility = Show(_mode == TraceMode.Colour);
        UpdatePaletteHint();
    }

    private void AutoThreshold_Click(object sender, RoutedEventArgs e)
    {
        if (_traceImage == null)
        {
            Toast.Show("Load an image first.");
            return;
        }
        int t = TraceEngine.AutoThreshold(_traceImage);
        // shading reads the threshold as the lightest tone, so leave it room above the split
        if (_mode == TraceMode.Shade) t = Math.Min(250, t + (255 - t) / 2);
        ThresholdSlider.Value = t;
    }

    // ---- speed ---------------------------------------------------------------------------

    /// <summary>Refresh rate of the monitor that will be drawn on (the chosen area, or the one the mouse is on).</summary>
    private int DrawingRefreshRate()
    {
        var pt = _area is { } a ? new POINT(a.X + a.Width / 2, a.Y + a.Height / 2) : CursorPos();
        return RefreshRate(pt);
    }

    /// <summary>One screen refresh, plus a millisecond of slack for timer jitter.</summary>
    private int FrameMs() => (int)Math.Ceiling(1000.0 / DrawingRefreshRate()) + 1;

    private (int Delay, int PointsPerStep) SpeedTiming() => _settings.TraceSpeed switch
    {
        "Careful" => (FrameMs() * 2, 1),
        "Fast" => (1, 1),
        "Instant" => (0, 8),
        _ => (FrameMs(), 1),
    };

    private void Speed_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag }) return;
        _settings.TraceSpeed = tag;
        UpdateSpeedHint();
        if (!_ready) return;
        if (_settings.BucketFill) ScheduleTrace(); // the quicker way to cover each area depends on speed
        UpdateEstimate();
    }

    private void Countdown_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } && int.TryParse(tag, out int s)) _settings.CountdownSeconds = s;
    }

    private void UpdateSpeedHint()
    {
        int hz = DrawingRefreshRate(), frame = FrameMs();
        SpeedHint.Text = _settings.TraceSpeed switch
        {
            "Careful" => $"Two screen refreshes per point ({frame * 2} ms). For laggy games, a busy PC, or when lines come out wobbly.",
            "Fast" => "For installed programs like Paint, Krita or Photoshop. Too quick for browser games: corners get cut.",
            "Instant" => "As fast as Windows allows. Installed programs only, and some may skip points.",
            _ => $"One point per screen refresh ({hz} Hz, every {frame} ms): the fastest a browser game like skribbl or Gartic can follow. Works everywhere.",
        };
    }

    // ---- labels and estimate -------------------------------------------------------------

    private void UpdateTraceLabels()
    {
        ThresholdText.Text = ((int)ThresholdSlider.Value).ToString();
        ScaleText.Text = (int)ScaleSlider.Value + "%";
        SpacingText.Text = (int)SpacingSlider.Value + " px";
        ResolutionRow.ToolTip = _area != null
            ? "100% traces at the size of the chosen area: one pixel per screen pixel. Lower is faster, higher is finer."
            : "Relative to the picture's own size. Once you set an area, 100% matches the screen.";
        UpdateEstimate();
    }

    private DrawSettings CurrentDrawSettings(int? countdown = null)
    {
        var (delay, perStep) = SpeedTiming();
        return new DrawSettings
        {
            DelayMs = delay,
            PointsPerStep = perStep,
            // a swatch or tool click only needs a few refreshes to register; Careful stays generous
            SelectPauseMs = _settings.TraceSpeed == "Careful" ? 140 : Math.Max(40, 3 * FrameMs()),
            CountdownSeconds = countdown ?? _settings.CountdownSeconds,
            StopOnMouseMove = true, // grabbing the mouse always stops a drawing
        };
    }

    private static int ColourSwitches(TraceResult trace)
    {
        if (trace.ColourIndex == null) return 0;
        int switches = 0, last = -1;
        foreach (int k in trace.ColourIndex)
        {
            if (k != last) switches++;
            last = k;
        }
        return switches;
    }

    private void UpdateEstimate()
    {
        if (_trace == null || _trace.Paths.Count == 0)
        {
            EstimateText.Text = "";
            return;
        }
        var estimate = MouseDrawer.Estimate(_trace.Paths.Count - _trace.FillCount, _trace.PointCount - _trace.FillCount,
            ColourSwitches(_trace) + (_trace.FillCount > 0 ? 2 : 0), CurrentDrawSettings(), _trace.FillCount);
        EstimateText.Text = "about " + FormatTime(estimate);
    }

    private static string FormatTime(TimeSpan t)
    {
        if (t.TotalSeconds < 1) return "1 s";
        if (t.TotalMinutes < 1) return $"{(int)Math.Ceiling(t.TotalSeconds)} s";
        if (t.TotalHours < 1) return $"{(int)t.TotalMinutes} min {t.Seconds:00} s";
        return $"{(int)t.TotalHours} h {t.Minutes:00} min";
    }

    // ---- tracing -------------------------------------------------------------------------

    private void ScheduleTrace()
    {
        if (_traceImage == null) return;
        _traceDebounce.Stop();
        _traceDebounce.Start();
    }

    /// <summary>With an area chosen, 100% detail means one traced pixel per screen pixel there.</summary>
    private double BaseScale()
    {
        if (_area is not { } area || _traceImage == null) return 1.0;
        var fit = Placement.Fit(area, _traceImage.Width, _traceImage.Height);
        return fit.Width / (double)_traceImage.Width;
    }

    private TraceOptions CurrentOptions() => new()
    {
        Mode = _mode,
        Threshold = (int)ThresholdSlider.Value,
        Invert = InvertSwitch.IsChecked == true,
        Blur = 1,
        Scale = BaseScale() * ScaleSlider.Value / 100.0,
        MinPathLength = 8,
        Tolerance = 1.0,
        Spacing = SpacingSlider.Value,
        Angle = _mode == TraceMode.Hatch ? 45 : 0,
        Crosshatch = false,
        Levels = 3,
        OutlineToo = true,
        Palette = _settings.Palette.Select(p => (p.Color, p.Skip)).ToList(),
        BucketFill = _settings.BucketFill && _mode is TraceMode.Outline or TraceMode.Colour,
        PointMs = SpeedTiming() is var (d, per) ? Math.Max(0.5, d / (double)per) : 8,
    };

    private async void RunTrace()
    {
        if (_traceImage == null) return;

        _traceCts?.Cancel();
        var cts = _traceCts = new CancellationTokenSource();
        var image = _traceImage;
        var options = CurrentOptions();

        try
        {
            var result = await Task.Run(() => TraceEngine.Trace(image, options, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return; // a newer run has superseded this one

            _trace = result;
            Preview.Set(result, showBackdrop: true);

            if (result.Paths.Count == 0)
            {
                TraceStats.Text = result.Message ?? "Nothing to draw.";
            }
            else
            {
                string strokes = result.Paths.Count == 1 ? "1 stroke" : $"{result.Paths.Count:N0} strokes";
                string colours = result.ColourIndex is { } idx ? $"  ·  {idx.Distinct().Count()} colours" : "";
                string fills = result.FillCount > 0 ? $"  ·  {result.FillCount:N0} fills" : "";
                int lines = result.Paths.Count - result.FillCount;
                strokes = lines == 1 ? "1 stroke" : $"{lines:N0} strokes";
                TraceStats.Text = $"{strokes}{fills}  ·  {result.PointCount - result.FillCount:N0} points{colours}  ·  {result.Width} × {result.Height}";
            }
            _ghostSuppressed = false;
            UpdateEstimate();
            UpdateGhost();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            TraceStats.Text = "Couldn't trace this image: " + ex.Message;
        }
    }

    // ---- catalog -------------------------------------------------------------------------

    private readonly System.Collections.ObjectModel.ObservableCollection<CatalogItem> _catalog = new();
    private string? _currentCatalogPath;

    private void RefreshCatalog()
    {
        var known = _catalog.ToDictionary(c => c.Path, StringComparer.OrdinalIgnoreCase);
        _catalog.Clear();
        foreach (var item in TraceCatalog.Load())
        {
            var shown = known.TryGetValue(item.Path, out var old) ? old : item;
            shown.IsCurrent = string.Equals(shown.Path, _currentCatalogPath, StringComparison.OrdinalIgnoreCase);
            _catalog.Add(shown);
            if (shown.Thumb == null) _ = TraceCatalog.LoadThumbAsync(shown);
        }
        CatalogCount.Text = _catalog.Count == 0 ? "" : _catalog.Count.ToString();
        CatalogEmpty.Visibility = _catalog.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CatalogScroll.Visibility = _catalog.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static CatalogItem? CatalogOf(object sender) => (sender as FrameworkElement)?.DataContext as CatalogItem;

    private void CatalogOpen_Click(object sender, RoutedEventArgs e)
    {
        if (CatalogOf(sender) is not { } item) return;
        if (TryLoadFile(item.Path, out var image)) LoadTraceImage(image!);
        else RefreshCatalog();
    }

    private void CatalogPin_Click(object sender, RoutedEventArgs e)
    {
        if (CatalogOf(sender) is { } item && TryLoadFile(item.Path, out var image))
            PinImage(image!, "Catalog picture", item.Path);
    }

    private void CatalogRemove_Click(object sender, RoutedEventArgs e)
    {
        if (CatalogOf(sender) is not { } item) return;
        TraceCatalog.Remove(item.Path);
        RefreshCatalog();
    }

    private void CatalogAdd_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Add pictures to the catalog", Filter = ImageLoader.FileFilter, Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        int added = 0;
        foreach (string file in dialog.FileNames)
        {
            try
            {
                if (TraceCatalog.Add(ImageLoader.LoadFile(file)) != null) added++;
            }
            catch
            {
                // skip files that aren't pictures
            }
        }
        RefreshCatalog();
        Toast.Show(added == 1 ? "1 picture added." : $"{added} pictures added.");
    }

    /// <summary>The strip scrolls sideways with the ordinary mouse wheel.</summary>
    private void CatalogScroll_Wheel(object sender, MouseWheelEventArgs e)
    {
        if (CatalogScroll.ScrollableWidth <= 0) return; // nothing to scroll: let the page scroll instead
        CatalogScroll.ScrollToHorizontalOffset(CatalogScroll.HorizontalOffset - e.Delta * 0.6);
        e.Handled = true;
    }

    // ---- game colours --------------------------------------------------------------------

    private void BuildPaletteChips()
    {
        PaletteSwatches.Children.Clear();
        foreach (var swatch in _settings.Palette)
        {
            var s = swatch;
            var brush = new SolidColorBrush(s.Color);
            brush.Freeze();
            var chip = new System.Windows.Controls.Primitives.ToggleButton
            {
                Style = (Style)FindResource("PaletteChip"),
                Background = brush,
                IsChecked = !s.Skip,
                ToolTip = PaletteTip(s),
            };
            chip.Click += (_, _) =>
            {
                s.Skip = chip.IsChecked != true;
                chip.ToolTip = PaletteTip(s);
                ScheduleTrace();
            };
            chip.MouseRightButtonUp += (_, a) =>
            {
                a.Handled = true;
                _settings.Palette.Remove(s);
                BuildPaletteChips();
                ScheduleTrace();
            };
            PaletteSwatches.Children.Add(chip);
        }

        int count = _settings.Palette.Count;
        PaletteCount.Text = count == 0 ? "" : count.ToString();
        PaletteSwatches.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdatePaletteHint();
    }

    private static string PaletteTip(PaletteSwatch s) =>
        s.Skip ? $"{s.Hex}, left blank. Click to use it." : $"{s.Hex}. Click to leave it blank, right-click to remove.";

    private void UpdatePaletteHint()
    {
        if (_paletteCapture?.IsActive == true)
            PaletteHint.Text = "Click each colour in the game's palette. Right-click or press Esc when you're done.";
        else if (_settings.Palette.Count == 0)
            PaletteHint.Text = "Open the game, click Pick from screen, then click every colour you want. Your clicks won't reach the game.";
        else
            PaletteHint.Text = "Crossed-out colours are left blank, like the canvas white. Right-click one to remove it. Pick again if the game window moves.";
    }

    private void PickColours_Click(object sender, RoutedEventArgs e)
    {
        if (_paletteCapture == null) return;
        if (_paletteCapture.IsActive)
        {
            _paletteCapture.Stop();
            return;
        }
        if (!_paletteCapture.Start())
        {
            Toast.Show("Couldn't start colour picking.", true);
            return;
        }
        PickColoursLabel.Text = "Done";
        _hotkeys?.Register(HkPaletteEsc, new Hotkey(ModifierKeys.None, Key.Escape), () => _paletteCapture.Stop());
        UpdatePaletteHint();
        WindowState = WindowState.Minimized; // out of the way of the game's palette; it comes back when done
        Toast.ShowSticky("Click each colour in the game's palette. Right-click or Esc to finish.");
    }

    // ---- brush and bucket ------------------------------------------------------------------

    /// <summary>-1 when not picking tools; 0 waiting for the brush, 1 waiting for the bucket.</summary>
    private int _toolPickStep = -1;

    private void BucketSwitch_Click(object sender, RoutedEventArgs e)
    {
        _settings.BucketFill = BucketSwitch.IsChecked == true;
        SaveSettings();
        UpdateModeUi();
        ScheduleTrace();
        if (_settings.BucketFill && (_settings.BrushTool == null || _settings.FillTool == null))
            PickTools_Click(sender, e);
    }

    private void UpdateToolsLabel()
    {
        BucketSwitch.IsChecked = _settings.BucketFill;
        bool picked = _settings.BrushTool != null && _settings.FillTool != null;
        PickToolsLabel.Text = _toolPickStep >= 0 ? "Done" : picked ? "Brush and bucket set" : "Pick brush and bucket";
        PickToolsButton.Visibility = _settings.BucketFill ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PickTools_Click(object sender, RoutedEventArgs e)
    {
        if (_paletteCapture == null) return;
        if (_paletteCapture.IsActive)
        {
            _paletteCapture.Stop();
            return;
        }
        if (!_paletteCapture.Start())
        {
            Toast.Show("Couldn't start picking.", true);
            return;
        }
        _toolPickStep = 0;
        UpdateToolsLabel();
        _hotkeys?.Register(HkPaletteEsc, new Hotkey(ModifierKeys.None, Key.Escape), () => _paletteCapture.Stop());
        WindowState = WindowState.Minimized;
        Toast.ShowSticky("Click the game's brush (pen) button.  Esc cancels.");
    }

    private void OnPaletteColourPicked(int x, int y, Color colour)
    {
        if (_toolPickStep >= 0)
        {
            var spot = new PaletteSwatch { X = x, Y = y, Hex = Theming.ToHex(colour) };
            if (_toolPickStep == 0)
            {
                _settings.BrushTool = spot;
                _toolPickStep = 1;
                Toast.ShowSticky("Now click the game's bucket (fill) button.");
            }
            else
            {
                _settings.FillTool = spot;
                _paletteCapture?.Stop();
            }
            return;
        }

        string hex = Theming.ToHex(colour);
        // clicking the same swatch again updates it instead of adding a duplicate
        var existing = _settings.Palette.FirstOrDefault(p => Math.Abs(p.X - x) <= 6 && Math.Abs(p.Y - y) <= 6);
        if (existing != null)
        {
            existing.Hex = hex;
        }
        else
        {
            bool nearWhite = colour.R > 235 && colour.G > 235 && colour.B > 235;
            _settings.Palette.Add(new PaletteSwatch { X = x, Y = y, Hex = hex, Skip = nearWhite });
        }
        BuildPaletteChips();
        ScheduleTrace();
        Toast.ShowSticky($"Added {hex}  ·  {_settings.Palette.Count} colours.  Right-click or Esc to finish.");
    }

    private void OnPaletteCaptureEnded()
    {
        _hotkeys?.Unregister(HkPaletteEsc);
        if (_toolPickStep >= 0)
        {
            _toolPickStep = -1;
            UpdateToolsLabel();
            Toast.Hide();
            SaveSettings();
            ShowFromTray();
            return;
        }
        PickColoursLabel.Text = "Pick from screen";
        UpdatePaletteHint();
        Toast.Hide();
        SaveSettings();
        ShowFromTray();
    }

    private void ClearPalette_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.Palette.Count == 0) return;
        _settings.Palette.Clear();
        BuildPaletteChips();
        ScheduleTrace();
    }

    // ---- placement -----------------------------------------------------------------------

    private void SetArea_Click(object sender, RoutedEventArgs e)
    {
        if (_drawer.IsRunning) return;

        _ghost?.Hide();
        Hide(); // get out of the way so the whole screen can be chosen
        var picker = new AreaSelectWindow(_traceSource);
        picker.ShowDialog();
        Show();
        Activate();

        if (picker.Selection is { } selection)
        {
            _area = selection;
            UpdateSpeedHint();
            AreaText.Text = $"{selection.Width} × {selection.Height} at {selection.X}, {selection.Y}";
            _ghostSuppressed = false;
            UpdateTraceLabels();
            // detail is measured against the area, so the trace changes with it
            _traceDebounce.Stop();
            RunTrace();
        }
        UpdateGhost();
    }

    /// <summary>The exact screen rectangle the drawing will occupy: the chosen area, aspect-corrected.</summary>
    private Int32Rect? DrawingRect() =>
        _area is { } area && _trace != null ? Placement.Fit(area, _trace.Width, _trace.Height) : null;

    private void UpdateGhost()
    {
        bool show = _tab == TabTraceIndex && IsVisible && WindowState != WindowState.Minimized
                    && !_drawer.IsRunning && !_ghostSuppressed
                    && _trace is { Paths.Count: > 0 } && DrawingRect() != null;
        if (!show)
        {
            _ghost?.Hide();
            return;
        }
        _ghost ??= new GhostWindow();
        _ghost.Present(_trace!, DrawingRect()!.Value);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        UpdateGhost();
    }

    // ---- drawing -------------------------------------------------------------------------

    private void Draw_Click(object sender, RoutedEventArgs e)
    {
        if (_drawer.IsRunning)
        {
            _drawer.Abort("Stopped.");
            return;
        }
        BeginDrawing(countdown: null);
    }

    /// <summary>The start hotkey: works from inside the game, without switching back to Overskin.</summary>
    private void HotkeyStartDrawing()
    {
        if (_drawer.IsRunning)
        {
            _drawer.TogglePause();
            return;
        }
        BeginDrawing(countdown: Math.Min(1, _settings.CountdownSeconds));
    }

    private void BeginDrawing(int? countdown)
    {
        if (_trace == null)
        {
            Toast.Show("Load an image to trace first.");
            return;
        }
        if (_trace.Paths.Count == 0)
        {
            Toast.Show(_trace.Message ?? "There's nothing to draw.");
            return;
        }
        if (DrawingRect() is not { } rect)
        {
            Toast.Show("Choose where to draw first: click Set area.");
            return;
        }

        bool bucket = _trace.IsFill != null;
        if (bucket && (_settings.BrushTool == null || _settings.FillTool == null))
        {
            Toast.Show("Pick the game's brush and bucket first, so Overskin can switch between them.");
            return;
        }

        // working pixels -> physical screen pixels; colour strokes first click their swatch
        double sx = (double)rect.Width / _trace.Width, sy = (double)rect.Height / _trace.Height;
        var strokes = new List<DrawStroke>(_trace.Paths.Count);
        for (int p = 0; p < _trace.Paths.Count; p++)
        {
            var path = _trace.Paths[p];
            var mapped = new Point[path.Length];
            for (int i = 0; i < path.Length; i++)
                mapped[i] = new Point(rect.X + path[i].X * sx, rect.Y + path[i].Y * sy);

            Point? select = null;
            if (_trace.ColourIndex is { } index && index[p] < _settings.Palette.Count)
            {
                var swatch = _settings.Palette[index[p]];
                select = new Point(swatch.X, swatch.Y);
            }
            if (bucket)
            {
                bool fill = _trace.IsFill![p];
                var tool = fill ? _settings.FillTool! : _settings.BrushTool!;
                strokes.Add(new DrawStroke(mapped, select, new Point(tool.X, tool.Y), fill));
            }
            else
            {
                strokes.Add(new DrawStroke(mapped, select));
            }
        }

        StartDrawing(strokes, rect, CurrentDrawSettings(countdown));
    }

    /// <summary>Draws just the border of the chosen area: a cheap way to check placement and input.</summary>
    private void TestBox_Click(object sender, RoutedEventArgs e)
    {
        if (_drawer.IsRunning) return;
        if ((DrawingRect() ?? _area) is not { } rect)
        {
            Toast.Show("Choose where to draw first: click Set area.");
            return;
        }

        double l = rect.X, t = rect.Y, r = rect.X + rect.Width - 1, b = rect.Y + rect.Height - 1;
        var box = new[] { new Point(l, t), new Point(r, t), new Point(r, b), new Point(l, b), new Point(l, t) };
        StartDrawing(new List<DrawStroke> { new(box) }, rect, CurrentDrawSettings());
    }

    private void StartDrawing(List<DrawStroke> strokes, Int32Rect rect, DrawSettings settings)
    {
        _hotkeys?.Register(HkAbortEsc, new Hotkey(ModifierKeys.None, Key.Escape), () => _drawer.Abort("Stopped with Esc."));
        if (Hotkey.TryParse(_settings.HotkeyAbort, out var abort))
            _hotkeys?.Register(HkAbort, abort, () => _drawer.Abort($"Stopped with {_settings.HotkeyAbort}."));
        if (Hotkey.TryParse(_settings.HotkeyPause, out var pause))
            _hotkeys?.Register(HkPause, pause, () => _drawer.TogglePause());

        if (!_drawer.Start(strokes, settings))
        {
            EndDrawingUi();
            return;
        }

        _ghost?.Hide();
        DrawLabel.Text = "Stop";
        DrawIcon.Data = (Geometry)FindResource("IcSquare");
        SetAreaButton.IsEnabled = false;
        TestButton.Visibility = Visibility.Collapsed;
        PauseButton.Visibility = Visibility.Visible;
        SetPauseButton(paused: false);
        ProgressRow.Visibility = Visibility.Visible;
        DrawProgress.Value = 0;
        EtaText.Text = "";

        // never let the pen click on our own window
        var own = VisibleBounds(new WindowInteropHelper(this).Handle);
        bool overlaps = own.Left < rect.X + rect.Width && own.Right > rect.X && own.Top < rect.Y + rect.Height && own.Bottom > rect.Y;
        if (!overlaps)
        {
            foreach (var s in strokes)
                if (s.SelectAt is { } p && own.Contains(new POINT((int)p.X, (int)p.Y))) { overlaps = true; break; }
        }
        if (overlaps)
        {
            _minimizedForDrawing = true;
            WindowState = WindowState.Minimized;
        }
    }

    private void Pause_Click(object sender, RoutedEventArgs e) => _drawer.TogglePause();

    private void SetPauseButton(bool paused)
    {
        PauseLabel.Text = paused ? "Resume" : "Pause";
        PauseIcon.Data = (Geometry)FindResource(paused ? "IcPlay" : "IcPause");
    }

    private void OnDrawProgress(DrawProgress p)
    {
        if (!_drawer.IsRunning) return;

        string hud;
        if (p.Paused)
        {
            SetPauseButton(paused: true);
            EtaText.Text = "paused";
            hud = $"Paused.  Press {_settings.HotkeyPause} to carry on, Esc to stop.";
        }
        else if (p.IsCountdown)
        {
            EtaText.Text = $"starts in {p.CountdownSeconds}";
            hud = $"Drawing starts in {p.CountdownSeconds}.  Click into your drawing program now.  Esc cancels.";
        }
        else
        {
            SetPauseButton(paused: false);
            DrawProgress.Value = p.Fraction;
            string left = FormatTime(p.Remaining);
            EtaText.Text = left + " left";
            hud = $"Drawing  {(int)(p.Fraction * 100)}%  ·  {left} left  ·  {_settings.HotkeyPause} pauses  ·  Esc stops";
        }

        if (hud != _lastHud)
        {
            _lastHud = hud;
            Toast.ShowSticky(hud);
        }
    }

    private void OnDrawFinished(DrawOutcome outcome, string message)
    {
        EndDrawingUi();
        if (outcome == DrawOutcome.Completed) DrawProgress.Value = 1;
        _ghostSuppressed = true; // the real drawing is there now; the ghost would only cover it

        if (_minimizedForDrawing)
        {
            _minimizedForDrawing = false;
            WindowState = WindowState.Normal;
        }
        Toast.Show(message, warning: outcome == DrawOutcome.Failed);
    }

#if DEBUG
    /// <summary>Developer aid: renders each tab to a PNG so layout can be checked without a screen.</summary>
    public async Task SnapshotTabsAsync(string folder, string? traceImage, string prefix = "")
    {
        _exiting = true;
        System.IO.Directory.CreateDirectory(folder);
        if (traceImage != null && _images.Count == 0 && TryLoadFile(traceImage, out var bitmap))
        {
            LoadTraceImage(bitmap!);
            PinImage(bitmap!, System.IO.Path.GetFileName(traceImage));
            _images[0].Rotation = 20;
            _images[0].GridDivisions = 4;
            ImageList.SelectedIndex = 0;
            AddToHistory(traceImage);
            AddToHistory(traceImage.Replace("rich.png", "test.png"));
        }
        for (int tab = 0; tab < 5; tab++)
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

            // the trace tab in each style, with a sample palette for Colour
            _settings.Palette = new List<PaletteSwatch>
            {
                new() { X = 10, Y = 10, Hex = "#FFFFFF", Skip = true },
                new() { X = 40, Y = 10, Hex = "#000000" },
                new() { X = 70, Y = 10, Hex = "#E3242B" },
                new() { X = 100, Y = 10, Hex = "#2563EB" },
                new() { X = 130, Y = 10, Hex = "#F5C518" },
            };
            BuildPaletteChips();
            SelectTab(TabTraceIndex);
            foreach (var mode in new[] { "Lines", "Sketch", "Hatch", "Shade", "Colour" })
            {
                SelectMode(mode);
                _traceDebounce.Stop();
                RunTrace();
                await Task.Delay(900);
                foreach (var target in new[] { ("top", 0.0), ("bottom", 2000.0) })
                {
                    TraceScroll.ScrollToVerticalOffset(target.Item2);
                    await Task.Delay(250);
                    var page = new RenderTargetBitmap((int)(ActualWidth * 2), (int)(ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
                    page.Render((FrameworkElement)Content);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(page));
                    using var f = System.IO.File.Create(System.IO.Path.Combine(folder, $"trace-{mode}-{target.Item1}.png"));
                    enc.Save(f);
                }
            }
            TraceScroll.ScrollToTop();

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
            if (traceImage != null && TryLoadFile(traceImage, out var shotImg))
            {
                var ed = new EditorWindow(shotImg!, traceImage, "Overskin 2026-11-16 14.03.22.png", new EditorHost { SaveNew = _ => null, Pin = (_, _) => { }, Trace = _ => { } });
                ed.Show();
                await Task.Delay(400);
                ed.AddForTest(new Annotation { Kind = AnnotationKind.Arrow, Points = { new Point(80, 820), new Point(330, 600) }, Color = Color.FromRgb(0xEF, 0x44, 0x44), Size = 5 });
                ed.AddForTest(new Annotation { Kind = AnnotationKind.Rectangle, Points = { new Point(600, 200), new Point(1080, 740) }, Color = Color.FromRgb(0xEF, 0x44, 0x44), Size = 5 });
                ed.AddForTest(new Annotation { Kind = AnnotationKind.Step, Points = { new Point(600, 200) }, Color = Color.FromRgb(0x3B, 0x82, 0xF6), Size = 6, Number = 1 });
                ed.AddForTest(new Annotation { Kind = AnnotationKind.Text, Points = { new Point(620, 760) }, Color = Color.FromRgb(0xEF, 0x44, 0x44), Size = 6, Text = "five tools in one" });
                ed.AddForTest(new Annotation { Kind = AnnotationKind.Highlighter, Points = { new Point(560, 120), new Point(900, 120) }, Color = Color.FromRgb(0xFA, 0xCC, 0x15), Size = 8 });
                await Task.Delay(500);
                var edShot = new RenderTargetBitmap((int)(ed.ActualWidth * 1.5), (int)(ed.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
                edShot.Render((FrameworkElement)ed.Content);
                var edEnc = new PngBitmapEncoder();
                edEnc.Frames.Add(BitmapFrame.Create(edShot));
                using (var f = System.IO.File.Create(System.IO.Path.Combine(folder, "editor.png"))) edEnc.Save(f);
                ed.Close();

                CaptureNotice.ShowFor(shotImg!, null, "1200 × 900  ·  Saved and copied", () => { }, () => { }, () => { }, () => { }, () => { });
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
            if (traceImage != null && TryLoadFile(traceImage, out var gifImg))
            {
                var gclip = GifClip.FromImage(gifImg!);
                gclip.Frames.Add(gclip.Frames[0]);
                gclip.DelaysMs.Add(500);
                var ge = new GifEditorWindow(gclip, "Recording 14.03.22  ·  2 frames", new GifEditorHost { Folder = folder, Saved = _ => { } });
                ge.Show();
                await Task.Delay(500);
                ge.TopBox.Text = "me when the screenshot is already on my clipboard";
                await Task.Delay(400);
                var geShot = new RenderTargetBitmap((int)(ge.ActualWidth * 1.5), (int)(ge.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
                geShot.Render((FrameworkElement)ge.Content);
                var geEnc = new PngBitmapEncoder();
                geEnc.Frames.Add(BitmapFrame.Create(geShot));
                using (var f = System.IO.File.Create(System.IO.Path.Combine(folder, "gif-editor.png"))) geEnc.Save(f);
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
            _settings.AccentColor = "#48C288";
            _settings.BackgroundTone = "Black";
            ApplyTheme();
            await SnapshotTabsAsync(folder, null, "alt-");
        }
    }
#endif

    private void EndDrawingUi()
    {
        _hotkeys?.Unregister(HkAbortEsc);
        _hotkeys?.Unregister(HkAbort);
        _hotkeys?.Unregister(HkPause);
        _lastHud = "";
        DrawLabel.Text = "Draw";
        DrawIcon.Data = (Geometry)FindResource("IcPencil");
        SetAreaButton.IsEnabled = true;
        TestButton.Visibility = Visibility.Visible;
        PauseButton.Visibility = Visibility.Collapsed;
        ProgressRow.Visibility = Visibility.Collapsed;
    }
}
