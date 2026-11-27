using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Overskin.Linux.Core;
using Overskin.Linux.Platform;

namespace Overskin.Linux.Interface;

/// <summary>Favourites and tags for captures, kept beside the settings (library-marks.json).</summary>
public static class LibraryMarks
{
    public sealed class Entry
    {
        public bool Favourite { get; set; }
        public List<string> Tags { get; set; } = new();
    }

    private static Dictionary<string, Entry>? _all;
    private static string FilePath => Path.Combine(Path.GetDirectoryName(Settings.FilePath)!, "library-marks.json");

    private static Dictionary<string, Entry> All
    {
        get
        {
            if (_all != null) return _all;
            try
            {
                if (File.Exists(FilePath)) _all = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FilePath));
            }
            catch { }
            return _all ??= new Dictionary<string, Entry>(StringComparer.Ordinal);
        }
    }

    public static Entry? Of(string path) => All.TryGetValue(path, out var e) ? e : null;
    public static bool IsFavourite(string path) => Of(path)?.Favourite == true;
    public static IReadOnlyList<string> TagsOf(string path) => Of(path)?.Tags ?? (IReadOnlyList<string>)Array.Empty<string>();
    public static IEnumerable<string> AllTags => All.Values.SelectMany(e => e.Tags).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t, StringComparer.OrdinalIgnoreCase);

    public static void SetFavourite(IEnumerable<string> paths, bool on)
    {
        foreach (var p in paths) Get(p).Favourite = on;
        Save();
    }

    /// <summary>Tags from what's typed: words, with or without #, separated by spaces or commas.</summary>
    public static List<string> Parse(string text) =>
        text.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.TrimStart('#').Trim()).Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static void SetTags(string path, IEnumerable<string> tags)
    {
        Get(path).Tags = tags.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Save();
    }

    public static void Renamed(string from, string to)
    {
        if (All.Remove(from, out var e)) All[to] = e;
        Save();
    }

    private static Entry Get(string path) => All.TryGetValue(path, out var e) ? e : All[path] = new Entry();

    private static void Save()
    {
        try
        {
            // only what's still marked, for files that still exist
            var keep = All.Where(kv => (kv.Value.Favourite || kv.Value.Tags.Count > 0) && File.Exists(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(keep));
        }
        catch (Exception ex) { Log.Write("library marks: " + ex.Message); }
    }
}

/// <summary>
/// Captures that look the same (the same screenshot taken twice, or saved again a little smaller),
/// found the way the Windows app finds them: a rough fingerprint, a small grey copy, and the shape.
/// </summary>
public static class Duplicates
{
    public sealed record Print(ulong Hash, byte[] Small, double Shape);
    private const int SmallSide = 32;
    private static readonly Dictionary<(string, long), Print> Cache = new();

    public static Print PrintOf(Ai.Pixels picture)
    {
        byte[] Grey(int w, int h)
        {
            var px = Ai.Resample(picture, 0, 0, picture.Width, picture.Height, w, h);
            var grey = new byte[w * h];
            for (int i = 0; i < w * h; i++)
            {
                int a = px[i * 4 + 3];
                // shown on white, as a capture would be
                double b = px[i * 4] + 255 - a, g = px[i * 4 + 1] + 255 - a, r = px[i * 4 + 2] + 255 - a;
                grey[i] = (byte)Math.Clamp(Math.Round(0.299 * r + 0.587 * g + 0.114 * b), 0, 255);
            }
            return grey;
        }
        var nine = Grey(9, 8);
        ulong hash = 0;
        int bit = 0;
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++, bit++)
                if (nine[y * 9 + x] > nine[y * 9 + x + 1]) hash |= 1UL << bit;
        return new Print(hash, Grey(SmallSide, SmallSide), picture.Width / (double)Math.Max(1, picture.Height));
    }

    /// <summary>The same picture: the same shape, close fingerprints, and small grey copies that match almost exactly.</summary>
    public static bool Same(Print a, Print b)
    {
        if (Math.Abs(a.Shape - b.Shape) > 0.03 * a.Shape || System.Numerics.BitOperations.PopCount(a.Hash ^ b.Hash) > 8) return false;
        long diff = 0;
        int worst = 0;
        for (int k = 0; k < a.Small.Length; k++)
        {
            int d = Math.Abs(a.Small[k] - b.Small[k]);
            diff += d;
            worst = Math.Max(worst, d);
        }
        return diff / (double)a.Small.Length < 2.5 && worst < 60;
    }

    /// <summary>Groups of two or more pictures that look the same. Reads the files; run it in the background.</summary>
    public static List<List<string>> Groups(IReadOnlyList<string> paths)
    {
        var prints = new List<(string Path, Print Print)>();
        foreach (var p in paths)
        {
            try
            {
                var key = (p, File.GetLastWriteTimeUtc(p).Ticks);
                Print? print;
                lock (Cache) Cache.TryGetValue(key, out print);
                if (print == null)
                {
                    using var bitmap = new Bitmap(p);
                    print = PrintOf(Ai.Of(bitmap));
                    lock (Cache) Cache[key] = print;
                }
                prints.Add((p, print));
            }
            catch { } // a file that can't be read just isn't compared
        }
        var groups = new List<List<string>>();
        var used = new bool[prints.Count];
        for (int i = 0; i < prints.Count; i++)
        {
            if (used[i]) continue;
            var group = new List<string> { prints[i].Path };
            for (int j = i + 1; j < prints.Count; j++)
                if (!used[j] && Same(prints[i].Print, prints[j].Print)) { group.Add(prints[j].Path); used[j] = true; }
            if (group.Count > 1) groups.Add(group);
        }
        return groups;
    }
}

/// <summary>Sends a capture to a phone on the same Wi-Fi: a QR code to scan; nothing goes through the internet.</summary>
public sealed class PhoneWindow : Window
{
    private readonly Overskin.PhoneShare _share;

    private PhoneWindow(Overskin.PhoneShare share, string path)
    {
        _share = share;
        Title = "Send to phone";
        Icon = App.Icon;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var status = Ui.Label("Scan it with your phone's camera. Your phone must be on the same Wi-Fi.", "dim");
        status.TextWrapping = TextWrapping.Wrap;
        share.Sent += () => Avalonia.Threading.Dispatcher.UIThread.Post(() => status.Text = "Sent. You can close this.");
        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                Ui.Label("Send to phone", "h2"),
                new Border { Background = Brushes.White, Padding = new Thickness(10), CornerRadius = new CornerRadius(10), HorizontalAlignment = HorizontalAlignment.Center,
                             Child = new Image { Source = QrCode(share.Url), Width = 240, Height = 240 } },
                Ui.Label(Path.GetFileName(path)),
                status,
                Ui.Label("It stops when this closes, or after 15 minutes. Your computer may ask once whether Overskin may use the network.", "dim"),
            },
        };
        Closed += (_, _) => _share.Dispose();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    /// <summary>A QR code for some text, black on white.</summary>
    public static Bitmap QrCode(string text, int size = 480)
    {
        var writer = new ZXing.BarcodeWriterPixelData
        {
            Format = ZXing.BarcodeFormat.QR_CODE,
            Options = new ZXing.Common.EncodingOptions { Width = size, Height = size, Margin = 1 },
        };
        var data = writer.Write(text);
        return Imaging.FromPixels(data.Pixels, new PixelSize(data.Width, data.Height));
    }

    public static void Show(string path)
    {
        var share = Overskin.PhoneShare.Start(path);
        if (share == null)
        {
            MessageWindow.Show("Not on a home network", "Sending to a phone needs this computer and the phone on the same Wi-Fi (or network).");
            return;
        }
        new PhoneWindow(share, path).Show();
    }
}

/// <summary>Two pictures, one over the other: drag the line to see before and after.</summary>
public sealed class CompareWindow : Window
{
    public CompareWindow(string before, string after)
    {
        Title = $"Compare: {Path.GetFileName(before)} and {Path.GetFileName(after)}";
        Icon = App.Icon;
        Width = 1000;
        Height = 700;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var surface = new CompareSurface(new Bitmap(before), new Bitmap(after));
        Content = new DockPanel
        {
            Children =
            {
                Docked(Ui.Label("Drag across the picture: left of the line is " + Path.GetFileName(before) + ", right is " + Path.GetFileName(after) + ".", "dim")),
                surface,
            },
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private static Control Docked(Control c)
    {
        c.Margin = new Thickness(14, 10);
        DockPanel.SetDock(c, Dock.Top);
        return c;
    }

    private sealed class CompareSurface : Control
    {
        private readonly Bitmap _a, _b;
        private double _split = 0.5;

        public CompareSurface(Bitmap a, Bitmap b)
        {
            _a = a;
            _b = b;
            Cursor = new Cursor(StandardCursorType.SizeWestEast);
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            var r = Fit();
            if (r.Width <= 0) return;
            _split = Math.Clamp((e.GetPosition(this).X - r.X) / r.Width, 0, 1);
            InvalidateVisual();
        }

        private Rect Fit()
        {
            double w = Math.Max(_a.Size.Width, _b.Size.Width), h = Math.Max(_a.Size.Height, _b.Size.Height);
            double k = Math.Min(Bounds.Width / Math.Max(1, w), Bounds.Height / Math.Max(1, h));
            return new Rect((Bounds.Width - w * k) / 2, (Bounds.Height - h * k) / 2, w * k, h * k);
        }

        public override void Render(DrawingContext ctx)
        {
            var r = Fit();
            ctx.DrawImage(_b, new Rect(_b.Size), r);
            using (ctx.PushClip(new Rect(r.X, r.Y, r.Width * _split, r.Height)))
                ctx.DrawImage(_a, new Rect(_a.Size), r);
            double x = r.X + r.Width * _split;
            ctx.DrawLine(new Pen(Brushes.White, 2), new Point(x, r.Y), new Point(x, r.Bottom));
        }
    }
}
