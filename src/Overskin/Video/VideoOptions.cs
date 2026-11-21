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
    /// <summary>
    /// Records the pointer apart from the picture and draws it a moment later, smoothed with no lag
    /// and blurred while it moves (instead of drawing it as it is, frame by frame).
    /// </summary>
    public bool SmoothCursor { get; set; } = true;
    /// <summary>0 (sharp) to 100 (the shutter open the whole frame).</summary>
    public int MotionBlur { get; set; } = 50;
    /// <summary>Show keyboard shortcuts (never ordinary typing) as a label near the bottom.</summary>
    public bool ShowKeys { get; set; }

    /// <summary>A Beautify backdrop name to put the recording on, or "None".</summary>
    public string Backdrop { get; set; } = "None";

    public bool AutoZoom { get; set; } = true;
    public double Zoom { get; set; } = 1.8;
    /// <summary>0 (slow, gentle) to 100 (quick).</summary>
    public int ZoomSpeed { get; set; } = 50;
    /// <summary>0 (tight, follows closely) to 100 (loose, drifts after the pointer).</summary>
    public int FollowSmoothing { get; set; } = 50;
    /// <summary>Seconds without a click before zooming back out.</summary>
    public double ZoomHold { get; set; } = 2.5;
    /// <summary>
    /// Keep the recording unzoomed alongside the zoomed video, so the zooms can be moved, removed
    /// or added in the video editor afterwards.
    /// </summary>
    public bool EditableZooms { get; set; } = true;

    /// <summary>Record what the speakers play.</summary>
    public bool SystemSound { get; set; } = true;
    /// <summary>Record a microphone.</summary>
    public bool Microphone { get; set; }
    /// <summary>Which microphone; empty for the Windows default.</summary>
    public string MicrophoneId { get; set; } = "";

    /// <summary>Show the webcam in a bubble.</summary>
    public bool Webcam { get; set; }
    /// <summary>Which camera; empty for the first one.</summary>
    public string WebcamId { get; set; } = "";
    /// <summary>"TopLeft", "TopRight", "BottomLeft" or "BottomRight".</summary>
    public string WebcamCorner { get; set; } = "BottomRight";
    /// <summary>"Circle" or "Rounded".</summary>
    public string WebcamShape { get; set; } = "Circle";
    /// <summary>Bubble height as a percentage of the video's height.</summary>
    public int WebcamSize { get; set; } = 24;

    public void Normalize()
    {
        MicrophoneId ??= "";
        WebcamId ??= "";
        if (WebcamCorner is not ("TopLeft" or "TopRight" or "BottomLeft" or "BottomRight")) WebcamCorner = "BottomRight";
        if (WebcamShape is not ("Circle" or "Rounded")) WebcamShape = "Circle";
        WebcamSize = Math.Clamp(WebcamSize, 12, 45);
        Fps = Fps >= 60 ? 60 : 30;
        if (PointerStyle is not ("White" or "Black" or "System")) PointerStyle = "White";
        PointerSize = Math.Clamp(PointerSize, 0.75, 3);
        PointerSmoothing = Math.Clamp(PointerSmoothing, 0, 100);
        MotionBlur = Math.Clamp(MotionBlur, 0, 100);
        if (Backdrop == null || !Beautify.Backgrounds.Any(b => b.Name == Backdrop)) Backdrop = "None";
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
