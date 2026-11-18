using Tmds.DBus.Protocol;

namespace Overskin.Linux.Platform;

/// <summary>
/// org.freedesktop.portal.Screenshot: the sanctioned way to read the screen on Wayland.
/// The desktop may ask the user for permission the first time.
/// </summary>
public static class Portal
{
    private const string Bus = "org.freedesktop.portal.Desktop";
    private const string ObjectPath = "/org/freedesktop/portal/desktop";

    /// <summary>Returns the local path of the screenshot, or null if the user cancelled.</summary>
    public static async Task<string?> ScreenshotAsync(bool interactive)
    {
        using var connection = new Connection(Address.Session!);
        await connection.ConnectAsync();

        // The reply arrives as a signal on a request object whose path we can predict from a token,
        // so listen there before making the call
        var token = "overskin" + Guid.NewGuid().ToString("N")[..12];
        var sender = connection.UniqueName!.TrimStart(':').Replace('.', '_');
        var requestPath = $"/org/freedesktop/portal/desktop/request/{sender}/{token}";

        var done = new TaskCompletionSource<(uint Code, string? Uri)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rule = new MatchRule
        {
            Type = MessageType.Signal,
            Interface = "org.freedesktop.portal.Request",
            Member = "Response",
            Path = requestPath,
        };
        using var match = await connection.AddMatchAsync(rule,
            static (Message message, object? _) =>
            {
                var reader = message.GetBodyReader();
                var code = reader.ReadUInt32();
                var results = reader.ReadDictionaryOfStringToVariantValue();
                return (code, results.TryGetValue("uri", out var uri) ? uri.GetString() : null);
            },
            (Exception? ex, (uint, string?) value, object? _, object? _) =>
            {
                if (ex != null) done.TrySetException(ex);
                else done.TrySetResult(value);
            },
            ObserverFlags.None, null, null, true);

        await connection.CallMethodAsync(CreateCall(connection, token, interactive));

        var finished = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromMinutes(2)));
        if (finished != done.Task) throw new TimeoutException("the desktop portal didn't answer");

        var (code, uri) = await done.Task;
        if (code != 0 || uri == null) return null; // 1 = cancelled, 2 = refused
        return new Uri(uri).LocalPath;
    }

    private static MessageBuffer CreateCall(Connection connection, string token, bool interactive)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Bus, ObjectPath, "org.freedesktop.portal.Screenshot", "Screenshot", "sa{sv}", MessageFlags.None);
        writer.WriteString(""); // no parent window
        var dict = writer.WriteDictionaryStart();
        writer.WriteDictionaryEntryStart();
        writer.WriteString("handle_token");
        writer.WriteVariantString(token);
        writer.WriteDictionaryEntryStart();
        writer.WriteString("interactive");
        writer.WriteVariantBool(interactive);
        writer.WriteDictionaryEnd(dict);
        return writer.CreateMessage();
    }
}
