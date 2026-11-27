using System.IO;
using System.Text;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace Overskin;

/// <summary>Subtitles: the Windows parts (reading a video's sound with Windows, and finding Whisper's engine).</summary>
public static partial class Subtitles
{
    /// <summary>The video's sound as 16 kHz mono samples, decoded by Windows.</summary>
    internal static async partial Task<float[]> ReadSoundAsync(string videoPath, CancellationToken cancel)
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

    /// <summary>
    /// Tells Whisper where its engine (whisper.dll and the ggml files beside it) is. Next to
    /// Overskin.exe in the Store copy; in the one-file GitHub copy, .NET unpacks it into a folder
    /// of its own, the same one as WPF's own native files, so it's found from where one of those
    /// was loaded.
    /// </summary>
    static partial void FindEngine()
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
}
