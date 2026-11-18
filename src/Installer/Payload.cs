using System.Reflection;
using System.Text;

namespace OverskinSetup;

/// <summary>
/// Overskin.exe travels appended to the end of this exe:
/// [setup exe][Overskin.exe][payload length: 8 bytes][magic: 8 bytes].
/// build.ps1 writes it; without it the exe is just the engine, which is what Uninstall.exe is.
/// </summary>
public static class Payload
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("OVSKPAY1");
    private const int TrailerSize = 16;

    public static string SelfPath => Assembly.GetEntryAssembly()!.Location;

    /// <summary>Where the payload lives: (offset, length), or null when this exe has none.</summary>
    public static (long Offset, long Length)? Locate(string file)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length < TrailerSize) return null;
        fs.Seek(-TrailerSize, SeekOrigin.End);
        var trailer = new byte[TrailerSize];
        ReadExactly(fs, trailer);
        for (int i = 0; i < Magic.Length; i++)
            if (trailer[8 + i] != Magic[i]) return null;
        long length = BitConverter.ToInt64(trailer, 0);
        long offset = fs.Length - TrailerSize - length;
        if (length <= 0 || offset <= 0) return null;
        return (offset, length);
    }

    /// <summary>Size of Overskin.exe in this build, or 0 if there is none.</summary>
    public static long Size(string? overridePath)
    {
        if (overridePath != null) return File.Exists(overridePath) ? new FileInfo(overridePath).Length : 0;
        return Locate(SelfPath)?.Length ?? 0;
    }

    /// <summary>Writes Overskin.exe to <paramref name="target"/>, reporting 0–1 as it goes.</summary>
    public static void Extract(string target, string? overridePath, Action<double> progress)
    {
        string source;
        long offset, length;
        if (overridePath != null)
        {
            source = overridePath;
            offset = 0;
            length = new FileInfo(overridePath).Length;
        }
        else
        {
            source = SelfPath;
            var at = Locate(source) ?? throw new InvalidOperationException("This setup has no Overskin.exe inside.");
            (offset, length) = at;
        }

        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        input.Seek(offset, SeekOrigin.Begin);
        CopyTo(input, target, length, progress);
    }

    /// <summary>Writes this exe without its payload: the uninstaller.</summary>
    public static void WriteEngine(string target)
    {
        string self = SelfPath;
        long length = Locate(self)?.Offset ?? new FileInfo(self).Length;
        using var input = new FileStream(self, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        CopyTo(input, target, length, _ => { });
    }

    private static void CopyTo(Stream input, string target, long length, Action<double> progress)
    {
        string temp = target + ".partial";
        try
        {
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[1 << 20];
                long done = 0;
                while (done < length)
                {
                    int n = input.Read(buffer, 0, (int)Math.Min(buffer.Length, length - done));
                    if (n <= 0) throw new EndOfStreamException("The setup file is incomplete.");
                    output.Write(buffer, 0, n);
                    done += n;
                    progress((double)done / length);
                }
            }
            if (File.Exists(target)) File.Delete(target);
            File.Move(temp, target);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private static void ReadExactly(Stream s, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = s.Read(buffer, read, buffer.Length - read);
            if (n <= 0) throw new EndOfStreamException();
            read += n;
        }
    }
}
