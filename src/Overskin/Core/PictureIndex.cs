using System.IO;
using System.Windows.Media.Imaging;

namespace Overskin;

/// <summary>
/// What's in every saved capture, as a CLIP fingerprint worked out once in the background on
/// this PC and kept in %LocalAppData%\Overskin\picture-index.bin, so the Library can find "cat",
/// "chart" or "minecraft" in pictures that have no such words in them.
/// </summary>
public sealed class PictureIndex
{
    private const int Size = 512;
    /// <summary>A picture this alike to the search counts as a match.</summary>
    public const float Match = 0.245f;

    private static readonly string FilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Overskin", "picture-index.bin");

    private readonly Dictionary<string, (long Ticks, float[] Print)> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _queue = new();
    private readonly object _lock = new();
    private bool _running;
    private int _sinceSave;

    /// <summary>Raised (on a background thread) when more pictures have been looked at.</summary>
    public event Action? Updated;

    public PictureIndex()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            using var r = new BinaryReader(File.OpenRead(FilePath));
            int count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                string path = r.ReadString();
                long ticks = r.ReadInt64();
                var print = new float[Size];
                for (int k = 0; k < Size; k++) print[k] = r.ReadSingle();
                _entries[path] = (ticks, print);
            }
        }
        catch
        {
            // a damaged index is simply rebuilt
            _entries.Clear();
        }
    }

    public int Pending { get { lock (_lock) return _queue.Count; } }

    /// <summary>Looks at any of these captures that are new or changed, one at a time, in the background.</summary>
    public void Add(IEnumerable<string> paths)
    {
        if (!AiModels.Available) return;
        lock (_lock)
        {
            foreach (var path in paths)
            {
                if (Thumbs.IsVideo(path) || !File.Exists(path) || _queue.Contains(path)) continue;
                long ticks = File.GetLastWriteTimeUtc(path).Ticks;
                if (_entries.TryGetValue(path, out var e) && e.Ticks == ticks) continue;
                _queue.Enqueue(path);
            }
            if (_running || _queue.Count == 0) return;
            _running = true;
        }
        var worker = new Thread(Work) { IsBackground = true, Name = "Overskin picture index", Priority = ThreadPriority.BelowNormal };
        worker.SetApartmentState(ApartmentState.STA); // WPF's bitmaps are drawn on it
        worker.Start();
    }

    private void Work()
    {
        while (true)
        {
            string path;
            lock (_lock)
            {
                if (_queue.Count == 0)
                {
                    _running = false;
                    break;
                }
                path = _queue.Dequeue();
            }
            try
            {
                var print = AiModels.PictureFingerprint(Load(path));
                lock (_lock) _entries[path] = (File.GetLastWriteTimeUtc(path).Ticks, print);
                if (++_sinceSave >= 20) Save();
                Updated?.Invoke();
            }
            catch
            {
                // a file that can't be read is skipped (and tried again next time)
            }
            Thread.Sleep(40); // stay out of the way of whatever else is running
        }
        Save();
        Updated?.Invoke();
    }

    private static BitmapSource Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.DecodePixelWidth = 448; // plenty for a 224-pixel look, and quick to load
        bmp.StreamSource = stream;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    /// <summary>How alike a capture is to the search, or null if it hasn't been looked at yet.</summary>
    public float? Likeness(string path, float[] query)
    {
        lock (_lock) return _entries.TryGetValue(path, out var e) ? AiModels.Likeness(e.Print, query) : null;
    }

    public void Renamed(string from, string to)
    {
        lock (_lock)
        {
            if (!_entries.Remove(from, out var e)) return;
            _entries[to] = (File.Exists(to) ? File.GetLastWriteTimeUtc(to).Ticks : e.Ticks, e.Print);
        }
    }

    public void Save()
    {
        try
        {
            KeyValuePair<string, (long Ticks, float[] Print)>[] all;
            lock (_lock)
            {
                all = _entries.Where(e => File.Exists(e.Key)).ToArray();
                _sinceSave = 0;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string temp = FilePath + ".tmp";
            using (var w = new BinaryWriter(File.Create(temp)))
            {
                w.Write(all.Length);
                foreach (var (path, (ticks, print)) in all)
                {
                    w.Write(path);
                    w.Write(ticks);
                    foreach (float f in print) w.Write(f);
                }
            }
            File.Move(temp, FilePath, overwrite: true);
        }
        catch
        {
            // the index is only a speed-up: it's rebuilt if it can't be saved
        }
    }

    /// <summary>The search's fingerprint: what the words mean, as a picture or a screenshot.</summary>
    public static float[] QueryPrint(string query)
    {
        var a = AiModels.WordsFingerprint($"a picture of {query}");
        var b = AiModels.WordsFingerprint($"a screenshot of {query}");
        var mean = new float[a.Length];
        double n = 0;
        for (int i = 0; i < a.Length; i++) { mean[i] = a[i] + b[i]; n += mean[i] * mean[i]; }
        n = Math.Sqrt(n);
        for (int i = 0; i < mean.Length; i++) mean[i] = (float)(mean[i] / n);
        return mean;
    }
}
