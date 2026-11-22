using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static Overskin.NativeMethods;

namespace Overskin;

/// <summary>A saved capture shown in the library.</summary>
public sealed class LibraryItem : INotifyPropertyChanged
{
    private ImageSource? _thumb;

    public required string Path { get; init; }
    public required DateTime Time { get; init; }
    public string Caption { get; init; } = "";
    public string Details { get; set; } = "";

    public ImageSource? Thumb
    {
        get => _thumb;
        set { _thumb = value; Raise(); }
    }

    private bool _selected;

    /// <summary>Picked for combining.</summary>
    public bool IsSelected
    {
        get => _selected;
        set { _selected = value; Raise(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Screenshots: capturing, what happens afterwards, the editor, the library and the screen tools.</summary>
public partial class MainWindow
{
    private readonly ObservableCollection<LibraryItem> _library = new();
    private bool _captureBusy;

    // ===================================================================================
    //  getting out of the way of the camera
    // ===================================================================================

    /// <summary>Hides everything of ours that would show up in a screenshot. Returns whether the main window was showing.</summary>
    private async Task<bool> ClearTheScreen()
    {
        bool wasShown = IsVisible && WindowState != WindowState.Minimized;
        _ghost?.Hide();
        Toast.HideNow();
        CaptureNotice.CloseAll();
        if (wasShown) Hide();
        await Task.Delay(wasShown ? 180 : 60); // let the window's fade finish before the screen is copied
        return wasShown;
    }

    private void Reappear(bool wasShown)
    {
        if (wasShown)
        {
            Show();
            UpdateGhost();
        }
    }

    /// <summary>Counts down in the toast, then clears it so it isn't captured.</summary>
    private async Task CountDown(int seconds)
    {
        for (int s = seconds; s > 0; s--)
        {
            Toast.ShowSticky($"Capturing in {s}…");
            await Task.Delay(1000);
        }
        Toast.HideNow();
        await Task.Delay(60);
    }

    /// <summary>Freezes the screen and lets the user choose; null if they cancelled.</summary>
    private async Task<(CaptureOverlay Overlay, bool WasShown)?> Choose(OverlayMode mode, string hint, bool useDelay, bool tools = false, int? delay = null)
    {
        bool wasShown = await ClearTheScreen();
        int wait = delay ?? (useDelay ? _settings.CaptureDelay : 0);
        if (wait > 0) await CountDown(wait);

        bool pointer = _settings.AfterCursor && mode is OverlayMode.Region or OverlayMode.Window;
        var overlay = CaptureOverlay.Open(mode, hint, pointer, tools);
        if (overlay == null)
        {
            Reappear(wasShown);
            Toast.Show("Couldn't read the screen.", true);
            return null;
        }
        overlay.ShowDialog();
        if (overlay.HasToolbar && overlay.IncludePointer != _settings.AfterCursor)
        {
            // the pointer button on the capture bar is the same setting as on the Capture page
            _settings.AfterCursor = overlay.IncludePointer;
            AfterCursorSwitch.IsChecked = _settings.AfterCursor;
            SaveSettings();
        }
        return (overlay, wasShown);
    }

    private async void Guarded(Func<Task> work)
    {
        if (_captureBusy || _drawer.IsRunning) return;
        _captureBusy = true;
        try { await work(); }
        catch (Exception ex) { Toast.Show("Something went wrong: " + ex.Message, true); }
        finally { _captureBusy = false; }
    }

    // ===================================================================================
    //  capture modes
    // ===================================================================================

    private void CaptureRegion_Click(object sender, RoutedEventArgs e) => CaptureRegion(useDelay: true);
    private void CaptureWindow_Click(object sender, RoutedEventArgs e) => CaptureWindowPick();
    private void CaptureScreen_Click(object sender, RoutedEventArgs e) => CaptureFullScreen(useDelay: true);
    private void RepeatRegion_Click(object sender, RoutedEventArgs e) => CaptureLastRegion();

    private void CaptureRegion(bool useDelay = false, AfterSteps? steps = null, int? delay = null) => Guarded(async () =>
    {
        var chosen = await Choose(OverlayMode.Region, "Drag over an area, or click a window.  Esc cancels.", useDelay, tools: true, delay);
        if (chosen is not { } c) return;
        Reappear(c.WasShown);
        if (c.Overlay.Selection is not { } r) return;
        _settings.LastRegion = new[] { r.X, r.Y, r.Width, r.Height };
        Finish(await WindowAloneOrAsync(c.Overlay, r), r, steps);
    });

    private void CaptureWindowPick(AfterSteps? steps = null, int? delay = null) => Guarded(async () =>
    {
        var chosen = await Choose(OverlayMode.Window, "Click the window to capture.  Esc cancels.", useDelay: true, tools: true, delay);
        if (chosen is not { } c) return;
        Reappear(c.WasShown);
        if (c.Overlay.Selection is not { } r)
        {
            CrashLog.Note("window pick: closed without a window chosen");
            return;
        }
        Finish(await WindowAloneOrAsync(c.Overlay, r), r, steps);
    });

    /// <summary>
    /// With "Windows on a transparent background", a clicked window is grabbed on its own and set
    /// on a transparent backdrop with a shadow; otherwise (or if it can't be) the frozen crop.
    /// </summary>
    private async Task<BitmapSource> WindowAloneOrAsync(CaptureOverlay overlay, Int32Rect r)
    {
        CrashLog.Note($"capture: {r.Width}x{r.Height}, window clicked={overlay.SelectedWindow != IntPtr.Zero}, on its own={_settings.WindowAlpha}");
        if (_settings.WindowAlpha && overlay.SelectedWindow != IntPtr.Zero && !overlay.HasMarks && await WindowAloneAsync(overlay.SelectedWindow) is { } alone)
            return alone;
        return overlay.Crop(r);
    }

    /// <summary>
    /// The window drawn on its own. The window is asked to draw itself, which a busy or stuck app
    /// can take forever to do, so it's asked off the main thread and given a moment and a half;
    /// after that the frozen screen is used instead.
    /// </summary>
    private static async Task<BitmapSource?> WindowAloneAsync(IntPtr window)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var grab = Task.Run(() => CaptureWindowOnly(window));
        if (await Task.WhenAny(grab, Task.Delay(1500)) != grab)
        {
            CrashLog.Note("window on its own: the app didn't draw itself within 1.5 s; used the screen instead");
            return null;
        }
        BitmapSource? shot;
        try { shot = await grab; }
        catch (Exception ex)
        {
            CrashLog.Note("window on its own failed: " + ex.GetType().Name);
            return null;
        }
        CrashLog.Note($"window on its own: {(shot == null ? "came back blank" : $"{shot.PixelWidth}x{shot.PixelHeight}")} in {sw.ElapsedMilliseconds} ms");
        if (shot == null) return null;
        return WindowAlone(shot);
    }

    private static BitmapSource? WindowAlone(BitmapSource shot)
    {
        bool rounded = Environment.OSVersion.Version.Build >= 22000; // Windows 11 rounds window corners
        return ImageLoader.Normalize(Beautify.Render(shot, new BeautifyOptions { Background = "None", Padding = 36, Corners = rounded ? 8 : 0, Shadow = 45 }));
    }

    /// <summary>The hotkey version: whatever window is in front right now, no clicking.</summary>
    private void CaptureActiveWindow(AfterSteps? steps = null, int? delay = null)
    {
        IntPtr front = GetForegroundWindow();
        if (front == IntPtr.Zero || _manager.IsOwnWindow(front) || ClassName(front) is "Progman" or "WorkerW")
        {
            CaptureWindowPick(steps, delay); // nothing useful in front: let the user click one
            return;
        }
        CaptureWindowHandle(front, steps, delay);
    }

    private void CaptureWindowHandle(IntPtr front, AfterSteps? steps = null, int? delay = null) => Guarded(async () =>
    {
        bool wasShown = await ClearTheScreen();
        if (delay is > 0) await CountDown(delay.Value);
        var b = VisibleBounds(front);
        var vs = VirtualScreen();
        int x = Math.Max(b.Left, vs.Left), y = Math.Max(b.Top, vs.Top);
        int w = Math.Min(b.Right, vs.Right) - x, h = Math.Min(b.Bottom, vs.Bottom) - y;
        var shot = (_settings.WindowAlpha ? await WindowAloneAsync(front) : null) ?? CaptureScreen(x, y, w, h, _settings.AfterCursor);
        Reappear(wasShown);
        if (shot != null) Finish(shot, new Int32Rect(x, y, w, h), steps);
    });

    private void CaptureFullScreen(bool useDelay = false, AfterSteps? steps = null, int? delay = null) => Guarded(async () =>
    {
        bool wasShown = await ClearTheScreen();
        int wait = delay ?? (useDelay ? _settings.CaptureDelay : 0);
        if (wait > 0) await CountDown(wait);
        var r = _settings.FullScreenAllMonitors ? VirtualScreen() : MonitorBounds(CursorPos());
        var shot = CaptureScreen(r.Left, r.Top, r.Width, r.Height, _settings.AfterCursor);
        Reappear(wasShown);
        if (shot != null) Finish(shot, new Int32Rect(r.Left, r.Top, r.Width, r.Height), steps);
    });

    private void CaptureLastRegion(AfterSteps? steps = null, int? delay = null)
    {
        if (_settings.LastRegion is not { Length: 4 } a)
        {
            Toast.Show("Capture a region first; this repeats it.");
            return;
        }
        Guarded(async () =>
        {
            bool wasShown = await ClearTheScreen();
            int wait = delay ?? _settings.CaptureDelay;
            if (wait > 0) await CountDown(wait);
            var shot = CaptureScreen(a[0], a[1], a[2], a[3], _settings.AfterCursor);
            Reappear(wasShown);
            if (shot != null) Finish(shot, new Int32Rect(a[0], a[1], a[2], a[3]), steps);
        });
    }

    /// <summary>What the Capture page says to do after a capture.</summary>
    private AfterSteps SettingsSteps => new()
    {
        AutoHide = _settings.AutoHide,
        Watermark = _settings.Watermark.OnScreenshots,
        Save = _settings.AfterSave,
        Copy = _settings.AfterCopy,
        Pin = _settings.AfterPin,
        Editor = _settings.AfterEditor,
        Viewer = _settings.AfterViewer && !_settings.AfterEditor,
        Preview = _settings.AfterPreview,
    };

    /// <summary>Runs a workflow: its capture, then its steps.</summary>
    private void RunWorkflow(Workflow w)
    {
        var steps = w.Steps;
        int delay = w.Delay;
        switch (w.Capture)
        {
            case "Window": CaptureActiveWindow(steps, delay); break;
            case "Screen": CaptureFullScreen(false, steps, delay); break;
            case "LastRegion": CaptureLastRegion(steps, delay); break;
            default: CaptureRegion(false, steps, delay); break;
        }
    }

    // ===================================================================================
    //  after a capture
    // ===================================================================================

    /// <summary>The strip of latest captures (Ctrl+Alt+H).</summary>
    private void ShowRecent()
    {
        var paths = _settings.History.AsEnumerable().Reverse().Select(h => h.Path).Where(File.Exists).Take(10).ToList();
        // a click opens the viewer, with the other recent captures either side, as the Library does
        RecentStrip.Toggle(paths, path =>
        {
            var list = paths.Select(p => (string?)p).ToList();
            var viewer = new ViewerWindow(list, Math.Max(0, list.IndexOf(path)), ViewerHostFor());
            viewer.PlaceBeside(this);
            viewer.Show();
            viewer.Activate();
        });
    }

    private string CaptureName(string? path) =>
        path != null ? Path.GetFileName(path) : $"Capture {DateTime.Now:HH.mm.ss}";

    /// <summary>Copy, save, pin, edit or preview: as set on the Capture page, or as a workflow says.</summary>
    private async void Finish(BitmapSource image, Int32Rect where, AfterSteps? steps = null)
    {
        var s = steps ?? SettingsSteps;
        // what was captured, for the file name: the window in the middle of it
        FileNames.Context = NameInfo.At(where.X + where.Width / 2, where.Y + where.Height / 2);
        if (s.AutoHide)
        {
            try
            {
                var found = await Redaction.FindPrivateAsync(image);
                if (found is { Count: > 0 })
                {
                    image = Redaction.Apply(image, found.Select(f => f.Area), HideMode.Pixelate, 4);
                    Toast.Show("Hid " + Redaction.Describe(found.Select(f => f.Kind)) + ".");
                }
            }
            catch
            {
                // a failed read leaves the picture as it is
            }
        }
        if (s.Beautify)
            image = ImageLoader.Normalize(Beautify.Render(image, new BeautifyOptions()));
        if (s.Watermark && _settings.Watermark.HasContent)
            image = Watermark.Apply(image, _settings.Watermark);
        // the file is written off the main thread, then the clipboard gets the picture and the file
        var timing = System.Diagnostics.Stopwatch.StartNew();
        string? path = s.Save ? await SaveCaptureAsync(image) : null;
        long saved = timing.ElapsedMilliseconds;
        if (s.Copy) CopyImage(image, quiet: true, path);
        CrashLog.Note($"after capture: saved in {saved} ms, copied by {timing.ElapsedMilliseconds} ms");
        bool textCopied = false;
        if (s.CopyText)
        {
            string? text = null;
            try { text = await TextRecognizer.RecognizeAsync(image); } catch { }
            if (string.IsNullOrWhiteSpace(text)) Toast.Show("No text found in that capture.", true);
            else
            {
                try { Clipboard.SetText(text); textCopied = true; }
                catch { Toast.Show("The clipboard is busy. Try again.", true); }
            }
        }
        if (s.Pin) PinImage(image, CaptureName(path), path, placeAt: where);

        if (s.Editor)
        {
            OpenEditor(image, path, CaptureName(path));
        }
        else if (s.Viewer)
        {
            OpenViewer(path, image);
        }
        else if (s.Preview)
        {
            string what = string.Join(" and ", new[] { s.Save ? "saved" : null, s.Copy ? "copied" : null, textCopied ? "text copied" : null }.Where(x => x != null));
            string caption = $"{image.PixelWidth} × {image.PixelHeight}" + (what.Length > 0 ? $"  ·  {char.ToUpper(what[0])}{what[1..]}" : "");
            CaptureNotice.ShowFor(image, path, caption,
                edit: () => OpenEditor(image, path, CaptureName(path)),
                pin: () => PinImage(image, CaptureName(path), path, placeAt: where),
                copy: () => CopyImage(image, quiet: false, path),
                openFolder: path != null ? () => ShowInFolder(path) : null,
                trace: () => SendToTrace(image));
        }
        else
        {
            string what = string.Join(" and ", new[] { s.Save ? "saved" : null, s.Copy ? "copied" : null, textCopied ? "text copied" : null }.Where(x => x != null));
            if (what.Length > 0) Toast.Show(char.ToUpper(what[0]) + what[1..] + ".");
        }
    }

    /// <summary>
    /// <see cref="SaveCapture"/> on a thread of its own, so the screen doesn't wait for the
    /// picture to be compressed. (WPF's encoders need a thread of the right kind: STA.)
    /// </summary>
    private async Task<string?> SaveCaptureAsync(BitmapSource image)
    {
        if (!image.IsFrozen)
        {
            if (!image.CanFreeze) return SaveCapture(image);
            image.Freeze();
        }
        string path = NewCapturePath(_settings.ImageFormat == "jpg" ? ".jpg" : ".png");
        long? limit = _settings.SizeLimit ? (long)(_settings.SizeLimitMb * 1024 * 1024) : null;
        int quality = _settings.JpegQuality;
        var done = new TaskCompletionSource<string?>();
        var thread = new Thread(() =>
        {
            try { done.SetResult(ImageFiles.TrySave(image, path, out string? error, quality, limit) ? null : error ?? "unknown error"); }
            catch (Exception ex) { done.SetResult(ex.Message); }
        }) { IsBackground = true, Name = "Overskin save" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (await done.Task is { } failed)
        {
            Toast.Show("Couldn't save the capture: " + failed, true);
            return null;
        }
        AddToHistory(path);
        return path;
    }

    // ---- file names ---------------------------------------------------------------------

    /// <summary>One small button per word that can go in a file name; a click puts it where the cursor is.</summary>
    private void BuildFileNameTokens()
    {
        if (FileNameTokens.Children.Count > 0) { UpdateFileNameExample(); return; }
        foreach (var (token, label, meaning) in FileNames.Tokens.Take(6))
        {
            var b = new Button { Content = label, ToolTip = $"{token}: {meaning}", Height = 22, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(0, 0, 4, 4), FontSize = 11.5 };
            b.SetResourceReference(StyleProperty, "Btn");
            string t = token;
            b.Click += (_, _) =>
            {
                int at = FileNameBox.CaretIndex;
                string text = FileNameBox.Text;
                // a space either side when it's next to other words
                string add = (at > 0 && text[at - 1] != ' ' && text[at - 1] != '\\' ? " " : "") + t;
                FileNameBox.Text = text.Insert(at, add);
                FileNameBox.CaretIndex = at + add.Length;
                FileNameBox.Focus();
            };
            FileNameTokens.Children.Add(b);
        }
        UpdateFileNameExample();
    }

    private void FileName_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        string pattern = FileNameBox.Text;
        _settings.FileNamePattern = string.IsNullOrWhiteSpace(pattern) ? FileNames.Default : pattern;
        FileNames.Pattern = _settings.FileNamePattern;
        UpdateFileNameExample();
        SaveSettings();
    }

    private void FileNameReset_Click(object sender, RoutedEventArgs e) => FileNameBox.Text = FileNames.Default;

    private void UpdateFileNameExample()
    {
        string ext = _settings.ImageFormat == "jpg" ? ".jpg" : ".png";
        FileNameExample.Text = "For example: " + FileNames.Example(_settings.FileNamePattern, ext);
        FileNameExample.ToolTip = FileNameExample.Text;
    }

    /// <summary>A new file in the screenshots folder, named by the pattern from the settings.</summary>
    private string NewCapturePath(string extension)
    {
        FileNames.Pattern = _settings.FileNamePattern;
        return FileNames.NewPath(_settings.SaveFolder, extension);
    }

    /// <summary>Writes a capture into the screenshots folder and adds it to the library.</summary>
    private string? SaveCapture(BitmapSource image)
    {
        string path = NewCapturePath(_settings.ImageFormat == "jpg" ? ".jpg" : ".png");
        long? limit = _settings.SizeLimit ? (long)(_settings.SizeLimitMb * 1024 * 1024) : null;
        if (!ImageFiles.TrySave(image, path, out string? error, _settings.JpegQuality, limit))
        {
            Toast.Show("Couldn't save the capture: " + error, true);
            return null;
        }
        AddToHistory(path);
        return path;
    }

    /// <summary>The picture, and the saved file too when there is one, so pasting into a chat keeps its name.</summary>
    private static void CopyImage(BitmapSource image, bool quiet, string? path = null)
    {
        bool ok;
        try { ok = FastClipboard.Copy(image, path); }
        catch { ok = false; }
        if (!ok)
        {
            // the slower way, which copes with anything
            if (path != null && File.Exists(path)) ok = ClipboardFiles.Copy(new[] { path }, image);
            else
            {
                try { Clipboard.SetImage(image); ok = true; }
                catch { ok = false; }
            }
        }
        if (!ok) Toast.Show("The clipboard is busy. Try again.", true);
        else if (!quiet) Toast.Show("Copied to the clipboard.");
    }

    private static void ShowInFolder(string path)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch { Toast.Show("Couldn't open the folder.", true); }
    }

    private void SendToTrace(BitmapSource image)
    {
        SelectTab(TabTraceIndex);
        LoadTraceImage(image);
        ShowFromTray();
    }

    private void OpenEditor(BitmapSource image, string? path, string name)
    {
        var editor = new EditorWindow(image, path, name, new EditorHost
        {
            SaveNew = SaveCapture,
            Pin = (bmp, n) => PinImage(bmp, n),
            Trace = SendToTrace,
            Saved = p => { AddToHistory(p); RefreshLibrary(); },
            Folder = _settings.SaveFolder,
        });
        editor.Show();
        editor.Activate();
    }

    // ===================================================================================
    //  screen tools
    // ===================================================================================

    private void SnipPin_Click(object sender, RoutedEventArgs e) => SnipAndPin();
    private void TextFromScreen_Click(object sender, RoutedEventArgs e) => TextFromScreen();
    private void ColourPicker_Click(object sender, RoutedEventArgs e) => PickScreenColour();
    private void Ruler_Click(object sender, RoutedEventArgs e) => ShowRuler();

    /// <summary>Drag over part of the screen; a copy of it is pinned right where it was.</summary>
    private void SnipAndPin() => Guarded(async () =>
    {
        var chosen = await Choose(OverlayMode.Region, "Drag over what you want to pin, or click a window.  Esc cancels.", useDelay: false, tools: true);
        if (chosen is not { } c) return;
        Reappear(c.WasShown);
        if (c.Overlay.Selection is not { } r) return;
        PinImage(c.Overlay.Crop(r), $"Snip {DateTime.Now:HH.mm.ss}", placeAt: r);
        Toast.Show("Pinned. Drag it anywhere, or right-click it for options.");
    });

    private void TextFromScreen() => Guarded(async () =>
    {
        var chosen = await Choose(OverlayMode.Region, "Drag over the text or QR code you want to copy.  Esc cancels.", useDelay: false);
        if (chosen is not { } c) return;
        Reappear(c.WasShown);
        if (c.Overlay.Selection is not { } r) return;

        // a QR code or barcode there: copy what it says
        var crop = c.Overlay.Crop(r);
        if (QrReader.Read(crop) is { } code)
        {
            try { Clipboard.SetText(code); } catch { }
            new TextResultWindow(code, qr: true).Show();
            return;
        }

        Toast.ShowSticky("Reading text…");
        string? text = await TextRecognizer.RecognizeAsync(crop);
        Toast.Hide();
        if (text == null)
        {
            Toast.Show("Windows has no text-recognition language installed. Add one under Settings › Time & language › Language.", true);
            return;
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            Toast.Show("No text found there.");
            return;
        }
        try { Clipboard.SetText(text); } catch { }
        new TextResultWindow(text).Show();
    });

    private void PickScreenColour() => Guarded(async () =>
    {
        var chosen = await Choose(OverlayMode.Colour, "Click any pixel to copy its colour.  Esc cancels.", useDelay: false);
        if (chosen is not { } c) return;
        Reappear(c.WasShown);
        if (c.Overlay.PickedColour is not { } colour) return;
        string hex = Theming.ToHex(colour);
        try { Clipboard.SetText(hex); } catch { }
        Toast.ShowColour($"Copied {hex}   ·   rgb({colour.R}, {colour.G}, {colour.B})", colour);
    });

    private void ShowRuler() => Guarded(async () =>
    {
        var chosen = await Choose(OverlayMode.Ruler, "Drag to measure. Drag again for a new measurement.  Esc closes.", useDelay: false);
        if (chosen is { } c) Reappear(c.WasShown);
    });

    private void OpenInEditor_Click(object sender, RoutedEventArgs e)
    {
        string? file = BrowseForImage("Open an image to mark up");
        if (file != null && TryLoadFile(file, out var image)) OpenEditor(image!, file, Path.GetFileName(file));
    }

    private void EditClipboard_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var image = ImageLoader.FromClipboard(out _);
            if (image == null) Toast.Show("There's no picture on the clipboard.");
            else OpenEditor(image, null, "Clipboard");
        }
        catch (Exception ex)
        {
            Toast.Show(ex.Message, true);
        }
    }

    // ===================================================================================
    //  capture settings
    // ===================================================================================

    private void ApplyCaptureSettingsToControls()
    {
        AfterCopySwitch.IsChecked = _settings.AfterCopy;
        AutoHideSwitch.IsChecked = _settings.AutoHide;
        Redaction.Kinds = _settings.AutoHideKinds;
        WindowAlphaSwitch.IsChecked = _settings.WindowAlpha;
        SyncVideoControls();
        SyncWatermarkControls();
        RefreshWorkflowRows();
        AfterSaveSwitch.IsChecked = _settings.AfterSave;
        AfterEditorSwitch.IsChecked = _settings.AfterEditor;
        AfterViewerSwitch.IsChecked = _settings.AfterViewer && !_settings.AfterEditor;
        AfterPinSwitch.IsChecked = _settings.AfterPin;
        AfterPreviewSwitch.IsChecked = _settings.AfterPreview;
        AfterCursorSwitch.IsChecked = _settings.AfterCursor;
        FormatPng.IsChecked = _settings.ImageFormat != "jpg";
        FormatJpg.IsChecked = _settings.ImageFormat == "jpg";
        JpegQualitySlider.Value = _settings.JpegQuality;
        SizeLimitSwitch.IsChecked = _settings.SizeLimit;
        SizeLimitSlider.Value = _settings.SizeLimitMb;
        UpdateCompressionRows();
        foreach (var chip in new[] { Delay0, Delay3, Delay5, Delay10 })
            chip.IsChecked = (string)chip.Tag == _settings.CaptureDelay.ToString();
        SaveFolderText.Text = _settings.SaveFolder;
        SaveFolderText.ToolTip = _settings.SaveFolder;
        FileNames.Pattern = _settings.FileNamePattern;
        if (FileNameBox.Text != _settings.FileNamePattern) FileNameBox.Text = _settings.FileNamePattern;
        BuildFileNameTokens();
        AfterPreviewSwitch.IsEnabled = !_settings.AfterEditor && !_settings.AfterViewer;
    }

    private void CaptureSwitch_Click(object sender, RoutedEventArgs e)
    {
        _settings.AfterCopy = AfterCopySwitch.IsChecked == true;
        _settings.AfterSave = AfterSaveSwitch.IsChecked == true;
        // the viewer and the editor both show the picture: one or the other
        if (sender == AfterEditorSwitch && AfterEditorSwitch.IsChecked == true) AfterViewerSwitch.IsChecked = false;
        if (sender == AfterViewerSwitch && AfterViewerSwitch.IsChecked == true) AfterEditorSwitch.IsChecked = false;
        _settings.AfterEditor = AfterEditorSwitch.IsChecked == true;
        _settings.AfterViewer = AfterViewerSwitch.IsChecked == true;
        _settings.AfterPin = AfterPinSwitch.IsChecked == true;
        _settings.AfterPreview = AfterPreviewSwitch.IsChecked == true;
        _settings.AfterCursor = AfterCursorSwitch.IsChecked == true;
        _settings.AutoHide = AutoHideSwitch.IsChecked == true;
        _settings.WindowAlpha = WindowAlphaSwitch.IsChecked == true;
        AfterPreviewSwitch.IsEnabled = !_settings.AfterEditor && !_settings.AfterViewer; // they already show the picture
        SaveSettings();
    }

    private void Delay_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not RadioButton { Tag: string tag } || !int.TryParse(tag, out int seconds)) return;
        _settings.CaptureDelay = seconds;
        SaveSettings();
    }

    private void Format_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not RadioButton { Tag: string tag }) return;
        _settings.ImageFormat = tag;
        UpdateCompressionRows();
        SaveSettings();
    }

    private void Compression_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || JpegQualitySlider == null || SizeLimitSlider == null) return;
        _settings.JpegQuality = (int)JpegQualitySlider.Value;
        _settings.SizeLimit = SizeLimitSwitch.IsChecked == true;
        _settings.SizeLimitMb = SizeLimitSlider.Value;
        UpdateCompressionRows();
        SaveSettings();
    }

    private void CompressionSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => Compression_Changed(sender, e);

    private void UpdateCompressionRows()
    {
        if (JpegQualityRow == null || SizeLimitText == null) return;
        JpegQualityRow.Visibility = _settings.ImageFormat == "jpg" ? Visibility.Visible : Visibility.Collapsed;
        JpegQualityText.Text = $"{_settings.JpegQuality}%";
        SizeLimitText.Text = $"{_settings.SizeLimitMb:0.#} MB";
        SizeLimitSlider.IsEnabled = _settings.SizeLimit;
    }

    private void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Where should captures be saved?",
            InitialDirectory = Directory.Exists(_settings.SaveFolder) ? _settings.SaveFolder : null,
        };
        if (dialog.ShowDialog(this) != true) return;
        _settings.SaveFolder = dialog.FolderName;
        ApplyCaptureSettingsToControls();
        SaveSettings();
    }

    // ===================================================================================
    //  library
    // ===================================================================================

    private bool _libraryRefreshQueued;

    private void AddToHistory(string path)
    {
        _settings.History.RemoveAll(h => string.Equals(h.Path, path, StringComparison.OrdinalIgnoreCase));
        _settings.History.Add(new HistoryEntry { Path = path, Time = DateTime.Now });
        if (_settings.History.Count > 500) _settings.History.RemoveRange(0, _settings.History.Count - 500);
        // the library is rebuilt once things are quiet, so a capture's copy and preview never wait for it
        if (!_libraryRefreshQueued)
        {
            _libraryRefreshQueued = true;
            Dispatcher.BeginInvoke(() => { _libraryRefreshQueued = false; RefreshLibrary(); }, System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private static string Friendly(DateTime t)
    {
        var today = DateTime.Today;
        if (t.Date == today) return $"Today, {t:HH:mm}";
        if (t.Date == today.AddDays(-1)) return $"Yesterday, {t:HH:mm}";
        return t.Year == today.Year ? t.ToString("d MMM, HH:mm") : t.ToString("d MMM yyyy");
    }

    /// <summary>Rebuilds the library from the history, newest first, loading thumbnails in the background.</summary>
    private string _libraryFilter = "All";

    private static bool IsGif(string path) => Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase);

    private void LibraryFilter_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag }) return;
        _libraryFilter = tag;
        if (_ready) RefreshLibrary();
    }

    private readonly SearchIndex _search = new();
    private bool _searchHooked;

    private void LibrarySearch_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        LibrarySearchHint.Visibility = LibrarySearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_ready) RefreshLibrary();
    }

    private void UpdateIndexing()
    {
        int pending = _search.Pending;
        LibraryIndexing.Text = pending > 0 ? $"Reading text… {pending} left" : "";
    }

    private void RefreshLibrary()
    {
        _settings.History.RemoveAll(h => !File.Exists(h.Path));
        if (!_searchHooked)
        {
            _searchHooked = true;
            // new text was read: refresh the results if a search is showing, and the progress either way
            _search.Updated += () => Dispatcher.BeginInvoke(() =>
            {
                UpdateIndexing();
                if (LibrarySearch.Text.Trim().Length > 0) RefreshLibrary();
            });
        }
        _search.Add(_settings.History.Select(h => h.Path));
        UpdateIndexing();
        string query = LibrarySearch.Text.Trim();
        var entries = Sorted(_settings.History.AsEnumerable().Reverse())
            .Where(h => _libraryFilter switch
            {
                "GIFs" => IsGif(h.Path),
                "Videos" => IsVideo(h.Path),
                "Pictures" => !IsGif(h.Path) && !IsVideo(h.Path),
                _ => true,
            })
            .Where(h => query.Length == 0 || _search.Matches(h.Path, Friendly(h.Time), query))
            .ToList();

        var existing = _library.ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);
        _library.Clear();
        foreach (var h in entries)
        {
            if (existing.TryGetValue(h.Path, out var kept) && kept.Time == h.Time)
            {
                _library.Add(kept);
                continue;
            }
            var item = new LibraryItem { Path = h.Path, Time = h.Time, Caption = Friendly(h.Time) };
            _library.Add(item);
            _ = IsVideo(item.Path) ? LoadVideoThumbAsync(item) : LoadThumbAsync(item);
        }

        UpdateLibraryCount();
        LibraryEmpty.Visibility = _library.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LibraryEmptyTitle.Text = query.Length > 0 ? "Nothing matches"
            : _libraryFilter switch { "GIFs" => "No GIFs", "Videos" => "No videos", "Pictures" => "No pictures", _ => "No captures yet" };
        LibraryEmptyHint.Text = query.Length > 0 ? (_search.Pending > 0 ? "Some captures haven't been read yet." : "")
            : _libraryFilter == "All" ? $"Press {_settings.HotkeyRegion}, or click Region on the Capture tab." : "";
    }

    private static async Task LoadThumbAsync(LibraryItem item)
    {
        try
        {
            var (thumb, w, h, bytes) = await Task.Run(() =>
            {
                using var stream = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                int pw = decoder.Frames[0].PixelWidth, ph = decoder.Frames[0].PixelHeight;
                stream.Position = 0;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 300;
                bmp.StreamSource = stream;
                bmp.EndInit();
                bmp.Freeze();
                return (bmp, pw, ph, new FileInfo(item.Path).Length);
            });
            item.Thumb = thumb;
            item.Details = $"{w} × {h}  ·  {(bytes >= 1 << 20 ? $"{bytes / (1024.0 * 1024):0.#} MB" : $"{Math.Max(1, bytes / 1024)} KB")}";
            item.Raise(nameof(LibraryItem.Details));
        }
        catch
        {
            item.Details = "Couldn't read this file";
            item.Raise(nameof(LibraryItem.Details));
        }
    }

    private static LibraryItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as LibraryItem;

    private BitmapSource? LoadLibraryImage(LibraryItem item)
    {
        if (!File.Exists(item.Path))
        {
            Toast.Show("That file has been moved or deleted.");
            RefreshLibrary();
            return null;
        }
        if (IsVideo(item.Path))
        {
            Toast.Show("That's a video. Click it to play or trim it.");
            return null;
        }
        return TryLoadFile(item.Path, out var image) ? image : null;
    }

    private void LibraryOpen_Click(object sender, RoutedEventArgs e)
    {
        // Ctrl+click starts picking several
        if (!_combining && sender is Button && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) SetCombining(true);
        if (_combining && sender is Button && ItemOf(sender) is { } pick)
        {
            pick.IsSelected = !pick.IsSelected;
            _pickOrder.Remove(pick);
            if (pick.IsSelected) _pickOrder.Add(pick);
            UpdateCombineBar();
            return;
        }
        // pictures, GIFs and videos all open in the viewer, which goes through what the Library shows
        if (ItemOf(sender) is { } item)
        {
            var paths = _library.Select(i => (string?)i.Path).ToList();
            var viewer = new ViewerWindow(paths, Math.Max(0, paths.IndexOf(item.Path)), ViewerHostFor());
            viewer.PlaceBeside(this);
            viewer.Show();
            viewer.Activate();
        }
    }

    /// <summary>What the viewer can ask for.</summary>
    private ViewerHost ViewerHostFor() => new()
    {
        Edit = (image, path, name) => OpenEditor(image, path, name),
        EditVideo = OpenVideo,
        EditGif = OpenClipFile,
        Pin = (image, name, path) => PinImage(image, name, path),
        ShowInFolder = ShowInFolder,
        Changed = RefreshLibrary,
        Matches = (path, query) => _search.Matches(path, Friendly(File.Exists(path) ? File.GetLastWriteTime(path) : DateTime.MinValue), query),
    };

    /// <summary>
    /// Opens a capture in the viewer, with the rest of the Library either side of it; an unsaved
    /// capture is shown on its own.
    /// </summary>
    private void OpenViewer(string? path, BitmapSource image)
    {
        List<string?> paths;
        int index = 0;
        if (path != null)
        {
            paths = _settings.History.AsEnumerable().Reverse().Select(h => (string?)h.Path).Where(p => File.Exists(p)).Distinct().ToList();
            index = paths.IndexOf(path);
            if (index < 0) { paths.Insert(0, path); index = 0; }
        }
        else paths = new List<string?> { null };
        var viewer = new ViewerWindow(paths, index, ViewerHostFor(), path == null ? image : null);
        viewer.PlaceBeside(this);
        viewer.Show();
        viewer.Activate();
    }

    /// <summary>Right-click → Convert to: makes a copy in another format next to the original.</summary>
    private async void LibraryConvert_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: string target } || ItemOf(sender) is not { } item) return;
        if (!File.Exists(item.Path))
        {
            Toast.Show("That file has been moved or deleted.");
            return;
        }
        Toast.ShowSticky($"Converting to {target}…");
        try
        {
            string made = await Converter.ConvertAsync(item.Path, target);
            AddToHistory(made);
            Toast.Show($"Made {Path.GetFileName(made)}.");
        }
        catch (Exception ex)
        {
            Toast.Show(ex is InvalidOperationException ? ex.Message : "Couldn't convert it: " + ex.Message, ex is not InvalidOperationException);
        }
    }

    private void LibraryCopy_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        if (!File.Exists(item.Path))
        {
            Toast.Show("That file has been moved or deleted.");
            return;
        }
        // the file itself (same name, GIFs keep moving), plus the pixels for still pictures
        var image = ClipboardFiles.IsStill(item.Path) && TryLoadFile(item.Path, out var still) ? still : null;
        Toast.Show(ClipboardFiles.Copy(new[] { item.Path }, image) ? $"Copied {Path.GetFileName(item.Path)}." : "The clipboard is busy. Try again.",
            warning: false);
    }

    private void LibraryPin_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item && LoadLibraryImage(item) is { } image) PinImage(image, Path.GetFileName(item.Path), item.Path);
    }

    private void LibraryTrace_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item && LoadLibraryImage(item) is { } image) SendToTrace(image);
    }

    private void LibraryShow_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) ShowInFolder(item.Path);
    }

    private void LibraryDelete_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        try
        {
            // to the Recycle Bin, so a slip of the mouse is easy to undo
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(item.Path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            if (IsVideo(item.Path)) ZoomTrack.DeleteFor(item.Path);
            Toast.Show("Moved to the Recycle Bin.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Toast.Show("Couldn't delete it: " + ex.Message, true);
        }
        RefreshLibrary();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_settings.SaveFolder);
            System.Diagnostics.Process.Start("explorer.exe", $"\"{_settings.SaveFolder}\"");
        }
        catch
        {
            Toast.Show("Couldn't open the folder.", true);
        }
    }
}
