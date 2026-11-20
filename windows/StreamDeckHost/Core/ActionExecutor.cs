using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json.Nodes;

namespace StreamDeckHost.Core;

/// <summary>
/// Executes a JSON action sent by the phone on THIS PC. Buttons are data: the phone sends
/// {"type": "...", ...params}; nothing about a specific button is known to the host.
/// </summary>
public sealed class ActionExecutor
{
    readonly SettingsStore store;
    public ActionExecutor(SettingsStore store) => this.store = store;
    public DiscordRpc? Discord { get; set; }
    public ObsRpc? Obs { get; set; }
    ObsRpc ObsOrThrow => Obs ?? throw new InvalidOperationException("OBS is not set up");

    static string Str(JsonNode n, string key, string def = "") =>
        n[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : def;

    static int Int(JsonNode n, string key, int def)
    {
        if (n[key] is not JsonValue v) return def;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d)) return (int)d;
        if (v.TryGetValue<string>(out var s) && int.TryParse(s, out var p)) return p;
        return def;
    }

    static bool Bool(JsonNode n, string key, bool def) =>
        n[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : def;

    public async Task<string?> ExecuteAsync(JsonNode action, int depth = 0)
    {
        if (depth > 4) throw new InvalidOperationException("Macro nesting too deep");
        var type = Str(action, "type").ToLowerInvariant();
        switch (type)
        {
            case "discord_mute_toggle": await (Discord ?? throw new InvalidOperationException("Discord is not set up")).ToggleMuteAsync(); break;
            case "discord_deafen_toggle": await (Discord ?? throw new InvalidOperationException("Discord is not set up")).ToggleDeafenAsync(); break;
            case "discord_join": await (Discord ?? throw new InvalidOperationException("Discord is not set up")).JoinAsync(Str(action, "channel")); break;
            case "obs_scene": await ObsOrThrow.SetSceneAsync(Str(action, "scene")); break;
            case "obs_source_toggle": await ObsOrThrow.ToggleSourceAsync(Str(action, "scene"), Str(action, "source")); break;
            case "obs_item_enabled": await ObsOrThrow.SetItemEnabledAsync(Str(action, "scene"), Int(action, "id", -1), Bool(action, "value", true)); break;
            case "obs_item_locked": await ObsOrThrow.SetItemLockedAsync(Str(action, "scene"), Int(action, "id", -1), Bool(action, "value", true)); break;
            case "obs_studio_toggle": await ObsOrThrow.StudioToggleAsync(); break;
            case "obs_preview_scene": await ObsOrThrow.PreviewSceneAsync(Str(action, "scene")); break;
            case "obs_transition": await ObsOrThrow.TransitionAsync(); break;
            case "obs_set_transition": await ObsOrThrow.SetTransitionAsync(Str(action, "name")); break;
            case "obs_transition_duration": await ObsOrThrow.SetTransitionDurationAsync(Int(action, "ms", 300)); break;
            case "obs_replay_toggle": await ObsOrThrow.ReplayToggleAsync(); break;
            case "obs_replay_save": await ObsOrThrow.ReplaySaveAsync(); break;
            case "obs_stream_toggle": await ObsOrThrow.ToggleStreamAsync(); break;
            case "obs_record_toggle": await ObsOrThrow.ToggleRecordAsync(); break;
            case "obs_record_pause_toggle": await ObsOrThrow.ToggleRecordPauseAsync(); break;
            case "obs_virtualcam_toggle": await ObsOrThrow.ToggleVirtualCamAsync(); break;
            case "obs_mute_toggle": await ObsOrThrow.ToggleMuteAsync(Str(action, "input")); break;
            case "obs_volume": await ObsOrThrow.SetVolumeAsync(Str(action, "input"), Int(action, "value", 50)); break;
            case "discord_disconnect": await (Discord ?? throw new InvalidOperationException("Discord is not set up")).DisconnectAsync(); break;
            case "none": case "folder": case "back": break; // live tiles / client-side navigation

            // ---------- audio ----------
            case "volume_up": AudioControl.SetVolumeWithFlyout(AudioControl.GetVolume() + Int(action, "step", 5)); break;
            case "volume_down": AudioControl.SetVolumeWithFlyout(AudioControl.GetVolume() - Int(action, "step", 5)); break;
            case "volume_set": AudioControl.SetVolumeWithFlyout(Int(action, "value", 50)); break;
            case "mute_toggle": AudioControl.ToggleMuteWithFlyout(); break;
            case "mute": if (AudioControl.GetMuted() != Bool(action, "value", true)) AudioControl.ToggleMuteWithFlyout(); break;
            case "mic_toggle":
            {
                bool nowMuted = !(AudioControl.GetMicMuted() ?? false);
                AudioControl.SetMicMuted(nowMuted);
                StreamDeckHost.UI.Osd.ShowMic(nowMuted);
                break;
            }
            case "mic_mute": { bool m = Bool(action, "value", true); AudioControl.SetMicMuted(m); StreamDeckHost.UI.Osd.ShowMic(m); break; }

            // ---------- display ----------
            case "brightness_up":
            {
                int cur = BrightnessControl.Get();
                if (cur < 0) throw new InvalidOperationException("No controllable display found");
                BrightnessControl.Set(cur + Int(action, "step", 10)); StreamDeckHost.UI.Osd.ShowBar("\uE706", "Brightness", BrightnessControl.Get()); break;
            }
            case "brightness_down":
            {
                int cur = BrightnessControl.Get();
                if (cur < 0) throw new InvalidOperationException("No controllable display found");
                BrightnessControl.Set(cur - Int(action, "step", 10)); StreamDeckHost.UI.Osd.ShowBar("\uE706", "Brightness", BrightnessControl.Get()); break;
            }
            case "brightness_set": BrightnessControl.Set(Int(action, "value", 50)); StreamDeckHost.UI.Osd.ShowBar("\uE706", "Brightness", BrightnessControl.Get()); break;

            // ---------- input ----------
            case "hotkey": Native.Hotkey(Str(action, "keys")); break;
            case "media": Native.Hotkey(Str(action, "key", "playpause")); break;
            case "text": Native.TypeText(Str(action, "text")); break;

            // ---------- launch ----------
            case "app":
            {
                var id = Str(action, "appId");
                var app = AppScanner.Find(id) ?? throw new InvalidOperationException("App not found on this PC (rescan apps?)");
                AppScanner.Launch(app);
                break;
            }
            case "launch":
            {
                var target = Str(action, "target");
                if (target == "") throw new ArgumentException("Missing target");
                var psi = new ProcessStartInfo(target) { UseShellExecute = true };
                var args = Str(action, "args");
                if (args != "") psi.Arguments = args;
                Process.Start(psi);
                break;
            }
            case "url":
            {
                var url = Str(action, "url");
                if (!(url.StartsWith("http://") || url.StartsWith("https://") || url.Contains("://")))
                    url = "https://" + url;
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                break;
            }
            case "close_app":
            {
                var name = Path.GetFileNameWithoutExtension(Str(action, "name"));
                if (name == "") throw new ArgumentException("Missing process name");
                var procs = Process.GetProcessesByName(name);
                if (procs.Length == 0) return "Not running";
                foreach (var p in procs) { try { if (!p.CloseMainWindow()) p.Kill(); } catch { } }
                break;
            }
            case "shell":
            {
                if (!store.Data.AllowShell)
                    throw new UnauthorizedAccessException("Shell commands are disabled. Enable them in StreamDeck Host > Settings.");
                var cmd = Str(action, "command");
                Process.Start(new ProcessStartInfo("cmd.exe", "/c " + cmd) { CreateNoWindow = true, UseShellExecute = false });
                break;
            }

            // ---------- power ----------
            case "lock": if (!Native.LockWorkStation()) throw new InvalidOperationException("Lock failed"); break;
            case "unlock_pc": await Task.Run(UnlockClient.Unlock); return "Unlocking…";
            case "sleep": Native.SetSuspendState(false, true, true); break;
            case "shutdown": Shutdown("/s /t 0"); break;
            case "restart": Shutdown("/r /t 0"); break;
            case "logout": Shutdown("/l"); break;

            // ---------- misc ----------
            case "screenshot": return "Saved " + Screenshot();
            case "wallpaper":
            {
                var name = Str(action, "name");
                var path = Str(action, "path");
                var mode = Str(action, "mode");
                if (name != "") Native.SetWallpaper(Path.Combine(AppPaths.Wallpapers, WallpaperLibrary.SafeName(name)));
                else if (path != "") Native.SetWallpaper(path);
                else WallpaperLibrary.Rotate(Str(action, "folder"), mode == "random");
                break;
            }
            case "delay": await Task.Delay(Math.Clamp(Int(action, "ms", 500), 0, 60000)); break;
            case "macro":
            {
                if (action["steps"] is not JsonArray steps) throw new ArgumentException("Macro has no steps");
                foreach (var step in steps)
                    if (step != null) await ExecuteAsync(step, depth + 1);
                break;
            }
            default: throw new NotSupportedException($"Unknown action type '{type}'");
        }
        return null;
    }

    static void Shutdown(string args) =>
        Process.Start(new ProcessStartInfo("shutdown.exe", args) { CreateNoWindow = true, UseShellExecute = false });

    static string Screenshot()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "StreamDeck Screenshots");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"Screenshot_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png");
        var r = System.Windows.Forms.SystemInformation.VirtualScreen;
        using var bmp = new Bitmap(r.Width, r.Height);
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Left, r.Top, 0, 0, r.Size);
        bmp.Save(file, ImageFormat.Png);
        return file;
    }
}
