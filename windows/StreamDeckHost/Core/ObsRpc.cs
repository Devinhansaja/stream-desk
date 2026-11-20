using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace StreamDeckHost.Core;

/// <summary>Where to find OBS's WebSocket server (Tools > WebSocket Server Settings). The password is DPAPI-protected.</summary>
public sealed class ObsAuth
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 4455;
    public string Password { get; set; } = "";

    static string FilePath => Path.Combine(AppPaths.Root, "obs-auth.json");

    static string Protect(string s)
    {
        if (s.Length == 0) return "";
        try { return "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(s), null, DataProtectionScope.CurrentUser)); }
        catch { return "plain:" + s; }
    }

    static string Unprotect(string s)
    {
        if (s.StartsWith("plain:")) return s[6..];   // accepted so tests can seed a password
        if (!s.StartsWith("dpapi:")) return s;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(s[6..]), null, DataProtectionScope.CurrentUser)); }
        catch { return ""; }
    }

    public static ObsAuth Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            var j = JsonNode.Parse(File.ReadAllText(FilePath))!;
            return new ObsAuth
            {
                Enabled = j["enabled"]?.GetValue<bool>() ?? false,
                Host = j["host"]?.GetValue<string>() is { Length: > 0 } h ? h : "127.0.0.1",
                Port = j["port"]?.GetValue<int>() ?? 4455,
                Password = Unprotect(j["password"]?.GetValue<string>() ?? ""),
            };
        }
        catch { return new(); }
    }

    public void Save()
    {
        AppPaths.Ensure();
        File.WriteAllText(FilePath, new JsonObject { ["enabled"] = Enabled, ["host"] = Host, ["port"] = Port, ["password"] = Protect(Password) }.ToJsonString());
    }
}

public sealed class ObsInput { public string Name = ""; public bool Mute; public double Vol; }
public sealed class ObsItem { public int Id; public string Name = ""; public bool Enabled, Locked, Group; public string Kind = ""; }

/// <summary>
/// OBS Studio remote control through obs-websocket v5 (built into OBS 28+): scenes, stream/record/virtual camera
/// and the audio mixer. The phone shows it on an "OBS control center" page.
/// </summary>
public sealed class ObsRpc : IDisposable
{
    const int Subscriptions = 1 | 4 | 8 | 64 | 128;   // General, Scenes, Inputs, Outputs, SceneItems (no high-volume meters)

    readonly CancellationTokenSource cts = new();
    readonly object gate = new();
    readonly Dictionary<string, TaskCompletionSource<JsonNode>> pending = new();
    readonly SemaphoreSlim writeLock = new(1, 1);
    readonly List<string> scenes = new();
    readonly List<ObsInput> inputs = new();
    readonly List<ObsItem> items = new();
    readonly List<string> transitions = new();

    ClientWebSocket? ws;
    bool restart;

    public event Action? Changed;
    public string Phase { get; private set; } = "off";      // off | waiting | ok | error
    public string StatusText { get; private set; } = "Not connected";
    public string Scene { get; private set; } = "";
    public bool Streaming { get; private set; }
    public bool Recording { get; private set; }
    public bool RecPaused { get; private set; }
    public bool VirtualCam { get; private set; }
    public bool Studio { get; private set; }
    public string Preview { get; private set; } = "";
    public string Transition { get; private set; } = "";
    public int TransitionMs { get; private set; }
    public bool Replay { get; private set; }
    public string ItemsScene { get; private set; } = "";
    public string StreamTime { get; private set; } = "";
    public string RecTime { get; private set; } = "";

    public void Start() => _ = Task.Run(Loop);
    public void Dispose() { cts.Cancel(); try { ws?.Dispose(); } catch { } }
    public void Reconfigure() { restart = true; try { ws?.Abort(); } catch { } }

    void Set(string phase, string text) { lock (gate) { Phase = phase; StatusText = text; } Changed?.Invoke(); }

    public JsonObject Snapshot()
    {
        lock (gate)
        {
            var sc = new JsonArray(); foreach (var s in scenes) sc.Add(s);
            var inp = new JsonArray(); foreach (var i in inputs) inp.Add(new JsonObject { ["name"] = i.Name, ["mute"] = i.Mute, ["vol"] = Math.Round(i.Vol, 3) });
            var tr = new JsonArray(); foreach (var t in transitions) tr.Add(t);
            var it = new JsonArray(); foreach (var i in items) it.Add(new JsonObject { ["id"] = i.Id, ["name"] = i.Name, ["enabled"] = i.Enabled, ["locked"] = i.Locked, ["group"] = i.Group, ["kind"] = i.Kind });
            return new JsonObject
            {
                ["studio"] = Studio, ["preview"] = Preview, ["transitions"] = tr, ["transition"] = Transition, ["transitionMs"] = TransitionMs,
                ["replay"] = Replay, ["itemsScene"] = ItemsScene, ["items"] = it,
                ["t"] = "obs", ["rpc"] = Phase, ["status"] = StatusText, ["scene"] = Scene, ["scenes"] = sc,
                ["streaming"] = Streaming, ["recording"] = Recording, ["paused"] = RecPaused, ["vcam"] = VirtualCam,
                ["streamTime"] = StreamTime, ["recTime"] = RecTime, ["inputs"] = inp,
            };
        }
    }

    // ------------------------------------------------------------------ session
    async Task Loop()
    {
        while (!cts.IsCancellationRequested)
        {
            try { await RunSession(cts.Token); }
            catch (OperationCanceledException) { break; }
            catch (Exception e) { if (!restart) Set("error", "OBS: " + e.Message); }
            Clear();
            restart = false;
            try { await Task.Delay(4000, cts.Token); } catch { break; }
        }
    }

    void Clear()
    {
        lock (gate) { scenes.Clear(); inputs.Clear(); items.Clear(); transitions.Clear(); Studio = Replay = false; Preview = Transition = ItemsScene = ""; Scene = ""; Streaming = Recording = RecPaused = VirtualCam = false; StreamTime = RecTime = ""; }
        Changed?.Invoke();
    }

    async Task RunSession(CancellationToken ct)
    {
        var auth = ObsAuth.Load();
        if (!auth.Enabled) { Set("off", "Not connected. Press Connect on the OBS tab of StreamDeck Host"); await Task.Delay(2500, ct); return; }

        using var c = new ClientWebSocket();
        c.Options.AddSubProtocol("obswebsocket.json");
        using (var to = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            to.CancelAfter(3000);
            try { await c.ConnectAsync(new Uri($"ws://{auth.Host}:{auth.Port}"), to.Token); }
            catch when (!ct.IsCancellationRequested) { Set("waiting", "OBS is not reachable (is OBS running with the WebSocket server on, port " + auth.Port + "?)"); return; }
        }
        ws = c;
        var hello = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        var identified = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = Task.Run(() => ReadLoop(c, hello, identified, ct));

        var h = await hello.Task.WaitAsync(TimeSpan.FromSeconds(8), ct);
        var id = new JsonObject { ["rpcVersion"] = 1, ["eventSubscriptions"] = Subscriptions };
        if (h["authentication"] is JsonObject a)
        {
            if (auth.Password.Length == 0) throw new InvalidOperationException("OBS needs a password. Enter it on the OBS tab");
            var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(auth.Password + a["salt"]!.GetValue<string>())));
            id["authentication"] = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + a["challenge"]!.GetValue<string>())));
        }
        await Send(c, new JsonObject { ["op"] = 1, ["d"] = id });
        try { await identified.Task.WaitAsync(TimeSpan.FromSeconds(8), ct); }
        catch (Exception) when (!ct.IsCancellationRequested) { throw new InvalidOperationException("OBS rejected the login (wrong password?)"); }

        await RefreshAll();
        Set("ok", "Connected to OBS");
        var pollTask = Task.Run(() => PollTimes(ct));
        await reader;
        await pollTask.WaitAsync(TimeSpan.FromSeconds(3)).ContinueWith(_ => { });
        ws = null;
        if (!restart) Set("waiting", "OBS closed. Waiting for it to start");
    }

    async Task Send(ClientWebSocket c, JsonObject o)
    {
        var bytes = Encoding.UTF8.GetBytes(o.ToJsonString());
        await writeLock.WaitAsync();
        try { await c.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None); }
        finally { writeLock.Release(); }
    }

    async Task ReadLoop(ClientWebSocket c, TaskCompletionSource<JsonNode> hello, TaskCompletionSource<bool> identified, CancellationToken ct)
    {
        var buf = new byte[64 * 1024];
        try
        {
            while (c.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await c.ReceiveAsync(buf, ct);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buf, 0, r.Count);
                } while (!r.EndOfMessage);
                JsonNode? m; try { m = JsonNode.Parse(ms.ToArray()); } catch { continue; }
                var op = m?["op"]?.GetValue<int>() ?? -1; var d = m?["d"];
                switch (op)
                {
                    case 0: hello.TrySetResult(d ?? new JsonObject()); break;
                    case 2: identified.TrySetResult(true); break;
                    case 5: { var ev = d?["eventType"]?.GetValue<string>(); var data = d?["eventData"]?.DeepClone(); if (ev != null) _ = Task.Run(() => { try { return HandleEvent(ev, data); } catch { return Task.CompletedTask; } }); break; }
                    case 7:
                    {
                        var rid = d?["requestId"]?.GetValue<string>(); if (rid == null) break;
                        TaskCompletionSource<JsonNode>? t; lock (pending) { pending.Remove(rid, out t); }
                        if (t == null) break;
                        if (d!["requestStatus"]?["result"]?.GetValue<bool>() == true) t.TrySetResult(d["responseData"]?.DeepClone() ?? new JsonObject());
                        else t.TrySetException(new InvalidOperationException(d["requestStatus"]?["comment"]?.GetValue<string>() ?? "OBS request failed"));
                        break;
                    }
                }
            }
        }
        catch { }
        finally
        {
            hello.TrySetCanceled(); identified.TrySetCanceled();
            lock (pending) { foreach (var t in pending.Values) t.TrySetException(new IOException("OBS closed the connection")); pending.Clear(); }
        }
    }

    public async Task<JsonNode> Cmd(string type, JsonObject? data = null, int timeoutMs = 8000)
    {
        var c = ws ?? throw new InvalidOperationException("OBS is not connected (set it up in StreamDeck Host > OBS)");
        var rid = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (pending) pending[rid] = tcs;
        var d = new JsonObject { ["requestType"] = type, ["requestId"] = rid };
        if (data != null) d["requestData"] = data;
        await Send(c, new JsonObject { ["op"] = 6, ["d"] = d });
        return await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
    }

    // ------------------------------------------------------------------ state
    async Task RefreshAll()
    {
        await RefreshScenes();
        var ss = await Cmd("GetStreamStatus"); var rs = await Cmd("GetRecordStatus");
        bool vc = false; try { vc = (await Cmd("GetVirtualCamStatus"))["outputActive"]?.GetValue<bool>() ?? false; } catch { }
        lock (gate)
        {
            Streaming = ss["outputActive"]?.GetValue<bool>() ?? false; StreamTime = Trim(ss["outputTimecode"]?.GetValue<string>());
            Recording = rs["outputActive"]?.GetValue<bool>() ?? false; RecPaused = rs["outputPaused"]?.GetValue<bool>() ?? false; RecTime = Trim(rs["outputTimecode"]?.GetValue<string>());
            VirtualCam = vc;
        }
        await RefreshInputs();
        await RefreshStudio();
        await RefreshTransitions();
        try { var rb = await Cmd("GetReplayBufferStatus"); lock (gate) Replay = rb["outputActive"]?.GetValue<bool>() ?? false; } catch { lock (gate) Replay = false; }
        await RefreshItems();
    }

    /// <summary>The scene whose sources are shown: the preview scene in Studio Mode, otherwise the live scene.</summary>
    string ViewScene() { lock (gate) return Studio && Preview.Length > 0 ? Preview : Scene; }

    async Task RefreshStudio()
    {
        try
        {
            bool on = (await Cmd("GetStudioModeEnabled"))["studioModeEnabled"]?.GetValue<bool>() ?? false;
            string prev = "";
            if (on) { try { prev = (await Cmd("GetCurrentPreviewScene"))["currentPreviewSceneName"]?.GetValue<string>() ?? ""; } catch { } }
            lock (gate) { Studio = on; Preview = prev; }
        }
        catch { lock (gate) { Studio = false; Preview = ""; } }
        Changed?.Invoke();
    }

    async Task RefreshTransitions()
    {
        try
        {
            var r = await Cmd("GetSceneTransitionList");
            int ms = 0;
            try { ms = (await Cmd("GetCurrentSceneTransition"))["transitionDuration"]?.GetValue<int>() ?? 0; } catch { }
            lock (gate)
            {
                transitions.Clear();
                if (r["transitions"] is JsonArray arr) foreach (var t in arr) if (t?["transitionName"]?.GetValue<string>() is { } n) transitions.Add(n);
                Transition = r["currentSceneTransitionName"]?.GetValue<string>() ?? ""; TransitionMs = ms;
            }
        }
        catch { }
        Changed?.Invoke();
    }

    async Task RefreshItems()
    {
        var scene = ViewScene();
        if (scene.Length == 0) { lock (gate) { items.Clear(); ItemsScene = ""; } Changed?.Invoke(); return; }
        var found = new List<ObsItem>();
        try
        {
            var r = await Cmd("GetSceneItemList", new JsonObject { ["sceneName"] = scene });
            if (r["sceneItems"] is JsonArray arr)
                foreach (var i in arr.Reverse())   // OBS lists bottom-up; show the top layer first
                    found.Add(new ObsItem
                    {
                        Id = i?["sceneItemId"]?.GetValue<int>() ?? 0, Name = i?["sourceName"]?.GetValue<string>() ?? "?",
                        Enabled = i?["sceneItemEnabled"]?.GetValue<bool>() ?? true, Locked = i?["sceneItemLocked"]?.GetValue<bool>() ?? false,
                        Group = i?["isGroup"]?.GetValue<bool>() ?? false, Kind = i?["inputKind"]?.GetValue<string>() ?? i?["sourceType"]?.GetValue<string>() ?? "",
                    });
        }
        catch { }
        lock (gate) { items.Clear(); items.AddRange(found); ItemsScene = scene; }
        Changed?.Invoke();
    }

    static string Trim(string? tc) => string.IsNullOrEmpty(tc) ? "" : (tc.Contains('.') ? tc[..tc.IndexOf('.')] : tc);

    async Task RefreshScenes()
    {
        var r = await Cmd("GetSceneList");
        lock (gate)
        {
            Scene = r["currentProgramSceneName"]?.GetValue<string>() ?? "";
            scenes.Clear();
            if (r["scenes"] is JsonArray arr)
                foreach (var s in arr.Reverse())   // OBS lists scenes bottom-up
                    if (s?["sceneName"]?.GetValue<string>() is { } n) scenes.Add(n);
        }
        Changed?.Invoke();
    }

    async Task RefreshInputs()
    {
        var r = await Cmd("GetInputList");
        var found = new List<ObsInput>();
        if (r["inputs"] is JsonArray arr)
            foreach (var i in arr)
            {
                var name = i?["inputName"]?.GetValue<string>(); if (name == null) continue;
                try   // only inputs with audio answer these
                {
                    var m = await Cmd("GetInputMute", new JsonObject { ["inputName"] = name });
                    var v = await Cmd("GetInputVolume", new JsonObject { ["inputName"] = name });
                    found.Add(new ObsInput { Name = name, Mute = m["inputMuted"]?.GetValue<bool>() ?? false, Vol = MulToPos(v["inputVolumeMul"]?.GetValue<double>() ?? 1) });
                }
                catch { }
            }
        lock (gate) { inputs.Clear(); inputs.AddRange(found); }
        Changed?.Invoke();
    }

    // OBS stores volume as a multiplier (1.0 = 0 dB). A cube-root fader feels natural and maps 100% to 0 dB.
    static double MulToPos(double mul) => Math.Clamp(Math.Cbrt(Math.Max(0, mul)), 0, 1);
    static double PosToMul(double pos) => Math.Pow(Math.Clamp(pos, 0, 1), 3);

    async Task HandleEvent(string ev, JsonNode? d)
    {
        switch (ev)
        {
            case "CurrentProgramSceneChanged":
                lock (gate) Scene = d?["sceneName"]?.GetValue<string>() ?? Scene;
                Changed?.Invoke();
                if (!Studio) await RefreshItems();
                break;
            case "StudioModeStateChanged": await RefreshStudio(); await RefreshItems(); break;
            case "CurrentPreviewSceneChanged": lock (gate) Preview = d?["sceneName"]?.GetValue<string>() ?? Preview; Changed?.Invoke(); await RefreshItems(); break;
            case "CurrentSceneTransitionChanged": case "CurrentSceneTransitionDurationChanged": case "SceneTransitionListChanged": await RefreshTransitions(); break;
            case "SceneItemEnableStateChanged":
            case "SceneItemLockStateChanged":
                {
                    var sn = d?["sceneName"]?.GetValue<string>(); var iid = d?["sceneItemId"]?.GetValue<int>() ?? -1;
                    lock (gate)
                    {
                        if (sn == ItemsScene) { var it = items.FirstOrDefault(x => x.Id == iid); if (it != null) { if (ev == "SceneItemEnableStateChanged") it.Enabled = d?["sceneItemEnabled"]?.GetValue<bool>() ?? it.Enabled; else it.Locked = d?["sceneItemLocked"]?.GetValue<bool>() ?? it.Locked; } }
                    }
                    Changed?.Invoke(); break;
                }
            case "SceneItemCreated": case "SceneItemRemoved": case "SceneItemListReindexed":
                if (d?["sceneName"]?.GetValue<string>() == ItemsScene) await RefreshItems();
                break;
            case "ReplayBufferStateChanged": lock (gate) Replay = d?["outputActive"]?.GetValue<bool>() ?? false; Changed?.Invoke(); break;
            case "SceneListChanged": case "SceneCreated": case "SceneRemoved": case "SceneNameChanged": await RefreshScenes(); break;
            case "StreamStateChanged":
                lock (gate) { Streaming = d?["outputActive"]?.GetValue<bool>() ?? false; if (!Streaming) StreamTime = ""; }
                Changed?.Invoke();
                if (Streaming) { try { var x = await Cmd("GetStreamStatus"); lock (gate) StreamTime = Trim(x["outputTimecode"]?.GetValue<string>()); Changed?.Invoke(); } catch { } }
                break;
            case "RecordStateChanged":
                lock (gate) { Recording = d?["outputActive"]?.GetValue<bool>() ?? false; RecPaused = d?["outputState"]?.GetValue<string>() == "OBS_WEBSOCKET_OUTPUT_PAUSED"; if (!Recording) RecTime = ""; }
                Changed?.Invoke();
                if (Recording) { try { var x = await Cmd("GetRecordStatus"); lock (gate) RecTime = Trim(x["outputTimecode"]?.GetValue<string>()); Changed?.Invoke(); } catch { } }
                break;
            case "VirtualcamStateChanged": lock (gate) VirtualCam = d?["outputActive"]?.GetValue<bool>() ?? false; Changed?.Invoke(); break;
            case "InputMuteStateChanged":
                { var n = d?["inputName"]?.GetValue<string>(); lock (gate) { var i = inputs.FirstOrDefault(x => x.Name == n); if (i != null) i.Mute = d?["inputMuted"]?.GetValue<bool>() ?? i.Mute; } Changed?.Invoke(); break; }
            case "InputVolumeChanged":
                { var n = d?["inputName"]?.GetValue<string>(); lock (gate) { var i = inputs.FirstOrDefault(x => x.Name == n); if (i != null) i.Vol = MulToPos(d?["inputVolumeMul"]?.GetValue<double>() ?? 1); } Changed?.Invoke(); break; }
            case "InputCreated": case "InputRemoved": case "InputNameChanged": await RefreshInputs(); break;
            case "ExitStarted": try { ws?.Abort(); } catch { } break;
        }
    }

    async Task PollTimes(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && ws is { State: WebSocketState.Open })
        {
            try
            {
                await Task.Delay(1000, ct);
                bool s, r; lock (gate) { s = Streaming; r = Recording; }
                if (s) { var x = await Cmd("GetStreamStatus"); lock (gate) StreamTime = Trim(x["outputTimecode"]?.GetValue<string>()); }
                if (r) { var x = await Cmd("GetRecordStatus"); lock (gate) RecTime = Trim(x["outputTimecode"]?.GetValue<string>()); }
                if (s || r) Changed?.Invoke();
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    // ------------------------------------------------------------------ controls (phone keys and the control center page)
    public Task SetSceneAsync(string scene) => string.IsNullOrWhiteSpace(scene) ? throw new ArgumentException("Missing scene name") : Cmd("SetCurrentProgramScene", new JsonObject { ["sceneName"] = scene });
    public Task ToggleStreamAsync() => Cmd("ToggleStream");
    public Task ToggleRecordAsync() => Cmd("ToggleRecord");
    public Task ToggleRecordPauseAsync() => Cmd("ToggleRecordPause");
    public Task ToggleVirtualCamAsync() => Cmd("ToggleVirtualCam");
    public Task SetItemEnabledAsync(string scene, int id, bool on) => Cmd("SetSceneItemEnabled", new JsonObject { ["sceneName"] = scene, ["sceneItemId"] = id, ["sceneItemEnabled"] = on });
    public Task SetItemLockedAsync(string scene, int id, bool on) => Cmd("SetSceneItemLocked", new JsonObject { ["sceneName"] = scene, ["sceneItemId"] = id, ["sceneItemLocked"] = on });

    /// <summary>Show/hide a source by name (for keys). Scene defaults to the one on screen.</summary>
    public async Task ToggleSourceAsync(string? scene, string source)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("Missing source name");
        var sc = string.IsNullOrWhiteSpace(scene) ? ViewScene() : scene;
        var id = (await Cmd("GetSceneItemId", new JsonObject { ["sceneName"] = sc, ["sourceName"] = source }))["sceneItemId"]!.GetValue<int>();
        var on = (await Cmd("GetSceneItemEnabled", new JsonObject { ["sceneName"] = sc, ["sceneItemId"] = id }))["sceneItemEnabled"]?.GetValue<bool>() ?? true;
        await SetItemEnabledAsync(sc, id, !on);
    }

    public Task StudioToggleAsync() { bool now; lock (gate) now = !Studio; return Cmd("SetStudioModeEnabled", new JsonObject { ["studioModeEnabled"] = now }); }
    public Task PreviewSceneAsync(string scene) => Cmd("SetCurrentPreviewScene", new JsonObject { ["sceneName"] = scene });
    public Task TransitionAsync() => Cmd("TriggerStudioModeTransition");
    public Task SetTransitionAsync(string name) => Cmd("SetCurrentSceneTransition", new JsonObject { ["transitionName"] = name });
    public Task SetTransitionDurationAsync(int ms) => Cmd("SetCurrentSceneTransitionDuration", new JsonObject { ["transitionDuration"] = Math.Clamp(ms, 50, 20000) });
    public Task ReplayToggleAsync() => Cmd("ToggleReplayBuffer");
    public Task ReplaySaveAsync() => Cmd("SaveReplayBuffer");

    public Task ToggleMuteAsync(string input) => string.IsNullOrWhiteSpace(input) ? throw new ArgumentException("Missing audio source name") : Cmd("ToggleInputMute", new JsonObject { ["inputName"] = input });
    public Task SetVolumeAsync(string input, int percent) =>
        Cmd("SetInputVolume", new JsonObject { ["inputName"] = input, ["inputVolumeMul"] = PosToMul(percent / 100.0) });
}
