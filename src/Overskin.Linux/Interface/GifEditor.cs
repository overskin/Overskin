using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Overskin.Linux.Core;
using Overskin.Linux.Platform;

namespace Overskin.Linux.Interface;

/// <summary>
/// Trims, speeds up, loops and captions a GIF or video, then saves or copies it as a GIF
/// (or an MP4). The frames are read with ffmpeg.
/// </summary>
public sealed class GifEditor : Window
{
    private readonly string _source;
    private readonly List<string> _frames;
    private readonly double _fps;
    private readonly string _work;
    private readonly Image _preview = new() { Stretch = Stretch.Uniform };
    private readonly Slider _scrub = new(), _from = new(), _to = new(), _textSize = new() { Minimum = 12, Maximum = 72, Value = 32 };
    private readonly ComboBox _loop = new() { ItemsSource = new[] { "Forwards", "Backwards", "Boomerang" }, SelectedIndex = 0 };
    private readonly ComboBox _speed = new() { ItemsSource = new[] { "¼×", "0.5×", "0.75×", "1×", "1.5×", "2×", "3×" }, SelectedIndex = 3 };
    private readonly ComboBox _size = new() { ItemsSource = new[] { "320 wide", "480 wide", "720 wide", "As it is" }, SelectedIndex = 2 };
    private readonly ComboBox _style = new() { ItemsSource = new[] { "No text", "Caption bar", "Meme (top and bottom)", "Speech bubble" }, SelectedIndex = 0 };
    private readonly ComboBox _colour = new() { ItemsSource = new[] { "White", "Black", "Yellow" }, SelectedIndex = 0 };
    private readonly ComboBox _where = new() { ItemsSource = new[] { "Above the GIF", "Below the GIF" }, SelectedIndex = 0 };
    private readonly ComboBox _tail = new() { ItemsSource = new[] { "Left", "Middle", "Right" }, SelectedIndex = 0, IsVisible = false };
    private readonly ComboBox _font = new();
    private readonly ComboBox _repeat = new() { ItemsSource = new[] { "Forever", "Once", "Three times" }, SelectedIndex = 0 };
    private readonly ComboBox _look = new() { ItemsSource = new[] { "As it is", "Grey", "Sepia", "Vivid" }, SelectedIndex = 0 };
    private readonly CheckBox _mirror = new() { Content = "Mirror" };
    private readonly CheckBox _fewer = new() { Content = "Fewer frames (about half the size)" };
    /// <summary>The part kept, as fractions of the frame (dragged out on the picture); null for all of it.</summary>
    private Rect? _crop;
    private readonly TextBox _top = new() { Watermark = "Caption" }, _bottom = new() { Watermark = "Bottom text", IsVisible = false };
    private readonly TextBlock _info = Ui.Label("", "dim");
    private readonly DispatcherTimer _play = new();
    private int _playAt;
    private readonly Dictionary<int, Bitmap> _cache = new();

    private GifEditor(string source, List<string> frames, double fps, string work)
    {
        _source = source;
        _frames = frames;
        _fps = fps;
        _work = work;
        Title = Path.GetFileName(source) + " — GIF editor";
        Icon = App.Icon;
        Width = 1000;
        Height = 700;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        foreach (var s in new[] { _scrub, _from, _to }) { s.Minimum = 0; s.Maximum = Math.Max(0, frames.Count - 1); }
        _to.Value = frames.Count - 1;
        _scrub.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty && !_play.IsEnabled) ShowFrame((int)_scrub.Value); };
        foreach (var s in new[] { _from, _to, _textSize })
            s.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) Changed(); };
        foreach (var f in FontChoices.All)
            _font.Items.Add(new ComboBoxItem { Content = f.Name, FontFamily = f.Family, FontWeight = f.Weight, Tag = f.Name });
        // the meme caption look: Anton, the closest free match to the usual meme font (as on Windows)
        _font.SelectedIndex = Math.Max(0, FontChoices.All.ToList().FindIndex(f => f.Name == "Anton"));
        foreach (var c in new[] { _loop, _speed, _size, _style, _colour, _where, _tail, _font, _repeat, _look }) c.SelectionChanged += (_, _) => Changed();
        _mirror.IsCheckedChanged += (_, _) => Changed();
        _fewer.IsCheckedChanged += (_, _) => Changed();
        _top.TextChanged += (_, _) => Changed();
        _bottom.TextChanged += (_, _) => Changed();

        _play.Tick += (_, _) =>
        {
            var order = Order();
            if (order.Count == 0) return;
            _playAt = (_playAt + 1) % order.Count;
            ShowFrame(order[_playAt]);
        };
        var play = Ui.Button("Play", () => { }, cls: null);
        play.Click += (_, _) =>
        {
            if (_play.IsEnabled) { _play.Stop(); play.Content = "Play"; }
            else { _playAt = 0; _play.Interval = TimeSpan.FromSeconds(1 / (_fps * Speed)); _play.Start(); play.Content = "Pause"; }
        };

        Control Row(string label, Control c) => new StackPanel { Spacing = 4, Children = { Ui.Label(label, "dim"), c } };
        var side = new StackPanel
        {
            Width = 280,
            Spacing = 12,
            Margin = new Thickness(16),
            Children =
            {
                Row("Start", _from), Row("End", _to),
                Row("Plays", _loop), Row("Repeats", _repeat), Row("Speed", _speed), Row("Size", _size),
                Row("Text", _style), _top, _bottom, Row("Font", _font), Row("Colour", _colour), Row("Bar", _where), Row("Tail points", _tail), Row("Text size", _textSize),
                Row("Look", _look), _mirror, _fewer,
                Ui.Label("Crop: drag over the picture. Double-click it to keep everything.", "dim"),
                _info,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    Children =
                    {
                        Ui.Button("Save GIF", () => _ = SaveAsync(".gif", copy: false), Icons.Save, "primary"),
                        Ui.Button("Copy", () => _ = SaveAsync(".gif", copy: true), Icons.Copy),
                    },
                },
                Ui.Button("Save as MP4", () => _ = SaveAsync(".mp4", copy: false)),
            },
        };
        var bottom = new DockPanel { Margin = new Thickness(16, 0, 16, 12), Children = { Docked(play, Dock.Left), _scrub } };
        play.Margin = new Thickness(0, 0, 12, 0);
        var stage = new Border { Background = new SolidColorBrush(Color.Parse("#0C0D0F")), Padding = new Thickness(16), Child = _preview };
        // crop: drag out the part to keep on the picture
        Point? dragFrom = null;
        _preview.PointerPressed += (_, e) =>
        {
            if (e.ClickCount == 2) { _crop = null; Changed(); return; }
            dragFrom = Fraction(e.GetPosition(_preview));
            e.Pointer.Capture(_preview);
        };
        _preview.PointerReleased += (_, e) =>
        {
            if (dragFrom is not { } a) return;
            e.Pointer.Capture(null);
            var b = Fraction(e.GetPosition(_preview));
            dragFrom = null;
            var r = new Rect(a, b).Normalize();
            if (r.Width > 0.05 && r.Height > 0.05) { _crop = CombineCrop(r); Changed(); }
        };
        var root = new DockPanel();
        DockPanel.SetDock(side, Dock.Right);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(side);
        root.Children.Add(bottom);
        root.Children.Add(stage);
        Content = root;

        Closed += (_, _) =>
        {
            _play.Stop();
            try { Directory.Delete(_work, true); } catch { }
        };
        Changed();
        ShowFrame(0);
    }

    private static Control Docked(Control c, Dock d) { DockPanel.SetDock(c, d); return c; }

    /// <summary>Reads the clip's frames and opens the editor.</summary>
    /// <param name="caption">Words to start the caption with (what's said in a video clip), shown in a caption bar.</param>
    public static async Task OpenAsync(string source, string? caption = null)
    {
        if (!ScreenGrabber.HasCommand("ffmpeg")) { MessageWindow.Show("The GIF editor needs ffmpeg", Converter.FfmpegHint); return; }
        var work = Path.Combine(Path.GetTempPath(), $"overskin-gif-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        // a GIF's own frame rate, capped so long clips stay quick to edit
        double fps = 15;
        var probe = await ScreenGrabber.OutputOf("ffprobe", $"-v error -select_streams v:0 -show_entries stream=avg_frame_rate -of csv=p=0 \"{source}\"");
        if (probe?.Trim().Split('/') is [var n, var d] && double.TryParse(n, CultureInfo.InvariantCulture, out var nn) && double.TryParse(d, CultureInfo.InvariantCulture, out var dd) && dd > 0)
            fps = Math.Clamp(nn / dd, 4, 30);
        var (ok, error) = await ScreenGrabber.RunAsync("ffmpeg", $"-hide_banner -loglevel error -i \"{source}\" -vf \"fps={fps.ToString(CultureInfo.InvariantCulture)},scale='min(960,iw)':-2\" -frames:v 900 \"{Path.Combine(work, "f%05d.png")}\"", 300);
        var frames = Directory.GetFiles(work, "f*.png").OrderBy(f => f).ToList();
        if (!ok || frames.Count == 0)
        {
            try { Directory.Delete(work, true); } catch { }
            MessageWindow.Show("Couldn't read the clip", error);
            return;
        }
        var editor = new GifEditor(source, frames, fps, work);
        if (!string.IsNullOrWhiteSpace(caption))
        {
            editor._style.SelectedIndex = 1;
            editor._top.Text = caption;
        }
        editor.Show();
    }

    private double Speed => _speed.SelectedIndex switch { 0 => 0.25, 1 => 0.5, 2 => 0.75, 4 => 1.5, 5 => 2, 6 => 3, _ => 1 };

    /// <summary>A point on the preview as a fraction of the shown picture (0 to 1 each way).</summary>
    private Point Fraction(Point p)
    {
        if (_preview.Source is not Bitmap shown || _preview.Bounds.Width <= 0) return default;
        double k = Math.Min(_preview.Bounds.Width / shown.Size.Width, _preview.Bounds.Height / shown.Size.Height);
        double w = shown.Size.Width * k, h = shown.Size.Height * k;
        double x0 = (_preview.Bounds.Width - w) / 2, y0 = (_preview.Bounds.Height - h) / 2;
        // the caption bar isn't part of the frame: measure against the frame's own share of the height
        double frameShare = FrameShare(shown);
        return new Point(Math.Clamp((p.X - x0) / w, 0, 1), Math.Clamp((p.Y - y0 - (BarAbove ? h * (1 - frameShare) : 0)) / (h * frameShare), 0, 1));
    }

    private bool BarAbove => _style.SelectedIndex == 1 && _where.SelectedIndex == 0 && !string.IsNullOrWhiteSpace(_top.Text);
    private double _lastFrameShare = 1;
    private double FrameShare(Bitmap shown) => _lastFrameShare;

    /// <summary>A crop dragged on an already cropped preview, as fractions of the whole frame.</summary>
    private Rect CombineCrop(Rect inShown)
    {
        var c = _crop ?? new Rect(0, 0, 1, 1);
        return new Rect(c.X + inShown.X * c.Width, c.Y + inShown.Y * c.Height, inShown.Width * c.Width, inShown.Height * c.Height);
    }

    private Color TextColour => _colour.SelectedIndex switch { 1 => Colors.Black, 2 => Color.Parse("#FACC15"), _ => Colors.White };
    private int OutputWidth => _size.SelectedIndex switch { 0 => 320, 1 => 480, 2 => 720, _ => 0 };

    private List<int> Order()
    {
        int a = (int)Math.Min(_from.Value, _to.Value), b = (int)Math.Max(_from.Value, _to.Value);
        var forward = Enumerable.Range(a, b - a + 1).ToList();
        return _loop.SelectedIndex switch
        {
            1 => Enumerable.Reverse(forward).ToList(),
            2 => forward.Concat(Enumerable.Reverse(forward).Skip(1).Take(Math.Max(0, forward.Count - 2))).ToList(),
            _ => forward,
        };
    }

    private void Changed()
    {
        _bottom.IsVisible = _style.SelectedIndex == 2;
        _top.Watermark = _style.SelectedIndex == 2 ? "Top text" : "Caption";
        _top.IsEnabled = _textSize.IsEnabled = _font.IsEnabled = _colour.IsEnabled = _style.SelectedIndex != 0;
        // the speech bubble has no words: the top of the GIF is cut out as a see-through bubble (the
        // Discord meme), so it looks like it's saying the message above it; text size sets its depth
        _top.IsVisible = _style.SelectedIndex != 3;
        _font.IsEnabled &= _style.SelectedIndex != 3;
        _tail.IsVisible = _style.SelectedIndex == 3;
        _where.IsVisible = _style.SelectedIndex == 1;
        _colour.IsEnabled = _style.SelectedIndex is 1 or 2;
        var order = Order();
        _info.Text = $"{order.Count} frames · {order.Count / (_fps * Speed):0.0} seconds" + (_fewer.IsChecked == true ? " · every other frame kept" : "");
        if (_play.IsEnabled) _play.Interval = TimeSpan.FromSeconds(1 / (_fps * Speed));
        ShowFrame((int)_scrub.Value);
    }

    private Bitmap Frame(int i)
    {
        if (!_cache.TryGetValue(i, out var b))
        {
            b = new Bitmap(_frames[i]);
            if (_cache.Count > 120) _cache.Clear();
            _cache[i] = b;
        }
        return b;
    }

    private void ShowFrame(int i)
    {
        if (_frames.Count == 0) return;
        _preview.Source = Render(Frame(Math.Clamp(i, 0, _frames.Count - 1)));
    }

    /// <summary>A frame at the output size, with the caption.</summary>
    private Bitmap Render(Bitmap frame)
    {
        var o = Options();
        var shown = RenderFrame(frame, o);
        // how much of the height is the frame (the rest is the caption bar), for dragging a crop
        double fw = o.Crop is { } c ? Math.Max(8, Math.Round(c.Width * frame.PixelSize.Width)) : frame.PixelSize.Width;
        double fh = o.Crop is { } c2 ? Math.Max(8, Math.Round(c2.Height * frame.PixelSize.Height)) : frame.PixelSize.Height;
        int w = o.Width > 0 ? (int)Math.Min(o.Width, fw) : (int)fw;
        w += w % 2;
        int h = (int)Math.Round(fh * (w / fw));
        h += h % 2;
        _lastFrameShare = Math.Clamp(h / (double)shown.PixelSize.Height, 0.05, 1);
        return shown;
    }

    /// <summary>Everything chosen, for drawing a frame (and for tests).</summary>
    public sealed record GifLook(int Width, int Style, string Top, string Bottom, double TextSize, string Font, Color Colour, bool BarAbove,
                                 int Look, bool Mirror, Rect? Crop, string Tail = "Left");

    private GifLook Options() => new(OutputWidth, _style.SelectedIndex, _top.Text ?? "", _bottom.Text ?? "", _textSize.Value,
        (_font.SelectedItem as ComboBoxItem)?.Tag as string ?? "Anton", TextColour, _where.SelectedIndex == 0, _look.SelectedIndex, _mirror.IsChecked == true, _crop,
        _tail.SelectedItem as string ?? "Left");

    /// <summary>A frame at the output size: cropped, mirrored, coloured, with its caption (as on Windows).</summary>
    public static Bitmap RenderFrame(Bitmap frame, GifLook o)
    {
        var src = new Rect(0, 0, frame.PixelSize.Width, frame.PixelSize.Height);
        if (o.Crop is { } c)
            src = new Rect(Math.Round(c.X * src.Width), Math.Round(c.Y * src.Height), Math.Max(8, Math.Round(c.Width * src.Width)), Math.Max(8, Math.Round(c.Height * src.Height)));
        int w = o.Width > 0 ? (int)Math.Min(o.Width, src.Width) : (int)src.Width;
        w += w % 2;
        int h = (int)Math.Round(src.Height * (w / src.Width));
        h += h % 2;
        double size = o.TextSize * w / 480.0;
        var typeface = FontChoices.Face(o.Font);
        FormattedText Text(string s, IBrush brush, double max) =>
            new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush) { MaxTextWidth = max, TextAlignment = TextAlignment.Center };

        // the caption bar: white with black text, black with white, or yellow with black
        int bar = 0;
        FormattedText? caption = null;
        var barColour = o.Colour;
        IBrush barText = barColour == Colors.Black ? Brushes.White : Brushes.Black;
        if (o.Style == 1 && !string.IsNullOrWhiteSpace(o.Top))
        {
            caption = Text(o.Top, barText, w - size);
            bar = (int)Math.Ceiling(caption.Height + size * 0.8);
            bar += bar % 2;
        }
        int top = caption != null && o.BarAbove ? bar : 0;
        var picture = Imaging.Render(new PixelSize(w, h + bar), ctx =>
        {
            var place = new Rect(0, top, w, h);
            // a speech bubble: the picture is cut away inside it, so it's see-through there
            using var cut = o.Style == 3 ? ctx.PushGeometryClip(new CombinedGeometry(GeometryCombineMode.Exclude,
                new RectangleGeometry(place), Bubble(w, h, o.Tail, o.TextSize / 32.0))) : (IDisposable?)null;
            using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
            {
                if (o.Mirror)
                {
                    using (ctx.PushTransform(Matrix.CreateScale(-1, 1) * Matrix.CreateTranslation(w, 0)))
                        ctx.DrawImage(frame, src, place);
                }
                else ctx.DrawImage(frame, src, place);
            }
            if (caption != null)
            {
                ctx.FillRectangle(new SolidColorBrush(barColour), new Rect(0, o.BarAbove ? 0 : h, w, bar));
                ctx.DrawText(caption, new Point(size / 2, (o.BarAbove ? 0 : h) + (bar - caption.Height) / 2));
            }
            if (o.Style == 2)
            {
                // meme text: the chosen colour with an outline of the opposite
                IBrush fill = new SolidColorBrush(o.Colour);
                IBrush edge = o.Colour == Colors.Black ? Brushes.White : Brushes.Black;
                void Meme(string s, bool atTop)
                {
                    if (string.IsNullOrWhiteSpace(s)) return;
                    var t = Text(s.ToUpperInvariant(), fill, w - size);
                    var at = new Point(size / 2, atTop ? size * 0.3 : h - t.Height - size * 0.3);
                    if (t.BuildGeometry(at) is { } g)
                        ctx.DrawGeometry(fill, new Pen(edge, Math.Max(2, size / 9)) { LineJoin = PenLineJoin.Round }, g);
                }
                Meme(o.Top, atTop: true);
                Meme(o.Bottom, atTop: false);
            }
        });
        if (o.Look == 0) return picture;
        // colour looks, on the picture's pixels: grey, sepia, or more vivid
        var px = Imaging.Pixels(picture, out _);
        for (int i = 0; i < px.Length; i += 4)
        {
            double b = px[i], gg = px[i + 1], r = px[i + 2];
            switch (o.Look)
            {
                case 1:
                    px[i] = px[i + 1] = px[i + 2] = (byte)Math.Round(0.299 * r + 0.587 * gg + 0.114 * b);
                    break;
                case 2:
                    px[i + 2] = (byte)Math.Min(255, 0.393 * r + 0.769 * gg + 0.189 * b);
                    px[i + 1] = (byte)Math.Min(255, 0.349 * r + 0.686 * gg + 0.168 * b);
                    px[i] = (byte)Math.Min(255, 0.272 * r + 0.534 * gg + 0.131 * b);
                    break;
                case 3:
                    double grey = 0.299 * r + 0.587 * gg + 0.114 * b;
                    px[i + 2] = (byte)Math.Clamp(grey + (r - grey) * 1.45, 0, 255);
                    px[i + 1] = (byte)Math.Clamp(grey + (gg - grey) * 1.45, 0, 255);
                    px[i] = (byte)Math.Clamp(grey + (b - grey) * 1.45, 0, 255);
                    break;
            }
        }
        return Imaging.FromPixels(px, picture.PixelSize);
    }

    /// <summary>
    /// The speech bubble across the top of a <paramref name="w"/> by <paramref name="h"/> picture (as on
    /// Windows): a wide oval running off the top edge, with a tail curving down to a point.
    /// </summary>
    public static Geometry Bubble(double w, double h, string tail, double size = 1)
    {
        double depth = h * Math.Clamp(0.2 * size, 0.1, 0.38);
        double cy = depth * 0.08, rx = w * 0.62;
        var oval = new EllipseGeometry(new Rect(w / 2 - rx, cy - depth, rx * 2, depth * 2));
        double baseX = tail switch { "Right" => w * 0.68, "Middle" => w * 0.5, _ => w * 0.32 };
        double tipX = tail switch { "Right" => w * 0.8, "Middle" => w * 0.5, _ => w * 0.2 };
        double baseY = cy + depth * Math.Sqrt(Math.Max(0, 1 - Math.Pow((baseX - w / 2) / rx, 2))) - 2;
        double half = w * 0.07, tipY = baseY + depth * 0.95;
        var tailShape = new StreamGeometry();
        using (var g = tailShape.Open())
        {
            g.BeginFigure(new Point(baseX - half, baseY - depth * 0.1), true);
            g.QuadraticBezierTo(new Point((baseX - half + tipX) / 2 - (tail == "Right" ? -half * 0.2 : half * 0.2), (baseY + tipY) / 2), new Point(tipX, tipY));
            g.QuadraticBezierTo(new Point((baseX + half + tipX) / 2, (baseY + tipY) / 2), new Point(baseX + half, baseY - depth * 0.1));
            g.EndFigure(true);
        }
        return new CombinedGeometry(GeometryCombineMode.Union, oval, tailShape);
    }

    /// <summary>
    /// ffmpeg's command for numbered PNG frames into a GIF (with a see-through colour when it has a
    /// speech bubble) or an MP4 (by the target's extension).
    /// </summary>
    public static string FfmpegArgs(string frames, string rate, string loop, string target, bool seeThrough)
    {
        var input = $"-framerate {rate} -i \"{frames}\"";
        return "-hide_banner -loglevel error -y " + (Path.GetExtension(target).Equals(".gif", StringComparison.OrdinalIgnoreCase)
            ? $"{input} -vf \"split[a][b];[a]palettegen=stats_mode=diff:reserve_transparent={(seeThrough ? 1 : 0)}[p];[b][p]paletteuse=dither=bayer:bayer_scale=4:alpha_threshold=128\" -loop {loop} \"{target}\""
            : $"{input} -c:v libx264 -pix_fmt yuv420p -movflags +faststart \"{target}\"");
    }

    private async Task SaveAsync(string extension, bool copy)
    {
        _play.Stop();
        var order = Order();
        // fewer frames: every other one, each shown twice as long
        bool fewer = _fewer.IsChecked == true;
        if (fewer) order = order.Where((_, k) => k % 2 == 0).ToList();
        if (order.Count == 0) return;
        var outDir = Path.Combine(_work, "out");
        bool seeThrough = _style.SelectedIndex == 3;
        try
        {
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
            Directory.CreateDirectory(outDir);
            for (int k = 0; k < order.Count; k++)
            {
                if (k % 10 == 0) { _info.Text = $"Preparing frame {k + 1} of {order.Count}…"; await Task.Yield(); }
                var shown = Render(Frame(order[k]));
                // a video can't be see-through: white inside the bubble
                if (seeThrough && extension != ".gif")
                {
                    var under = shown;
                    shown = Imaging.Render(under.PixelSize, ctx => { ctx.FillRectangle(Brushes.White, new Rect(0, 0, under.PixelSize.Width, under.PixelSize.Height)); ctx.DrawImage(under, new Rect(0, 0, under.PixelSize.Width, under.PixelSize.Height)); });
                }
                shown.Save(Path.Combine(outDir, $"o{k:00000}.png"));
            }
            var name = Path.GetFileNameWithoutExtension(_source);
            var target = Converter.Beside(Path.Combine(App.Settings.SaveFolder, name + " edited" + extension), extension);
            Directory.CreateDirectory(App.Settings.SaveFolder);
            var rate = (_fps * Speed / (fewer ? 2 : 1)).ToString("0.###", CultureInfo.InvariantCulture);
            // ffmpeg's -loop: 0 forever, -1 play once, 2 three times
            var loop = _repeat.SelectedIndex switch { 1 => "-1", 2 => "2", _ => "0" };
            _info.Text = "Making the file…";
            var (ok, error) = await ScreenGrabber.RunAsync("ffmpeg", FfmpegArgs(Path.Combine(outDir, "o%05d.png"), rate, loop, target, seeThrough), 600);
            if (!ok) throw new InvalidOperationException(error);
            App.Current.NotifyLibraryChanged();
            if (copy) await Desktop.CopyFilesAsync(new[] { target }, this);
            _info.Text = (copy ? "Copied and saved as " : "Saved as ") + Path.GetFileName(target);
        }
        catch (Exception ex) { _info.Text = "Couldn't make it: " + ex.Message; }
    }
}
