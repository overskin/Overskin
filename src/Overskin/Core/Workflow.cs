namespace Overskin;

/// <summary>What happens to a picture after it's captured, in this order.</summary>
public sealed record AfterSteps
{
    public bool AutoHide { get; init; }
    public bool Beautify { get; init; }
    public bool Watermark { get; init; }
    public bool Save { get; init; }
    public bool Copy { get; init; }
    /// <summary>Copy the words in the picture instead of the picture.</summary>
    public bool CopyText { get; init; }
    public bool Pin { get; init; }
    public bool Editor { get; init; }
    public bool Preview { get; init; }
}

/// <summary>
/// A hotkey that runs a chain: capture something, then any mix of hide private details,
/// beautify, watermark, save, copy (the picture or its text), pin and open in the editor.
/// </summary>
public sealed class Workflow
{
    public string Name { get; set; } = "New workflow";
    public string Hotkey { get; set; } = "";
    /// <summary>"Region", "Window", "Screen" or "LastRegion".</summary>
    public string Capture { get; set; } = "Region";
    /// <summary>Seconds to wait before capturing: 0, 3, 5 or 10.</summary>
    public int Delay { get; set; }

    public bool AutoHide { get; set; }
    public bool Beautify { get; set; }
    public bool Watermark { get; set; }
    public bool Save { get; set; } = true;
    public bool Copy { get; set; } = true;
    public bool CopyText { get; set; }
    public bool Pin { get; set; }
    public bool Editor { get; set; }
    public bool Preview { get; set; }

    public static readonly string[] Captures = { "Region", "Window", "Screen", "LastRegion" };

    public static string CaptureName(string c) => c switch
    {
        "Region" => "An area", "Window" => "The window in front", "Screen" => "The whole screen", "LastRegion" => "The same area as last time", _ => c,
    };

    public AfterSteps Steps => new()
    {
        AutoHide = AutoHide, Beautify = Beautify, Watermark = Watermark, Save = Save,
        Copy = Copy && !CopyText, CopyText = CopyText, Pin = Pin, Editor = Editor, Preview = Preview,
    };

    /// <summary>A short list of what it does, e.g. "Area → hide private details → save → copy".</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>
            {
                Capture switch { "Window" => "Window", "Screen" => "Screen", "LastRegion" => "Same area", _ => "Area" },
            };
            if (Delay > 0) parts[0] += $" after {Delay} s";
            if (AutoHide) parts.Add("hide private details");
            if (Beautify) parts.Add("beautify");
            if (Watermark) parts.Add("watermark");
            if (Save) parts.Add("save");
            if (CopyText) parts.Add("copy the text");
            else if (Copy) parts.Add("copy");
            if (Pin) parts.Add("pin");
            if (Editor) parts.Add("edit");
            if (Preview) parts.Add("preview");
            return string.Join(" → ", parts);
        }
    }

    public void Normalize()
    {
        Name = string.IsNullOrWhiteSpace(Name) ? "Workflow" : Name.Trim();
        Hotkey ??= "";
        if (!Captures.Contains(Capture)) Capture = "Region";
        if (Delay is not (0 or 3 or 5 or 10)) Delay = 0;
    }

    public Workflow Clone() => (Workflow)MemberwiseClone();
}
