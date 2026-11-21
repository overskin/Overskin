using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Overskin;

/// <summary>A slice of sound for the encoder: 48 kHz stereo 16-bit samples (left, right, left, …) and when it starts.</summary>
public readonly record struct AudioChunk(short[] Samples, TimeSpan Time);

/// <summary>A microphone or speaker, as listed in Windows' sound settings.</summary>
public sealed record AudioDevice(string Id, string Name);

/// <summary>
/// Records one sound device with WASAPI: what the speakers play (loopback) or a microphone.
/// Whatever the device's own format, the sound comes out as 48 kHz stereo floats.
/// </summary>
public sealed class AudioSource : IDisposable
{
    public const int Rate = 48000;

    private readonly IAudioClient _client;
    private readonly IAudioCaptureClient _capture;
    private readonly int _channels, _rate, _bits;
    private readonly bool _float;
    private readonly Thread _thread;
    private volatile bool _stop;

    // stereo frames waiting to be mixed
    private readonly object _lock = new();
    private float[] _buffer = new float[Rate * 2];
    private int _count; // frames in _buffer

    // resampling position between packets
    private double _phase;
    private float _lastL, _lastR;

    public string Name { get; }

    private AudioSource(IAudioClient client, IAudioCaptureClient capture, int channels, int rate, int bits, bool isFloat, string name)
    {
        _client = client;
        _capture = capture;
        _channels = channels;
        _rate = rate;
        _bits = bits;
        _float = isFloat;
        Name = name;
        _thread = new Thread(Run) { IsBackground = true, Name = "Overskin sound: " + name };
    }

    /// <summary>Opens the speakers (<paramref name="loopback"/>) or a microphone (by id, or the default one).</summary>
    public static AudioSource Open(bool loopback, string? deviceId = null)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        IMMDevice device;
        if (!string.IsNullOrEmpty(deviceId) && !loopback && enumerator.GetDevice(deviceId, out var chosen) == 0) device = chosen;
        else Check(enumerator.GetDefaultAudioEndpoint(loopback ? 0 : 1, 0, out device),
            loopback ? "There's no speaker or headphone output." : "There's no microphone.");

        var iid = typeof(IAudioClient).GUID;
        Check(device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out object o), "Windows wouldn't open the sound device.");
        var client = (IAudioClient)o;
        Check(client.GetMixFormat(out IntPtr format), "Windows wouldn't say what format the sound device uses.");
        try
        {
            // WAVEFORMATEX, and WAVEFORMATEXTENSIBLE's sub-format
            short tag = Marshal.ReadInt16(format, 0);
            int channels = Marshal.ReadInt16(format, 2);
            int rate = Marshal.ReadInt32(format, 4);
            int bits = Marshal.ReadInt16(format, 14);
            bool isFloat = tag == 3;
            if (tag == unchecked((short)0xFFFE))
            {
                var sub = Marshal.PtrToStructure<Guid>(format + 24);
                isFloat = sub == new Guid("00000003-0000-0010-8000-00aa00389b71");
            }
            const int Loopback = 0x00020000;
            Check(client.Initialize(0 /* shared */, loopback ? Loopback : 0, 2_000_000 /* 200 ms */, 0, format, IntPtr.Zero),
                "Windows wouldn't start recording from the sound device.");
            var capIid = typeof(IAudioCaptureClient).GUID;
            Check(client.GetService(ref capIid, out object c), "Windows wouldn't start recording from the sound device.");
            string name = loopback ? "system sound" : FriendlyName(device) ?? "microphone";
            var source = new AudioSource(client, (IAudioCaptureClient)c, channels, rate, bits, isFloat, name);
            Check(client.Start(), "Windows wouldn't start recording from the sound device.");
            source._thread.Start();
            return source;
        }
        finally
        {
            Marshal.FreeCoTaskMem(format);
        }
    }

    private static void Check(int hr, string message)
    {
        if (hr < 0) throw new InvalidOperationException(message);
    }

    /// <summary>The microphones Windows knows about.</summary>
    public static List<AudioDevice> Microphones()
    {
        var list = new List<AudioDevice>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator.EnumAudioEndpoints(1 /* capture */, 1 /* active */, out var devices) != 0) return list;
            devices.GetCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                if (devices.Item(i, out var device) != 0) continue;
                device.GetId(out string id);
                list.Add(new AudioDevice(id, FriendlyName(device) ?? "Microphone"));
            }
        }
        catch
        {
            // no sound system: no microphones
        }
        return list;
    }

    private static string? FriendlyName(IMMDevice device)
    {
        try
        {
            if (device.OpenPropertyStore(0, out var store) != 0) return null;
            var key = new PropertyKey { Format = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), Id = 14 };
            var value = new PropVariant();
            if (store.GetValue(ref key, ref value) != 0) return null;
            string? name = value.Type == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(value.Pointer) : null;
            PropVariantClear(ref value);
            return name;
        }
        catch
        {
            return null;
        }
    }

    private void Run()
    {
        var scratch = new float[4096];
        while (!_stop)
        {
            Thread.Sleep(5);
            try
            {
                while (!_stop && _capture.GetNextPacketSize(out uint packet) == 0 && packet > 0)
                {
                    if (_capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _) != 0) break;
                    bool silent = (flags & 0x2) != 0;
                    int n = (int)frames;
                    if (scratch.Length < n * 2) scratch = new float[n * 2];
                    ReadStereo(data, n, silent, scratch);
                    _capture.ReleaseBuffer(frames);
                    Push(scratch, n);
                }
            }
            catch
            {
                // the device went away (unplugged): stop quietly, the mixer fills in silence
                break;
            }
        }
    }

    /// <summary>The device's samples as stereo floats at its own rate.</summary>
    private unsafe void ReadStereo(IntPtr data, int frames, bool silent, float[] into)
    {
        if (silent || data == IntPtr.Zero)
        {
            Array.Clear(into, 0, frames * 2);
            return;
        }
        int bytes = _bits / 8;
        byte* p = (byte*)data;
        for (int i = 0; i < frames; i++)
        {
            byte* frame = p + i * _channels * bytes;
            float l = Sample(frame), r = _channels > 1 ? Sample(frame + bytes) : l;
            into[i * 2] = l;
            into[i * 2 + 1] = r;
        }
    }

    private unsafe float Sample(byte* s) =>
        _float ? (_bits == 64 ? (float)*(double*)s : *(float*)s)
        : _bits switch
        {
            16 => *(short*)s / 32768f,
            24 => ((s[0] << 8) | (s[1] << 16) | (s[2] << 24)) / 2147483648f,
            32 => *(int*)s / 2147483648f,
            _ => 0,
        };

    /// <summary>Resamples to 48 kHz (straight lines between samples) and adds to the waiting buffer.</summary>
    private void Push(float[] stereo, int frames)
    {
        lock (_lock)
        {
            if (_rate == Rate)
            {
                Ensure(frames);
                Array.Copy(stereo, 0, _buffer, _count * 2, frames * 2);
                _count += frames;
            }
            else
            {
                double step = _rate / (double)Rate;
                Ensure((int)(frames / step) + 2);
                // _phase is where the next output sample falls, counted from the sample before this packet
                while (_phase < frames)
                {
                    int i = (int)Math.Floor(_phase);
                    double f = _phase - i;
                    float l0 = i == 0 ? _lastL : stereo[(i - 1) * 2], r0 = i == 0 ? _lastR : stereo[(i - 1) * 2 + 1];
                    float l1 = stereo[i * 2], r1 = stereo[i * 2 + 1];
                    _buffer[_count * 2] = (float)(l0 + (l1 - l0) * f);
                    _buffer[_count * 2 + 1] = (float)(r0 + (r1 - r0) * f);
                    _count++;
                    _phase += step;
                }
                _phase -= frames;
            }
            if (frames > 0)
            {
                _lastL = stereo[(frames - 1) * 2];
                _lastR = stereo[(frames - 1) * 2 + 1];
            }
            // more than half a second waiting means the mixer isn't keeping up: drop the oldest
            int max = Rate / 2;
            if (_count > max)
            {
                int drop = _count - max;
                Array.Copy(_buffer, drop * 2, _buffer, 0, max * 2);
                _count = max;
            }
        }
    }

    private void Ensure(int more)
    {
        if ((_count + more) * 2 <= _buffer.Length) return;
        Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, (_count + more) * 2));
    }

    /// <summary>Adds up to <paramref name="frames"/> waiting frames into <paramref name="mix"/>. Returns how many there were.</summary>
    public int MixInto(float[] mix, int frames, float gain = 1)
    {
        lock (_lock)
        {
            int n = Math.Min(frames, _count);
            for (int i = 0; i < n * 2; i++) mix[i] += _buffer[i] * gain;
            Array.Copy(_buffer, n * 2, _buffer, 0, (_count - n) * 2);
            _count -= n;
            return n;
        }
    }

    /// <summary>Frames waiting.</summary>
    public int Waiting
    {
        get { lock (_lock) return _count; }
    }

    /// <summary>Throws away waiting frames beyond <paramref name="keep"/>, oldest first (keeps sound in step with the picture).</summary>
    public void TrimTo(int keep)
    {
        lock (_lock)
        {
            if (_count <= keep) return;
            int drop = _count - keep;
            Array.Copy(_buffer, drop * 2, _buffer, 0, keep * 2);
            _count = keep;
        }
    }

    public void Dispose()
    {
        _stop = true;
        if (_thread.IsAlive) _thread.Join(500);
        try { _client.Stop(); } catch { }
        try { Marshal.ReleaseComObject(_capture); } catch { }
        try { Marshal.ReleaseComObject(_client); } catch { }
    }

    // ---- WASAPI ----------------------------------------------------------------------------

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { public Guid Format; public int Id; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant { public ushort Type; public ushort R1, R2, R3; public IntPtr Pointer; public IntPtr Extra; }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, ref PropVariant value);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}

/// <summary>
/// Mixes the speakers and microphone into 10 ms chunks timed to the recording's clock. It runs a
/// little behind the clock so late packets still land in the right place; whatever hasn't arrived
/// by then is silence.
/// </summary>
public sealed class AudioMixer : IDisposable
{
    private const int Chunk = AudioSource.Rate / 100; // 10 ms
    private static readonly TimeSpan Behind = TimeSpan.FromMilliseconds(60);

    private readonly Stopwatch _clock;
    private readonly List<(AudioSource Source, float Gain)> _sources;
    private readonly BlockingCollection<AudioChunk> _output;
    private readonly Thread _thread;
    private volatile bool _stop;
    private long _emitted; // frames
    /// <summary>While paused nothing is written and whatever the devices send is thrown away.</summary>
    public volatile bool Paused;

    public AudioMixer(Stopwatch clock, IEnumerable<(AudioSource, float)> sources, BlockingCollection<AudioChunk> output)
    {
        _clock = clock;
        _sources = sources.ToList();
        _output = output;
        _thread = new Thread(Run) { IsBackground = true, Name = "Overskin sound mixer" };
        _thread.Start();
    }

    private void Run()
    {
        var mix = new float[Chunk * 2];
        while (true)
        {
            bool stopping = _stop;
            var until = stopping ? _clock.Elapsed : _clock.Elapsed - Behind;
            long target = (long)(until.TotalSeconds * AudioSource.Rate);
            while (_emitted + Chunk <= target)
            {
                Array.Clear(mix);
                foreach (var (source, gain) in _sources) source.MixInto(mix, Chunk, gain);
                var samples = new short[Chunk * 2];
                for (int i = 0; i < samples.Length; i++)
                    samples[i] = (short)Math.Clamp((int)Math.Round(mix[i] * 32767), short.MinValue, short.MaxValue);
                _output.TryAdd(new AudioChunk(samples, TimeSpan.FromSeconds(_emitted / (double)AudioSource.Rate)));
                _emitted += Chunk;
            }
            // a source that has piled up more than the delay is ahead of the clock: drop the excess
            foreach (var (source, _) in _sources) source.TrimTo(Paused ? 0 : Chunk * 12);
            if (stopping) break;
            Thread.Sleep(5);
        }
        _output.CompleteAdding();
    }

    /// <summary>Writes what's left up to now and ends the sound track.</summary>
    public void Finish()
    {
        _stop = true;
        _thread.Join(2000);
    }

    public void Dispose()
    {
        Finish();
        foreach (var (source, _) in _sources) source.Dispose();
    }
}
