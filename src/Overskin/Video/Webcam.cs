using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace Overskin;

/// <summary>A camera Windows knows about.</summary>
public sealed record CameraDevice(string Id, string Name);

/// <summary>
/// Keeps the newest picture from a webcam, for drawing into a recording as a bubble.
/// Uses Windows' own camera support, so it works with any camera the Camera app can use.
/// </summary>
public sealed class Webcam : IDisposable
{
    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private readonly object _lock = new();
    private byte[]? _pixels;
    private int _w, _h;

    /// <summary>The cameras plugged in.</summary>
    public static async Task<List<CameraDevice>> CamerasAsync()
    {
        var list = new List<CameraDevice>();
        try
        {
            var devices = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(Windows.Devices.Enumeration.DeviceClass.VideoCapture);
            foreach (var d in devices) list.Add(new CameraDevice(d.Id, d.Name));
        }
        catch
        {
            // no camera support
        }
        return list;
    }

    /// <summary>Starts the camera. Throws with a readable message when it can't.</summary>
    public static async Task<Webcam> OpenAsync(string? deviceId)
    {
        var cam = new Webcam();
        try
        {
            var settings = new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                SharingMode = MediaCaptureSharingMode.SharedReadOnly,
            };
            if (!string.IsNullOrEmpty(deviceId)) settings.VideoDeviceId = deviceId;
            cam._capture = new MediaCapture();
            await cam._capture.InitializeAsync(settings);
            var source = cam._capture.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color)
                         ?? throw new InvalidOperationException("The camera doesn't give a colour picture.");
            cam._reader = await cam._capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
            cam._reader.FrameArrived += cam.OnFrame;
            var status = await cam._reader.StartAsync();
            if (status != MediaFrameReaderStartStatus.Success) throw new InvalidOperationException("The camera wouldn't start (" + status + ").");
            return cam;
        }
        catch (UnauthorizedAccessException)
        {
            cam.Dispose();
            throw new InvalidOperationException("Windows blocked the camera. Turn on Settings → Privacy → Camera → \"Let desktop apps access your camera\".");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            cam.Dispose();
            throw new InvalidOperationException("The camera wouldn't start: " + ex.Message);
        }
        catch
        {
            cam.Dispose();
            throw;
        }
    }

    private void OnFrame(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        using var frame = sender.TryAcquireLatestFrame();
        var bitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
        if (bitmap == null) return;
        SoftwareBitmap? converted = null;
        try
        {
            if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
                bitmap = converted = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
            int w = bitmap.PixelWidth, h = bitmap.PixelHeight;
            var px = new byte[w * h * 4];
            bitmap.CopyToBuffer(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(px));
            lock (_lock)
            {
                _pixels = px;
                _w = w;
                _h = h;
            }
        }
        catch
        {
            // a frame that couldn't be read is skipped
        }
        finally
        {
            converted?.Dispose();
        }
    }

    /// <summary>The newest picture (BGRA, top-down), or null before the first one arrives.</summary>
    public (byte[] Pixels, int Width, int Height)? Latest()
    {
        lock (_lock) return _pixels == null ? null : (_pixels, _w, _h);
    }

    public void Dispose()
    {
        try
        {
            if (_reader != null)
            {
                _reader.FrameArrived -= OnFrame;
                _reader.StopAsync().AsTask().Wait(1000);
                _reader.Dispose();
            }
        }
        catch { }
        try { _capture?.Dispose(); } catch { }
        _reader = null;
        _capture = null;
    }
}

/// <summary>Draws the webcam into a video frame: a mirrored, centre-cropped circle or rounded square with a thin white edge.</summary>
public static class WebcamBubble
{
    /// <summary>Where the bubble goes in a <paramref name="w"/> × <paramref name="h"/> frame.</summary>
    public static (int X, int Y, int Size) Place(int w, int h, VideoOptions o)
    {
        int size = Math.Max(32, (int)(Math.Min(w, h) * o.WebcamSize / 100.0));
        size = Math.Min(size, Math.Min(w, h) - 8);
        int margin = Math.Max(8, (int)(Math.Min(w, h) * 0.03));
        int x = o.WebcamCorner.EndsWith("Left") ? margin : w - margin - size;
        int y = o.WebcamCorner.StartsWith("Top") ? margin : h - margin - size;
        return (x, y, size);
    }

    /// <summary>Blends the camera picture into <paramref name="frame"/> (BGRA, <paramref name="w"/> wide).</summary>
    public static void Draw(byte[] frame, int w, int h, byte[] cam, int cw, int ch, VideoOptions o)
    {
        var (bx, by, size) = Place(w, h, o);
        bool circle = o.WebcamShape == "Circle";
        double r = size / 2.0, corner = size * 0.18, border = Math.Max(1.5, size / 90.0);
        // the middle square of the camera picture
        int side = Math.Min(cw, ch), ox = (cw - side) / 2, oy = (ch - side) / 2;
        double k = side / (double)size;
        Parallel.For(0, size, y =>
        {
            int fy = by + y;
            if (fy < 0 || fy >= h) return;
            for (int x = 0; x < size; x++)
            {
                int fx = bx + x;
                if (fx < 0 || fx >= w) continue;
                // distance inside the shape's edge, in pixels (negative = outside)
                double px = x + 0.5, py = y + 0.5, inside;
                if (circle) inside = r - Math.Sqrt((px - r) * (px - r) + (py - r) * (py - r));
                else
                {
                    double dx = Math.Max(Math.Abs(px - r) - (r - corner), 0), dy = Math.Max(Math.Abs(py - r) - (r - corner), 0);
                    inside = dx > 0 && dy > 0 ? corner - Math.Sqrt(dx * dx + dy * dy) : Math.Min(r - Math.Abs(px - r), r - Math.Abs(py - r));
                }
                if (inside <= 0) continue;
                double alpha = Math.Min(1, inside);
                int o2 = (fy * w + fx) * 4;
                byte b, g, rr;
                if (inside < border)
                {
                    b = g = rr = 255; // the white edge
                }
                else
                {
                    // mirrored, like a mirror: what people expect to see of themselves
                    int sx = ox + Math.Clamp((int)((size - 1 - x) * k), 0, side - 1);
                    int sy = oy + Math.Clamp((int)(y * k), 0, side - 1);
                    int s = (sy * cw + sx) * 4;
                    b = cam[s]; g = cam[s + 1]; rr = cam[s + 2];
                }
                frame[o2] = (byte)(frame[o2] + (b - frame[o2]) * alpha);
                frame[o2 + 1] = (byte)(frame[o2 + 1] + (g - frame[o2 + 1]) * alpha);
                frame[o2 + 2] = (byte)(frame[o2 + 2] + (rr - frame[o2 + 2]) * alpha);
            }
        });
    }
}
