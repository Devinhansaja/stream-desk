using System.Text.Json;
using System.Text.Json.Nodes;

namespace StreamDeckHost.Core;

/// <summary>
/// The deck layout (profiles → pages → buttons) lives on the PC and is edited only in the PC editor.
/// Phones just render it. Button actions are stored in the same flat wire format the executor runs.
/// </summary>
public sealed class LayoutStore
{
    readonly object gate = new();
    JsonObject layout = Default();
    public event Action? Changed;

    static string FilePath => Path.Combine(AppPaths.Root, "layout.json");

    public void Load()
    {
        AppPaths.Ensure();
        lock (gate)
        {
            try
            {
                if (File.Exists(FilePath) && JsonNode.Parse(File.ReadAllText(FilePath)) is JsonObject o && Validate(o) == null)
                    layout = o;
            }
            catch { layout = Default(); }
        }
    }

    public string Json() { lock (gate) return layout.ToJsonString(); }

    public JsonObject Snapshot() { lock (gate) return (JsonObject)layout.DeepClone(); }

    /// <summary>Returns an error message, or null if the layout is acceptable.</summary>
    public static string? Validate(JsonObject o)
    {
        if (o["profiles"] is not JsonArray ps || ps.Count == 0) return "layout needs at least one profile";
        foreach (var p in ps)
        {
            if (p is not JsonObject po || po["pages"] is not JsonArray pages || pages.Count == 0) return "every profile needs pages";
            foreach (var pg in pages)
            {
                if (pg is not JsonObject pgo) return "bad page";
                int cols = pgo["cols"]?.GetValue<int>() ?? 0, rows = pgo["rows"]?.GetValue<int>() ?? 0;
                if (cols is < 1 or > 10 || rows is < 1 or > 10) return "page size must be 1-10";
                if (pgo["slots"] is not JsonArray slots || slots.Count != cols * rows) return "slots must equal cols*rows";
            }
        }
        return null;
    }

    public string? Save(JsonObject incoming)
    {
        var err = Validate(incoming);
        if (err != null) return err;
        lock (gate)
        {
            int v = layout["version"]?.GetValue<int>() ?? 0;
            incoming["version"] = v + 1;
            layout = incoming;
            try
            {
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, layout.ToJsonString());
                File.Move(tmp, FilePath, true);
            }
            catch (Exception e) { return "could not save: " + e.Message; }
        }
        Changed?.Invoke();
        return null;
    }

    // ------------------------------------------------------------------ default layout
    static JsonObject Btn(string label, string icon, string c1, string c2, JsonObject action, bool confirm = false) => new()
    {
        ["id"] = Guid.NewGuid().ToString("N")[..12],
        ["label"] = label, ["icon"] = icon, ["image"] = null,
        ["color1"] = c1, ["color2"] = c2, ["glow"] = 0.6, ["confirm"] = confirm,
        ["action"] = action,
    };

    static JsonObject A(string type, params (string k, JsonNode? v)[] p)
    {
        var o = new JsonObject { ["type"] = type };
        foreach (var (k, v) in p) o[k] = v;
        return o;
    }

    static JsonObject Page(string name, int cols, int rows, params JsonObject?[] buttons)
    {
        var slots = new JsonArray();
        for (int i = 0; i < cols * rows; i++) slots.Add(i < buttons.Length ? buttons[i] : null);
        return new JsonObject { ["id"] = Guid.NewGuid().ToString("N")[..12], ["name"] = name, ["cols"] = cols, ["rows"] = rows, ["slots"] = slots };
    }

    public static JsonObject Default()
    {
        var page1 = Page("Home", 4, 4,
            Btn("Volume Up", "volume_up", "#1e90ff", "#00e5ff", A("volume_up", ("step", 5))),
            Btn("Volume Down", "volume_down", "#1e90ff", "#00e5ff", A("volume_down", ("step", 5))),
            Btn("Mute", "volume_off", "#ff1744", "#b71c1c", A("mute_toggle")),
            Btn("Mic Mute", "mic_off", "#7c4dff", "#d500f9", A("mic_toggle")),
            Btn("Brightness +", "wb_sunny", "#ffc400", "#ff6d00", A("brightness_up", ("step", 10))),
            Btn("Brightness -", "brightness_low", "#ffc400", "#8d6e00", A("brightness_down", ("step", 10))),
            Btn("Screenshot", "photo_camera", "#7c4dff", "#1e90ff", A("screenshot")),
            Btn("Lock PC", "lock", "#1e90ff", "#0d47a1", A("lock")),
            Btn("Shutdown", "power_settings_new", "#00e676", "#1b5e20", A("shutdown"), true),
            Btn("Restart", "restart_alt", "#00e5ff", "#0277bd", A("restart"), true),
            Btn("Open Chrome", "public", "#7c4dff", "#1e90ff", A("url", ("url", "https://www.google.com"))),
            Btn("Task Manager", "speed", "#b0bec5", "#455a64", A("hotkey", ("keys", "ctrl+shift+esc"))),
            Btn("Change BG", "image", "#1e90ff", "#00e676", A("wallpaper", ("mode", "next"))),
            Btn("Play / Pause", "play_arrow", "#ff1744", "#b71c1c", A("media", ("key", "playpause"))),
            Btn("Show Desktop", "desktop_windows", "#b0bec5", "#455a64", A("hotkey", ("keys", "win+d"))));
        var prof = new JsonObject
        {
            ["id"] = "default", ["name"] = "Default",
            ["pages"] = new JsonArray(page1, Page("Games", 4, 4), Page("Page 3", 4, 4)),
        };
        return new JsonObject { ["version"] = 1, ["activeProfile"] = "default", ["profiles"] = new JsonArray(prof) };
    }
}
