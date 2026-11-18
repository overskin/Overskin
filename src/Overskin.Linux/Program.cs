using Avalonia;
using Overskin.Linux.Platform;

namespace Overskin.Linux;

public static class Program
{
    public const string Usage = """
        Overskin — screenshots, done right.

          overskin                 open Overskin (or bring it forward)
          overskin --capture       pick an area of the screen
          overskin --screen        capture the whole screen
          overskin --delay 3       wait before capturing (with --capture or --screen)
          overskin --open FILE     open a picture in the editor
          overskin --pin FILE      pin a picture on top of everything
          overskin --tray          start in the tray without opening the window
          overskin --quit          close the running Overskin

        Wayland desktops don't let apps set global shortcuts, so add one in your system's keyboard
        settings that runs "overskin --capture".
        """;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine(Usage);
            return 0;
        }

        bool alone = args.Contains("--selftest") || args.Contains("--snapshots");
        if (!alone && SingleInstance.TrySend(args)) return 0;

        App.StartArgs = args;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(new X11PlatformOptions { WmClass = "overskin" })
            .LogToTrace();
}
