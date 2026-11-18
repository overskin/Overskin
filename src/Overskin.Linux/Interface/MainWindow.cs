using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Overskin.Linux.Platform;

namespace Overskin.Linux.Interface;

/// <summary>The home window: the capture buttons, settings and how to set up a keyboard shortcut.</summary>
public sealed class MainWindow : Window
{
    private readonly TextBox _folder;

    public MainWindow()
    {
        var s = App.Settings;
        Title = "Overskin";
        Icon = App.Icon;
        Width = 720;
        Height = 700;
        MinWidth = 560;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // header
        var logo = new Image { Width = 40, Height = 40, Source = new Bitmap(AssetLoader.Open(new Uri("avares://overskin/Assets/overskin.png"))) };
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Children =
            {
                logo,
                new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Children = { Ui.Label("Overskin", "h1"), Ui.Label("Screenshots, done right.", "dim") },
                },
            },
        };

        // capture tiles
        var tiles = new UniformGrid { Columns = 3, ColumnSpacing = 10, RowSpacing = 10 };
        tiles.Children.Add(Tile(Icons.Area, "Capture area", "Drag out any part of the screen", () => _ = App.Current.CaptureAsync(true, 0), primary: true));
        tiles.Children.Add(Tile(Icons.Screen, "Capture screen", "Everything on every monitor", () => _ = App.Current.CaptureAsync(false, 0)));
        tiles.Children.Add(Tile(Icons.Timer, "In 3 seconds", "Time to open a menu first", () => _ = App.Current.CaptureAsync(true, 3)));
        tiles.Children.Add(Tile(Icons.Picture, "Open a picture", "Mark up any image", () => _ = App.Current.OpenPictureAsync(pin: false)));
        tiles.Children.Add(Tile(Icons.Pin, "Pin a picture", "Keep it on top of everything", () => _ = App.Current.OpenPictureAsync(pin: true)));
        tiles.Children.Add(Tile(Icons.Folder, "Screenshots folder", "Everything you've saved", App.Current.OpenFolder));

        // settings
        _folder = new TextBox { Text = s.SaveFolder, IsReadOnly = true };
        var browse = Ui.Button("Change…", () => _ = ChangeFolderAsync());
        var folderRow = new DockPanel { Children = { browse, _folder } };
        DockPanel.SetDock(browse, Dock.Right);
        browse.Margin = new Thickness(8, 0, 0, 0);

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

        var settings = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Ui.Label("Settings", "h2"),
                Ui.Label("Save screenshots to", "dim"),
                folderRow,
                Check("Save every screenshot automatically", s.SaveAutomatically, v => s.SaveAutomatically = v),
                Check("Copy every screenshot to the clipboard", s.CopyToClipboard, v => s.CopyToClipboard = v),
                Check("Open this window when Overskin starts", s.ShowWindowOnStart, v => s.ShowWindowOnStart = v),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { Ui.Label("After a capture"), after } },
            },
        };

        // shortcut help
        const string command = "overskin --capture";
        var shortcut = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Ui.Label("Keyboard shortcut", "h2"),
                Ui.Label("Linux desktops keep global shortcuts to themselves, so add one in your system's keyboard settings " +
                         "(GNOME: Settings → Keyboard → Custom Shortcuts; KDE: System Settings → Shortcuts) that runs:", "dim"),
                new DockPanel
                {
                    Children =
                    {
                        Docked(Ui.Button("Copy", () => _ = Desktop.CopyTextAsync(command, this)), Dock.Right, new Thickness(8, 0, 0, 0)),
                        new TextBox { Text = command, IsReadOnly = true, FontFamily = new FontFamily("monospace") },
                    },
                },
                Ui.Label("Also: --screen for the whole screen, --delay 3 to wait first, --pin FILE to pin a picture.", "dim"),
            },
        };

        var footer = new DockPanel
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
                Ui.Label($"Version 0.2 for Linux  ·  {ScreenGrabber.DesktopName}", "dim"),
            },
        };

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(24, 20, 24, 20),
                Spacing = 16,
                Children = { header, tiles, Ui.Card(settings), Ui.Card(shortcut), footer },
            },
        };

        // closing keeps Overskin running in the tray, ready for the shortcut
        Closing += (_, e) =>
        {
            s.Save();
            if (e.CloseReason == WindowCloseReason.WindowClosing && !e.IsProgrammatic)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

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
                        Width = 36, Height = 36, CornerRadius = new CornerRadius(10),
                        Background = new SolidColorBrush(primary ? Colours.Accent : Colours.Raised),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Child = new PathIcon { Data = StreamGeometry.Parse(icon), Width = 18, Height = 18, Foreground = Brushes.White },
                    },
                    new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 14 },
                    new TextBlock { Text = body, Classes = { "dim" }, FontSize = 12, FontWeight = FontWeight.Normal },
                },
            },
        };
        b.Click += (_, _) => click();
        return b;
    }

    private static CheckBox Check(string text, bool value, Action<bool> set)
    {
        var c = new CheckBox { Content = text, IsChecked = value };
        c.IsCheckedChanged += (_, _) =>
        {
            set(c.IsChecked == true);
            App.Settings.Save();
        };
        return c;
    }

    private async Task ChangeFolderAsync()
    {
        var folder = await Ui.PickFolderAsync(this, "Where should screenshots go?");
        if (folder == null) return;
        App.Settings.SaveFolder = folder;
        App.Settings.Save();
        _folder.Text = folder;
    }
}
