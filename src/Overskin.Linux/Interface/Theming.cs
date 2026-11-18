using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace Overskin.Linux.Interface;

public sealed record ThemePreset(string Name, bool Light, string Bg, string Panel, string Text, string Dim, string? Accent = null);

/// <summary>The colour themes, the same as the Windows app's, applied live.</summary>
public static class Theming
{
    public const string DefaultAccent = "#4F8CFF";

    public static readonly ThemePreset[] Themes =
    {
        new("Graphite", false, "#121316", "#1B1D22", "#E7E8EB", "#9094A0"),
        new("Carbon", false, "#141414", "#1D1D1D", "#E8E8E8", "#959595"),
        new("Ink", false, "#0D111A", "#161B26", "#E4E8F0", "#8A93A6"),
        new("Black", false, "#090909", "#121212", "#E6E6E6", "#8E8E8E"),
        new("Visual Studio", false, "#1E1E1E", "#252526", "#D4D4D4", "#9D9D9D", "#3794FF"),
        new("Dracula", false, "#21222C", "#282A36", "#F8F8F2", "#8E95B8", "#BD93F9"),
        new("Nord", false, "#2E3440", "#3B4252", "#ECEFF4", "#A3ADBF", "#88C0D0"),
        new("Gruvbox", false, "#1D2021", "#282828", "#EBDBB2", "#A89984", "#FABD2F"),
        new("Monokai", false, "#1E1F1C", "#272822", "#F8F8F2", "#9A988A", "#A6E22E"),
        new("One Dark", false, "#21252B", "#282C34", "#ABB2BF", "#7F848E", "#61AFEF"),
        new("Tokyo Night", false, "#16161E", "#1A1B26", "#C0CAF5", "#787C99", "#7AA2F7"),
        new("Catppuccin Mocha", false, "#11111B", "#1E1E2E", "#CDD6F4", "#9399B2", "#CBA6F7"),
        new("Solarized Dark", false, "#00212B", "#002B36", "#A3B3B3", "#6C8288", "#268BD2"),
        new("Serika Dark", false, "#2C2E31", "#323437", "#D1D0C5", "#8A8C90", "#E2B714"),
        new("Light", true, "#F0F1F3", "#FFFFFF", "#1E1F22", "#5F6370"),
        new("Paper", true, "#E8E8E6", "#F6F6F4", "#3A3A3A", "#7D7D7D"),
        new("Visual Studio Light", true, "#EEEEF2", "#F5F5F5", "#1E1E1E", "#6A6A6A", "#007ACC"),
        new("GitHub Light", true, "#F6F8FA", "#FFFFFF", "#1F2328", "#656D76", "#0969DA"),
        new("Solarized Light", true, "#EEE8D5", "#FDF6E3", "#3F5258", "#687C82", "#268BD2"),
        new("Gruvbox Light", true, "#F2E5BC", "#FBF1C7", "#3C3836", "#7C6F64", "#D65D0E"),
        new("Catppuccin Latte", true, "#E6E9EF", "#EFF1F5", "#4C4F69", "#7C7F93", "#8839EF"),
        new("Nord Light", true, "#D8DEE9", "#ECEFF4", "#2E3440", "#4C566A", "#5E81AC"),
        new("Serika", true, "#E1E1E3", "#ECECEE", "#323437", "#6E7176", "#C99E0B"),
    };

    public static ThemePreset Find(string? name) => Themes.FirstOrDefault(t => t.Name == name) ?? Themes[0];

    public static event Action? Changed;

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private static double Luma(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;

    public static void Apply(string name)
    {
        var theme = Find(name);
        var app = Application.Current!;
        var bg = Color.Parse(theme.Bg);
        var panel = Color.Parse(theme.Panel);
        var text = Color.Parse(theme.Text);
        var dim = Color.Parse(theme.Dim);
        var accent = Color.Parse(theme.Accent ?? DefaultAccent);

        Colours.Bg = bg;
        Colours.Panel = panel;
        Colours.Text = text;
        Colours.Dim = dim;
        Colours.Accent = accent;
        Colours.Raised = Mix(panel, text, theme.Light ? 0.07 : 0.07);

        void Set(string key, Color c) => app.Resources[key] = new SolidColorBrush(c);
        Set("BgBrush", bg);
        Set("PanelBrush", panel);
        Set("PanelHoverBrush", Mix(panel, text, 0.04));
        Set("RaisedBrush", Colours.Raised);
        Set("RaisedHoverBrush", Mix(panel, text, 0.13));
        Set("TextBrush", text);
        Set("DimBrush", dim);
        Set("AccentBrush", accent);
        Set("AccentHoverBrush", Mix(accent, theme.Light ? Colors.Black : Colors.White, 0.15));
        Set("OnAccentBrush", Luma(accent) > 0.62 ? Color.Parse("#111111") : Colors.White);
        // Fluent's own controls (text boxes, menus, sliders) follow these
        app.Resources["SystemAccentColor"] = accent;
        app.Resources["SystemAccentColorLight1"] = Mix(accent, Colors.White, 0.15);
        app.Resources["SystemAccentColorDark1"] = Mix(accent, Colors.Black, 0.15);

        app.RequestedThemeVariant = theme.Light ? ThemeVariant.Light : ThemeVariant.Dark;
        Changed?.Invoke();
    }
}
