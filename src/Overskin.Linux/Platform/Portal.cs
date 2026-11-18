using System.Collections.Concurrent;
using Overskin.Linux.Core;
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
    public static async Task<string?> ScreenshotAsync(bool interactive, TimeSpan timeout)
    {
        using var connection = new Connection(Address.Session!);
        await connection.ConnectAsync();

        var token = "overskin" + Guid.NewGuid().ToString("N")[..12];
        var sender = connection.UniqueName!.TrimStart(':').Replace('.', '_');
        var expected = $"/org/freedesktop/portal/desktop/request/{sender}/{token}";

        // Every Response that reaches us, by request path: the reply can arrive before the call
        // returns, and older portals don't use the path we predict, so match on both afterwards
        var responses = new ConcurrentDictionary<string, (uint Code, string? Uri)>();
        var arrived = new SemaphoreSlim(0);
        var rule = new MatchRule
        {
            Type = MessageType.Signal,
            Interface = "org.freedesktop.portal.Request",
            Member = "Response",
        };
        using var match = await connection.AddMatchAsync(rule,
            static (Message message, object? _) =>
            {
                var path = message.PathAsString ?? "";
                var reader = message.GetBodyReader();
                var code = reader.ReadUInt32();
                var results = reader.ReadDictionaryOfStringToVariantValue();
                return (path, code, results.TryGetValue("uri", out var uri) ? uri.GetString() : null);
            },
            (Exception? ex, (string Path, uint Code, string? Uri) value, object? _, object? _) =>
            {
                if (ex != null) { Log.Write("portal: reading the reply failed: " + ex.Message); return; }
                responses[value.Path] = (value.Code, value.Uri);
                arrived.Release();
            },
            ObserverFlags.None, null, null, false);

        var handle = await connection.CallMethodAsync(CreateCall(connection, token, interactive),
            static (Message m, object? _) => m.GetBodyReader().ReadObjectPathAsString(), null);
        Log.Write($"portal: asked for a screenshot (interactive={interactive}), request {handle}");

        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (responses.TryGetValue(handle, out var r) || responses.TryGetValue(expected, out r))
            {
                Log.Write($"portal: answered {r.Code} {r.Uri}");
                if (r.Code != 0 || r.Uri == null) return null; // 1 = cancelled, 2 = refused or failed
                return new Uri(r.Uri).LocalPath;
            }
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero || !await arrived.WaitAsync(left))
                throw new TimeoutException("the desktop portal didn't answer");
        }
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
