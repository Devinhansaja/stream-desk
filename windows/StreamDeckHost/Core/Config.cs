using System.Security.Cryptography;
using System.Text.Json;

namespace StreamDeckHost.Core;

public sealed class DeviceRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public DateTime PairedAt { get; set; } = DateTime.Now;
    public DateTime LastSeen { get; set; } = DateTime.Now;
}

public sealed class AppSettings
{
    public int Port { get; set; } = 47800;
    /// <summary>Allow the "shell" action (raw command lines). Off by default.</summary>
    public bool AllowShell { get; set; }
    /// <summary>Automatically run "adb reverse" so an Android phone on USB can reach this PC.</summary>
    public bool AutoUsb { get; set; } = true;
    public bool StartWithWindows { get; set; }
    /// <summary>Download and install new versions from GitHub Releases.</summary>
    public bool AutoUpdate { get; set; } = true;
    public bool FirewallAsked { get; set; }
    /// <summary>Public address phones use away from home: a Tailscale IP, DDNS name, or https URL (optionally with :port).</summary>
    public string RemoteAddress { get; set; } = "";
    /// <summary>Start a free Cloudflare quick tunnel (needs cloudflared) so phones can connect from any network.</summary>
    public bool InternetTunnel { get; set; }
    public List<DeviceRecord> Devices { get; set; } = new();
}

public static class AppPaths
{
    // SDH_DATA_DIR lets automated tests run without touching the real settings.
    public static readonly string Root = Environment.GetEnvironmentVariable("SDH_DATA_DIR") is { Length: > 0 } d ? d : Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StreamDeckHost");
    public static string Settings => Path.Combine(Root, "settings.json");
    public static string Wallpapers => Path.Combine(Root, "Wallpapers");
    public static string Backgrounds => Path.Combine(Root, "Backgrounds");
    public static string State => Path.Combine(Root, "state.json");

    public static void Ensure()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Wallpapers);
        Directory.CreateDirectory(Backgrounds);
    }
}

public sealed class SettingsStore
{
    readonly object gate = new();
    public AppSettings Data { get; private set; } = new();
    int diskPort = 47800;

    public void Load()
    {
        AppPaths.Ensure();
        lock (gate)
        {
            try
            {
                if (File.Exists(AppPaths.Settings))
                    Data = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.Settings)) ?? new();
            }
            catch { Data = new(); }
            diskPort = Data.Port;
        }
    }

    public void Save()
    {
        lock (gate)
        {
            try
            {
                var tmp = AppPaths.Settings + ".tmp";
                // A --port override is for this run only: never write it to disk.
                var runtimePort = Data.Port;
                Data.Port = diskPort;
                try { File.WriteAllText(tmp, JsonSerializer.Serialize(Data, new JsonSerializerOptions { WriteIndented = true })); }
                finally { Data.Port = runtimePort; }
                File.Move(tmp, AppPaths.Settings, true);
            }
            catch { /* best effort */ }
        }
    }

    public static string Hash(string token) =>
        Convert.ToBase64String(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    public static string NewToken(int bytes = 32) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public DeviceRecord? FindAuthorized(string deviceId, string token)
    {
        var h = Hash(token);
        lock (gate)
        {
            var d = Data.Devices.FirstOrDefault(x => x.Id == deviceId);
            if (d == null) return null;
            var a = System.Text.Encoding.UTF8.GetBytes(d.TokenHash);
            var b = System.Text.Encoding.UTF8.GetBytes(h);
            return CryptographicOperations.FixedTimeEquals(a, b) ? d : null;
        }
    }

    public string AddDevice(string id, string name)
    {
        var token = NewToken();
        lock (gate)
        {
            Data.Devices.RemoveAll(d => d.Id == id);
            Data.Devices.Add(new DeviceRecord { Id = id, Name = name, TokenHash = Hash(token) });
        }
        Save();
        return token;
    }

    public void RemoveDevice(string id)
    {
        lock (gate) Data.Devices.RemoveAll(d => d.Id == id);
        Save();
    }
}
