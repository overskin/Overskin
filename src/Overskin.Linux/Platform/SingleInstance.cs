using System.IO.Pipes;
using System.Text;

namespace Overskin.Linux.Platform;

/// <summary>
/// Only one Overskin runs. Starting it again (for example "overskin --capture" from a keyboard
/// shortcut) hands the command to the one that's running and exits.
/// </summary>
public static class SingleInstance
{
    private static string PipeName => "overskin-" + Environment.UserName;

    public static bool TrySend(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(300);
            var bytes = Encoding.UTF8.GetBytes(string.Join("\n", args) + "\n\n");
            client.Write(bytes);
            client.Flush();
            return true;
        }
        catch { return false; }
    }

    public static void Listen(Action<string[]> onCommand, CancellationToken stop)
    {
        Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(stop);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var args = new List<string>();
                    string? line;
                    while ((line = await reader.ReadLineAsync(stop)) is { Length: > 0 }) args.Add(line);
                    onCommand(args.ToArray());
                }
                catch (OperationCanceledException) { return; }
                catch { await Task.Delay(200); }
            }
        });
    }
}
