using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Overskin;

/// <summary>
/// Folding settings cards on the Capture tab, the Library's count and the space it uses, and
/// clearing out old captures.
/// </summary>
public partial class MainWindow
{
    // ===================================================================================
    //  folding cards
    // ===================================================================================

    private readonly List<CardFold> _folds = new();

    /// <summary>
    /// The settings under the capture buttons fold away to their headings, each with a short
    /// summary, so the page leads with what you capture rather than with dozens of switches.
    /// </summary>
    private void SetUpFolds()
    {
        void Fold(Border card, string key, Func<string> describe) =>
            _folds.Add(CardFold.Make(card, _settings.OpenCards.Contains(key), describe, open =>
            {
                _settings.OpenCards.Remove(key);
                if (open) _settings.OpenCards.Add(key);
                _settings.Save();
            }));

        Fold(CardAfter, "after", AfterSummary);
        Fold(CardWatermark, "watermark", WatermarkSummary);
        Fold(CardVideo, "video", VideoSummary);
        Fold(CardSound, "sound", SoundSummary);
        Fold(CardWebcam, "webcam", WebcamSummary);
        Fold(CardPointer, "pointer", PointerSummary);
        Fold(CardZoom, "zoom", ZoomSummary);
        PageCapture.IsVisibleChanged += (_, _) => { if (PageCapture.IsVisible) RefreshFoldSummaries(); };
    }

    private void RefreshFoldSummaries()
    {
        foreach (var fold in _folds) fold.Refresh();
    }

    /// <summary>For the self-test: each card's summary as shown while folded.</summary>
    internal List<string> FoldSummariesForTest() => new()
    {
        AfterSummary(), WatermarkSummary(), VideoSummary(), SoundSummary(), WebcamSummary(), PointerSummary(), ZoomSummary(),
    };

    internal int FoldCountForTest => _folds.Count;

    private string AfterSummary()
    {
        var s = _settings;
        var parts = new List<string>();
        if (s.AfterCopy) parts.Add("Copy");
        if (s.AfterSave) parts.Add("Save " + s.ImageFormat.ToUpperInvariant());
        if (s.AfterPreview) parts.Add("Preview");
        if (s.AfterViewer) parts.Add("Viewer");
        if (s.AfterEditor) parts.Add("Editor");
        if (s.AfterPin) parts.Add("Pin");
        if (s.AutoHide) parts.Add("Hide private");
        return parts.Count == 0 ? "Nothing" : string.Join(" · ", parts);
    }

    private string WatermarkSummary()
    {
        var w = _settings.Watermark;
        return (w.OnScreenshots, w.OnVideos) switch
        {
            (true, true) => "On screenshots and videos",
            (true, false) => "On screenshots",
            (false, true) => "On videos",
            _ => "Off",
        };
    }

    private string VideoSummary()
    {
        var v = _settings.Video;
        string text = $"{v.Fps} fps · {(v.HighQuality ? "Sharper" : "Smaller file")}";
        if (v.Backdrop != "None") text += " · Backdrop";
        if (v.ShowKeys) text += " · Shortcuts";
        return text;
    }

    private string SoundSummary()
    {
        var v = _settings.Video;
        return (v.SystemSound, v.Microphone) switch
        {
            (true, true) => "System sound and microphone",
            (true, false) => "System sound",
            (false, true) => "Microphone",
            _ => "Silent",
        };
    }

    private string WebcamSummary() =>
        _settings.Video.Webcam ? $"{Spaced(_settings.Video.WebcamCorner)} · {_settings.Video.WebcamShape}" : "Off";

    private string PointerSummary()
    {
        var v = _settings.Video;
        if (!v.Pointer) return "Hidden";
        var parts = new List<string> { v.PointerStyle == "System" ? "Windows pointer" : v.PointerStyle };
        if (v.SmoothCursor) parts.Add("Smooth");
        if (v.ClickRings) parts.Add("Rings");
        return string.Join(" · ", parts);
    }

    private string ZoomSummary()
    {
        var v = _settings.Video;
        return v.AutoZoom ? $"{v.Zoom:0.0}×{(v.EditableZooms ? " · Editable" : "")}" : "Off";
    }

    /// <summary>"BottomRight" → "Bottom right".</summary>
    private static string Spaced(string name)
    {
        var words = Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " ");
        return words.Length == 0 ? words : words[0] + words[1..].ToLowerInvariant();
    }

    // ===================================================================================
    //  library: count, space used and clearing out old captures
    // ===================================================================================

    private readonly Dictionary<string, (DateTime Written, long Length)> _sizes = new(StringComparer.OrdinalIgnoreCase);

    private long SizeOf(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return 0;
            if (_sizes.TryGetValue(path, out var known) && known.Written == info.LastWriteTimeUtc) return known.Length;
            _sizes[path] = (info.LastWriteTimeUtc, info.Length);
            return info.Length;
        }
        catch { return 0; }
    }

    internal static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (1024.0 * 1024 * 1024):0.0} GB",
        >= 1L << 20 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{Math.Max(1, bytes / 1024)} KB",
    };

    /// <summary>"12 captures · 48 MB", for what the Library is showing.</summary>
    private void UpdateLibraryCount()
    {
        int n = _library.Count;
        if (n == 0)
        {
            LibraryCount.Text = "";
            LibraryCount.ToolTip = null;
            return;
        }
        long shown = _library.Sum(i => SizeOf(i.Path));
        LibraryCount.Text = $"{n} capture{(n == 1 ? "" : "s")} · {Bytes(shown)}";
        long all = _settings.History.Sum(h => SizeOf(h.Path));
        LibraryCount.ToolTip = all == shown ? "The space these captures take up"
            : $"These take up {Bytes(shown)}. All your captures take up {Bytes(all)}.";
    }

    private static readonly int[] CleanUpChoices = { 0, 30, 90, 365 };

    private static string CleanUpName(int days) => days switch
    {
        0 => "Never",
        365 => "1 year",
        _ => $"{days} days",
    };

    private readonly DispatcherTimer _cleanUpTimer = new() { Interval = TimeSpan.FromHours(6) };

    private void SetUpCleanUp()
    {
        CleanUpChoicesPanel.Children.Clear();
        foreach (int days in CleanUpChoices)
        {
            var chip = new RadioButton
            {
                Style = (Style)FindResource("Seg"),
                GroupName = "CleanUp",
                Content = CleanUpName(days),
                Tag = days,
                MinWidth = 0,
                IsChecked = _settings.CleanUpDays == days,
            };
            chip.Click += CleanUpChoice_Click;
            CleanUpChoicesPanel.Children.Add(chip);
        }
        UpdateCleanUpText();
        _cleanUpTimer.Tick += (_, _) => CleanUpOldCaptures(ask: false);
        _cleanUpTimer.Start();
        // shortly after start-up, once the window has settled
        Dispatcher.BeginInvoke(() => CleanUpOldCaptures(ask: false), DispatcherPriority.ApplicationIdle);
    }

    private void UpdateCleanUpText()
    {
        long all = _settings.History.Where(h => File.Exists(h.Path)).Sum(h => SizeOf(h.Path));
        int count = _settings.History.Count(h => File.Exists(h.Path));
        CleanUpUsage.Text = count == 0 ? "No captures yet"
            : $"{count} capture{(count == 1 ? "" : "s")} take up {Bytes(all)}";
    }

    private void CleanUpChoice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: int days }) return;
        int before = _settings.CleanUpDays;
        _settings.CleanUpDays = days;
        if (days > 0 && !CleanUpOldCaptures(ask: true))
        {
            // they said no: back to what it was
            _settings.CleanUpDays = before;
            foreach (RadioButton chip in CleanUpChoicesPanel.Children) chip.IsChecked = (int)chip.Tag == before;
        }
        _settings.Save();
    }

    /// <summary>
    /// Captures past the chosen age, inside the save folder only (so nothing else you opened in
    /// Overskin is touched) and not pinned on screen.
    /// </summary>
    internal List<string> OldCaptures(DateTime now)
    {
        if (_settings.CleanUpDays <= 0) return new();
        var cutoff = now.AddDays(-_settings.CleanUpDays);
        string folder;
        try { folder = Path.GetFullPath(_settings.SaveFolder).TrimEnd('\\') + "\\"; }
        catch { return new(); }
        var pinned = new HashSet<string>(_images.Select(i => i.SourcePath).Where(p => !string.IsNullOrEmpty(p))!, StringComparer.OrdinalIgnoreCase);
        return _settings.History
            .Where(h => h.Time < cutoff)
            .Select(h => h.Path)
            .Where(p =>
            {
                try { return Path.GetFullPath(p).StartsWith(folder, StringComparison.OrdinalIgnoreCase) && File.Exists(p) && !pinned.Contains(p); }
                catch { return false; }
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Moves old captures to the Recycle Bin; false only when asked and told no.</summary>
    private bool CleanUpOldCaptures(bool ask)
    {
        var old = OldCaptures(DateTime.Now);
        if (old.Count == 0)
        {
            if (ask) Toast.Show($"Nothing older than {CleanUpName(_settings.CleanUpDays)} yet. Older captures will go to the Recycle Bin from now on.");
            UpdateCleanUpText();
            return true;
        }
        if (ask)
        {
            long size = old.Sum(SizeOf);
            int answer = AskDialog.Ask(this, $"Move {old.Count} capture{(old.Count == 1 ? "" : "s")} to the Recycle Bin?",
                $"They're older than {CleanUpName(_settings.CleanUpDays)} and take up {Bytes(size)}. From now on, captures will go to the Recycle Bin once they're that old. You can get them back from the Recycle Bin.",
                "IcTrash", ("Cancel", false), ("Move to the Recycle Bin", true));
            if (answer != 1) return false;
        }
        int moved = 0;
        foreach (var path in old)
        {
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                if (IsVideo(path)) ZoomTrack.DeleteFor(path);
                moved++;
            }
            catch { }
        }
        RefreshLibrary();
        UpdateCleanUpText();
        if (moved > 0) Toast.Show($"Moved {moved} old capture{(moved == 1 ? "" : "s")} to the Recycle Bin.");
        return true;
    }

#if DEBUG
    /// <summary>Checks for the folding cards, hotkey groups, tray and clean-up, written next to the snapshots.</summary>
    private async Task WriteTidyChecksAsync(string folder)
    {
        var log = new List<string>();
        void Check(string name, Func<string> run)
        {
            try { log.Add(name + ": " + run()); }
            catch (Exception ex) { log.Add(name + ": FAILED " + ex.Message); }
        }

        Check("folds", () => $"{FoldCountForTest} cards (expect 7); summaries: {string.Join(" | ", FoldSummariesForTest())}");
        Check("fold toggle", () =>
        {
            var fold = _folds[0];
            bool was = fold.IsOpen;
            fold.Toggle();
            bool open = fold.IsOpen, saved = _settings.OpenCards.Contains("after");
            fold.Toggle();
            return $"opens={open != was} remembered={saved == open} back={fold.IsOpen == was} (expect True True True)";
        });
        Check("hotkey groups", () =>
            $"recording pause={HkRecPauseButton.Content} pen={HkRecPenButton.Content} video tile={TileVideoKey.Text} hint=\"{RecordHint.Text}\" " +
            $"(expect {_settings.HotkeyPause} {_settings.HotkeyDraw} {_settings.HotkeyVideo})");
        Check("tray quick actions", () => string.Join(" | ", QuickActionsForTest()));

        Check("clean-up picks", () =>
        {
            string temp = Path.Combine(Path.GetTempPath(), "overskin-cleanup-test");
            string outside = Path.Combine(Path.GetTempPath(), "overskin-cleanup-outside");
            Directory.CreateDirectory(temp);
            Directory.CreateDirectory(outside);
            string oldIn = Path.Combine(temp, "old.png"), newIn = Path.Combine(temp, "new.png"), oldOut = Path.Combine(outside, "old.png");
            foreach (var f in new[] { oldIn, newIn, oldOut }) File.WriteAllBytes(f, new byte[10]);
            var (folderWas, daysWas, historyWas) = (_settings.SaveFolder, _settings.CleanUpDays, _settings.History);
            try
            {
                _settings.SaveFolder = temp;
                _settings.History = new List<HistoryEntry>
                {
                    new() { Path = oldIn, Time = DateTime.Now.AddDays(-40) },
                    new() { Path = newIn, Time = DateTime.Now.AddDays(-5) },
                    new() { Path = oldOut, Time = DateTime.Now.AddDays(-40) },
                };
                _settings.CleanUpDays = 0;
                int never = OldCaptures(DateTime.Now).Count;
                _settings.CleanUpDays = 30;
                var picked = OldCaptures(DateTime.Now);
                return $"never={never} after30={picked.Count} which={string.Join(",", picked.Select(Path.GetFileName))} (expect 0 1 old.png, inside the folder only)";
            }
            finally
            {
                (_settings.SaveFolder, _settings.CleanUpDays, _settings.History) = (folderWas, daysWas, historyWas);
                try { Directory.Delete(temp, true); Directory.Delete(outside, true); } catch { }
            }
        });
        // the preview card pops up: small, low and see-through at first, then full size in place
        {
            var pixel = System.Windows.Media.Imaging.BitmapSource.Create(64, 40, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[64 * 40 * 4], 64 * 4);
            CaptureNotice.ShowFor(pixel, null, "test", () => { }, () => { }, () => { }, null);
            await Task.Delay(40);
            var early = CaptureNotice.StateForTest;
            await Task.Delay(700);
            var late = CaptureNotice.StateForTest;
            CaptureNotice.CloseAll();
            log.Add($"preview pop-up: early scale={early?.Scale:0.00} rise={early?.Rise:0} opacity={early?.Opacity:0.00}; " +
                    $"settled scale={late?.Scale:0.00} rise={late?.Rise:0} opacity={late?.Opacity:0.00} (expect early smaller/lower/fainter, settled 1.00 0 1.00)");

            // swiping the card: a short slow push springs back, far left opens the viewer, a
            // quick flick right dismisses it, and with nothing to open it won't go far left
            int viewed = 0;
            async Task<CaptureNotice?> Card(bool canView)
            {
                CaptureNotice.ShowFor(pixel, null, "test", () => { }, () => { }, () => { }, null, canView ? () => viewed++ : null);
                await Task.Delay(600);
                return CaptureNotice.CurrentForTest;
            }
            var c1 = await Card(true);
            double home = c1?.Left ?? 0;
            c1?.BeginSwipe(0); c1?.MoveSwipe(-20, 50); c1?.MoveSwipe(-50, 400);
            int back = c1?.EndSwipe(600) ?? 9;
            await Task.Delay(400);
            double backLeft = (c1?.Left ?? 0) - home;
            bool stillOpen = c1?.IsVisible == true;
            c1?.BeginSwipe(0); c1?.MoveSwipe(-60, 100); c1?.MoveSwipe(-110, 300);
            int open = c1?.EndSwipe(500) ?? 9;
            var c2 = await Card(true);
            c2?.BeginSwipe(0); c2?.MoveSwipe(12, 16); c2?.MoveSwipe(40, 48);
            int flick = c2?.EndSwipe(60) ?? 9;
            await Task.Delay(400);
            bool flickClosed = c2?.IsVisible != true;
            var c3 = await Card(false);
            double home3 = c3?.Left ?? 0;
            c3?.BeginSwipe(0); c3?.MoveSwipe(-300, 200);
            double lean = (c3?.Left ?? 0) - home3;
            int none = c3?.EndSwipe(500) ?? 9;
            CaptureNotice.CloseAll();
            log.Add($"preview swipe: short={back} back-at={backLeft:0} open={stillOpen}; far-left={open} viewed={viewed}; " +
                    $"flick-right={flick} closed={flickClosed}; no-viewer lean={lean:0} result={none} " +
                    "(expect 0 0 True; -1 1; 1 True; lean -30 result 0)");
        }
        Check("library count", () => $"\"{LibraryCount.Text}\" usage \"{CleanUpUsage.Text}\"");

        // the update banner, as it shows when a newer version is out
        SelectTab(TabCaptureIndex);
        var shown = ShowUpdateForTest(new Release(new Version(3, 2, 0), "v3.2.0", "https://github.com/overskin/Overskin/releases/tag/v3.2.0",
            "https://github.com/overskin/Overskin/releases/download/v3.2.0/OverskinSetup.exe", 1));
        log.Add($"update banner: shown={shown.Shown} \"{shown.Title}\" \"{shown.Detail}\"");
        await Task.Delay(500);
        var banner = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(ActualWidth * 2), (int)(ActualHeight * 2), 192, 192, System.Windows.Media.PixelFormats.Pbgra32);
        banner.Render((FrameworkElement)Content);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
        png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(banner));
        using (var file = File.Create(Path.Combine(folder, "update-banner.png"))) png.Save(file);
        UpdateBanner.Visibility = Visibility.Collapsed;

        File.WriteAllLines(Path.Combine(folder, "checks.txt"), log);
    }
#endif
}
