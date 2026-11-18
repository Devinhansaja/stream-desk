using System.Net.Http;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace StreamDeckHost.Core;

/// <summary>
/// HTTP side of the host:
///   /deck      phone UI (any device on the LAN may load it; the WebSocket still needs pairing)
///   /editor    button editor, PC only (loopback)
///   /api/*     editor API, PC only (loopback + same-origin header)
/// </summary>
public static class WebEndpoints
{
    // The editor may fetch icons from these hosts only (through the host, so the browser can read the pixels).
    static readonly HttpClient imgHttp = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(12) };

    static bool ImageHostAllowed(Uri u)
    {
        if (u.Scheme != Uri.UriSchemeHttps) return false;
        var h = u.Host.ToLowerInvariant();
        if (h == "www.google.com") return u.AbsolutePath.StartsWith("/s2/favicons");
        return h is "api.iconify.design" or "cdn.simpleicons.org" or "cdn.cloudflare.steamstatic.com"
            or "shared.cloudflare.steamstatic.com" or "steamcdn-a.akamaihd.net" || h.EndsWith(".gstatic.com");
    }

    static string? Resource(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        var full = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("." + name, StringComparison.OrdinalIgnoreCase));
        if (full == null) return null;
        using var s = asm.GetManifestResourceStream(full)!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    static byte[]? ResourceBytes(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        var full = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("." + name, StringComparison.OrdinalIgnoreCase));
        if (full == null) return null;
        using var s = asm.GetManifestResourceStream(full)!;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>PC-only: loopback client, and a Host header that is localhost (blocks DNS rebinding).</summary>
    static bool IsLocalEditor(HttpContext ctx, int port)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        if (ip == null || !System.Net.IPAddress.IsLoopback(ip)) return false;
        var host = ctx.Request.Host.Host;
        return host is "localhost" or "127.0.0.1" or "[::1]" or "::1";
    }

    /// <summary>For state-changing calls: custom header (forces a CORS preflight for cross-site pages) + same-origin.</summary>
    static bool IsEditorCall(HttpContext ctx, int port)
    {
        if (!IsLocalEditor(ctx, port)) return false;
        if (ctx.Request.Headers["X-SD-Editor"] != "1") return false;
        var origin = ctx.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && origin != $"http://localhost:{port}" && origin != $"http://127.0.0.1:{port}") return false;
        return true;
    }

    public static void Map(WebApplication app, HostServer server, SettingsStore store, LayoutStore layout)
    {
        int port = store.Data.Port;
        IResult Html(string name) =>
            Resource(name) is { } h ? Results.Text(h, "text/html; charset=utf-8") : Results.NotFound();

        app.MapGet("/", () => "StreamDeck Host is running.");

        app.MapGet("/deck", (HttpContext c) =>
        {
            c.Response.Headers.CacheControl = "no-store";
            return Html("deck.html");
        });

        app.MapGet("/assets/MaterialIcons.otf", () =>
            ResourceBytes("MaterialIcons.otf") is { } b ? Results.File(b, "font/otf") : Results.NotFound());
        app.MapGet("/assets/icons.json", () =>
            Resource("icons.json") is { } j ? Results.Text(j, "application/json") : Results.NotFound());

        app.MapGet("/editor", (HttpContext c) =>
        {
            if (!IsLocalEditor(c, port)) return Results.StatusCode(403);
            c.Response.Headers.CacheControl = "no-store";
            return Html("editor.html");
        });

        app.MapGet("/api/layout", (HttpContext c) =>
            IsLocalEditor(c, port) ? Results.Text(layout.Json(), "application/json") : Results.StatusCode(403));

        app.MapPut("/api/layout", async (HttpContext c) =>
        {
            if (!IsEditorCall(c, port)) return Results.StatusCode(403);
            JsonObject? o;
            try { o = await c.Request.ReadFromJsonAsync<JsonObject>(); } catch { return Results.BadRequest(new { error = "invalid json" }); }
            if (o == null) return Results.BadRequest(new { error = "empty" });
            var err = layout.Save(o);
            return err == null ? Results.Ok(new { ok = true, version = layout.Snapshot()["version"]?.GetValue<int>() }) : Results.BadRequest(new { error = err });
        });

        app.MapGet("/api/apps", async (HttpContext c) =>
        {
            if (!IsLocalEditor(c, port)) return Results.StatusCode(403);
            bool refresh = c.Request.Query["refresh"] == "1";
            var list = await Task.Run(() => AppScanner.Scan(refresh));
            var arr = new JsonArray();
            foreach (var a in list)
                arr.Add(new JsonObject { ["id"] = a.Id, ["name"] = a.Name, ["kind"] = a.Kind, ["icon"] = a.IconPng });
            return Results.Text(arr.ToJsonString(), "application/json");
        });

        app.MapPost("/api/test-action", async (HttpContext c) =>
        {
            if (!IsEditorCall(c, port)) return Results.StatusCode(403);
            JsonObject? a;
            try { a = await c.Request.ReadFromJsonAsync<JsonObject>(); } catch { return Results.BadRequest(new { error = "invalid json" }); }
            if (a == null) return Results.BadRequest(new { error = "empty" });
            try
            {
                var msg = await server.Exec.ExecuteAsync(a);
                return Results.Ok(new { ok = true, message = msg });
            }
            catch (Exception e) { return Results.Ok(new { ok = false, error = e.Message }); }
        });

        app.MapGet("/api/fetch-image", async (HttpContext c) =>
        {
            if (!IsLocalEditor(c, port)) return Results.StatusCode(403);
            if (!Uri.TryCreate(c.Request.Query["url"].ToString(), UriKind.Absolute, out var u) || !ImageHostAllowed(u))
                return Results.BadRequest(new { error = "host not allowed" });
            try
            {
                for (int hop = 0; hop < 4; hop++)
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, u);
                    req.Headers.UserAgent.ParseAdd("StreamDeckHost-IconFetch");
                    using var resp = await imgHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                    if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location != null)
                    {
                        u = new Uri(u, resp.Headers.Location);
                        if (!ImageHostAllowed(u)) return Results.BadRequest(new { error = "redirect not allowed" });
                        continue;
                    }
                    if (!resp.IsSuccessStatusCode) return Results.NotFound();
                    var ct = resp.Content.Headers.ContentType?.MediaType ?? "";
                    if (!ct.StartsWith("image/")) return Results.StatusCode(415);
                    using var ms = new MemoryStream();
                    await using var s = await resp.Content.ReadAsStreamAsync();
                    var buf = new byte[16384]; int n;
                    while ((n = await s.ReadAsync(buf)) > 0) { ms.Write(buf, 0, n); if (ms.Length > 2_000_000) return Results.StatusCode(413); }
                    return Results.File(ms.ToArray(), ct);
                }
                return Results.NotFound();
            }
            catch (Exception e) { return Results.Json(new { error = e.Message }, statusCode: 502); }
        });

        app.MapGet("/api/info", (HttpContext c) =>
            !IsLocalEditor(c, port)
                ? Results.StatusCode(403)
                : Results.Ok(new { pc = server.PcName, version = Updater.Current.ToString(3), phones = server.ConnectedCount, shellAllowed = store.Data.AllowShell }));
    }
}
