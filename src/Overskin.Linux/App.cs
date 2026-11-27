using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Overskin.Linux.Core;
using Overskin.Linux.Interface;
using Overskin.Linux.Platform;

namespace Overskin.Linux;

public sealed class App : Application
{
    public static string[] StartArgs { get; set; } = Array.Empty<string>();
    public static Settings Settings { get; private set; } = new();
    public static new App Current => (App)Application.Current!;

    private IClassicDesktopStyleApplicationLifetime Desktop => (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
    private MainWindow? _main;
    private TrayIcon? _tray;
    private readonly CancellationTokenSource _stop = new();
    private bool _capturing;

    public static WindowIcon Icon => new(AssetLoader.Open(new Uri("avares://overskin/Assets/overskin.png")));

    /// <summary>The icon as a file, for notifications.</summary>
    public static string IconFile
    {
        get
        {
            var path = Path.Combine(Path.GetTempPath(), "overskin-icon.png");
            if (!File.Exists(path))
            {
                try
                {
                    using var src = AssetLoader.Open(new Uri("avares://overskin/Assets/overskin.png"));
                    using var dst = File.Create(path);
                    src.CopyTo(dst);
                }
                catch { }
            }
            return path;
        }
    }

    public override void Initialize()
    {
        Name = "Overskin";
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
        Colours.AddStyles(this);
        Theming.Apply(Theming.Themes[0].Name);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        var args = StartArgs;

        int demo = Array.IndexOf(args, "--demo");
        if (demo >= 0 && demo + 1 < args.Length) ScreenGrabber.DemoFile = Path.GetFullPath(args[demo + 1]);

        if (args.Contains("--selftest"))
        {
            Settings.FilePath = Path.Combine(Path.GetTempPath(), $"overskin-selftest-{Environment.ProcessId}.json");
            Dispatcher.UIThread.Post(async () => Desktop.Shutdown(await SelfTest.RunAsync()));
            return;
        }
        if (args.Contains("--snapshots"))
        {
            int at = Array.IndexOf(args, "--snapshots");
            var outDir = at + 1 < args.Length ? args[at + 1] : "snapshots";
            Dispatcher.UIThread.Post(async () => Desktop.Shutdown(await Snapshots.RunAsync(outDir)));
            return;
        }

        Settings = Settings.Load();
        Theming.Apply(Theming.Effective(Settings));
        Log.Write($"start: Overskin {typeof(App).Assembly.GetName().Version} on {Environment.OSVersion}; {Log.Environment_()}");
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log.Write("unhandled: " + e.Exception);
            e.Handled = true;
            MessageWindow.Show("Something went wrong", e.Exception.Message + "\n\nDetails are in " + Log.FilePath);
        };
        // each of these on its own: one that fails (say, the tray on an unusual desktop) mustn't stop the window opening
        Try("tray", SetUpTray);
        Try("dock", () =>
        {
            if (TryGetFeature(typeof(IActivatableLifetime)) is not IActivatableLifetime life) return;
            life.Activated += (_, e) =>
            {
                if (e.Kind == ActivationKind.Reopen) Dispatcher.UIThread.Post(ShowMain);
                else if (e is FileActivatedEventArgs files)
                    foreach (var f in files.Files)
                        if (Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(f) is { } p && File.Exists(p)) Dispatcher.UIThread.Post(() => OpenInEditor(p));
            };
        });
        if (Mac.IsMac) Try("hotkeys", () => RegisterHotkeys());
        _ = CleanUpAsync();
        Try("update checks", StartUpdateChecks);
        if (Settings.RestorePins) Try("pins", PinWindow.RestoreAll);
        // pins are kept when the computer shuts down or logs out too, not only on Quit
        Desktop.ShutdownRequested += (_, _) => { if (Settings.RestorePins) PinWindow.SaveAll(); };
        if (Settings.ReplayOn) _ = StartReplayAsync();
        Try("single instance", () => SingleInstance.Listen(a => Dispatcher.UIThread.Post(() => Handle(a, fromAnotherStart: true)), _stop.Token));
        Desktop.ShutdownRequested += (_, _) => _stop.Cancel();
        Handle(args, fromAnotherStart: false);
    }

    private static void Try(string what, Action step)
    {
        try { step(); }
        catch (Exception e) { Log.Write($"start: {what} failed: {e}"); }
    }

    /// <summary>Runs a command line, from this start or a later "overskin ..." call.</summary>
    public void Handle(string[] args, bool fromAnotherStart)
    {
        int delay = 0;
        int d = Array.IndexOf(args, "--delay");
        if (d >= 0 && d + 1 < args.Length) int.TryParse(args[d + 1], out delay);

        string? After(string flag)
        {
            int i = Array.IndexOf(args, flag);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        if (args.Contains("--quit")) { Quit(); return; }
        // --workflow "name" (or its number, from 1): for a desktop shortcut on Linux
        if (After("--workflow") is { } flow)
        {
            var w = int.TryParse(flow, out int n) && n >= 1 && n <= Settings.Workflows.Count ? Settings.Workflows[n - 1]
                : Settings.Workflows.FirstOrDefault(x => string.Equals(x.Name, flow, StringComparison.OrdinalIgnoreCase) || string.Equals(x.Title, flow, StringComparison.OrdinalIgnoreCase));
            if (w != null) RunWorkflow(w);
            else MessageWindow.Show("No such workflow", $"There's no workflow called \"{flow}\". They're made in Settings › Workflows.");
            return;
        }
        if (args.Contains("--stop")) { _ = StopRecordingAsync(keep: true); return; }
        if (args.Contains("--replay")) { _ = SaveReplayAsync(); return; }
        if (args.Contains("--pause")) { _ = TogglePauseAsync(); return; }
        if (args.Contains("--draw")) { TogglePen(); return; }
        if (args.Contains("--recent")) { new RecentStrip().Show(); return; }
        if (args.Contains("--scroll")) { _ = ScrollCaptureAsync(); return; }
        if (args.Contains("--step")) { _ = Steps.AddStepAsync(); return; }
        if (args.Contains("--steps")) { _ = Steps.ToggleAsync(); return; }
        if (args.Contains("--record")) { _ = RecordAsync(gif: false, delay); return; }
        if (args.Contains("--gif")) { _ = RecordAsync(gif: true, delay); return; }
        if (args.Contains("--search")) { OpenSearch(); return; }
        if (args.Contains("--ring")) { OpenRing(); return; }
        if (args.Contains("--text")) { _ = TextFromScreenAsync(); return; }
        if (args.Contains("--snip")) { _ = SnipAndPinAsync(); return; }
        if (args.Contains("--pin-clipboard")) { _ = PinClipboardAsync(); return; }
        if (args.Contains("--pin-window")) { _ = PinWindowAsync(); return; }
        if (args.Contains("--repeat")) { _ = RepeatRegionAsync(); return; }
        if (args.Contains("--window")) { _ = ActiveWindowAsync(); return; }
        if (args.Contains("--colour") || args.Contains("--color")) { _ = ScreenToolAsync(ScreenTool.Colour); return; }
        if (args.Contains("--ruler")) { _ = ScreenToolAsync(ScreenTool.Ruler); return; }
        if (args.Contains("--capture")) { _ = CaptureAsync(area: true, delay); return; }
        if (args.Contains("--screen")) { _ = CaptureAsync(area: false, delay); return; }
        if (After("--edit-video") is { } video) { _ = VideoEditor.OpenAsync(Path.GetFullPath(video)); return; }
        if (After("--open") is { } open) { OpenInEditor(open); return; }
        if (After("--pin") is { } pin) { PinFile(pin); return; }
        if (args.Contains("--tray") && !fromAnotherStart) return;
        if (!fromAnotherStart && !Settings.ShowWindowOnStart && _tray != null) return;
        ShowMain();
    }

    private void SetUpTray()
    {
        try
        {
            _tray = new TrayIcon { Icon = Icon, ToolTipText = "Overskin", Menu = TrayMenu(), IsVisible = true };
            _tray.Clicked += (_, _) => ShowMain();
            TrayIcon.SetIcons(this, new TrayIcons { _tray });
        }
        catch { _tray = null; } // no tray on this desktop; the window is the way in
    }

    /// <summary>The menu again (workflows were added, changed or removed).</summary>
    public void RebuildTrayMenu()
    {
        if (_tray != null) _tray.Menu = TrayMenu();
    }

    private NativeMenu TrayMenu()
    {
        var menu = new NativeMenu();
        void Item(string text, Action click)
        {
            var item = new NativeMenuItem(text);
            item.Click += (_, _) => click();
            menu.Items.Add(item);
        }
        Item("Capture area", () => _ = CaptureAsync(true, 0));
        Item("Capture screen", () => _ = CaptureAsync(false, 0));
        Item("Capture area in 3 seconds", () => _ = CaptureAsync(true, 3));
        Item("Capture the active window", () => _ = ActiveWindowAsync());
        Item("Repeat the last area", () => _ = RepeatRegionAsync());
        if (Settings.Workflows.Count > 0)
        {
            menu.Items.Add(new NativeMenuItemSeparator());
            foreach (var w in Settings.Workflows.ToList()) Item(w.Title, () => RunWorkflow(w));
        }
        menu.Items.Add(new NativeMenuItemSeparator());
        Item("Text or QR code from screen", () => _ = TextFromScreenAsync());
        Item("Snip and pin", () => _ = SnipAndPinAsync());
        Item("Pin the clipboard picture", () => _ = PinClipboardAsync());
        Item("Pin a window (live)", () => _ = PinWindowAsync());
        Item("Colour picker", () => _ = ScreenToolAsync(ScreenTool.Colour));
        Item("Ruler", () => _ = ScreenToolAsync(ScreenTool.Ruler));
        menu.Items.Add(new NativeMenuItemSeparator());
        Item("Record a video", () => _ = RecordAsync(false, 0));
        Item("Record a GIF", () => _ = RecordAsync(true, 0));
        Item("Stop recording", () => _ = StopRecordingAsync(keep: true));
        Item("Pause or carry on recording", () => _ = TogglePauseAsync());
        Item("Draw on the recording", TogglePen);
        Item("Save the instant replay", () => _ = SaveReplayAsync());
        Item("Scrolling capture", () => _ = ScrollCaptureAsync());
        Item("Record steps (a how-to guide)", () => _ = Steps.ToggleAsync());
        menu.Items.Add(new NativeMenuItemSeparator());
        Item("Recent captures", () => new RecentStrip().Show());
        Item("Search everything…", OpenSearch);
        Item("Make pins clickable again", () => { foreach (var pin in PinWindow.All.ToList()) pin.ClickThrough = false; });
        Item("Close all pins", () => { foreach (var pin in PinWindow.All.ToList()) pin.Close(); });
        menu.Items.Add(new NativeMenuItemSeparator());
        Item("Open a picture…", () => _ = OpenPictureAsync(pin: false));
        Item("Pin a picture…", () => _ = OpenPictureAsync(pin: true));
        Item("Open the screenshots folder", () => OpenFolder());
        menu.Items.Add(new NativeMenuItemSeparator());
        Item("Open Overskin", ShowMain);
        Item("Quit", Quit);
        return menu;
    }

    // ---- hotkeys (macOS) -------------------------------------------------------------------------

    /// <summary>What each hotkey setting does, and its name for the Settings page.</summary>
    public IEnumerable<(string Name, Func<string> Get, Action<string> Set, Action Run)> Hotkeys()
    {
        var s = Settings;
        yield return ("Capture an area", () => s.HotkeyArea, v => s.HotkeyArea = v, () => _ = CaptureAsync(true, 0));
        yield return ("Capture the whole screen", () => s.HotkeyScreen, v => s.HotkeyScreen = v, () => _ = CaptureAsync(false, 0));
        yield return ("Capture the active window", () => s.HotkeyWindow, v => s.HotkeyWindow = v, () => _ = ActiveWindowAsync());
        yield return ("Snip and pin", () => s.HotkeySnip, v => s.HotkeySnip = v, () => _ = SnipAndPinAsync());
        yield return ("Text or QR code from screen", () => s.HotkeyText, v => s.HotkeyText = v, () => _ = TextFromScreenAsync());
        yield return ("Colour picker", () => s.HotkeyColour, v => s.HotkeyColour = v, () => _ = ScreenToolAsync(ScreenTool.Colour));
        yield return ("Pin the clipboard picture", () => s.HotkeyPinClipboard, v => s.HotkeyPinClipboard = v, () => _ = PinClipboardAsync());
        yield return ("Search everything", () => s.HotkeySearch, v => s.HotkeySearch = v, OpenSearch);
        yield return ("Save the instant replay", () => s.HotkeyReplay, v => s.HotkeyReplay = v, () => _ = SaveReplayAsync());
        yield return ("Pause or carry on recording", () => s.HotkeyPause, v => s.HotkeyPause = v, () => _ = TogglePauseAsync());
        yield return ("Draw on the recording", () => s.HotkeyDraw, v => s.HotkeyDraw = v, TogglePen);
        yield return ("Ring menu", () => s.HotkeyRing, v => s.HotkeyRing = v, OpenRing);
        yield return ("Record a video (again to stop)", () => s.HotkeyRecord, v => s.HotkeyRecord = v, () => _ = RecordAsync(false, 0));
        yield return ("Record a GIF (again to stop)", () => s.HotkeyGif, v => s.HotkeyGif = v, () => _ = RecordAsync(true, 0));
        yield return ("Recent captures", () => s.HotkeyRecent, v => s.HotkeyRecent = v, () => new RecentStrip().Show());
        yield return ("Scrolling capture", () => s.HotkeyScroll, v => s.HotkeyScroll = v, () => _ = ScrollCaptureAsync());
    }

    /// <summary>The built-in hotkeys and the workflows' (each registered with macOS).</summary>
    private IEnumerable<(string Name, string Keys, Action Run)> AllHotkeys()
    {
        foreach (var (name, get, _, run) in Hotkeys()) yield return (name, get(), run);
        foreach (var w in Settings.Workflows.ToList()) yield return ($"the workflow \"{w.Title}\"", w.Hotkey, () => RunWorkflow(w));
    }

    /// <summary>What already uses a hotkey, leaving out <paramref name="except"/>; null when it's free.</summary>
    public string? HotkeyUsedBy(string keys, Overskin.Workflow? except = null)
    {
        if (string.IsNullOrWhiteSpace(keys)) return null;
        bool Same(string a) => string.Equals(Normal(a), Normal(keys), StringComparison.OrdinalIgnoreCase);
        static string Normal(string k) => string.Join("+", k.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).OrderBy(x => x.Length > 1 ? 0 : 1).ThenBy(x => x, StringComparer.OrdinalIgnoreCase));
        foreach (var (name, get, _, _) in Hotkeys())
            if (Same(get())) return name;
        foreach (var w in Settings.Workflows)
            if (!ReferenceEquals(w, except) && Same(w.Hotkey)) return $"the workflow \"{w.Title}\"";
        return null;
    }

    /// <summary>Registers every hotkey with macOS; the names of any that another app already has.</summary>
    public List<string> RegisterHotkeys()
    {
        var taken = new List<string>();
        Mac.ClearHotKeys();
        foreach (var (name, keys, run) in AllHotkeys())
        {
            if (string.IsNullOrWhiteSpace(keys)) continue;
            if (!Mac.AddHotKey(keys, run)) taken.Add($"{name} ({Mac.Symbols(keys)})");
        }
        if (taken.Count > 0) Log.Write("hotkeys not registered: " + string.Join(", ", taken));
        return taken;
    }

    // ---- recording -----------------------------------------------------------------------------

    private Recorder? _recorder;
    private RecordBar? _recordBar;
    public bool IsRecording => _recorder != null;
    public event Action? LibraryChanged;
    public void NotifyLibraryChanged() => LibraryChanged?.Invoke();

    /// <summary>Starts a video or GIF recording of an area (or, pressed again, stops it).</summary>
    public async Task RecordAsync(bool gif, int delaySeconds)
    {
        if (_recorder != null) { await StopRecordingAsync(keep: true); return; }
        if (_capturing) return;
        if (Recorder.Missing(gif) is { } missing) { MessageWindow.Show("Can't record yet", missing); return; }
        _capturing = true;
        bool mainWasOpen = _main?.IsVisible == true;
        try
        {
            if (mainWasOpen) _main!.Hide();
            await Task.Delay(300);

            // pick the area on a still of the screen, where the desktop allows it; GNOME and KDE
            // under Wayland ask which screen or window in their own dialog instead
            PixelRect? area = null, whole = null;
            if (!ScreenGrabber.IsWayland || ScreenGrabber.IsWlroots)
            {
                var still = await ScreenGrabber.GrabAsync();
                whole = new PixelRect(still.PixelSize);
                var picked = await CaptureOverlay.PickAsync(still, recording: true);
                if (picked is not { } pick) return;
                if (pick.Area != new PixelRect(still.PixelSize)) area = pick.Area;
            }
            if (delaySeconds > 0) await Task.Delay(delaySeconds * 1000);
            if (Settings.CountdownFirst && !await Countdown.RunAsync(area ?? whole, whole)) return;

            var path = Settings.NewFilePath(gif ? ".gif" : ".mp4");
            StopReplay(); // the screen is recorded by one thing at a time; the replay comes back after
            // with a smooth pointer (or auto-zoom) Overskin draws its own pointer afterwards, so the system's is left out
            bool zoom = Settings.AutoZoom && !gif;
            _recorder = await Recorder.StartAsync(area, path, gif, Settings.RecordPointer, Settings.RecordFps, whole, zoom ? Settings.ZoomLevel : 0,
                dim: Settings.DimAroundPointer && !gif, mic: Settings.RecordMic && !gif, systemSound: Settings.RecordSystemSound && !gif, webcam: Settings.Webcam && !gif,
                look: gif ? null : Look());
            if (_recorder == null) return; // cancelled in the desktop's dialog
            // the pen: a see-through sheet over what's recorded, which takes the pointer only while drawing
            if (Mac.IsMac || (OperatingSystem.IsLinux() && !ScreenGrabber.IsWayland))
            {
                _pen = new ScreenPen(area ?? whole ?? new PixelRect(0, 0, 1920, 1080)) { AutoFade = Settings.PenFades, Linger = Settings.PenLinger };
                _pen.Changed += () => _recordBar?.ShowPen(_pen?.On == true);
                _pen.Show();
            }
            _recordBar = new RecordBar(gif, () => _ = StopRecordingAsync(keep: true), () => _ = StopRecordingAsync(keep: false), Settings.RecordStopAfter,
                _recorder.CanPause ? () => _ = TogglePauseAsync() : null, _pen != null ? TogglePen : null);
            _recordBar.Show();
        }
        catch (Exception ex)
        {
            Log.Write("record failed: " + ex);
            _recorder = null;
            MessageWindow.Show("Couldn't start recording", ex.Message);
        }
        finally
        {
            _capturing = false;
            if (_recorder == null) ReturnToMain(mainWasOpen);
            else _mainBeforeRecording = mainWasOpen;
        }
    }

    public async Task ScrollCaptureAsync()
    {
        if (_capturing) return;
        _capturing = true;
        bool mainWasOpen = _main?.IsVisible == true;
        try
        {
            if (mainWasOpen) _main!.Hide();
            await Task.Delay(300);
            var page = await ScrollCapture.RunAsync();
            if (page != null) await FinishAsync(page);
        }
        catch (Exception ex)
        {
            Log.Write("scroll capture failed: " + ex);
            MessageWindow.Show("Scrolling capture didn't work", ex.Message);
        }
        finally
        {
            _capturing = false;
            ReturnToMain(mainWasOpen);
        }
    }

    private ScreenPen? _pen;

    /// <summary>Pauses the recording, or carries on.</summary>
    public async Task TogglePauseAsync()
    {
        if (_recorder is not { } r) return;
        if (!await r.TogglePauseAsync())
        {
            Platform.Desktop.Notify("Can't pause this recording", "Pausing needs ffmpeg to record it.", IconFile);
            return;
        }
        _recordBar?.ShowPaused(r.IsPaused);
    }

    /// <summary>Drawing on the screen while it records, on or off.</summary>
    public void TogglePen()
    {
        if (_pen == null) return;
        _pen.Toggle();
        _recordBar?.ShowPen(_pen.On);
    }

    public async Task StopRecordingAsync(bool keep)
    {
        var recorder = _recorder;
        if (recorder == null) return;
        _recorder = null;
        _pen?.Close();
        _pen = null;
        if (Settings.ReplayOn) _ = Task.Delay(1500).ContinueWith(_ => Dispatcher.UIThread.Post(() => _ = StartReplayAsync()));
        _recordBar?.Close();
        _recordBar = null;
        try
        {
            if (recorder.Gif && keep) Platform.Desktop.Notify("Making the GIF…", "This takes a moment for long recordings.", IconFile);
            var file = await recorder.StopAsync(keep, Settings.GifFps, Settings.GifMaxWidth);
            if (file == null) return;
            LibraryChanged?.Invoke();
            Platform.Desktop.Notify(recorder.Gif ? "GIF saved" : "Video saved", Path.GetFileName(file), IconFile);
            if (Settings.CopyToClipboard) await Platform.Desktop.CopyFilesAsync(new[] { file }, _main);
        }
        catch (Exception ex)
        {
            Log.Write("record stop failed: " + ex);
            MessageWindow.Show("The recording didn't save", ex.Message);
        }
        finally
        {
            ReturnToMain(_mainBeforeRecording);
            _mainBeforeRecording = false;
        }
    }

    public void ShowMain()
    {
        _main ??= new MainWindow();
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    public void Quit()
    {
        if (Settings.RestorePins) PinWindow.SaveAll();
        else { Settings.Pins.Clear(); Settings.Save(); }
        StopReplay();
        _stop.Cancel();
        if (_tray != null) _tray.IsVisible = false;
        Desktop.Shutdown();
    }

    public void OpenFolder()
    {
        Directory.CreateDirectory(Settings.SaveFolder);
        Platform.Desktop.Open(Settings.SaveFolder);
    }

    /// <summary>Runs a workflow: its capture (after its delay), then its steps.</summary>
    public void RunWorkflow(Overskin.Workflow w)
    {
        Log.Write($"workflow: {w.Title} ({w.Summary})");
        var steps = w.Steps;
        _ = w.Capture switch
        {
            "Window" => ActiveWindowAsync(steps, w.Delay),
            "Screen" => CaptureAsync(false, w.Delay, steps),
            "LastRegion" => RepeatRegionAsync(steps, w.Delay),
            _ => CaptureAsync(true, w.Delay, steps),
        };
    }

    /// <summary>What happens after a capture, as set on the Capture page.</summary>
    private Overskin.AfterSteps SettingsSteps => new()
    {
        AutoHide = Settings.AutoHide,
        Watermark = Settings.Watermark.OnScreenshots,
        Save = Settings.SaveAutomatically,
        Copy = Settings.CopyToClipboard,
        Editor = Settings.AfterCapture == "editor",
        Viewer = Settings.AfterCapture == "viewer",
        Preview = Settings.AfterCapture == "preview",
    };

    public async Task CaptureAsync(bool area, int delaySeconds, Overskin.AfterSteps? steps = null)
    {
        if (_capturing) return;
        _capturing = true;
        bool mainWasOpen = _main?.IsVisible == true;
        try
        {
            if (mainWasOpen) _main!.Hide();
            // let the window fade out (and the tray menu close) before reading the screen
            await Task.Delay(300);
            await DelayBadge.RunAsync(delaySeconds);

            Log.Write($"capture: {(area ? "area" : "screen")}, delay {delaySeconds}s");
            await NoteFrontAsync();
            var screen = await ScreenGrabber.GrabAsync();
            Bitmap shot = screen;
            PixelRect? where = null;
            if (area)
            {
                var picked = await CaptureOverlay.PickAsync(screen);
                Log.Write("capture: picked " + (picked?.Area.ToString() ?? "nothing (cancelled)"));
                if (picked is not { } pick) return;
                if (pick.Marks.Count > 0)
                {
                    // what was drawn on the screen first becomes part of the picture
                    var drawn = new EditorCanvas(screen);
                    drawn.AddRange(pick.Marks);
                    screen = drawn.Export();
                }
                shot = pick.Area == new PixelRect(screen.PixelSize) ? screen : Imaging.Crop(screen, pick.Area);
                where = pick.Area;
                Settings.LastRegion = new[] { pick.Area.X, pick.Area.Y, pick.Area.Width, pick.Area.Height };
                Settings.Save();
            }
            await FinishAsync(shot, steps, where);
        }
        catch (Exception ex)
        {
            Log.Write("capture failed: " + ex);
            MessageWindow.Show("Couldn't take the screenshot", ex.Message);
        }
        finally
        {
            _capturing = false;
            ReturnToMain(mainWasOpen);
        }
    }

    /// <summary>
    /// Brings the main window back after something hid it for a capture: straight away, or once the
    /// windows the capture opened (the editor, the video editor, a viewer) are closed. Without this a
    /// Mac, which has no tray icon to click, could be left with no window at all.
    /// </summary>
    public void ReturnToMain(bool wasOpen)
    {
        if (!wasOpen || _main == null) return;
        static bool Working(Window w) => w.IsVisible && w is EditorWindow or VideoEditor or GifEditor or ViewerWindow or StepsWindow or BeautifyWindow;
        var opened = Desktop.Windows.Where(w => w != _main && Working(w)).ToList();
        if (opened.Count == 0) { _main.Show(); return; }
        void OnClosed(object? sender, EventArgs e)
        {
            if (sender is Window w) w.Closed -= OnClosed;
            if (_main != null && !_main.IsVisible && !Desktop.Windows.Any(x => x != _main && x != sender && Working(x))) _main.Show();
        }
        foreach (var w in opened) w.Closed += OnClosed;
    }

    private bool _mainBeforeRecording;

    /// <summary>The main window see-through (blurred behind) or solid, as set.</summary>
    public void ApplySeeThrough(Window? w = null)
    {
        w ??= _main;
        if (w == null) return;
        if (Settings.SeeThrough)
        {
            w.TransparencyLevelHint = new[] { WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur };
            var bg = Interface.Colours.Bg;
            w.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(170, bg.R, bg.G, bg.B));
        }
        else
        {
            w.TransparencyLevelHint = new[] { WindowTransparencyLevel.None };
            w.Background = new Avalonia.Media.SolidColorBrush(Interface.Colours.Bg);
        }
    }

    /// <summary>How recordings are finished, from the Record page.</summary>
    public RecordLook Look()
    {
        var s = Settings;
        return new RecordLook
        {
            SmoothPointer = s.SmoothPointer, PointerStyle = s.PointerStyle, PointerSize = s.PointerSize, PointerSmoothing = s.PointerSmoothing,
            MotionBlur = s.MotionBlur, ClickRings = s.ClickRings, ShowKeys = s.ShowKeys, Backdrop = s.RecordBackdrop, EditableZooms = s.EditableZooms,
            // the same curves as on Windows: 50 is the usual speed and smoothness
            ZoomRate = 1.6 * Math.Pow(7, s.ZoomSpeed / 100.0), PanRate = 12 * Math.Pow(0.12, s.FollowSmoothing / 100.0), ZoomHold = s.ZoomHold,
            WebcamCorner = s.WebcamCorner, WebcamShape = s.WebcamShape, WebcamSize = s.WebcamSize, Microphone = s.MicrophoneName, Camera = s.WebcamName,
        };
    }

    // ---- instant replay ---------------------------------------------------------------------------

    private Replay? _replay;
    public string ReplayStatus { get; private set; } = "Off";

    /// <summary>Starts (or restarts) the background recording for instant replay.</summary>
    public async Task StartReplayAsync()
    {
        StopReplay();
        if (!Settings.ReplayOn) { ReplayStatus = "Off"; return; }
        if (Replay.Missing() is { } missing) { ReplayStatus = missing; return; }
        try
        {
            _replay = await Replay.StartAsync(Settings.ReplaySeconds, Settings.ReplaySound);
            ReplayStatus = $"On: keeping the last {Settings.ReplaySeconds} seconds of the main screen";
        }
        catch (Exception ex)
        {
            ReplayStatus = "Couldn't start: " + ex.Message;
            Log.Write("replay start: " + ex);
        }
    }

    public void StopReplay()
    {
        _replay?.Dispose();
        _replay = null;
    }

    /// <summary>Saves the last seconds as a video in the screenshots folder.</summary>
    public async Task SaveReplayAsync()
    {
        if (_replay is not { Running: true } replay)
        {
            Platform.Desktop.Notify("Instant replay is off", "Turn it on in Record (Instant replay), then the hotkey saves the last seconds.", IconFile);
            return;
        }
        try
        {
            Platform.Desktop.Notify("Saving the replay…", $"The last {replay.Seconds} seconds.", IconFile);
            var path = Settings.NewFilePath(".mp4");
            await replay.SaveAsync(path);
            LibraryChanged?.Invoke();
            Platform.Desktop.Notify("Replay saved", Path.GetFileName(path), IconFile);
            if (Settings.CopyToClipboard) await Platform.Desktop.CopyFilesAsync(new[] { path }, _main);
        }
        catch (Exception ex)
        {
            Log.Write("replay save: " + ex);
            MessageWindow.Show("The replay didn't save", ex.Message);
        }
    }

    // ---- updates ---------------------------------------------------------------------------------

    /// <summary>A newer release with a download for this computer, once one has been found.</summary>
    public Release? UpdateAvailable { get; private set; }
    public string UpdateStatus { get; private set; } = $"You have Overskin {Updater.Current.ToString(3)}.";
    public event Action? UpdateChanged;
    private DispatcherTimer? _updateTimer;

    /// <summary>A few seconds after starting, then every six hours, when that's switched on.</summary>
    private void StartUpdateChecks()
    {
        var first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        first.Tick += async (_, _) => { first.Stop(); await CheckForUpdateAsync(quietly: true); };
        first.Start();
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _updateTimer.Tick += async (_, _) => await CheckForUpdateAsync(quietly: true);
        _updateTimer.Start();
    }

    /// <summary>Asks GitHub for the latest release; <paramref name="quietly"/> skips it when switched off and says nothing when up to date.</summary>
    public async Task CheckForUpdateAsync(bool quietly)
    {
        if (quietly && !Settings.CheckForUpdates) return;
        if (!quietly) { UpdateStatus = "Checking…"; UpdateChanged?.Invoke(); }
        var release = await Updater.CheckAsync();
        if (release == null)
        {
            if (!quietly) { UpdateStatus = $"You have Overskin {Updater.Current.ToString(3)}, the latest."; UpdateChanged?.Invoke(); }
            return;
        }
        bool news = UpdateAvailable?.Version != release.Version;
        UpdateAvailable = release;
        UpdateStatus = $"Overskin {release.Version.ToString(3)} is out. You have {Updater.Current.ToString(3)}.";
        UpdateChanged?.Invoke();
        if (quietly && news && Settings.DismissedUpdate != release.Version.ToString(3))
            Platform.Desktop.Notify($"Overskin {release.Version.ToString(3)} is out", "Open Overskin to get it. Your settings and captures stay.", IconFile);
    }

    /// <summary>The release page, where the new copy is downloaded.</summary>
    public void OpenUpdatePage()
    {
        if (UpdateAvailable is { } r) Platform.Desktop.Open(r.Page);
    }

    public void DismissUpdate()
    {
        if (UpdateAvailable is not { } r) return;
        Settings.DismissedUpdate = r.Version.ToString(3);
        Settings.Save();
        UpdateChanged?.Invoke();
    }

    /// <summary>Whether the main window shows the "new version" banner.</summary>
    public bool ShowUpdateBanner => UpdateAvailable is { } r && Settings.DismissedUpdate != r.Version.ToString(3);

    // ---- clean-up --------------------------------------------------------------------------------

    /// <summary>The captures old enough to go: older than the chosen days, not favourites. For tests too.</summary>
    public static List<string> OldCaptures(string folder, int days, DateTime now)
    {
        if (days <= 0 || !Directory.Exists(folder)) return new();
        return new DirectoryInfo(folder).EnumerateFiles()
            .Where(f => (Converter.IsPicture(f.FullName) || Converter.IsMoving(f.FullName)) && (now - f.LastWriteTime).TotalDays > days
                        && !LibraryMarks.IsFavourite(f.FullName))
            .Select(f => f.FullName).ToList();
    }

    /// <summary>Moves old captures to the trash, when that's switched on (at start and when changed).</summary>
    public async Task CleanUpAsync()
    {
        try
        {
            var old = OldCaptures(Settings.SaveFolder, Settings.CleanUpDays, DateTime.Now);
            if (old.Count == 0) return;
            await Platform.Desktop.TrashAsync(old);
            Log.Write($"clean-up: moved {old.Count} old capture(s) to the trash");
            LibraryChanged?.Invoke();
        }
        catch (Exception ex) { Log.Write("clean-up: " + ex.Message); }
    }

    // ---- search everything and the ring menu ----------------------------------------------------

    /// <summary>Everything Overskin does, for the command search.</summary>
    public List<PaletteItem> PaletteItems()
    {
        const string capture = "Capture", record = "Record", tools = "Tools", go = "Go to";
        return new List<PaletteItem>
        {
            new("Capture an area", capture, Icons.Area, () => _ = CaptureAsync(true, 0), "region screenshot snip"),
            new("Capture the whole screen", capture, Icons.Screen, () => _ = CaptureAsync(false, 0), "full screenshot"),
            new("Capture the active window", capture, Icons.Window, () => _ = ActiveWindowAsync(), "window screenshot"),
            new("Capture an area in 3 seconds", capture, Icons.Timer, () => _ = CaptureAsync(true, 3), "delay timer"),
            new("Repeat the last area", capture, Icons.Area, () => _ = RepeatRegionAsync(), "same region again"),
            new("Scrolling capture", capture, Icons.Scroll, () => _ = ScrollCaptureAsync(), "long page full page"),
            new("Record a video", record, Icons.Record, () => _ = RecordAsync(false, 0), "screen recording mp4"),
            new("Record a GIF", record, Icons.Record, () => _ = RecordAsync(true, 0), "animation"),
            new("Stop recording", record, Icons.Stop, () => _ = StopRecordingAsync(keep: true), "finish end"),
            new("Save the instant replay", record, Icons.Record, () => _ = SaveReplayAsync(), "last seconds clip game"),
            new("Pause or carry on recording", record, Icons.Pause, () => _ = TogglePauseAsync(), "pause resume continue"),
            new("Draw on the recording", record, Icons.Pen, TogglePen, "pen annotate draw arrow while recording"),
            new("Record steps (how-to guide)", record, Icons.Steps, () => _ = Steps.ToggleAsync(), "tutorial guide clicks"),
            new("Text or QR code from screen", tools, Icons.Text, () => _ = TextFromScreenAsync(), "ocr copy text read qr barcode"),
            new("Snip and pin", tools, Icons.Pin, () => _ = SnipAndPinAsync(), "pin on top"),
            new("Pin the clipboard picture", tools, Icons.Pin, () => _ = PinClipboardAsync(), "paste pin"),
            new("Pin a window (live)", tools, Icons.Window, () => _ = PinWindowAsync(), "live window on top picture in picture"),
            new("Colour picker", tools, Icons.Pen, () => _ = ScreenToolAsync(ScreenTool.Colour), "color hex eyedropper"),
            new("Ruler", tools, Icons.Rectangle, () => _ = ScreenToolAsync(ScreenTool.Ruler), "measure pixels distance"),
            new("Recent captures", tools, Icons.Library, () => new RecentStrip().Show(), "latest drag"),
            new("Open a picture", tools, Icons.Picture, () => _ = OpenPictureAsync(pin: false), "edit file"),
            new("Edit a video", tools, Icons.Record, () => _ = EditVideoAsync(), "trim cut crop speed subtitles captions"),
            new("Pin a picture file", tools, Icons.Pin, () => _ = OpenPictureAsync(pin: true), "on top"),
            new("Open the screenshots folder", tools, Icons.Folder, OpenFolder, "files finder"),
            new("Make pins clickable again", tools, Icons.Pin, () => { foreach (var pin in PinWindow.All.ToList()) pin.ClickThrough = false; }, "click through"),
            new("Close all pins", tools, Icons.Close, () => { foreach (var pin in PinWindow.All.ToList()) pin.Close(); }),
            new("Capture page", go, Icons.Area, () => { ShowMain(); _main!.Go("Capture"); }),
            new("Record page", go, Icons.Record, () => { ShowMain(); _main!.Go("Record"); }, "recording settings frame rate gif auto-zoom countdown"),
            new("Library", go, Icons.Library, () => { ShowMain(); _main!.Go("Library"); }, "captures search favourites tags duplicates"),
            new("Settings", go, Icons.Gear, () => { ShowMain(); _main!.Go("Settings"); }, "hotkeys theme folder watermark start at login"),
            new("Quit Overskin", tools, Icons.Close, Quit, "exit close"),
        }.Concat(Settings.Workflows.ToList().Select(w => new PaletteItem(w.Title, "Workflows", Icons.Steps, () => RunWorkflow(w), "workflow " + w.Summary))).ToList();
    }

    public void OpenSearch() => CommandSearch.Open(PaletteItems());

    /// <summary>The ring menu round the pointer: the capture choices, as on Windows.</summary>
    public void OpenRing() => RingMenu.Open(new List<RingItem>
    {
        new("Capture an area", Icons.Area, () => _ = CaptureAsync(true, 0)),
        new("Capture the active window", Icons.Window, () => _ = ActiveWindowAsync()),
        new("Capture the whole screen", Icons.Screen, () => _ = CaptureAsync(false, 0)),
        new("Record a video", Icons.Record, () => _ = RecordAsync(false, 0)),
        new("Record a GIF", Icons.Record, () => _ = RecordAsync(true, 0)),
        new("Text or QR code from screen", Icons.Text, () => _ = TextFromScreenAsync()),
        new("Snip and pin", Icons.Pin, () => _ = SnipAndPinAsync()),
        new("Recent captures", Icons.Library, () => new RecentStrip().Show()),
    });

    // ---- screen tools ---------------------------------------------------------------------------

    /// <summary>
    /// Hides Overskin's window, takes a still of the screen and lets <paramref name="use"/> work on it;
    /// errors are shown, and the window comes back if it was open.
    /// </summary>
    private async Task OnStillAsync(Func<Bitmap, Task> use, string what, int delaySeconds = 0)
    {
        if (_capturing) return;
        _capturing = true;
        bool mainWasOpen = _main?.IsVisible == true;
        try
        {
            if (mainWasOpen) _main!.Hide();
            await Task.Delay(300);
            await DelayBadge.RunAsync(delaySeconds);
            await NoteFrontAsync();
            await use(await ScreenGrabber.GrabAsync());
        }
        catch (Exception ex)
        {
            Log.Write(what + " failed: " + ex);
            MessageWindow.Show("That didn't work", ex.Message);
        }
        finally
        {
            _capturing = false;
            ReturnToMain(mainWasOpen);
        }
    }

    /// <summary>Drag over text or a QR code: what it says goes on the clipboard (and into a window, unless "just copy").</summary>
    public Task TextFromScreenAsync() => OnStillAsync(async screen =>
    {
        var picked = await CaptureOverlay.PickAsync(screen, hint: "Drag over the text or QR code you want to copy.  Esc cancels.");
        if (picked is not { } pick) return;
        var crop = Imaging.Crop(screen, pick.Area);
        var top = _main ?? (TopLevel?)Desktop.Windows.FirstOrDefault();
        if (QrReader.Read(crop) is { } code)
        {
            await Platform.Desktop.CopyTextAsync(code, top);
            if (Settings.TextJustCopy) Platform.Desktop.Notify("Copied what the code says", Short(code), IconFile);
            else new TextResultWindow(code, qr: true).Show();
            return;
        }
        if (!Ocr.IsAvailable) { MessageWindow.Show("Can't read text yet", Ocr.InstallHint); return; }
        var text = await Ocr.ReadTextAsync(crop);
        if (string.IsNullOrWhiteSpace(text)) { Platform.Desktop.Notify("No text found there", "Try dragging over it again, a little wider.", IconFile); return; }
        await Platform.Desktop.CopyTextAsync(text, top);
        if (Settings.TextJustCopy) Platform.Desktop.Notify("Text copied", Short(text), IconFile);
        else new TextResultWindow(text).Show();
    }, "text from screen");

    private static string Short(string text)
    {
        var one = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return one.Length <= 60 ? one : one[..57] + "…";
    }

    /// <summary>Drag out a part of the screen: it's pinned on top, exactly where it was.</summary>
    public Task SnipAndPinAsync() => OnStillAsync(async screen =>
    {
        var picked = await CaptureOverlay.PickAsync(screen, hint: "Drag over what to pin. It stays on top, right where it is.  Esc cancels.");
        if (picked is not { } pick) return;
        new PinWindow(Imaging.Crop(screen, pick.Area), pick.Area).Show();
    }, "snip and pin");

    /// <summary>
    /// Click a window: it's pinned on top and kept up to date as it changes (a Mac, or X11); where
    /// Overskin can't follow a window, the pin is a still picture of it.
    /// </summary>
    public Task PinWindowAsync() => OnStillAsync(async screen =>
    {
        var picked = await CaptureOverlay.PickAsync(screen, hint: "Click the window to pin. It stays on top, kept up to date.  Esc cancels.");
        if (picked is not { } pick) return;
        var area = pick.Area;
        var pin = new PinWindow(Imaging.Crop(screen, area), area);
        if (Mac.IsMac)
        {
            var centre = Mac.FromPicture(new Point(area.X + area.Width / 2.0, area.Y + area.Height / 2.0));
            uint id = Mac.WindowIdAt(centre);
            // screencapture gives the window's own pixels (2x on Retina), the same scale as the still
            if (id != 0) pin.Live(() => MacWindowPicture.WindowPictureAsync(id), TimeSpan.FromMilliseconds(400));
        }
        else if (OperatingSystem.IsLinux() && !ScreenGrabber.IsWayland && X11.IsAvailable)
        {
            pin.Live(() =>
            {
                var root = X11.GrabRoot();
                return Task.FromResult<Bitmap?>(Imaging.Crop(root, area.Intersect(new PixelRect(root.PixelSize))));
            }, TimeSpan.FromMilliseconds(250));
        }
        pin.Show();
    }, "pin a window");

    /// <summary>Pins the picture on the clipboard (or a copied picture file).</summary>
    public async Task PinClipboardAsync()
    {
        var picture = await Platform.Desktop.PastePictureAsync(_main ?? (TopLevel?)Desktop.Windows.FirstOrDefault());
        if (picture == null) { Platform.Desktop.Notify("No picture on the clipboard", "Copy a picture first, then pin it.", IconFile); return; }
        new PinWindow(picture).Show();
    }

    /// <summary>Opens the picture on the clipboard (or a copied picture file) in the editor.</summary>
    public async Task EditClipboardAsync()
    {
        var picture = await Platform.Desktop.PastePictureAsync(_main ?? (TopLevel?)Desktop.Windows.FirstOrDefault());
        if (picture == null) { Platform.Desktop.Notify("No picture on the clipboard", "Copy a picture first, then edit it.", IconFile); return; }
        new EditorWindow(picture, null).Show();
    }

    /// <summary>Captures the same area as last time, straight away.</summary>
    public Task RepeatRegionAsync(Overskin.AfterSteps? steps = null, int delaySeconds = 0) => OnStillAsync(async screen =>
    {
        if (Settings.LastRegion is not { Length: 4 } r) { MessageWindow.Show("No area yet", "Capture an area first; then this captures the same area again."); return; }
        var area = new PixelRect(r[0], r[1], r[2], r[3]).Intersect(new PixelRect(screen.PixelSize));
        if (area.Width < 2 || area.Height < 2) { MessageWindow.Show("That area isn't on this screen", "Capture an area here first."); return; }
        await FinishAsync(Imaging.Crop(screen, area), steps, area);
    }, "repeat region", delaySeconds);

    /// <summary>Captures the window in front (on a Mac and X11, where Overskin can see the windows).</summary>
    public Task ActiveWindowAsync(Overskin.AfterSteps? steps = null, int delaySeconds = 0) => OnStillAsync(async screen =>
    {
        var windows = await Input.WindowsAsync();
        var whole = new PixelRect(screen.PixelSize);
        var front = windows.Select(w => w.Bounds.Intersect(whole)).FirstOrDefault(b => b.Width > 40 && b.Height > 40);
        if (front.Width == 0) { await FinishAsync(screen, steps); return; } // can't tell which: the whole screen
        await FinishAsync(Imaging.Crop(screen, front), steps, front);
    }, "active window", delaySeconds);

    /// <summary>The colour picker or the ruler; what they give goes on the clipboard.</summary>
    public Task ScreenToolAsync(ScreenTool tool) => OnStillAsync(async screen =>
    {
        var result = await ScreenTools.RunAsync(screen, tool);
        if (result == null) return;
        await Platform.Desktop.CopyTextAsync(result, _main ?? (TopLevel?)Desktop.Windows.FirstOrDefault());
        Platform.Desktop.Notify(tool == ScreenTool.Colour ? "Colour copied" : "Measurement copied", result, IconFile);
    }, tool == ScreenTool.Colour ? "colour picker" : "ruler");

    /// <summary>Notes the app and window in front, for {app} and {title} in file names.</summary>
    private static async Task NoteFrontAsync()
    {
        try
        {
            if (Mac.IsMac) { FileNames.Context = Mac.FrontWindow(); return; }
            var front = (await Input.WindowsAsync()).FirstOrDefault();
            FileNames.Context = (front.Title?.Split(" - ").LastOrDefault()?.Trim() ?? "", front.Title ?? "");
        }
        catch { FileNames.Context = ("", ""); }
    }

    /// <summary>Hide, beautify, watermark, save, copy (the picture or its text), pin, edit or preview: as set on the Capture page, or as a workflow says.</summary>
    private async Task FinishAsync(Bitmap shot, Overskin.AfterSteps? steps = null, PixelRect? where = null)
    {
        var s = steps ?? SettingsSteps;
        bool fromWorkflow = steps != null;
        if (s.AutoHide && Ocr.IsAvailable)
        {
            try
            {
                var found = await PrivateMenu.FindAsync(shot);
                if (found.Count > 0)
                {
                    var marks = found.Select(f => new PatchMark { Area = new Rect(f.X, f.Y, f.Width, f.Height), Mode = HideMode.Pixelate, Strength = 4 }).ToList();
                    var canvas = new EditorCanvas(shot);
                    canvas.AddRange(marks);
                    shot = canvas.Export();
                    Log.Write("capture: auto-hid " + Overskin.Shared.PrivateDetails.Describe(found.Select(f => f.Kind)));
                }
            }
            catch (Exception ex) { Log.Write("auto-hide failed: " + ex.Message); }
        }
        if (s.Beautify)
        {
            try { shot = Beautify.Render(shot, new BeautifyOptions()); }
            catch (Exception ex) { Log.Write("beautify failed: " + ex.Message); }
        }
        if (s.Watermark && Settings.Watermark.HasContent)
        {
            try { shot = Watermark.Apply(shot, Settings.Watermark); }
            catch (Exception ex) { Log.Write("watermark failed: " + ex.Message); }
        }
        string? path = null;
        if (s.Save)
        {
            path = Settings.NewFilePath();
            Imaging.SavePng(shot, path);
            LibraryChanged?.Invoke();
        }

        var anyWindow = _main ?? (TopLevel?)Desktop.Windows.FirstOrDefault();
        bool copied = s.Copy && await Platform.Desktop.CopyImageAsync(shot, anyWindow);
        bool textCopied = false;
        if (s.CopyText)
        {
            string? text = null;
            try { if (Ocr.IsAvailable) text = await Ocr.ReadTextAsync(shot); } catch (Exception ex) { Log.Write("copy text: " + ex.Message); }
            if (!Ocr.IsAvailable) Platform.Desktop.Notify("Can't read text", Ocr.InstallHint, IconFile);
            else if (string.IsNullOrWhiteSpace(text)) Platform.Desktop.Notify("No text found in that capture", "", IconFile);
            else { await Platform.Desktop.CopyTextAsync(text, anyWindow); textCopied = true; }
        }
        if (s.Pin) new PinWindow(shot, where).Show();

        string after = s.Editor ? "editor" : s.Viewer ? "viewer" : s.Preview ? "preview" : fromWorkflow ? "notice" : Settings.AfterCapture;
        if (fromWorkflow && after == "notice")
        {
            // a workflow says what it did
            string did = string.Join(" and ", new[] { path != null ? "saved" : null, copied ? "copied" : null, textCopied ? "text copied" : null, s.Pin ? "pinned" : null }.Where(x => x != null));
            if (did.Length > 0) Platform.Desktop.Notify(char.ToUpper(did[0]) + did[1..], path != null ? Path.GetFileName(path) : "", path ?? IconFile);
            return;
        }
        switch (after)
        {
            case "editor":
                new EditorWindow(shot, path).Show();
                break;
            case "preview":
                PreviewCard.Show(shot, path);
                break;
            case "viewer" when path != null:
                var pictures = Directory.EnumerateFiles(Settings.SaveFolder).Where(f => Converter.IsPicture(f) || Converter.IsMoving(f)).OrderByDescending(File.GetLastWriteTimeUtc).ToList();
                new ViewerWindow(pictures, path).Show();
                break;
            case "viewer":
                new EditorWindow(shot, path).Show(); // not saved: nothing to view, so the editor
                break;
            case "notice":
                Platform.Desktop.Notify(
                    path != null ? "Screenshot saved" : copied ? "Screenshot copied" : "Screenshot taken",
                    path != null ? Path.GetFileName(path) + (copied ? " — also on the clipboard" : "") : "It's on the clipboard",
                    path ?? IconFile);
                break;
        }
    }

    public void OpenInEditor(string file)
    {
        try { new EditorWindow(new Bitmap(file), Path.GetFullPath(file)).Show(); }
        catch (Exception ex) { MessageWindow.Show("Couldn't open the picture", ex.Message); }
    }

    public void PinFile(string file)
    {
        try { new PinWindow(new Bitmap(file), source: Path.GetFullPath(file)).Show(); }
        catch (Exception ex) { MessageWindow.Show("Couldn't pin the picture", ex.Message); }
    }

    /// <summary>Picks a video and opens it in the video editor.</summary>
    public async Task EditVideoAsync()
    {
        ShowMain();
        var files = await _main!.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Pick a video to edit",
            FileTypeFilter = new[] { new Avalonia.Platform.Storage.FilePickerFileType("Videos") { Patterns = new[] { "*.mp4", "*.mov", "*.mkv", "*.webm" } } },
        });
        if (files.Count > 0 && Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(files[0]) is { } path) await VideoEditor.OpenAsync(path);
    }

    public async Task OpenPictureAsync(bool pin)
    {
        ShowMain();
        var file = await Ui.PickPictureAsync(_main!);
        if (file == null) return;
        if (pin) PinFile(file); else OpenInEditor(file);
    }
}
