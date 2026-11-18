using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace StreamDeckHost.Core;

public sealed record InstalledApp(string Id, string Name, string Kind, string Target, string? IconPng);

/// <summary>Finds installed desktop apps (Start Menu), Store apps and Steam games.</summary>
public static class AppScanner
{
    static readonly object gate = new();
    static List<InstalledApp>? cache;
    static Dictionary<string, InstalledApp> byId = new();

    public static List<InstalledApp> Scan(bool refresh)
    {
        lock (gate)
        {
            if (cache != null && !refresh) return cache;
            var list = new List<InstalledApp>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(InstalledApp a) { if (names.Add(a.Name)) list.Add(a); }

            foreach (var a in ScanStartMenu()) Add(a);
            foreach (var a in ScanSteam()) Add(a);
            foreach (var a in ScanStoreApps()) Add(a);

            list.Sort((x, y) => string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase));
            cache = list;
            byId = list.ToDictionary(a => a.Id);
            return list;
        }
    }

    public static InstalledApp? Find(string id)
    {
        Scan(false);
        lock (gate) return byId.TryGetValue(id, out var a) ? a : null;
    }

    static string ShortId(string s) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s)))[..12].ToLowerInvariant();

    static IEnumerable<InstalledApp> ScanStartMenu()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        };
        foreach (var root in roots.Where(Directory.Exists))
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories).ToList(); }
            catch { continue; }
            foreach (var f in files)
            {
                var name = Path.GetFileNameWithoutExtension(f);
                if (Regex.IsMatch(name, "uninstall|readme|release notes|documentation|help$", RegexOptions.IgnoreCase)) continue;
                yield return new InstalledApp("lnk:" + ShortId(f), name, "app", f, IconFor(f));
            }
        }
    }

    static IEnumerable<InstalledApp> ScanSteam()
    {
        string? steam = null;
        try { steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string; } catch { }
        if (string.IsNullOrWhiteSpace(steam) || !Directory.Exists(steam)) yield break;

        var libs = new List<string> { Path.Combine(steam, "steamapps") };
        try
        {
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"(.+?)\""))
                    libs.Add(Path.Combine(m.Groups[1].Value.Replace(@"\\", @"\"), "steamapps"));
        }
        catch { }

        foreach (var lib in libs.Distinct().Where(Directory.Exists))
        {
            IEnumerable<string> manifests;
            try { manifests = Directory.EnumerateFiles(lib, "appmanifest_*.acf").ToList(); } catch { continue; }
            foreach (var mf in manifests)
            {
                string txt;
                try { txt = File.ReadAllText(mf); } catch { continue; }
                var id = Regex.Match(txt, "\"appid\"\\s+\"(\\d+)\"").Groups[1].Value;
                var name = Regex.Match(txt, "\"name\"\\s+\"(.+?)\"").Groups[1].Value;
                if (id == "" || name == "" || id == "228980") continue; // redistributables
                yield return new InstalledApp("steam:" + id, name, "game", "steam://rungameid/" + id, null);
            }
        }
    }

    static IEnumerable<InstalledApp> ScanStoreApps()
    {
        var results = new List<InstalledApp>();
        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -Command \"Get-StartApps | ConvertTo-Json -Compress\"")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } return results; }
            if (string.IsNullOrWhiteSpace(output)) return results;
            using var doc = JsonDocument.Parse(output);
            var items = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().ToList()
                : new List<JsonElement> { doc.RootElement };
            foreach (var it in items)
            {
                var name = it.GetProperty("Name").GetString();
                var appId = it.GetProperty("AppID").GetString();
                // Desktop shortcuts have path-like ids and are already found via Start Menu; keep Store (PackageFamily!App) ids.
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(appId) || !appId.Contains('!')) continue;
                results.Add(new InstalledApp("uwp:" + ShortId(appId), name, "store", "shell:AppsFolder\\" + appId, null));
            }
        }
        catch { }
        return results;
    }

    static string? IconFor(string path)
    {
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(path);
            if (icon == null) return null;
            using var bmp = new Bitmap(48, 48, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(icon.ToBitmap(), 0, 0, 48, 48);
            }
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return Convert.ToBase64String(ms.ToArray());
        }
        catch { return null; }
    }

    public static void Launch(InstalledApp app)
    {
        Process.Start(new ProcessStartInfo(app.Target) { UseShellExecute = true });
    }
}
