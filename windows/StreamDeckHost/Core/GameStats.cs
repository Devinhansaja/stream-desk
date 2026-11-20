using System.Collections;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace StreamDeckHost.Core;

public sealed class FpsInfo { public double Fps, Ms, Low1; public DateTime At; public string Source = ""; }

/// <summary>
/// Where FPS comes from: (1) the FPS helper (this exe started as administrator; it counts DXGI "Present" events with ETW),
/// (2) MSI Afterburner / RivaTuner (RTSS) shared memory if that is running.
/// </summary>
public sealed class FpsFeed : IDisposable
{
    readonly object gate = new();
    readonly Dictionary<int, FpsInfo> etw = new();
    NamedPipeServerStream? pipe;
    public string HelperStatus { get; private set; } = "Off";
    public bool HelperRunning => HelperStatus == "Running";
    public event Action? Changed;

    /// <summary>Starts the elevated helper (Windows shows the admin prompt). Safe to call twice.</summary>
    public void StartHelper()
    {
        if (HelperStatus is "Starting" or "Running") return;
        var name = "StreamDeckHostFps_" + Environment.ProcessId + "_" + Environment.TickCount;
        try { pipe?.Dispose(); } catch { }
        var p = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        pipe = p;
        HelperStatus = "Starting";
        Changed?.Invoke();
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--fps-helper {name} {Environment.ProcessId}") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
        }
        catch (Exception)   // the user pressed "No" in the admin prompt
        {
            HelperStatus = "Declined"; try { p.Dispose(); } catch { } Changed?.Invoke(); return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                using var to = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                await p.WaitForConnectionAsync(to.Token);
                HelperStatus = "Running"; Changed?.Invoke();
                using var r = new StreamReader(p);
                string? line;
                while ((line = await r.ReadLineAsync()) != null) Parse(line);
            }
            catch { }
            finally
            {
                if (HelperStatus != "Declined") HelperStatus = "Stopped";
                lock (gate) etw.Clear();
                try { p.Dispose(); } catch { }
                Changed?.Invoke();
            }
        });
    }

    public void StopHelper() { try { pipe?.Dispose(); } catch { } }

    void Parse(string line)
    {
        try
        {
            if (JsonNode.Parse(line)?["p"] is not JsonObject o) return;
            lock (gate)
                foreach (var kv in o)
                    if (kv.Value is JsonArray a && int.TryParse(kv.Key, out var pid))
                        etw[pid] = new FpsInfo { Fps = a[0]!.GetValue<double>(), Ms = a[1]!.GetValue<double>(), Low1 = a[2]!.GetValue<double>(), At = DateTime.UtcNow, Source = "etw" };
        }
        catch { }
    }

    public FpsInfo? Get(int pid)
    {
        lock (gate) { if (etw.TryGetValue(pid, out var f) && (DateTime.UtcNow - f.At).TotalSeconds < 3) return f; }
        return Rtss(pid);
    }

    /// <summary>RivaTuner Statistics Server shared memory (header + per-application entries).</summary>
    static FpsInfo? Rtss(int pid)
    {
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting("RTSSSharedMemoryV2", MemoryMappedFileRights.Read);
            using var v = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            if (v.ReadUInt32(0) != 0x52545353) return null;   // 'RTSS'
            uint size = v.ReadUInt32(8), off = v.ReadUInt32(12), n = v.ReadUInt32(16);
            for (uint i = 0; i < n && i < 512; i++)
            {
                long b = off + (long)i * size;
                if (b + 284 > v.Capacity) break;
                if ((int)v.ReadUInt32(b) != pid) continue;
                uint t0 = v.ReadUInt32(b + 268), t1 = v.ReadUInt32(b + 272), fr = v.ReadUInt32(b + 276), ft = v.ReadUInt32(b + 280);
                if (t1 <= t0 || fr == 0) return null;
                double fps = 1000.0 * fr / (t1 - t0);
                return new FpsInfo { Fps = fps, Ms = ft > 0 ? ft / 1000.0 : 1000.0 / fps, Low1 = 0, At = DateTime.UtcNow, Source = "rtss" };
            }
        }
        catch { }
        return null;
    }

    public void Dispose() => StopHelper();
}

/// <summary>Performance numbers for the phone's "Game stats" page: the game being played, FPS, CPU, GPU, RAM, VRAM, temperature.</summary>
public sealed class GameStats : IDisposable
{
    static readonly HashSet<string> Ignore = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "ApplicationFrameHost", "SearchHost", "StartMenuExperienceHost", "ShellExperienceHost", "LockApp", "TextInputHost",
        "StreamDeckHost", "msedgewebview2", "csrss", "System", "Idle", "RuntimeBroker", "svchost", "taskhostw", "sihost", "Widgets", "WidgetService",
    };

    readonly CancellationTokenSource cts = new();
    readonly object gate = new();
    public readonly FpsFeed Fps = new();
    public event Action? Changed;
    volatile int subs;
    JsonObject? last;

    readonly Dictionary<string, CounterSample> prevEng = new();
    PerformanceCounterCategory? catEng, catMem, catAdapter;
    int gamePid; Process? gameProc; TimeSpan prevCpu; DateTime prevAt;
    readonly Queue<double> histFps = new(), histCpu = new(), histGpu = new();
    (string name, double temp, double util, double memUsed, double memTotal, double power, double clock)? nv;
    DateTime nvAt = DateTime.MinValue;
    string? smiPath;
    public string GpuSource { get; private set; } = "";

    public GameStats() { Fps.Changed += () => Changed?.Invoke(); }

    public Func<int>? SubCount;
    public void SetSubscribers(int n) => subs = n;
    public JsonObject Snapshot() { lock (gate) return last != null ? (JsonObject)last.DeepClone() : new JsonObject { ["t"] = "game", ["active"] = null, ["isGame"] = false, ["fpsHelper"] = Fps.HelperStatus }; }
    public void Start() => _ = Task.Run(Loop);
    public void Dispose() { cts.Cancel(); Fps.Dispose(); }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    async Task Loop()
    {
        while (!cts.IsCancellationRequested)
        {
            if ((SubCount?.Invoke() ?? subs) > 0)
            {
                var sw = Stopwatch.StartNew();
                try { var s = Sample(); lock (gate) last = s; Changed?.Invoke(); } catch { }
                if (Environment.GetEnvironmentVariable("SDH_DEBUG_STATS") != null) Console.Error.WriteLine("sample ms " + sw.ElapsedMilliseconds);
                try { await Task.Delay(Math.Max(100, 1000 - (int)sw.ElapsedMilliseconds), cts.Token); } catch { break; }
            }
            else { try { await Task.Delay(400, cts.Token); } catch { break; } }
        }
    }

    // ------------------------------------------------------------------ sampling
    JsonObject Sample()
    {
        var now = DateTime.UtcNow;
        var gpu3d = new Dictionary<int, double>(); var luidTotal = new Dictionary<string, double>();
        ReadEngines(gpu3d, luidTotal);
        var gpuMem = new Dictionary<int, long>(); ReadProcessGpuMemory(gpuMem);

        if ((now - nvAt).TotalSeconds >= 2 && !nvBusy) { nvAt = now; nvBusy = true; _ = Task.Run(() => { try { var r = QueryNvidia(); if (r != null) nv = r; } finally { nvBusy = false; } }); }

        // which process is "the game": keep the current one while it keeps the GPU busy, else the busiest GPU process, else the foreground app
        GetWindowThreadProcessId(GetForegroundWindow(), out var fgU); int fg = (int)fgU;
        bool Usable(int pid) => pid > 4 && !IgnoredPid(pid);
        int pick = 0;
        if (gamePid != 0 && ProcAlive(gamePid) && (gpu3d.GetValueOrDefault(gamePid) > 5 || gamePid == fg)) pick = gamePid;
        else
        {
            var top = gpu3d.Where(kv => Usable(kv.Key)).OrderByDescending(kv => kv.Value).FirstOrDefault();
            if (top.Value > 15) pick = top.Key; else if (Usable(fg)) pick = fg;
        }
        if (pick != gamePid) { gamePid = pick; gameProc = null; prevCpu = TimeSpan.Zero; prevAt = now; histFps.Clear(); }
        if (gamePid != 0 && gameProc == null) { try { gameProc = Process.GetProcessById(gamePid); prevCpu = gameProc.TotalProcessorTime; prevAt = now; } catch { gamePid = 0; } }

        double cpuProc = 0, ramProc = 0, session = 0; string name = "";
        if (gameProc != null)
        {
            try
            {
                gameProc.Refresh();
                var cpuNow = gameProc.TotalProcessorTime; var dt = (now - prevAt).TotalSeconds;
                if (dt > 0.2) { cpuProc = Math.Clamp((cpuNow - prevCpu).TotalSeconds / dt / Environment.ProcessorCount * 100, 0, 100); prevCpu = cpuNow; prevAt = now; lastCpuProc = cpuProc; } else cpuProc = lastCpuProc;
                ramProc = gameProc.WorkingSet64 / 1073741824.0;
                session = (DateTime.Now - gameProc.StartTime).TotalSeconds;
                name = gameProc.ProcessName;
                try { var d = gameProc.MainModule?.FileVersionInfo.FileDescription; if (!string.IsNullOrWhiteSpace(d)) name = d!; } catch { }
            }
            catch { gamePid = 0; gameProc = null; }
        }

        double gpuProc = gamePid != 0 ? Math.Min(100, gpu3d.GetValueOrDefault(gamePid)) : 0;
        double gpuTotal = nv?.util ?? (luidTotal.Count > 0 ? Math.Min(100, luidTotal.Values.Max()) : 0);
        GpuSource = nv != null ? "nvidia" : (luidTotal.Count > 0 ? "windows" : "");
        var fps = gamePid != 0 ? Fps.Get(gamePid) : null;
        bool isGame = gamePid != 0 && (gpuProc >= 10 || fps != null);

        double vramUsed = nv != null ? nv.Value.memUsed / 1024.0 : ReadAdapterDedicated() / 1073741824.0;
        double vramTotal = nv != null ? nv.Value.memTotal / 1024.0 : 0;
        double vramProc = gamePid != 0 ? gpuMem.GetValueOrDefault(gamePid) / 1073741824.0 : 0;
        var (ramUsed, ramTotal) = SystemStats.RamInfo();
        int cpu = SystemStats.Cpu();

        Push(histFps, fps?.Fps ?? 0); Push(histCpu, cpu); Push(histGpu, gpuTotal);
        var ps = System.Windows.Forms.SystemInformation.PowerStatus;
        double bat = ps.BatteryLifePercent is >= 0 and <= 1 ? ps.BatteryLifePercent * 100 : -1;

        JsonArray H(Queue<double> q) { var a = new JsonArray(); foreach (var v in q) a.Add(Math.Round(v, 1)); return a; }
        return new JsonObject
        {
            ["t"] = "game",
            ["active"] = gamePid != 0 ? new JsonObject { ["pid"] = gamePid, ["name"] = name } : null,
            ["isGame"] = isGame, ["session"] = Math.Round(session),
            ["fps"] = fps != null ? Math.Round(fps.Fps, 1) : null, ["frameMs"] = fps != null ? Math.Round(fps.Ms, 1) : null,
            ["low1"] = fps != null && fps.Low1 > 0 ? Math.Round(fps.Low1, 1) : null, ["fpsSource"] = fps?.Source, ["fpsHelper"] = Fps.HelperStatus,
            ["cpu"] = cpu, ["cpuProc"] = Math.Round(cpuProc, 1),
            ["ramUsed"] = Math.Round(ramUsed, 1), ["ramTotal"] = Math.Round(ramTotal, 1), ["ramProc"] = Math.Round(ramProc, 2),
            ["gpu"] = Math.Round(gpuTotal), ["gpuProc"] = Math.Round(gpuProc), ["gpuName"] = nv?.name,
            ["vramUsed"] = Math.Round(vramUsed, 2), ["vramTotal"] = Math.Round(vramTotal, 1), ["vramProc"] = Math.Round(vramProc, 2),
            ["gpuTemp"] = nv != null ? Math.Round(nv.Value.temp) : null, ["gpuPower"] = nv != null ? Math.Round(nv.Value.power, 1) : null, ["gpuClock"] = nv != null ? Math.Round(nv.Value.clock) : null,
            ["battery"] = bat >= 0 ? Math.Round(bat) : null, ["charging"] = ps.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online,
            ["hist"] = new JsonObject { ["fps"] = H(histFps), ["cpu"] = H(histCpu), ["gpu"] = H(histGpu) },
        };
    }
    double lastCpuProc; volatile bool nvBusy;

    static void Push(Queue<double> q, double v) { q.Enqueue(v); while (q.Count > 60) q.Dequeue(); }

    static bool ProcAlive(int pid) { try { using var p = Process.GetProcessById(pid); return !p.HasExited; } catch { return false; } }
    static bool IgnoredPid(int pid) { try { using var p = Process.GetProcessById(pid); return Ignore.Contains(p.ProcessName); } catch { return true; } }

    // ------------------------------------------------------------------ Windows GPU counters (no admin needed)
    static int PidOf(string inst)
    {
        if (!inst.StartsWith("pid_")) return -1;
        int e = inst.IndexOf('_', 4);
        return e > 4 && int.TryParse(inst.AsSpan(4, e - 4), out var p) ? p : -1;
    }

    static string LuidOf(string inst)
    {
        int s = inst.IndexOf("luid_", StringComparison.Ordinal); if (s < 0) return "";
        int e = inst.IndexOf("_phys", s, StringComparison.Ordinal); return e > s ? inst[s..e] : inst[s..];
    }

    void ReadEngines(Dictionary<int, double> perPid, Dictionary<string, double> perLuid)
    {
        try
        {
            catEng ??= PerformanceCounterCategory.Exists("GPU Engine") ? new PerformanceCounterCategory("GPU Engine") : null;
            if (catEng == null) return;
            var data = catEng.ReadCategory(); InstanceDataCollection? util = null;
            foreach (InstanceDataCollection c in data.Values) if (c.CounterName.Contains("Utilization", StringComparison.OrdinalIgnoreCase)) util = c;
            if (util == null) return;
            var seen = new HashSet<string>();
            foreach (InstanceData d in util.Values)
            {
                var n = d.InstanceName; seen.Add(n);
                if (prevEng.TryGetValue(n, out var pv) && n.Contains("engtype_3d", StringComparison.OrdinalIgnoreCase))
                {
                    double v = CounterSample.Calculate(pv, d.Sample);
                    if (v > 0) { int pid = PidOf(n); if (pid > 0) perPid[pid] = perPid.GetValueOrDefault(pid) + v; var l = LuidOf(n); perLuid[l] = perLuid.GetValueOrDefault(l) + v; }
                }
                prevEng[n] = d.Sample;
            }
            foreach (var k in prevEng.Keys.Where(k => !seen.Contains(k)).ToList()) prevEng.Remove(k);
        }
        catch { }
    }

    void ReadProcessGpuMemory(Dictionary<int, long> perPid)
    {
        try
        {
            catMem ??= PerformanceCounterCategory.Exists("GPU Process Memory") ? new PerformanceCounterCategory("GPU Process Memory") : null;
            if (catMem == null) return;
            foreach (InstanceDataCollection c in catMem.ReadCategory().Values)
                if (c.CounterName.Contains("Dedicated", StringComparison.OrdinalIgnoreCase))
                    foreach (InstanceData d in c.Values) { int pid = PidOf(d.InstanceName); if (pid > 0) perPid[pid] = perPid.GetValueOrDefault(pid) + d.Sample.RawValue; }
        }
        catch { }
    }

    long ReadAdapterDedicated()
    {
        try
        {
            catAdapter ??= PerformanceCounterCategory.Exists("GPU Adapter Memory") ? new PerformanceCounterCategory("GPU Adapter Memory") : null;
            if (catAdapter == null) return 0;
            long best = 0;
            foreach (InstanceDataCollection c in catAdapter.ReadCategory().Values)
                if (c.CounterName.Contains("Dedicated", StringComparison.OrdinalIgnoreCase))
                    foreach (InstanceData d in c.Values) best = Math.Max(best, d.Sample.RawValue);   // the busiest adapter = the real GPU
            return best;
        }
        catch { return 0; }
    }

    // ------------------------------------------------------------------ NVIDIA (temperature, power, clock, VRAM total) via nvidia-smi
    (string, double, double, double, double, double, double)? QueryNvidia()
    {
        try
        {
            smiPath ??= new[] { Environment.GetEnvironmentVariable("SDH_NVSMI") ?? "", Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"), @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe" }
                .FirstOrDefault(p => p.Length > 0 && File.Exists(p)) ?? "";
            if (smiPath.Length == 0) return null;
            var psi = new ProcessStartInfo(smiPath, "--query-gpu=name,temperature.gpu,utilization.gpu,memory.used,memory.total,power.draw,clocks.gr --format=csv,noheader,nounits")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            var line = p.StandardOutput.ReadLine();
            if (!p.WaitForExit(3000)) { try { p.Kill(); } catch { } return null; }
            if (string.IsNullOrWhiteSpace(line)) return null;
            var f = line.Split(',').Select(x => x.Trim()).ToArray(); if (f.Length < 7) return null;
            double D(string s) => double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
            return (f[0], D(f[1]), D(f[2]), D(f[3]), D(f[4]), D(f[5]), D(f[6]));
        }
        catch { return null; }
    }
}
