using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Overskin;

/// <summary>
/// Subtitles in the video editor: made from what's said in the video, each line editable, shown
/// over the video while it plays and put into the saved video (with an .srt file beside it).
/// </summary>
public partial class VideoEditorWindow
{
    private IEnumerable<VideoOverlay> Captions => _overlays.Where(o => o.Kind == VideoOverlay.Kinds.Caption);

    private async void MakeSubtitles_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (!Subtitles.Available)
        {
            Toast.Show("This copy of Overskin was built without the speech model, so it can't make subtitles.", true);
            return;
        }
        _busy = true;
        Play(false);
        SubtitlesButton.IsEnabled = false;
        SaveProgress.Visibility = Visibility.Visible;
        SaveProgress.Value = 0;
        StatusText.Text = "Listening to the video…";
        try
        {
            var lines = await Subtitles.MakeAsync(_path, new Progress<double>(p => SaveProgress.Value = p));
            SetCaptions(lines);
            StatusText.Text = lines.Count == 0
                ? "Nobody speaks in this video, so there's nothing to write out."
                : $"Made {lines.Count} subtitle line{(lines.Count == 1 ? "" : "s")}. Play it to check them.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "";
            Toast.Show("Couldn't make subtitles: " + ex.Message, true);
        }
        finally
        {
            SaveProgress.Visibility = Visibility.Collapsed;
            SubtitlesButton.IsEnabled = true;
            _busy = false;
        }
    }

    /// <summary>Puts these lines in place of any subtitles there were.</summary>
    private void SetCaptions(IEnumerable<Caption> lines)
    {
        foreach (var old in Captions.ToList()) { _overlays.Remove(old); _drawn.Remove(old); }
        if (_chosen?.Kind == VideoOverlay.Kinds.Caption) _chosen = null;
        var at = CaptionHome();
        foreach (var l in lines)
            _overlays.Add(new VideoOverlay { Kind = VideoOverlay.Kinds.Caption, Text = l.Text, A = at, Start = l.Start, End = Math.Min(_length, l.End), Look = _captionLook, Words = _captionWords });
        RebuildSubtitleList();
        DrawOverlays();
    }

    // the look and word mode, kept for the next video while Overskin runs
    private static string _captionLook = "Classic", _captionWords = "Light";

    private void BuildCaptionChoices()
    {
        foreach (var (name, tip) in CaptionLook.Looks)
        {
            var b = new RadioButton { Style = (Style)FindResource("Seg"), GroupName = "CaptionLook", Content = name, ToolTip = tip, MinWidth = 0, Padding = new Thickness(9, 0, 9, 0), Margin = new Thickness(0, 0, 4, 4), IsChecked = name == _captionLook };
            var chosen = name;
            b.Checked += (_, _) => { _captionLook = chosen; RestyleCaptions(); };
            CaptionLooks.Children.Add(b);
        }
        foreach (var (name, label, tip) in CaptionLook.WordModes)
        {
            var b = new RadioButton { Style = (Style)FindResource("Seg"), GroupName = "CaptionWords", Content = label, ToolTip = tip, MinWidth = 0, Padding = new Thickness(9, 0, 9, 0), Margin = new Thickness(0, 0, 4, 4), IsChecked = name == _captionWords };
            var chosen = name;
            b.Checked += (_, _) => { _captionWords = chosen; RestyleCaptions(); };
            CaptionWords.Children.Add(b);
        }
    }

    /// <summary>Every subtitle line takes the chosen look and word mode.</summary>
    private void RestyleCaptions()
    {
        foreach (var c in Captions)
        {
            c.Look = _captionLook;
            c.Words = _captionWords;
        }
        DrawOverlays();
    }

    /// <summary>Where subtitles sit: in the middle, just above the bottom edge.</summary>
    private Point CaptionHome() => new(Math.Max(2, Player.NaturalVideoWidth) / 2.0, Math.Max(2, Player.NaturalVideoHeight) * 0.94);

    private void ClearSubtitles_Click(object sender, RoutedEventArgs e)
    {
        SetCaptions(Array.Empty<Caption>());
        StatusText.Text = "";
    }

    /// <summary>One row per line: when (click to go there), its words to fix, and a button to remove it.</summary>
    private void RebuildSubtitleList()
    {
        if (SubtitleList == null) return;
        SubtitleList.Children.Clear();
        var captions = Captions.OrderBy(c => c.Start).ToList();
        SubtitleScroll.Visibility = captions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearSubtitlesButton.Visibility = captions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CaptionStyleRows.Visibility = captions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SubtitlesButton.Content = captions.Count > 0 ? "Make them again" : "Make subtitles";
        SubtitlesText.Text = captions.Count > 0 ? $"{captions.Count} line{(captions.Count == 1 ? "" : "s")}" : "";
        foreach (var c in captions)
        {
            var line = c;
            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var when = new TextBlock
            {
                Text = Clock(line.Start, tenths: true), FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 44, Margin = new Thickness(0, 0, 6, 0), Cursor = Cursors.Hand, ToolTip = "Go to this line",
            };
            when.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
            when.MouseLeftButtonUp += (_, _) => { _chosen = line; Seek(line.Start + 0.01); DrawOverlays(); };
            row.Children.Add(when);
            var box = new TextBox { Text = line.Text, Height = 24, TextWrapping = TextWrapping.NoWrap };
            box.SetResourceReference(StyleProperty, "TextField");
            box.TextChanged += (_, _) =>
            {
                line.Text = box.Text;
                DrawOverlays();
            };
            box.GotKeyboardFocus += (_, _) =>
            {
                _chosen = line;
                if (!(Player.Position.TotalSeconds >= line.Start && Player.Position.TotalSeconds < line.End)) Seek(line.Start + 0.01);
                DrawOverlays();
            };
            Grid.SetColumn(box, 1);
            row.Children.Add(box);
            var remove = new Button { Content = "✕", ToolTip = "Remove this line", Height = 24, Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(7, 0, 7, 0) };
            remove.SetResourceReference(StyleProperty, "Btn");
            remove.Click += (_, _) =>
            {
                _overlays.Remove(line);
                _drawn.Remove(line);
                if (_chosen == line) _chosen = null;
                RebuildSubtitleList();
                DrawOverlays();
            };
            Grid.SetColumn(remove, 2);
            row.Children.Add(remove);
            SubtitleList.Children.Add(row);
        }
    }

    /// <summary>Dragging one subtitle moves them all, so they stay in one place through the video.</summary>
    private void MoveCaptions(Vector by)
    {
        foreach (var c in Captions) c.A += by;
    }

    /// <summary>
    /// Writes the subtitles as an .srt file beside a saved video, with their times as they are in
    /// it (after trimming and cuts). Lines that fall in a cut are left out.
    /// </summary>
    private void SaveSrt(string video, IReadOnlyList<(double Start, double End)> keep)
    {
        var lines = new List<Caption>();
        foreach (var c in Captions.OrderBy(c => c.Start))
        {
            double from = VideoEdit.ToOutput(c.Start, keep), to = VideoEdit.ToOutput(c.End, keep);
            if (to - from >= 0.05 && c.Text.Trim().Length > 0) lines.Add(new Caption(from, to, c.Text.Trim()));
        }
        string srt = Path.ChangeExtension(video, ".srt");
        if (lines.Count == 0) return;
        try { File.WriteAllText(srt, Subtitles.ToSrt(lines), new System.Text.UTF8Encoding(false)); }
        catch { }
    }

#if DEBUG
    internal void SetCaptionsForTest(IEnumerable<Caption> lines) => SetCaptions(lines);
    internal int CaptionCountForTest => Captions.Count();
    internal int SubtitleRowsForTest => SubtitleList.Children.Count;
    internal Task<bool> SaveForTest(string output) => SaveTo(output);
    internal void CutForTest(double a, double b) => AddCut(a, b);
    internal void RedrawForTest() => DrawOverlays();
#endif
}
