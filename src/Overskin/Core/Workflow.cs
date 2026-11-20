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
    public bool Viewer { get; init; }
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

    /// <summary>
    /// What it does, as a menu would say it: "Copy text from an area", "Capture the window in front
    /// and pin it", "Capture an area after 3 s".
    /// </summary>
    public string Describe()
    {
        string from = Capture switch
        {
            "Window" => "the window in front", "Screen" => "the whole screen", "LastRegion" => "the same area again", _ => "an area",
        };
        string text = CopyText ? $"Copy text from {from}"
            : Editor ? $"Capture {from} and edit it"
            : Pin ? $"Capture {from} and pin it"
            : Copy ? $"Capture {from} and copy it"
            : Save ? $"Capture {from} and save it"
            : $"Capture {from}";
        if (Delay > 0) text += $" after {Delay} s";
        return text;
    }

    /// <summary>
    /// What menus and lists show: the name, unless it's the default or a single word (such as
    /// "text"), which says too little; then what it does.
    /// </summary>
    public string Title =>
        Name is "New workflow" or "Workflow" || !Name.Trim().Contains(' ') ? Describe() : Name;

    public void Normalize()
    {
        Name = string.IsNullOrWhiteSpace(Name) ? Describe() : Name.Trim();
        // it's shown as a menu item and a list entry, which all start with a capital
        Name = char.ToUpper(Name[0], System.Globalization.CultureInfo.CurrentCulture) + Name[1..];
        Hotkey ??= "";
        if (!Captures.Contains(Capture)) Capture = "Region";
        if (Delay is not (0 or 3 or 5 or 10)) Delay = 0;
    }

    public Workflow Clone() => (Workflow)MemberwiseClone();
}
