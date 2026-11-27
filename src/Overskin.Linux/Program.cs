using Avalonia;
using Overskin.Linux.Core;
using Overskin.Linux.Platform;

namespace Overskin.Linux;

public static class Program
{
    public const string Usage = """
        Overskin — screenshots, done right.

          overskin                 open Overskin (or bring it forward)
          overskin --capture       pick an area of the screen
          overskin --screen        capture the whole screen
          overskin --delay 3       wait first (with --capture, --screen, --record or --gif)
          overskin --record        record a video of an area (again to stop)
          overskin --gif           record a GIF of an area
          overskin --stop          stop the recording
          overskin --scroll        scrolling capture of a long page
          overskin --steps         start or finish recording steps (a how-to guide)
          overskin --step          add a step now (for Wayland, where clicks can't be seen)
          overskin --recent        the latest captures, to copy or drag
          overskin --open FILE     open a picture in the editor
          overskin --pin FILE      pin a picture on top of everything
          overskin --tray          start in the tray without opening the window
          overskin --quit          close the running Overskin

        Wayland desktops don't let apps set global shortcuts, so add one in your system's keyboard
        settings that runs "overskin --capture". On a Mac, Overskin has its own hotkeys (Settings).
        """;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine(Usage);
            return 0;
        }

        if (OperatingSystem.IsMacOS())
        {
            // opened from Finder, an app only searches /usr/bin and friends: add Homebrew's folders,
            // where ffmpeg and tesseract live
            var path = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin:/usr/sbin:/sbin";
            foreach (var dir in new[] { "/opt/homebrew/bin", "/usr/local/bin" })
                if (!path.Split(':').Contains(dir)) path += ":" + dir;
            Environment.SetEnvironmentVariable("PATH", path);
        }

        bool alone = args.Contains("--selftest") || args.Contains("--snapshots");
        if (!alone && SingleInstance.TrySend(args)) return 0;

        App.StartArgs = args;
        // a crash anywhere, even before a window opens, leaves its reason in the log
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("crash: " + e.ExceptionObject);
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        }
        catch (Exception e)
        {
            Log.Write("crash at start: " + e);
            Console.Error.WriteLine("Overskin couldn't start: " + e.Message + "\nDetails are in " + Log.FilePath);
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(new X11PlatformOptions { WmClass = "overskin" })
            .With(new MacOSPlatformOptions { ShowInDock = true })
            .LogToTrace();
}
