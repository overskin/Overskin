using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Overskin.Linux.Platform;

namespace Overskin.Linux.Interface;

/// <summary>
/// The home window, laid out as the Windows app's: a top bar with the pages (Capture, Library,
/// Pins, Settings), the page, and a status line. On a Mac the window's own buttons sit in the top
/// bar, as Mac apps have them.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly ContentControl _page = new();
    private readonly Dictionary<string, ToggleButton> _tabs = new();
    private string _current = "Capture";
    private bool _recording; // the Capture page's Recording half
    private bool _pinWindows; // the Pins page's Windows half
    private static int _delay;
    private LibraryView? _library;
    private readonly TextBlock _status = new() { Classes = { "dim" }, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap };

    public MainWindow()
    {
        Title = "Overskin";
        Icon = App.Icon;
        Width = 540;
        Height = 720;
        MinWidth = 470;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (OperatingSystem.IsMacOS())
        {
            // the Mac's red, yellow and green buttons sit in Overskin's own top bar
            ExtendClientAreaToDecorationsHint = true;
            ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.PreferSystemChrome;
            ExtendClientAreaTitleBarHeightHint = 46;
        }

        Build();
        Theming.Changed += () => { _library = null; Build(); };
        PinWindow.ListChanged += () => { ShowStatus(); if (_current == "Pins") Go("Pins"); };

        // closing keeps Overskin running in the tray (the menu bar on a Mac), ready for the hotkeys
        Closing += (_, e) =>
        {
            App.Settings.Save();
            if (e.CloseReason == WindowCloseReason.WindowClosing && !e.IsProgrammatic)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    private void Build()
    {
        App.Current.ApplySeeThrough(this);
        if (_page.Parent is Decorator old) old.Child = null; // rebuilt for a new theme: move the page across

        var head = new StackPanel { Children = { TopBar(), UpdateBanner() } };
        var status = StatusBar();
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(status, Dock.Bottom);
        Content = new DockPanel { Children = { head, status, new Border { Child = _page } } };
        Go(_current);
    }

    // ---- the top bar -------------------------------------------------------------------------

    private Control TopBar()
    {
        bool mac = OperatingSystem.IsMacOS();
        var name = new StackPanel
        {
            Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
            Margin = new Thickness(mac ? 80 : 16, 0, 0, 0),
            Children =
            {
                Look.Mark(),
                new TextBlock { Text = "Overskin", FontSize = 13.5, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 0, 0, 1) },
                new Border { Width = 1, Height = 15, Background = Look.Brush(Colours.LineStrong), Margin = new Thickness(15, 1, 0, 0) },
            },
        };
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 0, -1) };
        _tabs.Clear();
        foreach (var page in new[] { "Capture", "Library", "Pins", "Settings" })
        {
            var t = new ToggleButton
            {
                Classes = { "tab" },
                Content = new Grid
                {
                    Children =
                    {
                        new TextBlock { Text = page, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 3) },
                        new Border { Classes = { "bar" }, Height = 2, VerticalAlignment = VerticalAlignment.Bottom, Background = Look.Brush(Colours.Accent), CornerRadius = new CornerRadius(2) },
                    },
                },
            };
            t.Click += (_, _) => Go(page);
            _tabs[page] = t;
            tabs.Children.Add(t);
        }
        var bar = new Border
        {
            Height = 46, Background = Brushes.Transparent, BorderBrush = Look.Brush(Colours.Line), BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new DockPanel { Children = { Docked(name, Dock.Left), tabs } },
        };
        // dragging the bar moves the window (the Mac's own title bar is hidden behind it)
        bar.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            if (e.ClickCount == 2 && mac) { WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; return; }
            BeginMoveDrag(e);
        };
        return bar;
    }

    private bool _updateHooked;

    /// <summary>"Overskin x.y is out": under the top bar when a newer version has been found, until "Not now".</summary>
    private Control UpdateBanner()
    {
        var title = new TextBlock { FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var detail = new TextBlock { Classes = { "dim" }, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                Look.Button("Get it", App.Current.OpenUpdatePage, Lucide.Download, "accent", "Opens the new version's page; your settings and captures stay"),
                Look.IconButton(Lucide.X, "Not now", App.Current.DismissUpdate, 12),
            },
        };
        var banner = new Border
        {
            Background = Look.Brush(Colours.AccentSoft), BorderBrush = Look.Brush(Colours.AccentLine), BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(14, 6, 8, 6),
            Child = new DockPanel
            {
                Children =
                {
                    Docked(new Border { Margin = new Thickness(0, 0, 10, 0), Child = Look.Icon(Lucide.Download, 15, Look.Brush(Colours.Accent)) }, Dock.Left),
                    Docked(buttons, Dock.Right),
                    new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { title, detail } },
                },
            },
        };
        banner.IsVisible = App.Current.ShowUpdateBanner;
        if (App.Current.UpdateAvailable is { } r)
        {
            title.Text = $"Overskin {r.Version.ToString(3)} is out";
            detail.Text = $"You have {Core.Updater.Current.ToString(3)}." + (OperatingSystem.IsMacOS() ? " Replace the app, then run the same Terminal line as the first time." : "");
        }
        if (!_updateHooked)
        {
            _updateHooked = true;
            App.Current.UpdateChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(Build);
        }
        return banner;
    }

    // ---- the status line ---------------------------------------------------------------------

    private Control StatusBar()
    {
        var version = new TextBlock { Text = "v" + Core.Updater.Current.ToString(3), Classes = { "faint" }, VerticalAlignment = VerticalAlignment.Center };
        var dot = new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(2), Background = Look.Brush(Colours.Accent), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        ShowStatus();
        if (_status.Parent is Panel was) was.Children.Remove(_status); // rebuilt for a new theme
        return new Border
        {
            Height = 28, BorderBrush = Look.Brush(Colours.Line), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(16, 0, 16, 0),
            Child = new DockPanel { Children = { Docked(version, Dock.Right), Docked(dot, Dock.Left), _status } },
        };
    }

    private void ShowStatus()
    {
        int pins = PinWindow.All.Count;
        _status.Text = pins == 0 ? "Ready" : pins == 1 ? "1 image pinned" : $"{pins} images pinned";
    }

    // ---- pages -------------------------------------------------------------------------------

    public void Go(string page)
    {
        if (page == "Record") { page = "Capture"; _recording = true; }
        _current = page;
        foreach (var (name, b) in _tabs) b.IsChecked = name == page;
        _page.Content = page switch
        {
            "Library" => new Border { Margin = new Thickness(14, 12, 14, 6), Child = _library ??= new LibraryView() },
            "Pins" => PinsPage(),
            "Settings" => SettingsPage(),
            _ => CapturePage(),
        };
    }

    private static ScrollViewer Scrolled(Control content) => new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Content = new Border { Margin = new Thickness(14, 12, 10, 8), Child = content },
    };

    private static string Keys(string hotkey) =>
        string.IsNullOrWhiteSpace(hotkey) || !OperatingSystem.IsMacOS() ? "" : Mac.Symbols(hotkey);

    // ---- Capture: screenshots and recording ---------------------------------------------------

    private Control CapturePage()
    {
        var stack = new StackPanel();
        stack.Children.Add(new Border
        {
            Margin = new Thickness(0, 0, 0, 12),
            Child = Look.Segs(new[] { "Screenshots", "Recording" }, _recording ? 1 : 0, i => { _recording = i == 1; Go("Capture"); },
                new[] { Lucide.Camera, Lucide.CircleDot }, save: false),
        });
        stack.Children.Add(_recording ? RecordingHalf() : ScreenshotsHalf());
        return Scrolled(stack);
    }

    private Control ScreenshotsHalf()
    {
        var s = App.Settings;
        var tiles = new UniformGrid { Columns = 4, Margin = new Thickness(0, 0, -8, 0) };
        tiles.Children.Add(Look.Tile(Lucide.SquareDashedMousePointer, "Region", Keys(s.HotkeyArea), () => _ = App.Current.CaptureAsync(true, _delay), "Drag over an area of the screen"));
        tiles.Children.Add(Look.Tile(Lucide.AppWindow, "Window", Keys(s.HotkeyWindow), () => _ = App.Current.ActiveWindowAsync(null, _delay), "The window in front"));
        tiles.Children.Add(Look.Tile(Lucide.Monitor, "Full screen", Keys(s.HotkeyScreen), () => _ = App.Current.CaptureAsync(false, _delay), "The screen the pointer is on"));
        tiles.Children.Add(Look.Tile(Lucide.ArrowUpDown, "Scrolling", Keys(s.HotkeyScroll), () => _ = App.Current.ScrollCaptureAsync(),
            "Overskin scrolls the page and joins everything into one tall picture"));

        var delays = new[] { 0, 3, 5, 10 };
        var delay = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new Border { Margin = new Thickness(0, 0, 8, 0), Child = Look.Icon(Lucide.Timer, 14, Look.Brush(Colours.Dim)) },
                new TextBlock { Text = "Delay", Classes = { "label" }, Margin = new Thickness(0, 0, 9, 0) },
                Look.Segs(new[] { "Off", "3 s", "5 s", "10 s" }, Math.Max(0, Array.IndexOf(delays, _delay)), i => _delay = delays[i], save: false),
            },
        };
        var same = Look.Button("Same area", () => _ = App.Current.RepeatRegionAsync(null, _delay), Lucide.Repeat, tip: "Capture the same area as last time");
        same.MinHeight = 25;
        same.Height = 25;
        var delayRow = new DockPanel { Margin = new Thickness(1, 2, 0, 14), Children = { Docked(same, Dock.Right), delay } };

        var tools = new UniformGrid { Columns = 2, Margin = new Thickness(-4, -2, -4, -4) };
        tools.Children.Add(Look.ToolRow(Lucide.Pin, "Snip and pin", () => _ = App.Current.SnipAndPinAsync(), "Keep part of the screen on top of everything"));
        tools.Children.Add(Look.ToolRow(Lucide.ScanText, "Text from screen", () => _ = App.Current.TextFromScreenAsync(),
            "Drag over text to copy it. Drag over a QR code or barcode to copy what it says."));
        tools.Children.Add(Look.ToolRow(Lucide.Pipette, "Colour picker", () => _ = App.Current.ScreenToolAsync(ScreenTool.Colour)));
        tools.Children.Add(Look.ToolRow(Lucide.Ruler, "Ruler", () => _ = App.Current.ScreenToolAsync(ScreenTool.Ruler), "Drag to measure. Esc closes."));
        tools.Children.Add(Look.ToolRow(Lucide.FolderOpen, "Open an image", () => _ = App.Current.OpenPictureAsync(pin: false)));
        tools.Children.Add(Look.ToolRow(Lucide.ClipboardPaste, "Edit the clipboard", () => _ = App.Current.EditClipboardAsync()));
        tools.Children.Add(Look.ToolRow(Lucide.Pin, "Pin the clipboard", () => _ = App.Current.PinClipboardAsync(), "Pin the copied picture on top of everything"));
        tools.Children.Add(Look.ToolRow(Lucide.ListOrdered, "Record steps", () => _ = Steps.ToggleAsync(),
            Steps.SeesClicks ? "Every click becomes a step of a how-to guide" : "A how-to guide, a key press per step"));

        var stack = new StackPanel
        {
            Children =
            {
                tiles, delayRow,
                Look.Card(Lucide.Zap, "Screen tools", tools),
                ShootingCard(), AfterCard(), WatermarkCard(),
            },
        };
        return stack;
    }

    private Control ShootingCard()
    {
        var s = App.Settings;
        return Look.Fold(Lucide.Crosshair, "Taking screenshots", () =>
            {
                var parts = new List<string>();
                if (s.Magnifier) parts.Add("Magnifier");
                if (s.Crosshair) parts.Add("Crosshair");
                if (s.Tighten) parts.Add("Fit box");
                if (s.IncludePointer) parts.Add("Pointer");
                return parts.Count == 0 ? "Off" : string.Join(" · ", parts);
            },
            Look.Switch("Magnifier", s.Magnifier, v => s.Magnifier = v, "The zoomed-in square by the pointer while you choose. M switches it on the capture bar too"),
            Look.Switch("Crosshair lines", s.Crosshair, v => s.Crosshair = v, "Lines right across the screen through the pointer, for lining up edges"),
            Look.Switch("Fit the box to what's inside", s.Tighten, v => s.Tighten = v,
                "Drag a rough box and its plain edges are trimmed, so it fits what's inside. T switches it on the capture bar too"),
            Look.Switch("Include the mouse pointer", s.IncludePointer, v => s.IncludePointer = v));
    }

    private static readonly string[] AfterKeys = { "editor", "notice", "nothing", "viewer", "preview" };
    private static readonly string[] AfterNames = { "Open the editor", "Show a notification", "Do nothing", "Open the viewer", "Show a preview card" };

    private Control AfterCard()
    {
        var s = App.Settings;
        var folder = new TextBlock { Text = s.SaveFolder, Classes = { "dim" }, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var change = Look.Button("Change", async () =>
        {
            var chosen = await Ui.PickFolderAsync(this, "Where should captures go?");
            if (chosen == null) return;
            s.SaveFolder = chosen;
            s.Save();
            folder.Text = chosen;
            App.Current.NotifyLibraryChanged();
        });
        change.Height = 25;
        change.MinHeight = 25;
        var folderRow = new DockPanel
        {
            Height = 33,
            Children =
            {
                Docked(change, Dock.Right),
                new Border
                {
                    Background = Look.Brush(Colours.Bg), BorderBrush = Look.Brush(Colours.Line), BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6, 1, 6, 1), Padding = new Thickness(9, 0), Height = 26, Margin = new Thickness(0, 0, 8, 0),
                    Child = new DockPanel { Children = { Docked(new Border { Margin = new Thickness(0, 0, 7, 0), Child = Look.Icon(Lucide.Folder, 13, Look.Brush(Colours.Faint)) }, Dock.Left), folder } },
                },
            },
        };

        var pattern = new TextBox { Text = s.FileNamePattern, Height = 26, MinHeight = 26 };
        var example = new TextBlock { Text = "Example: " + Core.FileNames.Example(s.FileNamePattern), Classes = { "faint" }, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 2) };
        pattern.TextChanged += (_, _) => example.Text = "Example: " + Core.FileNames.Example(pattern.Text ?? "");
        pattern.LostFocus += (_, _) => { s.FileNamePattern = string.IsNullOrWhiteSpace(pattern.Text) ? Core.FileNames.Default : pattern.Text!; s.Save(); };
        var reset = Look.IconButton(Lucide.RotateCcw, "Back to the usual names", () => { pattern.Text = Core.FileNames.Default; s.FileNamePattern = Core.FileNames.Default; s.Save(); }, 13);
        reset.Margin = new Thickness(4, 0, 0, 0);
        var nameRow = new DockPanel
        {
            Margin = new Thickness(0, 4, 0, 2),
            Children = { Docked(new TextBlock { Text = "File names", Classes = { "label" }, Margin = new Thickness(0, 0, 12, 0) }, Dock.Left), Docked(reset, Dock.Right), pattern },
        };
        ToolTip.SetTip(nameRow, "How saved captures are named. Click a word below to add it. A / makes a folder, such as {app}/{date} {time}");
        var tokens = new WrapPanel { ItemSpacing = 4, LineSpacing = 4, Margin = new Thickness(0, 2, 0, 0) };
        foreach (var (token, meaning) in Core.FileNames.Tokens)
        {
            var b = new ToggleButton { Content = token, Classes = { "seg" }, MinWidth = 0, Padding = new Thickness(8, 0) };
            ToolTip.SetTip(b, meaning);
            b.Click += (_, _) =>
            {
                b.IsChecked = false;
                pattern.Text = (pattern.Text ?? "").TrimEnd() + " " + token;
                s.FileNamePattern = pattern.Text;
                s.Save();
            };
            tokens.Children.Add(b);
        }

        var autoHide = Look.Switch("Hide private details automatically", s.AutoHide, v => s.AutoHide = v,
            "Reads the text in every screenshot and hides what's private before it's copied or saved. Right-click to choose which details." +
            (Ocr.IsAvailable ? "" : " Needs Tesseract."));
        autoHide.ContextMenu = PrivateMenu.Build();

        return Look.Fold(Lucide.Camera, "After capturing", () =>
            {
                var parts = new List<string>();
                if (s.CopyToClipboard) parts.Add("Copy");
                if (s.SaveAutomatically) parts.Add("Save PNG");
                int then = Array.IndexOf(AfterKeys, s.AfterCapture);
                if (then is 0) parts.Add("Editor");
                else if (then is 3) parts.Add("Viewer");
                else if (then is 4) parts.Add("Preview");
                if (s.AutoHide) parts.Add("Hide private");
                return parts.Count == 0 ? "Nothing" : string.Join(" · ", parts);
            },
            Look.Switch("Copy to the clipboard", s.CopyToClipboard, v => s.CopyToClipboard = v),
            Look.Switch("Save to a file", s.SaveAutomatically, v => s.SaveAutomatically = v),
            folderRow, nameRow, tokens, example,
            Look.Row("Then", Look.Choice(AfterNames, Math.Max(0, Array.IndexOf(AfterKeys, s.AfterCapture)), i => s.AfterCapture = AfterKeys[i])),
            autoHide,
            Look.Switch("Text from screen: just copy it", s.TextJustCopy, v => s.TextJustCopy = v, "Copies the text without opening a window"));
    }

    /// <summary>The watermark: a name, logo or handle stamped on every screenshot.</summary>
    private Control WatermarkCard()
    {
        var w = App.Settings.Watermark;
        var text = new TextBox { Text = w.Text, Watermark = "Your name, @handle or website", Width = 210 };
        text.LostFocus += (_, _) => { w.Text = text.Text ?? ""; App.Settings.Save(); };
        var logoName = new TextBlock { Text = string.IsNullOrEmpty(w.ImagePath) ? "None" : Path.GetFileName(w.ImagePath), Classes = { "dim" }, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 150, TextTrimming = TextTrimming.CharacterEllipsis };
        var pickLogo = Look.Button("Choose…", async () =>
        {
            var file = await Ui.PickPictureAsync(this);
            if (file == null) return;
            w.ImagePath = file;
            w.Kind = "Image";
            logoName.Text = Path.GetFileName(file);
            App.Settings.Save();
            Look.NoteChanged();
        });
        var positions = Core.WatermarkOptions.Positions;
        return Look.Fold(Lucide.Stamp, "Watermark", () => w.OnScreenshots ? "On screenshots" : "Off",
            Look.Switch("Stamp it on every screenshot", w.OnScreenshots, v => w.OnScreenshots = v),
            Look.Row("What", Look.Segs(new[] { "Text", "Logo" }, w.Kind == "Image" ? 1 : 0, i => w.Kind = i == 1 ? "Image" : "Text")),
            Look.Row("Text", text),
            Look.Row("Logo", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { logoName, pickLogo } }),
            Look.Row("Where", Look.Choice(positions.Select(Core.WatermarkOptions.PositionName).ToArray(), Math.Max(0, Array.IndexOf(positions, w.Position)), i => w.Position = positions[i])),
            Look.Row("Size", Look.Slider(2, 30, w.Size, v => w.Size = v)),
            Look.Row("Opacity", Look.Slider(10, 100, w.Opacity, v => w.Opacity = v)),
            Look.Row("Colour", Look.Segs(new[] { "White", "Black" }, w.Color == "Black" ? 1 : 0, i => w.Color = i == 1 ? "Black" : "White")),
            Look.Switch("Outline only (text)", w.Outline, v => w.Outline = v),
            Look.Row("Tilt (tiled)", Look.Slider(-90, 90, w.TileAngle, v => w.TileAngle = v)),
            Look.Row("Spacing (tiled)", Look.Slider(25, 300, w.TileSpacing, v => w.TileSpacing = v)),
            Look.Switch("Shift every other row (tiled)", w.TileStagger, v => w.TileStagger = v));
    }

    private static readonly string[] Corners = { "BottomRight", "BottomLeft", "TopRight", "TopLeft" };
    private static readonly string[] CornerNames = { "Bottom right", "Bottom left", "Top right", "Top left" };

    private Control RecordingHalf()
    {
        var s = App.Settings;
        var tiles = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, -8, 0) };
        tiles.Children.Add(Look.Tile(Lucide.CircleDot, "Video", Keys(s.HotkeyRecord), () => _ = App.Current.RecordAsync(false, 0), "Record an area as an MP4 (again to stop)"));
        tiles.Children.Add(Look.Tile(Lucide.Film, "GIF", Keys(s.HotkeyGif), () => _ = App.Current.RecordAsync(true, 0), "Record an area as a GIF (again to stop)"));
        tiles.Children.Add(Look.Tile(Lucide.ListOrdered, "Steps", null, () => _ = Steps.ToggleAsync(),
            Steps.SeesClicks ? "Every click becomes a step of a how-to guide" : "A how-to guide, a key press per step"));

        int[] stops = { 0, 15, 30, 60, 300 };
        string[] stopNames = { "Never", "15 seconds", "30 seconds", "1 minute", "5 minutes" };
        double[] lingers = { 2.0, 3.0, 5.0, 8.0 };
        var startStop = Look.Fold(Lucide.Timer, "Start and stop", () =>
            {
                var t = s.CountdownFirst ? "Count 3, 2, 1" : "Start straight away";
                int i = Array.IndexOf(stops, s.RecordStopAfter);
                return i > 0 ? t + " · Stops after " + stopNames[i].Replace(" seconds", " s") : t;
            },
            Look.Switch("Count 3, 2, 1 before recording", s.CountdownFirst, v => s.CountdownFirst = v),
            Look.Row("Stop by itself after", Look.Choice(stopNames, Array.IndexOf(stops, s.RecordStopAfter), i => s.RecordStopAfter = stops[i])),
            Look.Switch("Marks drawn while recording fade away", s.PenFades, v => s.PenFades = v,
                "Draw with the pen on the record bar (or its hotkey). Off: they stay until the pen is turned off."),
            Look.Row("They fade after", Look.Choice(new[] { "2 seconds", "3 seconds", "5 seconds", "8 seconds" }, Array.IndexOf(lingers, s.PenLinger), i => s.PenLinger = lingers[i])));

        int[] replaySeconds = { 15, 30, 60, 120 };
        var replay = Look.Fold(Lucide.RefreshCw, "Instant replay", () => s.ReplayOn ? $"Last {(s.ReplaySeconds >= 60 ? s.ReplaySeconds / 60 + " min" : s.ReplaySeconds + " s")}" : "Off",
            Look.Switch("Keep recording the main screen in the background", s.ReplayOn, v => { s.ReplayOn = v; _ = App.Current.StartReplayAsync(); },
                "The hotkey (or the menu) then saves what just happened. Needs ffmpeg."),
            Look.Row("Keeps the last", Look.Choice(new[] { "15 seconds", "30 seconds", "1 minute", "2 minutes" }, Array.IndexOf(replaySeconds, s.ReplaySeconds),
                i => { s.ReplaySeconds = replaySeconds[i]; _ = App.Current.StartReplayAsync(); })),
            Look.Switch("With the computer's sound", s.ReplaySound, v => { s.ReplaySound = v; _ = App.Current.StartReplayAsync(); },
                OperatingSystem.IsMacOS() ? "On a Mac this needs a loopback device such as BlackHole (free)." : "Through PulseAudio or PipeWire."),
            Look.Note("Save it with " + (Keys(s.HotkeyReplay) is { Length: > 0 } k ? k : "its hotkey") + " or from the menu."));

        int[] gifFps = { 10, 15, 20, 25 }, gifWidths = { 480, 720, 960, 0 };
        var gif = Look.Fold(Lucide.Images, "GIF", () => $"{s.GifFps} fps",
            Look.Row("Frame rate", Look.Segs(gifFps.Select(f => $"{f}").ToArray(), Array.IndexOf(gifFps, s.GifFps), i => s.GifFps = gifFps[i])),
            Look.Row("Size", Look.Choice(new[] { "Up to 480 wide", "Up to 720 wide", "Up to 960 wide", "As recorded" }, Array.IndexOf(gifWidths, s.GifMaxWidth), i => s.GifMaxWidth = gifWidths[i])));

        int[] fps = { 24, 30, 60 };
        var video = Look.Fold(Lucide.Film, "Video", () =>
            {
                var t = $"{s.RecordFps} fps";
                if (s.RecordBackdrop != "None" && !string.IsNullOrEmpty(s.RecordBackdrop)) t += " · Backdrop";
                if (s.ShowKeys) t += " · Shortcuts";
                return t;
            },
            Look.Row("Frame rate", Look.Segs(fps.Select(f => $"{f}").ToArray(), Array.IndexOf(fps, s.RecordFps), i => s.RecordFps = fps[i])),
            Look.Row("Backdrop", Look.Choice(Beautify.Backgrounds.Select(b => b.Name).ToArray(), Array.FindIndex(Beautify.Backgrounds, b => b.Name == s.RecordBackdrop),
                i => s.RecordBackdrop = Beautify.Backgrounds[i].Name)),
            Look.Switch(OperatingSystem.IsMacOS() ? "Show shortcuts you press (⌘ C, ⌥ ⇥, Return…)" : "Show shortcuts you press (a Mac only)", s.ShowKeys, v => s.ShowKeys = v,
                "As labels near the bottom. Only shortcuts and keys like Return or Esc, never ordinary typing. macOS asks once for Input Monitoring."));

        var (mic, cam) = DeviceChoices();
        var sound = Look.Fold(Lucide.Volume2, "Sound", () => (s.RecordSystemSound, s.RecordMic) switch
            {
                (true, true) => "System sound and microphone",
                (true, false) => "System sound",
                (false, true) => "Microphone",
                _ => "Silent",
            },
            Look.Switch("What the computer plays", s.RecordSystemSound, v => s.RecordSystemSound = v,
                OperatingSystem.IsMacOS() ? "macOS only lets apps hear the Mac's own sound through a loopback device: install BlackHole (free), and ffmpeg." : "Through PulseAudio or PipeWire, with ffmpeg."),
            Look.Switch("The microphone", s.RecordMic, v => s.RecordMic = v),
            mic);

        var webcam = Look.Fold(Lucide.Video, "Webcam", () => s.Webcam ? $"{CornerNames[Math.Max(0, Array.IndexOf(Corners, s.WebcamCorner))]} · {(s.WebcamShape == "Rounded" ? "Rounded" : "Circle")}" : "Off",
            Look.Switch("Webcam in a bubble in the corner", s.Webcam, v => s.Webcam = v, "Needs ffmpeg."),
            Look.Row("Corner", Look.Choice(CornerNames, Array.IndexOf(Corners, s.WebcamCorner), i => s.WebcamCorner = Corners[i])),
            Look.Row("Shape", Look.Segs(new[] { "Circle", "Rounded" }, s.WebcamShape == "Rounded" ? 1 : 0, i => s.WebcamShape = i == 1 ? "Rounded" : "Circle")),
            Look.Row("Size", Look.Slider(12, 45, s.WebcamSize, v => s.WebcamSize = v)),
            cam);

        double[] sizes = { 1.0, 1.5, 2.0, 2.5 };
        var pointer = Look.Fold(Lucide.MousePointer, "Pointer", () =>
            {
                if (!s.RecordPointer) return "Hidden";
                var parts = new List<string> { s.PointerStyle };
                if (s.SmoothPointer) parts.Add("Smooth");
                if (s.ClickRings) parts.Add("Rings");
                if (s.DimAroundPointer) parts.Add("Dim around");
                return string.Join(" · ", parts);
            },
            Look.Switch("Show the mouse pointer", s.RecordPointer, v => s.RecordPointer = v),
            Look.Switch("Smooth pointer: Overskin draws a sharp one that glides", s.SmoothPointer, v => s.SmoothPointer = v,
                "In place of the system's pointer; stays sharp however far it zooms. Needs ffmpeg, and the desktop to say where the pointer is."),
            Look.Row("Pointer", Look.Choice(RecordingLook.PointerStyles, Array.IndexOf(RecordingLook.PointerStyles, s.PointerStyle), i => s.PointerStyle = RecordingLook.PointerStyles[i])),
            Look.Row("Size", Look.Segs(new[] { "1×", "1.5×", "2×", "2.5×" }, Array.IndexOf(sizes, s.PointerSize), i => s.PointerSize = sizes[i])),
            Look.Row("Smoothing", Look.Slider(0, 100, s.PointerSmoothing, v => s.PointerSmoothing = v)),
            Look.Row("Motion blur", Look.Slider(0, 100, s.MotionBlur, v => s.MotionBlur = v)),
            Look.Switch("Rings where you click", s.ClickRings, v => s.ClickRings = v),
            Look.Switch("Dim around the pointer", s.DimAroundPointer, v => s.DimAroundPointer = v,
                "Darkens the video a little except round the pointer, so viewers' eyes follow it. Needs ffmpeg."));

        double[] levels = { 1.5, 1.8, 2.2 }, holds = { 1.5, 2.5, 4.0, 6.0 };
        var zoom = Look.Fold(Lucide.ZoomIn, "Auto-zoom", () => s.AutoZoom ? $"{s.ZoomLevel:0.0}×{(s.EditableZooms ? " · Editable" : "")}" : "Off",
            Look.Switch("Glide in on clicks and follow the pointer", s.AutoZoom, v => s.AutoZoom = v,
                ScreenGrabber.IsWayland ? "On Wayland Overskin can't see clicks, so it zooms in where the pointer comes to rest (Hyprland only)." : "Zooms in on each click, follows the pointer, eases back out."),
            Look.Row("Zoom", Look.Segs(new[] { "1.5×", "1.8×", "2.2×" }, Array.IndexOf(levels, s.ZoomLevel), i => s.ZoomLevel = levels[i])),
            Look.Switch("Editable zooms (change them in the video editor)", s.EditableZooms, v => s.EditableZooms = v),
            Look.Row("Zoom speed", Look.Slider(0, 100, s.ZoomSpeed, v => s.ZoomSpeed = v)),
            Look.Row("Follow smoothing", Look.Slider(0, 100, s.FollowSmoothing, v => s.FollowSmoothing = v)),
            Look.Row("Stays zoomed for", Look.Choice(new[] { "1.5 seconds", "2.5 seconds", "4 seconds", "6 seconds" }, Array.IndexOf(holds, s.ZoomHold), i => s.ZoomHold = holds[i])));

        var stack = new StackPanel { Children = { tiles, new Border { Height = 10 }, startStop, replay, gif, video, sound, webcam, pointer, zoom } };
        if (Recorder.Missing(gif: true) is { } missing)
            stack.Children.Add(Look.Card(Lucide.Info, "Before recording", Look.Note(missing)));
        return stack;
    }

    /// <summary>Which microphone and webcam (on a Mac, from what ffmpeg can see).</summary>
    private static (Control Mic, Control Cam) DeviceChoices()
    {
        var s = App.Settings;
        var mic = new ComboBox { Width = 190, ItemsSource = new[] { "The first one there is" }, SelectedIndex = 0 };
        var cam = new ComboBox { Width = 190, ItemsSource = new[] { "The first one there is" }, SelectedIndex = 0 };
        bool show = OperatingSystem.IsMacOS() && ScreenGrabber.HasCommand("ffmpeg");
        var micRow = Look.Row("Microphone", mic);
        var camRow = Look.Row("Camera", cam);
        micRow.IsVisible = camRow.IsVisible = show;
        if (show) _ = Fill();
        return (micRow, camRow);

        async Task Fill()
        {
            var list = await Devices.MacAsync();
            var mics = new[] { "The first one there is" }.Concat(list.Audio.Select(d => d.Name).Where(n => !System.Text.RegularExpressions.Regex.IsMatch(n, "blackhole|loopback|soundflower", System.Text.RegularExpressions.RegexOptions.IgnoreCase))).ToList();
            var cams = new[] { "The first one there is" }.Concat(list.Video.Select(d => d.Name).Where(n => !n.StartsWith("Capture screen", StringComparison.OrdinalIgnoreCase))).ToList();
            mic.ItemsSource = mics;
            cam.ItemsSource = cams;
            mic.SelectedIndex = Math.Max(0, mics.IndexOf(s.MicrophoneName));
            cam.SelectedIndex = Math.Max(0, cams.IndexOf(s.WebcamName));
            mic.SelectionChanged += (_, _) => { s.MicrophoneName = mic.SelectedIndex > 0 ? mics[mic.SelectedIndex] : ""; s.Save(); };
            cam.SelectionChanged += (_, _) => { s.WebcamName = cam.SelectedIndex > 0 ? cams[cam.SelectedIndex] : ""; s.Save(); };
        }
    }

    // ---- Pins: pictures and windows -----------------------------------------------------------

    private Control PinsPage()
    {
        var stack = new StackPanel();
        stack.Children.Add(new Border
        {
            Margin = new Thickness(0, 0, 0, 10),
            Child = Look.Segs(new[] { "Pictures", "Windows" }, _pinWindows ? 1 : 0, i => { _pinWindows = i == 1; Go("Pins"); },
                new[] { Lucide.Images, Lucide.AppWindow }, save: false),
        });

        var pinned = PinWindow.All.Where(p => p.IsLive == _pinWindows).ToList();
        Control head;
        if (!_pinWindows)
        {
            var browse = Look.Button("Browse", () => _ = App.Current.OpenPictureAsync(pin: true), Lucide.FolderOpen, "accent");
            head = new Border
            {
                Classes = { "card" }, Padding = new Thickness(14, 12),
                Child = new DockPanel
                {
                    Children =
                    {
                        Docked(BigIcon(Lucide.ImagePlus), Dock.Left),
                        Docked(new StackPanel
                        {
                            Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
                            Children =
                            {
                                Look.Button("Paste", () => _ = App.Current.PinClipboardAsync(), Lucide.ClipboardPaste, tip: "Pin the copied picture"),
                                Look.Button("Snip", () => _ = App.Current.SnipAndPinAsync(), Lucide.Scissors, tip: "Pin part of the screen"),
                                browse,
                            },
                        }, Dock.Right),
                        new TextBlock { Text = "Pin an image", Classes = { "heading" }, Margin = new Thickness(14, 0, 8, 0) },
                    },
                },
            };
        }
        else
        {
            head = new Border
            {
                Classes = { "card" }, Padding = new Thickness(14, 12),
                Child = new DockPanel
                {
                    Children =
                    {
                        Docked(BigIcon(Lucide.AppWindow), Dock.Left),
                        Docked(Look.Button("Pick a window", () => _ = App.Current.PinWindowAsync(), Lucide.MousePointerClick, "accent",
                            "Click a window: its pin keeps showing what's in it, live"), Dock.Right),
                        new StackPanel
                        {
                            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 8, 0),
                            Children = { new TextBlock { Text = "Pin a window", Classes = { "heading" } }, new TextBlock { Text = "A live copy of it, on top of everything", Classes = { "dim" } } },
                        },
                    },
                },
            };
        }
        stack.Children.Add(head);

        stack.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(1, 8, 0, 8),
            Children =
            {
                new Border { Margin = new Thickness(0, 0, 8, 0), Child = Look.Icon(Lucide.Pin, 14, Look.Brush(Colours.Dim)) },
                new TextBlock { Text = "Pinned", Classes = { "heading" } },
                new TextBlock { Text = pinned.Count.ToString(), Classes = { "faint" }, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center },
            },
        });
        if (pinned.Count == 0)
            stack.Children.Add(new TextBlock { Text = _pinWindows ? "No windows pinned." : "Nothing pinned yet.", Classes = { "dim" }, Margin = new Thickness(1, 0, 0, 8) });
        foreach (var pin in pinned) stack.Children.Add(PinRow(pin));

        stack.Children.Add(new Border { Height = 1, Background = Look.Brush(Colours.Line), Margin = new Thickness(0, 10, 0, 12) });
        bool mac = OperatingSystem.IsMacOS();
        string cmd = mac ? "⌘" : "Ctrl+";
        var keys = new (string Key, string What)[]
        {
            ("Scroll", "Resize"), (cmd + "Scroll", "Opacity"),
            ("Click", "Move, resize, turn"), ("Right-click", "Menu"),
            (cmd + "C", "Copy"), (cmd + "T", "The resize box"),
            ("Arrows", "Nudge"), ("Esc", "Close the image"),
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,*") };
        for (int i = 0; i < keys.Length; i++)
        {
            if (i % 2 == 0) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var key = Look.Key(keys[i].Key);
            key.Margin = new Thickness(0, 0, 14, 6);
            var what = new TextBlock { Text = keys[i].What, Classes = { "dim" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 6) };
            Grid.SetRow(key, i / 2); Grid.SetColumn(key, i % 2 * 2);
            Grid.SetRow(what, i / 2); Grid.SetColumn(what, i % 2 * 2 + 1);
            grid.Children.Add(key);
            grid.Children.Add(what);
        }
        stack.Children.Add(grid);
        return Scrolled(stack);
    }

    private static Border BigIcon(string icon) => new()
    {
        Width = 40, Height = 40, Background = Look.Brush(Colours.AccentSoft), BorderBrush = Look.Brush(Colours.AccentLine), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(9, 2, 9, 2), Child = Look.Icon(icon, 20, Look.Brush(Colours.Accent)),
    };

    private Control PinRow(PinWindow pin)
    {
        var thumb = new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(6, 1, 6, 1), ClipToBounds = true, BorderBrush = Look.Brush(Colours.Line), BorderThickness = new Thickness(1),
            Child = new Image { Source = pin.Shown, Stretch = Stretch.UniformToFill },
        };
        string name = pin.SourcePath is { } p ? Path.GetFileName(p) : pin.IsLive ? pin.Title ?? "Window" : "Picture";
        var through = new OnOff(pin.ClickThrough);
        through.Toggled += v => pin.ClickThrough = v;
        var close = Look.IconButton(Lucide.X, "Close the image", pin.Close, 13);
        return new Border
        {
            Classes = { "card" }, Padding = new Thickness(10, 8),
            Child = new DockPanel
            {
                Children =
                {
                    Docked(thumb, Dock.Left),
                    Docked(new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center,
                        Children = { new TextBlock { Text = "Click-through", Classes = { "dim" }, VerticalAlignment = VerticalAlignment.Center }, through, close },
                    }, Dock.Right),
                    new StackPanel
                    {
                        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0),
                        Children =
                        {
                            new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12.5 },
                            new TextBlock { Text = $"{pin.PictureSize.Width} × {pin.PictureSize.Height}  ·  {pin.ZoomPercent}%", Classes = { "dim" }, FontSize = 11 },
                        },
                    },
                },
            },
        };
    }

    // ---- Settings ----------------------------------------------------------------------------

    private Control SettingsPage()
    {
        var s = App.Settings;
        void Reapply() { s.Save(); Theming.Apply(Theming.Effective(s)); }

        // appearance: accent, mode, theme, match the Mac, see-through
        var accents = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        foreach (var hex in Theming.Accents)
        {
            bool chosen = string.Equals(s.AccentColor, hex, StringComparison.OrdinalIgnoreCase) && !s.MatchSystem;
            var swatch = new Border
            {
                Width = 26, Height = 26, Padding = new Thickness(2.5), BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(9, 2, 9, 2),
                BorderBrush = chosen ? Look.Brush(Colours.Text) : Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand),
                Child = new Border { Background = new SolidColorBrush(Color.Parse(hex)), CornerRadius = new CornerRadius(6, 1, 6, 1), BorderBrush = Look.Brush(Colours.LineStrong), BorderThickness = new Thickness(1) },
            };
            swatch.PointerPressed += (_, _) => { s.AccentColor = hex; s.MatchSystem = false; Reapply(); };
            accents.Children.Add(swatch);
        }
        var wheel = new ColourWheel { Width = 160, Height = 160 };
        wheel.ColourChanged += () => { s.AccentColor = ColourWheel.ToHex(ColourWheel.FromHsv(wheel.Hue, wheel.Saturation, 0.95)); s.MatchSystem = false; s.Save(); };
        var any = new Border
        {
            Width = 26, Height = 26, Padding = new Thickness(3), Cursor = new Cursor(StandardCursorType.Hand),
            Child = new Border { CornerRadius = new CornerRadius(10), Background = new ImageBrush(ColourWheel.CreateBitmap(48)) { Stretch = Stretch.UniformToFill } },
        };
        ToolTip.SetTip(any, "Any colour");
        var anyFlyout = new Flyout { Content = wheel };
        anyFlyout.Closed += (_, _) => Theming.Apply(Theming.Effective(s));
        any.PointerPressed += (_, _) => anyFlyout.ShowAt(any);
        accents.Children.Add(any);
        var hexBox = new TextBox { Text = s.AccentColor.ToUpperInvariant(), Width = 88, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        hexBox.LostFocus += (_, _) =>
        {
            var t = (hexBox.Text ?? "").Trim();
            if (!t.StartsWith('#')) t = "#" + t;
            if (Color.TryParse(t, out var c) && t.Length == 7) { s.AccentColor = ColourWheel.ToHex(c); s.MatchSystem = false; Reapply(); }
            else hexBox.Text = s.AccentColor.ToUpperInvariant();
        };
        accents.Children.Add(hexBox);

        var dark = Theming.Themes.Where(t => !t.Light).Select(t => t.Name).ToList();
        var light = Theming.Themes.Where(t => t.Light).Select(t => t.Name).ToList();
        bool isLight = Theming.Find(s.Theme).Light;
        var list = isLight ? light : dark;
        var theme = Look.Choice(list, Math.Max(0, list.IndexOf(s.Theme)), i => { s.Theme = list[i]; Reapply(); }, 200);
        var mode = Look.Segs(new[] { "Dark", "Light" }, isLight ? 1 : 0, i =>
        {
            if ((i == 1) == Theming.Find(s.Theme).Light) return;
            s.Theme = i == 1 ? light[0] : dark[0];
            Reapply();
        });
        var appearance = Look.Card(Lucide.Palette, "Appearance",
            Look.Row("Accent", accents),
            Look.Row("Mode", mode),
            Look.Row("Theme", theme),
            Look.Switch(OperatingSystem.IsMacOS() ? "Match the Mac: light or dark, and its accent colour" : "Match the system: light or dark, and its accent colour",
                s.MatchSystem, v => { s.MatchSystem = v; Reapply(); }),
            Look.Switch("See-through window", s.SeeThrough, v => { s.SeeThrough = v; s.Save(); App.Current.ApplySeeThrough(); },
                OperatingSystem.IsMacOS() ? "The window's background shows a soft blur of what's behind it, as macOS's own windows can." : "Where the desktop can blur behind windows (KDE, Hyprland)."));

        var general = Look.Card(Lucide.Settings, "General",
            Look.Switch("Open this window when Overskin starts", s.ShowWindowOnStart, v => s.ShowWindowOnStart = v),
            Look.Switch(OperatingSystem.IsMacOS() ? "Start when you log in (in the menu bar)" : "Start with the desktop (in the tray)", Autostart.IsOn, v => Autostart.Set(v)),
            Look.Switch("Bring pins back when Overskin starts again", s.RestorePins, v => s.RestorePins = v, "Where they were, at the size, angle and see-through they had."),
            Look.Row("Move old captures to the trash", Look.Choice(new[] { "Never", "After a week", "After 30 days", "After 90 days", "After a year" },
                Array.IndexOf(new[] { 0, 7, 30, 90, 365 }, s.CleanUpDays), i => { s.CleanUpDays = new[] { 0, 7, 30, 90, 365 }[i]; _ = App.Current.CleanUpAsync(); }, 150),
                "Favourites are never moved. The trash keeps them until you empty it."),
            Look.Switch("Check for new versions now and then", s.CheckForUpdates, v => s.CheckForUpdates = v, "Only asks GitHub for the latest release; nothing else is sent."));

        var about = Look.Card(Lucide.Info, "About",
            Look.Row($"Overskin {Core.Updater.Current.ToString(3)} for {(OperatingSystem.IsMacOS() ? "macOS" : "Linux")}",
                Look.Button("Check now", () => _ = App.Current.CheckForUpdateAsync(quietly: false), Lucide.RefreshCw)),
            new TextBlock { Text = App.Current.UpdateStatus, Classes = { "dim" }, Margin = new Thickness(0, 0, 0, 6) },
            Look.Row("Something not right?", Look.Button("Copy details for a bug report", () => _ = Desktop.CopyTextAsync(
                $"Overskin {typeof(App).Assembly.GetName().Version} · {Environment.OSVersion} · {Core.Log.Environment_()}\n\n{Core.Log.Tail()}", this), Lucide.Copy)),
            Look.Row("Close Overskin completely", Look.Button("Quit", App.Current.Quit, Lucide.Power)));

        var shortcuts = OperatingSystem.IsMacOS() ? MacHotkeys() : LinuxShortcuts();
        var workflows = WorkflowsCard();

        // a search box over the settings: only the cards and rows that mention every word typed stay
        var search = new TextBox
        {
            Watermark = "Search settings", Margin = new Thickness(0, 0, 0, 10), Height = 30,
            InnerLeftContent = new Border { Margin = new Thickness(9, 0, 0, 0), Child = Look.Icon(Lucide.Search, 14, Look.Brush(Colours.Dim)) },
        };
        var cards = new Control[] { appearance, shortcuts, workflows, general, about };
        search.TextChanged += (_, _) => FilterSettings(cards, search.Text ?? "");
        var stack = new StackPanel { Children = { search } };
        foreach (var c in cards) stack.Children.Add(c);
        return Scrolled(stack);
    }

    /// <summary>The Mac's hotkeys, grouped as on Windows: each typed as Ctrl+Alt+S (Alt is ⌥ Option, Cmd is ⌘).</summary>
    private Control MacHotkeys()
    {
        var status = new TextBlock { Classes = { "dim" } };
        var groups = new (string Title, string Icon, List<Control> Rows)[]
        {
            ("Capture", Lucide.Camera, new()), ("Recording", Lucide.CircleDot, new()), ("Tools", Lucide.Zap, new()),
        };
        foreach (var (name, get, set, _) in App.Current.Hotkeys())
        {
            var box = new TextBox { Text = get(), Width = 140, HorizontalContentAlignment = HorizontalAlignment.Center, FontWeight = FontWeight.SemiBold };
            var shown = new TextBlock { Text = Keys(get()), Classes = { "dim" }, VerticalAlignment = VerticalAlignment.Center, MinWidth = 46, TextAlignment = TextAlignment.Right };
            box.LostFocus += (_, _) =>
            {
                var text = (box.Text ?? "").Trim();
                if (text.Length > 0 && !Mac.TryParseHotKey(text, out _, out _))
                {
                    status.Text = $"\"{text}\" isn't a hotkey Overskin understands. Try Ctrl+Alt+S, Cmd+Shift+2 or Ctrl+Alt+F5.";
                    box.Text = get();
                    return;
                }
                if (text == get()) return;
                set(text);
                App.Settings.Save();
                shown.Text = Keys(text);
                var taken = App.Current.RegisterHotkeys();
                status.Text = taken.Count == 0 ? "Saved." : "Another app already uses: " + string.Join(", ", taken);
            };
            var row = Look.Row(name, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { shown, box } });
            string n = name.ToLowerInvariant();
            int group = n.Contains("record") || n.Contains("replay") || n.Contains("pause") || n.Contains("draw") ? 1
                : n.Contains("capture") || n.Contains("snip") || n.Contains("scroll") || n.Contains("recent") ? 0 : 2;
            groups[group].Rows.Add(row);
        }
        var rows = new List<Control> { Look.Note("They work from any app. Type each as Ctrl+Alt+S (Alt is the ⌥ Option key, Cmd is ⌘); leave one empty to turn it off.") };
        foreach (var (title, icon, list) in groups)
        {
            if (list.Count == 0) continue;
            rows.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 2),
                Children =
                {
                    new Border { Margin = new Thickness(0, 0, 8, 0), Child = Look.Icon(icon, 14, Look.Brush(Colours.Accent)) },
                    new TextBlock { Text = title, Classes = { "dim" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Center },
                },
            });
            rows.AddRange(list);
        }
        rows.Add(status);
        return Look.Card(Lucide.Keyboard, "Hotkeys", rows.ToArray());
    }

    private Control LinuxShortcuts()
    {
        const string command = "overskin --capture";
        return Look.Card(Lucide.Keyboard, "Keyboard shortcuts",
            Look.Note("Linux desktops keep global shortcuts to themselves, so add them in your system's keyboard settings " +
                      "(GNOME: Settings → Keyboard → Custom Shortcuts; KDE: System Settings → Shortcuts; Hyprland: bind = , Print, exec, overskin --capture). Each runs a command:"),
            new DockPanel
            {
                Children =
                {
                    Docked(Look.Button("Copy", () => _ = Desktop.CopyTextAsync(command, this), Lucide.Copy), Dock.Right, new Thickness(8, 0, 0, 0)),
                    new TextBox { Text = command, IsReadOnly = true, FontFamily = new FontFamily("monospace") },
                },
            },
            Look.Note("--capture an area · --screen the whole screen · --delay 3 wait first · --record a video · --gif a GIF · --stop the recording · --pin FILE · --open FILE"));
    }

    /// <summary>Workflows: a hotkey for a whole routine (capture, hide, beautify, watermark, copy, pin…), as on Windows.</summary>
    private Control WorkflowsCard()
    {
        var rows = new StackPanel { Spacing = 6 };
        var status = new TextBlock { Classes = { "dim" } };
        void Changed()
        {
            App.Settings.Save();
            Fill();
            App.Current.RebuildTrayMenu();
            if (OperatingSystem.IsMacOS())
            {
                var taken = App.Current.RegisterHotkeys();
                status.Text = taken.Count == 0 ? "" : "Another app already uses: " + string.Join(", ", taken);
            }
        }
        async Task EditAsync(Overskin.Workflow start, Overskin.Workflow? editing)
        {
            var dialog = new WorkflowWindow(start, editing);
            await dialog.ShowDialog(this);
            if (dialog.Result is not { } made) return;
            int i = editing != null ? App.Settings.Workflows.IndexOf(editing) : -1;
            if (i >= 0) App.Settings.Workflows[i] = made;
            else App.Settings.Workflows.Add(made);
            Changed();
        }
        void Fill()
        {
            rows.Children.Clear();
            if (App.Settings.Workflows.Count == 0)
                rows.Children.Add(Look.Note("None yet. A workflow is a hotkey for a whole routine: say, capture the window in front, hide private details, add the watermark and copy it."));
            foreach (var w in App.Settings.Workflows.ToList())
            {
                var text = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock { Text = w.Title, FontWeight = FontWeight.SemiBold },
                        new TextBlock { Text = w.Summary + (w.Hotkey.Length > 0 ? "   ·   " + (OperatingSystem.IsMacOS() ? Mac.Symbols(w.Hotkey) : w.Hotkey) : ""), Classes = { "dim" }, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap },
                    },
                };
                var buttons = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        Look.IconButton(Lucide.Play, "Run it now", () => App.Current.RunWorkflow(w)),
                        Look.IconButton(Lucide.Pencil, "Change it", () => _ = EditAsync(w, w)),
                        Look.IconButton(Lucide.Trash, "Delete it", () => { App.Settings.Workflows.Remove(w); Changed(); }),
                    },
                };
                rows.Children.Add(new DockPanel { MinHeight = 34, Children = { Docked(buttons, Dock.Right), text } });
            }
        }
        Fill();
        var add = Look.Button("Add a workflow", () => _ = EditAsync(new Overskin.Workflow { Name = $"Workflow {App.Settings.Workflows.Count + 1}", AutoHide = true }, null), Lucide.Plus);
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Margin = new Thickness(0, 6, 0, 0);
        return Look.Card(Lucide.Workflow, "Workflows",
            Look.Note("A hotkey for a whole routine: what to capture, then any mix of hiding private details, beautifying, the watermark, saving, copying, pinning and editing." +
                      (OperatingSystem.IsMacOS() ? "" : " On Linux, each runs with  overskin --workflow \"its name\"  (give it a key in your desktop's keyboard settings).")),
            rows, add, status);
    }

    /// <summary>
    /// Shows only the settings that mention every word of the search: whole cards whose heading
    /// matches, otherwise just the matching rows (with the heading) inside them.
    /// </summary>
    public static int FilterSettings(IEnumerable<Control> cards, string query)
    {
        var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int shown = 0;
        foreach (var card in cards)
        {
            var rows = (card as Decorator)?.Child is StackPanel panel ? panel.Children.ToList() : new List<Control>();
            bool Matches(Control c) => words.All(TextOf(c).ToLowerInvariant().Contains);
            bool heading = rows.Count > 0 && Matches(rows[0]);
            bool any = false;
            for (int i = 0; i < rows.Count; i++)
            {
                bool show = words.Length == 0 || heading || i == 0 || Matches(rows[i]);
                rows[i].IsVisible = show;
                if (i > 0 && show && words.Length > 0) any = true;
            }
            card.IsVisible = words.Length == 0 || heading || any;
            if (card.IsVisible) shown++;
        }
        return shown;
    }

    /// <summary>All the words a control shows: labels, check boxes, buttons, choices and tips.</summary>
    private static string TextOf(Control c)
    {
        var parts = new List<string>();
        void Walk(object? o)
        {
            switch (o)
            {
                case TextBlock t: parts.Add(t.Text ?? ""); break;
                case TextBox box: parts.Add(box.Watermark?.ToString() ?? ""); break;
                case ContentControl cc when cc.Content is string s: parts.Add(s); break;
                case ComboBox combo when combo.ItemsSource != null: foreach (var item in combo.ItemsSource) parts.Add(item?.ToString() ?? ""); break;
            }
            if (o is Control ctl && ToolTip.GetTip(ctl) is string tip) parts.Add(tip);
            if (o is Avalonia.LogicalTree.ILogical l) foreach (var child in l.LogicalChildren) Walk(child);
        }
        Walk(c);
        return string.Join(" ", parts);
    }

    private static Control Docked(Control c, Dock dock, Thickness? margin = null)
    {
        DockPanel.SetDock(c, dock);
        if (margin is { } m) c.Margin = m;
        return c;
    }
}

/// <summary>Starting with the desktop: a file in ~/.config/autostart, or a launch agent on a Mac.</summary>
public static class Autostart
{
    private static string FilePath => OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", "app.overskin.plist")
        : Path.Combine(Core.Settings.ConfigFolder, "..", "autostart", "overskin.desktop");

    public static bool IsOn => File.Exists(FilePath);

    public static void Set(bool on)
    {
        try
        {
            if (!on) { File.Delete(FilePath); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            if (OperatingSystem.IsMacOS())
            {
                // a launch agent: macOS starts it at login, in the menu bar
                var exe = System.Security.SecurityElement.Escape(Environment.ProcessPath ?? "");
                File.WriteAllText(FilePath,
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                    "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
                    "<plist version=\"1.0\"><dict>\n" +
                    "  <key>Label</key><string>app.overskin</string>\n" +
                    $"  <key>ProgramArguments</key><array><string>{exe}</string><string>--tray</string></array>\n" +
                    "  <key>RunAtLoad</key><true/>\n" +
                    "  <key>ProcessType</key><string>Interactive</string>\n" +
                    "</dict></plist>\n");
                return;
            }
            File.WriteAllText(FilePath,
                "[Desktop Entry]\nType=Application\nName=Overskin\nComment=Screenshots, done right\n" +
                $"Exec=\"{Environment.ProcessPath}\" --tray\nIcon=overskin\nTerminal=false\nX-GNOME-Autostart-enabled=true\n");
        }
        catch (Exception ex) { Core.Log.Write("autostart: " + ex.Message); }
    }
}
