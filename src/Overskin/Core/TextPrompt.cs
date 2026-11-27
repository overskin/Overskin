using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Overskin;

/// <summary>A small box asking for one line of text, such as a new tag's name.</summary>
public sealed class TextPrompt : Window
{
    private readonly TextBox _box = new();

    private TextPrompt(string title, string label, string initial)
    {
        Title = title;
        Width = 360;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Panel");
        SetResourceReference(ForegroundProperty, "Text");
        FontFamily = (FontFamily)FindResource("UiFont");

        _box.Style = (Style)FindResource("TextField");
        _box.Text = initial;
        var ok = new Button { Style = (Style)FindResource("BtnAccent"), Content = "OK", MinWidth = 80, IsDefault = true };
        ok.Click += (_, _) => DialogResult = _box.Text.Trim().Length > 0;
        var cancel = new Button { Style = (Style)FindResource("Btn"), Content = "Cancel", MinWidth = 80, IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("Label"), Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(_box);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) => { _box.Focus(); Keyboard.Focus(_box); _box.SelectAll(); };
    }

    /// <summary>What was typed, trimmed; null when cancelled or left empty.</summary>
    public static string? Ask(Window? owner, string title, string label, string initial = "")
    {
        var prompt = new TextPrompt(title, label, initial);
        if (owner is { IsVisible: true }) prompt.Owner = owner;
        else prompt.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return prompt.ShowDialog() == true ? prompt._box.Text.Trim() : null;
    }
}
