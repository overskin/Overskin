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
    public static Color Raised { get; set; } = Color.Parse("#262930");
    public static Color Text { get; set; } = Color.Parse("#E7E8EB");
    public static Color Dim { get; set; } = Color.Parse("#9094A0");
    public static Color Accent { get; set; } = Color.Parse("#4F8CFF");

    public static void AddStyles(Application app) =>
        app.Styles.Add(new StyleInclude(new Uri("avares://overskin/"))
        {
            Source = new Uri("avares://overskin/Interface/Theme.axaml"),
        });
}

/// <summary>Simple filled icons on a 24 × 24 grid.</summary>
public static class Icons
{
    public const string Area = "M3,3 h6 v2 h-4 v4 h-2 z M15,3 h6 v6 h-2 v-4 h-4 z M3,15 h2 v4 h4 v2 h-6 z M19,15 h2 v6 h-6 v-2 h4 z";
    public const string Screen = "M2,4 h20 v13 h-20 z M4,6 v9 h16 v-9 z M8,19 h8 v2 h-8 z";
    public const string Timer = "M12,4 A9,9 0 1 1 11.99,4 z M12,6 A7,7 0 1 0 12.01,6 z M11,8 h2 v5 h-2 z M10,1 h4 v2 h-4 z";
    public const string Folder = "M2,5 h7 l2,2 h11 v13 h-20 z";
    public const string Picture = "M3,4 h18 v16 h-18 z M5,6 v12 h14 v-12 z M6,16 l4,-5 l3,3 l2,-2 l3,4 z";
    public const string Pin = "M14,2 l8,8 l-2,1 l-3,-1 l-4,4 l1,4 l-1.5,1.5 l-4,-4 l-5,5 l-1,-1 l5,-5 l-4,-4 l1.5,-1.5 l4,1 l4,-4 l-1,-3 z";
    public const string Pen = "M3,17 l11,-11 l4,4 l-11,11 h-4 z M15,5 l2,-2 l4,4 l-2,2 z";
    public const string Highlighter = "M4,20 h16 v2 h-16 z M7,16 l9,-13 l4,3 l-9,13 h-4 z";
    public const string Arrow = "M4,18.5 l11,-11 h-5 v-2.5 h9.5 v9.5 h-2.5 v-5 l-11,11 z";
    public const string Rectangle = "M3,5 h18 v14 h-18 z M5,7 v10 h14 v-10 z";
    public const string Ellipse = "M12,4 A9,8 0 1 1 11.99,4 z M12,6 A7,6 0 1 0 12.01,6 z";
    public const string Text = "M4,4 h16 v4 h-2 v-2 h-5 v12 h2 v2 h-6 v-2 h2 v-12 h-5 v2 h-2 z";
    public const string Pixelate = "M3,3 h6 v6 h-6 z M9,9 h6 v6 h-6 z M15,3 h6 v6 h-6 z M3,15 h6 v6 h-6 z M15,15 h6 v6 h-6 z";
    public const string Counter = "M12,2 A10,10 0 1 1 11.99,2 z M11,7 h2 v10 h-2 z M9,8 l2,-1.5 v2.2 l-2,1 z";
    public const string Undo = "M8,5 v-3 l-6,5 l6,5 v-3 h6 a5,5 0 0 1 0,10 h-6 v3 h6 a8,8 0 0 0 0,-16 z";
    public const string Redo = "M16,5 v-3 l6,5 l-6,5 v-3 h-6 a5,5 0 0 0 0,10 h6 v3 h-6 a8,8 0 0 1 0,-16 z";
    public const string Copy = "M8,2 h12 v14 h-12 z M10,4 v10 h8 v-10 z M4,6 h2 v14 h10 v2 h-12 z";
    public const string Save = "M4,3 h13 l4,4 v14 h-17 z M7,5 v4 h9 v-4 z M12,12 a3,3 0 1 0 0.01,0 z";
    public const string Line = "M4,18.6 l14.6,-14.6 l1.4,1.4 l-14.6,14.6 z";
    public const string Eraser = "M15,3 l6,6 l-10,10 h-6 l-3,-3 z M14,6 l-8,8 l2,2 h2.5 l6.5,-6.5 z M11,21 h10 v-2 h-8 z";
    public const string Crop = "M6,2 h2 v14 h14 v2 h-4 v4 h-2 v-4 h-10 z M10,6 h8 v8 h-2 v-6 h-6 z M2,6 h4 v2 h-4 z";
    public const string Record = "M12,4 A8,8 0 1 1 11.99,4 z";
    public const string Stop = "M6,6 h12 v12 h-12 z";
    public const string Library = "M3,3 h8 v8 h-8 z M13,3 h8 v8 h-8 z M3,13 h8 v8 h-8 z M13,13 h8 v8 h-8 z";
    public const string Gear = "M10,2 h4 l0.6,2.6 l2.2,0.9 l2.3,-1.4 l2.8,2.8 l-1.4,2.3 l0.9,2.2 l2.6,0.6 v4 l-2.6,0.6 l-0.9,2.2 l1.4,2.3 l-2.8,2.8 l-2.3,-1.4 l-2.2,0.9 l-0.6,2.6 h-4 l-0.6,-2.6 l-2.2,-0.9 l-2.3,1.4 l-2.8,-2.8 l1.4,-2.3 l-0.9,-2.2 l-2.6,-0.6 v-4 l2.6,-0.6 l0.9,-2.2 l-1.4,-2.3 l2.8,-2.8 l2.3,1.4 l2.2,-0.9 z M12,8.5 a3.5,3.5 0 1 0 0.01,0 z";
    public const string Window = "M2,4 h20 v16 h-20 z M4,8 v10 h16 v-10 z";
    public const string Scroll = "M6,2 h12 v20 h-12 z M8,4 v16 h8 v-16 z M12,6 l3,3 h-6 z M12,18 l3,-3 h-6 z";
    public const string Steps = "M3,4 h4 v4 h-4 z M10,5 h11 v2 h-11 z M3,10 h4 v4 h-4 z M10,11 h11 v2 h-11 z M3,16 h4 v4 h-4 z M10,17 h11 v2 h-11 z";
    public const string Close = "M5,6.5 l1.5,-1.5 l5.5,5.5 l5.5,-5.5 l1.5,1.5 l-5.5,5.5 l5.5,5.5 l-1.5,1.5 l-5.5,-5.5 l-5.5,5.5 l-1.5,-1.5 l5.5,-5.5 z";
}

public static class Ui
{
    public static PathIcon Icon(string data, double size = 18) =>
        new() { Data = StreamGeometry.Parse(data), Width = size, Height = size };

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
                Children = { Icon(icon, 16), new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } },
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
