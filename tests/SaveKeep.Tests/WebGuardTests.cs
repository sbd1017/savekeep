using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using SaveKeep.Core.Util;
using SaveKeep.Web;

namespace SaveKeep.Tests;

public class WebGuardTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.9", true)]
    [InlineData("172.32.0.9", false)]
    [InlineData("192.168.1.20", true)]
    [InlineData("169.254.10.1", true)]
    [InlineData("100.64.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("::1", true)]
    [InlineData("fd12:3456::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("2001:4860::8888", false)]
    [InlineData("::ffff:192.168.1.5", true)]
    [InlineData("::ffff:8.8.8.8", false)]
    public void Only_local_addresses_are_local(string ip, bool local) => Assert.Equal(local, LanAddress.IsLocal(IPAddress.Parse(ip)));

    private static async Task<int> Run(string remote, string host, string method = "GET", bool csrf = false, string? forwarded = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        ctx.Request.Host = new HostString(host);
        ctx.Request.Method = method;
        if (csrf) ctx.Request.Headers[LanOnlyMiddleware.CsrfHeader] = "1";
        if (forwarded is not null) ctx.Request.Headers["X-Forwarded-For"] = forwarded;
        ctx.Response.Body = new MemoryStream();
        bool reached = false;
        var mw = new LanOnlyMiddleware(_ => { reached = true; return Task.CompletedTask; }, new SaveKeepOptions(), NullLogger<LanOnlyMiddleware>.Instance);
        await mw.InvokeAsync(ctx);
        return reached ? 200 : ctx.Response.StatusCode;
    }

    [Fact]
    public async Task Middleware_enforces_lan_host_and_csrf()
    {
        Assert.Equal(200, await Run("192.168.1.10", "192.168.1.2:8797"));
        Assert.Equal(200, await Run("192.168.1.10", "raspberrypi.local:8797"));
        Assert.Equal(403, await Run("8.8.8.8", "192.168.1.2:8797"));
        Assert.Equal(421, await Run("192.168.1.10", "attacker.example:8797"));
        Assert.Equal(421, await Run("192.168.1.10", "8.8.8.8"));
        Assert.Equal(403, await Run("192.168.1.10", "192.168.1.2", "POST"));
        Assert.Equal(200, await Run("192.168.1.10", "192.168.1.2", "POST", csrf: true));
        Assert.Equal(403, await Run("192.168.1.10", "192.168.1.2", forwarded: "8.8.8.8"));
    }
}
