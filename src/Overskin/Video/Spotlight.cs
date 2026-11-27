namespace Overskin;

/// <summary>
/// Dims a video frame everywhere except a soft-edged circle round the pointer, so the eye
/// follows it. Rows wholly outside the circle are dimmed with a lookup table; only the rows
/// through the circle need the distance worked out.
/// </summary>
internal static class Spotlight
{
    /// <summary>How bright the dimmed part stays.</summary>
    public const double Dimmed = 0.5;
    private static readonly byte[] Table = Enumerable.Range(0, 256).Select(v => (byte)Math.Round(v * Dimmed)).ToArray();

    /// <summary>The circle's radius for a frame this size.</summary>
    public static double Radius(int width, int height) => Math.Max(48, 0.17 * Math.Min(width, height));

    /// <summary>Dims BGRA <paramref name="pixels"/> outside a circle round (cx, cy).</summary>
    public static void Apply(byte[] pixels, int width, int height, double cx, double cy, double radius)
    {
        double feather = radius * 0.4, inner = radius - feather;
        Parallel.For(0, height, y =>
        {
            int row = y * width * 4;
            double dy = y + 0.5 - cy;
            if (Math.Abs(dy) >= radius)
            {
                for (int i = row, end = row + width * 4; i < end; i += 4)
                {
                    pixels[i] = Table[pixels[i]];
                    pixels[i + 1] = Table[pixels[i + 1]];
                    pixels[i + 2] = Table[pixels[i + 2]];
                }
                return;
            }
            for (int x = 0; x < width; x++)
            {
                double dx = x + 0.5 - cx, d = Math.Sqrt(dx * dx + dy * dy);
                if (d <= inner) continue;
                int i = row + x * 4;
                if (d >= radius)
                {
                    pixels[i] = Table[pixels[i]];
                    pixels[i + 1] = Table[pixels[i + 1]];
                    pixels[i + 2] = Table[pixels[i + 2]];
                    continue;
                }
                double t = (d - inner) / feather;
                t = t * t * (3 - 2 * t);
                double k = 1 - (1 - Dimmed) * t;
                pixels[i] = (byte)(pixels[i] * k);
                pixels[i + 1] = (byte)(pixels[i + 1] * k);
                pixels[i + 2] = (byte)(pixels[i + 2] * k);
            }
        });
    }
}
