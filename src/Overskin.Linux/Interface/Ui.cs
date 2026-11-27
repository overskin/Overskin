using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace Overskin.Linux.Interface;

public static class Colours
{
    public static Color Bg { get; set; } = Color.Parse("#121316");
    public static Color Panel { get; set; } = Color.Parse("#1B1D22");
    public static Color Raised { get; set; } = Color.Parse("#25282F");
    public static Color Text { get; set; } = Color.Parse("#E7E8EB");
    public static Color Dim { get; set; } = Color.Parse("#9094A0");
    public static Color Accent { get; set; } = Color.Parse("#4F8CFF");
    // the rest of the Windows app's colours, worked out from those as it does
    public static Color Field { get; set; } = Color.Parse("#25282F");
    public static Color FieldHover { get; set; } = Color.Parse("#2E323B");
    public static Color Line { get; set; } = Color.Parse("#2A2D35");
    public static Color LineStrong { get; set; } = Color.Parse("#3B3F4A");
    public static Color Faint { get; set; } = Color.Parse("#5E626E");
    public static Color AccentSoft { get; set; } = Color.Parse("#18223A");
    public static Color AccentLine { get; set; } = Color.Parse("#324E86");
    public static Color AccentInk { get; set; } = Color.Parse("#071530");

    public static void AddStyles(Application app) =>
        app.Styles.Add(new StyleInclude(new Uri("avares://overskin/"))
        {
            Source = new Uri("avares://overskin/Interface/Theme.axaml"),
        });
}

/// <summary>The icons by what they're for: the Windows app's outline icons (see <see cref="Lucide"/>).</summary>
public static class Icons
{
    public const string Area = Lucide.SquareDashedMousePointer;
    public const string Screen = Lucide.Monitor;
    public const string Timer = Lucide.Timer;
    public const string Folder = Lucide.FolderOpen;
    public const string Picture = Lucide.Image;
    public const string Pin = Lucide.Pin;
    public const string Select = Lucide.MousePointer2;
    public const string Move = Lucide.Move;
    public const string Play = Lucide.Play;
    public const string Pause = Lucide.Pause;
    public const string Volume = Lucide.Volume2;
    public const string Mute = Lucide.VolumeX;
    public const string Back = Lucide.ChevronLeft;
    public const string Forward = Lucide.ChevronRight;
    public const string ZoomIn = Lucide.ZoomIn;
    public const string ZoomOut = Lucide.ZoomOut;
    public const string FullScreen = Lucide.Maximize;
    public const string Wand = Lucide.Wand;
    public const string Phone = Lucide.Smartphone;
    public const string Trash = Lucide.Trash;
    public const string Pen = Lucide.Pencil;
    public const string Highlighter = Lucide.Highlighter;
    public const string Arrow = Lucide.ArrowUpRight;
    public const string Rectangle = Lucide.Square;
    public const string Ellipse = Lucide.Circle;
    public const string Text = Lucide.Type;
    public const string Pixelate = Lucide.Grid3x3;
    public const string Counter = Lucide.Hash;
    public const string Undo = Lucide.Undo2;
    public const string Redo = Lucide.Redo2;
    public const string Copy = Lucide.Copy;
    public const string Save = Lucide.Save;
    public const string Line = Lucide.Slash;
    public const string Eraser = Lucide.Eraser;
    public const string Spotlight = Lucide.Focus;
    public const string Crop = Lucide.Crop;
    public const string Record = Lucide.CircleDot;
    public const string Stop = Lucide.Square;
    public const string Library = Lucide.Images;
    public const string Gear = Lucide.Settings;
    public const string Window = Lucide.AppWindow;
    public const string Scroll = Lucide.ArrowUpDown;
    public const string Steps = Lucide.ListOrdered;
    public const string Close = Lucide.X;
}

public static class Ui
{
    public static LineIcon Icon(string data, double size = 18) =>
        new() { Data = LineIcon.Shape(data), Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };

    public static TextBlock Label(string text, string? cls = null)
    {
        var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        if (cls != null) t.Classes.Add(cls);
        return t;
    }

    public static Button Button(string text, Action click, string? icon = null, string? cls = null)
    {
        object content = text;
        if (icon != null)
        {
            content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { Icon(icon, 14), new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } },
            };
        }
        var b = new Button { Content = content };
        if (cls != null) b.Classes.Add(cls);
        b.Click += (_, _) => click();
        return b;
    }

    public static Button IconButton(string icon, string tip, Action click)
    {
        var b = new Button { Content = Icon(icon), Classes = { "icon" } };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    public static Border Card(Control child) => new() { Classes = { "card" }, Child = child };

    private static readonly FilePickerFileType Pictures = new("Pictures")
    {
        Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp" },
        MimeTypes = new[] { "image/png", "image/jpeg", "image/bmp", "image/gif", "image/webp" },
    };

    public static async Task<string?> PickPictureAsync(TopLevel owner)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a picture",
            FileTypeFilter = new[] { Pictures },
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public static async Task<string?> PickFolderAsync(TopLevel owner, string title)
    {
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public static async Task<string?> PickSavePathAsync(TopLevel owner, string suggestedName)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save the picture",
            SuggestedFileName = suggestedName,
            DefaultExtension = "png",
            FileTypeChoices = new[] { new FilePickerFileType("PNG picture") { Patterns = new[] { "*.png" } } },
        });
        return file?.TryGetLocalPath();
    }
}

/// <summary>A plain message with an OK button.</summary>
public sealed class MessageWindow : Window
{
    private MessageWindow(string title, string body)
    {
        Title = title;
        Icon = App.Icon;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        var ok = Ui.Button("OK", Close, cls: "primary");
        ok.HorizontalAlignment = HorizontalAlignment.Right;
        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                Ui.Label(title, "h2"),
                new SelectableTextBlock { Text = body, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Colours.Dim) },
                ok,
            },
        };
    }

    public static void Show(string title, string body) => new MessageWindow(title, body).Show();
}
