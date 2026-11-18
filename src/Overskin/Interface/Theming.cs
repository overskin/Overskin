using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Overskin;

/// <summary>A colour theme: the window, the panels on it, text, quieter text, and (for the
/// named editor themes) the accent it comes with.</summary>
public sealed record ThemePreset(string Name, bool Light, string Bg, string Panel, string Text, string Dim, string? Accent = null);

/// <summary>
/// The user's colour choices: a theme (dark or light) and an accent colour. Every other colour in
/// the app is derived from those, so any combination stays coherent.
/// </summary>
public static class Theming
{
    public const string DefaultAccent = "#4F8CFF";
    public const string DefaultTone = "Graphite";


    /// <summary>Accent presets offered as swatches.</summary>
    public static readonly (string Name, string Hex)[] Accents =
    {
        ("Blue", "#4F8CFF"),
        ("Violet", "#8E7BFF"),
        ("Pink", "#EE5FA0"),
        ("Red", "#EF5F5B"),
        ("Orange", "#F2994A"),
        ("Yellow", "#E6C344"),
        ("Green", "#48C288"),
        ("Teal", "#38BFC3"),
    };

    public const string DefaultLightTheme = "Light";

    /// <summary>
    /// Every theme. The first four are Overskin's own; the rest follow well-known editor and
    /// typing-site palettes (Visual Studio, Dracula, Nord, Gruvbox, Catppuccin, monkeytype's Serika...).
    /// </summary>
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

    /// <summary>True while the current theme is a light one.</summary>
    public static bool IsLight { get; private set; }

    public static bool TryParseHex(string? text, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string hex = text.Trim().TrimStart('#');
        if (hex.Length == 3) hex = string.Concat(hex.Select(c => $"{c}{c}"));
        if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb)) return false;
        color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Hue 0–360, saturation and value 0–1.</summary>
    public static Color FromHsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = (int)(h / 60) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    public static (double H, double S, double V) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0 : max == r ? 60 * ((g - b) / d % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        return ((h + 360) % 360, max == 0 ? 0 : d / max, max);
    }

    /// <summary>Recomputes every themed brush. Anything using DynamicResource updates at once.</summary>
    public static void Apply(string? accentHex, string? themeName)
    {
        if (!TryParseHex(accentHex, out var accent)) TryParseHex(DefaultAccent, out accent);
        var theme = Find(themeName);
        IsLight = theme.Light;
        TryParseHex(theme.Bg, out var bg);
        TryParseHex(theme.Panel, out var panel);
        TryParseHex(theme.Text, out var text);
        TryParseHex(theme.Dim, out var dim);
        bool light = theme.Light;

        var res = Application.Current.Resources;
        Set(res, "Bg", bg);
        Set(res, "Panel", panel);
        Set(res, "Field", Mix(panel, text, light ? 0.06 : 0.07));
        Set(res, "FieldHover", Mix(panel, text, light ? 0.11 : 0.12));
        Set(res, "Line", Mix(panel, text, light ? 0.13 : 0.09));
        Set(res, "LineStrong", Mix(panel, text, light ? 0.24 : 0.17));
        Set(res, "Text", text);
        Set(res, "TextDim", dim);
        Set(res, "TextFaint", Mix(panel, dim, 0.65));
        Set(res, "AccentSoft", Mix(panel, accent, light ? 0.13 : 0.15));
        Set(res, "AccentLine", Mix(panel, accent, light ? 0.55 : 0.45));
        Set(res, "Accent", accent);
        // text that sits on a solid accent fill: dark on bright accents, white on deep ones
        Set(res, "AccentInk", Luminance(accent) > 0.34 ? Mix(Colors.Black, accent, 0.16) : Colors.White);
        Set(res, "Shadow", light ? Color.FromArgb(70, 0, 0, 0) : Colors.Black);
        // behind pictures in the editors
        Set(res, "Stage", light ? Mix(bg, text, 0.09) : Color.FromRgb(0x0A, 0x0A, 0x0C));

        ApplyShapes(res, panel, text, light);
    }

    private static void ApplyShapes(ResourceDictionary res, Color panel, Color text, bool light)
    {
        var field = Mix(panel, text, light ? 0.06 : 0.07);
        Set(res, "ButtonFill", field);
        Set(res, "ButtonFillHover", Mix(panel, text, light ? 0.11 : 0.12));
        Set(res, "AccentFill", (Color)((SolidColorBrush)res["Accent"]).Color);
        Set(res, "MenuBg", panel);
        Set(res, "MenuHover", field);
        Set(res, "MenuHoverLine", Colors.Transparent);
        Set(res, "HeaderBg", Colors.Transparent);
        Set(res, "TileFill", panel);
        Set(res, "Bevel", Colors.Transparent);
        res["ButtonWeight"] = FontWeights.SemiBold;

        res["RadiusWindow"] = new CornerRadius(16, 3, 16, 3);
        res["RadiusCard"] = new CornerRadius(13, 2, 13, 2);
        res["RadiusControl"] = new CornerRadius(9, 2, 9, 2);
        res["RadiusSmall"] = new CornerRadius(6, 1, 6, 1);
        res["RadiusPill"] = new CornerRadius(9, 2, 9, 2);
        res["RadiusKnob"] = new CornerRadius(6, 1, 6, 1);
        res["RadiusBar"] = new CornerRadius(2);
    }

    private static void Set(ResourceDictionary res, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        res[key] = brush;
    }

    /// <summary>Lightens a colour by roughly <paramref name="amount"/> levels, keeping its tint.</summary>
    private static Color Lift(Color c, int amount)
    {
        double avg = Math.Max(1, (c.R + c.G + c.B) / 3.0);
        byte Channel(byte v) => (byte)Math.Clamp(Math.Round(v + amount * (0.6 + 0.4 * v / avg)), 0, 255);
        return Color.FromRgb(Channel(c.R), Channel(c.G), Channel(c.B));
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

    private static double Luminance(Color c)
    {
        static double Lin(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }
}
