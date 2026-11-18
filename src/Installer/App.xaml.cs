using System.Diagnostics;
using System.Windows;

namespace OverskinSetup;

/// <summary>
/// OverskinSetup.exe                 install, with the window
/// Uninstall.exe --uninstall         remove, with the window
/// Add --quiet for no window. Install options: --dir PATH, --no-startmenu, --desktop,
/// --startup or --no-startup (otherwise left as it is), --launch. Uninstall options: --dir PATH, --remove-settings. --log FILE records
/// errors in quiet mode. --payload FILE installs that Overskin.exe instead of the bundled one.
/// </summary>
public partial class App : Application
{
    public sealed class Args
    {
        public bool Uninstall, Quiet, Relaunched, NoStartMenu, Desktop, Launch, RemoveSettings;
        public bool? Startup;
        public string? Dir, Payload, Log, Original;

        public static Args Parse(string[] a)
        {
            var r = new Args();
            for (int i = 0; i < a.Length; i++)
            {
                string Next() => i + 1 < a.Length ? a[++i] : "";
                switch (a[i].ToLowerInvariant())
                {
                    case "--uninstall": r.Uninstall = true; break;
                    case "--quiet": r.Quiet = true; break;
                    case "--relaunched": r.Relaunched = true; break;
                    case "--no-startmenu": r.NoStartMenu = true; break;
                    case "--desktop": r.Desktop = true; break;
                    case "--startup": r.Startup = true; break;
                    case "--no-startup": r.Startup = false; break;
                    case "--launch": r.Launch = true; break;
                    case "--remove-settings": r.RemoveSettings = true; break;
                    case "--dir": r.Dir = Next(); break;
                    case "--payload": r.Payload = Next(); break;
                    case "--log": r.Log = Next(); break;
                    case "--original": r.Original = Next(); break;
                }
            }
            return r;
        }
    }

    public static Args Options { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var o = Options = Args.Parse(e.Args);

        // developer check: --snapshot DIR [--uninstall] renders the pages offscreen and exits
        int snap = Array.FindIndex(e.Args, a => a.Equals("--snapshot", StringComparison.OrdinalIgnoreCase));
        if (snap >= 0 && snap + 1 < e.Args.Length)
        {
            string dir = e.Args[snap + 1];
            o.Dir ??= o.Uninstall ? Setup.DefaultFolder : null;
            var w = new SetupWindow(o) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowInTaskbar = false };
            MainWindow = w;
            w.Show();
            w.Snapshot(dir).ContinueWith(_ => Dispatcher.Invoke(() => Shutdown()));
            return;
        }

        if (o.Uninstall)
        {
            o.Dir ??= Setup.InstalledFolder ?? Path.GetDirectoryName(Payload.SelfPath);

            // An exe can't delete itself, so the uninstaller runs from a temporary copy.
            if (!o.Relaunched)
            {
                Shutdown(RelaunchFromTemp(e.Args, o));
                return;
            }
        }

        if (o.Quiet)
        {
            Shutdown(RunQuiet(o));
            return;
        }

        var window = new SetupWindow(o);
        MainWindow = window;
        window.Show();
    }

    private static int RelaunchFromTemp(string[] args, Args o)
    {
        try
        {
            string temp = Path.Combine(Path.GetTempPath(), $"OverskinUninstall-{Guid.NewGuid():N}.exe");
            Payload.WriteEngine(temp);
            var argList = new List<string>(args) { "--relaunched", "--original", Payload.SelfPath };
            if (!args.Any(a => a.Equals("--dir", StringComparison.OrdinalIgnoreCase))) { argList.Add("--dir"); argList.Add(o.Dir!); }
            var p = Process.Start(new ProcessStartInfo(temp, string.Join(" ", argList.Select(Quote))) { UseShellExecute = false });
            if (o.Quiet && p != null)
            {
                p.WaitForExit();
                return p.ExitCode;
            }
            return 0;
        }
        catch (Exception ex)
        {
            if (o.Quiet) { Log(o, ex); return 1; }
            MessageBox.Show(ex.Message, "Overskin", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }

    private static int RunQuiet(Args o)
    {
        try
        {
            if (o.Uninstall)
            {
                Setup.Uninstall(o.Dir!, o.RemoveSettings, (_, _) => { });
                CleanUpAfterUninstall(o, removed: true);
            }
            else
            {
                var opts = new InstallOptions
                {
                    Folder = o.Dir ?? Setup.InstalledFolder ?? Setup.DefaultFolder,
                    StartMenu = !o.NoStartMenu,
                    Desktop = o.Desktop,
                    Startup = o.Startup,
                };
                Setup.Install(opts, o.Payload, (_, _) => { });
                if (o.Launch) LaunchApp(opts.Folder);
            }
            return 0;
        }
        catch (Exception ex)
        {
            Log(o, ex);
            return 1;
        }
    }

    public static void LaunchApp(string folder)
    {
        string exe = Path.Combine(Setup.Normalize(folder), Setup.ExeName);
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! });
    }

    /// <summary>
    /// When the temporary uninstaller copy exits: remove it, and if Overskin was removed, the
    /// original Uninstall.exe and the install folder (rmdir only removes it once empty).
    /// </summary>
    public static void CleanUpAfterUninstall(Args o, bool removed)
    {
        try
        {
            var cmd = new List<string> { "ping 127.0.0.1 -n 3 >nul" };
            if (removed && o.Original != null && Path.GetFileName(o.Original).Equals(Setup.UninstallerName, StringComparison.OrdinalIgnoreCase))
                cmd.Add($"del /f /q {Quote(o.Original)}");
            if (removed && o.Dir != null) cmd.Add($"rmdir {Quote(Setup.Normalize(o.Dir))}");
            cmd.Add($"del /f /q {Quote(Payload.SelfPath)}");
            Process.Start(new ProcessStartInfo("cmd.exe", "/c " + string.Join(" & ", cmd))
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch { }
    }

    private static string Quote(string s) => "\"" + s.TrimEnd('\\') + "\"";

    private static void Log(Args o, Exception ex)
    {
        if (o.Log == null) return;
        try { File.AppendAllText(o.Log, $"{DateTime.Now:s} {ex.GetType().Name}: {ex.Message}{Environment.NewLine}"); } catch { }
    }
}
