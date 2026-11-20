using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;

namespace Overskin;

/// <summary>What the viewer asks the main window to do.</summary>
public sealed class ViewerHost
{
    public required Action<BitmapSource, string?, string> Edit { get; init; }
    public required Action<string> EditVideo { get; init; }
    public required Action<string> EditGif { get; init; }
    public required Action<BitmapSource, string, string?> Pin { get; init; }
    public required Action<string> ShowInFolder { get; init; }
    /// <summary>A file was rotated or deleted: the Library should look again.</summary>
    public required Action Changed { get; init; }
}

/// <summary>
/// A quick, smooth look at your captures: the arrow keys go through them, the wheel zooms in
/// towards the mouse (gliding, not jumping), drag to look around, double-click for actual size.
/// GIFs play and videos play too. From here: copy, rotate, pin, open in the editor, show in the
/// folder, or move to the Recycle Bin.
/// </summary>
public sealed class ViewerWindow : Window
{
    private readonly List<string?> _paths;
    private readonly BitmapSource? _single;
    private readonly ViewerHost _host;
    private int _index;

    private readonly Grid _stage = new() { ClipToBounds = true };
    private Border? _frame;
    private readonly Image _image = new() { Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly Canvas _canvas = new();
    private readonly ScaleTransform _zoom = new();
    private readonly TranslateTransform _pan = new();
    private readonly MediaElement _video = new() { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed, Margin = new Thickness(16, 16, 16, 90) };
    private readonly TextBlock _title = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _details = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    private readonly TextBlock _position = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, MinWidth = 64, TextAlignment = TextAlignment.Center };
    private readonly TextBlock _zoomText = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, MinWidth = 46, TextAlignment = TextAlignment.Center };
    private readonly TextBlock _message = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
    private readonly Button _rotateLeft, _rotateRight, _edit, _pin, _copy;
    private readonly DispatcherTimer _gifTimer = new();
    private readonly Dictionary<string, BitmapSource> _cache = new();

    private BitmapSource? _shown;
    private GifClip? _gif;
    private int _gifFrame;
    private double _scale = 1, _x, _y;
    private bool _fit = true, _panning;
    private Point _panFrom;
    private int _loadToken;

    /// <param name="paths">The captures to go through (files), newest first, or one null for <paramref name="single"/>.</param>
    public ViewerWindow(IReadOnlyList<string?> paths, int index, ViewerHost host, BitmapSource? single = null)
    {
        _paths = paths.ToList();
        _index = Math.Clamp(index, 0, Math.Max(0, _paths.Count - 1));
        _single = single;
        _host = host;

        Title = "Overskin";
        Width = 1100;
        Height = 760;
        MinWidth = 520;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.CanResize;
        FontFamily = (FontFamily)FindResource("UiFont");
        SetResourceReference(ForegroundProperty, "Text");
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 44, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0), UseAeroCaptionButtons = false });

        // the picture: moved and scaled as one, so zooming is a smooth transform, not a re-layout
        _image.RenderTransform = new TransformGroup { Children = { _zoom, _pan } };
        _canvas.Children.Add(_image);
        _stage.Children.Add(_canvas);
        _stage.Children.Add(_video);
        _message.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        _stage.Children.Add(_message);
        _stage.Background = Brushes.Transparent;
        _stage.MouseWheel += Stage_Wheel;
        _stage.MouseLeftButtonDown += Stage_Down;
        _stage.MouseMove += Stage_Move;
        _stage.MouseLeftButtonUp += Stage_Up;
        _stage.SizeChanged += (_, _) => { if (_fit) FitNow(animate: false); else Apply(animate: false); };

        // title bar
        var bar = new Grid { Height = 44 };
        var left = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(Glyph("IcImage", 16, accent: true));
        _title.Margin = new Thickness(10, 0, 0, 0);
        _title.MaxWidth = 460;
        left.Children.Add(_title);
        _details.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        left.Children.Add(_details);
        bar.Children.Add(left);
        var caption = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        caption.Children.Add(CaptionButton("IcMinus", "Minimize", () => WindowState = WindowState.Minimized));
        caption.Children.Add(CaptionButton("IcSquare", "Maximize", () => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized));
        caption.Children.Add(CaptionButton("IcX", "Close  (Esc)", Close, close: true));
        bar.Children.Add(caption);

        // the floating tool bar along the bottom
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        tools.Children.Add(ToolButton("IcChevronLeft", "Previous  (Left)", () => Go(-1)));
        _position.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        tools.Children.Add(_position);
        tools.Children.Add(ToolButton("IcChevronRight", "Next  (Right)", () => Go(1)));
        tools.Children.Add(Separator());
        tools.Children.Add(ToolButton("IcZoomOut", "Zoom out  (-)", () => ZoomBy(1 / 1.4, Centre())));
        _zoomText.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        tools.Children.Add(_zoomText);
        tools.Children.Add(ToolButton("IcZoomIn", "Zoom in  (+)", () => ZoomBy(1.4, Centre())));
        tools.Children.Add(ToolButton("IcMaximize", "Fit  (0)   ·   Actual size  (1)", () => { if (_fit) ActualSize(Centre()); else FitNow(animate: true); }));
        tools.Children.Add(Separator());
        _rotateLeft = ToolButton("IcRotateCcw", "Rotate left  (Shift+R)", () => _ = RotateAsync(-90));
        _rotateRight = ToolButton("IcRotateCw", "Rotate right  (R)", () => _ = RotateAsync(90));
        tools.Children.Add(_rotateLeft);
        tools.Children.Add(_rotateRight);
        _copy = ToolButton("IcCopy", "Copy  (Ctrl+C)", Copy);
        tools.Children.Add(_copy);
        _pin = ToolButton("IcPin", "Pin it on top  (P)", Pin);
        tools.Children.Add(_pin);
        tools.Children.Add(ToolButton("IcFolderOpen", "Show in folder", () => { if (CurrentPath is { } p) _host.ShowInFolder(p); }));
        tools.Children.Add(ToolButton("IcTrash", "Move to the Recycle Bin  (Delete)", Delete));
        tools.Children.Add(Separator());
        _edit = new Button { Height = 32, Margin = new Thickness(2, 0, 0, 0), Padding = new Thickness(12, 0, 12, 0), ToolTip = "Open in the editor  (E)" };
        _edit.SetResourceReference(StyleProperty, "BtnAccent");
        var editLabel = new StackPanel { Orientation = Orientation.Horizontal };
        editLabel.Children.Add(Glyph("IcPencil", 14));
        editLabel.Children.Add(new TextBlock { Text = "Edit", Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        _edit.Content = editLabel;
        _edit.Click += (_, _) => Edit();
        tools.Children.Add(_edit);
        var pill = new Border
        {
            Child = tools, Padding = new Thickness(8, 6, 8, 6), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 18),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Direction = 270, Opacity = 0.4 },
        };
        pill.SetResourceReference(Border.BackgroundProperty, "Panel");
        pill.SetResourceReference(Border.BorderBrushProperty, "LineStrong");
        pill.SetResourceReference(Border.CornerRadiusProperty, "RadiusCard");

        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
        body.RowDefinitions.Add(new RowDefinition());
        body.Children.Add(bar);
        Grid.SetRow(_stage, 1);
        body.Children.Add(_stage);
        Grid.SetRow(pill, 1);
        body.Children.Add(pill);
        // the same background and rounded corners as Overskin's other windows
        var frame = new Border { Child = body, BorderThickness = new Thickness(1) };
        frame.SetResourceReference(Border.BorderBrushProperty, "Line");
        frame.SetResourceReference(Border.BackgroundProperty, "Bg");
        frame.SetResourceReference(Border.CornerRadiusProperty, "RadiusWindow");
        _frame = frame;
        Content = frame;

        _gifTimer.Tick += (_, _) => NextGifFrame();
        _video.MediaEnded += (_, _) => { _video.Position = TimeSpan.Zero; _video.Play(); };
        Loaded += (_, _) => Show(_index);
        Closed += (_, _) => { _gifTimer.Stop(); _video.Close(); };
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (_frame == null) return;
        // square and inset when it fills the screen, rounded otherwise
        _frame.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        if (WindowState == WindowState.Maximized) _frame.CornerRadius = new CornerRadius(0);
        else _frame.SetResourceReference(Border.CornerRadiusProperty, "RadiusWindow");
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        int round = 2;
        NativeMethods.DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 33, ref round, sizeof(int));
    }

    // ---- building blocks -----------------------------------------------------------------

    private Overskin.Icon Glyph(string key, double size, bool accent = false)
    {
        var icon = new Overskin.Icon { Data = (Geometry)FindResource(key), Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        if (accent) icon.SetResourceReference(Overskin.Icon.ForegroundProperty, "Accent");
        return icon;
    }

    private Button ToolButton(string icon, string tip, Action act)
    {
        var b = new Button { Width = 34, Height = 32, ToolTip = tip, Content = Glyph(icon, 16), Margin = new Thickness(1, 0, 1, 0) };
        b.SetResourceReference(StyleProperty, "BtnIcon");
        b.Click += (_, _) => act();
        return b;
    }

    private Button CaptionButton(string icon, string tip, Action act, bool close = false)
    {
        var b = new Button { ToolTip = tip, Content = Glyph(icon, icon == "IcSquare" ? 12 : 14) };
        b.SetResourceReference(StyleProperty, close ? "BtnCaptionClose" : "BtnCaption");
        WindowChrome.SetIsHitTestVisibleInChrome(b, true);
        b.Click += (_, _) => act();
        return b;
    }

    private Border Separator()
    {
        var s = new Border { Width = 1, Height = 20, Margin = new Thickness(6, 0, 6, 0) };
        s.SetResourceReference(Border.BackgroundProperty, "Line");
        return s;
    }

    // ---- what's shown --------------------------------------------------------------------

    private string? CurrentPath => _index < _paths.Count ? _paths[_index] : null;

    private static bool IsVideoFile(string p) => Path.GetExtension(p).ToLowerInvariant() is ".mp4" or ".mov" or ".wmv" or ".avi" or ".mkv" or ".webm";
    private static bool IsGifFile(string p) => Path.GetExtension(p).Equals(".gif", StringComparison.OrdinalIgnoreCase);

    private async void Show(int index)
    {
        if (_paths.Count == 0) { Close(); return; }
        _index = (index % _paths.Count + _paths.Count) % _paths.Count;
        int token = ++_loadToken;
        _gifTimer.Stop();
        _gif = null;
        _video.Stop();
        _video.Visibility = Visibility.Collapsed;
        _message.Text = "";
        _position.Text = _paths.Count > 1 ? $"{_index + 1} / {_paths.Count}" : "";
        string? path = CurrentPath;
        _title.Text = path == null ? "New capture" : Path.GetFileName(path);

        if (path != null && IsVideoFile(path))
        {
            _image.Source = null;
            _shown = null;
            _video.Source = new Uri(Path.GetFullPath(path));
            _video.Visibility = Visibility.Visible;
            _video.Play();
            _details.Text = Describe(path, null);
            SetTools(video: true);
            _zoomText.Text = "";
            return;
        }

        BitmapSource? picture = null;
        if (path == null) picture = _single;
        else if (!File.Exists(path))
        {
            _image.Source = null;
            _message.Text = "This file has been moved or deleted.";
            _details.Text = "";
            SetTools(video: false);
            return;
        }
        else if (IsGifFile(path))
        {
            try
            {
                var clip = await Task.Run(() => GifReader.Read(path));
                if (token != _loadToken) return;
                if (clip.Count > 0)
                {
                    _gif = clip;
                    _gifFrame = 0;
                    picture = clip.FrameBitmap(0);
                    if (clip.Count > 1) StartGif();
                }
            }
            catch { }
        }
        else picture = await LoadAsync(path);
        if (token != _loadToken) return;
        if (picture == null)
        {
            _image.Source = null;
            _message.Text = "This picture can't be shown.";
            SetTools(video: false);
            return;
        }
        bool same = _shown != null && _shown.PixelWidth == picture.PixelWidth && _shown.PixelHeight == picture.PixelHeight;
        _shown = picture;
        _image.Source = picture;
        _image.Width = picture.PixelWidth;
        _image.Height = picture.PixelHeight;
        _details.Text = Describe(path, picture);
        SetTools(video: false);
        if (!same || _fit) FitNow(animate: false);
        // a quick fade, so going through them feels smooth rather than flickery
        _image.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(140)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        Preload();
    }

    private void SetTools(bool video)
    {
        bool gif = _gif != null;
        _rotateLeft.IsEnabled = _rotateRight.IsEnabled = !video && !gif && _shown != null;
        _pin.IsEnabled = !video && _shown != null;
        _copy.IsEnabled = video || _shown != null || CurrentPath != null;
        _edit.ToolTip = video ? "Trim, cut and add text to the video  (E)" : gif ? "Open in the GIF editor  (E)" : "Open in the editor  (E)";
    }

    private static string Describe(string? path, BitmapSource? picture)
    {
        var parts = new List<string>();
        if (picture != null) parts.Add($"{picture.PixelWidth} × {picture.PixelHeight}");
        if (path != null && File.Exists(path))
        {
            var info = new FileInfo(path);
            parts.Add(info.Length >= 1 << 20 ? $"{info.Length / 1048576.0:0.0} MB" : $"{Math.Max(1, info.Length / 1024)} KB");
            parts.Add(info.LastWriteTime.ToString("d MMM yyyy, HH:mm"));
        }
        return string.Join("  ·  ", parts);
    }

    private async Task<BitmapSource?> LoadAsync(string path)
    {
        if (_cache.TryGetValue(path, out var cached)) return cached;
        try
        {
            var picture = await Task.Run(() => ImageLoader.LoadFile(path));
            _cache[path] = picture;
            if (_cache.Count > 8) _cache.Remove(_cache.Keys.First(k => k != path));
            return picture;
        }
        catch { return null; }
    }

    /// <summary>The pictures either side, loaded ahead so the arrow keys are instant.</summary>
    private void Preload()
    {
        foreach (int d in new[] { 1, -1 })
        {
            if (_paths.Count < 2) return;
            string? p = _paths[(_index + d + _paths.Count) % _paths.Count];
            if (p != null && !IsVideoFile(p) && !IsGifFile(p) && !_cache.ContainsKey(p) && File.Exists(p))
                _ = LoadAsync(p);
        }
    }

    private void Go(int step)
    {
        if (_paths.Count < 2) return;
        _fit = true;
        Show(_index + step);
    }

    // ---- GIFs ----------------------------------------------------------------------------

    private void StartGif()
    {
        _gifTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(20, _gif!.DelaysMs[0]));
        _gifTimer.Start();
    }

    private void NextGifFrame()
    {
        if (_gif == null || _gif.Count < 2) { _gifTimer.Stop(); return; }
        _gifFrame = (_gifFrame + 1) % _gif.Count;
        _image.Source = _gif.FrameBitmap(_gifFrame);
        _gifTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(20, _gif.DelaysMs[_gifFrame]));
    }

    // ---- zoom and pan --------------------------------------------------------------------

    private Point Centre() => new(_stage.ActualWidth / 2, _stage.ActualHeight / 2);

    /// <summary>The whole picture in view, never blown up past its real size.</summary>
    private void FitNow(bool animate)
    {
        if (_shown == null || _stage.ActualWidth <= 0) return;
        double room = Math.Max(1, _stage.ActualHeight - 90);
        double s = Math.Min(1, Math.Min((_stage.ActualWidth - 32) / _shown.PixelWidth, (room - 16) / _shown.PixelHeight));
        _scale = Math.Max(0.02, s);
        _x = (_stage.ActualWidth - _shown.PixelWidth * _scale) / 2;
        _y = (room - _shown.PixelHeight * _scale) / 2 + 8;
        _fit = true;
        Apply(animate);
    }

    private void ActualSize(Point around) => ZoomTo(1, around);

    private void ZoomBy(double factor, Point around) => ZoomTo(_scale * factor, around);

    private void ZoomTo(double scale, Point around)
    {
        if (_shown == null) return;
        scale = Math.Clamp(scale, 0.05, 32);
        // keep the point under the mouse where it is
        double ix = (around.X - _x) / _scale, iy = (around.Y - _y) / _scale;
        _scale = scale;
        _x = around.X - ix * scale;
        _y = around.Y - iy * scale;
        _fit = false;
        Apply(animate: true);
    }

    private void Apply(bool animate)
    {
        // sharp pixels when looking closely, smooth when shrunk
        RenderOptions.SetBitmapScalingMode(_image, _scale >= 2 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        _zoomText.Text = _shown == null ? "" : $"{_scale * 100:0}%";
        var time = TimeSpan.FromMilliseconds(animate ? 160 : 0);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        void To(Animatable target, DependencyProperty p, double value)
        {
            if (!animate) { target.BeginAnimation(p, null); target.SetValue(p, value); return; }
            target.BeginAnimation(p, new DoubleAnimation(value, time) { EasingFunction = ease }, HandoffBehavior.SnapshotAndReplace);
        }
        To(_zoom, ScaleTransform.ScaleXProperty, _scale);
        To(_zoom, ScaleTransform.ScaleYProperty, _scale);
        To(_pan, TranslateTransform.XProperty, _x);
        To(_pan, TranslateTransform.YProperty, _y);
    }

    private void Stage_Wheel(object sender, MouseWheelEventArgs e)
    {
        if (_shown == null) return;
        // the wheel zooms; Ctrl isn't needed
        ZoomBy(e.Delta > 0 ? 1.25 : 1 / 1.25, e.GetPosition(_stage));
        e.Handled = true;
    }

    private void Stage_Down(object sender, MouseButtonEventArgs e)
    {
        if (_video.Visibility == Visibility.Visible)
        {
            if (_video.CanPause) { if (_playing) _video.Pause(); else _video.Play(); _playing = !_playing; }
            return;
        }
        if (e.ClickCount == 2)
        {
            if (_fit) ActualSize(e.GetPosition(_stage));
            else FitNow(animate: true);
            return;
        }
        _panning = true;
        _panFrom = e.GetPosition(_stage);
        _stage.CaptureMouse();
        _stage.Cursor = Cursors.SizeAll;
    }

    private bool _playing = true;

    private void Stage_Move(object sender, MouseEventArgs e)
    {
        if (!_panning) return;
        var p = e.GetPosition(_stage);
        _x += p.X - _panFrom.X;
        _y += p.Y - _panFrom.Y;
        _panFrom = p;
        _fit = false;
        Apply(animate: false);
    }

    private void Stage_Up(object sender, MouseButtonEventArgs e)
    {
        if (!_panning) return;
        _panning = false;
        _stage.ReleaseMouseCapture();
        _stage.Cursor = null;
    }

    // ---- actions -------------------------------------------------------------------------

    private void Copy()
    {
        string? path = CurrentPath;
        bool ok = path != null
            ? ClipboardFiles.Copy(new[] { path }, ClipboardFiles.IsStill(path) ? _shown : null)
            : _shown != null && TrySetImage(_shown);
        Toast.Show(ok ? "Copied." : "The clipboard is busy. Try again.", !ok);
    }

    private static bool TrySetImage(BitmapSource image)
    {
        try { Clipboard.SetImage(image); return true; } catch { return false; }
    }

    private void Pin()
    {
        if (_shown == null) return;
        _host.Pin(_shown, CurrentPath != null ? Path.GetFileName(CurrentPath) : "Capture", CurrentPath);
    }

    private void Edit()
    {
        string? path = CurrentPath;
        if (path != null && IsVideoFile(path)) { _video.Pause(); _host.EditVideo(path); return; }
        if (path != null && IsGifFile(path)) { _host.EditGif(path); return; }
        if (_shown != null) _host.Edit(_gif != null ? _gif.FrameBitmap(0) : _shown, path, path != null ? Path.GetFileName(path) : "Capture");
    }

    /// <summary>Turns the picture a quarter and saves it back to its file (lossless for PNG).</summary>
    private async Task RotateAsync(int degrees)
    {
        if (_shown == null || _gif != null) return;
        var turned = new TransformedBitmap(_shown, new RotateTransform(degrees));
        var result = ImageLoader.Normalize(turned);
        string? path = CurrentPath;
        if (path != null)
        {
            try
            {
                await Task.Run(() =>
                {
                    BitmapEncoder encoder = Path.GetExtension(path).ToLowerInvariant() switch
                    {
                        ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = 95 },
                        ".bmp" => new BmpBitmapEncoder(),
                        ".tif" or ".tiff" => new TiffBitmapEncoder(),
                        _ => new PngBitmapEncoder(),
                    };
                    encoder.Frames.Add(BitmapFrame.Create(result));
                    string temp = path + ".part";
                    using (var f = File.Create(temp)) encoder.Save(f);
                    File.Move(temp, path, overwrite: true);
                });
                _cache[path] = result;
                _host.Changed();
            }
            catch (Exception ex)
            {
                Toast.Show("Couldn't save the turned picture: " + ex.Message, true);
                return;
            }
        }
        _shown = result;
        _image.Source = result;
        _image.Width = result.PixelWidth;
        _image.Height = result.PixelHeight;
        _details.Text = Describe(path, result);
        FitNow(animate: true);
    }

    private void Delete()
    {
        string? path = CurrentPath;
        if (path == null || !File.Exists(path)) return;
        try
        {
            if (IsVideoFile(path)) { _video.Stop(); _video.Close(); }
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            Toast.Show("Couldn't delete it: " + ex.Message, true);
            return;
        }
        Toast.Show("Moved to the Recycle Bin.");
        _cache.Remove(path);
        _paths.RemoveAt(_index);
        _host.Changed();
        if (_paths.Count == 0) { Close(); return; }
        Show(Math.Min(_index, _paths.Count - 1));
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        Action? act = e.Key switch
        {
            Key.Escape => Close,
            Key.Left or Key.PageUp => () => Go(-1),
            Key.Right or Key.PageDown => () => Go(1),
            Key.Home => () => { _fit = true; Show(0); },
            Key.End => () => { _fit = true; Show(_paths.Count - 1); },
            Key.OemPlus or Key.Add => () => ZoomBy(1.4, Centre()),
            Key.OemMinus or Key.Subtract => () => ZoomBy(1 / 1.4, Centre()),
            Key.D0 or Key.NumPad0 => () => FitNow(animate: true),
            Key.D1 or Key.NumPad1 => () => ActualSize(Centre()),
            Key.C when ctrl => Copy,
            Key.R => () => _ = RotateAsync(shift ? -90 : 90),
            Key.E => Edit,
            Key.P => Pin,
            Key.Delete => Delete,
            Key.Space when _video.Visibility == Visibility.Visible => () => { if (_playing) _video.Pause(); else _video.Play(); _playing = !_playing; },
            _ => null,
        };
        if (act == null) { base.OnPreviewKeyDown(e); return; }
        e.Handled = true;
        act();
    }

#if DEBUG
    internal int IndexForTest => _index;
    internal double ScaleForTest => _scale;
    internal string TitleForTest => _title.Text;
    internal void GoForTest(int step) => Go(step);
    internal void ZoomForTest(double factor) => ZoomBy(factor, Centre());
    internal Task RotateForTest(int degrees) => RotateAsync(degrees);
    internal BitmapSource? ShownForTest => _shown;
#endif
}
