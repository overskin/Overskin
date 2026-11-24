#if DEBUG
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.SpeechSynthesis;
using Windows.Storage;

namespace Overskin;

/// <summary>
/// Self-tests for subtitles: Windows' own voice reads known sentences into a test video, and the
/// subtitles made from it are checked for the words, their times, silence and the saved files.
/// </summary>
public partial class App
{
    private const string SpokenFirst = "Welcome to Overskin. This video shows how to pin a screenshot on top of every window.";
    private const string SpokenSecond = "After a short pause, the subtitles should start again right here.";

    private static void SubtitleTests(List<string> log)
    {
        string tmp = Path.Combine(Path.GetTempPath(), "Overskin-subtitles");
        Directory.CreateDirectory(tmp);
        string? outDir = Environment.GetEnvironmentVariable("OVERSKIN_TEST_OUT") is { Length: > 0 } o ? o : null;

        // ---- small pieces: cleaning lines, joining them up, .srt ----
        try
        {
            string a = Subtitles.Clean("  [Music] "), b = Subtitles.Clean(" (laughs) Hello there. "), c = Subtitles.Clean("♪ ♪"), d = Subtitles.Clean(" ...");
            var tidy = Subtitles.Tidy(new List<Caption> { new(1.0, 1.2, "Hi."), new(1.5, 3.0, "Second line here"), new(6.0, 7.0, "Later") });
            string srt = Subtitles.ToSrt(new[] { new Caption(0.5, 2.25, "One"), new Caption(3661.0, 3662.5, "Two") });
            string sentence = "This video shows how to pin a screenshot on top of every window, and then how to fade it so you can trace over it.";
            var split = Subtitles.Split(sentence);
            log.Add($"subtitle lines: [{string.Join(" | ", split)}] longest={split.Max(s => s.Length)} (expect ≤ {Subtitles.LineLength}, even lengths, a break after the comma) " +
                    $"same words={string.Join(" ", split) == sentence} (expect True)");
            // one stretch of talk with a pause in it, and Whisper's times half a second early
            var aligned = Subtitles.Align(new List<Caption> { new(0.0, 4.0, "First sentence here."), new(4.0, 9.0, "Second one.") },
                new List<(double, double)> { (1.0, 3.0), (6.0, 8.0) });
            log.Add($"subtitle aligning: {string.Join(" ", aligned.Select(t => $"{t.Start:0.0}-{t.End:0.0}"))} (expect 1.0-3.0 6.0-8.0)");
            log.Add($"subtitle pieces: clean=[{a}|{b}|{c}|{d}] (expect [|Hello there.||]); " +
                    $"tidy={string.Join(" ", tidy.Select(t => $"{t.Start:0.0}-{t.End:0.0}"))} (expect 1.0-1.5 1.5-3.0 6.0-7.0); " +
                    $"srt ok={srt.Contains("00:00:00,500 --> 00:00:02,250") && srt.Contains("01:01:01,000 --> 01:01:02,500") && srt.StartsWith("1\r\n")} (expect True); " +
                    $"model inside={Subtitles.Available} (expect True); engine={Subtitles.EngineForTest()}");
        }
        catch (Exception ex) { log.Add("subtitle pieces: FAILED " + ex); }

        // ---- a long line wraps to fit the video, and stays centred ----
        try
        {
            var cap = new VideoOverlay { Kind = VideoOverlay.Kinds.Caption, Text = "This is a long subtitle line that has to wrap onto a second line to fit", A = new Point(640, 680) };
            var (pic, at) = cap.Render(720, 900); // a narrow video: the line has to wrap
            var one = new VideoOverlay { Kind = VideoOverlay.Kinds.Caption, Text = "Short", A = new Point(640, 680) };
            var (pic1, at1) = one.Render(720, 1280);
            double mid = at.X + pic.PixelWidth / 2.0, mid1 = at1.X + pic1.PixelWidth / 2.0;
            log.Add($"subtitle drawing: long {pic.PixelWidth}×{pic.PixelHeight} (expect width ≤ 765, two lines: height > {pic1.PixelHeight * 1.6:0}); " +
                    $"bottom={at.Y + pic.PixelHeight:0} (expect about 680); centre long={mid:0} short={mid1:0} (expect both about 640)");
            if (outDir != null) { SavePng(pic, Path.Combine(outDir, "subtitle-long.png")); SavePng(pic1, Path.Combine(outDir, "subtitle-short.png")); }
        }
        catch (Exception ex) { log.Add("subtitle drawing: FAILED " + ex); }

        // ---- the real thing: speech in a video becomes the right words at the right times ----
        string speech = Path.Combine(tmp, "speech.mp4");
        try
        {
            var sw = Stopwatch.StartNew();
            string ssml = "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'>" +
                          "<break time='1500ms'/>" + SpokenFirst + "<break time='3000ms'/>" + SpokenSecond + "<break time='1000ms'/></speak>";
            string wav = Path.Combine(tmp, "speech.wav");
            Task.Run(() => Speak(ssml, wav)).GetAwaiter().GetResult();
            Task.Run(() => MakeVideo(wav, speech)).GetAwaiter().GetResult();
            long made = sw.ElapsedMilliseconds;
            double length = Task.Run(() => VideoTrim.DurationAsync(speech)).GetAwaiter().GetResult().TotalSeconds;
            sw.Restart();
            var lines = Task.Run(() => Subtitles.MakeAsync(speech)).GetAwaiter().GetResult();
            long took = sw.ElapsedMilliseconds;
            string heard = string.Join(" ", lines.Select(l => l.Text));
            double matched = WordMatch(SpokenFirst + " " + SpokenSecond, heard);
            // where the second sentence starts: the first line whose words begin it
            var second = lines.FirstOrDefault(l => Regex.IsMatch(l.Text, @"\b(After|pause)\b", RegexOptions.IgnoreCase));
            bool inOrder = lines.Zip(lines.Skip(1)).All(p => p.First.End <= p.Second.Start + 0.001);
            int longest = lines.Count == 0 ? 0 : lines.Max(l => l.Text.Length);
            // the real pause, read straight off the voice: the longest stretch of near silence inside it
            var (speechStart, pauseFrom, pauseTo) = PauseIn(wav);
            double inPause = lines.Max(l => Math.Max(0, Math.Min(l.End, pauseTo) - Math.Max(l.Start, pauseFrom)));
            log.Add($"subtitles from speech: video {length:0.0} s made in {made} ms; listened in {took} ms; {lines.Count} lines, longest {longest} letters (expect ≤ {Subtitles.LineLength})");
            log.Add($"subtitles words: {matched:P0} of the spoken words found (expect 90% or more); in order, no overlaps={inOrder} (expect True)");
            log.Add($"subtitles timing: voice starts {speechStart:0.00} s, first line {lines.FirstOrDefault()?.Start:0.00} s (expect within 0.25); " +
                    $"pause {pauseFrom:0.00}–{pauseTo:0.00} s, most of it under a line {inPause:0.00} s (expect under 0.5, the subtitle staying a moment after the voice); " +
                    $"second sentence {second?.Start:0.00} s (expect within 0.3 of {pauseTo:0.00})");
            foreach (var l in lines) log.Add($"    {l.Start,6:0.00} – {l.End,6:0.00}  {l.Text}");
        }
        catch (Exception ex) { log.Add("subtitles from speech: FAILED " + ex); }

        // ---- silence and no sound at all: nothing made up ----
        try
        {
            string quietWav = Path.Combine(tmp, "quiet.wav"), quiet = Path.Combine(tmp, "quiet.mp4");
            WriteWav(quietWav, new short[16000 * 4], 16000);
            Task.Run(() => MakeVideo(quietWav, quiet)).GetAwaiter().GetResult();
            var none = Task.Run(() => Subtitles.MakeAsync(quiet)).GetAwaiter().GetResult();
            // a hum (a quiet 120 Hz tone) with no words
            string humWav = Path.Combine(tmp, "hum.wav"), hum = Path.Combine(tmp, "hum.mp4");
            var tone = new short[16000 * 5];
            for (int i = 0; i < tone.Length; i++) tone[i] = (short)(Math.Sin(i * 2 * Math.PI * 120 / 16000) * 1500);
            WriteWav(humWav, tone, 16000);
            Task.Run(() => MakeVideo(humWav, hum)).GetAwaiter().GetResult();
            var humLines = Task.Run(() => Subtitles.MakeAsync(hum)).GetAwaiter().GetResult();
            string mute = Path.Combine(tmp, "mute.mp4");
            Task.Run(() => MakeVideo(null, mute)).GetAwaiter().GetResult();
            string muteResult;
            try { muteResult = $"{Task.Run(() => Subtitles.MakeAsync(mute)).GetAwaiter().GetResult().Count} lines"; }
            catch (InvalidOperationException ex) { muteResult = "message: " + ex.Message; }
            log.Add($"subtitles from silence: {none.Count} lines (expect 0); from a hum: {humLines.Count} lines [{string.Join(" | ", humLines.Select(l => l.Text))}] (expect 0); " +
                    $"no sound at all: {muteResult} (expect a message or 0 lines)");
        }
        catch (Exception ex) { log.Add("subtitles from silence: FAILED " + ex); }

        // ---- the editor: lines listed, saved into the video, .srt beside it with times after a cut ----
        try
        {
            if (!File.Exists(speech)) throw new InvalidOperationException("no speech video from the test above");
            var ed = new VideoEditorWindow(speech, _ => { }) { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
            ed.Show();
            Pump(900);
            ed.SetCaptionsForTest(new[] { new Caption(1.5, 4.0, "First line"), new Caption(5.0, 6.5, "Second line"), new Caption(8.0, 9.0, "Third line") });
            Pump(200);
            int rows = ed.SubtitleRowsForTest, count = ed.CaptionCountForTest;
            ed.SubtitlesCard.BringIntoView();
            ed.Player.Position = TimeSpan.FromSeconds(2.0);
            ed.RedrawForTest();
            SaveWindow(ed, "video-editor-subtitles.png");
            // cut 4.5 to 7.0: the second line goes, the third moves 2.5 s earlier
            ed.CutForTest(4.5, 7.0);
            string saved = Path.Combine(tmp, "speech edited.mp4");
            File.Delete(saved);
            File.Delete(Path.ChangeExtension(saved, ".srt"));
            var save = ed.SaveForTest(saved);
            var until = DateTime.UtcNow.AddSeconds(90);
            while (!save.IsCompleted && DateTime.UtcNow < until) Pump(50);
            ed.Close();
            string srtPath = Path.ChangeExtension(saved, ".srt");
            string srt = File.Exists(srtPath) ? File.ReadAllText(srtPath) : "";
            // the saved video shows the first line at 2 s: the bottom middle is the dark band, not the plain picture
            var frame = MediaReader.FrameAt(saved, TimeSpan.FromSeconds(2.5));
            var plain = MediaReader.FrameAt(saved, TimeSpan.FromSeconds(0.5));
            double band = frame == null ? -1 : Brightness(frame, 0.5, 0.92), bare = plain == null ? -1 : Brightness(plain, 0.5, 0.92);
            if (outDir != null && frame != null) SavePng(frame, Path.Combine(outDir, "subtitle-in-video.png"));
            log.Add($"subtitles in the editor: rows={rows} lines={count} (expect 3 3); saved={save.IsCompleted && save.Result} " +
                    $"bottom-middle brightness with a line={band:0} without={bare:0} (expect clearly different)");
            log.Add($"subtitles .srt: {srt.Replace("\r\n", " / ").Trim()} " +
                    "(expect 1 / 00:00:01,500 --> 00:00:04,000 / First line / 2 / 00:00:05,500 --> 00:00:06,500 / Third line)");
        }
        catch (Exception ex) { log.Add("subtitles in the editor: FAILED " + ex); }
    }

    /// <summary>Where the voice in a WAV starts, and its longest pause (seconds), from the raw samples.</summary>
    private static (double SpeechStart, double PauseFrom, double PauseTo) PauseIn(string wav)
    {
        var bytes = File.ReadAllBytes(wav);
        int channels = BitConverter.ToInt16(bytes, 22), rate = BitConverter.ToInt32(bytes, 24), bits = BitConverter.ToInt16(bytes, 34);
        int at = 12;
        while (Encoding.ASCII.GetString(bytes, at, 4) != "data") at += 8 + BitConverter.ToInt32(bytes, at + 4);
        int data = at + 8, frame = channels * bits / 8, count = (bytes.Length - data) / frame;
        bool Loud(int i) => Math.Abs(BitConverter.ToInt16(bytes, data + i * frame)) > 300;
        int first = 0;
        while (first < count && !Loud(first)) first++;
        int last = count - 1;
        while (last > first && !Loud(last)) last--;
        int bestFrom = 0, bestLen = 0, quietFrom = -1;
        for (int i = first; i <= last; i++)
        {
            if (!Loud(i)) { if (quietFrom < 0) quietFrom = i; }
            else if (quietFrom >= 0) { if (i - quietFrom > bestLen) { bestLen = i - quietFrom; bestFrom = quietFrom; } quietFrom = -1; }
        }
        return (first / (double)rate, bestFrom / (double)rate, (bestFrom + bestLen) / (double)rate);
    }

    /// <summary>Windows' own voice reads the SSML into a WAV file.</summary>
    private static async Task Speak(string ssml, string wav)
    {
        using var synth = new SpeechSynthesizer();
        using var stream = await synth.SynthesizeSsmlToStreamAsync(ssml);
        using var reader = new global::Windows.Storage.Streams.DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        var bytes = new byte[stream.Size];
        reader.ReadBytes(bytes);
        await File.WriteAllBytesAsync(wav, bytes);
    }

    /// <summary>A plain 1280×720 MP4 as long as the sound, with that sound (or none).</summary>
    private static async Task MakeVideo(string? wav, string mp4, double silentLength = 4)
    {
        var composition = new MediaComposition();
        TimeSpan length = TimeSpan.FromSeconds(silentLength);
        BackgroundAudioTrack? track = null;
        if (wav != null)
        {
            track = await BackgroundAudioTrack.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(wav));
            length = track.OriginalDuration;
        }
        composition.Clips.Add(MediaClip.CreateFromColor(global::Windows.UI.Color.FromArgb(255, 40, 90, 160), length));
        if (track != null) composition.BackgroundAudioTracks.Add(track);
        File.WriteAllBytes(mp4, Array.Empty<byte>());
        var target = await StorageFile.GetFileFromPathAsync(mp4);
        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD720p);
        if (wav == null) profile.Audio = null;
        await composition.RenderToFileAsync(target, MediaTrimmingPreference.Precise, profile);
    }

    private static void WriteWav(string path, short[] samples, int rate)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8.ToArray()); w.Write(36 + samples.Length * 2); w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8.ToArray()); w.Write(samples.Length * 2);
        foreach (var s in samples) w.Write(s);
    }

    /// <summary>The share of the expected words that turn up, in order, in what was heard.</summary>
    private static double WordMatch(string expected, string heard)
    {
        static string[] Words(string s) => Regex.Matches(s.ToLowerInvariant(), @"[a-z0-9']+").Select(m => m.Value).ToArray();
        var a = Words(expected); var b = Words(heard);
        // longest common subsequence of words
        var dp = new int[a.Length + 1, b.Length + 1];
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                dp[i, j] = a[i - 1] == b[j - 1] ? dp[i - 1, j - 1] + 1 : Math.Max(dp[i - 1, j], dp[i, j - 1]);
        return a.Length == 0 ? 0 : dp[a.Length, b.Length] / (double)a.Length;
    }

    /// <summary>Average brightness of a small patch at a spot given as fractions of the width and height.</summary>
    private static double Brightness(BitmapSource image, double fx, double fy)
    {
        var src = new FormatConvertedBitmap(image, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        int w = src.PixelWidth, h = src.PixelHeight, size = 6;
        int x = (int)(fx * w) - size / 2, y = (int)(fy * h) - size / 2;
        var px = new byte[size * size * 4];
        src.CopyPixels(new Int32Rect(x, y, size, size), px, size * 4, 0);
        double sum = 0;
        for (int i = 0; i < px.Length; i += 4) sum += (px[i] + px[i + 1] + px[i + 2]) / 3.0;
        return sum / (size * size);
    }

    private static void SavePng(BitmapSource image, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(image));
        using var f = File.Create(path);
        enc.Save(f);
    }
}
#endif
