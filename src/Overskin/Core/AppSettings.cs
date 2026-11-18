using System.IO;
using System.Text.Json;

namespace Overskin;

/// <summary>A saved capture, for the library.</summary>
public sealed class HistoryEntry
{
    public string Path { get; set; } = "";
    public DateTime Time { get; set; }
}

/// <summary>A pinned image to bring back on the next start.</summary>
public sealed class PinnedImageState
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Opacity { get; set; } = 100;
    public double Rotation { get; set; }
    public bool FlipHorizontal { get; set; }
    public bool FlipVertical { get; set; }
    public bool Grayscale { get; set; }
    public bool Locked { get; set; }
    public bool ClickThrough { get; set; }
    public int Grid { get; set; }
}

/// <summary>Everything that persists between runs. Saved as JSON next to the exe.</summary>
public sealed class AppSettings
{
    public const string FileName = "overskin.settings.json";

    // hotkeys
    public string HotkeyPin { get; set; } = "Ctrl+Alt+T";
    public string HotkeyOpacityUp { get; set; } = "Ctrl+Alt+Up";
    public string HotkeyOpacityDown { get; set; } = "Ctrl+Alt+Down";
    public string HotkeyClickThrough { get; set; } = "Ctrl+Alt+C";
    public string HotkeyAbort { get; set; } = "Ctrl+Alt+X";
    public string HotkeyDraw { get; set; } = "Ctrl+Alt+D";
    public string HotkeyPause { get; set; } = "Ctrl+Alt+P";
    public string HotkeySnip { get; set; } = "Ctrl+Alt+A";
    public string HotkeyRegion { get; set; } = "Ctrl+Alt+S";
    public string HotkeyWindow { get; set; } = "Ctrl+Alt+W";
    public string HotkeyScreen { get; set; } = "Ctrl+Alt+F";
    public string HotkeyText { get; set; } = "Ctrl+Alt+O";
    public string HotkeyColour { get; set; } = "Ctrl+Alt+K";
    public string HotkeyGif { get; set; } = "Ctrl+Alt+G";
    public string HotkeyRecent { get; set; } = "Ctrl+Alt+H";
    /// <summary>Library order: Newest, Oldest, Name or Largest.</summary>
    public string LibrarySort { get; set; } = "Newest";

    // capture
    public bool AfterCopy { get; set; } = true;
    public bool AfterSave { get; set; } = true;
    public bool AfterEditor { get; set; }
    public bool AfterPin { get; set; }
    public bool AfterPreview { get; set; } = true;
    /// <summary>Draw the mouse pointer into screenshots and recordings.</summary>
    public bool AfterCursor { get; set; }
    /// <summary>Find emails, phone numbers, card numbers, IPs and keys in every screenshot and hide them.</summary>
    public bool AutoHide { get; set; }
    /// <summary>What auto-hide looks for.</summary>
    public Overskin.Shared.PrivateKinds AutoHideKinds { get; set; } = Overskin.Shared.PrivateKinds.Default;
    /// <summary>Window shots: just the window, on a transparent background with a shadow.</summary>
    public bool WindowAlpha { get; set; }
    public string SaveFolder { get; set; } = DefaultSaveFolder();
    /// <summary>"png" or "jpg".</summary>
    public string ImageFormat { get; set; } = "png";
    public int CaptureDelay { get; set; }
    public bool FullScreenAllMonitors { get; set; }
    public int[]? LastRegion { get; set; }
    public List<HistoryEntry> History { get; set; } = new();

    public static string DefaultSaveFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Overskin");

    // general
    public string AccentColor { get; set; } = Theming.DefaultAccent;
    public VideoOptions Video { get; set; } = new();
    /// <summary>The colour theme (named for the older "background tone" setting it replaced).</summary>
    public string BackgroundTone { get; set; } = Theming.DefaultTone;
    /// <summary>Themes to return to when flipping between dark and light.</summary>
    public string LastDarkTheme { get; set; } = Theming.DefaultTone;
    public string LastLightTheme { get; set; } = Theming.DefaultLightTheme;
    public bool AlwaysOnTop { get; set; }
    /// <summary>Holding Ctrl+Alt makes click-through images respond to the mouse for as long as it is held.</summary>
    public bool GrabWithCtrlAlt { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool TrayHintShown { get; set; }
    public int LastTab { get; set; }
    /// <summary>Interface size in percent.</summary>
    public int UiScale { get; set; } = 100;
    /// <summary>Window size at 100% scale, after the user resized it.</summary>
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }

    // pinned images
    public int ImageOpacity { get; set; } = 100;
    /// <summary>Pin the same images again on the next start.</summary>
    public bool RestoreImages { get; set; } = true;
    public List<PinnedImageState> PinnedImages { get; set; } = new();

    // auto-trace
    public string TraceMode { get; set; } = "Outline";
    public int Threshold { get; set; } = 128;
    public bool Invert { get; set; }
    public int Blur { get; set; } = 1;
    /// <summary>Working resolution in percent. With an area chosen, 100 = one traced pixel per screen pixel.</summary>
    public int ScalePercent { get; set; } = 100;
    public int MinPathLength { get; set; } = 8;
    public double Tolerance { get; set; } = 1.0;
    public int FillSpacing { get; set; } = 4;
    public int HatchAngle { get; set; }
    public bool Crosshatch { get; set; }
    public int ShadeLevels { get; set; } = 3;
    public bool OutlineToo { get; set; } = true;
    public List<PaletteSwatch> Palette { get; set; } = new();
    /// <summary>Outline big areas and fill them with the game's bucket instead of hatching them.</summary>
    public bool BucketFill { get; set; }
    /// <summary>Where the game's brush and bucket buttons are, for switching between them.</summary>
    public PaletteSwatch? BrushTool { get; set; }
    public PaletteSwatch? FillTool { get; set; }

    // drawing
    public int DelayMs { get; set; } = 1;
    public int PointsPerStep { get; set; } = 1;
    public int CountdownSeconds { get; set; } = 3;
    /// <summary>"Optimized", "Careful", "Fast" or "Instant".</summary>
    public string TraceSpeed { get; set; } = "Optimized";
    public bool StopOnMouseMove { get; set; } = true;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Where settings live: %AppData%\Overskin, the usual place for a per-user app, so they survive
    /// reinstalls and updates. Developer runs (self-test, snapshots) keep theirs next to the exe so
    /// they never touch real settings.
    /// </summary>
    public static string FilePath { get; private set; } = DefaultPath();

    private static string DefaultPath() => App.Quiet ? ExeFolderPath() : FallbackPath();

    /// <summary>Next to the exe: where settings were kept before 2.4, and where developer runs keep them.</summary>
    private static string ExeFolderPath()
    {
        string? dir = Path.GetDirectoryName(Environment.ProcessPath);
        return Path.Combine(string.IsNullOrEmpty(dir) ? AppContext.BaseDirectory : dir, FileName);
    }

    private static string FallbackPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Overskin", FileName);

    /// <summary>Where settings lived before the app was renamed from Overlay.</summary>
    private static IEnumerable<string> LegacyPaths()
    {
        string? dir = Path.GetDirectoryName(Environment.ProcessPath);
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(dir)) yield return Path.Combine(dir, "onionskin.settings.json");
        yield return Path.Combine(appData, "Onionskin", "onionskin.settings.json");
        if (!string.IsNullOrEmpty(dir)) yield return Path.Combine(dir, "overlay.settings.json");
        yield return Path.Combine(appData, "Overlay", "overlay.settings.json");
    }

    public static AppSettings Load()
    {
        FilePath = DefaultPath();
        // the real place first; then settings left next to the exe by older versions, read once and moved
        var candidates = new[] { DefaultPath(), ExeFolderPath() }.Distinct().Concat(LegacyPaths()).ToList();
        for (int i = 0; i < candidates.Count; i++)
        {
            string path = candidates[i];
            try
            {
                if (!File.Exists(path)) continue;
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
                if (loaded == null) continue;
                // old files are read once and from then on saved under the new name
                FilePath = DefaultPath();
                loaded.Sanitize();
                return loaded;
            }
            catch
            {
                // unreadable or hand-edited into something invalid: start from defaults
            }
        }
        return new AppSettings();
    }

    public void Save()
    {
        string json = JsonSerializer.Serialize(this, JsonOptions);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            try
            {
                string fallback = FallbackPath();
                Directory.CreateDirectory(Path.GetDirectoryName(fallback)!);
                File.WriteAllText(fallback, json);
                FilePath = fallback;
            }
            catch
            {
                // nowhere writable; settings just won't persist this run
            }
        }
    }

    private void Sanitize()
    {
        ImageOpacity = Math.Clamp(ImageOpacity, 10, 100);
        Threshold = Math.Clamp(Threshold, 0, 255);
        ScalePercent = Math.Clamp(ScalePercent, 10, 200);
        MinPathLength = Math.Clamp(MinPathLength, 0, 100);
        Tolerance = Math.Clamp(Tolerance, 0, 5);
        FillSpacing = Math.Clamp(FillSpacing, 1, 40);
        Blur = Math.Clamp(Blur, 0, 4);
        ShadeLevels = Math.Clamp(ShadeLevels, 2, 5);
        if (CountdownSeconds is not (0 or 1 or 3 or 5)) CountdownSeconds = 3;
        if (TraceSpeed is not ("Optimized" or "Careful" or "Fast" or "Instant")) TraceSpeed = "Optimized";
        if (HatchAngle is not (0 or 45 or 90 or 135)) HatchAngle = 0;
        Palette ??= new();
        PinnedImages ??= new();
        History ??= new();
        History.RemoveAll(h => h == null || string.IsNullOrEmpty(h.Path));
        if (string.IsNullOrWhiteSpace(SaveFolder)) SaveFolder = DefaultSaveFolder();
        if (ImageFormat is not ("png" or "jpg")) ImageFormat = "png";
        if (CaptureDelay is not (0 or 3 or 5 or 10)) CaptureDelay = 0;
        if (LastRegion is { Length: not 4 }) LastRegion = null;
        // Ctrl+Alt+S used to be "snip and pin"; it now captures a region
        if (HotkeySnip == HotkeyRegion) HotkeySnip = "Ctrl+Alt+A";
        PinnedImages.RemoveAll(s => s == null || string.IsNullOrEmpty(s.Path));
        if (UiScale is not (90 or 100 or 115 or 130 or 150)) UiScale = 100;
        Palette.RemoveAll(s => s == null || !Theming.TryParseHex(s.Hex, out _));
        DelayMs = Math.Clamp(DelayMs, 0, 50);
        PointsPerStep = Math.Clamp(PointsPerStep, 1, 20);
        LastTab = Math.Clamp(LastTab, 0, 4);
        if (TraceMode == "Fill") TraceMode = "Hatch"; // name used before 1.3
        if (!Enum.TryParse<Overskin.TraceMode>(TraceMode, out _)) TraceMode = "Outline";
        if (!Theming.TryParseHex(AccentColor, out _)) AccentColor = Theming.DefaultAccent;
        if (Theming.Themes.All(t => t.Name != BackgroundTone)) BackgroundTone = Theming.DefaultTone;
        if (Theming.Themes.All(t => t.Name != LastDarkTheme || t.Light)) LastDarkTheme = Theming.DefaultTone;
        if (Theming.Themes.All(t => t.Name != LastLightTheme || !t.Light)) LastLightTheme = Theming.DefaultLightTheme;
        Video ??= new VideoOptions();
        Video.Normalize();
    }
}
