using System.IO.Pipes;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StreamDeckHost.Core;

/// <summary>Discord app credentials + OAuth tokens (secret and tokens are DPAPI-protected for this Windows user).</summary>
public sealed class DiscordAuth
{
    /// <summary>True once the user pressed Connect. Quick mode = no own application (ClientId empty).</summary>
    public bool Enabled { get; set; }
    public string ClientId { get; set; } = "";
    public string Secret { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public DateTime Expires { get; set; }

    static string FilePath => Path.Combine(AppPaths.Root, "discord-auth.json");

    static string Protect(string s)
    {
        if (s.Length == 0) return "";
        try { return "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(s), null, DataProtectionScope.CurrentUser)); }
        catch { return "plain:" + s; }
    }

    static string Unprotect(string s)
    {
        if (s.StartsWith("plain:")) return s[6..];   // accepted so tests can seed tokens
        if (!s.StartsWith("dpapi:")) return s;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(s[6..]), null, DataProtectionScope.CurrentUser)); }
        catch { return ""; }
    }

    public static DiscordAuth Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            var j = JsonNode.Parse(File.ReadAllText(FilePath))!;
            var cid = j["clientId"]?.GetValue<string>() ?? "";
            return new DiscordAuth
            {
                Enabled = j["enabled"]?.GetValue<bool>() ?? cid.Length > 0,
                ClientId = cid,
                Secret = Unprotect(j["secret"]?.GetValue<string>() ?? ""),
                AccessToken = Unprotect(j["access"]?.GetValue<string>() ?? ""),
                RefreshToken = Unprotect(j["refresh"]?.GetValue<string>() ?? ""),
                Expires = DateTime.TryParse(j["expires"]?.GetValue<string>(), out var d) ? d.ToUniversalTime() : default,
            };
        }
        catch { return new(); }
    }

    public void Save()
    {
        AppPaths.Ensure();
        var j = new JsonObject
        {
            ["enabled"] = Enabled, ["clientId"] = ClientId, ["secret"] = Protect(Secret), ["access"] = Protect(AccessToken),
            ["refresh"] = Protect(RefreshToken), ["expires"] = Expires.ToUniversalTime().ToString("o"),
        };
        File.WriteAllText(FilePath, j.ToJsonString());
    }
}

public sealed class DiscordUser
{
    public string Id = "", Name = "", Avatar = "";
    public bool Mute, Deaf, Speaking;
}

/// <summary>
/// Talks to the local Discord app over its IPC pipe (Discord RPC): knows whether you are in a voice call, who is in it,
/// who is speaking, and can mute / deafen / disconnect. The phone shows this on an auto-opening "Discord call" page.
/// </summary>
public sealed class DiscordRpc : IDisposable
{
    /// <summary>
    /// Discord's own StreamKit overlay application. Using it needs no developer application of your own, but it is an unofficial
    /// route (Discord can change it). The "own application" route is the supported one.
    /// </summary>
    public const string StreamKitId = "207646673902501888";
    static readonly string[] ScopesFull = { "rpc", "rpc.voice.read", "rpc.voice.write" };
    static readonly string[] ScopesReadOnly = { "rpc", "rpc.voice.read" };
    static readonly string[] ChannelEvents = { "VOICE_STATE_CREATE", "VOICE_STATE_UPDATE", "VOICE_STATE_DELETE", "SPEAKING_START", "SPEAKING_STOP" };

    readonly CancellationTokenSource cts = new();
    readonly object gate = new();
    readonly Dictionary<string, DiscordUser> users = new();
    readonly Dictionary<string, TaskCompletionSource<JsonNode>> pending = new();
    readonly SemaphoreSlim writeLock = new(1, 1);
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };

    NamedPipeClientStream? pipe;
    string? currentChannel;
    bool restart;

    public event Action? Changed;
    public string Phase { get; private set; } = "off";      // off | waiting | auth | ok | error
    public string StatusText { get; private set; } = "Not set up";
    public bool InCall { get; private set; }
    public string Channel { get; private set; } = "";
    public bool SelfMute { get; private set; }
    public bool SelfDeaf { get; private set; }
    public bool CanControl { get; private set; } = true;   // false when Discord only granted read access
    public string MyId { get; private set; } = "";
    public string MyName { get; private set; } = "";

    public void Start() => _ = Task.Run(Loop);
    public void Dispose() { cts.Cancel(); try { pipe?.Dispose(); } catch { } }

    /// <summary>Call after the credentials changed: drops the current session so a new one starts.</summary>
    public void Reconfigure() { restart = true; try { pipe?.Dispose(); } catch { } }

    void Set(string phase, string text)
    {
        lock (gate) { Phase = phase; StatusText = text; }
        Changed?.Invoke();
    }

    public JsonObject Snapshot()
    {
        lock (gate)
        {
            var arr = new JsonArray();
            foreach (var u in users.Values)
                arr.Add(new JsonObject { ["id"] = u.Id, ["name"] = u.Name, ["avatar"] = u.Avatar, ["mute"] = u.Mute, ["deaf"] = u.Deaf, ["speaking"] = u.Speaking });
            return new JsonObject
            {
                ["t"] = "discord", ["rpc"] = Phase, ["status"] = StatusText, ["inCall"] = InCall, ["channel"] = Channel,
                ["mute"] = SelfMute, ["deaf"] = SelfDeaf, ["control"] = CanControl, ["me"] = MyName, ["users"] = arr,
            };
        }
    }

    // ------------------------------------------------------------------ session loop
    async Task Loop()
    {
        while (!cts.IsCancellationRequested)
        {
            try { await RunSession(cts.Token); }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                if (!restart) Set("error", "Discord: " + e.Message);
            }
            ClearCall();
            restart = false;
            try { await Task.Delay(4000, cts.Token); } catch { break; }
        }
    }

    void ClearCall()
    {
        lock (gate) { InCall = false; Channel = ""; users.Clear(); currentChannel = null; }
        Changed?.Invoke();
    }

    async Task RunSession(CancellationToken ct)
    {
        var auth = DiscordAuth.Load();
        if (!auth.Enabled) { Set("off", "Not connected. Press Connect on the Discord tab of StreamDeck Host"); await Task.Delay(2500, ct); return; }
        var clientId = string.IsNullOrWhiteSpace(auth.ClientId) ? StreamKitId : auth.ClientId;

        NamedPipeClientStream? p = null;
        var forced = Environment.GetEnvironmentVariable("SDH_DISCORD_PIPE");   // tests only
        var names = !string.IsNullOrEmpty(forced) ? new[] { forced } : Enumerable.Range(0, 10).Select(i => $"discord-ipc-{i}").ToArray();
        foreach (var n in names)
        {
            var c = new NamedPipeClientStream(".", n, PipeDirection.InOut, PipeOptions.Asynchronous);
            try { await c.ConnectAsync(400, ct); p = c; break; } catch { c.Dispose(); }
        }
        if (p == null) { Set("waiting", "Discord is not running"); return; }

        using (p)
        {
            pipe = p;
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reader = Task.Run(() => ReadLoop(p, ready, ct));
            await WriteFrame(p, 0, new JsonObject { ["v"] = 1, ["client_id"] = clientId }.ToJsonString(), ct);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

            await Authenticate(auth, clientId, ct);
            Set("ok", "Connected to Discord" + (MyName.Length > 0 ? " as " + MyName : "") + (CanControl ? "" : " (view only: Discord did not allow mute/deafen control)"));

            await Cmd("SUBSCRIBE", null, "VOICE_CHANNEL_SELECT");
            await Cmd("SUBSCRIBE", null, "VOICE_SETTINGS_UPDATE");
            ApplyVoiceSettings((await Cmd("GET_VOICE_SETTINGS"))["data"]);
            await ApplyChannel((await Cmd("GET_SELECTED_VOICE_CHANNEL"))["data"]);
            await reader;   // until the pipe closes (Discord quit) or we are disposed
        }
        pipe = null;
        if (!restart) Set("waiting", "Discord closed. Waiting for it to start");
    }

    // ------------------------------------------------------------------ framing
    static async Task WriteFrame(Stream s, int op, string json, CancellationToken ct)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var buf = new byte[8 + payload.Length];
        BitConverter.TryWriteBytes(buf.AsSpan(0, 4), op);
        BitConverter.TryWriteBytes(buf.AsSpan(4, 4), payload.Length);
        payload.CopyTo(buf, 8);
        await s.WriteAsync(buf, ct);
        await s.FlushAsync(ct);
    }

    static async Task<bool> ReadExact(Stream s, byte[] buf, CancellationToken ct)
    {
        int off = 0;
        while (off < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(off), ct);
            if (n == 0) return false;
            off += n;
        }
        return true;
    }

    async Task ReadLoop(NamedPipeClientStream p, TaskCompletionSource<bool> ready, CancellationToken ct)
    {
        try
        {
            var head = new byte[8];
            while (!ct.IsCancellationRequested)
            {
                if (!await ReadExact(p, head, ct)) break;
                int op = BitConverter.ToInt32(head, 0), len = BitConverter.ToInt32(head, 4);
                if (len < 0 || len > 16 * 1024 * 1024) break;
                var body = new byte[len];
                if (!await ReadExact(p, body, ct)) break;
                var json = Encoding.UTF8.GetString(body);
                if (op == 2) break;                                              // CLOSE
                if (op == 3) { await WriteFrame(p, 4, json, ct); continue; }     // PING -> PONG
                if (op != 1) continue;
                JsonNode? m; try { m = JsonNode.Parse(json); } catch { continue; }
                if (m == null) continue;
                var evt = m["evt"]?.GetValue<string>();
                var nonce = m["nonce"]?.GetValue<string>();
                if (evt == "READY") { ready.TrySetResult(true); continue; }
                if (nonce != null)
                {
                    TaskCompletionSource<JsonNode>? t;
                    lock (pending) { pending.Remove(nonce, out t); }
                    if (t != null)
                    {
                        if (evt == "ERROR") t.TrySetException(new InvalidOperationException(m["data"]?["message"]?.GetValue<string>() ?? "Discord error"));
                        else t.TrySetResult(m);
                    }
                    continue;
                }
                if (m["cmd"]?.GetValue<string>() == "DISPATCH" && evt != null)
                {
                    var data = m["data"]?.DeepClone();
                    _ = Task.Run(() => { try { return HandleEvent(evt, data); } catch { return Task.CompletedTask; } });
                }
            }
        }
        catch { }
        finally
        {
            ready.TrySetCanceled();
            lock (pending) { foreach (var t in pending.Values) t.TrySetException(new IOException("Discord closed the connection")); pending.Clear(); }
        }
    }

    public async Task<JsonNode> Cmd(string cmd, JsonObject? args = null, string? evt = null, int timeoutMs = 10000)
    {
        var p = pipe ?? throw new InvalidOperationException("Discord is not connected (set it up in StreamDeck Host > Discord)");
        var nonce = Guid.NewGuid().ToString();
        var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (pending) pending[nonce] = tcs;
        var o = new JsonObject { ["cmd"] = cmd, ["nonce"] = nonce };
        if (args != null) o["args"] = args;
        if (evt != null) o["evt"] = evt;
        await writeLock.WaitAsync();
        try { await WriteFrame(p, 1, o.ToJsonString(), CancellationToken.None); }
        finally { writeLock.Release(); }
        return await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
    }

    // ------------------------------------------------------------------ OAuth
    async Task Authenticate(DiscordAuth a, string clientId, CancellationToken ct)
    {
        bool quick = clientId == StreamKitId;
        if (a.AccessToken.Length > 0 && a.Expires > DateTime.UtcNow.AddMinutes(1))
        {
            try { SetMe(await Cmd("AUTHENTICATE", new JsonObject { ["access_token"] = a.AccessToken })); return; } catch { }
        }
        if (!quick && a.RefreshToken.Length > 0 && a.Secret.Length > 0)
        {
            try
            {
                await Exchange(a, new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = a.RefreshToken });
                SetMe(await Cmd("AUTHENTICATE", new JsonObject { ["access_token"] = a.AccessToken })); return;
            }
            catch { }
        }
        if (!quick && a.Secret.Length == 0) throw new InvalidOperationException("Client Secret is missing");

        Set("auth", "Approve the request in the Discord app (a popup appears there)");
        JsonNode res;
        try { res = await Cmd("AUTHORIZE", new JsonObject { ["client_id"] = clientId, ["scopes"] = ToArray(ScopesFull) }, null, 180000); CanControl = true; }
        catch (InvalidOperationException e) when (e.Message.Contains("scope", StringComparison.OrdinalIgnoreCase))
        {
            // Discord only grants some scopes to this application: continue read-only (see who talks, no mute/deafen keys).
            res = await Cmd("AUTHORIZE", new JsonObject { ["client_id"] = clientId, ["scopes"] = ToArray(ScopesReadOnly) }, null, 180000); CanControl = false;
        }
        var code = res["data"]?["code"]?.GetValue<string>() ?? throw new InvalidOperationException("Discord did not return a code");
        if (quick) await ExchangeQuick(a, code);
        else await Exchange(a, new Dictionary<string, string> { ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = "http://localhost" });
        SetMe(await Cmd("AUTHENTICATE", new JsonObject { ["access_token"] = a.AccessToken }));
    }

    static JsonArray ToArray(string[] items) { var arr = new JsonArray(); foreach (var s in items) arr.Add(s); return arr; }

    static async Task Exchange(DiscordAuth a, Dictionary<string, string> form)
    {
        form["client_id"] = a.ClientId; form["client_secret"] = a.Secret;
        using var resp = await http.PostAsync("https://discord.com/api/oauth2/token", new FormUrlEncodedContent(form));
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException("Discord login failed: " + text);
        var j = JsonNode.Parse(text)!;
        a.AccessToken = j["access_token"]!.GetValue<string>();
        a.RefreshToken = j["refresh_token"]?.GetValue<string>() ?? a.RefreshToken;
        a.Expires = DateTime.UtcNow.AddSeconds(j["expires_in"]?.GetValue<int>() ?? 604800);
        a.Save();
    }

    /// <summary>Token exchange for the StreamKit application (done by Discord's own StreamKit service).</summary>
    static async Task ExchangeQuick(DiscordAuth a, string code)
    {
        var url = Environment.GetEnvironmentVariable("SDH_DISCORD_TOKEN_URL") ?? "https://streamkit.discord.com/overlay/token";
        using var resp = await http.PostAsync(url, new StringContent(new JsonObject { ["code"] = code }.ToJsonString(), Encoding.UTF8, "application/json"));
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException("Discord login failed: " + text);
        a.AccessToken = JsonNode.Parse(text)!["access_token"]!.GetValue<string>();
        a.RefreshToken = "";
        a.Expires = DateTime.UtcNow.AddDays(6);     // when it expires the next start asks you to approve again
        a.Enabled = true; a.Save();
    }
    void SetMe(JsonNode r)
    {
        var u = r["data"]?["user"];
        lock (gate) { MyId = u?["id"]?.GetValue<string>() ?? ""; MyName = u?["global_name"]?.GetValue<string>() ?? u?["username"]?.GetValue<string>() ?? ""; }
    }

    // ------------------------------------------------------------------ state
    void ApplyVoiceSettings(JsonNode? d)
    {
        if (d == null) return;
        lock (gate)
        {
            if (d["mute"] is JsonValue m && m.TryGetValue<bool>(out var mv)) SelfMute = mv;
            if (d["deaf"] is JsonValue f && f.TryGetValue<bool>(out var fv)) SelfDeaf = fv;
        }
        Changed?.Invoke();
    }

    static DiscordUser ParseUser(JsonNode vs)
    {
        var u = vs["user"]; var st = vs["voice_state"];
        bool B(JsonNode? n, string k) => n?[k] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        return new DiscordUser
        {
            Id = u?["id"]?.GetValue<string>() ?? "",
            Name = vs["nick"]?.GetValue<string>() ?? u?["global_name"]?.GetValue<string>() ?? u?["username"]?.GetValue<string>() ?? "?",
            Avatar = u?["avatar"]?.GetValue<string>() ?? "",
            Mute = B(st, "mute") || B(st, "self_mute") || B(vs, "mute"),
            Deaf = B(st, "deaf") || B(st, "self_deaf"),
        };
    }

    async Task ApplyChannel(JsonNode? d)
    {
        string? old;
        lock (gate) old = currentChannel;
        if (old != null)
            foreach (var e in ChannelEvents) { try { await Cmd("UNSUBSCRIBE", new JsonObject { ["channel_id"] = old }, e, 3000); } catch { } }

        if (d == null || d is not JsonObject || d["id"] == null)
        {
            ClearCall();
            return;
        }
        var id = d["id"]!.GetValue<string>();
        lock (gate)
        {
            currentChannel = id; InCall = true; Channel = d["name"]?.GetValue<string>() ?? "Voice";
            users.Clear();
            if (d["voice_states"] is JsonArray arr)
                foreach (var vs in arr) { if (vs == null) continue; var u = ParseUser(vs); users[u.Id] = u; }
        }
        foreach (var e in ChannelEvents) { try { await Cmd("SUBSCRIBE", new JsonObject { ["channel_id"] = id }, e); } catch { } }
        Changed?.Invoke();
    }

    async Task HandleEvent(string evt, JsonNode? data)
    {
        switch (evt)
        {
            case "VOICE_CHANNEL_SELECT":
                if (data?["channel_id"] == null) await ApplyChannel(null);
                else await ApplyChannel((await Cmd("GET_SELECTED_VOICE_CHANNEL"))["data"]);
                break;
            case "VOICE_SETTINGS_UPDATE":
                ApplyVoiceSettings(data);
                break;
            case "VOICE_STATE_CREATE":
            case "VOICE_STATE_UPDATE":
                if (data != null) { var u = ParseUser(data); lock (gate) { if (users.TryGetValue(u.Id, out var o)) u.Speaking = o.Speaking; users[u.Id] = u; } Changed?.Invoke(); }
                break;
            case "VOICE_STATE_DELETE":
                { var id = data?["user"]?["id"]?.GetValue<string>(); if (id != null) { lock (gate) users.Remove(id); Changed?.Invoke(); } }
                break;
            case "SPEAKING_START":
            case "SPEAKING_STOP":
                { var id = data?["user_id"]?.GetValue<string>(); bool on = evt == "SPEAKING_START";
                  if (id != null) { lock (gate) { if (users.TryGetValue(id, out var u)) u.Speaking = on; } Changed?.Invoke(); } }
                break;
        }
    }

    // ------------------------------------------------------------------ browsing servers and joining voice channels
    public sealed record GuildInfo(string Id, string Name, string Icon);
    public sealed record ChannelInfo(string Id, string Name);

    List<GuildInfo>? guildCache; DateTime guildCacheAt;

    public async Task<List<GuildInfo>> GetGuildsAsync()
    {
        if (guildCache != null && DateTime.UtcNow - guildCacheAt < TimeSpan.FromSeconds(60)) return guildCache;
        var r = await Cmd("GET_GUILDS");
        var list = new List<GuildInfo>();
        if (r["data"]?["guilds"] is JsonArray arr)
            foreach (var g in arr)
            {
                var id = g?["id"]?.GetValue<string>(); if (id == null) continue;
                var icon = g!["icon_url"]?.GetValue<string>() ?? "";
                if (icon.Length == 0 && g["icon"]?.GetValue<string>() is { Length: > 0 } hash) icon = $"https://cdn.discordapp.com/icons/{id}/{hash}.png?size=128";
                list.Add(new GuildInfo(id, g["name"]?.GetValue<string>() ?? id, icon));
            }
        guildCache = list; guildCacheAt = DateTime.UtcNow;
        return list;
    }

    /// <summary>Voice channels (type 2) of a server, in Discord's order.</summary>
    public async Task<List<ChannelInfo>> GetVoiceChannelsAsync(string guildId)
    {
        var r = await Cmd("GET_CHANNELS", new JsonObject { ["guild_id"] = guildId });
        var list = new List<ChannelInfo>();
        if (r["data"]?["channels"] is JsonArray arr)
            foreach (var c in arr)
            {
                int type = c?["type"] is JsonValue v && v.TryGetValue<int>(out var t) ? t : -1;
                var id = c?["id"]?.GetValue<string>();
                if (type == 2 && id != null) list.Add(new ChannelInfo(id, c!["name"]?.GetValue<string>() ?? id));
            }
        return list;
    }

    public async Task JoinAsync(string channelId)
    {
        if (string.IsNullOrWhiteSpace(channelId)) throw new ArgumentException("Missing channel");
        await Cmd("SELECT_VOICE_CHANNEL", new JsonObject { ["channel_id"] = channelId, ["force"] = true });
    }

    // ------------------------------------------------------------------ controls (used by phone keys)
    public async Task ToggleMuteAsync()
    {
        bool now; lock (gate) now = !SelfMute;
        await Cmd("SET_VOICE_SETTINGS", new JsonObject { ["mute"] = now });
    }

    public async Task ToggleDeafenAsync()
    {
        bool now; lock (gate) now = !SelfDeaf;
        await Cmd("SET_VOICE_SETTINGS", new JsonObject { ["deaf"] = now });
    }

    public async Task DisconnectAsync()
    {
        if (!InCall) throw new InvalidOperationException("You are not in a Discord call");
        await Cmd("SELECT_VOICE_CHANNEL", new JsonObject { ["channel_id"] = null, ["force"] = true });
    }
}
