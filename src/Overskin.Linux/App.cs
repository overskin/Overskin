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
        Styles.Add(new FluentTheme
        {
            Palettes =
            {
                [ThemeVariant.Dark] = new ColorPaletteResources
                {
                    Accent = Colours.Accent,
                    RegionColor = Colours.Bg,
                    AltHigh = Colours.Bg,
                    BaseHigh = Colours.Text,
                    ChromeMediumLow = Colours.Panel,
                },
            },
        });
        Colours.AddStyles(this);
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
        Log.Write($"start: Overskin {typeof(App).Assembly.GetName().Version} on {Environment.OSVersion}; {Log.Environment_()}");
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log.Write("unhandled: " + e.Exception);
            e.Handled = true;
            MessageWindow.Show("Something went wrong", e.Exception.Message + "\n\nDetails are in " + Log.FilePath);
        };
        SetUpTray();
        SingleInstance.Listen(a => Dispatcher.UIThread.Post(() => Handle(a, fromAnotherStart: true)), _stop.Token);
        Desktop.ShutdownRequested += (_, _) => _stop.Cancel();
        Handle(args, fromAnotherStart: false);
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
        if (args.Contains("--capture")) { _ = CaptureAsync(area: true, delay); return; }
        if (args.Contains("--screen")) { _ = CaptureAsync(area: false, delay); return; }
        if (After("--open") is { } open) { OpenInEditor(open); return; }
        if (After("--pin") is { } pin) { PinFile(pin); return; }
        if (args.Contains("--tray") && !fromAnotherStart) return;
        if (!fromAnotherStart && !Settings.ShowWindowOnStart && _tray != null) return;
        ShowMain();
    }

    private void SetUpTray()
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
        menu.Items.Add(new NativeMenuItemSeparator());
        Item("Open a picture…", () => _ = OpenPictureAsync(pin: false));
        Item("Pin a picture…", () => _ = OpenPictureAsync(pin: true));
        Item("Open the screenshots folder", () => OpenFolder());
        menu.Items.Add(new NativeMenuItemSeparator());
        Item("Open Overskin", ShowMain);
        Item("Quit", Quit);

        try
        {
            _tray = new TrayIcon { Icon = Icon, ToolTipText = "Overskin", Menu = menu, IsVisible = true };
            _tray.Clicked += (_, _) => ShowMain();
            TrayIcon.SetIcons(this, new TrayIcons { _tray });
        }
        catch { _tray = null; } // no tray on this desktop; the window is the way in
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
        _stop.Cancel();
        if (_tray != null) _tray.IsVisible = false;
        Desktop.Shutdown();
    }

    public void OpenFolder()
    {
        Directory.CreateDirectory(Settings.SaveFolder);
        Platform.Desktop.Open(Settings.SaveFolder);
    }

    public async Task CaptureAsync(bool area, int delaySeconds)
    {
        if (_capturing) return;
        _capturing = true;
        bool mainWasOpen = _main?.IsVisible == true;
        try
        {
            if (mainWasOpen) _main!.Hide();
            // let the window fade out (and the tray menu close) before reading the screen
            await Task.Delay(Math.Max(0, delaySeconds) * 1000 + 300);

            Log.Write($"capture: {(area ? "area" : "screen")}, delay {delaySeconds}s");
            var screen = await ScreenGrabber.GrabAsync();
            Bitmap shot = screen;
            if (area)
            {
                var picked = await CaptureOverlay.PickAsync(screen);
                Log.Write("capture: picked " + (picked?.ToString() ?? "nothing (cancelled)"));
                if (picked is not { } rect) return;
                shot = rect == new PixelRect(screen.PixelSize) ? screen : Imaging.Crop(screen, rect);
            }
            await FinishAsync(shot);
        }
        catch (Exception ex)
        {
            Log.Write("capture failed: " + ex);
            MessageWindow.Show("Couldn't take the screenshot", ex.Message);
        }
        finally
        {
            _capturing = false;
            if (mainWasOpen && Settings.AfterCapture != "editor") _main?.Show();
        }
    }

    private async Task FinishAsync(Bitmap shot)
    {
        string? path = null;
        if (Settings.SaveAutomatically)
        {
            path = Settings.NewFilePath();
            Imaging.SavePng(shot, path);
        }

        var anyWindow = _main ?? (TopLevel?)Desktop.Windows.FirstOrDefault();
        bool copied = Settings.CopyToClipboard && await Platform.Desktop.CopyImageAsync(shot, anyWindow);

        switch (Settings.AfterCapture)
        {
            case "editor":
                new EditorWindow(shot, path).Show();
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
        try { new PinWindow(new Bitmap(file)).Show(); }
        catch (Exception ex) { MessageWindow.Show("Couldn't pin the picture", ex.Message); }
    }

    public async Task OpenPictureAsync(bool pin)
    {
        ShowMain();
        var file = await Ui.PickPictureAsync(_main!);
        if (file == null) return;
        if (pin) PinFile(file); else OpenInEditor(file);
    }
}
