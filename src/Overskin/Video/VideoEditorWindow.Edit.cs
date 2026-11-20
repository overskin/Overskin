using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Overskin;

/// <summary>
/// Editing beyond trimming: cutting idle or unwanted parts out of the middle, and putting text
/// labels and arrows over the video at chosen times.
/// </summary>
public partial class VideoEditorWindow
{
    private readonly List<(double Start, double End)> _cuts = new();
    private double? _cutFrom;
    private readonly List<VideoOverlay> _overlays = new();
    private readonly Dictionary<VideoOverlay, (string Key, BitmapSource Picture, Point At)> _drawn = new();
    private VideoOverlay? _grabbed, _chosen;
    private Point _grabFrom;
    private bool _arrowMode, _pressed, _dragged;

    // ---- cuts ----------------------------------------------------------------------------

    private (double Start, double End)? CutAt(double t)
    {
        foreach (var c in _cuts)
            if (t >= c.Start && t < c.End - 0.02) return c;
        return null;
    }

    private void AddCut(double a, double b)
    {
        if (b - a < 0.1) return;
        _cuts.Add((a, b));
        // joined up where they overlap
        _cuts.Sort((x, y) => x.Start.CompareTo(y.Start));
        for (int i = _cuts.Count - 1; i > 0; i--)
            if (_cuts[i].Start <= _cuts[i - 1].End)
            {
                _cuts[i - 1] = (_cuts[i - 1].Start, Math.Max(_cuts[i - 1].End, _cuts[i].End));
                _cuts.RemoveAt(i);
            }
        UpdateCuts();
    }

    private void Cut_Click(object sender, RoutedEventArgs e)
    {
        double now = Player.Position.TotalSeconds;
        if (_cutFrom is not { } from)
        {
            _cutFrom = now;
            CutButton.Content = "End the cut here";
            StatusText.Text = $"Cutting from {Clock(now, tenths: true)}: go to where it should end and click again.";
            return;
        }
        _cutFrom = null;
        CutButton.Content = "Cut from here";
        StatusText.Text = "";
        AddCut(Math.Min(from, now), Math.Max(from, now));
    }

    private void ClearCuts_Click(object sender, RoutedEventArgs e)
    {
        _cuts.Clear();
        _cutFrom = null;
        CutButton.Content = "Cut from here";
        UpdateCuts();
    }

    private async void CutIdle_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        Play(false);
        IdleButton.IsEnabled = false;
        SaveProgress.Visibility = Visibility.Visible;
        StatusText.Text = "Looking for idle parts…";
        try
        {
            var idle = await VideoEdit.FindIdleAsync(_path, 2.0, new Progress<double>(p => SaveProgress.Value = p));
            foreach (var (s, en) in idle) AddCut(s, en);
            StatusText.Text = idle.Count == 0 ? "No idle parts: something moves all the way through."
                : $"Cut {idle.Count} idle part{(idle.Count == 1 ? "" : "s")}, {Clock(idle.Sum(i => i.End - i.Start), tenths: true)} in all.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "";
            Toast.Show("Couldn't look through the video: " + ex.Message, true);
        }
        finally
        {
            SaveProgress.Visibility = Visibility.Collapsed;
            IdleButton.IsEnabled = true;
            _busy = false;
        }
    }

    /// <summary>The red bands under the scrubber and the totals.</summary>
    private void UpdateCuts()
    {
        if (Bands == null) return;
        Bands.Children.Clear();
        double width = Bands.ActualWidth;
        if (_length > 0 && width > 0)
            foreach (var (s, e) in _cuts)
            {
                var band = new Rectangle { Height = 4, Width = Math.Max(2, (e - s) / _length * width), Fill = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)), RadiusX = 2, RadiusY = 2 };
                Canvas.SetLeft(band, s / _length * width);
                Bands.Children.Add(band);
            }
        var (start, end) = Range;
        double kept = VideoEdit.Keep(start, end, _cuts).Sum(k => k.End - k.Start);
        CutsText.Text = _cuts.Count == 0 ? "" : $"{_cuts.Count} cut{(_cuts.Count == 1 ? "" : "s")}";
        TrimText.Text = $"{Clock(kept, tenths: true)} kept";
    }

    // ---- text and arrows -----------------------------------------------------------------

    private void AddText_Click(object sender, RoutedEventArgs e)
    {
        string text = OverlayTextBox.Text.Trim();
        if (text.Length == 0)
        {
            Toast.Show("Type the words first, then Add text.");
            OverlayTextBox.Focus();
            return;
        }
        double now = Player.Position.TotalSeconds;
        var o = new VideoOverlay
        {
            Kind = VideoOverlay.Kinds.Text, Text = text,
            A = new Point(Player.NaturalVideoWidth / 2.0, Player.NaturalVideoHeight * 0.82),
            Start = now, End = Math.Min(_length, now + 3),
        };
        _overlays.Add(o);
        _chosen = o;
        OverlayTextBox.Clear();
        RebuildOverlayList();
        DrawOverlays();
    }

    private void OverlayText_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        AddText_Click(sender, e);
        e.Handled = true;
    }

    private void AddArrow_Click(object sender, RoutedEventArgs e)
    {
        _arrowMode = !_arrowMode;
        ArrowButton.Content = _arrowMode ? "Drag on the video…" : "Add an arrow";
        OverlayCanvas.Cursor = _arrowMode ? Cursors.Cross : null;
        if (_arrowMode) Play(false);
    }

    /// <summary>Where the video sits inside the overlay canvas, and how big.</summary>
    private (double Scale, double X, double Y) Fit()
    {
        double vw = Math.Max(1, Player.NaturalVideoWidth), vh = Math.Max(1, Player.NaturalVideoHeight);
        double aw = OverlayCanvas.ActualWidth, ah = OverlayCanvas.ActualHeight;
        double s = Math.Min(aw / vw, ah / vh);
        return (s, (aw - vw * s) / 2, (ah - vh * s) / 2);
    }

    private Point ToVideo(Point c)
    {
        var (s, x, y) = Fit();
        return s <= 0 ? c : new Point((c.X - x) / s, (c.Y - y) / s);
    }

    private (BitmapSource Picture, Point At) Drawn(VideoOverlay o)
    {
        string key = $"{o.Kind}|{o.Text}|{o.A}|{o.B}";
        if (_drawn.TryGetValue(o, out var d) && d.Key == key) return (d.Picture, d.At);
        var (picture, at) = o.Render(Math.Max(2, Player.NaturalVideoHeight));
        _drawn[o] = (key, picture, at);
        return (picture, at);
    }

    /// <summary>Shows the labels and arrows due at this moment (and the one being worked on).</summary>
    private void DrawOverlays()
    {
        if (OverlayCanvas == null || !_opened) return;
        OverlayCanvas.Children.Clear();
        var (s, ox, oy) = Fit();
        if (s <= 0) return;
        double now = Player.Position.TotalSeconds;
        foreach (var o in _overlays)
        {
            bool due = now >= o.Start && now < o.End;
            if (!due && o != _chosen) continue;
            var (picture, at) = Drawn(o);
            var image = new Image { Source = picture, Width = picture.PixelWidth * s, Height = picture.PixelHeight * s, Opacity = due ? 1 : 0.45, IsHitTestVisible = false };
            Canvas.SetLeft(image, ox + at.X * s);
            Canvas.SetTop(image, oy + at.Y * s);
            OverlayCanvas.Children.Add(image);
            if (o == _chosen)
            {
                var frame = new Rectangle
                {
                    Width = image.Width + 6, Height = image.Height + 6, Stroke = (Brush)FindResource("Accent"), StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 4, 3 }, IsHitTestVisible = false,
                };
                Canvas.SetLeft(frame, ox + at.X * s - 3);
                Canvas.SetTop(frame, oy + at.Y * s - 3);
                OverlayCanvas.Children.Add(frame);
            }
        }
    }

    private VideoOverlay? OverlayAt(Point video)
    {
        double now = Player.Position.TotalSeconds;
        for (int i = _overlays.Count - 1; i >= 0; i--)
        {
            var o = _overlays[i];
            if (!(now >= o.Start && now < o.End) && o != _chosen) continue;
            var (picture, at) = Drawn(o);
            if (new Rect(at.X, at.Y, picture.PixelWidth, picture.PixelHeight).Contains(video)) return o;
        }
        return null;
    }

    private void Overlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var p = ToVideo(e.GetPosition(OverlayCanvas));
        _pressed = true;
        _dragged = false;
        _grabFrom = p;
        OverlayCanvas.CaptureMouse();
        if (_arrowMode)
        {
            double now = Player.Position.TotalSeconds;
            _grabbed = new VideoOverlay { Kind = VideoOverlay.Kinds.Arrow, A = p, B = p, Start = now, End = Math.Min(_length, now + 3) };
            _overlays.Add(_grabbed);
            _chosen = _grabbed;
            return;
        }
        _grabbed = OverlayAt(p);
        if (_grabbed != null) { _chosen = _grabbed; Play(false); RebuildOverlayList(); DrawOverlays(); }
    }

    private void Overlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_pressed || _grabbed == null) return;
        var p = ToVideo(e.GetPosition(OverlayCanvas));
        if (!_dragged && (p - _grabFrom).Length < 3) return;
        _dragged = true;
        if (_arrowMode) _grabbed.B = p;
        else
        {
            var d = p - _grabFrom;
            _grabbed.A += d;
            _grabbed.B += d;
            _grabFrom = p;
        }
        DrawOverlays();
    }

    private void Overlay_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pressed) return;
        _pressed = false;
        OverlayCanvas.ReleaseMouseCapture();
        if (_arrowMode)
        {
            if (_grabbed != null && (_grabbed.B - _grabbed.A).Length < 12) { _overlays.Remove(_grabbed); _chosen = null; }
            _arrowMode = false;
            ArrowButton.Content = "Add an arrow";
            OverlayCanvas.Cursor = null;
            _grabbed = null;
            RebuildOverlayList();
            DrawOverlays();
            return;
        }
        // a click that didn't move anything plays or pauses, as on the video itself
        if (!_dragged && _grabbed == null)
        {
            _chosen = null;
            Play(!_playing);
        }
        _grabbed = null;
        RebuildOverlayList();
        DrawOverlays();
    }

    private void Overlay_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        DrawOverlays();
        UpdateCuts();
    }

    /// <summary>One row per label or arrow: what and when, with buttons to set its times and remove it.</summary>
    private void RebuildOverlayList()
    {
        OverlayList.Children.Clear();
        foreach (var o in _overlays)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel();
            var name = new TextBlock { Text = o.Kind == VideoOverlay.Kinds.Arrow ? "Arrow" : $"“{o.Text}”", TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = o == _chosen ? FontWeights.SemiBold : FontWeights.Normal };
            var when = new TextBlock { Text = $"{Clock(o.Start, tenths: true)} to {Clock(o.End, tenths: true)}", FontSize = 11 };
            when.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
            text.Children.Add(name);
            text.Children.Add(when);
            text.Cursor = Cursors.Hand;
            var chosen = o;
            text.MouseLeftButtonUp += (_, _) => { _chosen = chosen; Seek(chosen.Start); RebuildOverlayList(); };
            row.Children.Add(text);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Button Small(string label, string tip, Action act)
            {
                var b = new Button { Content = label, ToolTip = tip, Height = 24, Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(7, 0, 7, 0) };
                b.SetResourceReference(StyleProperty, "Btn");
                b.Click += (_, _) => act();
                return b;
            }
            buttons.Children.Add(Small("From", "Starts where the video is now", () => { chosen.Start = Math.Min(Player.Position.TotalSeconds, chosen.End - 0.1); RebuildOverlayList(); DrawOverlays(); }));
            buttons.Children.Add(Small("To", "Ends where the video is now", () => { chosen.End = Math.Max(Player.Position.TotalSeconds, chosen.Start + 0.1); RebuildOverlayList(); DrawOverlays(); }));
            buttons.Children.Add(Small("✕", "Remove", () => { _overlays.Remove(chosen); _drawn.Remove(chosen); if (_chosen == chosen) _chosen = null; RebuildOverlayList(); DrawOverlays(); }));
            Grid.SetColumn(buttons, 1);
            row.Children.Add(buttons);
            OverlayList.Children.Add(row);
        }
    }

#if DEBUG
    internal void AddCutForTest(double a, double b) => AddCut(a, b);
    internal int CutCountForTest => _cuts.Count;
#endif
}
