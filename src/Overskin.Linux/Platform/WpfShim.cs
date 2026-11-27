// Just enough of WPF's Point, Vector and Color for the shared trace engine (src/Overskin/Trace/TraceEngine.cs)
// to compile on Linux. Only that file uses these namespaces here.

namespace System.Windows
{
    public struct Point : IEquatable<Point>
    {
        public double X { get; set; }
        public double Y { get; set; }
        public Point(double x, double y) { X = x; Y = y; }

        public static Vector operator -(Point a, Point b) => new(a.X - b.X, a.Y - b.Y);
        public static Point operator +(Point a, Vector v) => new(a.X + v.X, a.Y + v.Y);
        public static Point operator -(Point a, Vector v) => new(a.X - v.X, a.Y - v.Y);
        public static bool operator ==(Point a, Point b) => a.X == b.X && a.Y == b.Y;
        public static bool operator !=(Point a, Point b) => !(a == b);
        public bool Equals(Point other) => this == other;
        public override bool Equals(object? obj) => obj is Point p && this == p;
        public override int GetHashCode() => HashCode.Combine(X, Y);
        public override string ToString() => $"{X},{Y}";
    }

    public struct Rect
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public Rect(double x, double y, double width, double height) { X = x; Y = y; Width = width; Height = height; }
    }

    public struct Vector
    {
        public double X { get; set; }
        public double Y { get; set; }
        public Vector(double x, double y) { X = x; Y = y; }
        public double Length => Math.Sqrt(X * X + Y * Y);
        public double LengthSquared => X * X + Y * Y;

        public void Normalize()
        {
            double l = Length;
            if (l > 0) { X /= l; Y /= l; }
        }

        public static Vector operator -(Vector v) => new(-v.X, -v.Y);
        public static Vector operator +(Vector a, Vector b) => new(a.X + b.X, a.Y + b.Y);
        public static Vector operator -(Vector a, Vector b) => new(a.X - b.X, a.Y - b.Y);
        public static Vector operator *(Vector v, double k) => new(v.X * k, v.Y * k);
        public static Vector operator *(double k, Vector v) => new(v.X * k, v.Y * k);
        public static Vector operator /(Vector v, double k) => new(v.X / k, v.Y / k);
    }
}

namespace System.Windows.Media
{
    public struct Color : IEquatable<Color>
    {
        public byte A { get; set; }
        public byte R { get; set; }
        public byte G { get; set; }
        public byte B { get; set; }

        public static Color FromRgb(byte r, byte g, byte b) => new() { A = 255, R = r, G = g, B = b };
        public static Color FromArgb(byte a, byte r, byte g, byte b) => new() { A = a, R = r, G = g, B = b };
        public static bool operator ==(Color a, Color b) => a.A == b.A && a.R == b.R && a.G == b.G && a.B == b.B;
        public static bool operator !=(Color a, Color b) => !(a == b);
        public bool Equals(Color other) => this == other;
        public override bool Equals(object? obj) => obj is Color c && this == c;
        public override int GetHashCode() => HashCode.Combine(A, R, G, B);
    }
}
