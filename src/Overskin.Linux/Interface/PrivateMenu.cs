using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Overskin.Linux.Platform;
using Overskin.Shared;

namespace Overskin.Linux.Interface;

/// <summary>Auto-hide: reading the picture, and the right-click menu that picks what it hides.</summary>
public static class PrivateMenu
{
    public static async Task<List<PrivateDetail>> FindAsync(Bitmap picture)
    {
        var lines = await Ocr.ReadLinesAsync(picture);
        if (lines == null) return new();
        var words = lines.Select(l => (IReadOnlyList<TextWord>)l.Select(w => new TextWord(w.Text, w.Bounds.X, w.Bounds.Y, w.Bounds.Width, w.Bounds.Height)).ToList()).ToList();
        return PrivateDetails.Find(words, App.Settings.AutoHideKinds);
    }

    public static ContextMenu Build()
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "Hide these:", IsEnabled = false });
        var items = new List<(PrivateKinds Kind, MenuItem Item)>();
        foreach (var (kind, label) in PrivateDetails.Choices)
        {
            var item = new MenuItem { Header = label, ToggleType = MenuItemToggleType.CheckBox, StaysOpenOnClick = true };
            item.Click += (_, _) =>
            {
                var s = App.Settings;
                s.AutoHideKinds = (s.AutoHideKinds & kind) != 0 ? s.AutoHideKinds & ~kind : s.AutoHideKinds | kind;
                item.IsChecked = (s.AutoHideKinds & kind) != 0;
                s.Save();
            };
            items.Add((kind, item));
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        void Preset(string text, PrivateKinds kinds)
        {
            var m = new MenuItem { Header = text };
            m.Click += (_, _) => { App.Settings.AutoHideKinds = kinds; App.Settings.Save(); };
            menu.Items.Add(m);
        }
        Preset("Everything", PrivateKinds.All);
        Preset("Back to the defaults", PrivateKinds.Default);
        menu.Opening += (_, _) =>
        {
            foreach (var (kind, item) in items) item.IsChecked = (App.Settings.AutoHideKinds & kind) != 0;
        };
        return menu;
    }
}
