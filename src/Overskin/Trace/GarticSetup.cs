using System.Text.Json.Serialization;
using System.Windows;

namespace Overskin;

/// <summary>
/// Where Gartic Phone's controls are on screen, and how big its brushes draw: picked once by
/// clicking them, so Overskin can switch brush size, opacity and colour while it paints.
/// </summary>
public sealed class GarticSetup
{
    /// <summary>The pen tool.</summary>
    public PaletteSwatch? Pen { get; set; }
    /// <summary>The five brush-size buttons, smallest first.</summary>
    public List<PaletteSwatch> Sizes { get; set; } = new();
    /// <summary>The faintest and the full opacity buttons; the ones between are evenly spaced.</summary>
    public PaletteSwatch? OpacityLow { get; set; }
    public PaletteSwatch? OpacityHigh { get; set; }
    /// <summary>Gartic always has ten opacity steps.</summary>
    public const int OpacityLevels = 10;
    /// <summary>Mix shades with opacity (otherwise always full opacity).</summary>
    public bool UseOpacity { get; set; } = true;
    /// <summary>Measured brush diameters in screen pixels, smallest first; null until measured.</summary>
    public double[]? BrushPx { get; set; }

    public const int SizeCount = 5;

    [JsonIgnore]
    public bool ButtonsPicked => Pen != null && Sizes.Count == SizeCount && OpacityLow != null && OpacityHigh != null;

    /// <summary>Where opacity level <paramref name="level"/> (1 = faintest … <see cref="OpacityLevels"/> = full) is on screen.</summary>
    public Point? OpacityAt(int level)
    {
        if (OpacityLow == null || OpacityHigh == null) return null;
        const int n = OpacityLevels;
        double t = (Math.Clamp(level, 1, n) - 1) / (double)(n - 1);
        return new Point(Math.Round(OpacityLow.X + (OpacityHigh.X - OpacityLow.X) * t), Math.Round(OpacityLow.Y + (OpacityHigh.Y - OpacityLow.Y) * t));
    }

    public void Normalize()
    {
        Sizes ??= new();
        Sizes.RemoveAll(s => s == null);
        if (Sizes.Count > SizeCount) Sizes = Sizes.Take(SizeCount).ToList();
        if (BrushPx is { } px && (px.Length != SizeCount || px.Any(v => !(v >= 1 && v < 500)))) BrushPx = null;
    }
}
