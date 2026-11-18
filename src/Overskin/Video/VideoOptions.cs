namespace Overskin;

/// <summary>Everything the video recorder can be told, as saved in the settings.</summary>
public sealed class VideoOptions
{
    public int Fps { get; set; } = 30;
    public bool HighQuality { get; set; } = true;

    public bool Pointer { get; set; } = true;
    /// <summary>"White" or "Black": sharp vector pointers; "System": Windows' own pointer.</summary>
    public string PointerStyle { get; set; } = "White";
    /// <summary>Times the normal pointer size.</summary>
    public double PointerSize { get; set; } = 1.5;
    /// <summary>0 (none) to 100 (very smooth).</summary>
    public int PointerSmoothing { get; set; } = 45;
    public bool ClickRings { get; set; } = true;

    public bool AutoZoom { get; set; } = true;
    public double Zoom { get; set; } = 1.8;
    /// <summary>0 (slow, gentle) to 100 (quick).</summary>
    public int ZoomSpeed { get; set; } = 50;
    /// <summary>0 (tight, follows closely) to 100 (loose, drifts after the pointer).</summary>
    public int FollowSmoothing { get; set; } = 50;
    /// <summary>Seconds without a click before zooming back out.</summary>
    public double ZoomHold { get; set; } = 2.5;

    public void Normalize()
    {
        Fps = Fps >= 60 ? 60 : 30;
        if (PointerStyle is not ("White" or "Black" or "System")) PointerStyle = "White";
        PointerSize = Math.Clamp(PointerSize, 0.75, 3);
        PointerSmoothing = Math.Clamp(PointerSmoothing, 0, 100);
        Zoom = Math.Clamp(Zoom, 1.2, 3);
        ZoomSpeed = Math.Clamp(ZoomSpeed, 0, 100);
        FollowSmoothing = Math.Clamp(FollowSmoothing, 0, 100);
        ZoomHold = Math.Clamp(ZoomHold, 0.5, 8);
    }

    public VideoOptions With(bool autoZoom)
    {
        var copy = (VideoOptions)MemberwiseClone();
        copy.AutoZoom = autoZoom;
        return copy;
    }

    // ---- slider positions -> camera rates (per second) ----
    public double PointerRate => PointerSmoothing == 0 ? 0 : 40 * Math.Pow(0.08, PointerSmoothing / 100.0);
    public double ZoomRate => 1.6 * Math.Pow(7, ZoomSpeed / 100.0);
    public double PanRate => 12 * Math.Pow(0.12, FollowSmoothing / 100.0);
}
