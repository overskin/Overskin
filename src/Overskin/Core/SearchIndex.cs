using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace Overskin;

/// <summary>
/// The words in every saved capture, read once in the background with Windows' own text reader
/// (offline) and kept in %LocalAppData%\Overskin\search-index.json, so the Library can find
/// "that error message from Tuesday" by what the picture says.
/// </summary>
public sealed class SearchIndex
{
    private sealed record Entry(long Ticks, string Text);

    private static readonly string FilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Overskin", "search-index.json");

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _queue = new();
    private readonly object _lock = new();
    private bool _running;
    private int _sinceSave;

    /// <summary>Raised (on a background thread) when more captures have been read.</summary>
    public event Action? Updated;

    public SearchIndex()
    {
        try
        {
            if (File.Exists(FilePath))
                foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FilePath)) ?? new())
                    _entries[k] = v;
        }
        catch
        {
            // a damaged index is simply rebuilt
        }
    }

    /// <summary>Keeps what was read from a file under its new name.</summary>
    public void Renamed(string from, string to)
    {
        lock (_lock)
        {
            if (!_entries.Remove(from, out var e)) return;
            _entries[to] = new Entry(File.Exists(to) ? File.GetLastWriteTimeUtc(to).Ticks : e.Ticks, e.Text);
        }
    }

    public int Pending { get { lock (_lock) return _queue.Count; } }

    /// <summary>The text found in a capture ("" while it hasn't been read yet).</summary>
    public string TextOf(string path)
    {
        lock (_lock) return _entries.TryGetValue(path, out var e) ? e.Text : "";
    }

    /// <summary>Reads any of these captures that are new or changed, one at a time, in the background.</summary>
    public void Add(IEnumerable<string> paths)
    {
        if (!TextRecognizer.IsAvailable) return;
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
        _ = Task.Run(Work);
    }

    private async Task Work()
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
                var image = Load(path);
                string text = image == null ? "" : await TextRecognizer.RecognizeAsync(image) ?? "";
                lock (_lock) _entries[path] = new Entry(File.GetLastWriteTimeUtc(path).Ticks, text.Replace('\n', ' ').Replace('\r', ' '));
                if (++_sinceSave >= 10) Save();
                Updated?.Invoke();
            }
            catch
            {
                lock (_lock) _entries[path] = new Entry(File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0, "");
            }
            await Task.Delay(60); // stay out of the way of whatever else is running
        }
        Save();
        Updated?.Invoke();
    }

    private static BitmapSource? Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = stream;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    public void Save()
    {
        try
        {
            Dictionary<string, Entry> copy;
            lock (_lock)
            {
                // forget files that are gone
                foreach (var gone in _entries.Keys.Where(k => !File.Exists(k)).ToList()) _entries.Remove(gone);
                copy = new Dictionary<string, Entry>(_entries);
                _sinceSave = 0;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(copy));
        }
        catch { }
    }

    /// <summary>True when every word of the search appears in the name, the date or the text.</summary>
    public bool Matches(string path, string caption, string query)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0) return true;
        string haystack = Path.GetFileName(path) + " " + caption + " " + TextOf(path);
        return terms.All(t => haystack.Contains(t, StringComparison.CurrentCultureIgnoreCase));
    }
}
