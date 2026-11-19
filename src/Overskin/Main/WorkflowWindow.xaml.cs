using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Overskin;

/// <summary>Makes or changes one workflow: its name, hotkey, what it captures and the steps after.</summary>
public partial class WorkflowWindow : Window
{
    private readonly Func<string, string?> _usedBy;
    private string _hotkey;
    private bool _listening;

    public Workflow Result { get; private set; }

    /// <param name="usedBy">Given a hotkey, the name of whatever already uses it (null when it's free).</param>
    public WorkflowWindow(Workflow workflow, Func<string, string?> usedBy)
    {
        InitializeComponent();
        _usedBy = usedBy;
        Result = workflow.Clone();
        _hotkey = workflow.Hotkey;
        NameBox.Text = workflow.Name;
        Pick(CaptureChoices, workflow.Capture);
        Pick(DelayChoices, workflow.Delay.ToString());
        AutoHideSwitch.IsChecked = workflow.AutoHide;
        BeautifySwitch.IsChecked = workflow.Beautify;
        WatermarkSwitch.IsChecked = workflow.Watermark;
        SaveSwitch.IsChecked = workflow.Save;
        CopySwitch.IsChecked = workflow.Copy && !workflow.CopyText;
        CopyTextSwitch.IsChecked = workflow.CopyText;
        PinSwitch.IsChecked = workflow.Pin;
        EditorSwitch.IsChecked = workflow.Editor;
        PreviewSwitch.IsChecked = workflow.Preview;
        ShowHotkey();
        // a new workflow (or one named after what it did) is named after what it does, as you change it
        _autoName = workflow.Name is "New workflow" or "Workflow" || workflow.Name == workflow.Describe();
        if (_autoName) NameBox.Text = Current().Describe();
        AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, _) => Dispatcher.BeginInvoke(AutoName)));
        NameBox.TextChanged += (_, _) => { if (!_naming) _autoName = NameBox.Text.Length == 0; };
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    private bool _autoName, _naming;

    private void AutoName()
    {
        if (!_autoName) return;
        _naming = true;
        NameBox.Text = Current().Describe();
        _naming = false;
    }

    private static void Pick(Panel panel, string tag)
    {
        foreach (var r in panel.Children.OfType<RadioButton>()) r.IsChecked = (string)r.Tag == tag;
        if (!panel.Children.OfType<RadioButton>().Any(r => r.IsChecked == true)) panel.Children.OfType<RadioButton>().First().IsChecked = true;
    }

    private static string Chosen(Panel panel) =>
        panel.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string ?? "";

    private void ShowHotkey() => HotkeyButton.Content = _listening ? "Press keys…" : _hotkey.Length == 0 ? "None" : _hotkey;

    // copying the picture and copying its text both use the clipboard: one or the other
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (sender == CopySwitch && CopySwitch.IsChecked == true) CopyTextSwitch.IsChecked = false;
        if (sender == CopyTextSwitch && CopyTextSwitch.IsChecked == true) CopySwitch.IsChecked = false;
    }

    private void HotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        _listening = !_listening;
        ShowHotkey();
    }

    private void ClearHotkey_Click(object sender, RoutedEventArgs e)
    {
        _listening = false;
        _hotkey = "";
        ShowHotkey();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!_listening)
        {
            base.OnPreviewKeyDown(e);
            return;
        }
        e.Handled = true;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.ImeProcessed or Key.DeadCharProcessed) return;
        if (key == Key.Escape)
        {
            _listening = false;
            ShowHotkey();
            return;
        }
        var mods = Keyboard.Modifiers;
        bool standalone = key is Key.PrintScreen or (>= Key.F13 and <= Key.F24) or Key.Pause;
        if (!standalone && (mods == ModifierKeys.None || mods == ModifierKeys.Shift))
        {
            Toast.Show("Include Ctrl, Alt or Win so the hotkey can't fire while you type.");
            return;
        }
        string text = new Hotkey(mods, key).ToString();
        if (_usedBy(text) is { } other)
        {
            Toast.Show($"{text} is already used by {other}.");
            return;
        }
        _hotkey = text;
        _listening = false;
        ShowHotkey();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Result = Current();
        Result.Normalize();
        DialogResult = true;
    }

    /// <summary>The workflow as the controls have it now.</summary>
    private Workflow Current()
    {
        return new Workflow
        {
            Name = NameBox.Text,
            Hotkey = _hotkey,
            Capture = Chosen(CaptureChoices),
            Delay = int.TryParse(Chosen(DelayChoices), out int d) ? d : 0,
            AutoHide = AutoHideSwitch.IsChecked == true,
            Beautify = BeautifySwitch.IsChecked == true,
            Watermark = WatermarkSwitch.IsChecked == true,
            Save = SaveSwitch.IsChecked == true,
            Copy = CopySwitch.IsChecked == true,
            CopyText = CopyTextSwitch.IsChecked == true,
            Pin = PinSwitch.IsChecked == true,
            Editor = EditorSwitch.IsChecked == true,
            Preview = PreviewSwitch.IsChecked == true,
        };
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Title_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
