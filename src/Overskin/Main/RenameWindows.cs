using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Overskin;

/// <summary>
/// Rename one capture, or many at once with a pattern: {name} the current name, {n} a running
/// number, {date} and {time} when it was taken. The extension always stays.
/// </summary>
public sealed class RenameWindow : Window
{
    private readonly IReadOnlyList<(string Path, DateTime Time)> _files;
    private readonly TextBox _pattern = new();
    private readonly TextBox _start = new() { Text = "1", Width = 60 };
    private readonly TextBlock _preview = new() { TextWrapping = TextWrapping.NoWrap };
    private readonly TextBlock _problem = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _apply;

    /// <summary>Old path → new file name, for every file, once the window closes with Rename.</summary>
    public List<(string Path, string NewName)> Result { get; } = new();

    public RenameWindow(IReadOnlyList<(string Path, DateTime Time)> files)
    {
        _files = files;
        bool bulk = files.Count > 1;
        Title = bulk ? $"Rename {files.Count} captures" : "Rename";
        Width = bulk ? 520 : 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Panel");
        SetResourceReference(ForegroundProperty, "Text");
        FontFamily = (FontFamily)FindResource("UiFont");

        _pattern.Style = (Style)FindResource("TextField");
        _start.Style = (Style)FindResource("TextField");
        _pattern.Text = bulk ? "{name}" : Path.GetFileNameWithoutExtension(files[0].Path);
        _pattern.TextChanged += (_, _) => Refresh();
        _start.TextChanged += (_, _) => Refresh();
        _preview.Style = (Style)FindResource("Dim");
        _problem.SetResourceReference(TextBlock.ForegroundProperty, "Warn");

        _apply = new Button { Style = (Style)FindResource("BtnAccent"), Content = "Rename", MinWidth = 90, IsDefault = true };
        _apply.Click += (_, _) =>
        {
            if (!Build(out var names, out _)) return;
            for (int i = 0; i < files.Count; i++) Result.Add((files[i].Path, names[i]));
            DialogResult = true;
        };
        var cancel = new Button { Style = (Style)FindResource("Btn"), Content = "Cancel", MinWidth = 80, IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = bulk ? "New names" : "New name", Style = (Style)FindResource("Label"), Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(_pattern);
        if (bulk)
        {
            var tokens = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var (token, tip) in new[] { ("{name}", "the current name"), ("{n}", "1, 2, 3…"), ("{date}", "2026-11-17"), ("{time}", "14.03.22") })
            {
                var b = new Button { Style = (Style)FindResource("Btn"), Content = token, Height = 24, Margin = new Thickness(0, 0, 5, 0), ToolTip = "Insert " + tip };
                b.Click += (_, _) =>
                {
                    int at = _pattern.CaretIndex;
                    _pattern.Text = _pattern.Text.Insert(at, token);
                    _pattern.CaretIndex = at + token.Length;
                    _pattern.Focus();
                };
                tokens.Children.Add(b);
            }
            var startRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0) };
            startRow.Children.Add(new TextBlock { Text = "Start at", Style = (Style)FindResource("Dim"), Margin = new Thickness(0, 0, 8, 0) });
            startRow.Children.Add(_start);
            tokens.Children.Add(startRow);
            panel.Children.Add(tokens);
            panel.Children.Add(new TextBlock { Text = "Preview", Style = (Style)FindResource("Label"), Margin = new Thickness(0, 14, 0, 4) });
            panel.Children.Add(_preview);
        }
        panel.Children.Add(_problem);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(_apply);
        panel.Children.Add(buttons);
        Content = panel;

        Loaded += (_, _) =>
        {
            _pattern.Focus();
            if (bulk) _pattern.SelectAll(); else _pattern.SelectAll();
            Refresh();
        };
    }

    private static readonly char[] Bad = Path.GetInvalidFileNameChars();

    /// <summary>The new file names (with extensions), or false with the reason they won't work.</summary>
    private bool Build(out List<string> names, out string? problem)
    {
        names = new List<string>();
        problem = null;
        int.TryParse(_start.Text, out int start);
        int digits = Math.Max(1, (start + _files.Count - 1).ToString().Length);
        string pattern = _pattern.Text.Trim();
        if (_files.Count > 1 && !pattern.Contains("{n}") && !pattern.Contains("{name}") && !pattern.Contains("{time}"))
            pattern += " {n}"; // several files can't all have the same name
        for (int i = 0; i < _files.Count; i++)
        {
            var (path, time) = _files[i];
            string name = pattern
                .Replace("{name}", Path.GetFileNameWithoutExtension(path))
                .Replace("{n}", (start + i).ToString().PadLeft(digits, '0'))
                .Replace("{date}", time.ToString("yyyy-MM-dd"))
                .Replace("{time}", time.ToString("HH.mm.ss"))
                .Trim();
            if (name.Length == 0) { problem = "A name can't be empty."; return false; }
            if (name.IndexOfAny(Bad) >= 0) { problem = "Names can't contain \\ / : * ? \" < > |"; return false; }
            names.Add(name + Path.GetExtension(path));
        }
        // no two the same, and nothing that would overwrite another file
        var built = names;
        var targets = _files.Select((f, i) => Path.Combine(Path.GetDirectoryName(f.Path)!, built[i])).ToList();
        if (targets.Distinct(StringComparer.OrdinalIgnoreCase).Count() < targets.Count) { problem = "Two files would get the same name. Add {n}."; return false; }
        var renamed = new HashSet<string>(_files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        var clash = targets.FirstOrDefault(t => File.Exists(t) && !renamed.Contains(t));
        if (clash != null) { problem = $"{Path.GetFileName(clash)} already exists."; return false; }
        return true;
    }

    private void Refresh()
    {
        bool ok = Build(out var names, out var problem);
        _problem.Text = problem ?? "";
        _problem.Visibility = problem == null ? Visibility.Collapsed : Visibility.Visible;
        _apply.IsEnabled = ok;
        if (ok && _files.Count > 1)
            _preview.Text = string.Join("\n", names.Take(4)) + (names.Count > 4 ? $"\n…and {names.Count - 4} more" : "");
    }
}
