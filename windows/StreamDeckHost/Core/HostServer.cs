using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StreamDeckHost.Core;

/// <summary>
/// WebSocket server the Android app talks to (Wi-Fi, or USB via "adb reverse"), plus UDP discovery.
/// Protocol is documented in docs/PROTOCOL.md.
/// </summary>
public sealed class HostServer : IDisposable
{
    public const int DiscoveryPort = 47801;

    sealed class Client
    {
        public Guid Id = Guid.NewGuid();
        public WebSocket Ws = null!;
        public string Ip = "";
        public DeviceRecord? Device;
        public readonly SemaphoreSlim SendLock = new(1, 1);
    }

    readonly SettingsStore store;
    readonly ActionExecutor exec;
    readonly LayoutStore layout;
    public ActionExecutor Exec => exec;
    readonly ConcurrentDictionary<Guid, Client> clients = new();
    readonly ConcurrentDictionary<string, (int fails, DateTime until)> failures = new();
    readonly CancellationTokenSource cts = new();
    Microsoft.AspNetCore.Builder.WebApplication? app;
    UdpClient? udp;

    string pairSecret = NewSecret();
    DateTime pairExpiry = DateTime.UtcNow.AddMinutes(10);
    string lastStatusJson = "";
    int cachedBrightness = -1;
    DateTime brightnessAt = DateTime.MinValue;

    public event Action<string>? Log;
    public event Action? Changed;
    public string PcName => Environment.MachineName;
    public int Port => store.Data.Port;
    public int ConnectedCount => clients.Values.Count(c => c.Device != null);
    public IReadOnlyList<string> ConnectedDeviceIds => clients.Values.Where(c => c.Device != null).Select(c => c.Device!.Id).ToList();
    public string? DevPairingFile { get; set; }
    public Tunnel Tunnel { get; }

    public HostServer(SettingsStore store, LayoutStore layout)
    {
        this.store = store;
        this.layout = layout;
        exec = new ActionExecutor(store);
        layout.Changed += () => _ = BroadcastLayout();
        Tunnel = new Tunnel(store.Data.Port);
        Tunnel.Changed += () => { WriteDevPairing(); Changed?.Invoke(); };
    }

    /// <summary>Applies the "internet access" settings (start/stop the tunnel). Call after changing them.</summary>
    public void ApplyRemoteSettings()
    {
        if (store.Data.InternetTunnel) Tunnel.Start(); else Tunnel.Stop();
        WriteDevPairing();
        Changed?.Invoke();
    }

    /// <summary>Addresses a phone can try, best-first: home LAN, Tailscale, then the public address / tunnel.</summary>
    public List<string> AllAddresses()
    {
        var list = LocalAddresses();
        list.AddRange(TailscaleAddresses());
        var remote = store.Data.RemoteAddress.Trim();
        if (remote.Length > 0) list.Add(remote);
        if (Tunnel.Url != null) list.Add(Tunnel.Url);
        return list.Distinct().ToList();
    }

    /// <summary>Tailscale uses 100.64.0.0/10. Included automatically so phones on the same tailnet work from any network.</summary>
    public static List<string> TailscaleAddresses()
    {
        var result = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var b = ua.Address.GetAddressBytes();
                    if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) result.Add(ua.Address.ToString());
                }
            }
        }
        catch { }
        return result;
    }

    static string NewSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    void L(string msg) => Log?.Invoke($"{DateTime.Now:HH:mm:ss}  {msg}");

    // ------------------------------------------------------------------ pairing
    public string CurrentPairingSecret()
    {
        if (DateTime.UtcNow > pairExpiry) RotateSecret();
        return pairSecret;
    }

    public void RotateSecret()
    {
        pairSecret = NewSecret();
        pairExpiry = DateTime.UtcNow.AddMinutes(10);
        WriteDevPairing();
        Changed?.Invoke();
    }

    /// <summary>JSON shown in the QR code.</summary>
    public string PairingPayload() => JsonSerializer.Serialize(new
    {
        v = 1,
        n = PcName,
        p = Port,
        h = AllAddresses(),
        s = CurrentPairingSecret(),
    });

    void WriteDevPairing()
    {
        if (string.IsNullOrEmpty(DevPairingFile)) return;
        try { File.WriteAllText(DevPairingFile, PairingPayload()); } catch { }
    }

    public static List<string> LocalAddresses()
    {
        var result = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var desc = ni.Description + " " + ni.Name;
                if (System.Text.RegularExpressions.Regex.IsMatch(desc, "virtual|vmware|vbox|hyper-v|vethernet|wsl|docker|tap-|vpn|bluetooth", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ua.Address)) continue;
                    var s = ua.Address.ToString();
                    if (s.StartsWith("169.254.")) continue; // link-local, not routable
                    result.Add(s);
                }
            }
        }
        catch { }
        // Typical home-LAN ranges first.
        return result.OrderBy(a => a.StartsWith("192.168.") ? 0 : a.StartsWith("10.") ? 1 : a.StartsWith("172.") ? 2 : 3).ToList();
    }

    // ------------------------------------------------------------------ lifecycle
    public async Task StartAsync()
    {
        var b = Microsoft.AspNetCore.Builder.WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.ConfigureKestrel(o => o.ListenAnyIP(Port));
        app = b.Build();
        app.UseWebSockets(new Microsoft.AspNetCore.Builder.WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
        WebEndpoints.Map(app, this, store, layout);
        app.Map("/ws", (Microsoft.AspNetCore.Http.HttpContext ctx) => HandleSocket(ctx));
        await app.StartAsync();
        L($"Listening on port {Port}");

        StartDiscovery();
        if (store.Data.InternetTunnel) Tunnel.Start();
        _ = Task.Run(StatusLoop);
        WriteDevPairing();
    }

    public void Dispose()
    {
        cts.Cancel();
        Tunnel.Dispose();
        try { udp?.Close(); } catch { }
        try { app?.StopAsync(TimeSpan.FromSeconds(1)).Wait(2000); } catch { }
    }

    // ------------------------------------------------------------------ discovery (UDP)
    void StartDiscovery()
    {
        try
        {
            udp = new UdpClient(new IPEndPoint(IPAddress.Any, DiscoveryPort)) { EnableBroadcast = true };
            _ = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        var r = await udp.ReceiveAsync(cts.Token);
                        if (Encoding.UTF8.GetString(r.Buffer).Trim() != "STREAMDECK_DISCOVER") continue;
                        var reply = JsonSerializer.Serialize(new { app = "streamdeck-host", name = PcName, port = Port, v = 1 });
                        var bytes = Encoding.UTF8.GetBytes(reply);
                        await udp.SendAsync(bytes, bytes.Length, r.RemoteEndPoint);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (ObjectDisposedException) { break; }
                    catch { /* ignore malformed */ }
                }
            });
        }
        catch (Exception e) { L("Discovery unavailable: " + e.Message); }
    }

    // ------------------------------------------------------------------ rate limit
    bool IsLocked(string ip) => failures.TryGetValue(ip, out var f) && f.fails >= 5 && f.until > DateTime.UtcNow;

    void Fail(string ip)
    {
        failures.AddOrUpdate(ip, (1, DateTime.UtcNow.AddMinutes(1)),
            (_, o) => (o.until < DateTime.UtcNow ? 1 : o.fails + 1, DateTime.UtcNow.AddMinutes(1)));
    }

    // ------------------------------------------------------------------ socket handling
    async Task HandleSocket(Microsoft.AspNetCore.Http.HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
        var ip = ctx.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "?";
        // Behind the tunnel every connection comes from loopback; use the real client address for rate limiting.
        if (ctx.Connection.RemoteIpAddress is { } ra && IPAddress.IsLoopback(ra) &&
            ctx.Request.Headers["CF-Connecting-IP"].ToString() is { Length: > 0 and < 64 } real)
            ip = real;
        if (IsLocked(ip)) { ctx.Response.StatusCode = 429; return; }

        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        var c = new Client { Ws = ws, Ip = ip };
        clients[c.Id] = c;

        _ = Task.Delay(15000).ContinueWith(_ => { if (c.Device == null) { try { ws.Abort(); } catch { } } });

        try
        {
            var buf = new byte[64 * 1024];
            while (ws.State == WebSocketState.Open && !cts.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buf, cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close) goto done;
                    ms.Write(buf, 0, r.Count);
                    if (ms.Length > 40 * 1024 * 1024) { ws.Abort(); goto done; }
                } while (!r.EndOfMessage);

                JsonNode? msg;
                try { msg = JsonNode.Parse(ms.ToArray()); } catch { continue; }
                if (msg == null) continue;
                await OnMessage(c, msg);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (Exception e) { L("Connection error: " + e.Message); }
        done:
        clients.TryRemove(c.Id, out _);
        if (c.Device != null) L($"Disconnected: {c.Device.Name}");
        Changed?.Invoke();
    }

    async Task Send(Client c, JsonObject o)
    {
        var bytes = Encoding.UTF8.GetBytes(o.ToJsonString());
        await c.SendLock.WaitAsync();
        try
        {
            if (c.Ws.State == WebSocketState.Open)
                await c.Ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch { }
        finally { c.SendLock.Release(); }
    }

    static string S(JsonNode m, string k) => m[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    async Task OnMessage(Client c, JsonNode m)
    {
        var t = S(m, "t");

        if (c.Device == null)
        {
            if (t == "pair") await HandlePair(c, m);
            else if (t == "auth") await HandleAuth(c, m);
            else await Send(c, new JsonObject { ["t"] = "error", ["error"] = "not_authenticated" });
            return;
        }

        var id = m["id"]?.DeepClone();
        switch (t)
        {
            case "ping":
                await Send(c, new JsonObject { ["t"] = "pong" });
                break;

            case "status":
            {
                var st = await BuildStatus(true);
                st["id"] = id;
                await Send(c, st);
                break;
            }

            case "apps":
            {
                bool refresh = m["refresh"] is JsonValue rv && rv.TryGetValue<bool>(out var b) && b;
                _ = Task.Run(async () =>
                {
                    var apps = AppScanner.Scan(refresh);
                    var arr = new JsonArray();
                    foreach (var a in apps)
                        arr.Add(new JsonObject { ["id"] = a.Id, ["name"] = a.Name, ["kind"] = a.Kind, ["icon"] = a.IconPng });
                    await Send(c, new JsonObject { ["t"] = "apps", ["id"] = id, ["apps"] = arr });
                });
                break;
            }

            case "action":
            {
                var action = m["action"]?.DeepClone();
                _ = Task.Run(async () =>
                {
                    var res = new JsonObject { ["t"] = "result", ["id"] = id };
                    try
                    {
                        if (action == null) throw new ArgumentException("Missing action");
                        var type = S(action, "type");
                        L($"{c.Device!.Name}: {type}");
                        var info = await exec.ExecuteAsync(action);
                        res["ok"] = true;
                        if (info != null) res["message"] = info;
                        brightnessAt = DateTime.MinValue; // force refresh
                    }
                    catch (Exception e)
                    {
                        res["ok"] = false;
                        res["error"] = e.Message;
                        L("Action failed: " + e.Message);
                    }
                    await Send(c, res);
                    await BroadcastStatus(false);
                });
                break;
            }

            case "layout_get":
                await Send(c, new JsonObject { ["t"] = "layout", ["id"] = id, ["layout"] = layout.Snapshot() });
                break;

            case "wallpapers":
            {
                _ = Task.Run(async () =>
                {
                    var arr = new JsonArray();
                    foreach (var f in WallpaperLibrary.List())
                        arr.Add(new JsonObject { ["name"] = Path.GetFileName(f), ["thumb"] = WallpaperLibrary.Thumbnail(f) });
                    await Send(c, new JsonObject { ["t"] = "wallpapers", ["id"] = id, ["items"] = arr });
                });
                break;
            }

            case "wallpaper_upload":
            {
                var name = S(m, "name");
                var data = S(m, "data");
                bool apply = m["apply"] is JsonValue av && av.TryGetValue<bool>(out var ab) && ab;
                _ = Task.Run(async () =>
                {
                    var res = new JsonObject { ["t"] = "result", ["id"] = id };
                    try
                    {
                        var file = WallpaperLibrary.Save(name, Convert.FromBase64String(data));
                        if (apply) Native.SetWallpaper(file);
                        res["ok"] = true;
                        res["name"] = Path.GetFileName(file);
                        L($"{c.Device!.Name}: wallpaper uploaded ({Path.GetFileName(file)})");
                    }
                    catch (Exception e) { res["ok"] = false; res["error"] = e.Message; }
                    await Send(c, res);
                });
                break;
            }

            case "wallpaper_delete":
            {
                var ok = WallpaperLibrary.Delete(S(m, "name"));
                await Send(c, new JsonObject { ["t"] = "result", ["id"] = id, ["ok"] = ok });
                break;
            }

            default:
                await Send(c, new JsonObject { ["t"] = "result", ["id"] = id, ["ok"] = false, ["error"] = "unknown message type" });
                break;
        }
    }

    async Task HandlePair(Client c, JsonNode m)
    {
        var secret = S(m, "secret");
        var devId = S(m, "deviceId");
        var name = S(m, "deviceName");
        var valid = DateTime.UtcNow <= pairExpiry && secret.Length > 0 &&
            CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(pairSecret));
        if (!valid || devId.Length < 8 || devId.Length > 80)
        {
            Fail(c.Ip);
            L($"Pairing rejected from {c.Ip}");
            await Send(c, new JsonObject { ["t"] = "pair_fail", ["error"] = "Invalid or expired pairing code" });
            return;
        }
        if (name.Length == 0) name = "Android device";
        if (name.Length > 60) name = name[..60];
        var token = store.AddDevice(devId, name);
        c.Device = store.Data.Devices.First(d => d.Id == devId);
        RotateSecret(); // one-time use
        L($"Paired new device: {name}");
        await Send(c, new JsonObject { ["t"] = "paired", ["token"] = token, ["host"] = PcName });
        await Send(c, await BuildStatus(true));
        Changed?.Invoke();
    }

    async Task HandleAuth(Client c, JsonNode m)
    {
        var dev = store.FindAuthorized(S(m, "deviceId"), S(m, "token"));
        if (dev == null)
        {
            Fail(c.Ip);
            await Send(c, new JsonObject { ["t"] = "auth_fail", ["error"] = "Not paired with this PC" });
            return;
        }
        failures.TryRemove(c.Ip, out _);
        dev.LastSeen = DateTime.Now;
        c.Device = dev;
        L($"Connected: {dev.Name} ({c.Ip})");
        var hostsArr = new JsonArray();
        foreach (var a in AllAddresses()) hostsArr.Add(a);
        await Send(c, new JsonObject { ["t"] = "auth_ok", ["host"] = PcName, ["hosts"] = hostsArr });
        await Send(c, await BuildStatus(true));
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ status sync
    async Task<JsonObject> BuildStatus(bool forceBrightness)
    {
        return await Task.Run(() =>
        {
            int vol = 0; bool muted = false; bool? mic = null;
            try { vol = AudioControl.GetVolume(); muted = AudioControl.GetMuted(); } catch { }
            try { mic = AudioControl.GetMicMuted(); } catch { }
            if (forceBrightness || DateTime.UtcNow - brightnessAt > TimeSpan.FromSeconds(15))
            {
                try { cachedBrightness = BrightnessControl.Get(); } catch { cachedBrightness = -1; }
                brightnessAt = DateTime.UtcNow;
            }
            return new JsonObject
            {
                ["t"] = "status",
                ["cpu"] = SystemStats.Cpu(),
                ["ram"] = SystemStats.Ram(),
                ["uptime"] = SystemStats.UptimeSeconds(),
                ["disks"] = SystemStats.Disks(),
                ["pc"] = PcName,
                ["volume"] = vol,
                ["muted"] = muted,
                ["micMuted"] = mic,
                ["brightness"] = cachedBrightness,
            };
        });
    }

    async Task BroadcastStatus(bool force)
    {
        if (ConnectedCount == 0) return;
        var s = await BuildStatus(false);
        var json = s.ToJsonString();
        if (!force && json == lastStatusJson) return;
        lastStatusJson = json;
        foreach (var c in clients.Values.Where(x => x.Device != null))
            await Send(c, JsonNode.Parse(json)!.AsObject());
    }

    async Task StatusLoop()
    {
        while (!cts.IsCancellationRequested)
        {
            try { await Task.Delay(1000, cts.Token); await BroadcastStatus(false); }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    async Task BroadcastLayout()
    {
        foreach (var c in clients.Values.Where(x => x.Device != null))
            await Send(c, new JsonObject { ["t"] = "layout", ["layout"] = layout.Snapshot() });
        L("Layout updated and sent to phones");
    }

    public void Disconnect(string deviceId)
    {
        foreach (var c in clients.Values.Where(x => x.Device?.Id == deviceId))
            try { c.Ws.Abort(); } catch { }
    }
}
