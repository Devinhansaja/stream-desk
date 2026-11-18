using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Windows.Media.Control;

namespace StreamDeckHost.Core;

/// <summary>What is playing on this PC (Spotify, browsers, any app that reports to Windows media controls) and its synced lyrics.</summary>
public static class MediaInfo
{
    public sealed record Now(string Title, string Artist, string Album, double Pos, double Dur, bool Playing, string App)
    {
        public string Key => Title + "|" + Artist;
    }

    static GlobalSystemMediaTransportControlsSessionManager? mgr;
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(10) };
    static readonly Dictionary<string, JsonArray?> lyricCache = new();

    static MediaInfo() => http.DefaultRequestHeaders.UserAgent.ParseAdd("StreamDeckHost");

    public static async Task<Now?> GetAsync()
    {
        try
        {
            mgr ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var sessions = mgr.GetSessions();
            // Prefer a session that is playing, then Spotify, then whatever Windows considers current.
            GlobalSystemMediaTransportControlsSession? s = null;
            foreach (var x in sessions)
                if (x.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) { s = x; break; }
            s ??= sessions.FirstOrDefault(x => x.SourceAppUserModelId.Contains("Spotify", StringComparison.OrdinalIgnoreCase));
            s ??= mgr.GetCurrentSession();
            if (s == null) return null;
            var p = await s.TryGetMediaPropertiesAsync();
            var tl = s.GetTimelineProperties();
            var playing = s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var pos = tl.Position.TotalSeconds;
            if (playing) pos += Math.Max(0, (DateTimeOffset.Now - tl.LastUpdatedTime).TotalSeconds);
            var dur = (tl.EndTime - tl.StartTime).TotalSeconds;
            if (dur > 0) pos = Math.Min(pos, dur);
            if (string.IsNullOrWhiteSpace(p.Title)) return null;
            return new Now(p.Title, p.Artist ?? "", p.AlbumTitle ?? "", Math.Round(pos, 1), Math.Round(dur, 1), playing, s.SourceAppUserModelId);
        }
        catch { return null; }
    }

    /// <summary>Time-synced lyrics from LRCLIB (free, no account): array of {t:seconds, l:text}; null if none found.</summary>
    public static async Task<JsonArray?> LyricsAsync(Now n)
    {
        var key = n.Key;
        lock (lyricCache) if (lyricCache.TryGetValue(key, out var c)) return (JsonArray?)c?.DeepClone();
        JsonArray? result = null;
        try
        {
            var title = Regex.Replace(n.Title, @"\s*[\(\[].*?(remaster|live|version|feat|ft\.).*?[\)\]]", "", RegexOptions.IgnoreCase).Trim();
            var url = "https://lrclib.net/api/get?track_name=" + Uri.EscapeDataString(title) + "&artist_name=" + Uri.EscapeDataString(n.Artist) +
                (n.Album.Length > 0 ? "&album_name=" + Uri.EscapeDataString(n.Album) : "") +
                (n.Dur > 0 ? "&duration=" + ((int)Math.Round(n.Dur)).ToString(CultureInfo.InvariantCulture) : "");
            var r = await http.GetAsync(url);
            string? lrc = null, plain = null;
            if (r.IsSuccessStatusCode)
            {
                var j = JsonNode.Parse(await r.Content.ReadAsStringAsync());
                lrc = j?["syncedLyrics"]?.GetValue<string>();
                plain = j?["plainLyrics"]?.GetValue<string>();
            }
            else
            {
                var sr = await http.GetAsync("https://lrclib.net/api/search?q=" + Uri.EscapeDataString(title + " " + n.Artist));
                if (sr.IsSuccessStatusCode && JsonNode.Parse(await sr.Content.ReadAsStringAsync()) is JsonArray arr)
                    foreach (var it in arr)
                    {
                        lrc = it?["syncedLyrics"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(lrc)) break;
                        plain ??= it?["plainLyrics"]?.GetValue<string>();
                    }
            }
            if (!string.IsNullOrWhiteSpace(lrc)) result = ParseLrc(lrc);
            else if (!string.IsNullOrWhiteSpace(plain))
            {
                // Unsynced: spread evenly over the track so it still scrolls roughly in time.
                var lines = plain.Split('\n', StringSplitOptions.TrimEntries).Where(x => x.Length > 0).ToList();
                result = new JsonArray();
                for (int i = 0; i < lines.Count; i++)
                    result.Add(new JsonObject { ["t"] = n.Dur > 0 ? Math.Round(n.Dur * i / lines.Count, 1) : i * 4.0, ["l"] = lines[i] });
            }
        }
        catch { }
        lock (lyricCache) { if (lyricCache.Count > 60) lyricCache.Clear(); lyricCache[key] = result; }
        return (JsonArray?)result?.DeepClone();
    }

    static JsonArray ParseLrc(string lrc)
    {
        var list = new List<(double t, string l)>();
        foreach (var raw in lrc.Split('\n'))
        {
            var line = raw.Trim();
            var stamps = Regex.Matches(line, @"\[(\d+):(\d+(?:\.\d+)?)\]");
            if (stamps.Count == 0) continue;
            var text = Regex.Replace(line, @"\[[^\]]*\]", "").Trim();
            foreach (Match m in stamps)
                list.Add((int.Parse(m.Groups[1].Value) * 60 + double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), text));
        }
        var arr = new JsonArray();
        foreach (var (t, l) in list.OrderBy(x => x.t)) arr.Add(new JsonObject { ["t"] = Math.Round(t, 2), ["l"] = l });
        return arr;
    }
}
