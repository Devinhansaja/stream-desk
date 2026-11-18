using System.Diagnostics;
using System.Text.RegularExpressions;

namespace StreamDeckHost.Core;

/// <summary>
/// Optional internet access: runs Cloudflare's "cloudflared" quick tunnel, which gives this PC a public
/// https://xxxx.trycloudflare.com address that forwards to the local host server. No router settings needed.
/// Everything still requires pairing; the editor stays PC-only (its Host-header check rejects tunnel traffic).
/// </summary>
public sealed class Tunnel : IDisposable
{
    readonly int port;
    Process? proc;
    public string? Url { get; private set; }
    public string Status { get; private set; } = "Off";
    public event Action? Changed;

    public Tunnel(int port) => this.port = port;

    public static string? FindExe()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "cloudflared.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "cloudflared", "cloudflared.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "cloudflared", "cloudflared.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", "cloudflared.exe"),
        };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            try { candidates.Add(Path.Combine(dir.Trim('"'), "cloudflared.exe")); } catch { }
        return candidates.FirstOrDefault(File.Exists);
    }

    void Set(string status, string? url = null)
    {
        Status = status;
        Url = url;
        Changed?.Invoke();
    }

    public void Start()
    {
        if (proc is { HasExited: false }) return;
        var exe = FindExe();
        if (exe == null)
        {
            Set("cloudflared is not installed. In PowerShell run:  winget install Cloudflare.cloudflared   then restart this app.");
            return;
        }
        try
        {
            var psi = new ProcessStartInfo(exe, $"tunnel --no-autoupdate --url http://127.0.0.1:{port}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            proc = Process.Start(psi);
            if (proc == null) { Set("Could not start cloudflared."); return; }
            Set("Starting internet tunnel...");
            var p = proc;
            p.OutputDataReceived += (_, _) => { };
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                var m = Regex.Match(e.Data, @"https://[a-z0-9-]+\.trycloudflare\.com");
                if (m.Success && Url == null) Set("Internet access on", m.Value);
            };
            p.EnableRaisingEvents = true;
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.Exited += (_, _) => { if (ReferenceEquals(proc, p)) Set("Tunnel stopped."); };
        }
        catch (Exception e) { Set("Tunnel failed: " + e.Message); }
    }

    public void Stop()
    {
        var p = proc;
        proc = null;
        try { if (p is { HasExited: false }) p.Kill(true); } catch { }
        Set("Off");
    }

    public void Dispose() => Stop();
}
