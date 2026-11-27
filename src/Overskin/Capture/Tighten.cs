namespace Overskin;

/// <summary>
/// Fits a dragged box to what's inside it: plain rows and columns along each edge (the
/// background round what you meant to capture) are trimmed away, so a rough drag comes out
/// tight to the content.
/// </summary>
internal static class Tighten
{
    /// <summary>How far a pixel may differ from the edge's background and still count as background.</summary>
    public const int Tolerance = 14;

    /// <summary>
    /// The box <paramref name="left"/>, <paramref name="top"/> (inclusive) to <paramref name="right"/>,
    /// <paramref name="bottom"/> (exclusive) in a BGRA picture <paramref name="width"/> wide, shrunk
    /// to its content. Unchanged when it's all background.
    /// </summary>
    public static (int Left, int Top, int Right, int Bottom) Fit(byte[] pixels, int width, int left, int top, int right, int bottom)
    {
        int l = left, t = top, r = right, b = bottom;
        // the background is what each side of the box as dragged shows; only lines of that colour
        // come off (read afresh each time, a solid shape inside would be taken for background too,
        // trimmed away entirely, and the box left as it was)
        var cl = Edge(pixels, width, l, t, l + 1, b);
        var cr = Edge(pixels, width, r - 1, t, r, b);
        var ct = Edge(pixels, width, l, t, r, t + 1);
        var cb = Edge(pixels, width, l, b - 1, r, b);
        // each side, in turn and again, until nothing more comes off (a corner can hide behind another side)
        for (int pass = 0; pass < 3; pass++)
        {
            int before = l + t + r + b;
            while (r - l > 4 && Plain(pixels, width, l, t, l + 1, b, cl)) l++;
            while (r - l > 4 && Plain(pixels, width, r - 1, t, r, b, cr)) r--;
            while (b - t > 4 && Plain(pixels, width, l, t, r, t + 1, ct)) t++;
            while (b - t > 4 && Plain(pixels, width, l, b - 1, r, b, cb)) b--;
            if (l + t + r + b == before) break;
        }
        // nothing but background: keep what was dragged
        if (r - l <= 4 || b - t <= 4) return (left, top, right, bottom);
        return (l, t, r, b);
    }

    /// <summary>The middle colour of a one-pixel line (the background, if the line is plain).</summary>
    private static (int B, int G, int R) Edge(byte[] p, int width, int x0, int y0, int x1, int y1)
    {
        int x = (x0 + x1 - 1) / 2, y = (y0 + y1 - 1) / 2;
        // a corner pixel of the line is a better guess than the middle, which may be content
        int i = (y0 * width + x0) * 4;
        int j = (y * width + x) * 4;
        return (p[i], p[i + 1], p[i + 2]) is var corner && Near(p, j, corner) ? corner : (p[i], p[i + 1], p[i + 2]);
    }

    private static bool Near(byte[] p, int i, (int B, int G, int R) c) =>
        Math.Abs(p[i] - c.B) <= Tolerance && Math.Abs(p[i + 1] - c.G) <= Tolerance && Math.Abs(p[i + 2] - c.R) <= Tolerance;

    /// <summary>True when every pixel of the line is within <see cref="Tolerance"/> of <paramref name="c"/>.</summary>
    private static bool Plain(byte[] p, int width, int x0, int y0, int x1, int y1, (int B, int G, int R) c)
    {
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
                if (!Near(p, (y * width + x) * 4, c)) return false;
        return true;
    }
}
