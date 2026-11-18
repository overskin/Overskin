using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Overskin.Linux.Platform;

namespace Overskin.Linux.Interface;

/// <summary>The home window: Capture, Record, Library and Settings pages.</summary>
public sealed class MainWindow : Window
{
    private readonly ContentControl _page = new();
    private readonly Dictionary<string, ToggleButton> _nav = new();
    private string _current = "Capture";
    private LibraryView? _library;
    private TracePage? _trace;

    public MainWindow()
    {
        Title = "Overskin";
        Icon = App.Icon;
        Width = 980;
        Height = 700;
        MinWidth = 720;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        Build();
        Theming.Changed += () => { _library = null; _trace = null; Build(); };

        // closing keeps Overskin running in the tray, ready for the shortcut
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
        var logo = new Image { Width = 30, Height = 30, Source = new Bitmap(AssetLoader.Open(new Uri("avares://overskin/Assets/overskin.png"))) };
        var nav = new StackPanel
        {
            Width = 190,
            Spacing = 4,
            Margin = new Thickness(14, 16, 8, 16),
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(6, 0, 0, 18),
                    Children = { logo, new TextBlock { Text = "Overskin", FontSize = 18, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center } },
                },
            },
        };
        _nav.Clear();
        foreach (var (name, icon) in new[] { ("Capture", Icons.Area), ("Record", Icons.Record), ("Library", Icons.Library), ("Trace", Icons.Pen), ("Settings", Icons.Gear) })
        {
            var b = new ToggleButton
            {
                Classes = { "nav" },
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { Ui.Icon(icon, 16), new TextBlock { Text = name } } },
            };
            b.Click += (_, _) => Go(name);
            _nav[name] = b;
            nav.Children.Add(b);
        }

        if (_page.Parent is Decorator old) old.Child = null; // rebuilt for a new theme: move the page across
        var side = new Border { Background = new SolidColorBrush(Colours.Panel), Child = nav };
        DockPanel.SetDock(side, Dock.Left);
        Content = new DockPanel { Children = { side, new Border { Padding = new Thickness(26, 22, 26, 20), Child = _page } } };
        Go(_current);
    }

    public void Go(string page)
    {
        _current = page;
        foreach (var (name, b) in _nav) b.IsChecked = name == page;
        _page.Content = page switch
        {
            "Record" => RecordPage(),
            "Library" => _library ??= new LibraryView(),
            "Trace" => _trace ??= new TracePage(),
            "Settings" => SettingsPage(),
            _ => CapturePage(),
        };
    }

    private static Control Page(string title, string subtitle, params Control[] parts)
    {
        var stack = new StackPanel { Spacing = 16 };
        stack.Children.Add(new StackPanel { Children = { Ui.Label(title, "h1"), Ui.Label(subtitle, "dim") } });
        foreach (var p in parts) stack.Children.Add(p);
        return new ScrollViewer { Content = stack };
    }

    private Control CapturePage()
    {
        var s = App.Settings;
        var tiles = new UniformGrid { Columns = 3, ColumnSpacing = 10, RowSpacing = 10 };
        tiles.Children.Add(Tile(Icons.Area, "Capture area", "Drag out any part of the screen", () => _ = App.Current.CaptureAsync(true, 0), primary: true));
        tiles.Children.Add(Tile(Icons.Screen, "Capture screen", "The whole screen", () => _ = App.Current.CaptureAsync(false, 0)));
        tiles.Children.Add(Tile(Icons.Timer, "In 3 seconds", "Time to open a menu first", () => _ = App.Current.CaptureAsync(true, 3)));
        tiles.Children.Add(Tile(Icons.Picture, "Open a picture", "Mark up any image", () => _ = App.Current.OpenPictureAsync(pin: false)));
        tiles.Children.Add(Tile(Icons.Pin, "Pin a picture", "Keep it on top of everything", () => _ = App.Current.OpenPictureAsync(pin: true)));
        tiles.Children.Add(Tile(Icons.Folder, "Screenshots folder", "Everything you've saved", App.Current.OpenFolder));
        tiles.Children.Add(Tile(Icons.Scroll, "Scrolling capture", "A whole long page in one picture", () => _ = App.Current.ScrollCaptureAsync()));
        tiles.Children.Add(Tile(Icons.Steps, "Record steps", Steps.SeesClicks ? "Every click becomes a how-to step" : "A how-to guide, a key press per step", () => _ = Steps.ToggleAsync()));
        tiles.Children.Add(Tile(Icons.Library, "Recent captures", "Copy or drag the latest ones", () => new RecentStrip().Show()));

        var after = new ComboBox
        {
            ItemsSource = new[] { "Open the editor", "Show a notification", "Do nothing" },
            SelectedIndex = s.AfterCapture switch { "notice" => 1, "nothing" => 2, _ => 0 },
            MinWidth = 200,
        };
        after.SelectionChanged += (_, _) =>
        {
            s.AfterCapture = after.SelectedIndex switch { 1 => "notice", 2 => "nothing", _ => "editor" };
            s.Save();
        };

        var autoHide = Check("Hide private details automatically", s.AutoHide, v => s.AutoHide = v,
            "Reads the text in every screenshot and hides what's private before it's copied or saved. Right-click to choose which details." +
            (Ocr.IsAvailable ? "" : " Needs Tesseract."));
        autoHide.ContextMenu = PrivateMenu.Build();

        var options = Ui.Card(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Ui.Label("After a capture", "h2"),
                Check("Save it automatically", s.SaveAutomatically, v => s.SaveAutomatically = v),
                Check("Copy it to the clipboard", s.CopyToClipboard, v => s.CopyToClipboard = v),
                autoHide,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { Ui.Label("Then"), after } },
            },
        });
        return Page("Capture", "Screenshots, done right.", tiles, options);
    }

    private Control RecordPage()
    {
        var s = App.Settings;
        var tiles = new UniformGrid { Columns = 3, ColumnSpacing = 10, RowSpacing = 10 };
        tiles.Children.Add(Tile(Icons.Record, "Record a video", "MP4, sharp and small", () => _ = App.Current.RecordAsync(false, 0), primary: true));
        tiles.Children.Add(Tile(Icons.Picture, "Record a GIF", "Plays anywhere, loops", () => _ = App.Current.RecordAsync(true, 0)));
        tiles.Children.Add(Tile(Icons.Stop, "Stop recording", "Or use the bar, or overskin --stop", () => _ = App.Current.StopRecordingAsync(keep: true)));

        ComboBox Pick(string[] items, int selected, Action<int> set)
        {
            var c = new ComboBox { ItemsSource = items, SelectedIndex = Math.Max(0, selected), MinWidth = 160 };
            c.SelectionChanged += (_, _) => { set(c.SelectedIndex); s.Save(); };
            return c;
        }
        int[] fps = { 24, 30, 60 }, gifFps = { 10, 15, 20, 25 }, gifWidths = { 480, 720, 960, 0 };
        var options = Ui.Card(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Ui.Label("Recording", "h2"),
                Check("Show the mouse pointer", s.RecordPointer, v => s.RecordPointer = v),
                Check("Auto-zoom: glide in on clicks and follow the pointer (videos)", s.AutoZoom, v => s.AutoZoom = v,
                    ScreenGrabber.IsWayland ? "On Wayland Overskin can't see clicks, so it zooms in where the pointer comes to rest (Hyprland only)." : "Zooms in on each click, follows the pointer, eases back out."),
                Row("Zoom", Pick(new[] { "A little (1.5×)", "Medium (1.8×)", "Close (2.2×)" }, Array.IndexOf(new[] { 1.5, 1.8, 2.2 }, s.ZoomLevel), i => s.ZoomLevel = new[] { 1.5, 1.8, 2.2 }[i])),
                Row("Video frame rate", Pick(fps.Select(f => $"{f} frames a second").ToArray(), Array.IndexOf(fps, s.RecordFps), i => s.RecordFps = fps[i])),
                Row("GIF frame rate", Pick(gifFps.Select(f => $"{f} frames a second").ToArray(), Array.IndexOf(gifFps, s.GifFps), i => s.GifFps = gifFps[i])),
                Row("GIF size", Pick(new[] { "Up to 480 wide", "Up to 720 wide", "Up to 960 wide", "As recorded" }, Array.IndexOf(gifWidths, s.GifMaxWidth), i => s.GifMaxWidth = gifWidths[i])),
            },
        });

        var how = !OperatingSystem.IsLinux() ? "Recording runs on Linux."
            : !ScreenGrabber.IsWayland ? "On X11 Overskin records with ffmpeg."
            : ScreenGrabber.IsWlroots ? "On this desktop Overskin records with wf-recorder."
            : "On this desktop the system asks which screen or window to share (once), and Overskin records it with GStreamer.";
        var missing = Recorder.Missing(gif: true);
        var info = Ui.Card(new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Ui.Label("How it records here", "h2"),
                Ui.Label(how + " GIFs are made from the video with ffmpeg.", "dim"),
                Ui.Label(missing ?? "Everything it needs is installed.", "dim"),
                Ui.Label("Keyboard shortcuts: bind overskin --record, overskin --gif and overskin --stop in your system's keyboard settings.", "dim"),
            },
        });
        return Page("Record", "Videos and GIFs of any area.", tiles, options, info);
    }

    private Control SettingsPage()
    {
        var s = App.Settings;
        var folder = new TextBox { Text = s.SaveFolder, IsReadOnly = true };
        var browse = Ui.Button("Change…", async () =>
        {
            var chosen = await Ui.PickFolderAsync(this, "Where should screenshots go?");
            if (chosen == null) return;
            s.SaveFolder = chosen;
            s.Save();
            folder.Text = chosen;
            App.Current.NotifyLibraryChanged();
        });
        browse.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(browse, Dock.Right);

        var dark = Theming.Themes.Where(t => !t.Light).Select(t => t.Name).ToList();
        var light = Theming.Themes.Where(t => t.Light).Select(t => t.Name).ToList();
        var mode = new ComboBox { ItemsSource = new[] { "Dark", "Light" }, SelectedIndex = Theming.Find(s.Theme).Light ? 1 : 0, MinWidth = 120 };
        var theme = new ComboBox { MinWidth = 220 };
        void FillThemes()
        {
            var list = mode.SelectedIndex == 1 ? light : dark;
            theme.ItemsSource = list;
            theme.SelectedIndex = Math.Max(0, list.IndexOf(s.Theme));
        }
        FillThemes();
        mode.SelectionChanged += (_, _) =>
        {
            FillThemes();
            if (theme.SelectedItem is string name && name != s.Theme) { s.Theme = name; s.Save(); Theming.Apply(name); }
        };
        theme.SelectionChanged += (_, _) =>
        {
            if (theme.SelectedItem is not string name || name == s.Theme) return;
            s.Theme = name;
            s.Save();
            Theming.Apply(name);
        };

        var pattern = new TextBox { Text = s.FileNamePattern, MinWidth = 300 };
        pattern.LostFocus += (_, _) => { s.FileNamePattern = pattern.Text ?? s.FileNamePattern; s.Save(); };

        var general = Ui.Card(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Ui.Label("General", "h2"),
                Ui.Label("Save screenshots and recordings to", "dim"),
                new DockPanel { Children = { browse, folder } },
                Row("File names", pattern),
                Ui.Label("{0:yyyy-MM-dd_HH-mm-ss} is the date and time.", "dim"),
                Check("Open this window when Overskin starts", s.ShowWindowOnStart, v => s.ShowWindowOnStart = v),
                Check("Start Overskin with the desktop (in the tray)", Autostart.IsOn, v => Autostart.Set(v)),
            },
        });
        var look = Ui.Card(new StackPanel
        {
            Spacing = 10,
            Children = { Ui.Label("Look", "h2"), Row("Mode", mode), Row("Theme", theme) },
        });

        const string command = "overskin --capture";
        var shortcuts = Ui.Card(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Ui.Label("Keyboard shortcuts", "h2"),
                Ui.Label("Linux desktops keep global shortcuts to themselves, so add them in your system's keyboard settings " +
                         "(GNOME: Settings → Keyboard → Custom Shortcuts; KDE: System Settings → Shortcuts; Hyprland: bind = , Print, exec, overskin --capture). Each runs a command:", "dim"),
                new DockPanel
                {
                    Children =
                    {
                        Docked(Ui.Button("Copy", () => _ = Desktop.CopyTextAsync(command, this)), Dock.Right, new Thickness(8, 0, 0, 0)),
                        new TextBox { Text = command, IsReadOnly = true, FontFamily = new FontFamily("monospace") },
                    },
                },
                Ui.Label("--capture an area · --screen the whole screen · --delay 3 wait first · --record a video · --gif a GIF · --stop the recording · --pin FILE · --open FILE", "dim"),
            },
        });

        var about = new DockPanel
        {
            Children =
            {
                Docked(new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        Ui.Button("Copy details for a bug report", () => _ = Desktop.CopyTextAsync(
                            $"Overskin {typeof(App).Assembly.GetName().Version} · {Environment.OSVersion} · {Core.Log.Environment_()}\n\n{Core.Log.Tail()}", this)),
                        Ui.Button("Quit Overskin", App.Current.Quit),
                    },
                }, Dock.Right),
                Ui.Label($"Version {typeof(App).Assembly.GetName().Version?.ToString(2)} for Linux  ·  {ScreenGrabber.DesktopName}", "dim"),
            },
        };
        return Page("Settings", "Where things go, how it looks, and shortcuts.", general, look, shortcuts, about);
    }

    private static Control Row(string label, Control control) =>
        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { new TextBlock { Text = label, MinWidth = 130, VerticalAlignment = VerticalAlignment.Center }, control } };

    private static Control Docked(Control c, Dock dock, Thickness? margin = null)
    {
        DockPanel.SetDock(c, dock);
        if (margin is { } m) c.Margin = m;
        return c;
    }

    private static Button Tile(string icon, string title, string body, Action click, bool primary = false)
    {
        var b = new Button
        {
            Classes = { "tile" },
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new Border
                    {
                        Width = 36, Height = 36, CornerRadius = new CornerRadius(10, 2, 10, 2),
                        Background = new SolidColorBrush(primary ? Colours.Accent : Colours.Raised),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Child = new PathIcon { Data = StreamGeometry.Parse(icon), Width = 18, Height = 18, Foreground = primary ? Brushes.White : new SolidColorBrush(Colours.Text) },
                    },
                    new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 14 },
                    new TextBlock { Text = body, Classes = { "dim" }, FontSize = 12, FontWeight = FontWeight.Normal },
                },
            },
        };
        b.Click += (_, _) => click();
        return b;
    }

    private static CheckBox Check(string text, bool value, Action<bool> set, string? tip = null)
    {
        var c = new CheckBox { Content = text, IsChecked = value };
        if (tip != null) ToolTip.SetTip(c, tip);
        c.IsCheckedChanged += (_, _) =>
        {
            set(c.IsChecked == true);
            App.Settings.Save();
        };
        return c;
    }
}

/// <summary>Starting with the desktop: a file in ~/.config/autostart.</summary>
public static class Autostart
{
    private static string FilePath => Path.Combine(Core.Settings.ConfigFolder, "..", "autostart", "overskin.desktop");

    public static bool IsOn => File.Exists(FilePath);

    public static void Set(bool on)
    {
        try
        {
            if (!on) { File.Delete(FilePath); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath,
                "[Desktop Entry]\nType=Application\nName=Overskin\nComment=Screenshots, done right\n" +
                $"Exec=\"{Environment.ProcessPath}\" --tray\nIcon=overskin\nTerminal=false\nX-GNOME-Autostart-enabled=true\n");
        }
        catch (Exception ex) { Core.Log.Write("autostart: " + ex.Message); }
    }
}
