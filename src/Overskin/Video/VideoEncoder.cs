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

    /// <summary>Encodes until <paramref name="frames"/> is completed and empty.</summary>
    public static async Task EncodeAsync(string path, int width, int height, int fps, BlockingCollection<VideoFrame> frames, bool highQuality = true)
    {
        var input = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)width, (uint)height);
        input.FrameRate.Numerator = (uint)fps;
        input.FrameRate.Denominator = 1;
        var source = new MediaStreamSource(new VideoStreamDescriptor(input)) { BufferTime = TimeSpan.Zero };
        var frameLength = TimeSpan.FromSeconds(1.0 / fps);
        TimeSpan last = TimeSpan.MinValue;

        source.Starting += (_, e) => e.Request.SetActualStartPosition(TimeSpan.Zero);
        source.SampleRequested += (_, e) =>
        {
            var deferral = e.Request.GetDeferral();
            try
            {
                if (frames.TryTake(out var frame, Timeout.Infinite))
                {
                    var time = frame.Time <= last ? last + TimeSpan.FromMilliseconds(1) : frame.Time;
                    last = time;
                    // uncompressed RGB samples are read bottom row first
                    var sample = MediaStreamSample.CreateFromBuffer(FlipRows(frame.Pixels, width * 4, height).AsBuffer(), time);
                    sample.Duration = frameLength;
                    e.Request.Sample = sample;
                }
                else
                {
                    e.Request.Sample = null; // end of the recording
                }
            }
            catch (InvalidOperationException)
            {
                e.Request.Sample = null;
            }
            finally
            {
                deferral.Complete();
            }
        };

        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        profile.Audio = null;
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
            throw new InvalidOperationException("Windows couldn't start the video encoder (" + prepared.FailureReason + ").");
        }
        await prepared.TranscodeAsync();
    }
}
