using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Overskin.Linux.Core;
using Overskin.Linux.Platform;

namespace Overskin.Linux.Interface;

/// <summary>
/// A quick, smooth look at captures (as on Windows): the arrow keys go through them, the wheel
/// zooms in towards the pointer, drag to look around, double-click for actual size. GIFs play
/// and videos play too, with a bar along the bottom like a video site. A strip of thumbnails
/// along the bottom, a search through the words in them, links in pictures that can be clicked,
/// and full screen (F). From here: copy, rotate, pin, edit, show in the folder, lift out the
/// subject, send to a phone, or move to the trash.
/// </summary>
public sealed class ViewerWindow : Window
{
    private List<string?> _paths;
    /// <summary>Every capture the viewer was opened with; <see cref="_paths"/> is those the search finds.</summary>
    private readonly List<string?> _all;
    private readonly Bitmap? _single;
    private int _index;
    private int _loadToken;

    private readonly ViewSurface _surface = new();
    private readonly Grid _stage = new() { ClipToBounds = true };
    private readonly TextBlock _title = new() { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 460 };
    private readonly TextBlock _details = new() { FontSize = 12, Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    private readonly TextBlock _position = new() { FontSize = 12, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, MinWidth = 64, TextAlignment = TextAlignment.Center };
    private readonly TextBlock _zoomText = new() { FontSize = 12, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, MinWidth = 46, TextAlignment = TextAlignment.Center };
    private readonly TextBlock _message = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _search = new() { Width = 220, Watermark = "Search the text in your captures" };
    private readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private readonly StackPanel _film = new() { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(8, 6) };
    private readonly ScrollViewer _filmScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly Dictionary<string, Bitmap> _cache = new();
    private Control _topBar = null!, _toolStrip = null!;
    private Button _rotateLeft = null!, _rotateRight = null!, _pin = null!, _lift = null!, _edit = null!;
    private Bitmap? _shown;
    private SearchIndex? _text;

    public ViewerWindow(IReadOnlyList<string> paths, string start, Bitmap? single = null)
    {
        _paths = paths.Select(p => (string?)p).ToList();
        if (_paths.Count == 0) _paths.Add(single != null ? null : start);
        _all = _paths.ToList();
        _single = single;
        _index = Math.Max(0, _paths.IndexOf(start));
        Title = "Overskin viewer";
        Icon = App.Icon;
        Width = 1100;
        Height = 760;
        MinWidth = 560;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.Parse("#0C0D0F"));

        _stage.Children.Add(_surface);
        _stage.Children.Add(_message);
        BuildVideoBar();
        BuildTheater();
        _surface.LinkClicked += link => _ = RunLinkAsync(link);
        _surface.Clicked += () => { if (_player != null) TogglePlay(); };
        _surface.ZoomChanged += () => _zoomText.Text = _player == null && _shown == null ? "" : $"{Math.Round(_surface.Scale * 100)}%";
        _surface.PointerMoved += (_, _) => { WakeVideoBar(); WakeTheater(); };

        // the bar along the top: what it is, and the search
        var top = new DockPanel { Margin = new Thickness(16, 8) };
        if (_all.Count(p => p != null) > 1)
        {
            ToolTip.SetTip(_search, "Finds captures by the words in them, their name or date  (Ctrl+F)");
            DockPanel.SetDock(_search, Dock.Right);
            top.Children.Add(_search);
            _search.TextChanged += (_, _) => { _searchDelay.Stop(); _searchDelay.Start(); };
            _searchDelay.Tick += (_, _) => { _searchDelay.Stop(); ApplySearch(); };
        }
        top.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { Ui.Icon(Icons.Picture, 16), new Border { Width = 10 }, _title, _details } });
        _topBar = top;

        // the tools along the bottom
        Button Tool(string icon, string tip, Action act)
        {
            var b = Ui.IconButton(icon, tip, act);
            return b;
        }
        Control Gap() => new Border { Width = 1, Height = 20, Margin = new Thickness(6, 0), Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)) };
        _rotateLeft = Tool(Icons.Undo, "Rotate left  (Shift+R)", () => _ = RotateAsync(-90));
        _rotateRight = Tool(Icons.Redo, "Rotate right  (R)", () => _ = RotateAsync(90));
        _pin = Tool(Icons.Pin, "Pin it on top  (P)", Pin);
        _lift = Tool(Icons.Wand, "Lift out the subject: the person, pet or thing in front, on a see-through background, opened in the editor", () => _ = LiftOutAsync());
        _edit = Ui.Button("Edit", Edit, Icons.Pen, "primary");
        var tools = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                Tool(Icons.Back, "Previous  (←)", () => Go(-1)), _position, Tool(Icons.Forward, "Next  (→)", () => Go(1)), Gap(),
                Tool(Icons.ZoomOut, "Zoom out  (-)", () => _surface.ZoomBy(1 / 1.4, null)), _zoomText,
                Tool(Icons.ZoomIn, "Zoom in  (+)", () => _surface.ZoomBy(1.4, null)),
                Tool(Icons.Area, "Fit  (0)  ·  Actual size  (1)", () => { if (_surface.Fitted) _surface.ActualSize(null); else _surface.Fit(); }),
                Tool(Icons.FullScreen, "Full screen  (F)", ToggleTheater), Gap(),
                _rotateLeft, _rotateRight,
                Tool(Icons.Copy, $"Copy  ({(OperatingSystem.IsMacOS() ? "⌘" : "Ctrl+")}C)", () => _ = CopyAsync()), _pin,
                Tool(Icons.Folder, "Show in folder", () => { if (CurrentPath is { } p) Desktop.ShowInFolder(p); }),
                _lift,
                Tool(Icons.Phone, "Send to your phone: a QR code to scan, over your Wi-Fi", () => { if (CurrentPath is { } p) PhoneWindow.Show(p); }),
                Tool(Icons.Trash, "Move to the trash  (Delete)", () => _ = DeleteAsync()), Gap(),
                _edit,
            },
        };
        _toolStrip = new Border { Child = tools, Padding = new Thickness(12, 6), Background = new SolidColorBrush(Colours.Panel) };
        _filmScroll.Content = _film;
        var bottom = new StackPanel { Children = { _toolStrip, _filmScroll } };
        _bottom = bottom;

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(_stage);
        Content = root;

        KeyDown += OnKey;
        Closed += (_, _) => { StopVideo(); _theaterIdle.Stop(); _videoIdle.Stop(); _videoTick.Stop(); };
        BuildFilm();
        Show(_index);
    }

    private Control _bottom = null!;

    // ---- what's shown --------------------------------------------------------------------

    private string? CurrentPath => _index < _paths.Count ? _paths[_index] : null;
    private static bool IsVideoFile(string p) => Path.GetExtension(p).ToLowerInvariant() is ".mp4" or ".mov" or ".m4v" or ".avi" or ".mkv" or ".webm";
    private static bool IsGifFile(string p) => Path.GetExtension(p).Equals(".gif", StringComparison.OrdinalIgnoreCase);

    private async void Show(int index)
    {
        if (_paths.Count == 0) { if (_all.Count == 0) Close(); return; }
        _index = (index % _paths.Count + _paths.Count) % _paths.Count;
        int token = ++_loadToken;
        StopVideo();
        _message.Text = "";
        _surface.Links = new();
        _position.Text = _paths.Count > 1 ? $"{_index + 1} / {_paths.Count}" + (_search.Text?.Length > 0 ? " found" : "") : "";
        MarkFilm();
        string? path = CurrentPath;
        _title.Text = path == null ? "New capture" : Path.GetFileName(path);
        Title = "Overskin viewer: " + _title.Text;

        if (path != null && (IsVideoFile(path) || IsGifFile(path)))
        {
            _shown = null;
            _surface.Show(null, null);
            if (!File.Exists(path)) { Missing(); return; }
            var player = await MediaPlayer.OpenAsync(path);
            if (token != _loadToken) { player?.Dispose(); return; }
            if (player == null)
            {
                // without ffmpeg a GIF still shows its first picture
                if (IsGifFile(path) && await LoadAsync(path) is { } still) { ShowPicture(path, still, token); return; }
                _message.Text = "Playing this needs ffmpeg. " + Converter.FfmpegHint.Replace("Converting needs", "It needs");
                _details.Text = Describe(path, null);
                SetTools(video: true);
                return;
            }
            StartVideo(player);
            _details.Text = Describe(path, player.Size);
            SetTools(video: !player.Loop);
            return;
        }

        Bitmap? picture = path == null ? _single : File.Exists(path) ? await LoadAsync(path) : null;
        if (token != _loadToken) return;
        if (path != null && !File.Exists(path)) { Missing(); return; }
        if (picture == null)
        {
            _surface.Show(null, null);
            _shown = null;
            _message.Text = "This picture can't be shown.";
            SetTools(video: false);
            return;
        }
        ShowPicture(path, picture, token);
        Preload();
    }

    private void Missing()
    {
        _surface.Show(null, null);
        _shown = null;
        _message.Text = "This file has been moved or deleted.";
        _details.Text = "";
        SetTools(video: false);
    }

    private void ShowPicture(string? path, Bitmap picture, int token)
    {
        _shown = picture;
        _surface.Show(picture, picture.PixelSize);
        _details.Text = Describe(path, picture.PixelSize);
        SetTools(video: false);
        _ = ShowLinksAsync(picture, token);
    }

    private void SetTools(bool video)
    {
        bool picture = _shown != null && _player == null;
        _rotateLeft.IsEnabled = _rotateRight.IsEnabled = picture;
        _pin.IsEnabled = picture;
        _lift.IsEnabled = picture && Ai.Available;
        ToolTip.SetTip(_edit, video ? "Trim, cut and add text to the video  (E)" : _player != null ? "Open in the GIF editor  (E)" : "Open in the editor  (E)");
    }

    private static string Describe(string? path, PixelSize? size)
    {
        var parts = new List<string>();
        if (size is { } s) parts.Add($"{s.Width} × {s.Height}");
        if (path != null && File.Exists(path))
        {
            var info = new FileInfo(path);
            parts.Add(info.Length >= 1 << 20 ? $"{info.Length / 1048576.0:0.0} MB" : $"{Math.Max(1, info.Length / 1024)} KB");
            parts.Add(info.LastWriteTime.ToString("d MMM yyyy, HH:mm"));
        }
        return string.Join("  ·  ", parts);
    }

    private async Task<Bitmap?> LoadAsync(string path)
    {
        if (_cache.TryGetValue(path, out var cached)) return cached;
        try
        {
            var picture = await Task.Run(() => new Bitmap(path));
            _cache[path] = picture;
            if (_cache.Count > 8) _cache.Remove(_cache.Keys.First(k => k != path));
            return picture;
        }
        catch { return null; }
    }

    /// <summary>The pictures either side, loaded ahead so the arrow keys are instant.</summary>
    private void Preload()
    {
        if (_paths.Count < 2) return;
        foreach (int d in new[] { 1, -1 })
        {
            string? p = _paths[(_index + d + _paths.Count) % _paths.Count];
            if (p != null && Converter.IsPicture(p) && !_cache.ContainsKey(p) && File.Exists(p)) _ = LoadAsync(p);
        }
    }

    private void Go(int step)
    {
        if (_paths.Count < 2) return;
        Show(_index + step);
    }

    // ---- links in pictures -----------------------------------------------------------------

    /// <summary>Reads the picture's words in the background and makes its links, email addresses and phone numbers clickable.</summary>
    private async Task ShowLinksAsync(Bitmap picture, int token)
    {
        if (!Ocr.IsAvailable) return;
        try
        {
            var lines = await Ocr.ReadLinesAsync(picture);
            if (lines == null || token != _loadToken || !ReferenceEquals(_shown, picture)) return;
            var words = new List<OcrWord>();
            var lineOf = new List<int>();
            for (int i = 0; i < lines.Count; i++)
                foreach (var w in lines[i]) { words.Add(w); lineOf.Add(i); }
            _surface.Links = TextActions.Find(words, lineOf);
        }
        catch (Exception ex) { Log.Write("viewer links: " + ex.Message); }
    }

    private async Task RunLinkAsync(TextAction link) => _details.Text = await TextActions.RunAsync(link, this);

    // ---- search ----------------------------------------------------------------------------

    /// <summary>Keeps only the captures the search finds (by name, date or the words in them), staying on the one shown if it's among them.</summary>
    private void ApplySearch()
    {
        string query = (_search.Text ?? "").Trim();
        string? current = CurrentPath;
        _text ??= new SearchIndex();
        _paths = query.Length == 0 ? _all.ToList() : _all.Where(p => p != null && Matches(p, query)).ToList();
        BuildFilm();
        if (_paths.Count == 0)
        {
            _loadToken++;
            StopVideo();
            _surface.Show(null, null);
            _shown = null;
            _position.Text = "";
            _message.Text = $"No captures with “{query}” in them.";
            return;
        }
        int keep = current != null ? _paths.IndexOf(current) : -1;
        Show(keep >= 0 ? keep : 0);
    }

    private bool Matches(string path, string query) =>
        Path.GetFileName(path).Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || File.GetLastWriteTime(path).ToString("d MMMM yyyy dddd").Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || _text!.Matches(path, query);

    // ---- film strip ------------------------------------------------------------------------

    private const double FilmHeight = 58;

    /// <summary>A thumbnail per capture.</summary>
    private void BuildFilm()
    {
        _film.Children.Clear();
        _filmScroll.IsVisible = _paths.Count >= 2;
        if (_paths.Count < 2) return;
        for (int i = 0; i < _paths.Count; i++)
        {
            int at = i;
            string? path = _paths[i];
            var thumb = new Image { Height = FilmHeight, MaxWidth = FilmHeight * 2.2, Stretch = Stretch.Uniform };
            var grid = new Grid { Children = { thumb } };
            if (path != null && (IsVideoFile(path) || IsGifFile(path)))
                grid.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)), CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 1),
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(3),
                    Child = new TextBlock { Text = IsGifFile(path) ? "GIF" : "Video", FontSize = 10, Foreground = Brushes.White },
                });
            var tile = new Border
            {
                Child = grid, Height = FilmHeight + 8, MinWidth = FilmHeight, Padding = new Thickness(2), BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.FromArgb(25, 255, 255, 255)), Cursor = new Cursor(StandardCursorType.Hand),
            };
            ToolTip.SetTip(tile, path != null ? Path.GetFileName(path) : "New capture");
            tile.PointerReleased += (_, _) => Show(at);
            _film.Children.Add(tile);
            _ = LoadThumbAsync(path, thumb);
        }
        MarkFilm();
    }

    private async Task LoadThumbAsync(string? path, Image target)
    {
        if (path == null) { target.Source = _single; return; }
        try
        {
            target.Source = await Task.Run(async () =>
            {
                if (Converter.IsPicture(path) || IsGifFile(path))
                {
                    await using var s = File.OpenRead(path);
                    return Bitmap.DecodeToWidth(s, (int)(FilmHeight * 2.2 * 1.5));
                }
                return await Converter.FirstFrameAsync(path, (int)(FilmHeight * 2.2 * 1.5));
            });
        }
        catch { }
    }

    /// <summary>Picks out the shown capture in the strip and scrolls it into view.</summary>
    private void MarkFilm()
    {
        for (int i = 0; i < _film.Children.Count; i++)
        {
            var tile = (Border)_film.Children[i];
            tile.BorderBrush = i == _index ? new SolidColorBrush(Colours.Accent) : Brushes.Transparent;
            tile.Opacity = i == _index ? 1 : 0.7;
        }
        if (_index < _film.Children.Count)
            Dispatcher.UIThread.Post(() =>
            {
                if (_index >= _film.Children.Count) return;
                var tile = _film.Children[_index];
                tile.BringIntoView();
            }, DispatcherPriority.Background);
    }

    // ---- what can be done with it ------------------------------------------------------------

    private void Edit()
    {
        var path = CurrentPath;
        if (path != null && IsVideoFile(path)) { StopVideo(); _ = VideoEditor.OpenAsync(path); return; }
        if (path != null && IsGifFile(path)) { StopVideo(); _ = GifEditor.OpenAsync(path); return; }
        if (_shown == null) return;
        new EditorWindow(_shown, path).Show();
        Close();
    }

    private void Pin()
    {
        if (_shown != null && _player == null) new PinWindow(_shown).Show();
    }

    private async Task CopyAsync()
    {
        var path = CurrentPath;
        bool ok = path != null ? await Desktop.CopyFilesAsync(new[] { path }, this) : _shown != null && await Desktop.CopyImageAsync(_shown, this);
        _details.Text = ok ? "Copied" : "Couldn't reach the clipboard.";
    }

    /// <summary>Turns the picture a quarter and saves it so (the file keeps its kind).</summary>
    private async Task RotateAsync(int degrees)
    {
        if (_shown == null || _player != null) return;
        var turned = Effects.Rotate(_shown, clockwise: degrees > 0);
        string? path = CurrentPath;
        if (path != null)
        {
            try
            {
                await Task.Run(() =>
                {
                    string temp = path + ".part";
                    if (Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg") Imaging.SaveJpeg(turned, temp);
                    else Imaging.SavePng(turned, temp);
                    File.Move(temp, path, overwrite: true);
                });
                _cache[path] = turned;
                App.Current.NotifyLibraryChanged();
            }
            catch (Exception ex)
            {
                _details.Text = "Couldn't save the turned picture: " + ex.Message;
                return;
            }
        }
        ShowPicture(path, turned, _loadToken);
    }

    /// <summary>Moves it to the trash and shows the next one.</summary>
    private async Task DeleteAsync()
    {
        string? path = CurrentPath;
        if (path == null || !File.Exists(path)) return;
        StopVideo();
        try { await Desktop.TrashAsync(new[] { path }); }
        catch (Exception ex) { _details.Text = "Couldn't move it to the trash: " + ex.Message; return; }
        if (IsVideoFile(path)) ZoomTrack.DeleteFor(path);
        _cache.Remove(path);
        _all.Remove(path);
        _paths.RemoveAt(_index);
        BuildFilm();
        App.Current.NotifyLibraryChanged();
        if (_all.Count == 0) { Close(); return; }
        if (_paths.Count == 0) { ApplySearch(); return; }
        Show(Math.Min(_index, _paths.Count - 1));
    }

    /// <summary>Lifts the subject out of the picture showing and opens it in the editor, see-through round it.</summary>
    private async Task LiftOutAsync()
    {
        if (_shown is not { } picture || _player != null) return;
        if (!Ai.Available) { _details.Text = "This copy of Overskin was built without its AI models."; return; }
        _details.Text = "Lifting out the subject…";
        try
        {
            var pixels = Ai.Of(picture);
            var cut = await Task.Run(() => Ai.CutOut(pixels));
            if (cut is not { } c) { _details.Text = "Nothing stands out enough in this picture to lift out."; return; }
            new EditorWindow(Imaging.FromPixels(c.Data, new PixelSize(c.Width, c.Height)), null).Show();
            _details.Text = Describe(CurrentPath, picture.PixelSize);
        }
        catch (Exception ex) { _details.Text = "Couldn't lift it out: " + ex.Message; }
    }

    // ---- keys ------------------------------------------------------------------------------

    private void OnKey(object? sender, KeyEventArgs e)
    {
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (ctrl && e.Key == Key.F && _search.IsVisible) { _search.Focus(); _search.SelectAll(); e.Handled = true; return; }
        if (FocusManager?.GetFocusedElement() is TextBox)
        {
            if (e.Key is Key.Escape or Key.Enter or Key.Down) { _surface.Focus(); e.Handled = true; }
            return;
        }
        bool video = _player != null;
        Action? act = e.Key switch
        {
            Key.Escape when _theater => ToggleTheater,
            Key.Escape => Close,
            Key.F or Key.F11 when !ctrl => ToggleTheater,
            Key.Left or Key.PageUp => () => Go(-1),
            Key.Right or Key.PageDown => () => Go(1),
            Key.Home => () => Show(0),
            Key.End => () => Show(_paths.Count - 1),
            Key.OemPlus or Key.Add => () => _surface.ZoomBy(1.4, null),
            Key.OemMinus or Key.Subtract => () => _surface.ZoomBy(1 / 1.4, null),
            Key.D0 or Key.NumPad0 => _surface.Fit,
            Key.D1 or Key.NumPad1 => () => _surface.ActualSize(null),
            Key.C when ctrl => () => _ = CopyAsync(),
            Key.R when !ctrl => () => _ = RotateAsync(shift ? -90 : 90),
            Key.E when !ctrl => Edit,
            Key.P when !ctrl => Pin,
            Key.Delete or Key.Back when !video || !ctrl => () => _ = DeleteAsync(),
            Key.Space or Key.K when video => TogglePlay,
            Key.J when video => () => Skip(-5),
            Key.L when video => () => Skip(5),
            Key.M when video => ToggleMute,
            Key.Up when video => () => ChangeVolume(0.1),
            Key.Down when video => () => ChangeVolume(-0.1),
            _ => null,
        };
        if (act == null) return;
        e.Handled = true;
        act();
    }

    // ---- full screen ------------------------------------------------------------------------

    private bool _theater;
    private WindowState _stateBefore;
    private readonly Button _exitTheater = new() { Width = 40, Height = 40, IsVisible = false, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 16, 16, 0) };
    private readonly DispatcherTimer _theaterIdle = new() { Interval = TimeSpan.FromSeconds(2) };

    private void BuildTheater()
    {
        _exitTheater.Content = Ui.Icon(Icons.FullScreen, 18);
        _exitTheater.Background = new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0));
        _exitTheater.Foreground = Brushes.White;
        _exitTheater.Transitions = new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(300) } };
        ToolTip.SetTip(_exitTheater, "Leave full screen  (Esc)");
        _exitTheater.Click += (_, _) => ToggleTheater();
        _stage.Children.Add(_exitTheater);
        // the button shows while the pointer moves and tucks away when it rests
        _theaterIdle.Tick += (_, _) =>
        {
            _theaterIdle.Stop();
            if (_theater && !_exitTheater.IsPointerOver)
            {
                _exitTheater.Opacity = 0;
                if (_player is not { Playing: true }) _surface.Cursor = new Cursor(StandardCursorType.None);
            }
        };
    }

    private void WakeTheater()
    {
        if (!_theater) return;
        _exitTheater.Opacity = 1;
        _surface.Cursor = Cursor.Default;
        _theaterIdle.Stop();
        _theaterIdle.Start();
    }

    /// <summary>The picture or video on black over the whole screen, with nothing else in the way.</summary>
    private void ToggleTheater()
    {
        _theater = !_theater;
        _topBar.IsVisible = _bottom.IsVisible = !_theater;
        _exitTheater.IsVisible = _theater;
        if (_theater)
        {
            _stateBefore = WindowState;
            WindowState = WindowState.FullScreen;
            Background = Brushes.Black;
            WakeTheater();
        }
        else
        {
            WindowState = _stateBefore == WindowState.FullScreen ? WindowState.Normal : _stateBefore;
            Background = new SolidColorBrush(Color.Parse("#0C0D0F"));
            _surface.Cursor = Cursor.Default;
            _theaterIdle.Stop();
        }
        Dispatcher.UIThread.Post(_surface.Fit, DispatcherPriority.Background);
    }

    // ---- videos and GIFs ----------------------------------------------------------------------

    private static double _volume = 0.8;
    private static bool _muted;
    private MediaPlayer? _player;
    private Border _videoBar = null!;
    private Button _playButton = null!, _muteButton = null!;
    private Slider _scrub = null!, _volumeSlider = null!;
    private TextBlock _clock = null!;
    private Border _flash = null!;
    private readonly DispatcherTimer _videoTick = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly DispatcherTimer _videoIdle = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private bool _scrubSync;

    private void BuildVideoBar()
    {
        _playButton = Ui.IconButton(Icons.Pause, "Pause  (Space)", TogglePlay);
        _muteButton = Ui.IconButton(Icons.Volume, "Mute  (M)", ToggleMute);
        _clock = new TextBlock { Foreground = Brushes.White, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 12, 0), MinWidth = 86 };
        _scrub = new Slider { Minimum = 0, Maximum = 1, VerticalAlignment = VerticalAlignment.Center, IsSnapToTickEnabled = false };
        ToolTip.SetTip(_scrub, "Drag to move through the video  (J and L skip 5 seconds)");
        _scrub.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty || _scrubSync || _player == null) return;
            _player.Seek(_scrub.Value);
            ShowClock();
            WakeVideoBar();
        };
        _volumeSlider = new Slider { Minimum = 0, Maximum = 1, Width = 90, Value = _volume, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(_volumeSlider, "Volume  (Up and Down)");
        _volumeSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            _volume = _volumeSlider.Value;
            if (_volume > 0 && _muted) _muted = false;
            ApplyVolume();
        };
        var row = new DockPanel { Margin = new Thickness(10, 22, 14, 8) };
        DockPanel.SetDock(_playButton, Dock.Left);
        DockPanel.SetDock(_clock, Dock.Left);
        DockPanel.SetDock(_volumeSlider, Dock.Right);
        DockPanel.SetDock(_muteButton, Dock.Right);
        _muteButton.Margin = new Thickness(12, 0, 2, 0);
        row.Children.Add(_playButton);
        row.Children.Add(_clock);
        row.Children.Add(_volumeSlider);
        row.Children.Add(_muteButton);
        row.Children.Add(_scrub);
        // a soft dark fade behind the bar, so it reads over any video
        _videoBar = new Border
        {
            Child = row, VerticalAlignment = VerticalAlignment.Bottom, IsVisible = false,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(0, 0, 0, 0), 0), new GradientStop(Color.FromArgb(0xB0, 0, 0, 0), 1) },
            },
            Transitions = new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(250) } },
        };
        _videoBar.PointerMoved += (_, _) => WakeVideoBar();
        _stage.Children.Add(_videoBar);
        _flash = new Border
        {
            Width = 72, Height = 72, CornerRadius = new CornerRadius(36), Background = new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0)),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = 0, IsHitTestVisible = false,
            Transitions = new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(450) } },
        };
        _stage.Children.Add(_flash);
        _videoTick.Tick += (_, _) =>
        {
            if (_player == null) return;
            _scrubSync = true;
            _scrub.Value = Math.Min(_scrub.Maximum, _player.Position);
            _scrubSync = false;
            ShowClock();
        };
        _videoIdle.Tick += (_, _) =>
        {
            _videoIdle.Stop();
            // tucked away while it plays and the pointer rests, like a video site
            if (_player is { Playing: true } && !_videoBar.IsPointerOver) _videoBar.Opacity = 0;
        };
    }

    private void StartVideo(MediaPlayer player)
    {
        _player = player;
        player.SetVolume(_volume, _muted);
        player.FrameShown += () => { if (ReferenceEquals(_player, player)) _surface.ShowFrame(player.Frame, player.Size); };
        player.Ended += () => { if (ReferenceEquals(_player, player)) { SetPlayIcon(); _videoBar.Opacity = 1; } };
        _surface.Show(null, player.Size);
        // GIFs loop on their own and have no bar; videos get the bar
        _videoBar.IsVisible = !player.Loop;
        _videoBar.Opacity = 1;
        _scrubSync = true;
        _scrub.Maximum = Math.Max(0.1, player.Duration);
        _scrub.Value = 0;
        _scrubSync = false;
        _videoTick.Start();
        player.Play();
        SetPlayIcon();
        ApplyVolume();
        WakeVideoBar();
    }

    private void StopVideo()
    {
        _videoTick.Stop();
        _videoIdle.Stop();
        _videoBar.IsVisible = false;
        _flash.Opacity = 0;
        _player?.Dispose();
        _player = null;
    }

    private void TogglePlay()
    {
        if (_player == null) return;
        if (_player.Playing) _player.Pause(); else _player.Play();
        SetPlayIcon();
        Flash(_player.Playing ? Icons.Play : Icons.Pause);
        WakeVideoBar();
    }

    private void SetPlayIcon()
    {
        bool playing = _player?.Playing == true;
        _playButton.Content = Ui.Icon(playing ? Icons.Pause : Icons.Play, 18);
        ToolTip.SetTip(_playButton, (playing ? "Pause" : "Play") + "  (Space)");
    }

    /// <summary>The sign in the middle on every click: shows, then fades away.</summary>
    private void Flash(string icon)
    {
        _flash.Child = new PathIcon { Data = StreamGeometry.Parse(icon), Width = 30, Height = 30, Foreground = Brushes.White };
        var transitions = _flash.Transitions;
        _flash.Transitions = null;
        _flash.Opacity = 1;
        _flash.Transitions = transitions;
        DispatcherTimer.RunOnce(() => _flash.Opacity = 0, TimeSpan.FromMilliseconds(200));
    }

    private void Skip(double seconds)
    {
        if (_player == null) return;
        double to = Math.Clamp(_player.Position + seconds, 0, _player.Duration);
        _player.Seek(to);
        _scrubSync = true;
        _scrub.Value = to;
        _scrubSync = false;
        ShowClock();
        WakeVideoBar();
    }

    private void ChangeVolume(double by)
    {
        _volumeSlider.Value = Math.Clamp(_volumeSlider.Value + by, 0, 1);
        WakeVideoBar();
    }

    private void ToggleMute()
    {
        _muted = !_muted;
        ApplyVolume();
        WakeVideoBar();
    }

    private void ApplyVolume()
    {
        _player?.SetVolume(_volume, _muted);
        bool silent = _muted || _volume <= 0.001;
        _muteButton.Content = Ui.Icon(silent ? Icons.Mute : Icons.Volume, 18);
        ToolTip.SetTip(_muteButton, (_muted ? "Unmute" : "Mute") + "  (M)" + (_player is { HasSound: true } && !MediaPlayer.CanPlaySound ? " · sound needs ffplay, which comes with ffmpeg" : ""));
    }

    private void ShowClock()
    {
        static string T(double s) => s >= 3600 ? TimeSpan.FromSeconds(s).ToString(@"h\:mm\:ss") : TimeSpan.FromSeconds(s).ToString(@"m\:ss");
        if (_player != null) _clock.Text = $"{T(_player.Position)} / {T(_player.Duration)}";
    }

    /// <summary>The bar shows again whenever the pointer moves, and tucks away after a rest.</summary>
    private void WakeVideoBar()
    {
        if (_player == null || _player.Loop) return;
        _videoBar.Opacity = 1;
        _videoIdle.Stop();
        _videoIdle.Start();
    }

    // ---- for the self-test ----

    internal int IndexForTest => _index;
    internal double ScaleForTest => _surface.Scale;
    internal string TitleForTest => _title.Text ?? "";
    internal int FilmForTest => _film.Children.Count;
    internal void GoForTest(int step) => Go(step);
    internal void ZoomForTest(double factor) => _surface.ZoomBy(factor, null);
    internal Task RotateForTest(int degrees) => RotateAsync(degrees);
    internal Bitmap? ShownForTest => _shown;
    internal MediaPlayer? PlayerForTest => _player;
    internal void SearchForTest(string text) { _search.Text = text; ApplySearch(); }
    internal void KeyForTest(Key key) => OnKey(this, new KeyEventArgs { Key = key, RoutedEvent = KeyDownEvent });
    internal bool TheaterForTest => _theater;

    /// <summary>The picture (or the video's frame), zoomed and moved; and the links in it, underlined, to click.</summary>
    private sealed class ViewSurface : Control
    {
        private Bitmap? _picture;
        private PixelSize? _size;
        private double _zoom; // 0: fit the window
        private Vector _offset;
        private Point? _dragFrom;
        private bool _dragged;
        private List<TextAction> _links = new();
        private TextAction? _hoverLink;

        public event Action<TextAction>? LinkClicked;
        public event Action? Clicked;
        public event Action? ZoomChanged;

        public List<TextAction> Links
        {
            get => _links;
            set { _links = value; _hoverLink = null; InvalidateVisual(); }
        }

        public ViewSurface()
        {
            ClipToBounds = true;
            Focusable = true;
        }

        /// <summary>Something new to show at this size (the picture, or nothing yet while a video starts).</summary>
        public void Show(Bitmap? picture, PixelSize? size)
        {
            _picture = picture;
            _size = size;
            Fit();
        }

        /// <summary>The next frame of a video: same place and zoom.</summary>
        public void ShowFrame(Bitmap? frame, PixelSize size)
        {
            _picture = frame;
            if (_size != size) { _size = size; Fit(); }
            InvalidateVisual();
        }

        public bool Fitted => _zoom == 0;

        public void Fit()
        {
            _zoom = 0;
            _offset = default;
            InvalidateVisual();
            ZoomChanged?.Invoke();
        }

        public void ActualSize(Point? at) => ZoomTo(1, at);

        public void ZoomBy(double factor, Point? at) => ZoomTo(Math.Clamp(Scale * factor, 0.05, 32), at);

        private void ZoomTo(double next, Point? at)
        {
            if (_size == null) return;
            var p = at ?? new Point(Bounds.Width / 2, Bounds.Height / 2);
            var before = Placed();
            // keep the point under the pointer where it is
            var rel = new Point((p.X - before.X) / before.Width, (p.Y - before.Y) / before.Height);
            _zoom = next;
            var after = Placed();
            _offset += new Vector(p.X - (after.X + rel.X * after.Width), p.Y - (after.Y + rel.Y * after.Height));
            InvalidateVisual();
            ZoomChanged?.Invoke();
        }

        /// <summary>The whole picture in view, never blown up past its real size (Retina: its real size on screen).</summary>
        private double FitScale => _size is not { } s ? 1 : Math.Min(1, Math.Min((Bounds.Width - 40) / s.Width, (Bounds.Height - 40) / s.Height));
        public double Scale => _zoom > 0 ? _zoom : Math.Max(0.02, FitScale);

        private Rect Placed()
        {
            if (_size is not { } s) return default;
            double k = Scale;
            var size = new Size(s.Width * k, s.Height * k);
            return new Rect(new Point((Bounds.Width - size.Width) / 2 + _offset.X, (Bounds.Height - size.Height) / 2 + _offset.Y), size);
        }

        private TextAction? LinkAt(Point p)
        {
            if (_links.Count == 0 || _size == null) return null;
            var placed = Placed();
            double k = Scale;
            var at = new Point((p.X - placed.X) / k, (p.Y - placed.Y) / k);
            return _links.FirstOrDefault(l => l.Bounds.Inflate(new Thickness(2, 3)).Contains(at));
        }

        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);
            if (_zoom > 0)
            {
                // zoomed in: keep what was in the middle in the middle
                InvalidateVisual();
            }
            ZoomChanged?.Invoke();
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            if (_size == null) return;
            ZoomBy(e.Delta.Y > 0 ? 1.15 : 1 / 1.15, e.GetPosition(this));
            e.Handled = true;
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            Focus();
            var p = e.GetPosition(this);
            if (e.ClickCount == 2)
            {
                if (_zoom > 0) Fit(); else ActualSize(p);
                return;
            }
            if (LinkAt(p) is { } link) { LinkClicked?.Invoke(link); return; }
            _dragFrom = p;
            _dragged = false;
            e.Pointer.Capture(this);
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            var now = e.GetPosition(this);
            if (_dragFrom is not { } from)
            {
                var link = LinkAt(now);
                if (!ReferenceEquals(link, _hoverLink))
                {
                    _hoverLink = link;
                    Cursor = link != null ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
                    ToolTip.SetTip(this, link?.Hint);
                    InvalidateVisual();
                }
                return;
            }
            if (Math.Abs(now.X - from.X) + Math.Abs(now.Y - from.Y) > 3) _dragged = true;
            _offset += now - from;
            _dragFrom = now;
            InvalidateVisual();
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            bool click = _dragFrom != null && !_dragged;
            _dragFrom = null;
            e.Pointer.Capture(null);
            if (click) Clicked?.Invoke();
        }

        public override void Render(DrawingContext ctx)
        {
            // the whole area takes the pointer
            ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
            if (_picture == null || _size == null) return;
            var placed = Placed();
            using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = Scale >= 2 ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality }))
                ctx.DrawImage(_picture, new Rect(_picture.Size), placed);
            if (_links.Count == 0) return;
            var accent = Colours.Accent;
            double k = Scale;
            foreach (var link in _links)
            {
                var r = link.Bounds;
                var box = new Rect(placed.X + (r.X - 2) * k, placed.Y + (r.Y - 1) * k, (r.Width + 4) * k, (r.Height + 2) * k);
                byte alpha = ReferenceEquals(link, _hoverLink) ? (byte)55 : (byte)18;
                ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(alpha, accent.R, accent.G, accent.B)), box, 2);
                double line = Math.Max(1.5, r.Height * 0.09 * k);
                ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(200, accent.R, accent.G, accent.B)), new Rect(box.X, box.Bottom - line, box.Width, line));
            }
        }
    }
}

/// <summary>A question with a line of text to type, OK and Cancel.</summary>
public sealed class PromptWindow : Window
{
    private readonly TaskCompletionSource<string?> _result = new();

    private PromptWindow(string title, string body, string text)
    {
        Title = title;
        Icon = App.Icon;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var box = new TextBox { Text = text };
        var ok = Ui.Button("OK", () => { _result.TrySetResult(box.Text ?? ""); Close(); }, cls: "primary");
        var cancel = Ui.Button("Cancel", Close);
        var label = Ui.Label(body, "dim");
        label.TextWrapping = TextWrapping.Wrap;
        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 12,
            Children =
            {
                Ui.Label(title, "h2"), label, box,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } },
            },
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { _result.TrySetResult(box.Text ?? ""); Close(); }
            else if (e.Key == Key.Escape) Close();
        };
        Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        Closed += (_, _) => _result.TrySetResult(null);
    }

    /// <summary>The text typed, or null if cancelled.</summary>
    public static Task<string?> AskAsync(Window? owner, string title, string body, string text = "")
    {
        var w = new PromptWindow(title, body, text);
        if (owner != null) w.ShowDialog(owner); else w.Show();
        return w._result.Task;
    }
}
