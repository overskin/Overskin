using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace Overskin;

/// <summary>One frame of video: top-down BGRA pixels and when it was captured.</summary>
public readonly record struct VideoFrame(byte[] Pixels, TimeSpan Time);

/// <summary>
/// Encodes frames into an H.264 MP4 with Windows' own encoder (hardware when there is one),
/// pulling them from a queue as they arrive, so a long recording never sits in memory.
/// </summary>
public static class VideoEncoder
{
    private static byte[] FlipRows(byte[] pixels, int stride, int rows)
    {
        var flipped = new byte[pixels.Length];
        for (int r = 0; r < rows; r++)
            Buffer.BlockCopy(pixels, r * stride, flipped, (rows - 1 - r) * stride, stride);
        return flipped;
    }

    /// <summary>
    /// Encodes until <paramref name="frames"/> (and <paramref name="audio"/>, when there's sound) is
    /// completed and empty. Sound is 48 kHz stereo, saved as AAC.
    /// </summary>
    public static async Task EncodeAsync(string path, int width, int height, int fps, BlockingCollection<VideoFrame> frames,
        bool highQuality = true, BlockingCollection<AudioChunk>? audio = null)
    {
        var input = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)width, (uint)height);
        input.FrameRate.Numerator = (uint)fps;
        input.FrameRate.Denominator = 1;
        var videoStream = new VideoStreamDescriptor(input);
        AudioStreamDescriptor? audioStream = audio == null ? null
            : new AudioStreamDescriptor(AudioEncodingProperties.CreatePcm(AudioSource.Rate, 2, 16));
        var source = (audioStream == null ? new MediaStreamSource(videoStream) : new MediaStreamSource(videoStream, audioStream));
        source.BufferTime = TimeSpan.Zero;
        var frameLength = TimeSpan.FromSeconds(1.0 / fps);
        TimeSpan last = TimeSpan.MinValue, lastSound = TimeSpan.MinValue;

        source.Starting += (_, e) => e.Request.SetActualStartPosition(TimeSpan.Zero);
        source.SampleRequested += (_, e) =>
        {
            var request = e.Request;
            var deferral = request.GetDeferral();
            bool sound = audioStream != null && request.StreamDescriptor is AudioStreamDescriptor;
            // wait for the next frame or chunk on another thread, so one stream never holds up the other
            Task.Run(() =>
            {
                try
                {
                    if (sound)
                    {
                        if (audio!.TryTake(out var chunk, Timeout.Infinite))
                        {
                            var time = chunk.Time <= lastSound ? lastSound + TimeSpan.FromTicks(1) : chunk.Time;
                            lastSound = time;
                            var bytes = new byte[chunk.Samples.Length * 2];
                            Buffer.BlockCopy(chunk.Samples, 0, bytes, 0, bytes.Length);
                            var sample = MediaStreamSample.CreateFromBuffer(bytes.AsBuffer(), time);
                            sample.Duration = TimeSpan.FromSeconds(chunk.Samples.Length / 2.0 / AudioSource.Rate);
                            request.Sample = sample;
                        }
                        else request.Sample = null; // end of the sound
                    }
                    else if (frames.TryTake(out var frame, Timeout.Infinite))
                    {
                        var time = frame.Time <= last ? last + TimeSpan.FromMilliseconds(1) : frame.Time;
                        last = time;
                        // uncompressed RGB samples are read bottom row first
                        var sample = MediaStreamSample.CreateFromBuffer(FlipRows(frame.Pixels, width * 4, height).AsBuffer(), time);
                        sample.Duration = frameLength;
                        request.Sample = sample;
                    }
                    else
                    {
                        request.Sample = null; // end of the recording
                    }
                }
                catch (InvalidOperationException)
                {
                    request.Sample = null;
                }
                finally
                {
                    deferral.Complete();
                }
            });
        };

        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        profile.Audio = audio == null ? null : AudioEncodingProperties.CreateAac(AudioSource.Rate, 2, 192000);
        profile.Video.Width = (uint)width;
        profile.Video.Height = (uint)height;
        profile.Video.FrameRate.Numerator = (uint)fps;
        profile.Video.FrameRate.Denominator = 1;
        // screen content is mostly sharp edges: a generous bitrate keeps text crisp
        profile.Video.Bitrate = (uint)Math.Clamp(width * (double)height * fps * (highQuality ? 0.16 : 0.07), 2_000_000, 60_000_000);

        string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(folder);
        var storageFolder = await StorageFolder.GetFolderFromPathAsync(folder);
        var file = await storageFolder.CreateFileAsync(Path.GetFileName(path), CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);

        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true, AlwaysReencode = true };
        var prepared = await transcoder.PrepareMediaStreamSourceTranscodeAsync(source, stream, profile);
        if (!prepared.CanTranscode)
        {
            // drain so the recorder isn't left blocked on a full queue
            while (frames.TryTake(out _, Timeout.Infinite)) { }
            if (audio != null) while (audio.TryTake(out _, Timeout.Infinite)) { }
            throw new InvalidOperationException("Windows couldn't start the video encoder (" + prepared.FailureReason + ").");
        }
        await prepared.TranscodeAsync();
    }
}
