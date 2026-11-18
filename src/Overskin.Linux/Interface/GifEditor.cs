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
    private readonly ComboBox _speed = new() { ItemsSource = new[] { "0.5×", "0.75×", "1×", "1.5×", "2×", "3×" }, SelectedIndex = 2 };
    private readonly ComboBox _size = new() { ItemsSource = new[] { "320 wide", "480 wide", "720 wide", "As it is" }, SelectedIndex = 2 };
    private readonly ComboBox _style = new() { ItemsSource = new[] { "No text", "Caption below", "Meme (top and bottom)" }, SelectedIndex = 0 };
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
        foreach (var c in new[] { _loop, _speed, _size, _style }) c.SelectionChanged += (_, _) => Changed();
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
                Row("Plays", _loop), Row("Speed", _speed), Row("Size", _size),
                Row("Text", _style), _top, _bottom, Row("Text size", _textSize),
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
    public static async Task OpenAsync(string source)
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
        new GifEditor(source, frames, fps, work).Show();
    }

    private double Speed => _speed.SelectedIndex switch { 0 => 0.5, 1 => 0.75, 3 => 1.5, 4 => 2, 5 => 3, _ => 1 };
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
        _top.IsEnabled = _textSize.IsEnabled = _style.SelectedIndex != 0;
        var order = Order();
        _info.Text = $"{order.Count} frames · {order.Count / (_fps * Speed):0.0} seconds";
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
        int w = OutputWidth > 0 ? Math.Min(OutputWidth, frame.PixelSize.Width) : frame.PixelSize.Width;
        int h = (int)Math.Round(frame.PixelSize.Height * (w / (double)frame.PixelSize.Width));
        h += h % 2;
        double size = _textSize.Value * w / 480.0;
        var typeface = new Typeface("Inter", FontStyle.Normal, FontWeight.Black);
        FormattedText Text(string s, IBrush brush, double max) =>
            new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush) { MaxTextWidth = max, TextAlignment = TextAlignment.Center };

        int bar = 0;
        FormattedText? caption = null;
        if (_style.SelectedIndex == 1 && !string.IsNullOrWhiteSpace(_top.Text))
        {
            caption = Text(_top.Text!, Brushes.Black, w - size);
            bar = (int)Math.Ceiling(caption.Height + size * 0.8);
            bar += bar % 2;
        }
        return Imaging.Render(new PixelSize(w, h + bar), ctx =>
        {
            using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
                ctx.DrawImage(frame, new Rect(frame.Size), new Rect(0, 0, w, h));
            if (caption != null)
            {
                ctx.FillRectangle(Brushes.White, new Rect(0, h, w, bar));
                ctx.DrawText(caption, new Point(size / 2, h + (bar - caption.Height) / 2));
            }
            if (_style.SelectedIndex == 2)
            {
                void Meme(string? s, bool top)
                {
                    if (string.IsNullOrWhiteSpace(s)) return;
                    var t = Text(s.ToUpperInvariant(), Brushes.White, w - size);
                    var at = new Point(size / 2, top ? size * 0.3 : h - t.Height - size * 0.3);
                    if (t.BuildGeometry(at) is { } g)
                        ctx.DrawGeometry(Brushes.White, new Pen(Brushes.Black, Math.Max(2, size / 9)) { LineJoin = PenLineJoin.Round }, g);
                }
                Meme(_top.Text, top: true);
                Meme(_bottom.Text, top: false);
            }
        });
    }

    private async Task SaveAsync(string extension, bool copy)
    {
        _play.Stop();
        var order = Order();
        if (order.Count == 0) return;
        var outDir = Path.Combine(_work, "out");
        try
        {
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
            Directory.CreateDirectory(outDir);
            for (int k = 0; k < order.Count; k++)
            {
                if (k % 10 == 0) { _info.Text = $"Preparing frame {k + 1} of {order.Count}…"; await Task.Yield(); }
                Render(Frame(order[k])).Save(Path.Combine(outDir, $"o{k:00000}.png"));
            }
            var name = Path.GetFileNameWithoutExtension(_source);
            var target = Converter.Beside(Path.Combine(App.Settings.SaveFolder, name + " edited" + extension), extension);
            Directory.CreateDirectory(App.Settings.SaveFolder);
            var rate = (_fps * Speed).ToString("0.###", CultureInfo.InvariantCulture);
            var input = $"-framerate {rate} -i \"{Path.Combine(outDir, "o%05d.png")}\"";
            var args = extension == ".gif"
                ? $"{input} -vf \"split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4\" -loop 0 \"{target}\""
                : $"{input} -c:v libx264 -pix_fmt yuv420p -movflags +faststart \"{target}\"";
            _info.Text = "Making the file…";
            var (ok, error) = await ScreenGrabber.RunAsync("ffmpeg", "-hide_banner -loglevel error -y " + args, 600);
            if (!ok) throw new InvalidOperationException(error);
            App.Current.NotifyLibraryChanged();
            if (copy) await Desktop.CopyFilesAsync(new[] { target }, this);
            _info.Text = (copy ? "Copied and saved as " : "Saved as ") + Path.GetFileName(target);
        }
        catch (Exception ex) { _info.Text = "Couldn't make it: " + ex.Message; }
    }
}
