using System.Text.Json;
using System.Text.Json.Nodes;

namespace StreamDeckHost.Core;

/// <summary>
/// The deck layout (profiles â†’ pages â†’ buttons) lives on the PC and is edited only in the PC editor.
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
        EnsureDiscordPage();
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
                if (cols is < 1 or > 12 || rows is < 1 or > 12) return "page size must be 1-12";
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

    /// <summary>Full-page "Discord call" widget (participants, speaking, mute/deafen/leave). Opens by itself when you join a call.</summary>
    public static JsonObject DiscordPage()
    {
        var slots = new JsonArray(); slots.Add(null);
        return new JsonObject { ["id"] = Guid.NewGuid().ToString("N")[..12], ["name"] = "Discord call", ["cols"] = 1, ["rows"] = 1, ["slots"] = slots, ["kind"] = "discord", ["autoOpen"] = true };
    }

    /// <summary>Full-page "OBS control center" widget (scenes, stream / record, audio mixer).</summary>
    public static JsonObject ObsPage()
    {
        var slots = new JsonArray(); slots.Add(null);
        return new JsonObject { ["id"] = Guid.NewGuid().ToString("N")[..12], ["name"] = "OBS", ["cols"] = 1, ["rows"] = 1, ["slots"] = slots, ["kind"] = "obs" };
    }

    /// <summary>Full-page "Game stats" widget (FPS, CPU, GPU, RAM, VRAM, temperature).</summary>
    public static JsonObject GameStatsPage()
    {
        var slots = new JsonArray(); slots.Add(null);
        return new JsonObject { ["id"] = Guid.NewGuid().ToString("N")[..12], ["name"] = "Game stats", ["cols"] = 1, ["rows"] = 1, ["slots"] = slots, ["kind"] = "gamestats" };
    }

    /// <summary>Key pages for Adobe programs. They open by themselves when that program is in front ("autoApp" = process names).</summary>
    public static IEnumerable<JsonObject> AdobePages()
    {
        JsonObject P(string name, string apps, params JsonObject[] keys) { var p = Page(name, 6, 4, keys); p["autoApp"] = apps; return p; }
        const string r1 = "#ff4d6d", r2 = "#b71c1c", b1 = "#31a8ff", b2 = "#001e36", p1 = "#9999ff", p2 = "#2a1a5e", o1 = "#ff9a00", o2 = "#5a2d00";
        yield return P("Photoshop", "photoshop",
            Hk("Undo", "undo", b1, b2, "ctrl+z"), Hk("Redo", "redo", b1, b2, "ctrl+shift+z"), Hk("Save", "save", b1, b2, "ctrl+s"), Hk("Export", "ios_share", b1, b2, "ctrl+alt+shift+s"), Hk("Copy", "content_copy", b1, b2, "ctrl+c"), Hk("Paste", "content_paste", b1, b2, "ctrl+v"),
            Hk("Move", "open_with", b1, b2, "v"), Hk("Brush", "brush", b1, b2, "b"), Hk("Eraser", "auto_fix_normal", b1, b2, "e"), Hk("Lasso", "gesture", b1, b2, "l"), Hk("Crop", "crop", b1, b2, "c"), Hk("Text", "title", b1, b2, "t"),
            Hk("Brush +", "add_circle", b1, b2, "]"), Hk("Brush -", "remove_circle", b1, b2, "["), Hk("Swap colors", "swap_horiz", b1, b2, "x"), Hk("Reset colors", "palette", b1, b2, "d"), Hk("Zoom in", "zoom_in", b1, b2, "ctrl+plus"), Hk("Zoom out", "zoom_out", b1, b2, "ctrl+minus"),
            Hk("Fit screen", "fit_screen", b1, b2, "ctrl+0"), Hk("New layer", "layers", b1, b2, "ctrl+shift+n"), Hk("Merge down", "merge", b1, b2, "ctrl+e"), Hk("Transform", "transform", b1, b2, "ctrl+t"), Hk("Select all", "select_all", b1, b2, "ctrl+a"), Hk("Deselect", "deselect", b1, b2, "ctrl+d"));
        yield return P("Premiere Pro", "adobe premiere pro,premiere pro",
            Hk("Play / Pause", "play_arrow", p1, p2, "space"), Hk("Cut (razor)", "content_cut", p1, p2, "ctrl+k"), Hk("Ripple delete", "delete_sweep", p1, p2, "shift+delete"), Hk("Undo", "undo", p1, p2, "ctrl+z"), Hk("Redo", "redo", p1, p2, "ctrl+shift+z"), Hk("Save", "save", p1, p2, "ctrl+s"),
            Hk("Selection", "near_me", p1, p2, "v"), Hk("Razor tool", "content_cut", p1, p2, "c"), Hk("Mark in", "first_page", p1, p2, "i"), Hk("Mark out", "last_page", p1, p2, "o"), Hk("Marker", "bookmark", p1, p2, "m"), Hk("Import", "file_download", p1, p2, "ctrl+i"),
            Hk("Frame back", "skip_previous", p1, p2, "left"), Hk("Frame forward", "skip_next", p1, p2, "right"), Hk("Prev edit", "keyboard_arrow_up", p1, p2, "up"), Hk("Next edit", "keyboard_arrow_down", p1, p2, "down"), Hk("Zoom in", "zoom_in", p1, p2, "plus"), Hk("Zoom out", "zoom_out", p1, p2, "minus"),
            Hk("Render", "movie_filter", p1, p2, "enter"), Hk("Export", "ios_share", p1, p2, "ctrl+m"), Hk("Select all", "select_all", p1, p2, "ctrl+a"), Hk("Copy", "content_copy", p1, p2, "ctrl+c"), Hk("Paste", "content_paste", p1, p2, "ctrl+v"));
        yield return P("After Effects", "afterfx",
            Hk("Play / Pause", "play_arrow", p1, p2, "space"), Hk("Undo", "undo", p1, p2, "ctrl+z"), Hk("Redo", "redo", p1, p2, "ctrl+shift+z"), Hk("Save", "save", p1, p2, "ctrl+s"), Hk("Split layer", "content_cut", p1, p2, "ctrl+shift+d"), Hk("Duplicate", "content_copy", p1, p2, "ctrl+d"),
            Hk("Render queue", "movie_filter", p1, p2, "ctrl+m"), Hk("New comp", "add_box", p1, p2, "ctrl+n"), Hk("Import", "file_download", p1, p2, "ctrl+i"), Hk("Zoom in", "zoom_in", p1, p2, "plus"), Hk("Zoom out", "zoom_out", p1, p2, "minus"), Hk("Select all", "select_all", p1, p2, "ctrl+a"));
        yield return P("Illustrator", "illustrator",
            Hk("Selection", "near_me", o1, o2, "v"), Hk("Direct select", "call_made", o1, o2, "a"), Hk("Pen", "edit", o1, o2, "p"), Hk("Text", "title", o1, o2, "t"), Hk("Rectangle", "crop_square", o1, o2, "m"), Hk("Ellipse", "circle", o1, o2, "l"),
            Hk("Undo", "undo", o1, o2, "ctrl+z"), Hk("Redo", "redo", o1, o2, "ctrl+shift+z"), Hk("Save", "save", o1, o2, "ctrl+s"), Hk("Group", "group_work", o1, o2, "ctrl+g"), Hk("Ungroup", "workspaces", o1, o2, "ctrl+shift+g"), Hk("Fit screen", "fit_screen", o1, o2, "ctrl+0"),
            Hk("Zoom in", "zoom_in", o1, o2, "ctrl+plus"), Hk("Zoom out", "zoom_out", o1, o2, "ctrl+minus"), Hk("Copy", "content_copy", o1, o2, "ctrl+c"), Hk("Paste", "content_paste", o1, o2, "ctrl+v"));
    }

    /// <summary>Existing layouts get the Discord page once (deleting it afterwards sticks).</summary>
    void EnsureDiscordPage()
    {
        lock (gate)
        {
            bool dirty = false;
            if (layout["profiles"] is JsonArray pf && pf.Count > 0 && pf[0]?["pages"] is JsonArray pgs)
            {
                if (layout["gameAdded"]?.GetValue<bool>() != true) { layout["gameAdded"] = true; dirty = true; if (!pgs.Any(p => p?["kind"]?.GetValue<string>() == "gamestats")) pgs.Add(GameStatsPage()); }
                if (layout["adobeAdded"]?.GetValue<bool>() != true) { layout["adobeAdded"] = true; dirty = true; if (!pgs.Any(p => p?["autoApp"] != null)) foreach (var ap in AdobePages()) pgs.Add(ap); }
            }
            if (dirty) { try { File.WriteAllText(FilePath, layout.ToJsonString()); } catch { } }
            if (layout["discordAdded"]?.GetValue<bool>() == true && layout["obsAdded"]?.GetValue<bool>() == true) return;
            if (layout["discordAdded"]?.GetValue<bool>() != true)
            {
            layout["discordAdded"] = true;
            if (layout["profiles"] is JsonArray ps && ps.Count > 0 && ps[0]?["pages"] is JsonArray pages
                && !pages.Any(p => p?["kind"]?.GetValue<string>() == "discord"))
                pages.Add(DiscordPage());
            }
            if (layout["obsAdded"]?.GetValue<bool>() != true)
            {
                layout["obsAdded"] = true;
                if (layout["profiles"] is JsonArray ps2 && ps2.Count > 0 && ps2[0]?["pages"] is JsonArray pages2 && !pages2.Any(p => p?["kind"]?.GetValue<string>() == "obs"))
                    pages2.Add(ObsPage());
            }
            try { File.WriteAllText(FilePath, layout.ToJsonString()); } catch { }
        }
    }

    static JsonObject Tile(string label, string widget, string color)
    {
        var b = Btn(label, "bolt", color, color, A("none"));
        b["widget"] = widget;
        return b;
    }

    static JsonObject Hk(string label, string icon, string c1, string c2, string keys, bool confirm = false) =>
        Btn(label, icon, c1, c2, A("hotkey", ("keys", keys)), confirm);

    /// <summary>First-run layout: 6x4 = 24 keys per page and several pages. All of it is editable.</summary>
    public static JsonObject Default()
    {
        var home = Page("Home", 6, 4,
            Btn("Volume Up", "volume_up", "#1e90ff", "#00e5ff", A("volume_up", ("step", 5))),
            Btn("Volume Down", "volume_down", "#1e90ff", "#00e5ff", A("volume_down", ("step", 5))),
            Btn("Mute", "volume_off", "#ff1744", "#b71c1c", A("mute_toggle")),
            Btn("Mic Mute", "mic_off", "#7c4dff", "#d500f9", A("mic_toggle")),
            Btn("Brightness +", "wb_sunny", "#ffc400", "#ff6d00", A("brightness_up", ("step", 10))),
            Btn("Brightness -", "brightness_low", "#ffc400", "#8d6e00", A("brightness_down", ("step", 10))),
            Btn("Play / Pause", "play_arrow", "#1de9b6", "#00897b", A("media", ("key", "playpause"))),
            Btn("Previous", "skip_previous", "#1de9b6", "#00897b", A("media", ("key", "prev"))),
            Btn("Next", "skip_next", "#1de9b6", "#00897b", A("media", ("key", "next"))),
            Btn("Screenshot", "photo_camera", "#7c4dff", "#1e90ff", A("screenshot")),
            Hk("Snip", "content_cut", "#7c4dff", "#1e90ff", "win+shift+s"),
            Btn("Change BG", "image", "#1e90ff", "#00e676", A("wallpaper", ("mode", "next"))),
            Hk("Desktop", "desktop_windows", "#b0bec5", "#455a64", "win+d"),
            Hk("Task Manager", "speed", "#b0bec5", "#455a64", "ctrl+shift+esc"),
            Hk("Explorer", "folder", "#ffc400", "#ff6d00", "win+e"),
            Hk("Settings", "settings", "#b0bec5", "#455a64", "win+i"),
            Btn("Open Chrome", "public", "#7c4dff", "#1e90ff", A("url", ("url", "https://www.google.com"))),
            Btn("YouTube", "smart_display", "#ff1744", "#b71c1c", A("url", ("url", "https://www.youtube.com"))),
            Btn("Notepad", "edit_note", "#1de9b6", "#00897b", A("launch", ("target", "notepad.exe"))),
            Btn("Calculator", "calculate", "#ff6d00", "#bf360c", A("launch", ("target", "calc.exe"))),
            Btn("Lock PC", "lock", "#1e90ff", "#0d47a1", A("lock")),
            Btn("Sleep", "bedtime", "#7c4dff", "#311b92", A("sleep"), true),
            Btn("Restart", "restart_alt", "#00e5ff", "#0277bd", A("restart"), true),
            Btn("Shutdown", "power_settings_new", "#00e676", "#1b5e20", A("shutdown"), true));

        var stats = Page("Stats", 6, 4,
            Tile("CPU", "cpu", "#22e58b"), Tile("RAM", "ram", "#1e90ff"), Tile("C:", "disk:C", "#ffc400"),
            Tile("Uptime", "uptime", "#7c4dff"), Tile("Clock", "clock", "#ffffff"), Tile("Volume", "volume", "#00e5ff"),
            Tile("Brightness", "brightness", "#ff6d00"));

        var allPages = new JsonArray(home, stats, Page("Apps", 6, 4), Page("Games", 6, 4), Page("Streaming", 6, 4), Page("Page 6", 6, 4), DiscordPage(), ObsPage(), GameStatsPage());
        foreach (var ap in AdobePages()) allPages.Add(ap);
        var prof = new JsonObject { ["id"] = "default", ["name"] = "Default", ["pages"] = allPages };
        return new JsonObject { ["version"] = 1, ["discordAdded"] = true, ["obsAdded"] = true, ["gameAdded"] = true, ["adobeAdded"] = true, ["style"] = "flat", ["background"] = null, ["keyAlpha"] = 0.55, ["keyBlur"] = 0, ["activeProfile"] = "default", ["profiles"] = new JsonArray(prof) };
    }
}