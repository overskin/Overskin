using Overskin.Linux.Platform;

namespace Overskin;

/// <summary>Subtitles on Mac and Linux: the video's sound is read with ffmpeg.</summary>
public static partial class Subtitles
{
    internal static async partial Task<float[]> ReadSoundAsync(string videoPath, CancellationToken cancel)
    {
        if (!ScreenGrabber.HasCommand("ffmpeg")) throw new InvalidOperationException(Converter.FfmpegHint);
        string wav = Path.Combine(Path.GetTempPath(), $"overskin-speech-{Guid.NewGuid():N}.wav");
        try
        {
            // 16 kHz, one channel, 16-bit: what Whisper listens to
            var (ok, error) = await ScreenGrabber.RunAsync("ffmpeg", $"-hide_banner -loglevel error -y -i \"{videoPath}\" -vn -ac 1 -ar {Rate} -c:a pcm_s16le \"{wav}\"", 900);
            if (!ok || !File.Exists(wav))
                throw new InvalidOperationException(error.Contains("does not contain any stream") || error.Contains("matches no streams") || error.Contains("Output file") && error.Contains("not contain")
                    ? "This video has no sound to make subtitles from." : "Couldn't read the sound in this video. " + error);
            return ReadWav(await File.ReadAllBytesAsync(wav, cancel));
        }
        finally
        {
            try { File.Delete(wav); } catch { }
        }
    }

    // Whisper.net finds its engine in runtimes/<platform> beside the app by itself
    static partial void FindEngine() { }
}
