using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Overskin;

/// <summary>
/// A question in Overskin's own look (instead of Windows' grey message box): a title, a line or
/// two, and a few buttons, the main one in the accent colour. Esc or the close button cancels.
/// </summary>
public sealed class AskDialog : Window
{
    private int _answer = -1;

    private AskDialog(string title, string message, string icon, IReadOnlyList<(string Label, bool Main)> buttons)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = (FontFamily)FindResource("UiFont");
        FontSize = 12.5;
        SetResourceReference(ForegroundProperty, "Text");
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        var heading = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        var mark = new Overskin.Icon { Data = (Geometry)FindResource(icon), Width = 18, Height = 18, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        mark.SetResourceReference(Overskin.Icon.ForegroundProperty, "Accent");
        heading.Children.Add(mark);
        heading.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });

        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 360, Margin = new Thickness(0, 0, 0, 18) };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        for (int i = 0; i < buttons.Count; i++)
        {
            int index = i;
            var b = new Button { Content = buttons[i].Label, MinWidth = 84, Height = 30, Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0), IsDefault = buttons[i].Main };
            b.SetResourceReference(StyleProperty, buttons[i].Main ? "BtnAccent" : "Btn");
            b.Click += (_, _) => { _answer = index; Close(); };
            row.Children.Add(b);
        }

        var body = new StackPanel { Margin = new Thickness(22, 18, 22, 18), MinWidth = 300 };
        body.Children.Add(heading);
        body.Children.Add(text);
        body.Children.Add(row);

        var card = new Border { BorderThickness = new Thickness(1), Child = body, Margin = new Thickness(16) };
        card.SetResourceReference(Border.BackgroundProperty, "Panel");
        card.SetResourceReference(Border.BorderBrushProperty, "LineStrong");
        card.SetResourceReference(Border.CornerRadiusProperty, "RadiusWindow");
        card.Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Direction = 270, Opacity = 0.45, Color = Colors.Black };
        card.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        Content = card;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { _answer = -1; Close(); e.Handled = true; return; }
        base.OnKeyDown(e);
    }

    /// <summary>Asks; the index of the button clicked, or -1 for Esc.</summary>
    public static int Ask(Window? owner, string title, string message, params (string Label, bool Main)[] buttons) =>
        Ask(owner, title, message, "IcInfo", buttons);

    public static int Ask(Window? owner, string title, string message, string icon, params (string Label, bool Main)[] buttons)
    {
        var dialog = new AskDialog(title, message, icon, buttons);
        if (owner != null && owner.IsVisible) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowDialog();
        return dialog._answer;
    }
}
