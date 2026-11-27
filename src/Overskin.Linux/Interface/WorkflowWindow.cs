using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Overskin.Linux.Platform;
using Workflow = Overskin.Workflow;

namespace Overskin.Linux.Interface;

/// <summary>Makes or changes one workflow (as on Windows): its name, hotkey, what it captures and the steps after.</summary>
public sealed class WorkflowWindow : Window
{
    private readonly Workflow? _editing;
    private readonly TextBox _name = new() { MinWidth = 320 };
    private readonly TextBox _hotkey = new() { MinWidth = 180, Watermark = OperatingSystem.IsMacOS() ? "e.g. Ctrl+Alt+1" : "" };
    private readonly ComboBox _capture = new() { ItemsSource = Workflow.Captures.Select(Workflow.CaptureName).ToList(), MinWidth = 220 };
    private readonly ComboBox _delay = new() { ItemsSource = new[] { "No wait", "3 seconds", "5 seconds", "10 seconds" }, MinWidth = 140 };
    private readonly CheckBox _autoHide = new() { Content = "Hide private details (emails, numbers, cards…)" };
    private readonly CheckBox _beautify = new() { Content = "Beautify (backdrop, padding, rounded corners, shadow)" };
    private readonly CheckBox _watermark = new() { Content = "Stamp the watermark (set on the Capture page)" };
    private readonly CheckBox _save = new() { Content = "Save it to the screenshots folder" };
    private readonly CheckBox _copy = new() { Content = "Copy the picture" };
    private readonly CheckBox _copyText = new() { Content = "Copy the text in it instead" };
    private readonly CheckBox _pin = new() { Content = "Pin it on top of everything, where it was" };
    private readonly CheckBox _editor = new() { Content = "Open it in the editor" };
    private readonly CheckBox _preview = new() { Content = "Show a preview card" };
    private readonly TextBlock _status = Ui.Label("", "dim");
    private bool _autoName, _naming;
    private static readonly int[] Delays = { 0, 3, 5, 10 };

    public Workflow? Result { get; private set; }

    public WorkflowWindow(Workflow workflow, Workflow? editing)
    {
        _editing = editing;
        Title = editing == null ? "New workflow" : "Change workflow";
        Icon = App.Icon;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _name.Text = workflow.Name;
        _hotkey.Text = workflow.Hotkey;
        _capture.SelectedIndex = Math.Max(0, Array.IndexOf(Workflow.Captures, workflow.Capture));
        _delay.SelectedIndex = Math.Max(0, Array.IndexOf(Delays, workflow.Delay));
        _autoHide.IsChecked = workflow.AutoHide;
        _beautify.IsChecked = workflow.Beautify;
        _watermark.IsChecked = workflow.Watermark;
        _save.IsChecked = workflow.Save;
        _copy.IsChecked = workflow.Copy && !workflow.CopyText;
        _copyText.IsChecked = workflow.CopyText;
        _pin.IsChecked = workflow.Pin;
        _editor.IsChecked = workflow.Editor;
        _preview.IsChecked = workflow.Preview;
        // copying the picture and copying its text both use the clipboard: one or the other
        _copy.IsCheckedChanged += (_, _) => { if (_copy.IsChecked == true) _copyText.IsChecked = false; AutoName(); };
        _copyText.IsCheckedChanged += (_, _) => { if (_copyText.IsChecked == true) _copy.IsChecked = false; AutoName(); };
        foreach (var c in new[] { _autoHide, _beautify, _watermark, _save, _pin, _editor, _preview }) c.IsCheckedChanged += (_, _) => AutoName();
        _capture.SelectionChanged += (_, _) => AutoName();
        _delay.SelectionChanged += (_, _) => AutoName();
        // a new workflow (or one named after what it did) is named after what it does, as it's changed
        _autoName = workflow.Name is "New workflow" or "Workflow" || workflow.Name.StartsWith("Workflow ") || workflow.Name == workflow.Describe();
        if (_autoName) _name.Text = Current().Describe();
        _name.TextChanged += (_, _) => { if (!_naming) _autoName = string.IsNullOrEmpty(_name.Text); };

        var hotkeyHelp = OperatingSystem.IsMacOS()
            ? "Typed as Ctrl+Alt+1 (Alt is ⌥ Option, Cmd is ⌘). Leave it empty for none."
            : "On Linux, give it a key in your desktop's keyboard settings: the command is  overskin --workflow \"its name\"";
        var save = Ui.Button(editing == null ? "Add" : "Save", SaveIt, cls: "primary");
        var cancel = Ui.Button("Cancel", Close);

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 10,
            Children =
            {
                Ui.Label("Name", "dim"), _name,
                Ui.Label("Hotkey", "dim"), _hotkey, Ui.Label(hotkeyHelp, "dim"),
                Ui.Label("Capture", "h2"),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _capture, _delay } },
                Ui.Label("Then", "h2"),
                _autoHide, _beautify, _watermark, _save, _copy, _copyText, _pin, _editor, _preview,
                _status,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, save } },
            },
        };
        foreach (var t in ((StackPanel)Content).Children.OfType<TextBlock>()) t.TextWrapping = TextWrapping.Wrap;
        Opened += (_, _) => { _name.Focus(); _name.SelectAll(); };
    }

    private void AutoName()
    {
        if (!_autoName) return;
        _naming = true;
        _name.Text = Current().Describe();
        _naming = false;
    }

    /// <summary>The workflow as the controls have it now.</summary>
    private Workflow Current() => new()
    {
        Name = _name.Text ?? "",
        Hotkey = (_hotkey.Text ?? "").Trim(),
        Capture = Workflow.Captures[Math.Max(0, _capture.SelectedIndex)],
        Delay = Delays[Math.Max(0, _delay.SelectedIndex)],
        AutoHide = _autoHide.IsChecked == true,
        Beautify = _beautify.IsChecked == true,
        Watermark = _watermark.IsChecked == true,
        Save = _save.IsChecked == true,
        Copy = _copy.IsChecked == true,
        CopyText = _copyText.IsChecked == true,
        Pin = _pin.IsChecked == true,
        Editor = _editor.IsChecked == true,
        Preview = _preview.IsChecked == true,
    };

    private void SaveIt()
    {
        var w = Current();
        if (w.Hotkey.Length > 0)
        {
            if (OperatingSystem.IsMacOS() && !Mac.TryParseHotKey(w.Hotkey, out _, out _))
            {
                _status.Text = $"\"{w.Hotkey}\" isn't a hotkey Overskin understands. Try Ctrl+Alt+1, Cmd+Shift+2 or Ctrl+Alt+F5.";
                return;
            }
            if (App.Current.HotkeyUsedBy(w.Hotkey, _editing) is { } other)
            {
                _status.Text = $"{w.Hotkey} is already used by {other}.";
                return;
            }
        }
        w.Normalize();
        Result = w;
        Close();
    }

    /// <summary>For the self-test: the workflow the window would save.</summary>
    internal Workflow CurrentForTest() => Current();
    internal void SetForTest(string capture, bool copyText) { _capture.SelectedIndex = Array.IndexOf(Workflow.Captures, capture); _copyText.IsChecked = copyText; }
}
