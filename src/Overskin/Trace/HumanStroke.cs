using System.Windows;

namespace Overskin;

/// <summary>
/// Turns a planned stroke into the points a hand would send: the drawer sends one point per tick,
/// so spacing the points out is the same as setting the speed. Each stroke starts slowly, speeds
/// up along the way and slows down again before the pen lifts (ease in, ease out), and wanders a
/// fraction of a pixel either side of the line, never at its ends.
/// </summary>
public static class HumanStroke
{
    /// <param name="path">The stroke, in screen pixels.</param>
    /// <param name="cruise">Pixels per tick at full speed.</param>
    /// <param name="wobble">How far, at most, the pen strays from the line, in pixels.</param>
    public static Point[] Path(Point[] path, double cruise, double wobble, Random rng)
    {
        if (path.Length < 2) return path;
        var cum = new double[path.Length];
        for (int i = 1; i < path.Length; i++) cum[i] = cum[i - 1] + (path[i] - path[i - 1]).Length;
        double length = cum[^1];
        if (length < 1) return path;

        cruise = Math.Max(1, cruise);
        double slowest = Math.Max(0.6, cruise * 0.18);
        // speed up over this far, and slow down over the same at the end
        double ramp = Math.Min(length / 2, cruise * 5);
        double phase1 = rng.NextDouble() * Math.PI * 2, phase2 = rng.NextDouble() * Math.PI * 2;
        double wave1 = 70 + rng.NextDouble() * 50, wave2 = 23 + rng.NextDouble() * 15;

        var points = new List<Point>();
        int seg = 0;
        double s = 0;
        while (true)
        {
            while (seg < path.Length - 2 && cum[seg + 1] < s) seg++;
            var a = path[seg];
            var b = path[seg + 1];
            double segLength = Math.Max(1e-9, cum[seg + 1] - cum[seg]);
            double t = Math.Clamp((s - cum[seg]) / segLength, 0, 1);
            var at = new Point(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

            // a slow, uneven drift sideways, faded out towards both ends
            double edge = Math.Min(s, length - s);
            double fade = Smoothstep(Math.Clamp(edge / Math.Max(1, cruise * 3), 0, 1));
            double drift = wobble * fade * (0.65 * Math.Sin(s / wave1 * 2 * Math.PI + phase1) + 0.35 * Math.Sin(s / wave2 * 2 * Math.PI + phase2));
            var dir = (b - a) / segLength;
            points.Add(new Point(at.X - dir.Y * drift, at.Y + dir.X * drift));

            if (s >= length) break;
            double speed = slowest + (cruise - slowest) * Smoothstep(Math.Clamp(edge / Math.Max(1e-6, ramp), 0, 1));
            s = Math.Min(length, s + speed);
        }
        return points.ToArray();
    }

    /// <summary>
    /// Where the pen goes while lifted, from one stroke's end to the next one's start: a quick
    /// eased glide rather than a jump.
    /// </summary>
    public static Point[] Travel(Point from, Point to, double cruise)
    {
        double length = (to - from).Length;
        int steps = (int)Math.Clamp(Math.Ceiling(length / Math.Max(1, cruise * 2.5)), 1, 30);
        var points = new Point[steps];
        for (int i = 1; i <= steps; i++)
        {
            double t = Smoothstep(i / (double)steps);
            points[i - 1] = new Point(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t);
        }
        return points;
    }

    private static double Smoothstep(double x) => x * x * (3 - 2 * x);
}
