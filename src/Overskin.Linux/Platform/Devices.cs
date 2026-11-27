using System.Diagnostics;
using System.Text.RegularExpressions;
using Overskin.Linux.Core;

namespace Overskin.Linux.Platform;

/// <summary>
/// The cameras, microphones and screens ffmpeg can record from: AVFoundation on a Mac (where the
/// computer's own sound needs a loopback device such as BlackHole), PulseAudio/PipeWire and
/// video4linux on Linux.
/// </summary>
public static partial class Devices
{
    public sealed record Device(string Index, string Name);

    public sealed record List(IReadOnlyList<Device> Video, IReadOnlyList<Device> Audio);

    private static List? _mac;

    /// <summary>What AVFoundation offers, from "ffmpeg -f avfoundation -list_devices true".</summary>
    public static async Task<List> MacAsync()
    {
        if (_mac != null) return _mac;
        var text = await ListingAsync("-hide_banner -f avfoundation -list_devices true -i \"\"");
        return _mac = ParseMac(text);
    }

    /// <summary>Reads ffmpeg's device listing (it's printed as errors, on purpose).</summary>
    public static List ParseMac(string text)
    {
        var video = new List<Device>();
        var audio = new List<Device>();
        List<Device>? into = null;
        foreach (var line in text.Split('\n'))
        {
            if (line.Contains("AVFoundation video devices")) { into = video; continue; }
            if (line.Contains("AVFoundation audio devices")) { into = audio; continue; }
            var m = DeviceLine().Match(line);
            if (into != null && m.Success) into.Add(new Device(m.Groups[1].Value, m.Groups[2].Value.Trim()));
        }
        return new List(video, audio);
    }

    [GeneratedRegex(@"\]\s*\[(\d+)\]\s*(.+)$")]
    private static partial Regex DeviceLine();

    /// <summary>The screen to record: "Capture screen N", the Nth active display (the main one first).</summary>
    public static Device? Screen(List list, int display) =>
        list.Video.FirstOrDefault(d => d.Name.Equals($"Capture screen {display}", StringComparison.OrdinalIgnoreCase))
        ?? list.Video.FirstOrDefault(d => d.Name.StartsWith("Capture screen", StringComparison.OrdinalIgnoreCase));

    /// <summary>A camera: anything that isn't a screen.</summary>
    public static Device? Camera(List list, string? name = null) =>
        (string.IsNullOrEmpty(name) ? null : list.Video.FirstOrDefault(d => d.Name == name))
        ?? list.Video.FirstOrDefault(d => !d.Name.StartsWith("Capture screen", StringComparison.OrdinalIgnoreCase));

    /// <summary>The computer's own sound: a loopback device (BlackHole, Loopback, Soundflower).</summary>
    public static Device? Loopback(List list) =>
        list.Audio.FirstOrDefault(d => Regex.IsMatch(d.Name, "blackhole|loopback|soundflower|zoomaudiodevice", RegexOptions.IgnoreCase) && !d.Name.Contains("Zoom", StringComparison.OrdinalIgnoreCase))
        ?? list.Audio.FirstOrDefault(d => Regex.IsMatch(d.Name, "blackhole|loopback|soundflower", RegexOptions.IgnoreCase));

    /// <summary>A microphone: the first audio device that isn't a loopback one.</summary>
    public static Device? Microphone(List list, string? name = null)
    {
        if (!string.IsNullOrEmpty(name) && list.Audio.FirstOrDefault(d => d.Name == name) is { } chosen) return chosen;
        var loop = Loopback(list);
        return list.Audio.FirstOrDefault(d => d != loop && !Regex.IsMatch(d.Name, "blackhole|loopback|soundflower|aggregate|multi-output", RegexOptions.IgnoreCase));
    }

    private static async Task<string> ListingAsync(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("ffmpeg", args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true });
            if (p == null) return "";
            p.StandardInput.Close();
            var err = p.StandardError.ReadToEndAsync();
            var outText = p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return await err + await outText;
        }
        catch (Exception ex)
        {
            Log.Write("devices: " + ex.Message);
            return "";
        }
    }

    /// <summary>
    /// The ffmpeg inputs and filter for sound: the computer's own and/or the microphone, mixed.
    /// Returns the extra inputs, the filter part (empty if none) and the label of the sound, or null for none.
    /// <paramref name="firstInput"/> is the number the first extra input will have.
    /// </summary>
    public static (string Inputs, string Filter, string? Label) SoundInputs(IEnumerable<string> sources, int firstInput)
    {
        var list = sources.ToList();
        if (list.Count == 0) return ("", "", null);
        var inputs = string.Join(" ", list);
        if (list.Count == 1) return (inputs, "", $"{firstInput}:a");
        var labels = string.Concat(Enumerable.Range(firstInput, list.Count).Select(i => $"[{i}:a]"));
        return (inputs, $"{labels}amix=inputs={list.Count}:duration=longest:normalize=0[snd]", "[snd]");
    }
}
