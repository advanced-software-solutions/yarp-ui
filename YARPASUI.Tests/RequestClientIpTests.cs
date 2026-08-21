using System.Net;
using Microsoft.AspNetCore.Http;
using YARPUI.Services;

namespace YARPASUI.Tests;

/// <summary>
/// Covers the client-IP resolution used by request logging. Plain xUnit because the Reqnroll
/// scenarios never drive real traffic through the proxy pipeline — entries are seeded via the store.
/// </summary>
public sealed class RequestClientIpTests
{
    [Fact]
    public void FallsBackToRemoteAddressWithoutForwardedHeader()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.23");

        Assert.Equal("198.51.100.23", RequestClientIp.Resolve(context));
    }

    [Fact]
    public void PrefersTheLeftmostForwardedEntry()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.9, 10.0.0.1, 10.0.0.2";

        Assert.Equal("203.0.113.9", RequestClientIp.Resolve(context));
    }

    [Fact]
    public void HandlesASingleForwardedEntry()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.9";

        Assert.Equal("203.0.113.9", RequestClientIp.Resolve(context));
    }

    [Fact]
    public void IgnoresABlankForwardedHeader()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.23");
        context.Request.Headers["X-Forwarded-For"] = " ";

        Assert.Equal("198.51.100.23", RequestClientIp.Resolve(context));
    }

    [Fact]
    public void SupportsIpv6RemoteAddresses()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("2001:db8::1");

        Assert.Equal("2001:db8::1", RequestClientIp.Resolve(context));
    }

    [Fact]
    public void ReturnsNullWhenNothingIsKnown()
    {
        var context = new DefaultHttpContext();

        Assert.Null(RequestClientIp.Resolve(context));
    }
}
