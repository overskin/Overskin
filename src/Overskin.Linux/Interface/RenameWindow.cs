using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace Overskin.Linux.Interface;

/// <summary>
/// Renaming one file, or several at once with a pattern: "Trip {n}" gives Trip 1, Trip 2…;
/// {date} and {name} are filled in too. The extension is kept.
/// </summary>
public sealed class RenameWindow : Window
{
    private readonly List<string> _paths;
    private readonly TextBox _name = new();
    private readonly NumericUpDown _start = new() { Value = 1, Minimum = 0, Maximum = 99999, Increment = 1, FormatString = "0", Width = 120 };
    private readonly TextBlock _preview = Ui.Label("", "dim");
    public List<string> Renamed { get; } = new();

    public RenameWindow(List<string> paths)
    {
        _paths = paths;
        bool many = paths.Count > 1;
        Title = many ? $"Rename {paths.Count} files" : "Rename";
        Icon = App.Icon;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _name.Text = many ? Common(paths) + " {n}" : Path.GetFileNameWithoutExtension(paths[0]);
        _name.TextChanged += (_, _) => UpdatePreview();
        _start.ValueChanged += (_, _) => UpdatePreview();

        var ok = Ui.Button("Rename", Apply, cls: "primary");
        var cancel = Ui.Button("Cancel", Close);
        var panel = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 10,
            Children =
            {
                Ui.Label(many ? "New names" : "New name", "h2"),
                _name,
            },
        };
        if (many)
        {
            panel.Children.Add(Ui.Label("{n} counts up, {date} is the day each was made, {name} is its old name.", "dim"));
            panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { Ui.Label("Count from"), _start } });
        }
        panel.Children.Add(_preview);
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } });
        Content = panel;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Apply();
            else if (e.Key == Key.Escape) Close();
        };
        Opened += (_, _) => { _name.Focus(); _name.SelectAll(); };
        UpdatePreview();
    }

    private static string Common(List<string> paths)
    {
        var names = paths.Select(Path.GetFileNameWithoutExtension).ToList();
        var first = names[0]!;
        int n = first.Length;
        foreach (var name in names) n = Math.Min(n, first.Zip(name!).TakeWhile(p => p.First == p.Second).Count());
        var common = first[..n].TrimEnd(' ', '_', '-', '(');
        return common.Length >= 3 ? common : "Screenshot";
    }

    private List<string> Targets()
    {
        int n = (int)(_start.Value ?? 1);
        var pattern = _name.Text?.Trim() ?? "";
        var result = new List<string>();
        int width = Math.Max(1, (n + _paths.Count - 1).ToString().Length);
        foreach (var p in _paths)
        {
            var name = pattern
                .Replace("{n}", n.ToString().PadLeft(_paths.Count > 9 ? width : 1, '0'))
                .Replace("{date}", File.GetLastWriteTime(p).ToString("yyyy-MM-dd"))
                .Replace("{name}", Path.GetFileNameWithoutExtension(p));
            if (_paths.Count > 1 && !pattern.Contains("{n}")) name += $" {n}";
            foreach (var bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');
            result.Add(Path.Combine(Path.GetDirectoryName(p)!, name + Path.GetExtension(p)));
            n++;
        }
        return result;
    }

    private void UpdatePreview()
    {
        var targets = Targets();
        _preview.Text = string.Join("\n", targets.Take(3).Select(Path.GetFileName)) + (targets.Count > 3 ? "\n…" : "");
    }

    private void Apply()
    {
        var targets = Targets();
        if (targets.Any(t => Path.GetFileNameWithoutExtension(t).Length == 0)) { _preview.Text = "Type a name first"; return; }
        if (targets.Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Count) { _preview.Text = "Those names would clash: add {n}"; return; }
        var clash = targets.Where((t, i) => File.Exists(t) && !string.Equals(t, _paths[i], StringComparison.Ordinal) && !_paths.Contains(t)).FirstOrDefault();
        if (clash != null) { _preview.Text = Path.GetFileName(clash) + " already exists"; return; }

        // via temporary names, so swapping names around between the files can't collide
        var temp = _paths.Select(p => p + ".overskin-rename").ToList();
        try
        {
            for (int i = 0; i < _paths.Count; i++) File.Move(_paths[i], temp[i]);
            for (int i = 0; i < _paths.Count; i++) { File.Move(temp[i], targets[i]); Renamed.Add(targets[i]); }
            Close();
        }
        catch (Exception ex)
        {
            for (int i = 0; i < _paths.Count; i++) if (File.Exists(temp[i])) File.Move(temp[i], _paths[i]);
            Renamed.Clear();
            _preview.Text = "Couldn't rename: " + ex.Message;
        }
    }
}
