using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Whisper.net;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace Overskin;

/// <summary>One line of subtitles: what was said and when (seconds of the original video).</summary>
public sealed record Caption(double Start, double End, string Text);

/// <summary>
/// Turns what's said in a video into timed subtitles, on this PC, with the Whisper speech model
/// that ships inside Overskin (OpenAI's Whisper, "base" size, through whisper.cpp). Nothing is
/// sent anywhere. It understands about 100 languages and works out which one is spoken.
/// </summary>
public static class Subtitles
{
    private const string ModelResource = "Overskin.whisper-model.bin";
    private const int Rate = 16000; // what Whisper listens at: 16 kHz, one channel

    /// <summary>Longest a subtitle line gets, in letters, before it's split into the next one.</summary>
    public const int LineLength = 48;

    private static byte[]? _model;
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    /// <summary>Whether the speech model is inside this copy of Overskin.</summary>
    public static bool Available => typeof(Subtitles).Assembly.GetManifestResourceInfo(ModelResource) != null;

    /// <summary>The subtitles for the speech in <paramref name="videoPath"/>; empty when nobody speaks.</summary>
    /// <exception cref="InvalidOperationException">The video has no sound, or it can't be read.</exception>
    public static async Task<List<Caption>> MakeAsync(string videoPath, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        await OneAtATime.WaitAsync(cancel);
        try
        {
            progress?.Report(0);
            float[] samples = await ReadSoundAsync(videoPath, cancel);
            progress?.Report(0.08);
            if (samples.Length < Rate / 2 || Loudest(samples, 0, samples.Length) < Quiet)
                return new List<Caption>();
            return await Task.Run(() => Listen(samples, progress, cancel), cancel);
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    // ---- the sound ---------------------------------------------------------------------------

    /// <summary>The video's sound as 16 kHz mono samples, decoded by Windows.</summary>
    internal static async Task<float[]> ReadSoundAsync(string videoPath, CancellationToken cancel = default)
    {
        string wav = Path.Combine(Path.GetTempPath(), $"Overskin-speech-{Guid.NewGuid():N}.wav");
        try
        {
            var source = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(videoPath));
            var props = await source.Properties.GetVideoPropertiesAsync();
            File.WriteAllBytes(wav, Array.Empty<byte>());
            var target = await StorageFile.GetFileFromPathAsync(wav);
            var profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.Low);
            profile.Audio = AudioEncodingProperties.CreatePcm(Rate, 1, 16);
            profile.Video = null;
            var transcoder = new MediaTranscoder();
            PrepareTranscodeResult prepared;
            try { prepared = await transcoder.PrepareFileTranscodeAsync(source, target, profile); }
            catch (Exception ex) { throw new InvalidOperationException("Windows couldn't read the sound in this video. " + ex.Message); }
            if (!prepared.CanTranscode)
                throw new InvalidOperationException("This video has no sound to make subtitles from.");
            await prepared.TranscodeAsync().AsTask(cancel);
            return ReadWav(File.ReadAllBytes(wav));
        }
        finally
        {
            try { File.Delete(wav); } catch { }
        }
    }

    /// <summary>The 16-bit samples in a WAV file, as numbers from -1 to 1.</summary>
    internal static float[] ReadWav(byte[] wav)
    {
        // find the "data" chunk rather than assume where it starts
        int at = 12;
        while (at + 8 <= wav.Length)
        {
            string id = Encoding.ASCII.GetString(wav, at, 4);
            int size = BitConverter.ToInt32(wav, at + 4);
            if (id == "data")
            {
                int length = Math.Min(size < 0 ? int.MaxValue : size, wav.Length - at - 8) / 2;
                var samples = new float[length];
                for (int i = 0; i < length; i++) samples[i] = BitConverter.ToInt16(wav, at + 8 + i * 2) / 32768f;
                return samples;
            }
            at += 8 + size + (size & 1);
        }
        return Array.Empty<float>();
    }

    /// <summary>Below this (about -45 dB) a stretch is taken as silence.</summary>
    private const double Quiet = 0.0056;

    /// <summary>The loudness (RMS) of the loudest tenth of a second between two samples.</summary>
    internal static double Loudest(float[] samples, int from, int to)
    {
        from = Math.Clamp(from, 0, samples.Length);
        to = Math.Clamp(to, from, samples.Length);
        const int Window = Rate / 10;
        double best = 0;
        for (int w = from; w < to; w += Window / 2)
        {
            int end = Math.Min(to, w + Window);
            double sum = 0;
            for (int i = w; i < end; i++) sum += samples[i] * samples[i];
            if (end > w) best = Math.Max(best, Math.Sqrt(sum / (end - w)));
        }
        return best;
    }

    // ---- listening ---------------------------------------------------------------------------

    private static byte[] Model()
    {
        if (_model != null) return _model;
        using var stream = typeof(Subtitles).Assembly.GetManifestResourceStream(ModelResource)
            ?? throw new InvalidOperationException("The speech model is missing from this copy of Overskin.");
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return _model = bytes;
    }

    /// <summary>
    /// Tells Whisper where its engine (whisper.dll and the ggml files beside it) is. Next to
    /// Overskin.exe in the Store copy; in the one-file GitHub copy, .NET unpacks it into a folder
    /// of its own, the same one as WPF's own native files, so it's found from where one of those
    /// was loaded.
    /// </summary>
    private static void FindEngine()
    {
        if (Whisper.net.LibraryLoader.RuntimeOptions.LibraryPath is { Length: > 0 }) return;
        string arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.Arm64 => "win-arm64",
            System.Runtime.InteropServices.Architecture.X86 => "win-x86",
            _ => "win-x64",
        };
        var folders = new List<string> { AppContext.BaseDirectory };
        foreach (var module in new[] { "PresentationNative_cor3.dll", "wpfgfx_cor3.dll", "D3DCompiler_47_cor3.dll" })
        {
            IntPtr handle = GetModuleHandle(module);
            if (handle == IntPtr.Zero) continue;
            var name = new StringBuilder(1024);
            if (GetModuleFileName(handle, name, name.Capacity) > 0 && Path.GetDirectoryName(name.ToString()) is { } dir) folders.Add(dir);
        }
        foreach (var folder in folders)
        {
            string dll = Path.Combine(folder, "runtimes", arch, "whisper.dll");
            // Whisper looks in "<the folder of LibraryPath>\runtimes\<arch>", so it's given a file in the folder above
            if (File.Exists(dll)) { Whisper.net.LibraryLoader.RuntimeOptions.LibraryPath = Path.Combine(folder, "whisper.dll"); return; }
        }
    }

    /// <summary>For the self-test: where the engine was looked for and found.</summary>
    internal static string EngineForTest() { FindEngine(); return Whisper.net.LibraryLoader.RuntimeOptions.LibraryPath ?? "(none)"; }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string name);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetModuleFileName(IntPtr module, StringBuilder name, int size);

    private static async Task<List<Caption>> Listen(float[] samples, IProgress<double>? progress, CancellationToken cancel)
    {
        FindEngine();
        using var factory = WhisperFactory.FromBuffer(Model());
        int threads = Math.Clamp(Environment.ProcessorCount - 1, 1, 8);
        await using var processor = factory.CreateBuilder()
            .WithLanguage("auto")
            .WithThreads(threads)
            .WithProgressHandler(p => progress?.Report(0.08 + 0.92 * Math.Clamp(p, 0, 100) / 100.0))
            .Build();
        // Whisper's own sentences (it's good at the words, rough at the times)
        var sentences = new List<Caption>();
        await foreach (var segment in processor.ProcessAsync(samples, cancel))
        {
            string text = Clean(segment.Text);
            double start = segment.Start.TotalSeconds, end = segment.End.TotalSeconds;
            if (text.Length == 0 || end <= start) continue;
            // Whisper sometimes "hears" words in silence: only keep sentences over real sound
            if (Loudest(samples, (int)(start * Rate), (int)(end * Rate)) < Quiet) continue;
            sentences.Add(new Caption(start, end, text));
        }
        progress?.Report(1);
        return Tidy(Align(sentences, VoiceRuns(samples)));
    }

    // ---- timing: lining the words up with the voice ---------------------------------------------

    private const double Frame = 0.02; // seconds in each loudness step

    /// <summary>
    /// The stretches where there's sound clearly above the background (seconds), with the short
    /// gaps between words joined up and clicks left out.
    /// </summary>
    internal static List<(double Start, double End)> VoiceRuns(float[] samples)
    {
        int step = (int)(Rate * Frame), frames = samples.Length / step;
        var level = new double[frames];
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            for (int i = f * step; i < (f + 1) * step; i++) sum += samples[i] * samples[i];
            level[f] = Math.Sqrt(sum / step);
        }
        if (frames == 0) return new();
        // the background: how loud the quieter fifth of the video is
        var sorted = level.OrderBy(v => v).ToArray();
        double floor = sorted[sorted.Length / 5];
        double threshold = Math.Max(Quiet, floor * 2.5);
        var runs = new List<(double Start, double End)>();
        int from = -1;
        for (int f = 0; f <= frames; f++)
        {
            bool loud = f < frames && level[f] >= threshold;
            if (loud && from < 0) from = f;
            if (!loud && from >= 0) { runs.Add((from * Frame, f * Frame)); from = -1; }
        }
        // gaps under a quarter of a second are between words, not pauses
        var joined = new List<(double Start, double End)>();
        foreach (var r in runs)
        {
            if (joined.Count > 0 && r.Start - joined[^1].End < 0.25) joined[^1] = (joined[^1].Start, r.End);
            else joined.Add(r);
        }
        return joined.Where(r => r.End - r.Start >= 0.08).ToList();
    }

    /// <summary>
    /// Moves each sentence onto the voice it belongs to (the stretches of sound it overlaps most),
    /// then splits it into lines short enough to read, timed by where in the voice each falls.
    /// A sentence with no voice found keeps Whisper's times.
    /// </summary>
    internal static List<Caption> Align(List<Caption> sentences, List<(double Start, double End)> runs)
    {
        sentences = sentences.OrderBy(s => s.Start).ToList();
        var owned = sentences.Select(_ => new List<(double Start, double End)>()).ToList();
        foreach (var run in runs)
        {
            // how much of the run each sentence covers
            var overlaps = sentences.Select((s, i) => (i, o: Math.Min(run.End, s.End) - Math.Max(run.Start, s.Start))).Where(x => x.o > 0).ToList();
            if (overlaps.Count == 0)
            {
                // just outside every sentence: the nearest one, if it's close
                int near = -1; double best = 0.6;
                for (int i = 0; i < sentences.Count; i++)
                {
                    double d = Math.Max(sentences[i].Start - run.End, run.Start - sentences[i].End);
                    if (d < best) { best = d; near = i; }
                }
                if (near >= 0) owned[near].Add(run);
                continue;
            }
            var big = overlaps.Where(x => x.o >= 0.3).ToList();
            if (big.Count <= 1)
            {
                owned[overlaps.MaxBy(x => x.o).i].Add(run);
                continue;
            }
            // one long stretch of talk across two sentences: split it where Whisper split them
            foreach (var (i, _) in big)
            {
                double s = i == big[0].i ? run.Start : Math.Max(run.Start, sentences[i].Start);
                double e = i == big[^1].i ? run.End : Math.Min(run.End, sentences[i].End);
                if (e > s) owned[i].Add((s, e));
            }
        }

        var lines = new List<Caption>();
        for (int i = 0; i < sentences.Count; i++)
        {
            var voice = owned[i].OrderBy(r => r.Start).ToList();
            if (voice.Count == 0) voice.Add((sentences[i].Start, sentences[i].End));
            var parts = Split(sentences[i].Text);
            // each part takes its share of the voiced time, by its length in letters
            double talk = voice.Sum(r => r.End - r.Start);
            int letters = parts.Sum(p => p.Length + 1);
            int done = 0;
            foreach (var part in parts)
            {
                double a = TimeAt(voice, talk * done / letters, start: true);
                done += part.Length + 1;
                double b = TimeAt(voice, talk * done / letters, start: false);
                if (b > a) lines.Add(new Caption(a, b, part));
            }
        }
        return lines;

        // the moment a given amount of talking time in: past a pause, it's the start of the next stretch
        static double TimeAt(List<(double Start, double End)> voice, double into, bool start)
        {
            foreach (var (s, e) in voice)
            {
                double len = e - s;
                if (into < len || (!start && into <= len + 1e-9)) return s + into;
                into -= len;
            }
            return voice[^1].End;
        }
    }

    /// <summary>
    /// A sentence in lines of at most <see cref="LineLength"/> letters, split between words,
    /// after a comma or full stop where there's one near the middle, and evened out.
    /// </summary>
    internal static List<string> Split(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (text.Length <= LineLength || words.Length < 2) return new List<string> { text };
        var lines = new List<string>();
        int w = 0;
        while (w < words.Length)
        {
            string rest = string.Join(' ', words.Skip(w));
            if (rest.Length <= LineLength) { lines.Add(rest); break; }
            double aim = rest.Length / Math.Ceiling(rest.Length / (double)LineLength);
            // the best place to break: near the even share, better after punctuation, never too long
            int bestEnd = w + 1; double bestScore = double.MaxValue;
            int length = -1;
            for (int k = w; k < words.Length - 1; k++)
            {
                length += words[k].Length + 1;
                if (length > LineLength) break;
                double score = Math.Abs(length - aim);
                if (words[k].EndsWith(',') || words[k].EndsWith('.') || words[k].EndsWith('?') || words[k].EndsWith('!') || words[k].EndsWith(';')) score -= 8;
                if (score < bestScore) { bestScore = score; bestEnd = k + 1; }
            }
            lines.Add(string.Join(' ', words.Skip(w).Take(bestEnd - w)));
            w = bestEnd;
        }
        return lines;
    }

    /// <summary>Trims a line and drops the ones that are only a sound description: [Music], (laughs), ♪.</summary>
    internal static string Clean(string text)
    {
        text = Regex.Replace(text, @"\[[^\]]*\]|\([^)]*\)|♪|\*[^*]*\*", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (text.Trim('.', ',', '-', '!', '?', ' ').Length == 0) return "";
        return text;
    }

    /// <summary>
    /// Joins the lines up into readable subtitles: no gaps flickering between lines spoken one after
    /// another, each up long enough to read, and none on top of the next.
    /// </summary>
    internal static List<Caption> Tidy(List<Caption> lines)
    {
        lines = lines.OrderBy(l => l.Start).ToList();
        var tidy = new List<Caption>();
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            double nextStart = i + 1 < lines.Count ? lines[i + 1].Start : double.MaxValue;
            // a short line stays up a little longer, but never into the next one
            double end = Math.Max(l.End, l.Start + Math.Min(1.2, 0.3 + l.Text.Length * 0.05));
            // a small gap before the next line is closed, so the subtitles don't blink
            if (nextStart - end < 0.4) end = nextStart;
            end = Math.Min(end, nextStart);
            if (end - l.Start < 0.1) continue;
            tidy.Add(l with { End = end });
        }
        return tidy;
    }

    // ---- .srt files ----------------------------------------------------------------------------

    /// <summary>The subtitles as an .srt file, the kind YouTube, VLC and video editors read.</summary>
    public static string ToSrt(IEnumerable<Caption> captions)
    {
        var sb = new StringBuilder();
        int n = 1;
        foreach (var c in captions)
        {
            sb.Append(n++).Append("\r\n");
            sb.Append(Stamp(c.Start)).Append(" --> ").Append(Stamp(c.End)).Append("\r\n");
            sb.Append(c.Text.Replace("\r", "")).Append("\r\n\r\n");
        }
        return sb.ToString();

        static string Stamp(double s)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, s));
            return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}";
        }
    }
}
