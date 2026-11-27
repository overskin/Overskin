using System.Text.Json;

namespace Overskin.Linux.Core;

/// <summary>Kept in ~/.config/overskin/settings.json (or $XDG_CONFIG_HOME).</summary>
public sealed class Settings
{
    public string SaveFolder { get; set; } = DefaultSaveFolder();
    public bool SaveAutomatically { get; set; } = true;
    public bool CopyToClipboard { get; set; } = true;
    /// <summary>Hide private details in every screenshot before it's saved or copied.</summary>
    public bool AutoHide { get; set; }
    /// <summary>Screenshots have the mouse pointer in them, where it was.</summary>
    public bool IncludePointer { get; set; }
    /// <summary>Look on GitHub for a newer Overskin now and then (as on Windows).</summary>
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>Pins come back where they were when Overskin starts again (as on Windows).</summary>
    public bool RestorePins { get; set; } = true;
    public List<Overskin.Linux.Interface.PinState> Pins { get; set; } = new();
    /// <summary>A newer version you said "not now" to: it isn't offered again until there's one newer still.</summary>
    public string DismissedUpdate { get; set; } = "";
    /// <summary>Hotkeys for whole routines: capture, then hide, beautify, watermark, save, copy, pin, edit (as on Windows).</summary>
    public List<Overskin.Workflow> Workflows { get; set; } = new();
    /// <summary>"editor", "notice" or "nothing".</summary>
    public string AfterCapture { get; set; } = "editor";
    public string FileNamePattern { get; set; } = FileNames.Default;
    public bool ShowWindowOnStart { get; set; } = true;
    public string PenColour { get; set; } = "#EF4444";
    /// <summary>The font for text on pictures.</summary>
    public string TextFont { get; set; } = "Inter";
    public double PenWidth { get; set; } = 4;
    /// <summary>What auto-hide looks for.</summary>
    public Overskin.Shared.PrivateKinds AutoHideKinds { get; set; } = Overskin.Shared.PrivateKinds.Default;

    // recording
    public bool RecordPointer { get; set; } = true;
    /// <summary>Videos glide in on clicks and follow the pointer, with a large smoothed pointer.</summary>
    public bool AutoZoom { get; set; }
    public double ZoomLevel { get; set; } = 1.8;
    public int RecordFps { get; set; } = 30;
    public int GifFps { get; set; } = 15;
    /// <summary>GIFs are scaled down to at most this wide (0 = as recorded).</summary>
    public int GifMaxWidth { get; set; } = 960;
    /// <summary>Instant replay: keep recording the main screen in the background.</summary>
    public bool ReplayOn { get; set; }
    /// <summary>How much instant replay keeps: 15, 30, 60 or 120 seconds.</summary>
    public int ReplaySeconds { get; set; } = 30;
    /// <summary>Instant replay records what the computer plays too (Mac: needs BlackHole).</summary>
    public bool ReplaySound { get; set; }
    public string HotkeyReplay { get; set; } = "Ctrl+Alt+F10";
    public string HotkeyPause { get; set; } = "Ctrl+Alt+P";
    public string HotkeyDraw { get; set; } = "Ctrl+Alt+D";
    /// <summary>Marks drawn while recording fade away a few seconds after they're drawn.</summary>
    public bool PenFades { get; set; } = true;
    public double PenLinger { get; set; } = 3;

    /// <summary>Record the microphone with videos.</summary>
    public bool RecordMic { get; set; }
    /// <summary>Record what the computer plays (on a Mac this needs a loopback device such as BlackHole).</summary>
    public bool RecordSystemSound { get; set; }
    /// <summary>The webcam in a round bubble in the corner of videos.</summary>
    public bool Webcam { get; set; }

    /// <summary>3, 2, 1 before a video or GIF starts (off unless chosen).</summary>
    public bool CountdownFirst { get; set; }
    /// <summary>Stop a recording by itself after this many seconds; 0 for never.</summary>
    public int RecordStopAfter { get; set; }
    /// <summary>Darken the video a little except round the pointer.</summary>
    public bool DimAroundPointer { get; set; }

    // how recordings are finished (as on Windows)
    /// <summary>Overskin draws a sharp pointer that glides, instead of recording the system's.</summary>
    public bool SmoothPointer { get; set; } = true;
    /// <summary>"White" or "Black".</summary>
    public string PointerStyle { get; set; } = "White";
    public double PointerSize { get; set; } = 1.5;
    /// <summary>0 to 100.</summary>
    public int PointerSmoothing { get; set; } = 45;
    /// <summary>0 to 100.</summary>
    public int MotionBlur { get; set; } = 50;
    public bool ClickRings { get; set; } = true;
    /// <summary>Shortcuts pressed while recording shown as labels (never ordinary typing).</summary>
    public bool ShowKeys { get; set; }
    /// <summary>A Beautify background the recording sits on, or "None".</summary>
    public string RecordBackdrop { get; set; } = "None";
    public bool EditableZooms { get; set; } = true;
    /// <summary>0 to 100: how quickly it zooms in.</summary>
    public int ZoomSpeed { get; set; } = 50;
    /// <summary>0 to 100: how gently it follows the pointer.</summary>
    public int FollowSmoothing { get; set; } = 50;
    /// <summary>Seconds a zoom stays in after the last click.</summary>
    public double ZoomHold { get; set; } = 2.5;
    /// <summary>"TopLeft", "TopRight", "BottomLeft" or "BottomRight".</summary>
    public string WebcamCorner { get; set; } = "BottomRight";
    /// <summary>"Circle" or "Rounded".</summary>
    public string WebcamShape { get; set; } = "Circle";
    /// <summary>Per cent of the video's height, 12 to 45.</summary>
    public int WebcamSize { get; set; } = 24;
    /// <summary>The microphone and webcam to use by name; empty for the first one there is.</summary>
    public string MicrophoneName { get; set; } = "";
    public string WebcamName { get; set; } = "";
    /// <summary>The desktop's permission to record again without asking (Wayland portal).</summary>
    public string? ScreenCastToken { get; set; }

    /// <summary>A name, logo or handle stamped on screenshots.</summary>
    public WatermarkOptions Watermark { get; set; } = new();

    /// <summary>Captures older than this many days go to the trash (favourites never); 0: never.</summary>
    public int CleanUpDays { get; set; }

    /// <summary>The magnifier beside the pointer while choosing an area.</summary>
    public bool Magnifier { get; set; } = true;
    /// <summary>Lines across the whole screen through the pointer while choosing an area.</summary>
    public bool Crosshair { get; set; }
    /// <summary>A dragged box shrinks to fit what's inside it (T switches it while choosing).</summary>
    public bool Tighten { get; set; } = true;

    /// <summary>Text from screen just copies, without showing the text in a window.</summary>
    public bool TextJustCopy { get; set; }
    /// <summary>The last area captured (picture pixels), for "repeat last region".</summary>
    public int[]? LastRegion { get; set; }

    // hotkeys (macOS; Linux desktops keep global shortcuts to themselves)
    public string HotkeyArea { get; set; } = "Ctrl+Alt+S";
    public string HotkeyWindow { get; set; } = "Ctrl+Alt+W";
    public string HotkeySnip { get; set; } = "Ctrl+Alt+A";
    public string HotkeyText { get; set; } = "Ctrl+Alt+O";
    public string HotkeyColour { get; set; } = "Ctrl+Alt+K";
    public string HotkeyPinClipboard { get; set; } = "Ctrl+Alt+V";
    public string HotkeySearch { get; set; } = "Ctrl+Alt+J";
    public string HotkeyRing { get; set; } = "Ctrl+Alt+Space";
    public string HotkeyScreen { get; set; } = "Ctrl+Alt+F";
    public string HotkeyRecord { get; set; } = "Ctrl+Alt+R";
    public string HotkeyGif { get; set; } = "Ctrl+Alt+G";
    public string HotkeyRecent { get; set; } = "Ctrl+Alt+H";
    public string HotkeyScroll { get; set; } = "Ctrl+Alt+L";

    // library
    public string LibrarySort { get; set; } = "newest";
    public string Theme { get; set; } = "Graphite";
    /// <summary>The accent colour (buttons, highlights); a theme with its own accent uses that while this is the default.</summary>
    public string AccentColor { get; set; } = Overskin.Linux.Interface.Theming.DefaultAccent;
    /// <summary>Light or dark as the system is set, with the system's accent colour (as "Match Windows" does on Windows).</summary>
    public bool MatchSystem { get; set; }
    /// <summary>The main window's background is see-through and blurred.</summary>
    public bool SeeThrough { get; set; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string ConfigFolder
    {
        get
        {
            if (OperatingSystem.IsMacOS()) return Path.Combine(Home, "Library", "Application Support", "Overskin");
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var root = !string.IsNullOrEmpty(xdg) ? xdg
                : OperatingSystem.IsLinux() ? Path.Combine(Home, ".config")
                : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(root, "overskin");
        }
    }

    public static string FilePath { get; set; } = Path.Combine(ConfigFolder, "settings.json");

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { /* a broken file just means defaults */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { }
    }

    public string NewFilePath(string extension = ".png")
    {
        Directory.CreateDirectory(SaveFolder);
        return FileNames.NewPath(SaveFolder, extension, FileNamePattern);
    }

    /// <summary>The XDG Pictures folder (it's translated on non-English systems), then ~/Pictures.</summary>
    public static string DefaultSaveFolder()
    {
        var pictures = Path.Combine(Home, "Pictures");
        try
        {
            var dirs = Path.Combine(ConfigFolderRoot(), "user-dirs.dirs");
            if (File.Exists(dirs))
            {
                foreach (var line in File.ReadAllLines(dirs))
                {
                    if (!line.StartsWith("XDG_PICTURES_DIR=")) continue;
                    var value = line["XDG_PICTURES_DIR=".Length..].Trim().Trim('"').Replace("$HOME", Home);
                    if (value.Length > 0) pictures = value;
                }
            }
        }
        catch { }
        if (!OperatingSystem.IsLinux()) pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        return Path.Combine(pictures, "Overskin");
    }

    private static string ConfigFolderRoot()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return !string.IsNullOrEmpty(xdg) ? xdg : Path.Combine(Home, ".config");
    }
}
