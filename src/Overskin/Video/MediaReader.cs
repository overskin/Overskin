using System.Runtime.InteropServices;

namespace Overskin;

/// <summary>
/// Reads a video's frames one after another as BGRA pixels, with Windows' own Media Foundation
/// (the same decoder the Photos app uses), so a recording can be cut and zoomed afresh.
/// </summary>
internal sealed unsafe class MediaReader : IDisposable
{
    private IntPtr _reader;
    public int Width { get; }
    public int Height { get; }
    /// <summary>Frames per second the video says it has (30 when it doesn't say).</summary>
    public double FrameRate { get; } = 30;
    private readonly int _stride;

    private const uint FirstVideoStream = 0xFFFFFFFC;
    private const int EndOfStream = 0x2;

    private static readonly Guid EnableVideoProcessing = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
    private static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid FormatRgb32 = new("00000016-0000-0010-8000-00AA00389B71");
    private static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private static readonly Guid FrameRateKey = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");

    [DllImport("mfplat.dll")] private static extern int MFStartup(int version, int flags);
    [DllImport("mfplat.dll")] private static extern int MFCreateAttributes(out IntPtr attributes, int size);
    [DllImport("mfplat.dll")] private static extern int MFCreateMediaType(out IntPtr type);
    [DllImport("mfreadwrite.dll")] private static extern int MFCreateSourceReaderFromURL([MarshalAs(UnmanagedType.LPWStr)] string url, IntPtr attributes, out IntPtr reader);

    private static void* Slot(IntPtr obj, int index) => (*(void***)obj)[index];
    private static void Release(IntPtr obj) { if (obj != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(obj, 2))(obj); }
    private static void Check(int hr, string what) { if (hr < 0) throw new InvalidOperationException($"Couldn't read the video ({what}, 0x{hr:X8})."); }

    // IMFAttributes
    private static int SetUInt32(IntPtr a, Guid key, uint v) => ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, int>)Slot(a, 21))(a, &key, v);
    private static int SetGuid(IntPtr a, Guid key, Guid v) => ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)Slot(a, 24))(a, &key, &v);
    private static int GetUInt64(IntPtr a, Guid key, out ulong v) { ulong r; int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, ulong*, int>)Slot(a, 8))(a, &key, &r); v = r; return hr; }
    private static int GetUInt32(IntPtr a, Guid key, out uint v) { uint r; int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint*, int>)Slot(a, 7))(a, &key, &r); v = r; return hr; }

    public MediaReader(string path)
    {
        Check(MFStartup(0x20070, 0), "start");
        Check(MFCreateAttributes(out var attributes, 1), "settings");
        try
        {
            // let Windows convert whatever the video holds into plain 32-bit pixels
            SetUInt32(attributes, EnableVideoProcessing, 1);
            Check(MFCreateSourceReaderFromURL(System.IO.Path.GetFullPath(path), attributes, out _reader), "open");
        }
        finally { Release(attributes); }

        Check(MFCreateMediaType(out var wanted), "type");
        try
        {
            SetGuid(wanted, MajorType, MediaTypeVideo);
            SetGuid(wanted, Subtype, FormatRgb32);
            // IMFSourceReader::SetCurrentMediaType (7)
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, int>)Slot(_reader, 7))(_reader, FirstVideoStream, IntPtr.Zero, wanted), "pixels");
        }
        finally { Release(wanted); }

        // IMFSourceReader::GetCurrentMediaType (6)
        IntPtr current;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(_reader, 6))(_reader, FirstVideoStream, &current), "size");
        try
        {
            GetUInt64(current, FrameSize, out ulong size);
            Width = (int)(size >> 32);
            Height = (int)(size & 0xFFFFFFFF);
            _stride = GetUInt32(current, DefaultStride, out uint stride) >= 0 ? unchecked((int)stride) : Width * 4;
            if (GetUInt64(current, FrameRateKey, out ulong rate) >= 0 && (rate & 0xFFFFFFFF) != 0)
            {
                double fps = (rate >> 32) / (double)(rate & 0xFFFFFFFF);
                if (fps is > 1 and < 1000) FrameRate = fps;
            }
        }
        finally { Release(current); }
    }

    /// <summary>For the self-test: the size of the last frame's buffer, and the row width read from the video.</summary>
    internal uint LastLength;
    internal int StrideForTest => _stride;

    /// <summary>The next frame (top row first) and when it's shown, or null at the end.</summary>
    public (byte[] Pixels, TimeSpan Time)? Next()
    {
        while (true)
        {
            uint actual, flags;
            long time;
            IntPtr sample;
            // IMFSourceReader::ReadSample (9)
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint*, uint*, long*, IntPtr*, int>)Slot(_reader, 9))(
                _reader, FirstVideoStream, 0, &actual, &flags, &time, &sample), "frame");
            if ((flags & EndOfStream) != 0) { Release(sample); return null; }
            if (sample == IntPtr.Zero) continue;
            try
            {
                IntPtr buffer;
                // IMFSample::ConvertToContiguousBuffer (41)
                Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(sample, 41))(sample, &buffer), "buffer");
                try
                {
                    byte* data;
                    uint max, length;
                    // IMFMediaBuffer::Lock (3) / Unlock (4)
                    Check(((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, int>)Slot(buffer, 3))(buffer, &data, &max, &length), "lock");
                    LastLength = length;
                    var pixels = new byte[Width * Height * 4];
                    int stride = Math.Abs(_stride), row = Width * 4, rows = Height;
                    bool bottomUp = _stride < 0;
                    // Decoders work in blocks of 16 pixels: a 200 × 120 video can come out as 208 × 128,
                    // with the frame size and row width still saying 200. The buffer's own size tells.
                    long total = length;
                    if (total > (long)stride * Height && total % 4 == 0)
                    {
                        bool done = false;
                        foreach (int align in new[] { 16, 32, 64, 128 })
                        {
                            int aw = (Width + align - 1) / align * align;
                            foreach (int ah in new[] { Height, (Height + 15) / 16 * 16, (Height + 31) / 32 * 32 })
                                if ((long)aw * ah * 4 == total) { stride = aw * 4; rows = ah; done = true; break; }
                            if (done) break;
                        }
                    }
                    int lastRow = bottomUp ? rows - 1 : 0;
                    fixed (byte* dst = pixels)
                        for (int y = 0; y < Height; y++)
                        {
                            byte* from = data + (long)(bottomUp ? lastRow - y : y) * stride;
                            Buffer.MemoryCopy(from, dst + (long)y * row, row, row);
                            for (int x = 3; x < row; x += 4) dst[(long)y * row + x] = 255;
                        }
                    ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(buffer, 4))(buffer);
                    return (pixels, TimeSpan.FromTicks(time));
                }
                finally { Release(buffer); }
            }
            finally { Release(sample); }
        }
    }

    /// <summary>Jumps to a moment (the key frame before it; <see cref="FrameAt"/> reads on to the exact frame).</summary>
    public void Seek(TimeSpan time)
    {
        // a PROPVARIANT holding a 64-bit number (VT_I8) of 100-nanosecond units; GUID_NULL means that format
        var variant = stackalloc byte[24];
        new Span<byte>(variant, 24).Clear();
        *(ushort*)variant = 20;
        *(long*)(variant + 8) = Math.Max(0, time.Ticks);
        Guid format = Guid.Empty;
        // IMFSourceReader::SetCurrentPosition (8)
        Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, byte*, int>)Slot(_reader, 8))(_reader, &format, variant), "seek");
    }

    /// <summary>The frame showing at <paramref name="time"/> in a video, top row first, as a picture.</summary>
    public static System.Windows.Media.Imaging.BitmapSource FrameAt(string path, TimeSpan time)
    {
        using var reader = new MediaReader(path);
        reader.Seek(time);
        (byte[] Pixels, TimeSpan Time)? best = null;
        double half = 0.5 / reader.FrameRate;
        while (reader.Next() is { } frame)
        {
            if (best != null && frame.Time.TotalSeconds > time.TotalSeconds + half) break;
            best = frame;
            if (frame.Time.TotalSeconds >= time.TotalSeconds - half) break;
        }
        if (best is not { } b) throw new InvalidOperationException("Couldn't read that frame of the video.");
        var image = System.Windows.Media.Imaging.BitmapSource.Create(reader.Width, reader.Height, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, b.Pixels, reader.Width * 4);
        image.Freeze();
        return image;
    }

    public void Dispose()
    {
        Release(_reader);
        _reader = IntPtr.Zero;
    }
}
