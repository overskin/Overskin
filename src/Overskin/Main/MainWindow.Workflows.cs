using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Overskin;

/// <summary>Workflows: a hotkey for a whole routine (capture, hide, watermark, copy…), set up in Settings.</summary>
public partial class MainWindow
{
    /// <summary>Hotkey ids 200 and up belong to workflows.</summary>
    private const int HkWorkflowBase = 200, HkWorkflowMax = 299;

    /// <summary>Every built-in hotkey, by what it does.</summary>
    private Dictionary<string, string> BuiltInHotkeys() => new()
    {
        ["capturing a region"] = _settings.HotkeyRegion,
        ["capturing the active window"] = _settings.HotkeyWindow,
        ["capturing the full screen"] = _settings.HotkeyScreen,
        ["recording a GIF"] = _settings.HotkeyGif,
        ["text from screen"] = _settings.HotkeyText,
        ["the colour picker"] = _settings.HotkeyColour,
        ["recent captures"] = _settings.HotkeyRecent,
        ["pinning the clipboard"] = _settings.HotkeyPinClipboard,
        ["recording a video"] = _settings.HotkeyVideo,
        ["pinning a window"] = _settings.HotkeyPin,
        ["raising opacity"] = _settings.HotkeyOpacityUp,
        ["lowering opacity"] = _settings.HotkeyOpacityDown,
        ["click-through"] = _settings.HotkeyClickThrough,
        ["snip and pin"] = _settings.HotkeySnip,
        ["drawing on a recording"] = _settings.HotkeyDraw,
        ["pausing a recording"] = _settings.HotkeyPause,
        ["the ring menu"] = _settings.HotkeyRing,
        ["search everything"] = _settings.HotkeySearch,
        ["saving the instant replay"] = _settings.HotkeyReplay,
    };

    /// <summary>What already uses a hotkey, leaving out <paramref name="except"/>; null when it's free.</summary>
    private string? HotkeyUsedBy(string text, Workflow? except = null)
    {
        foreach (var (what, key) in BuiltInHotkeys())
            if (key == text) return what;
        foreach (var w in _settings.Workflows)
            if (!ReferenceEquals(w, except) && w.Hotkey == text) return $"the workflow \"{w.Title}\"";
        return null;
    }

    /// <summary>Binds every workflow's hotkey. Returns those that another program already owns.</summary>
    private List<string> RegisterWorkflowHotkeys()
    {
        var failed = new List<string>();
        if (_hotkeys == null) return failed;
        for (int id = HkWorkflowBase; id <= HkWorkflowMax; id++) _hotkeys.Unregister(id);
        for (int i = 0; i < _settings.Workflows.Count && HkWorkflowBase + i <= HkWorkflowMax; i++)
        {
            var w = _settings.Workflows[i];
            if (w.Hotkey.Length == 0) continue;
            if (!Hotkey.TryParse(w.Hotkey, out var hotkey) || !_hotkeys.Register(HkWorkflowBase + i, hotkey, () => RunWorkflow(w)))
                failed.Add(w.Hotkey);
        }
        return failed;
    }

    private void RefreshWorkflowRows()
    {
        WorkflowRows.Children.Clear();
        WorkflowsEmpty.Visibility = _settings.Workflows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var w in _settings.Workflows)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = w.Title, Style = (Style)FindResource("Label"), FontWeight = FontWeights.SemiBold });
            text.Children.Add(new TextBlock { Text = w.Summary, Style = (Style)FindResource("Dim"), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
            row.Children.Add(text);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(buttons, 1);
            var key = new Border { Style = (Style)FindResource("Key"), Margin = new Thickness(8, 0, 6, 0) };
            key.Child = new TextBlock { Text = w.Hotkey.Length == 0 ? "No hotkey" : w.Hotkey, Style = (Style)FindResource("KeyText") };
            buttons.Children.Add(key);
            var run = IconButton("IcPlay", "Run it now");
            run.Click += (_, _) => RunWorkflow(w);
            var edit = IconButton("IcPencil", "Change");
            edit.Click += (_, _) => EditWorkflow(w);
            var delete = IconButton("IcTrash", "Delete");
            delete.Click += (_, _) =>
            {
                _settings.Workflows.Remove(w);
                WorkflowsChanged();
            };
            buttons.Children.Add(run);
            buttons.Children.Add(edit);
            buttons.Children.Add(delete);
            row.Children.Add(buttons);
            WorkflowRows.Children.Add(row);
        }
    }

    private Button IconButton(string icon, string tip) => new()
    {
        Style = (Style)FindResource("BtnIcon"),
        Width = 28,
        Height = 26,
        ToolTip = tip,
        Content = new Icon { Data = (Geometry)FindResource(icon), Width = 13, Height = 13 },
    };

    private void AddWorkflow_Click(object sender, RoutedEventArgs e)
    {
        var w = new Workflow { Name = $"Workflow {_settings.Workflows.Count + 1}", AutoHide = true };
        if (ShowWorkflowDialog(w, null) is { } made)
        {
            _settings.Workflows.Add(made);
            WorkflowsChanged();
        }
    }

    private void EditWorkflow(Workflow w)
    {
        if (ShowWorkflowDialog(w, w) is not { } changed) return;
        int i = _settings.Workflows.IndexOf(w);
        if (i >= 0) _settings.Workflows[i] = changed;
        WorkflowsChanged();
    }

    private Workflow? ShowWorkflowDialog(Workflow start, Workflow? editing)
    {
        // while the dialog listens for keys, our own hotkeys mustn't swallow them
        _hotkeys?.UnregisterAll();
        try
        {
            var dialog = new WorkflowWindow(start, text => HotkeyUsedBy(text, editing)) { Owner = this };
            return dialog.ShowDialog() == true ? dialog.Result : null;
        }
        finally
        {
            RegisterHotkeys(announceFailures: false);
        }
    }

    private void RebuildTrayMenu() => _tray?.Rebuild();

    private void WorkflowsChanged()
    {
        SaveSettings();
        RefreshWorkflowRows();
        var failed = RegisterWorkflowHotkeys();
        if (failed.Count > 0) Toast.Show($"{string.Join(", ", failed)} is already used by another program. Pick another hotkey.", true);
        RebuildTrayMenu();
    }
}
