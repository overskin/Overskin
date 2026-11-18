using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Overskin.Linux.Core;
using Overskin.Linux.Platform;
using WPoint = System.Windows.Point;

namespace Overskin.Linux.Interface;

/// <summary>
/// Trace: turn a picture into pen strokes, then let Overskin draw them with the mouse into any
/// drawing app or game (pick where), or pin the picture see-through to trace over it by hand.
/// </summary>
public sealed class TracePage : DockPanel
{
    private static Bitmap? _picture;
    private static PixelRect? _target;
    private TraceSource? _source;
    private TraceResult? _result;
    private CancellationTokenSource? _tracing, _drawing;

    private readonly ComboBox _mode = new() { ItemsSource = new[] { "Outline", "Lines (for line art and text)", "Sketch (for photos)", "Hatch", "Shade" }, SelectedIndex = 0, MinWidth = 220 };
    private readonly Slider _threshold = new() { Minimum = 10, Maximum = 245, Value = 128 };
    private readonly Slider _detail = new() { Minimum = 0.3, Maximum = 4, Value = 1 };
    private readonly Slider _spacing = new() { Minimum = 2, Maximum = 16, Value = 5 };
    private readonly Slider _size = new() { Minimum = 0.2, Maximum = 1, Value = 0.6 };
    private readonly CheckBox _invert = new() { Content = "Swap light and dark" };
    private readonly ComboBox _speed = new() { ItemsSource = new[] { "Fast", "Normal", "Careful (for slow apps and games)" }, SelectedIndex = 1 };
    private readonly TracePreview _preview = new();
    private readonly TextBlock _status = Ui.Label("", "dim");
    private readonly TextBlock _where = Ui.Label("", "dim");

    public TracePage()
    {
        Control Row(string label, Control c) => new StackPanel { Spacing = 4, Children = { Ui.Label(label, "dim"), c } };
        foreach (var s in new[] { _threshold, _detail, _spacing, _size })
            s.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) Retrace(); };
        _mode.SelectionChanged += (_, _) => Retrace();
        _invert.IsCheckedChanged += (_, _) => Retrace();

        var side = new StackPanel
        {
            Width = 300,
            Spacing = 12,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    Children =
                    {
                        Ui.Button("Open a picture", () => _ = OpenAsync(), Icons.Picture),
                        Ui.Button("Latest capture", Latest),
                    },
                },
                Row("Style", _mode),
                Row("Darkness that counts as ink", _threshold),
                Row("Detail (smoother to the right)", _detail),
                Row("Hatch spacing", _spacing),
                Row("Size", _size),
                _invert,
                Ui.Card(new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        Ui.Label("Draw it with the mouse", "h2"),
                        Ui.Button("Choose where to draw…", () => _ = ChooseWhereAsync(), Icons.Area),
                        _where,
                        Row("Speed", _speed),
                        Ui.Button("Draw", () => _ = DrawAsync(), Icons.Pen, "primary"),
                        Ui.Label("Esc stops it" + (OperatingSystem.IsLinux() && ScreenGrabber.IsWayland ? " (on X11; on Wayland, move the mouse away hard or close the app)" : "") + ".", "dim"),
                    },
                }),
                Ui.Button("Pin it see-through to trace by hand", PinToTrace, Icons.Pin),
                _status,
            },
        };
        var scroll = new ScrollViewer { Content = side, Margin = new Thickness(18, 0, 0, 0) };
        DockPanel.SetDock(scroll, Dock.Right);
        Children.Add(scroll);
        Children.Add(new Border { Background = Brushes.White, CornerRadius = new CornerRadius(13, 2, 13, 2), ClipToBounds = true, Child = _preview });

        ShowWhere();
        if (_picture != null) Load(_picture);
        else _status.Text = "Open a picture to begin: a drawing, a logo, a photo.";
    }

    private async Task OpenAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        if (await Ui.PickPictureAsync(top) is not { } file) return;
        try { Load(new Bitmap(file)); }
        catch (Exception ex) { _status.Text = "Couldn't open it: " + ex.Message; }
    }

    private void Latest()
    {
        var folder = App.Settings.SaveFolder;
        var latest = Directory.Exists(folder)
            ? new DirectoryInfo(folder).EnumerateFiles().Where(f => Converter.IsPicture(f.FullName)).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
            : null;
        if (latest == null) { _status.Text = "No captures yet."; return; }
        Load(new Bitmap(latest.FullName));
    }

    private void Load(Bitmap picture)
    {
        _picture = picture;
        var limited = Math.Max(picture.PixelSize.Width, picture.PixelSize.Height) > 2400
            ? Effects.Resize(picture, (int)(picture.PixelSize.Width * 2400.0 / Math.Max(picture.PixelSize.Width, picture.PixelSize.Height)),
                                      (int)(picture.PixelSize.Height * 2400.0 / Math.Max(picture.PixelSize.Width, picture.PixelSize.Height)))
            : picture;
        _source = TraceEngine.PrepareBgra(Imaging.Pixels(limited, out _), limited.PixelSize.Width, limited.PixelSize.Height);
        _threshold.Value = TraceEngine.AutoThreshold(_source);
        _preview.Picture = limited;
        Retrace();
    }

    private TraceOptions Options() => new()
    {
        Mode = _mode.SelectedIndex switch { 1 => TraceMode.Lines, 2 => TraceMode.Sketch, 3 => TraceMode.Hatch, 4 => TraceMode.Shade, _ => TraceMode.Outline },
        Threshold = (int)_threshold.Value,
        Tolerance = _detail.Value,
        Spacing = _spacing.Value,
        Scale = _size.Value,
        Invert = _invert.IsChecked == true,
        Blur = 1,
        MinPathLength = 6,
        OutlineToo = true,
    };

    private void Retrace()
    {
        if (_source == null) return;
        _tracing?.Cancel();
        var cts = _tracing = new CancellationTokenSource();
        var source = _source;
        var options = Options();
        _status.Text = "Tracing…";
        _ = Task.Run(() =>
        {
            try
            {
                var result = TraceEngine.Trace(source, options, cts.Token);
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    _result = result;
                    _preview.Result = result;
                    int points = result.Paths.Sum(p => p.Length);
                    _status.Text = $"{result.Paths.Count} strokes, {points} points · about {Estimate(points, result.Paths.Count).TotalSeconds:0} seconds to draw";
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Avalonia.Threading.Dispatcher.UIThread.Post(() => _status.Text = "Couldn't trace it: " + ex.Message); }
        });
    }

    private int DelayMs => _speed.SelectedIndex switch { 0 => 1, 2 => 8, _ => 3 };
    private TimeSpan Estimate(int points, int strokes) =>
        TimeSpan.FromMilliseconds(points * (DelayMs + (ScreenGrabber.IsWayland ? 4 : 0.1)) + strokes * 12);

    private async Task ChooseWhereAsync()
    {
        var main = TopLevel.GetTopLevel(this) as Window;
        main?.Hide();
        try
        {
            await Task.Delay(300);
            var still = await ScreenGrabber.GrabAsync();
            var pick = await CaptureOverlay.PickAsync(still, recording: true, hint: "Drag over the canvas to draw on  ·  Esc to cancel");
            if (pick != null) _target = pick.Area;
        }
        catch (Exception ex) { _status.Text = ex.Message; }
        finally
        {
            main?.Show();
            ShowWhere();
        }
    }

    private void ShowWhere() =>
        _where.Text = _target is { } t ? $"Draws into {t.Width} × {t.Height} at {t.X}, {t.Y}" : "Pick the drawing area first.";

    /// <summary>The strokes moved into the chosen area, fitted inside it and centred.</summary>
    private List<Point[]> Placed(TraceResult r, PixelRect target)
    {
        double k = Math.Min(target.Width / (double)r.Width, target.Height / (double)r.Height) * 0.96;
        double ox = target.X + (target.Width - r.Width * k) / 2, oy = target.Y + (target.Height - r.Height * k) / 2;
        return r.Paths.Select(p => p.Select(q => new Point(ox + q.X * k, oy + q.Y * k)).ToArray()).ToList();
    }

    private async Task DrawAsync()
    {
        if (_drawing != null) { _drawing.Cancel(); return; }
        if (_result == null) { _status.Text = "Open a picture first."; return; }
        if (_target is not { } target) { _status.Text = "Choose where to draw first."; return; }
        if (Input.CannotDrive() is { } why) { _status.Text = why; return; }

        var strokes = Placed(_result, target);
        var cts = _drawing = new CancellationTokenSource();
        var main = TopLevel.GetTopLevel(this) as Window;
        main?.Hide();
        Desktop.Notify("Drawing in 3 seconds", "Press Esc to stop.", App.IconFile);
        int delay = DelayMs;
        try
        {
            await Task.Delay(3000, cts.Token);
            await Task.Run(async () =>
            {
                foreach (var stroke in strokes)
                {
                    if (stroke.Length == 0) continue;
                    await Input.MoveAsync(stroke[0]);
                    await Task.Delay(4);
                    await Input.ButtonAsync(true);
                    try
                    {
                        for (int i = 1; i < stroke.Length; i++)
                        {
                            if (cts.IsCancellationRequested || Input.EscapeDown()) { cts.Cancel(); return; }
                            await Input.MoveAsync(stroke[i]);
                            if (delay > 0) await Task.Delay(delay);
                        }
                    }
                    finally
                    {
                        await Input.ButtonAsync(false);
                    }
                    await Task.Delay(6);
                }
            });
            _status.Text = cts.IsCancellationRequested ? "Stopped." : "Done drawing.";
        }
        catch (OperationCanceledException) { _status.Text = "Stopped."; }
        catch (Exception ex) { _status.Text = "Drawing stopped: " + ex.Message; }
        finally
        {
            try { await Input.ButtonAsync(false); } catch { }
            _drawing = null;
            main?.Show();
        }
    }

    private void PinToTrace()
    {
        if (_picture == null) { _status.Text = "Open a picture first."; return; }
        var pin = new PinWindow(_picture) { Opacity = 0.5 };
        pin.Show();
        pin.ClickThrough = true;
        _status.Text = "Pinned at half strength, letting clicks through. To move or close it: tray menu → Make pins clickable again.";
    }
}

/// <summary>The picture faintly, with the traced strokes on top.</summary>
public sealed class TracePreview : Control
{
    private Bitmap? _picture;
    private TraceResult? _result;
    public Bitmap? Picture { get => _picture; set { _picture = value; InvalidateVisual(); } }
    public TraceResult? Result { get => _result; set { _result = value; InvalidateVisual(); } }

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Brushes.White, new Rect(Bounds.Size));
        if (_picture == null) return;
        double k = Math.Min(Bounds.Width / _picture.PixelSize.Width, Bounds.Height / _picture.PixelSize.Height) * 0.94;
        double w = _picture.PixelSize.Width * k, h = _picture.PixelSize.Height * k;
        var frame = new Rect((Bounds.Width - w) / 2, (Bounds.Height - h) / 2, w, h);
        using (ctx.PushOpacity(0.18)) ctx.DrawImage(_picture, new Rect(_picture.Size), frame);
        if (_result == null) return;
        double s = w / _result.Width;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            foreach (var path in _result.Paths)
            {
                if (path.Length < 2) continue;
                g.BeginFigure(new Point(frame.X + path[0].X * s, frame.Y + path[0].Y * s), false);
                for (int i = 1; i < path.Length; i++) g.LineTo(new Point(frame.X + path[i].X * s, frame.Y + path[i].Y * s));
                g.EndFigure(false);
            }
        }
        ctx.DrawGeometry(null, new Pen(Brushes.Black, 1.2) { LineJoin = PenLineJoin.Round, LineCap = PenLineCap.Round }, geometry);
    }
}
