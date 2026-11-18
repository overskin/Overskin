using System.Collections.Concurrent;
using Overskin.Linux.Core;
using Tmds.DBus.Protocol;

namespace Overskin.Linux.Platform;

/// <summary>
/// The desktop portal (org.freedesktop.portal.*): the sanctioned way to read and record the screen
/// on Wayland. The desktop may ask the user for permission.
/// </summary>
public static class Portal
{
    private const string Bus = "org.freedesktop.portal.Desktop";
    private const string ObjectPath = "/org/freedesktop/portal/desktop";

    /// <summary>One option in an a{sv} dictionary.</summary>
    public readonly record struct Option(string Key, object Value);

    /// <summary>
    /// Calls a portal method that answers through a Request object, and waits for that answer.
    /// <paramref name="leading"/> are the arguments before the options (strings or object paths).
    /// </summary>
    private static async Task<(uint Code, Dictionary<string, VariantValue> Results)> RequestAsync(
        Connection connection, string iface, string method, string signature, object[] leading, List<Option> options, TimeSpan timeout)
    {
        var token = "overskin" + Guid.NewGuid().ToString("N")[..12];
        var sender = connection.UniqueName!.TrimStart(':').Replace('.', '_');
        var expected = $"/org/freedesktop/portal/desktop/request/{sender}/{token}";
        options.Insert(0, new Option("handle_token", token));

        // every Response that reaches us, by request path: the reply can arrive before the call
        // returns, and older portals don't use the path we predict, so match on both afterwards
        var responses = new ConcurrentDictionary<string, (uint, Dictionary<string, VariantValue>)>();
        var arrived = new SemaphoreSlim(0);
        var rule = new MatchRule { Type = MessageType.Signal, Interface = "org.freedesktop.portal.Request", Member = "Response" };
        using var match = await connection.AddMatchAsync(rule,
            static (Message message, object? _) =>
            {
                var path = message.PathAsString ?? "";
                var reader = message.GetBodyReader();
                var code = reader.ReadUInt32();
                return (path, code, reader.ReadDictionaryOfStringToVariantValue());
            },
            (Exception? ex, (string Path, uint Code, Dictionary<string, VariantValue> Results) value, object? _, object? _) =>
            {
                if (ex != null) { Log.Write("portal: reading a reply failed: " + ex.Message); return; }
                responses[value.Path] = (value.Code, value.Results);
                arrived.Release();
            },
            ObserverFlags.None, null, null, false);

        var handle = await connection.CallMethodAsync(Call(connection, iface, method, signature, leading, options),
            static (Message m, object? _) => m.GetBodyReader().ReadObjectPathAsString(), null);
        Log.Write($"portal: {iface.Split('.').Last()}.{method} asked, request {handle}");

        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (responses.TryGetValue(handle, out var r) || responses.TryGetValue(expected, out r))
            {
                Log.Write($"portal: {method} answered {r.Item1}");
                return r;
            }
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero || !await arrived.WaitAsync(left))
                throw new TimeoutException("the desktop portal didn't answer");
        }
    }

    private static MessageBuffer Call(Connection connection, string iface, string method, string signature, object[] leading, List<Option> options)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Bus, ObjectPath, iface, method, signature, MessageFlags.None);
        int i = 0;
        foreach (var arg in leading)
        {
            // the signature says whether each leading argument is a string or an object path
            if (signature[i] == 'o') writer.WriteObjectPath((string)arg);
            else writer.WriteString((string)arg);
            i++;
        }
        var dict = writer.WriteDictionaryStart();
        foreach (var (key, value) in options)
        {
            writer.WriteDictionaryEntryStart();
            writer.WriteString(key);
            switch (value)
            {
                case string s: writer.WriteVariantString(s); break;
                case bool b: writer.WriteVariantBool(b); break;
                case uint u: writer.WriteVariantUInt32(u); break;
                case int n: writer.WriteVariantInt32(n); break;
                default: throw new ArgumentException("unsupported option " + key);
            }
        }
        writer.WriteDictionaryEnd(dict);
        return writer.CreateMessage();
    }

    private static async Task<Connection> ConnectAsync()
    {
        var connection = new Connection(Address.Session!);
        await connection.ConnectAsync();
        return connection;
    }

    /// <summary>Returns the local path of the screenshot, or null if the user cancelled.</summary>
    public static async Task<string?> ScreenshotAsync(bool interactive, TimeSpan timeout)
    {
        using var connection = await ConnectAsync();
        var (code, results) = await RequestAsync(connection, "org.freedesktop.portal.Screenshot", "Screenshot", "sa{sv}",
            new object[] { "" }, new List<Option> { new("interactive", interactive) }, timeout);
        if (code != 0 || !results.TryGetValue("uri", out var uri)) return null; // 1 = cancelled, 2 = refused or failed
        return new Uri(uri.GetString()).LocalPath;
    }

    // ---- screen recording ---------------------------------------------------------------------

    /// <summary>A screen-cast the desktop agreed to: a PipeWire stream to record from. Dispose to end it.</summary>
    public sealed class ScreenCast : IDisposable
    {
        internal Connection? Connection;
        internal string SessionPath = "";
        public uint Node { get; internal set; }
        public int Width { get; internal set; }
        public int Height { get; internal set; }

        public void Dispose()
        {
            if (Connection == null) return;
            try
            {
                using var writer = Connection.GetMessageWriter();
                writer.WriteMethodCallHeader(Bus, SessionPath, "org.freedesktop.portal.Session", "Close", null, MessageFlags.NoReplyExpected);
                Connection.TrySendMessage(writer.CreateMessage());
            }
            catch { }
            Connection.Dispose();
            Connection = null;
        }
    }

    /// <summary>
    /// Asks the desktop to share a screen or window (it shows its own chooser). Remembers the
    /// choice, so later recordings needn't ask again. Null if the user cancelled.
    /// </summary>
    public static async Task<ScreenCast?> StartScreenCastAsync(bool showPointer)
    {
        var connection = await ConnectAsync();
        var cast = new ScreenCast { Connection = connection };
        try
        {
            const string Iface = "org.freedesktop.portal.ScreenCast";
            var (code, results) = await RequestAsync(connection, Iface, "CreateSession", "a{sv}", Array.Empty<object>(),
                new List<Option> { new("session_handle_token", "overskin" + Guid.NewGuid().ToString("N")[..8]) }, TimeSpan.FromSeconds(30));
            if (code != 0) { cast.Dispose(); return null; }
            cast.SessionPath = results["session_handle"].Type == VariantValueType.ObjectPath
                ? results["session_handle"].GetObjectPathAsString()
                : results["session_handle"].GetString();

            var select = new List<Option>
            {
                new("types", 1u | 2u),                     // a monitor or a window
                new("multiple", false),
                new("cursor_mode", showPointer ? 2u : 1u), // drawn into the video, or left out
                new("persist_mode", 2u),                   // remember until revoked
            };
            if (App.Settings.ScreenCastToken is { Length: > 0 } restore) select.Add(new("restore_token", restore));
            (code, _) = await RequestAsync(connection, Iface, "SelectSources", "oa{sv}", new object[] { cast.SessionPath }, select, TimeSpan.FromMinutes(2));
            if (code != 0) { cast.Dispose(); return null; }

            (code, results) = await RequestAsync(connection, Iface, "Start", "osa{sv}", new object[] { cast.SessionPath, "" }, new List<Option>(), TimeSpan.FromMinutes(3));
            if (code != 0) { cast.Dispose(); return null; }

            if (results.TryGetValue("restore_token", out var token))
            {
                App.Settings.ScreenCastToken = token.GetString();
                App.Settings.Save();
            }
            var stream = results["streams"].GetItem(0);
            cast.Node = stream.GetItem(0).GetUInt32();
            var props = stream.GetItem(1);
            for (int i = 0; i < props.Count; i++)
            {
                var entry = props.GetDictionaryEntry(i);
                if (entry.Key.GetString() != "size") continue;
                var size = entry.Value.Type == VariantValueType.Variant ? entry.Value.GetVariantValue() : entry.Value;
                cast.Width = size.GetItem(0).GetInt32();
                cast.Height = size.GetItem(1).GetInt32();
            }
            Log.Write($"portal: screen-cast node {cast.Node}, {cast.Width}x{cast.Height}");
            return cast;
        }
        catch
        {
            cast.Dispose();
            throw;
        }
    }
}
