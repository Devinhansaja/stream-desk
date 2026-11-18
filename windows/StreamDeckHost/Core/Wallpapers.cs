using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;

namespace StreamDeckHost.Core;

/// <summary>Wallpaper library stored on the PC (uploaded from the phone) plus "change now" helpers.</summary>
public static class WallpaperLibrary
{
    static readonly string[] Ext = { ".jpg", ".jpeg", ".png", ".bmp", ".webp" };

    public static bool IsImage(string f) => Ext.Contains(Path.GetExtension(f).ToLowerInvariant());

    public static string SafeName(string name)
    {
        name = Path.GetFileName(name);
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        if (name.Length == 0) name = "wallpaper.jpg";
        if (!IsImage(name)) name += ".jpg";
        return name;
    }

    public static string Save(string name, byte[] data)
    {
        AppPaths.Ensure();
        var file = Path.Combine(AppPaths.Wallpapers, SafeName(name));
        File.WriteAllBytes(file, data);
        return file;
    }

    public static List<string> List() =>
        Directory.Exists(AppPaths.Wallpapers)
            ? Directory.EnumerateFiles(AppPaths.Wallpapers).Where(IsImage).OrderBy(Path.GetFileName).ToList()
            : new();

    public static bool Delete(string name)
    {
        var file = Path.Combine(AppPaths.Wallpapers, SafeName(name));
        if (!File.Exists(file)) return false;
        File.Delete(file);
        return true;
    }

    public static string? Thumbnail(string file, int width = 240)
    {
        try
        {
            using var img = Image.FromFile(file);
            int h = Math.Max(1, img.Height * width / Math.Max(1, img.Width));
            using var bmp = new Bitmap(width, h);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(img, 0, 0, width, h);
            }
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Jpeg);
            return Convert.ToBase64String(ms.ToArray());
        }
        catch { return null; }
    }

    // ---- "next wallpaper" rotation state ----
    static int LoadIndex()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(AppPaths.State))?.GetValueOrDefault("wp") ?? -1; }
        catch { return -1; }
    }

    static void SaveIndex(int i)
    {
        try { File.WriteAllText(AppPaths.State, JsonSerializer.Serialize(new Dictionary<string, int> { ["wp"] = i })); } catch { }
    }

    /// <summary>Applies next/random image from a folder (or the uploaded library when folder is empty).</summary>
    public static string Rotate(string? folder, bool random)
    {
        var files = string.IsNullOrWhiteSpace(folder)
            ? List()
            : Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder).Where(IsImage).OrderBy(f => f).ToList()
                : throw new DirectoryNotFoundException("Folder not found: " + folder);
        if (files.Count == 0) throw new InvalidOperationException("No wallpapers available. Upload some from the Backgrounds screen.");
        int idx = random ? Random.Shared.Next(files.Count) : (LoadIndex() + 1) % files.Count;
        SaveIndex(idx);
        Native.SetWallpaper(files[idx]);
        return files[idx];
    }
}
