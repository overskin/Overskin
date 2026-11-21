using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Overskin;

/// <summary>Blurring private details in the video, and grabbing a still frame.</summary>
public partial class VideoEditorWindow
{
    private readonly List<VideoBlur> _blurs = new();
    private VideoBlur? _blurGrabbed, _blurChosen;
    private bool _blurMode;
    private Point _blurFrom;

    /// <summary>Opens a picture in the screenshot editor (set by the main window).</summary>
    public static Action<BitmapSource, string>? OpenPicture { get; set; }

    private void AddBlur_Click(object sender, RoutedEventArgs e)
    {
        _blurMode = !_blurMode;
        BlurButton.Content = _blurMode ? "Drag over the video…" : "Add a blur box";
        OverlayCanvas.Cursor = _blurMode ? Cursors.Cross : null;
        if (_blurMode) Play(false);
    }

    private VideoBlur? BlurAt(Point video)
    {
        double now = Player.Position.TotalSeconds;
        for (int i = _blurs.Count - 1; i >= 0; i--)
        {
            var b = _blurs[i];
            if ((now >= b.Start && now < b.End || b == _blurChosen) && b.Area.Contains(video)) return b;
        }
        return null;
    }

    /// <summary>Starts a new blur box or picks one up; false when the press is for something else.</summary>
    private bool BlurMouseDown(Point p)
    {
        if (_blurMode)
        {
            double now = Player.Position.TotalSeconds;
            _blurGrabbed = new VideoBlur { Area = new Rect(p, p), Start = now, End = _length };
            _blurs.Add(_blurGrabbed);
            _blurChosen = _blurGrabbed;
            _blurFrom = p;
            return true;
        }
        if (OverlayAt(p) != null) return false; // labels and arrows sit on top
        _blurGrabbed = BlurAt(p);
        if (_blurGrabbed == null) return false;
        _blurChosen = _blurGrabbed;
        _blurFrom = p;
        Play(false);
        RebuildBlurList();
        DrawOverlays();
        return true;
    }

    private bool BlurMouseMove(Point p)
    {
        if (_blurGrabbed == null) return false;
        if (_blurMode) _blurGrabbed.Area = new Rect(_blurFrom, p);
        else
        {
            var d = p - _blurFrom;
            var r = _blurGrabbed.Area;
            _blurGrabbed.Area = new Rect(r.X + d.X, r.Y + d.Y, r.Width, r.Height);
            _blurFrom = p;
        }
        DrawOverlays();
        return true;
    }

    private bool BlurMouseUp()
    {
        if (_blurGrabbed == null && !_blurMode) return false;
        if (_blurMode)
        {
            if (_blurGrabbed != null && (_blurGrabbed.Area.Width < 6 || _blurGrabbed.Area.Height < 6)) { _blurs.Remove(_blurGrabbed); _blurChosen = null; }
            _blurMode = false;
            BlurButton.Content = "Add a blur box";
            OverlayCanvas.Cursor = null;
        }
        _blurGrabbed = null;
        RebuildBlurList();
        DrawOverlays();
        return true;
    }

    /// <summary>The blur boxes due now, frosted, with the chosen one outlined.</summary>
    private void DrawBlurs(double scale, double ox, double oy)
    {
        double now = Player.Position.TotalSeconds;
        foreach (var b in _blurs)
        {
            bool due = now >= b.Start && now < b.End;
            if (!due && b != _blurChosen) continue;
            var box = new Rectangle
            {
                Width = Math.Max(1, b.Area.Width * scale), Height = Math.Max(1, b.Area.Height * scale),
                Fill = new SolidColorBrush(Color.FromArgb(due ? (byte)236 : (byte)90, 0x8A, 0x8F, 0x98)),
                RadiusX = 3, RadiusY = 3, IsHitTestVisible = false,
            };
            if (b == _blurChosen)
            {
                box.SetResourceReference(Shape.StrokeProperty, "Accent");
                box.StrokeThickness = 1.5;
                box.StrokeDashArray = new DoubleCollection { 4, 3 };
            }
            Canvas.SetLeft(box, ox + b.Area.X * scale);
            Canvas.SetTop(box, oy + b.Area.Y * scale);
            OverlayCanvas.Children.Add(box);
            if (due && box.Width > 50 && box.Height > 18)
            {
                // the preview only covers it; the saved video is properly blurred
                var label = new TextBlock { Text = "Blurred", FontSize = 11, Foreground = Brushes.White, Opacity = 0.85, IsHitTestVisible = false };
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(label, ox + b.Area.X * scale + (box.Width - label.DesiredSize.Width) / 2);
                Canvas.SetTop(label, oy + b.Area.Y * scale + (box.Height - label.DesiredSize.Height) / 2);
                OverlayCanvas.Children.Add(label);
            }
        }
    }

    private void RebuildBlurList()
    {
        if (BlurList == null) return;
        BlurList.Children.Clear();
        int n = 0;
        foreach (var b in _blurs)
        {
            n++;
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { Cursor = Cursors.Hand };
            text.Children.Add(new TextBlock
            {
                Text = b.Label.Length > 0 ? "Blur: " + b.Label : $"Blur {n}", TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = b == _blurChosen ? FontWeights.SemiBold : FontWeights.Normal,
            });
            var when = new TextBlock { Text = $"{Clock(b.Start, tenths: true)} to {Clock(b.End, tenths: true)}", FontSize = 11 };
            when.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
            text.Children.Add(when);
            var chosen = b;
            text.MouseLeftButtonUp += (_, _) => { _blurChosen = chosen; Seek(chosen.Start); RebuildBlurList(); DrawOverlays(); };
            row.Children.Add(text);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Button Small(string label, string tip, Action act)
            {
                var button = new Button { Content = label, ToolTip = tip, Height = 24, Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(7, 0, 7, 0) };
                button.SetResourceReference(StyleProperty, "Btn");
                button.Click += (_, _) => { act(); RebuildBlurList(); DrawOverlays(); };
                return button;
            }
            buttons.Children.Add(Small("From", "Starts where the video is now", () => chosen.Start = Math.Min(Player.Position.TotalSeconds, chosen.End - 0.1)));
            buttons.Children.Add(Small("To", "Ends where the video is now", () => chosen.End = Math.Max(Player.Position.TotalSeconds, chosen.Start + 0.1)));
            buttons.Children.Add(Small("✕", "Remove", () => { _blurs.Remove(chosen); if (_blurChosen == chosen) _blurChosen = null; }));
            Grid.SetColumn(buttons, 1);
            row.Children.Add(buttons);
            BlurList.Children.Add(row);
        }
    }

    /// <summary>Reads the frame showing now and blurs every private detail on it, from here to the end.</summary>
    private async void FindPrivate_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        Play(false);
        double now = Player.Position.TotalSeconds;
        FindPrivateButton.IsEnabled = false;
        StatusText.Text = "Reading this frame…";
        try
        {
            var frame = await Task.Run(() => MediaReader.FrameAt(_path, TimeSpan.FromSeconds(now)));
            var found = await Redaction.FindPrivateAsync(frame);
            if (found == null)
            {
                Toast.Show("Windows has no text-recognition language installed. Add one under Settings › Time & language › Language.", true);
                return;
            }
            int added = 0;
            foreach (var (area, kind) in found)
            {
                var padded = area;
                padded.Inflate(Math.Max(4, area.Height * 0.25), Math.Max(3, area.Height * 0.2));
                // a detail that's already covered at this moment isn't added twice
                if (_blurs.Any(b => now >= b.Start && now < b.End && b.Area.Contains(padded))) continue;
                _blurs.Add(new VideoBlur { Area = padded, Start = now, End = _length, Label = kind });
                added++;
            }
            Toast.Show(added == 0 ? "Nothing private found on this frame." : $"Blurred {added} private detail{(added == 1 ? "" : "s")} from here on. Use From and To to change when.");
            RebuildBlurList();
            DrawOverlays();
        }
        catch (Exception ex)
        {
            Toast.Show("Couldn't read this frame: " + ex.Message, true);
        }
        finally
        {
            StatusText.Text = "";
            FindPrivateButton.IsEnabled = true;
        }
    }

    /// <summary>The frame showing now as a picture: copied, and opened in the screenshot editor.</summary>
    private async void GrabStill_Click(object sender, RoutedEventArgs e)
    {
        Play(false);
        double now = Player.Position.TotalSeconds;
        try
        {
            var frame = await Task.Run(() => MediaReader.FrameAt(_path, TimeSpan.FromSeconds(now)));
            try { Clipboard.SetImage(frame); } catch { }
            string name = $"{System.IO.Path.GetFileNameWithoutExtension(_path)} at {Clock(now, tenths: true).Replace(':', '.')}";
            if (OpenPicture != null)
            {
                OpenPicture(frame, name);
                Toast.Show("Frame copied and opened in the editor.");
            }
            else Toast.Show("Frame copied.");
        }
        catch (Exception ex)
        {
            Toast.Show("Couldn't grab the frame: " + ex.Message, true);
        }
    }

    internal void AddBlurForTest(VideoBlur b) { _blurs.Add(b); RebuildBlurList(); DrawOverlays(); }
    internal int BlurCountForTest => _blurs.Count;
}
