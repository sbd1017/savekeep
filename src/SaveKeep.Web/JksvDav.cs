using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using SaveKeep.Core.Vault;

namespace SaveKeep.Web;

/// <summary>
/// The minimal WebDAV server JKSV's "upload" talks to, at <c>/dav/jksv/</c> on the web port. It answers only
/// what JKSV's client sends (PROPFIND, MKCOL, PUT; OPTIONS for completeness) and refuses reading back (GET/HEAD),
/// deleting, moving and everything else. Every request needs the receiver's Basic login. It runs after
/// <see cref="LanOnlyMiddleware"/>, which still enforces the LAN, proxy and Host checks here; only its custom-header
/// check is skipped for this path, because JKSV cannot send that header.
/// </summary>
public sealed class JksvDavMiddleware(RequestDelegate next, AppState state)
{
    public static readonly PathString Prefix = "/" + JksvReceiver.BasePath;
    private const string Allow = "OPTIONS, PROPFIND, MKCOL, PUT";

    /// <summary>Case-sensitive, so the exemption in <see cref="LanOnlyMiddleware"/> covers exactly what this handles.</summary>
    public static bool IsDavPath(PathString path, out PathString rest) => path.StartsWithSegments(Prefix, StringComparison.Ordinal, out rest);

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (!IsDavPath(ctx.Request.Path, out var rest)) { await next(ctx); return; }
        var r = state.Jksv;
        var remote = ctx.Connection.RemoteIpAddress?.ToString();
        ctx.Response.Headers.CacheControl = "no-store";

        if (!r.Enabled) { await Text(ctx, 403, "The JKSV receiver is off. Turn it on in SaveKeep first."); return; }
        if (r.LockedOut) { await Text(ctx, 429, "Too many wrong logins; try again in a few minutes."); return; }
        if (!r.CheckBasic(ctx.Request.Headers.Authorization))
        {
            r.RecordAuthFailure(remote);
            ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"SaveKeep JKSV\", charset=\"UTF-8\"";
            await Text(ctx, 401, "Login required.");
            return;
        }

        var error = JksvReceiver.ParsePath(rest.Value ?? "", out var segments, out var isCollection);
        if (error is not null) { await Text(ctx, 400, error); return; }

        switch (ctx.Request.Method.ToUpperInvariant())
        {
            case "OPTIONS":
                ctx.Response.Headers["DAV"] = "1";
                ctx.Response.Headers.Allow = Allow;
                ctx.Response.StatusCode = 200;
                return;
            case "PROPFIND":
                await PropFind(ctx, r, segments);
                return;
            case "MKCOL":
                await MkCol(ctx, r, segments, remote);
                return;
            case "PUT":
                await Put(ctx, r, segments, isCollection, remote);
                return;
            case "DELETE":
                await Text(ctx, 403, "Deleting is refused. Manage saves in SaveKeep.");
                return;
            default: // GET and HEAD included: nothing is ever read back.
                ctx.Response.Headers.Allow = Allow;
                await Text(ctx, 405, "Upload only.");
                return;
        }
    }

    /// <summary>
    /// The root and remembered folders exist and are empty apart from sub-folders; files are never listed, so JKSV
    /// shows nothing to restore, overwrite or delete. Depth 0 describes the folder itself; 1 (or infinity) adds its folders.
    /// </summary>
    private static async Task PropFind(HttpContext ctx, JksvReceiver r, string[] segments)
    {
        if (!r.FolderExists(segments)) { await Text(ctx, 404, "Not found."); return; }
        var depth = ctx.Request.Headers["Depth"].ToString().Trim();
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<D:multistatus xmlns:D=\"DAV:\">\n");
        Collection(sb, segments);
        if (depth != "0")
            foreach (var child in r.ChildFolders(segments)) Collection(sb, [.. segments, child]);
        sb.Append("</D:multistatus>\n");
        ctx.Response.StatusCode = 207;
        ctx.Response.ContentType = "application/xml; charset=utf-8";
        await ctx.Response.WriteAsync(sb.ToString());
    }

    private static void Collection(StringBuilder sb, string[] segments)
    {
        var href = Prefix.Value + "/" + string.Concat(segments.Select(s => Uri.EscapeDataString(s) + "/"));
        var name = segments.Length == 0 ? "jksv" : segments[^1];
        sb.Append("  <D:response><D:href>").Append(SecurityElement.Escape(href)).Append("</D:href><D:propstat><D:prop>")
          .Append("<D:displayname>").Append(SecurityElement.Escape(name)).Append("</D:displayname>")
          .Append("<D:resourcetype><D:collection/></D:resourcetype></D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>\n");
    }

    private static async Task MkCol(HttpContext ctx, JksvReceiver r, string[] segments, string? remote)
    {
        var (status, message) = r.CreateFolder(segments, remote) switch
        {
            JksvReceiver.MkcolOutcome.Created => (201, "Created."),
            JksvReceiver.MkcolOutcome.Exists => (405, "Already exists."),
            JksvReceiver.MkcolOutcome.NoParent => (409, "Parent folder does not exist."),
            _ => (403, "Too many folders."),
        };
        await Text(ctx, status, message);
    }

    private static async Task Put(HttpContext ctx, JksvReceiver r, string[] segments, bool isCollection, string? remote)
    {
        if (isCollection || segments.Length == 0) { await Text(ctx, 405, "PUT needs a file name."); return; }
        // Kestrel's limit is sized for the web page's uploads. Allow more than the receiver's own limit (by more than one
        // read buffer) so the receiver always gives, and logs, the answer itself.
        if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = JksvReceiver.MaxUploadBytes + 1024 * 1024;
        JksvUploadResult result;
        try { result = await r.ReceiveAsync(ctx.Request.Body, ctx.Request.ContentLength, string.Join('/', segments), remote, ctx.RequestAborted); }
        catch (BadHttpRequestException e) { await Text(ctx, e.StatusCode, "Upload refused: " + e.Message); return; }
        catch (OperationCanceledException) { return; } // the console went away mid-upload; staging is already cleaned up
        if (result.Status == 204) { ctx.Response.StatusCode = 204; return; }
        await Text(ctx, result.Status, result.Upload.Message);
    }

    private static Task Text(HttpContext ctx, int status, string message)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        return ctx.Response.WriteAsync(message + "\n");
    }
}

/// <summary>The receiver's settings: status, login (shown once), webdav.json and recent uploads.</summary>
public static class JksvDavApi
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/webdav", (HttpContext ctx, AppState s) => Results.Ok(Status(ctx, s)));

        api.MapPost("/webdav/login", (WebDavLoginRequest req, HttpContext ctx, AppState s) =>
        {
            if (s.Jksv.Enabled && !req.ConfirmReplace)
                return Results.BadRequest(new { error = "The receiver already has a login. Confirm that the old webdav.json should stop working.", replace = true });
            var (username, password) = s.Jksv.Generate();
            var (origin, originWarning) = Origin(ctx);
            return Results.Ok(new { username, password, webdavJson = WebDavJson(origin, username, password), origin, originWarning, status = Status(ctx, s) });
        });

        api.MapPost("/webdav/off", (WebDavOffRequest req, HttpContext ctx, AppState s) =>
        {
            if (!req.Confirm) return Results.BadRequest(new { error = "Confirmation required." });
            s.Jksv.Disable();
            return Results.Ok(Status(ctx, s));
        });
    }

    private static object Status(HttpContext ctx, AppState s)
    {
        var (origin, warning) = Origin(ctx);
        var creds = s.Jksv.Credentials;
        return new
        {
            enabled = creds is not null,
            username = creds?.Username,
            createdAt = creds?.CreatedAt,
            origin,
            originWarning = warning,
            basepath = JksvReceiver.BasePath,
            maxUploadBytes = JksvReceiver.MaxUploadBytes,
            lockedOut = s.Jksv.LockedOut,
            uploads = s.Jksv.RecentUploads(),
        };
    }

    /// <summary>JKSV's config file, exactly as it is copied to <c>sd:/config/JKSV/webdav.json</c>.</summary>
    public static string WebDavJson(string origin, string username, string password) =>
        JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["origin"] = origin, ["basepath"] = JksvReceiver.BasePath, ["username"] = username, ["password"] = password,
        }, new JsonSerializerOptions { WriteIndented = true });

    /// <summary>
    /// http://&lt;server-ip&gt;:&lt;port&gt; as the console must reach it: the server's own address on the connection the browser used.
    /// The Switch cannot resolve <c>.local</c> names, so a warning is given when no IPv4 address is known.
    /// </summary>
    public static (string Origin, string? Warning) Origin(HttpContext ctx)
    {
        var local = ctx.Connection.LocalIpAddress;
        if (local is { IsIPv4MappedToIPv6: true }) local = local.MapToIPv4();
        int port = ctx.Connection.LocalPort != 0 ? ctx.Connection.LocalPort : ctx.Request.Host.Port ?? 80;
        if (local is { AddressFamily: AddressFamily.InterNetwork } && !IPAddress.IsLoopback(local))
            return ($"http://{local}:{port}", null);
        var host = ctx.Request.Host.Host;
        bool ipv4 = IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip);
        return ($"http://{host}:{port}", ipv4 ? null : "Open SaveKeep by the server's IP address (for example http://192.168.1.20:8797) before turning this on, or change 'origin' to that address: the Switch can't use this name.");
    }
}

public sealed record WebDavLoginRequest(bool ConfirmReplace);
public sealed record WebDavOffRequest(bool Confirm);
