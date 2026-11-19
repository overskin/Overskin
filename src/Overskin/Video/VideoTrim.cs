using System.IO;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace Overskin;

/// <summary>Cuts the start and end off a video, keeping its sound, with Windows' own editor.</summary>
public static class VideoTrim
{
    public static async Task<TimeSpan> DurationAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        var props = await file.Properties.GetVideoPropertiesAsync();
        return props.Duration;
    }

    /// <summary>Writes the part between <paramref name="start"/> and <paramref name="end"/> to <paramref name="output"/>.</summary>
    public static async Task TrimAsync(string input, string output, TimeSpan start, TimeSpan end, IProgress<double>? progress = null)
    {
        var source = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(input));
        var clip = await MediaClip.CreateFromFileAsync(source);
        var length = clip.OriginalDuration;
        start = TimeSpan.FromTicks(Math.Clamp(start.Ticks, 0, length.Ticks));
        end = TimeSpan.FromTicks(Math.Clamp(end.Ticks, start.Ticks, length.Ticks));
        if (end - start < TimeSpan.FromMilliseconds(100)) throw new InvalidOperationException("That's too short to save.");
        clip.TrimTimeFromStart = start;
        clip.TrimTimeFromEnd = length - end;

        var composition = new MediaComposition();
        composition.Clips.Add(clip);

        // the same size, frame rate and sound as the original
        var profile = await MediaEncodingProfile.CreateFromFileAsync(source);
        if (profile.Video != null)
            profile.Video.Bitrate = Math.Max(profile.Video.Bitrate, 4_000_000);

        string folder = Path.GetDirectoryName(Path.GetFullPath(output))!;
        Directory.CreateDirectory(folder);
        var storageFolder = await StorageFolder.GetFolderFromPathAsync(folder);
        var target = await storageFolder.CreateFileAsync(Path.GetFileName(output), CreationCollisionOption.ReplaceExisting);
        var render = composition.RenderToFileAsync(target, MediaTrimmingPreference.Precise, profile);
        if (progress != null) render.Progress = (_, p) => progress.Report(p / 100.0);
        var result = await render;
        if (result != TranscodeFailureReason.None)
        {
            try { await target.DeleteAsync(); } catch { }
            throw new InvalidOperationException("Windows couldn't save the video (" + result + ").");
        }
    }

    /// <summary>"clip.mp4" → "clip trimmed.mp4", or "clip trimmed 2.mp4" when that's taken.</summary>
    public static string TrimmedName(string path)
    {
        string dir = Path.GetDirectoryName(path) ?? "", name = Path.GetFileNameWithoutExtension(path);
        string candidate = Path.Combine(dir, name + " trimmed.mp4");
        for (int i = 2; File.Exists(candidate); i++) candidate = Path.Combine(dir, $"{name} trimmed {i}.mp4");
        return candidate;
    }
}
