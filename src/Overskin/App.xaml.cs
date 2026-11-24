using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Overskin;

public partial class App : Application
{
    private const string ActivateEventName = "Overskin.Activate.6f0b7b1e";

    /// <summary>"Overskin.exe --run region" runs one command (used by the taskbar jump list).</summary>
    public const string RunArgument = "--run";

    /// <summary>Where a second copy leaves its command for the running one.</summary>
    private static string PendingCommandFile => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Overskin.command");

    /// <summary>True for developer runs: suppresses start-up toasts.</summary>
    public static bool Quiet { get; private set; }

    private Mutex? _singleInstance;
    private MainWindow? _main;

    private EventWaitHandle? _activateSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Developer runs (with arguments) get their own instance name so they never disturb a real one.
        bool developerRun = e.Args.Length > 0 && e.Args[0] is "--snapshot" or "--selftest" or "--smoke";
        string scope = developerRun ? ".dev" : "";
        Quiet = developerRun;
        // the GitHub copy is started at sign-in with --tray; the Store copy by its startup task
        bool startInTray = e.Args.Contains(StartupRegistration.TrayArgument) || (!developerRun && StartupRegistration.LaunchedAtSignIn);
        string activateName = ActivateEventName + scope;
        int run = Array.IndexOf(e.Args, RunArgument);
        string? command = run >= 0 && run + 1 < e.Args.Length ? e.Args[run + 1] : null;

        // Two copies would fight over the same hotkeys and the same windows.
        _singleInstance = new Mutex(true, "Overskin.SingleInstance.6f0b7b1e" + scope, out bool first);
        if (!first)
        {
            // Already running: ask that copy to come forward instead of starting a second one.
            try
            {
                if (command != null) System.IO.File.WriteAllText(PendingCommandFile, command);
                using var signal = EventWaitHandle.OpenExisting(activateName);
                signal.Set();
            }
            catch
            {
                MessageBox.Show("Overskin is already running. Look for its icon in the notification area.",
                    "Overskin", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            Shutdown();
            return;
        }

        _activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, activateName);
        var listener = new Thread(() =>
        {
            while (_activateSignal.WaitOne())
                Dispatcher.BeginInvoke(() =>
                {
                    string? pending = null;
                    try
                    {
                        if (System.IO.File.Exists(PendingCommandFile))
                        {
                            pending = System.IO.File.ReadAllText(PendingCommandFile).Trim();
                            System.IO.File.Delete(PendingCommandFile);
                        }
                    }
                    catch { }
                    if (pending == null || _main?.RunCommand(pending) != true) _main?.BringForward();
                });
        })
        { IsBackground = true, Name = "Overskin activation listener" };
        listener.Start();

        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) CrashLog.Write(ex, "background thread");
            _main?.Cleanup();
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLog.Write(args.Exception, "background task (recovered)");
            args.SetObserved();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => _main?.Cleanup();
        SessionEnding += (_, _) => _main?.Cleanup();

#if DEBUG
        // Developer aid: "Overskin.exe --selftest <logfile>" runs the window manager against a fresh Notepad.
        if (e.Args.Length >= 2 && e.Args[0] == "--selftest" && e.Args.Contains("--more"))
        {
            // just the newest tests, for quick checks while working on them
            var quick = new List<string>();
            if (e.Args.Contains("--windows")) { try { WindowList(quick); } catch (Exception ex) { quick.Add("EXCEPTION " + ex); } }
            else if (e.Args.Contains("--round")) { try { RoundTests(quick); } catch (Exception ex) { quick.Add("EXCEPTION " + ex); } }
            else if (e.Args.Contains("--speed")) { try { SpeedTests(quick); } catch (Exception ex) { quick.Add("EXCEPTION " + ex); } }
            else if (e.Args.Contains("--pictures")) { try { PictureTests(quick); } catch (Exception ex) { quick.Add("EXCEPTION " + ex); } }
            else if (e.Args.Contains("--pass")) { try { PassTests(quick); } catch (Exception ex) { quick.Add("EXCEPTION " + ex); } }
            else if (e.Args.Contains("--subtitles")) { try { SubtitleTests(quick); } catch (Exception ex) { quick.Add("EXCEPTION " + ex); } }
            else try { MoreFeatureTests(quick); } catch (Exception ex) { quick.Add("EXCEPTION " + ex); }
            System.IO.File.WriteAllLines(e.Args[1], quick);
            Shutdown();
            return;
        }
        if (e.Args.Length >= 2 && e.Args[0] == "--selftest")
        {
            SelfTest(e.Args[1], mouse: !e.Args.Contains("--no-mouse"));
            Shutdown();
            return;
        }
#endif

        _main = new MainWindow();
        MainWindow = _main;
        if (startInTray || command != null)
            new System.Windows.Interop.WindowInteropHelper(_main).EnsureHandle(); // tray icon and hotkeys, no window yet
        else
            _main.Show();
        if (command != null)
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                if (!_main.RunCommand(command)) _main.Show();
            });

#if DEBUG
        // Developer aid: "Overskin.exe --snapshot <folder> [image]" renders every tab to PNG and exits.
        if (e.Args.Length >= 2 && e.Args[0] == "--snapshot")
        {
            string folder = e.Args[1];
            string? image = e.Args.Length > 2 ? e.Args[2] : null;
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, async () =>
            {
                await _main.SnapshotTabsAsync(folder, image);
                Shutdown();
            });
        }
#endif
    }

#if DEBUG
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowDisplayAffinity(IntPtr hWnd, out uint affinity);

    private sealed class FakeRecorder : IRecorder
    {
        public TimeSpan Elapsed => TimeSpan.FromSeconds(3);
        public TimeSpan? Limit => null;
        public void Stop() { }
        public void Cancel() { }
    }

    /// <summary>Submenus, combining, scroll stitching, the auto-zoom camera and MP4 encoding, all on made-up pictures.</summary>
    private static void NewFeatureTests(List<string> log)
    {
        // ---- tray-style submenu with icons opens ----
        try
        {
            var menu = new System.Windows.Controls.ContextMenu();
            var group = new System.Windows.Controls.MenuItem { Header = "Capture", Icon = new Icon { Data = (System.Windows.Media.Geometry)Current.FindResource("IcCamera") } };
            var leaf = new System.Windows.Controls.MenuItem { Header = "Region" };
            group.Items.Add(leaf);
            menu.Items.Add(group);
            menu.IsOpen = true;
            DoEvents();
            group.IsSubmenuOpen = true;
            DoEvents();
            log.Add($"submenu: open={group.IsSubmenuOpen} leafVisible={leaf.IsVisible} role={group.Role} (expect True True SubmenuHeader)");
            menu.IsOpen = false;
        }
        catch (Exception ex) { log.Add("submenu: FAILED " + ex.Message); }

        // ---- combine ----
        try
        {
            System.Windows.Media.Imaging.BitmapSource Solid(int w, int h, byte v) =>
                BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, Enumerable.Repeat(v, w * h * 4).ToArray(), w * 4);
            var pics = new[] { Solid(400, 300, 10), Solid(200, 300, 80), Solid(800, 600, 160) };
            var white = System.Windows.Media.Colors.White;
            var row = ImageCombiner.Combine(pics, CombineLayout.SideBySide, white);
            var col = ImageCombiner.Combine(pics, CombineLayout.Stacked, white);
            var grid = ImageCombiner.Combine(pics.Concat(pics).ToArray(), CombineLayout.Grid, white);
            log.Add($"combine: row={row.PixelWidth}x{row.PixelHeight} (expect 16+400+16+200+16+400+16=1064 x 332) " +
                    $"stacked={col.PixelWidth}x{col.PixelHeight} (expect 432 x 16+300+16+600+16+300+16=1264) grid={grid.PixelWidth}x{grid.PixelHeight} (3x2 cells of 400x300: 1264 x 648)");
        }
        catch (Exception ex) { log.Add("combine: FAILED " + ex.Message); }

        // ---- scroll stitching: a made-up page seen through a window with a fixed header and a moving scrollbar ----
        try
        {
            int w = 300, h = 200, pageH = 1400, header = 30;
            var rng = new Random(5);
            var page = new byte[w * pageH * 4];
            for (int y = 0; y < pageH; y++)
            {
                // text-like rows: some blank, some with random marks
                bool blank = y % 9 < 3;
                for (int x = 0; x < w; x++)
                {
                    byte v = blank ? (byte)255 : (byte)(rng.Next(4) == 0 ? 20 : 250);
                    int o = (y * w + x) * 4;
                    page[o] = v; page[o + 1] = v; page[o + 2] = (byte)(v ^ (y & 0x3F)); page[o + 3] = 255;
                }
            }
            byte[] View(int offset)
            {
                var f = new byte[w * h * 4];
                for (int y = 0; y < h; y++)
                {
                    if (y < header) { for (int x = 0; x < w; x++) { int o = (y * w + x) * 4; f[o] = 90; f[o + 1] = 40; f[o + 2] = 200; f[o + 3] = 255; } continue; }
                    int src = Math.Min(pageH - 1, offset + y - header);
                    Buffer.BlockCopy(page, src * w * 4, f, y * w * 4, w * 4);
                }
                // scrollbar thumb on the right edge moves with the offset
                int thumb = header + offset * (h - header - 40) / (pageH - (h - header));
                for (int y = header; y < h; y++)
                    for (int x = w - 10; x < w; x++) { int o = (y * w + x) * 4; byte v = (byte)(y >= thumb && y < thumb + 40 ? 120 : 230); f[o] = v; f[o + 1] = v; f[o + 2] = v; }
                return f;
            }
            int maxOffset = pageH - (h - header);
            var stitcher = new ScrollStitcher(View(0), w, h);
            int off = 0, steps = 0;
            string results = "";
            while (steps++ < 40)
            {
                off = Math.Min(maxOffset, off + 97);
                var r1 = stitcher.Add(View(off));
                results += r1.ToString()[0];
                if (r1 != ScrollStitcher.Step.Moved && off == maxOffset) break;
            }
            var stitched = stitcher.Result();
            var px = new byte[stitched.PixelWidth * stitched.PixelHeight * 4];
            stitched.CopyPixels(px, stitched.PixelWidth * 4, 0);
            int wrongRows = 0;
            for (int y = header; y < Math.Min(stitched.PixelHeight, header + pageH); y++)
                for (int x = 30; x < w - 30; x++)
                {
                    int o = (y * w + x) * 4, s = ((y - header) * w + x) * 4;
                    if (px[o] != page[s] || px[o + 2] != page[s + 2]) { wrongRows++; break; }
                }
            log.Add($"scroll stitch: steps={results} height={stitched.PixelHeight} (expect {header + pageH}) mismatchedRows={wrongRows} (expect 0)");
        }
        catch (Exception ex) { log.Add("scroll stitch: FAILED " + ex.Message); }

        // ---- auto-zoom camera ----
        try
        {
            var cam = new SmartCamera(1920, 1080);
            var p = new Point(1500, 300);
            for (int i = 0; i < 30; i++) cam.Update(1 / 30.0, p, false);
            double before = cam.Zoom;
            cam.Update(1 / 30.0, p, true);
            for (int i = 0; i < 30; i++) cam.Update(1 / 30.0, p, false);
            double zoomed = cam.Zoom;
            var view = cam.View();
            bool contains = view.Contains(p);
            for (int i = 0; i < 150; i++) cam.Update(1 / 30.0, p, false);
            // a jittery pointer should come out calmer
            var jitter = new SmartCamera(1920, 1080);
            double inJ = 0, outJ = 0;
            Point last = new(960, 540), lastOut = new(960, 540);
            var r2 = new Random(3);
            for (int i = 0; i < 120; i++)
            {
                var q = new Point(960 + r2.Next(-6, 7), 540 + r2.Next(-6, 7));
                jitter.Update(1 / 30.0, q, false);
                if (i > 10) { inJ += (q - last).Length; outJ += (jitter.Cursor - lastOut).Length; }
                last = q; lastOut = jitter.Cursor;
            }
            log.Add($"camera: before={before:0.00} afterClick={zoomed:0.00} (expect ~1.7) pointerInView={contains} later={cam.Zoom:0.00} (expect ~1.0) jitter in={inJ:0} out={outJ:0} (expect out < in)");
        }
        catch (Exception ex) { log.Add("camera: FAILED " + ex.Message); }

        // ---- our own windows stay out of screen grabs (pixels compared in memory, nothing kept) ----
        try
        {
            Window Box(int x, bool transparentWpf, bool alphaLayer)
            {
                var w = new Window
                {
                    WindowStyle = WindowStyle.None, AllowsTransparency = transparentWpf, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
                    Topmost = true, ShowActivated = false, Left = x, Top = 200, Width = 120, Height = 120,
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 0, 255)),
                };
                w.SourceInitialized += (_, _) =>
                {
                    var h = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                    if (alphaLayer)
                    {
                        NativeMethods.TrySetExStyle(h, NativeMethods.GetExStyle(h) | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TRANSPARENT, out _);
                        NativeMethods.SetLayeredWindowAttributes(h, 0, 250, NativeMethods.LWA_ALPHA);
                    }
                    if (alphaLayer) CaptureGuard.Apply(w);
                };
                w.Show();
                return w;
            }
            int Count(Window w)
            {
                var src = PresentationSource.FromVisual(w);
                double k = src?.CompositionTarget?.TransformToDevice.M11 ?? 1;
                var shot = NativeMethods.CaptureScreen((int)(w.Left * k) + 10, (int)(w.Top * k) + 10, 60, 60, cursor: false);
                if (shot == null) return -1;
                var px = new byte[60 * 60 * 4];
                new FormatConvertedBitmap(shot, System.Windows.Media.PixelFormats.Bgra32, null, 0).CopyPixels(px, 60 * 4, 0);
                int n = 0;
                for (int i = 0; i < px.Length; i += 4) if (px[i] > 200 && px[i + 1] < 60 && px[i + 2] > 200) n++;
                return n;
            }
            var guarded = new GuardedWindow(clickThrough: true)
            {
                Left = 300, Top = 200, Width = 120, Height = 120,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 0, 255)),
                Corners = new CornerRadius(16, 3, 16, 3), // shaped windows must stay hidden too
            };
            guarded.Show();
            var plain = Box(450, false, false);
            Toast.ShowSticky("capture guard test");
            var bar = new RecordBarWindow(new FakeRecorder(), new Int32Rect(100, 100, 400, 300));
            bar.Show();
            for (int i = 0; i < 10; i++) { DoEvents(); Thread.Sleep(30); }
            // the same through the fast video reader: hidden windows must stay hidden there too
            (int, string) CountFast(Window w)
            {
                var src = PresentationSource.FromVisual(w);
                double k = src?.CompositionTarget?.TransformToDevice.M11 ?? 1;
                var area = new Int32Rect((int)(w.Left * k) + 10, (int)(w.Top * k) + 10, 60, 60);
                using var fast = FrameGrabbers.For(area, 60, 60);
                var px = fast.Grab(area.X, area.Y, 60, 60, null);
                int n = 0;
                for (int i = 0; i < px.Length; i += 4) if (px[i] > 200 && px[i + 1] < 60 && px[i + 2] > 200) n++;
                return (n, fast.Kind);
            }
            var (fastGuarded, kind) = CountFast(guarded);
            var (fastPlain, _) = CountFast(plain);
            log.Add($"capture guard, {kind}: guarded window seen={fastGuarded} (expect 0) ordinary seen={fastPlain} (expect >3000)");
            log.Add($"capture guard: guarded window seen={Count(guarded)} (expect 0) ordinary seen={Count(plain)} (expect >3000) " +
                    $"toast hidden from captures={Toast.IsGuarded} record bar={bar.IsGuarded} (expect True True); " +
                    $"leaf corners: toast={Toast.IsShaped} record bar={bar.IsShaped} test window={guarded.IsShaped} (expect True True True)");
            Toast.HideNow();
            bar.Close(); guarded.Close(); plain.Close();
        }
        catch (Exception ex) { log.Add("capture guard: FAILED " + ex.Message); }

        // ---- capture bar: marks drawn on the frozen screen end up in the crop ----
        try
        {
            int sw = 400, sh = 300;
            var grey = Enumerable.Repeat((byte)128, sw * sh * 4).ToArray();
            var screen = BitmapSource.Create(sw, sh, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, grey, sw * 4);
            var overlay = CaptureOverlay.ForTest(screen, new NativeMethods.RECT { Left = 0, Top = 0, Right = sw, Bottom = sh });
            overlay.AddMarkForTest(CaptureTool.Box, System.Windows.Media.Color.FromRgb(255, 0, 0), new Point(50, 50), new Point(150, 120));
            overlay.AddMarkForTest(CaptureTool.Pen, System.Windows.Media.Color.FromRgb(0, 0, 255), new Point(200, 200), new Point(260, 200), new Point(300, 200));
            var crop = overlay.Crop(new Int32Rect(40, 40, 300, 200));
            var cpx = new byte[crop.PixelWidth * crop.PixelHeight * 4];
            new FormatConvertedBitmap(crop, System.Windows.Media.PixelFormats.Bgra32, null, 0).CopyPixels(cpx, crop.PixelWidth * 4, 0);
            string At(int x, int y) { int o = (y * crop.PixelWidth + x) * 4; return $"#{cpx[o + 2]:X2}{cpx[o + 1]:X2}{cpx[o]:X2}"; }
            var bar = overlay.ToolbarForTest!;
            bar.SetPick(CapturePick.Screen);
            bar.SetTool(CaptureTool.Arrow);
            log.Add($"capture bar: crop={crop.PixelWidth}x{crop.PixelHeight} boxEdge={At(10, 40)} (expect #FF0000) inside={At(50, 40)} (expect #808080) " +
                    $"pen={At(200, 160)} (expect #0000FF) pick={bar.Pick} tool={bar.Tool} pointer={overlay.IncludePointer}");
            overlay.Close();
        }
        catch (Exception ex) { log.Add("capture bar: FAILED " + ex); }

        // ---- hide modes, private details, smart erase, beautify, step guide, window-only grab ----
        try
        {
            BitmapSource Picture(int w, int h, Action<System.Windows.Media.DrawingContext> draw)
            {
                var v = new System.Windows.Media.DrawingVisual();
                using (var dc = v.RenderOpen()) draw(dc);
                var rtb = new RenderTargetBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                rtb.Render(v);
                return ImageLoader.Normalize(rtb);
            }
            string Hex(BitmapSource b, int x, int y)
            {
                var px = new byte[4];
                b.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
                return $"#{px[2]:X2}{px[1]:X2}{px[0]:X2}";
            }

            // stripes: blur and pixelate both turn them to an even grey, mosaic adds tile lines
            var stripes = Picture(120, 80, dc =>
            {
                dc.DrawRectangle(System.Windows.Media.Brushes.White, null, new Rect(0, 0, 120, 80));
                for (int x = 0; x < 120; x += 4) dc.DrawRectangle(System.Windows.Media.Brushes.Black, null, new Rect(x, 0, 2, 80));
            });
            var blur = Redaction.Render(stripes, new Rect(10, 10, 100, 60), HideMode.Blur, 4)!;
            var pix = Redaction.Render(stripes, new Rect(10, 10, 100, 60), HideMode.Pixelate, 4)!;
            var mos = Redaction.Render(stripes, new Rect(10, 10, 100, 60), HideMode.Mosaic, 4)!;
            log.Add($"hide modes: blur={blur.PixelWidth}x{blur.PixelHeight} {Hex(blur, 50, 30)} pixelate={Hex(pix, 50, 30)} mosaic edge={Hex(mos, 0, 0)} inside={Hex(mos, 7, 7)} (expect 100x60, greys near #7F7F7F, mosaic edge darker)");

            // private details: from made-up recognised words, then from real text read off a picture
            var words = new List<List<OcrWord>>
            {
                new() { new("Email", new Rect(0, 0, 40, 10)), new("me:", new Rect(45, 0, 20, 10)), new("ana.lopez@example.com", new Rect(70, 0, 120, 10)) },
                new() { new("Call", new Rect(0, 20, 30, 10)), new("+1", new Rect(35, 20, 12, 10)), new("415", new Rect(50, 20, 20, 10)), new("555", new Rect(73, 20, 20, 10)), new("0134", new Rect(96, 20, 26, 10)) },
                new() { new("Card", new Rect(0, 40, 30, 10)), new("4111", new Rect(35, 40, 25, 10)), new("1111", new Rect(63, 40, 25, 10)), new("1111", new Rect(91, 40, 25, 10)), new("1111", new Rect(119, 40, 25, 10)) },
                new() { new("Server", new Rect(0, 60, 40, 10)), new("192.168.1.20", new Rect(45, 60, 70, 10)), new("version", new Rect(120, 60, 40, 10)), new("2.4.0", new Rect(165, 60, 30, 10)) },
                new() { new("Order", new Rect(0, 80, 40, 10)), new("1234", new Rect(45, 80, 30, 10)) },
            };
            var found = Redaction.FindPrivate(words);
            log.Add($"private details: {string.Join(", ", found.Select(f => f.Kind))} (expect email, phone number, card number, IP address; not the order number or version)");

            // the cases from testing: a Visa read with l for 1, its expiry and code, IPv6, folders, addresses
            List<OcrWord> L(double y, params string[] ws)
            {
                var list = new List<OcrWord>();
                double x = 0;
                foreach (var w in ws) { list.Add(new(w, new Rect(x, y, w.Length * 7, 10))); x += w.Length * 7 + 5; }
                return list;
            }
            var more = new List<List<OcrWord>>
            {
                L(0, "Visa", "4lll", "1111", "1lI1", "1111"),
                L(20, "Expires", "12/28", "CVV", "123"),
                L(40, "Host", "2001:db8:85a3::8a2e:370:7334", "and", "fe80::1", "at", "12:30:45"),
                L(60, "Saved", "to", @"C:\Users\sam\Documents\taxes", "and", "/home/sam/notes"),
                L(80, "1600", "Pennsylvania", "Avenue", "NW,", "Washington,", "DC", "20500"),
                L(100, "Hauptstraße", "12,", "10115", "Berlin,", "Germany"),
            };
            var got = Redaction.FindPrivate(more, Overskin.Shared.PrivateKinds.All);
            log.Add($"private details, harder: {string.Join(", ", got.Select(f => f.Kind))} (expect card number, expiry date, security code, IP address x2, folder path x2, street address, place(s), street address, place, country; not 12:30:45)");
            // live text in the editor: select words, copy them, erase them
            {
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(300, 80, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                var dv = new System.Windows.Media.DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRectangle(System.Windows.Media.Brushes.White, null, new Rect(0, 0, 300, 80));
                    dc.DrawRectangle(System.Windows.Media.Brushes.Black, null, new Rect(20, 20, 60, 14));
                    dc.DrawRectangle(System.Windows.Media.Brushes.Black, null, new Rect(90, 20, 60, 14));
                    dc.DrawRectangle(System.Windows.Media.Brushes.Black, null, new Rect(20, 50, 80, 14));
                }
                rtb.Render(dv);
                var livePage = ImageLoader.Normalize(rtb);
                var ed = new EditorWindow(livePage, null, "test", new EditorHost { SaveNew = _ => null, Pin = (_, _) => { } });
                var text = ed.SelectWordsForTest(new List<OcrWord>
                {
                    new("Hello", new Rect(20, 20, 60, 14)), new("there", new Rect(90, 20, 60, 14)), new("friend", new Rect(20, 50, 80, 14)),
                }, 0, 2);
                var erase = ed.EraseSelectedForTest();
                for (int spin = 0; !erase.IsCompleted && spin < 300; spin++) { DoEvents(); Thread.Sleep(20); }
                var px = new byte[4];
                ed.BaseForTest.CopyPixels(new Int32Rect(50, 27, 1, 1), px, 4, 0);
                log.Add($"live text: selected \"{text.Replace("\n", " / ")}\" (expect Hello there / friend); erased pixel={px[0]},{px[1]},{px[2]} (expect near white); erase finished={erase.IsCompleted}");
                ed.CloseForTest();
            }
            // the step recorder names what was clicked from the words there, for apps that don't say
            {
                var shopShot = new System.Windows.Media.Imaging.RenderTargetBitmap(500, 220, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                var sv = new System.Windows.Media.DrawingVisual();
                using (var dc = sv.RenderOpen())
                {
                    dc.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 40, 80)), null, new Rect(0, 0, 500, 220));
                    dc.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(250, 200, 40)), null, new Rect(180, 80, 140, 56));
                    dc.DrawText(new System.Windows.Media.FormattedText("Shop", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new System.Windows.Media.Typeface("Segoe UI Semibold"), 30, System.Windows.Media.Brushes.Black, 1), new Point(218, 88));
                    dc.DrawText(new System.Windows.Media.FormattedText("Locker      Battle Pass", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new System.Windows.Media.Typeface("Segoe UI"), 22, System.Windows.Media.Brushes.White, 1), new Point(40, 20));
                }
                shopShot.Render(sv);
                var shotPlain = ImageLoader.Normalize(shopShot);
                var said = Task.Run(() => StepRecorder.TextAt(shotPlain, new Point(250, 108))).GetAwaiter().GetResult();
                var step = new RecordedStep { Shot = shotPlain, At = new Point(250, 108), Right = false, Window = "Fortnite", Target = said == null ? null : $"“{said}”", TargetIsText = said != null };
                log.Add($"step named from the screen: \"{StepTutorial.Caption(step)}\" (expect Click “Shop” in Fortnite)");
            }
            var noCard = Redaction.FindPrivate(new List<List<OcrWord>> { L(0, "Meeting", "12/28", "CVV", "123") }, Overskin.Shared.PrivateKinds.All);
            var off = Redaction.FindPrivate(more, Overskin.Shared.PrivateKinds.Emails);
            log.Add($"private details without a card: {noCard.Count} (expect 0); with only emails switched on: {off.Count} (expect 0)");

            var page = Picture(900, 200, dc =>
            {
                dc.DrawRectangle(System.Windows.Media.Brushes.White, null, new Rect(0, 0, 900, 200));
                var face = new System.Windows.Media.Typeface("Segoe UI");
                void Line(string s, double y) => dc.DrawText(new System.Windows.Media.FormattedText(s, System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, face, 26, System.Windows.Media.Brushes.Black, 1), new Point(20, y));
                Line("Contact: sam.taylor@example.org", 20);
                Line("Phone (020) 7946 0958, server 10.0.0.42", 80);
            });
            var cardPage = Picture(900, 260, dc =>
            {
                dc.DrawRectangle(System.Windows.Media.Brushes.White, null, new Rect(0, 0, 900, 260));
                var face = new System.Windows.Media.Typeface("Segoe UI");
                void Line(string s, double y) => dc.DrawText(new System.Windows.Media.FormattedText(s, System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, face, 26, System.Windows.Media.Brushes.Black, 1), new Point(20, y));
                Line("Card number 4111 1111 1111 1111", 20);
                Line("Expires 09/27    CVV 737", 70);
                Line("Router fe80::1ff:fe23:4567:890a", 120);
                Line(@"Saved in C:\Users\sam\Documents", 170);
            });
            var cardReal = Task.Run(() => Redaction.FindPrivateAsync(cardPage)).GetAwaiter().GetResult();
            log.Add($"card read off a picture: {(cardReal == null ? "no text reader" : string.Join(", ", cardReal.Select(f => f.Kind)))} (expect card number, IP address, folder path, expiry date, security code)");
            var real = Task.Run(() => Redaction.FindPrivateAsync(page)).GetAwaiter().GetResult();
            log.Add($"private details read off a picture: {(real == null ? "no text reader" : string.Join(", ", real.Select(f => f.Kind)))} (expect email, phone number, IP address)");

            // smart erase: a black bar on a two-tone background disappears into it
            var scene = Picture(200, 120, dc =>
            {
                dc.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 90, 200)), null, new Rect(0, 0, 200, 60));
                dc.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(240, 240, 240)), null, new Rect(0, 60, 200, 60));
                dc.DrawRectangle(System.Windows.Media.Brushes.Black, null, new Rect(60, 20, 80, 12));
                dc.DrawRectangle(System.Windows.Media.Brushes.Black, null, new Rect(60, 88, 80, 12));
            });
            var mask = new bool[200 * 120];
            for (int y = 16; y < 36; y++) for (int x = 56; x < 144; x++) mask[y * 200 + x] = true;
            for (int y = 84; y < 104; y++) for (int x = 56; x < 144; x++) mask[y * 200 + x] = true;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var erased = Inpaint.Fill(scene, mask);
            log.Add($"smart erase: top={Hex(erased, 100, 26)} (expect #285AC8) bottom={Hex(erased, 100, 94)} (expect #F0F0F0) in {sw.ElapsedMilliseconds} ms");
            var bigMask = new bool[1600 * 900];
            for (int y = 200; y < 700; y++) for (int x = 300; x < 1300; x++) bigMask[y * 1600 + x] = true;
            var bigScene = Picture(1600, 900, dc => dc.DrawRectangle(new System.Windows.Media.LinearGradientBrush(System.Windows.Media.Colors.White, System.Windows.Media.Colors.SteelBlue, 0), null, new Rect(0, 0, 1600, 900)));
            sw.Restart();
            Inpaint.Fill(bigScene, bigMask);
            log.Add($"smart erase 1000x500 hole in 1600x900: {sw.ElapsedMilliseconds} ms (expect well under 2000)");

            var pretty = Beautify.Render(scene, new BeautifyOptions { Background = "Ocean", Padding = 50, Corners = 10, Shadow = 50, Aspect = 16 / 9.0 });
            log.Add($"beautify: {pretty.PixelWidth}x{pretty.PixelHeight} (expect 391x220 for 16:9) corner={Hex(pretty, 2, 2)} (gradient blue) centre={Hex(pretty, 195, 80)}");

            var guide = StepTutorial.Build(new[]
            {
                new RecordedStep { Shot = scene, At = new Point(100, 30), Right = false, Window = "Notepad", Target = "“Save” button" },
                new RecordedStep { Shot = scene, At = new Point(20, 100), Right = true, Window = "the desktop" },
            }, System.Windows.Media.Color.FromRgb(0x4F, 0x8C, 0xFF));
            log.Add($"step guide: {guide.PixelWidth}x{guide.PixelHeight} captions=\"{StepTutorial.Caption(new RecordedStep { Shot = scene, At = default, Right = false, Window = "Notepad", Target = "“Save” button" })}\"");
        }
        catch (Exception ex) { log.Add("new editor features: FAILED " + ex); }

        // ---- Convert to: every route, on made-up files in a temporary folder ----
        try
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Overskin-convert-test");
            if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
            System.IO.Directory.CreateDirectory(dir);
            var v = new System.Windows.Media.DrawingVisual();
            using (var dc = v.RenderOpen())
            {
                dc.DrawRectangle(System.Windows.Media.Brushes.SteelBlue, null, new Rect(0, 0, 321, 181));
                dc.DrawEllipse(System.Windows.Media.Brushes.Orange, null, new Point(160, 90), 60, 60);
            }
            var rtb = new RenderTargetBitmap(321, 181, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(v);
            string png = System.IO.Path.Combine(dir, "shot.png");
            ImageFiles.TrySave(ImageLoader.Normalize(rtb), png, out _);

            string Convert(string from, string to)
            {
                try { return System.IO.Path.GetFileName(Task.Run(() => Converter.ConvertAsync(from, to)).GetAwaiter().GetResult()); }
                catch (Exception ex) { return "FAILED " + (ex.InnerException ?? ex).Message; }
            }
            string Full(string name) => System.IO.Path.Combine(dir, name);
            var results = new List<string>
            {
                "png->jpg " + Convert(png, "JPG"),
                "png->bmp " + Convert(png, "BMP"),
                "png->tiff " + Convert(png, "TIFF"),
                "png->gif " + Convert(png, "GIF"),
                "png->mp4 " + Convert(png, "MP4"),
            };
            results.Add("gif->mp4 " + Convert(Full("shot.gif"), "MP4"));
            results.Add("mp4->gif " + Convert(Full("shot.mp4"), "GIF"));
            results.Add("mp4->png " + Convert(Full("shot.mp4"), "PNG"));
            results.Add("png->png " + Convert(png, "PNG"));
            var sizes = System.IO.Directory.GetFiles(dir).Select(f => $"{System.IO.Path.GetFileName(f)}={new System.IO.FileInfo(f).Length / 1024}K");
            log.Add($"convert: {string.Join("; ", results)}  files: {string.Join(" ", sizes)}");
            System.IO.Directory.Delete(dir, true);
        }
        catch (Exception ex) { log.Add("convert: FAILED " + ex); }

        // ---- every theme, both styles: applies cleanly and text stays readable ----
        try
        {
            static double Lum(System.Windows.Media.Color c)
            {
                static double L(byte v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
                return 0.2126 * L(c.R) + 0.7152 * L(c.G) + 0.0722 * L(c.B);
            }
            static double Contrast(System.Windows.Media.Color a, System.Windows.Media.Color b)
            {
                double x = Lum(a), y = Lum(b);
                return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
            }
            System.Windows.Media.Color C(string key) => ((System.Windows.Media.SolidColorBrush)Current.Resources[key]).Color;
            var weak = new List<string>();
            int applied = 0;
            foreach (var style in new[] { "Modern" })
            {
                foreach (var theme in Theming.Themes)
                {
                    Theming.Apply(theme.Accent ?? Theming.DefaultAccent, theme.Name);
                    applied++;
                    double text = Contrast(C("Text"), C("Panel")), dim = Contrast(C("TextDim"), C("Panel")), ink = Contrast(C("AccentInk"), C("Accent"));
                    if (text < 6 || dim < 3.2 || ink < 3) weak.Add($"{style}/{theme.Name} text={text:0.0} dim={dim:0.0} accentInk={ink:0.0}");
                    if (Current.Resources["SwitchTemplate"] is not System.Windows.Controls.ControlTemplate) weak.Add($"{style}/{theme.Name} no switch template");
                }
            }
            Theming.Apply(Theming.DefaultAccent, Theming.DefaultTone);
            log.Add($"themes: applied={applied} weak=[{string.Join("; ", weak)}] (expect none)");
        }
        catch (Exception ex) { log.Add("themes: FAILED " + ex.Message); }

        // ---- big Windows pointers load at the asked size ----
        try
        {
            var big = SystemPointers.LoadAtSize(32649, 96);
            int bw = 0;
            if (big != IntPtr.Zero && NativeMethods.GetIconInfo(big, out var ii))
            {
                var bmp = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(ii.hbmColor != IntPtr.Zero ? ii.hbmColor : ii.hbmMask, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bw = bmp.PixelWidth;
                if (ii.hbmMask != IntPtr.Zero) NativeMethods.DeleteObject(ii.hbmMask);
                if (ii.hbmColor != IntPtr.Zero) NativeMethods.DeleteObject(ii.hbmColor);
                NativeMethods.DestroyIcon(big);
            }
            var sprite = PointerArt.Render(PointerArt.Shape.Arrow, 120, false);
            log.Add($"pointers: windows hand loaded at {bw}px (expect 96) vector arrow {sprite.Width}x{sprite.Height} tip=({sprite.HotX:0},{sprite.HotY:0})");
        }
        catch (Exception ex) { log.Add("pointers: FAILED " + ex.Message); }

        // ---- video grab speed (timing only; the pixels are thrown away) ----
        try
        {
            var mon = NativeMethods.MonitorBounds(NativeMethods.CursorPos());
            int gw = mon.Width & ~1, gh = mon.Height & ~1;
            using var grabber = new ScaledGrabber(gw, gh);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 20; i++) grabber.Grab(mon.Left, mon.Top, gw, gh, null);
            double plainMs = sw.Elapsed.TotalMilliseconds / 20;
            sw.Restart();
            for (int i = 0; i < 20; i++) grabber.Grab(mon.Left + 100, mon.Top + 100, gw / 1.8, gh / 1.8, dc => NativeMethods.DrawCursorAt(dc, 200, 200, 60));
            double zoomMs = sw.Elapsed.TotalMilliseconds / 20;
            log.Add($"video grab GDI {gw}x{gh}: plain={plainMs:0.0}ms zoomed+pointer={zoomMs:0.0}ms");

            // the fast way: same screen, and the two should agree (compared in memory, nothing kept)
            var area = new Int32Rect(mon.Left, mon.Top, gw, gh);
            using var fast = FrameGrabbers.For(area, gw, gh);
            fast.Grab(mon.Left, mon.Top, gw, gh, null); // the first picture can take a moment
            sw.Restart();
            for (int i = 0; i < 20; i++) fast.Grab(mon.Left, mon.Top, gw, gh, null);
            double fastPlain = sw.Elapsed.TotalMilliseconds / 20;
            sw.Restart();
            for (int i = 0; i < 20; i++) fast.Grab(mon.Left + 100, mon.Top + 100, gw / 1.8, gh / 1.8, dc => NativeMethods.DrawCursorAt(dc, 200, 200, 60));
            double fastZoom = sw.Elapsed.TotalMilliseconds / 20;
            var a = fast.Grab(mon.Left, mon.Top, gw, gh, null);
            var b = grabber.Grab(mon.Left, mon.Top, gw, gh, null);
            // most pixels should match exactly; whatever moved between the two reads (a video, a clock) won't,
            // so two GDI reads a moment apart are the yardstick
            var b2 = grabber.Grab(mon.Left, mon.Top, gw, gh, null);
            double Match(byte[] p, byte[] q)
            {
                int same = 0, sampled = 0;
                for (int i = 0; i < p.Length; i += 4 * 97, sampled++)
                    if (Math.Abs(p[i] - q[i]) <= 2 && Math.Abs(p[i + 1] - q[i + 1]) <= 2 && Math.Abs(p[i + 2] - q[i + 2]) <= 2) same++;
                return 100.0 * same / sampled;
            }
            log.Add($"video grab {fast.Kind} {gw}x{gh}: plain={fastPlain:0.0}ms zoomed+pointer={fastZoom:0.0}ms (need < 33 ms for 30 fps, < 16 for 60) " +
                    $"same picture as GDI: {Match(a, b):0.0}% of pixels match; GDI against itself a moment later: {Match(b, b2):0.0}% (expect about the same)");
        }
        catch (Exception ex) { log.Add("video grab: FAILED " + ex.Message); }

        // ---- MP4 encoding: 2 s of a red-over-blue picture with a moving square, read back by Windows ----
        try
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Overskin-selftest.mp4");
            int w = 640, h = 360, fps = 30;
            var queue = new System.Collections.Concurrent.BlockingCollection<VideoFrame>(8);
            var encode = Task.Run(() => VideoEncoder.EncodeAsync(path, w, h, fps, queue));
            for (int i = 0; i < fps * 2; i++)
            {
                var f = new byte[w * h * 4];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int o = (y * w + x) * 4;
                        bool square = x >= i * 8 && x < i * 8 + 40 && y >= 160 && y < 200;
                        if (square) { f[o] = 255; f[o + 1] = 255; f[o + 2] = 255; }
                        else if (y < h / 2) f[o + 2] = 230; else f[o + 1] = 230;
                        f[o + 3] = 255;
                    }
                queue.Add(new VideoFrame(f, TimeSpan.FromSeconds(i / (double)fps)));
            }
            queue.CompleteAdding();
            encode.GetAwaiter().GetResult();
            long size = new System.IO.FileInfo(path).Length;

            var (top, bottom, seconds) = Task.Run(async () =>
            {
                var file = await global::Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                var props = await file.Properties.GetVideoPropertiesAsync();
                var clip = await global::Windows.Media.Editing.MediaClip.CreateFromFileAsync(file);
                var comp = new global::Windows.Media.Editing.MediaComposition();
                comp.Clips.Add(clip);
                using var thumb = await comp.GetThumbnailAsync(TimeSpan.FromSeconds(0.5), w, h, global::Windows.Media.Editing.VideoFramePrecision.NearestFrame);
                var decoder = await global::Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(thumb);
                var data = await decoder.GetPixelDataAsync(global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, global::Windows.Graphics.Imaging.BitmapAlphaMode.Ignore,
                    new global::Windows.Graphics.Imaging.BitmapTransform(), global::Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation,
                    global::Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
                var bytes = data.DetachPixelData();
                int dw = (int)decoder.PixelWidth;
                string Pixel(int x, int y) { int o = (y * dw + x) * 4; return $"r{bytes[o + 2]}g{bytes[o + 1]}b{bytes[o]}"; }
                return (Pixel(dw / 2, 20), Pixel(dw / 2, (int)decoder.PixelHeight - 20), props.Duration.TotalSeconds);
            }).GetAwaiter().GetResult();
            log.Add($"video: bytes={size} duration={seconds:0.0}s top={top} (expect red) bottom={bottom} (expect green)");
            System.IO.File.Delete(path);
        }
        catch (Exception ex) { log.Add("video: FAILED " + ex.GetType().Name + " " + ex.Message); }
    }

    private static void SelfTest(string logFile, bool mouse)
    {
        var log = new List<string>();
        var notepad = System.Diagnostics.Process.Start("notepad.exe");
        try
        {
            IntPtr h = IntPtr.Zero;
            for (int i = 0; i < 50 && h == IntPtr.Zero; i++)
            {
                Thread.Sleep(200);
                notepad.Refresh();
                h = notepad.MainWindowHandle;
            }

            string State()
            {
                long ex = NativeMethods.GetExStyle(h);
                bool got = NativeMethods.GetLayeredWindowAttributes(h, out _, out byte alpha, out _);
                return $"topmost={(ex & NativeMethods.WS_EX_TOPMOST) != 0} layered={(ex & NativeMethods.WS_EX_LAYERED) != 0} " +
                       $"transparent={(ex & NativeMethods.WS_EX_TRANSPARENT) != 0} alpha={(got ? alpha.ToString() : "-")}";
            }

            var wm = new WindowManager();
            log.Add("listed:        " + wm.ListWindows().Any(w => w.Handle == h));
            var alone = NativeMethods.CaptureWindowOnly(h);
            var vb = NativeMethods.VisibleBounds(h);
            log.Add($"window on its own: {(alone == null ? "null" : $"{alone.PixelWidth}x{alone.PixelHeight}")} (expect {vb.Width}x{vb.Height})");
            log.Add("before:        " + State());
            log.Add("pin ok=" + wm.SetPinned(h, true).Ok + "    " + State());
            log.Add("opacity60 ok=" + wm.SetOpacity(h, 60).Ok + " " + State());
            log.Add("click ok=" + wm.SetClickThrough(h, true).Ok + "  " + State());

            NativeMethods.GetWindowRect(h, out var r);
            var mid = new NativeMethods.POINT((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
            log.Add("WindowAt finds click-through window: " + (wm.WindowAt(mid) == h));

            log.Add("unpin ok=" + wm.SetPinned(h, false).Ok + "  " + State() + " tracked=" + wm.ModifiedCount);
            log.Add("opacity40 ok=" + wm.SetOpacity(h, 40).Ok + " " + State());
            log.Add("opacity100 ok=" + wm.SetOpacity(h, 100).Ok + " " + State() + " tracked=" + wm.ModifiedCount);
            wm.SetPinned(h, true); wm.SetOpacity(h, 30); wm.SetClickThrough(h, true);
            log.Add("all three:     " + State());
            wm.RestoreAll();
            log.Add("restore all:   " + State() + " tracked=" + wm.ModifiedCount);

            // ---- real input: this moves the actual mouse for a moment ----
            var ui = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            void Pump(int ms)
            {
                long end = Environment.TickCount64 + ms;
                while (Environment.TickCount64 < end)
                {
                    ui.Invoke(DispatcherPriority.Background, new Action(() => { }));
                    Thread.Sleep(5);
                }
            }

            if (mouse)
            {
            // 1. Pick mode: the click must be swallowed and report the window under it.
            wm.SetPinned(h, true); // bring the test Notepad above everything so it is what gets clicked
            Pump(200);
            NativeMethods.GetWindowRect(h, out var nr);
            IntPtr picked = new IntPtr(-1);
            var picker = new WindowPicker(ui, wm.IsOwnWindow);
            picker.Finished += hit => picked = hit;
            bool hooked = picker.Start();
            TestMouse.MoveTo(new Point((nr.Left + nr.Right) / 2, nr.Top + 12));
            Pump(80);
            TestMouse.Button(true);
            Pump(60);
            TestMouse.Button(false);
            Pump(300);
            log.Add($"pick mode: hooked={hooked} pickedNotepad={picked == h} stillActive={picker.IsActive}");
            picker.Dispose();
            wm.RestoreAll();

            // 2. Reading the screen, snipping it, and bringing a pinned picture back.
            var board = new Window
            {
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Topmost = true, ShowInTaskbar = false,
                Left = 200, Top = 200, Width = 300, Height = 200,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x22, 0x88, 0xCC)),
            };
            board.Show();
            Pump(400);
            var b0 = board.PointToScreen(new Point(0, 0));
            var swatchAt = new Point(b0.X + 20, b0.Y + 20);
            var sampled = NativeMethods.ScreenPixel((int)swatchAt.X, (int)swatchAt.Y);
            log.Add($"screen sample: {(sampled is { } sc ? Theming.ToHex(sc) : "unreadable")} (window is #2288CC)");

            // snip: the copied rectangle must hold the window's colour
            var grab = NativeMethods.CaptureScreen((int)b0.X + 10, (int)b0.Y + 10, 40, 30);
            string grabbed = "nothing";
            if (grab != null)
            {
                var px = new byte[4];
                grab.CopyPixels(new Int32Rect(20, 15, 1, 1), px, 4, 0);
                grabbed = $"{grab.PixelWidth}x{grab.PixelHeight} #{px[2]:X2}{px[1]:X2}{px[0]:X2}";
            }
            log.Add($"screen capture: {grabbed} (expect 40x30 #2288CC)");

            // a pinned image reopens exactly as it was saved
            var saved = new PinnedImageState
            {
                Path = "x.png", Name = "t", Left = 300, Top = 250, Width = 120, Opacity = 60, Rotation = 30,
                FlipHorizontal = true, Grayscale = true, Locked = true, ClickThrough = true,
            };
            var pinned = new ImageOverlayWindow(grab ?? ImageLoader.Normalize(BitmapSource.Create(4, 4, 96, 96, System.Windows.Media.PixelFormats.Bgr24, null, new byte[48], 12)), "t", 100, wm)
            {
                RestoreState = saved, SourcePath = "x.png",
            };
            pinned.Show();
            Pump(300);
            var back = pinned.ToState();
            bool same = back.Left == saved.Left && back.Top == saved.Top && Math.Abs(back.Width - saved.Width) < 0.01
                        && back.Opacity == saved.Opacity && back.Rotation == saved.Rotation && back.FlipHorizontal
                        && back.Grayscale && back.Locked && back.ClickThrough;
            log.Add($"restore pinned image: same={same} (left {back.Left}, top {back.Top}, width {back.Width:0.#}, opacity {back.Opacity}, rotation {back.Rotation})");
            pinned.Close();
            Pump(100);

            board.Close();
            Pump(100);
            }

            // ---- a 3.2.1 settings file, Trace keys and all, still loads and keeps everything else ----
            try
            {
                const string old = """
                {
                  "HotkeyRegion": "Ctrl+Alt+Q", "HotkeyAbort": "Ctrl+Alt+X", "HotkeyDraw": "Ctrl+Alt+D",
                  "LastTab": 4, "AccentColor": "#48C288",
                  "TraceMode": "Gartic", "Threshold": 140, "Invert": true, "Blur": 2, "ScalePercent": 80,
                  "MinPathLength": 6, "Tolerance": 1.5, "FillSpacing": 5, "HatchAngle": 45, "Crosshatch": true,
                  "ShadeLevels": 4, "OutlineToo": false,
                  "Palette": [ { "X": 10, "Y": 20, "Hex": "#FF0000", "Skip": false } ],
                  "Gartic": { "Pen": { "X": 1, "Y": 2 }, "Sizes": [ 3, 7, 12 ], "Opacity": true },
                  "DelayMs": 2, "PointsPerStep": 3, "CountdownSeconds": 5, "TraceSpeed": "Careful", "StopOnMouseMove": false
                }
                """;
                var loaded = AppSettings.FromJsonForTest(old);
                log.Add($"old settings with Trace keys: loaded={loaded != null} region={loaded?.HotkeyRegion} draw={loaded?.HotkeyDraw} " +
                        $"accent={loaded?.AccentColor} tab={loaded?.LastTab} (expect True Ctrl+Alt+Q Ctrl+Alt+D #48C288 3)");
            }
            catch (Exception ex) { log.Add("old settings with Trace keys: FAILED " + ex.Message); }

            // ---- screenshots (none of these move the mouse) ----
            var swatchWin = new Window
            {
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Topmost = true, ShowInTaskbar = false,
                Left = 240, Top = 240, Width = 160, Height = 100,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x22, 0x88, 0xCC)),
            };
            swatchWin.Show();
            Pump(500);
            var so = swatchWin.PointToScreen(new Point(0, 0));
            var overlay = CaptureOverlay.Open(OverlayMode.Region, "test");
            if (overlay != null)
            {
                var crop = overlay.Crop(new Int32Rect((int)so.X + 20, (int)so.Y + 20, 30, 20));
                var cpx = new byte[4];
                crop.CopyPixels(new Int32Rect(15, 10, 1, 1), cpx, 4, 0);
                log.Add($"frozen screen crop: {crop.PixelWidth}x{crop.PixelHeight} #{cpx[2]:X2}{cpx[1]:X2}{cpx[0]:X2} (expect 30x20 #2288CC)");
                overlay.Close();
            }
            else log.Add("frozen screen crop: overlay failed to open");
            swatchWin.Close();

            // editor: a red box and some text on a white picture, burned into the export
            var whitePx = Enumerable.Repeat((byte)255, 200 * 120 * 3).ToArray();
            var whiteImg = ImageLoader.Normalize(BitmapSource.Create(200, 120, 96, 96, System.Windows.Media.PixelFormats.Bgr24, null, whitePx, 600));
            var editor = new EditorWindow(whiteImg, null, "test", new EditorHost { SaveNew = _ => null, Pin = (_, _) => { } });
            editor.Show();
            Pump(400);
            editor.AddForTest(new Annotation { Kind = AnnotationKind.Rectangle, Points = { new Point(40, 30), new Point(160, 90) }, Color = System.Windows.Media.Colors.Red, Size = 4 });
            editor.AddForTest(new Annotation { Kind = AnnotationKind.Arrow, Points = { new Point(10, 110), new Point(60, 60) }, Color = System.Windows.Media.Colors.Blue, Size = 3 });
            Pump(200);
            var exported = editor.Export();
            string Px(int x, int y)
            {
                var b = new byte[4];
                exported.CopyPixels(new Int32Rect(x, y, 1, 1), b, 4, 0);
                return $"#{b[2]:X2}{b[1]:X2}{b[0]:X2}";
            }
            log.Add($"editor export: {exported.PixelWidth}x{exported.PixelHeight} boxEdge={Px(41, 60)} inside={Px(100, 60)} step={Px(180, 20)} (expect 200x120 red, white, green)");
            ImageFiles.TrySave(exported, System.IO.Path.Combine(System.IO.Path.GetDirectoryName(logFile)!, "editor-export.png"), out _);
            editor.Close();

            // text recognition on a rendered sentence
            var textVisual = new System.Windows.Media.DrawingVisual();
            using (var dc = textVisual.RenderOpen())
            {
                dc.DrawRectangle(System.Windows.Media.Brushes.White, null, new Rect(0, 0, 520, 70));
                var ft = new System.Windows.Media.FormattedText("Overskin reads 1234 words", System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new System.Windows.Media.Typeface("Segoe UI"), 30, System.Windows.Media.Brushes.Black, 1.0);
                dc.DrawText(ft, new Point(14, 14));
            }
            var textBmp = new System.Windows.Media.Imaging.RenderTargetBitmap(520, 70, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            textBmp.Render(textVisual);
            var ocr = TextRecognizer.RecognizeAsync(ImageLoader.Normalize(textBmp));
            for (int i = 0; i < 400 && !ocr.IsCompleted; i++) Pump(20);
            log.Add($"text recognition: {(ocr.IsCompletedSuccessfully ? $"\"{ocr.Result}\"" : ocr.Exception?.InnerException?.Message ?? "timed out")}");

            // saving: png and jpg, with collision-free names
            string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "overskin-selftest");
            string p1 = ImageFiles.NewPath(tmp, ".png");
            bool s1 = ImageFiles.TrySave(exported, p1, out _);
            string p2 = ImageFiles.NewPath(tmp, ".png");
            bool s2 = ImageFiles.TrySave(exported, ImageFiles.NewPath(tmp, ".jpg"), out _);
            log.Add($"save: png={s1} jpg={s2} secondNameDiffers={p1 != p2} files={System.IO.Directory.GetFiles(tmp).Length}");
            try { System.IO.Directory.Delete(tmp, true); } catch { }

            // ---- GIF: encode a clip with motion, timing and a full colour gradient, then read it back ----
            string outDir = System.IO.Path.GetDirectoryName(logFile)!;
            var clip = new GifClip { Width = 160, Height = 100 };
            for (int f = 0; f < 20; f++)
            {
                var px = new byte[160 * 100 * 4];
                for (int y = 0; y < 100; y++)
                    for (int x = 0; x < 160; x++)
                    {
                        int i = (y * 160 + x) * 4;
                        // background: a smooth gradient, with a red square moving across it
                        bool square = x >= f * 6 && x < f * 6 + 30 && y >= 35 && y < 65;
                        px[i] = square ? (byte)30 : (byte)(x * 255 / 159);
                        px[i + 1] = square ? (byte)30 : (byte)(y * 255 / 99);
                        px[i + 2] = square ? (byte)230 : (byte)128;
                        px[i + 3] = 255;
                    }
                clip.Frames.Add(px);
                clip.DelaysMs.Add(f == 0 ? 500 : 66);
            }
            string gifPath = System.IO.Path.Combine(outDir, "roundtrip.gif");
            using (var fs = System.IO.File.Create(gifPath))
            using (var gw = new GifWriter(fs, 160, 100))
                for (int f = 0; f < clip.Count; f++) gw.AddFrame(clip.Frames[f], clip.DelaysMs[f]);
            var gifBack = GifReader.Read(gifPath);
            int worst = 0;
            for (int f = 0; f < Math.Min(gifBack.Count, clip.Count); f++)
                for (int i = 0; i < clip.Frames[f].Length; i += 4)
                    for (int ch = 0; ch < 3; ch++)
                        worst = Math.Max(worst, Math.Abs(gifBack.Frames[f][i + ch] - clip.Frames[f][i + ch]));
            int squareAt = (50 * 160 + 15 * 6 + 5) * 4; // inside the square on frame 15
            var sq = gifBack.Count > 15 ? gifBack.Frames[15] : new byte[squareAt + 4];
            log.Add($"gif round trip: frames={gifBack.Count}/20 size={gifBack.Width}x{gifBack.Height} delays={gifBack.DelaysMs[0]}ms,{gifBack.DelaysMs[1]}ms total={gifBack.TotalMs}ms " +
                    $"worstChannelError={worst} squareOnFrame15=#{sq[squareAt + 2]:X2}{sq[squareAt + 1]:X2}{sq[squareAt]:X2} bytes={new System.IO.FileInfo(gifPath).Length}");

            // ---- captions: render both styles to look at ----
            var cap = new CaptionSettings(CaptionStyle.Caption, "when the code compiles on the first try", "", 1);
            var capPx = CaptionRenderer.Render(clip.FrameBitmap(10), 480, cap, out int cw, out int ch2);
            ImageFiles.TrySave(BitmapSource.Create(cw, ch2, 96, 96, System.Windows.Media.PixelFormats.Pbgra32, null, capPx, cw * 4), System.IO.Path.Combine(outDir, "caption-bar.png"), out _);
            var meme = new CaptionSettings(CaptionStyle.Meme, "one does not simply", "record a gif", 1);
            var memePx = CaptionRenderer.Render(clip.FrameBitmap(10), 480, meme, out int mw, out int mh);
            ImageFiles.TrySave(BitmapSource.Create(mw, mh, 96, 96, System.Windows.Media.PixelFormats.Pbgra32, null, memePx, mw * 4), System.IO.Path.Combine(outDir, "caption-meme.png"), out _);
            log.Add($"captions: bar={cw}x{ch2} (frame 480x300 plus the bar)  meme={mw}x{mh}");

            // ---- recorder: our own window flashing colours, recorded for a second ----
            var flash = new Window
            {
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Topmost = true, ShowInTaskbar = false,
                Left = 300, Top = 300, Width = 200, Height = 120,
                Background = System.Windows.Media.Brushes.Black,
            };
            flash.Show();
            Pump(400);
            var fo = flash.PointToScreen(new Point(0, 0));
            var recorder = new GifRecorder(ui);
            GifClip? recorded = null;
            bool done = false;
            recorder.Finished += c => { recorded = c; done = true; };
            recorder.Start(new Int32Rect((int)fo.X + 20, (int)fo.Y + 20, 160, 80), cursor: false);
            var colours = new[] { System.Windows.Media.Colors.Red, System.Windows.Media.Colors.Lime, System.Windows.Media.Colors.Blue };
            for (int i = 0; i < 6; i++)
            {
                flash.Background = new System.Windows.Media.SolidColorBrush(colours[i % 3]);
                Pump(170);
            }
            recorder.Stop();
            for (int i = 0; i < 100 && !done; i++) Pump(20);
            string recColours = "";
            if (recorded != null)
            {
                var distinct = new HashSet<string>();
                foreach (var fr in recorded.Frames) distinct.Add($"{fr[2] / 64}{fr[1] / 64}{fr[0] / 64}");
                recColours = string.Join(",", distinct);
            }
            log.Add($"recorder: frames={recorded?.Count ?? 0} size={recorded?.Width}x{recorded?.Height} durationMs={recorded?.TotalMs} distinctColours={recColours} (expect ~15 frames at 15 fps, red/green/blue seen)");
            flash.Close();

            // ---- pointer: drawing it must change the pixels around it (compared in memory, nothing kept) ----
            var cp = NativeMethods.CursorPos();
            var plain = NativeMethods.CaptureScreen(cp.X - 16, cp.Y - 16, 48, 48, cursor: false);
            var withPointer = NativeMethods.CaptureScreen(cp.X - 16, cp.Y - 16, 48, 48, cursor: true);
            int changed = 0;
            if (plain != null && withPointer != null)
            {
                var a1 = new byte[48 * 48 * 4]; var a2 = new byte[48 * 48 * 4];
                plain.CopyPixels(a1, 48 * 4, 0); withPointer.CopyPixels(a2, 48 * 4, 0);
                for (int i = 0; i < a1.Length; i += 4) if (a1[i] != a2[i] || a1[i + 1] != a2[i + 1] || a1[i + 2] != a2[i + 2]) changed++;
            }
            var ci = new NativeMethods.CURSORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.CURSORINFO>() };
            NativeMethods.GetCursorInfo(ref ci);
            log.Add($"pointer drawn into capture: changedPixels={changed} pointerShowing={(ci.flags & 1) == 1} (expect > 0 while a pointer is showing)");

            // popups load WPF's Accessibility assembly; single-file builds have failed to find it
            // (WPF only goes down that path while some program has a focus WinEvent hook installed,
            // as screen readers and remote-control tools do, so install one for the test)
            IntPtr hook = IntPtr.Zero;
            WinEventProc keepAlive = (_, _, _, _, _, _, _) => { };
            try
            {
                hook = SetWinEventHook(0x8005, 0x8005, IntPtr.Zero, keepAlive, 0, 0, 0);
                var menu = new System.Windows.Controls.ContextMenu();
                menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "test" });
                menu.IsOpen = true;
                DoEvents();
                menu.IsOpen = false;
                log.Add($"context menu: ok (focus hook {(hook != IntPtr.Zero ? "installed" : "NOT installed")})");
            }
            catch (Exception ex)
            {
                log.Add("context menu: FAILED " + ex.GetType().Name + " " + ex.Message);
            }
            finally
            {
                if (hook != IntPtr.Zero) UnhookWinEvent(hook);
                GC.KeepAlive(keepAlive);
            }

            NewFeatureTests(log);
            MoreFeatureTests(log);
            PassTests(log);
            RoundTests(log);
        }
        catch (Exception ex)
        {
            log.Add("EXCEPTION " + ex);
        }
        finally
        {
            try { notepad.Kill(); } catch { }
            System.IO.File.WriteAllLines(logFile, log);
        }
    }
#endif

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Whatever went wrong, never leave the mouse button held or other windows altered.
        _main?.Cleanup();
        e.Handled = true;
        string? log = CrashLog.Write(e.Exception, "interface");
        if (Quiet)
        {
            Shutdown(1);
            return;
        }
        string text = "Overskin hit an unexpected error and has to close.\n\n" + e.Exception.Message;
        if (log != null)
        {
            text += "\n\nA crash report was saved. Open its folder? Attaching it to a bug report helps a lot.";
            if (MessageBox.Show(text, "Overskin", MessageBoxButton.YesNo, MessageBoxImage.Error) == MessageBoxResult.Yes)
                CrashLog.OpenFolder();
        }
        else
        {
            MessageBox.Show(text, "Overskin", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        Shutdown(1);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _main?.Cleanup();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

#if DEBUG
    private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int obj, int child, uint thread, uint time);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint process, uint thread, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    private static void DoEvents() =>
        Current.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => { }));

    internal static void DoEventsForTest() => DoEvents();
#endif
}

#if DEBUG
/// <summary>For the self-test: moves and clicks the real mouse, in physical screen pixels.</summary>
internal static class TestMouse
{
    public static void MoveTo(Point screen)
    {
        // absolute coordinates are 0..65535 across the whole virtual desktop
        var vs = NativeMethods.VirtualScreen();
        double nx = (screen.X - vs.Left) * 65535.0 / Math.Max(1, vs.Width - 1);
        double ny = (screen.Y - vs.Top) * 65535.0 / Math.Max(1, vs.Height - 1);
        Send(NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_VIRTUALDESK,
            (int)Math.Round(Math.Clamp(nx, 0, 65535)), (int)Math.Round(Math.Clamp(ny, 0, 65535)));
    }

    public static void Button(bool down) =>
        Send(down ? NativeMethods.MOUSEEVENTF_LEFTDOWN : NativeMethods.MOUSEEVENTF_LEFTUP, 0, 0);

    private static void Send(uint flags, int dx, int dy)
    {
        var input = new NativeMethods.INPUT[1];
        input[0].type = NativeMethods.INPUT_MOUSE;
        input[0].u.mi = new NativeMethods.MOUSEINPUT { dx = dx, dy = dy, dwFlags = flags };
        NativeMethods.SendInput(1, input, System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.INPUT>());
    }
}
#endif
