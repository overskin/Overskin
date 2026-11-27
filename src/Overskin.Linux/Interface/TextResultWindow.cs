using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Overskin.Linux.Core;
using Overskin.Linux.Platform;

namespace Overskin.Linux.Interface;

/// <summary>Text read from the screen (or what a QR code says), already copied, editable before copying again.</summary>
public sealed class TextResultWindow : Window
{
    public TextResultWindow(string text, bool qr = false)
    {
        Title = qr ? "QR code" : "Text from screen";
        Icon = App.Icon;
        Width = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var box = new TextBox
        {
            Text = text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MinHeight = 80, MaxHeight = 360, FontSize = 13.5,
        };
        var status = Ui.Label(qr ? "What the code says is on the clipboard." : "The text is on the clipboard. Fix anything, then copy it again.", "dim");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        if (qr && QrReader.LinkIn(text) is { } link)
            buttons.Children.Add(Ui.Button("Open the link", () => { Desktop.Open(link.AbsoluteUri); Close(); }, Icons.Picture));
        buttons.Children.Add(Ui.Button("Copy", async () =>
        {
            await Desktop.CopyTextAsync(box.Text ?? "", this);
            status.Text = "Copied.";
        }, Icons.Copy, "primary"));
        buttons.Children.Add(Ui.Button("Close", Close));

        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 12,
            Children = { Ui.Label(Title, "h2"), box, status, buttons },
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }
}
