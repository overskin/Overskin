using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;

namespace Overskin;

/// <summary>
/// Every feature as a named command, so the tray menu, the taskbar jump list and
/// "Overskin.exe --run &lt;id&gt;" all reach the same things.
/// </summary>
public partial class MainWindow
{
    private sealed record Command(string Id, string Header, string Icon, Action Run, bool InJumpList = false);

    private static readonly RoutedEventArgs NoArgs = new();

    private (string Group, string Icon, Command[] Items)[] Commands() => new[]
    {
        ("Capture", "IcCamera", new Command[]
        {
            new("region", "Region", "IcSquareDashed", () => CaptureRegion(), true),
            new("window", "Window", "IcAppWindow", CaptureWindowPick, true),
            new("screen", "Full screen", "IcMonitor", () => CaptureFullScreen(), true),
            new("repeat", "Repeat last region", "IcRepeat", CaptureLastRegion),
            new("scrolling", "Scrolling page", "IcArrowUpDown", CaptureScrolling, true),
            new("gif", "Record a GIF", "IcFilm", ToggleGifRecording, true),
            new("video", "Record a video", "IcCircleDot", () => ToggleVideo(false), true),
            new("zoomvideo", "Record a video with auto-zoom", "IcZoomIn", () => ToggleVideo(true), true),
            new("steps", "Record steps (how-to guide)", "IcListOrdered", ToggleSteps, true),
        }),
        ("Tools", "IcZap", new Command[]
        {
            new("snip", "Snip and pin", "IcScissors", SnipAndPin, true),
            new("text", "Text from screen", "IcScanText", TextFromScreen, true),
            new("colour", "Colour picker", "IcPipette", PickScreenColour, true),
            new("ruler", "Ruler", "IcRuler", ShowRuler),
        }),
        ("Pin", "IcPin", new Command[]
        {
            new("pinwindow", "Pin a window", "IcPin", PinWindowFromMenu),
            new("pinclipboard", "Pin the clipboard picture", "IcClipboardPaste", PasteImage),
            new("pinfile", "Pin a picture file…", "IcImagePlus", () => BrowseImage_Click(this, NoArgs)),
            new("clickable", "Make pinned pictures clickable", "IcMousePointerClick", MakeImagesClickable),
            new("restore", "Restore all windows", "IcUndo2", () => RestoreAll_Click(this, NoArgs)),
        }),
        ("Edit", "IcPencil", new Command[]
        {
            new("editclipboard", "Edit the clipboard picture", "IcClipboardPaste", () => EditClipboard_Click(this, NoArgs)),
            new("editfile", "Open a picture in the editor…", "IcFolderOpen", () => OpenInEditor_Click(this, NoArgs)),
            new("combinerow", "Combine pictures side by side…", "IcArrowLeftRight", () => CombineFiles(CombineLayout.SideBySide)),
            new("combinestack", "Combine pictures stacked…", "IcArrowUpDown", () => CombineFiles(CombineLayout.Stacked)),
            new("combinegrid", "Make a collage…", "IcGrid2x2", () => CombineFiles(CombineLayout.Grid)),
            new("caption", "Caption a GIF…", "IcType", () => CaptionGif_Click(this, NoArgs)),
        }),
        ("Open", "IcLayers", new Command[]
        {
            new("recent", "Recent captures", "IcImages", ShowRecent, true),
            new("library", "Library", "IcImages", () => ShowTab(TabLibraryIndex)),
            new("trace", "Trace", "IcPenTool", () => ShowTab(TabTraceIndex)),
            new("settings", "Settings", "IcSettings", () => ShowTab(TabSettingsIndex)),
            new("folder", "Screenshots folder", "IcFolder", () => OpenFolder_Click(this, NoArgs)),
        }),
    };

    /// <summary>Runs a command by id; false when there is no such command.</summary>
    public bool RunCommand(string id)
    {
        var command = Commands().SelectMany(g => g.Items).FirstOrDefault(c => c.Id == id);
        if (command == null) return false;
        command.Run();
        return true;
    }

    private void ShowTab(int index)
    {
        ShowFromTray();
        SelectTab(index);
    }

    private void PinWindowFromMenu()
    {
        ShowTab(TabPinsIndex);
        Pick_Click(this, NoArgs);
    }

    private Icon MenuIcon(string key) => new() { Data = (Geometry)FindResource(key), Width = 15, Height = 15 };

    private void FillTrayMenu(ItemCollection items)
    {
        foreach (var (group, icon, commands) in Commands())
        {
            var header = new MenuItem { Header = group, Icon = MenuIcon(icon) };
            foreach (var c in commands)
            {
                var item = new MenuItem { Header = c.Header, Icon = MenuIcon(c.Icon) };
                item.Click += (_, _) => c.Run();
                header.Items.Add(item);
            }
            items.Add(header);
        }
    }

    /// <summary>Right-clicking Overskin on the taskbar or in Start lists the main commands.</summary>
    private void SetUpJumpList()
    {
        if (App.Quiet || Environment.ProcessPath is not { } exe) return;
        try
        {
            var list = new JumpList { ShowFrequentCategory = false, ShowRecentCategory = false };
            foreach (var c in Commands().SelectMany(g => g.Items).Where(c => c.InJumpList))
            {
                list.JumpItems.Add(new JumpTask
                {
                    Title = c.Header == "Region" ? "Capture a region" : c.Header == "Window" ? "Capture a window"
                          : c.Header == "Full screen" ? "Capture the full screen" : c.Header,
                    Arguments = $"{App.RunArgument} {c.Id}",
                    ApplicationPath = exe,
                    IconResourcePath = exe,
                    IconResourceIndex = 0,
                });
            }
            JumpList.SetJumpList(Application.Current, list);
            list.Apply();
        }
        catch
        {
            // the jump list is a convenience; never let it stop start-up
        }
    }
}
