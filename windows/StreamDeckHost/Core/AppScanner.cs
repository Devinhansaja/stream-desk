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
                yield return new InstalledApp("lnk:" + ShortId(f), name, "app", f, IconForShortcut(f) ?? IconFor(f));
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
                yield return new InstalledApp("steam:" + id, name, "game", "steam://rungameid/" + id, SteamIcon(steam, id));
            }
        }
    }

    static IEnumerable<InstalledApp> ScanStoreApps()
    {
        var results = new List<InstalledApp>();
        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -Command \"[Console]::OutputEncoding=[Text.Encoding]::UTF8; Get-StartApps | ConvertTo-Json -Compress\"")
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
                results.Add(new InstalledApp("uwp:" + ShortId(appId), name, "store", "shell:AppsFolder\\" + appId, IconForStoreApp(appId)));
            }
        }
        catch { }
        return results;
    }

    /// <summary>
    /// The real app icon, not the shortcut picture (which carries the little arrow overlay):
    /// use the shortcut's icon location, else its target program, rendered at 64px.
    /// </summary>
    static string? IconForShortcut(string lnk)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return null;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic sc = shell.CreateShortcut(lnk);
            string target = Environment.ExpandEnvironmentVariables((string)(sc.TargetPath ?? ""));
            string loc = Environment.ExpandEnvironmentVariables((string)(sc.IconLocation ?? ""));

            var candidates = new List<(string path, int idx)>();
            if (loc.Length > 0)
            {
                int comma = loc.LastIndexOf(',');
                string p = loc, idxText = "0";
                if (comma > 1) { p = loc[..comma]; idxText = loc[(comma + 1)..]; }
                int.TryParse(idxText, out var idx);
                candidates.Add((p.Trim('"', ' '), idx));
            }
            if (target.Length > 0) candidates.Add((target, 0));

            foreach (var (path, idx) in candidates)
            {
                if (!File.Exists(path)) continue;
                try
                {
                    using var icon = path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)
                        ? new Icon(path, 128, 128)
                        : Icon.ExtractIcon(path, idx, 128);
                    if (icon == null) continue;
                    return ToPng(icon);
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    static string ToPng(Icon icon)
    {
        using var src = icon.ToBitmap();
        using var bmp = new Bitmap(128, 128, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, 0, 0, 128, 128);
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return Convert.ToBase64String(ms.ToArray());
    }
    // ---------------------------------------------------------------- icons for things that are not plain programs
    static string SquarePng(Image src, bool fromTop)
    {
        int side = Math.Min(src.Width, src.Height);
        int sx = (src.Width - side) / 2, sy = fromTop ? Math.Min((src.Height - side) / 3, src.Height - side) : (src.Height - side) / 2;
        using var bmp = new Bitmap(128, 128, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, new Rectangle(0, 0, 128, 128), new Rectangle(sx, sy, side, side), GraphicsUnit.Pixel);
        }
        using var ms = new MemoryStream(); bmp.Save(ms, ImageFormat.Png);
        return Convert.ToBase64String(ms.ToArray());
    }

    /// <summary>Steam game cover from Steam's local library cache (no internet needed): capsule, else header, else the 32px icon.</summary>
    static string? SteamIcon(string steamPath, string appId)
    {
        try
        {
            var dir = Path.Combine(steamPath, "appcache", "librarycache", appId);
            if (!Directory.Exists(dir)) return null;
            string? Find(params string[] names) { foreach (var n in names) { var f = Directory.EnumerateFiles(dir, n, SearchOption.AllDirectories).FirstOrDefault(); if (f != null) return f; } return null; }
            var file = Find("library_capsule.jpg", "library_600x900.jpg") ?? Find("library_header.jpg", "header.jpg") ?? Directory.EnumerateFiles(dir, "*.jpg").FirstOrDefault();
            if (file == null) return null;
            using var img = Image.FromFile(file);
            return SquarePng(img, fromTop: img.Height > img.Width);
        }
        catch { return null; }
    }

    /// <summary>Microsoft Store / UWP app: the same tile logo the Start menu shows.</summary>
    static string? IconForStoreApp(string appUserModelId)
    {
        try
        {
            int bang = appUserModelId.IndexOf('!'); if (bang < 0) return null;
            var pm = new Windows.Management.Deployment.PackageManager();
            var pkg = pm.FindPackagesForUser("", appUserModelId[..bang]).FirstOrDefault(); if (pkg == null) return null;
            var entries = pkg.GetAppListEntriesAsync().AsTask().GetAwaiter().GetResult();
            var entry = entries.FirstOrDefault(x => x.AppUserModelId == appUserModelId) ?? entries.FirstOrDefault(); if (entry == null) return null;
            var stream = entry.DisplayInfo.GetLogo(new Windows.Foundation.Size(128, 128)).OpenReadAsync().AsTask().GetAwaiter().GetResult();
            using var s = stream.AsStreamForRead();
            using var img = Image.FromStream(s);
            using var bmp = new Bitmap(128, 128, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                float k = Math.Min(128f / img.Width, 128f / img.Height); float w = img.Width * k, h = img.Height * k;
                g.DrawImage(img, (128 - w) / 2, (128 - h) / 2, w, h);
            }
            using var ms = new MemoryStream(); bmp.Save(ms, ImageFormat.Png);
            return Convert.ToBase64String(ms.ToArray());
        }
        catch { return null; }
    }

    static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["resolve"] = "davinci resolve", ["afterfx"] = "after effects", ["adobe premiere pro"] = "premiere pro", ["photoshop"] = "photoshop",
        ["illustrator"] = "illustrator", ["capcut"] = "capcut", ["blender"] = "blender", ["lightroom"] = "lightroom",
    };

    static bool ProcessExe(string token, out string? exe)
    {
        exe = null;
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    using (p)
                    {
                        if (!p.ProcessName.Contains(token, StringComparison.OrdinalIgnoreCase)) continue;
                        var f = p.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(f)) { exe = f; return true; }
                    }
                }
                catch { }
            }
        }
        catch { }
        return false;
    }

    /// <summary>The real icon of a program given its process name(s) ("photoshop, afterfx"): from the running program, else from the Start Menu.</summary>
    public static string? IconForProgram(string names)
    {
        foreach (var raw in names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (ProcessExe(raw, out var exe)) { var i = IconForPath(exe!); if (i != null) return i; }
            var want = Aliases.TryGetValue(raw, out var al) ? al : raw;
            var app = Scan(false).FirstOrDefault(a => a.Name.Contains(want, StringComparison.OrdinalIgnoreCase) && a.IconPng != null);
            if (app != null) return app.IconPng;
        }
        return null;
    }

    static readonly HashSet<string> NotPrograms = new(StringComparer.OrdinalIgnoreCase) { "streamdeckhost", "explorer", "applicationframehost", "textinputhost", "searchhost", "startmenuexperiencehost", "shellexperiencehost", "systemsettings", "widgets" };

    /// <summary>Programs with a visible window right now (process name, window title, icon) so the editor can offer them.</summary>
    public static List<(string Process, string Title, string? Icon)> RunningPrograms()
    {
        var res = new List<(string, string, string?)>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                using (p)
                {
                    if (p.MainWindowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(p.MainWindowTitle) || NotPrograms.Contains(p.ProcessName) || !seen.Add(p.ProcessName)) continue;
                    var f = p.MainModule?.FileName;
                    res.Add((p.ProcessName.ToLowerInvariant(), p.MainWindowTitle, f != null ? IconForPath(f) : null));
                }
            }
            catch { }
        }
        res.Sort((a, b) => string.Compare(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase));
        return res;
    }

    /// <summary>The icon of any file on this PC (program, shortcut, document), 128px PNG as base64.</summary>
    public static string? IconForPath(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return IconForShortcut(path) ?? IconFor(path);
            try { using var big = path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) ? new Icon(path, 128, 128) : Icon.ExtractIcon(path, 0, 128); if (big != null) return ToPng(big); } catch { }
            return IconFor(path);
        }
        catch { return null; }
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
