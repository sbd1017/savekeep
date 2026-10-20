using System.Net;
using SaveKeep.Core.Util;

namespace SaveKeep.Web;

/// <summary>
/// Keeps the service on the local network: rejects non-private client addresses, proxied requests,
/// unexpected Host headers (DNS rebinding), and state-changing requests without the custom header that
/// browsers can't send cross-origin without a preflight.
/// </summary>
public sealed class LanOnlyMiddleware(RequestDelegate next, SaveKeepOptions options, ILogger<LanOnlyMiddleware> log)
{
    public const string CsrfHeader = "X-SaveKeep";

    public async Task InvokeAsync(HttpContext ctx)
    {
        var remote = ctx.Connection.RemoteIpAddress;
        if (remote is null || !LanAddress.IsLocal(remote))
        {
            log.LogWarning("Rejected non-local client {Remote}", remote);
            await Deny(ctx, 403, "Local network only.");
            return;
        }
        if (ctx.Request.Headers.ContainsKey("X-Forwarded-For") || ctx.Request.Headers.ContainsKey("Forwarded"))
        {
            await Deny(ctx, 403, "Proxied requests are not accepted.");
            return;
        }
        if (!IsAllowedHost(ctx.Request.Host.Host))
        {
            await Deny(ctx, 421, "Unexpected Host header.");
            return;
        }
        // JKSV can't send the custom header. Only /dav/jksv is exempt, and every request there needs the receiver's
        // Basic login instead (see JksvDavMiddleware). Browsers can't send PUT/PROPFIND/MKCOL cross-origin without a
        // preflight, and the preflight fails that login.
        if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method)
            && !JksvDavMiddleware.IsDavPath(ctx.Request.Path, out _)
            && ctx.Request.Headers[CsrfHeader] != "1")
        {
            await Deny(ctx, 403, "Missing request header.");
            return;
        }
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers["X-Frame-Options"] = "DENY";
        ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
        ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; connect-src 'self'; frame-ancestors 'none'";
        if (ctx.Request.Path.StartsWithSegments("/api")) ctx.Response.Headers.CacheControl = "no-store";
        await next(ctx);
    }

    private bool IsAllowedHost(string host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip)) return LanAddress.IsLocal(ip);
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return true; // mDNS, e.g. raspberrypi.local
        if (host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return true;
        return options.AllowedHostNames.Any(h => h.Equals(host, StringComparison.OrdinalIgnoreCase));
    }

    private static Task Deny(HttpContext ctx, int status, string message)
    {
        ctx.Response.StatusCode = status;
        return ctx.Response.WriteAsJsonAsync(new { error = message });
    }
}

public static class Network
{
    /// <summary>Resolves a console address and insists it is on the local network.</summary>
    public static async Task<IPAddress> ResolveConsoleAsync(string host, CancellationToken ct)
    {
        host = host.Trim();
        if (host.Length is 0 or > 253) throw new ArgumentException("Enter the console's IP address.");
        if (IPAddress.TryParse(host, out var ip))
            return LanAddress.IsLocal(ip) ? ip : throw new ArgumentException("The console address must be on your local network.");
        var addrs = await Dns.GetHostAddressesAsync(host, ct);
        return addrs.FirstOrDefault(LanAddress.IsLocal) ?? throw new ArgumentException("That name does not resolve to a local-network address.");
    }
}
