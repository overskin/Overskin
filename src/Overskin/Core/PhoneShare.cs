using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Overskin;

/// <summary>
/// Sends a capture to a phone on the same Wi-Fi: Overskin serves that one file on this PC (to a
/// secret address nobody can guess), shows the address as a QR code, and the phone's camera
/// opens it. Nothing goes through the internet. It stops when the window closes, or after
/// 15 minutes.
/// </summary>
public sealed partial class PhoneShare : IDisposable
{
    private readonly string _path;
    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();

    /// <summary>The address to open on the phone.</summary>
    public string Url { get; }

    /// <summary>Raised (on a background thread) each time the phone downloads the file.</summary>
    public event Action? Sent;

    private PhoneShare(string path, IPAddress ip)
    {
        _path = path;
        _listener = new TcpListener(IPAddress.Any, 0);
        _listener.Start();
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Url = $"http://{ip}:{port}/{_token}";
        _ = Task.Run(Serve);
        _stop.CancelAfter(TimeSpan.FromMinutes(15));
        _stop.Token.Register(() => { try { _listener.Stop(); } catch { } });
    }

    /// <summary>Starts sharing; null when this PC isn't on a home network.</summary>
    public static PhoneShare? Start(string path) => LocalAddress() is { } ip ? new PhoneShare(path, ip) : null;

    /// <summary>This PC's address on the home network (Wi-Fi or cable), or null.</summary>
    internal static IPAddress? LocalAddress()
    {
        var candidates = new List<(IPAddress Ip, int Rank)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var props = nic.GetIPProperties();
            bool gateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            string name = (nic.Name + " " + nic.Description).ToLowerInvariant();
            bool virtualCard = name.Contains("virtual") || name.Contains("vethernet") || name.Contains("vmware") || name.Contains("hyper-v") || name.Contains("vpn") || name.Contains("tailscale") || name.Contains("zerotier");
            foreach (var a in props.UnicastAddresses)
            {
                if (a.Address.AddressFamily != AddressFamily.InterNetwork || !IsPrivate(a.Address)) continue;
                int rank = (gateway ? 0 : 10) + (virtualCard ? 5 : 0) + (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 0 : 1);
                candidates.Add((a.Address, rank));
            }
        }
        return candidates.OrderBy(c => c.Rank).Select(c => c.Ip).FirstOrDefault();
    }

    private static bool IsPrivate(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }

    private async Task Serve()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch { break; }
            _ = Task.Run(() => Answer(client));
        }
    }

    /// <summary>One request: the page with the picture on it, or the file itself. Anything else is "not found".</summary>
    private async Task Answer(TcpClient client)
    {
        using var _ = client;
        try
        {
            client.ReceiveTimeout = 10000;
            var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            string? first = await reader.ReadLineAsync();
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { } // the rest of the request's header
            string target = first?.Split(' ') is { Length: >= 2 } parts && parts[0] is "GET" or "HEAD" ? parts[1] : "";
            bool head = first?.StartsWith("HEAD") == true;
            string name = Path.GetFileName(_path);
            if (target == "/" + _token)
            {
                await Reply(stream, "200 OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(Page(name)), head);
                return;
            }
            if (target == $"/{_token}/file" || target == $"/{_token}/download")
            {
                bool download = target.EndsWith("/download");
                var bytes = await File.ReadAllBytesAsync(_path);
                await Reply(stream, "200 OK", TypeOf(_path), bytes, head,
                    download ? $"Content-Disposition: attachment; filename=\"{Uri.EscapeDataString(name)}\"\r\n" : "");
                if (download && !head) Sent?.Invoke();
                return;
            }
            await Reply(stream, "404 Not Found", "text/plain", Encoding.ASCII.GetBytes("Not found"), head);
        }
        catch
        {
            // the phone went away mid-request: nothing to do
        }
    }

    private static async Task Reply(NetworkStream stream, string status, string type, byte[] body, bool head, string extra = "")
    {
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\n{extra}Cache-Control: no-store\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header);
        if (!head) await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    private static string TypeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".bmp" => "image/bmp",
        ".webp" => "image/webp", ".mp4" => "video/mp4", _ => "application/octet-stream",
    };

    /// <summary>
    /// The page the phone opens: the capture, with Share and Save to Photos. A page on the home
    /// network isn't "secure" to a phone's browser, so it mostly can't open the share sheet
    /// itself; then the buttons show how: press and hold the picture, where both are offered.
    /// </summary>
    private string Page(string name)
    {
        string safe = WebUtility.HtmlEncode(name);
        string js = System.Text.Json.JsonSerializer.Serialize(name);
        bool video = Path.GetExtension(_path).Equals(".mp4", StringComparison.OrdinalIgnoreCase);
        string type = video ? "video/mp4" : Path.GetExtension(_path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp", _ => "image/png",
        };
        string shown = video
            ? $"<video id=\"pic\" src=\"/{_token}/file\" controls playsinline></video>"
            : $"<img id=\"pic\" src=\"/{_token}/file\" alt=\"{safe}\">";
        return $$"""
            <!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{safe}}</title>
            <style>
            body{margin:0;background:#121316;color:#e7e8eb;font:16px system-ui,-apple-system,Segoe UI,sans-serif;text-align:center}
            img,video{max-width:100%;max-height:70vh;display:block;margin:16px auto;border-radius:10px;transition:box-shadow .3s}
            img.glow,video.glow{box-shadow:0 0 0 3px #4f8cff,0 0 24px #4f8cff}
            .row{display:flex;gap:10px;justify-content:center;margin:8px 16px 14px}
            button,a.b{flex:1;max-width:190px;padding:14px 10px;border-radius:12px;border:0;font:600 16px system-ui,-apple-system,sans-serif;text-decoration:none;color:#fff}
            .go{background:#4f8cff}.alt{background:#2a2d35}
            #tip{display:none;margin:0 16px 16px;padding:12px 14px;border-radius:12px;background:#1e2a44;color:#cfe0ff;font-size:15px;line-height:1.4}
            p{color:#9094a0;font-size:13px;margin:0 16px 24px}
            </style></head><body>
            {{shown}}
            <div class="row">
            <button class="go" id="share">Share</button>
            <button class="alt" id="save">Save to Photos</button>
            </div>
            <div id="tip"></div>
            <p>{{safe}} · from Overskin on your PC · <a href="/{{_token}}/download" style="color:#9094a0">download the file</a></p>
            <script>
            var video={{(video ? "true" : "false")}}, name={{js}}, type="{{type}}";
            var ios=/iPhone|iPad|iPod/.test(navigator.userAgent)||(navigator.platform==="MacIntel"&&navigator.maxTouchPoints>1);
            var pic=document.getElementById("pic"), tip=document.getElementById("tip");
            function show(t){tip.textContent=t;tip.style.display="block";pic.classList.add("glow");setTimeout(function(){pic.classList.remove("glow")},2500);pic.scrollIntoView({behavior:"smooth",block:"center"});}
            function file(){return fetch("/{{_token}}/file").then(function(r){return r.blob()}).then(function(b){return new File([b],name,{type:type})});}
            function canShare(){return navigator.share&&navigator.canShare;}
            function tryShare(how){
              if(!canShare()){how();return;}
              file().then(function(f){
                if(!navigator.canShare({files:[f]})){how();return;}
                return navigator.share({files:[f],title:name}).catch(function(){});
              }).catch(how);
            }
            document.getElementById("share").onclick=function(){tryShare(function(){
              show(video?(ios?"Tap the picture's share button, or download the file and share it from Files.":"Tap the ⋮ on the video, then Share, or download the file and share it.")
                :"Press and hold the picture, then tap Share.");});};
            document.getElementById("save").onclick=function(){
              if(video){
                if(ios){tryShare(function(){location.href="/{{_token}}/download";show("It's in the Files app, in Downloads. Open it there and tap Share, then Save Video.");});}
                else{location.href="/{{_token}}/download";show("Saved. It's in your Photos or Gallery app, under Downloads.");}
                return;
              }
              if(ios)tryShare(function(){show("Press and hold the picture, then tap Save to Photos.");});
              else{location.href="/{{_token}}/download";show("Saved. It's in your Photos or Gallery app, under Downloads. (Or press and hold the picture, then Download image.)");}
            };
            </script>
            </body></html>
            """;
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener.Stop(); } catch { }
    }

    // ---- the QR code -------------------------------------------------------------------------
}
