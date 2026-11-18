using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace StreamDeckHost.Core;

public sealed record UpdateInfo(Version Version, string ExeUrl, string? ShaUrl, string PageUrl);

/// <summary>
/// Self-update from GitHub Releases. A release must contain the asset "StreamDeckHost.exe" (and optionally
/// "StreamDeckHost.exe.sha256"). The new exe is staged next to the running one and swapped in on restart.
/// </summary>
public static class Updater
{
    public const string Repo = "Devinhansaja/stream-desk";
    static readonly HttpClient http = MakeClient();

    static HttpClient MakeClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("StreamDeckHost-Updater");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    public static Version Current => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
    static string ExePath => Environment.ProcessPath!;
    static string Dir => Path.GetDirectoryName(ExePath)!;
    static string StagedPath => Path.Combine(Dir, "StreamDeckHost.new.exe");
    static string OldPath => Path.Combine(Dir, "StreamDeckHost.old.exe");

    static Version Norm(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

    public static bool CanWriteHere()
    {
        try
        {
            var t = Path.Combine(Dir, ".write-test");
            File.WriteAllText(t, "x");
            File.Delete(t);
            return true;
        }
        catch { return false; }
    }

    public static async Task<UpdateInfo?> CheckAsync()
    {
        using var resp = await http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest");
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var ver) || Norm(ver) <= Norm(Current)) return null;
        string? exe = null, sha = null;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString();
            var url = a.GetProperty("browser_download_url").GetString();
            if (name == "StreamDeckHost.exe") exe = url;
            else if (name == "StreamDeckHost.exe.sha256") sha = url;
        }
        if (exe == null) return null;
        return new UpdateInfo(ver, exe, sha, root.GetProperty("html_url").GetString() ?? "");
    }

    /// <summary>Downloads and verifies the new exe into StreamDeckHost.new.exe.</summary>
    public static async Task DownloadAsync(UpdateInfo u)
    {
        var tmp = StagedPath + ".part";
        using (var r = await http.GetAsync(u.ExeUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            r.EnsureSuccessStatusCode();
            await using var src = await r.Content.ReadAsStreamAsync();
            await using var dst = File.Create(tmp);
            await src.CopyToAsync(dst);
        }
        if (u.ShaUrl != null)
        {
            var expected = (await http.GetStringAsync(u.ShaUrl)).Trim().Split(' ', '\t', '\n')[0].ToLowerInvariant();
            await using var f = File.OpenRead(tmp);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(f)).ToLowerInvariant();
            if (actual != expected) { File.Delete(tmp); throw new InvalidDataException("Update checksum mismatch"); }
        }
        File.Move(tmp, StagedPath, true);
    }

    public static bool HasStaged()
    {
        try
        {
            if (!File.Exists(StagedPath)) return false;
            var v = FileVersionInfo.GetVersionInfo(StagedPath).FileVersion;
            if (Version.TryParse(v, out var sv) && Norm(sv) > Norm(Current)) return true;
            File.Delete(StagedPath);
        }
        catch { }
        return false;
    }

    /// <summary>Swaps the staged exe in and launches it. The caller must exit right after (returns true if launched).</summary>
    public static bool ApplyStagedAndRestart()
    {
        try
        {
            if (File.Exists(OldPath)) File.Delete(OldPath);
            File.Move(ExePath, OldPath);               // a running exe can be renamed
            File.Move(StagedPath, ExePath);
            Process.Start(new ProcessStartInfo(ExePath, $"--tray --wait-pid {Environment.ProcessId}") { UseShellExecute = false });
            return true;
        }
        catch
        {
            try { if (!File.Exists(ExePath) && File.Exists(OldPath)) File.Move(OldPath, ExePath); } catch { }
            return false;
        }
    }

    public static void CleanupOld()
    {
        try { if (File.Exists(OldPath)) File.Delete(OldPath); } catch { }
    }
}
