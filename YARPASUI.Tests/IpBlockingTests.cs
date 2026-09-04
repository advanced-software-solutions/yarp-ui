using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using YARPASUI.Tests.Support;
using YARPUI.Resources;
using YARPUI.Services;
using YARPUI.Services.IpBlocking;

namespace YARPASUI.Tests;

/// <summary>
/// Covers the IP block list's matching engine, rule parsing/canonicalization, persistence
/// and the blocking middleware. Plain xUnit (like RequestClientIpTests) because the
/// interesting behavior is below the HTTP surface; the end-to-end flow lives in
/// IpBlocking.feature.
/// </summary>
public sealed class IpBlockingTests : IDisposable
{
    private readonly ServiceProvider _services = new ServiceCollection()
        .AddLogging()
        .AddLocalization()
        .BuildServiceProvider();

    private readonly TempDir _root = new();
    private readonly IStringLocalizer<UIStrings> _localizer;

    public IpBlockingTests()
    {
        _localizer = _services.GetRequiredService<IStringLocalizer<UIStrings>>();
    }

    public void Dispose()
    {
        _services.Dispose();
        _root.Dispose();
    }

    private IpBlockListService CreateService() =>
        new(_root.Path, NullLogger<IpBlockListService>.Instance, _localizer);

    private SqliteRequestLogStore CreateStore() =>
        new(System.IO.Path.Combine(_root.Path, "logs.db"), 30, NullLogger<SqliteRequestLogStore>.Instance);

    private static IpBlockRule Rule(string value) => new() { Id = value, Value = value };

    private static IPAddress Ip(string value) => IPAddress.Parse(value);

    // ---- matcher ----

    [Fact]
    public void AnEmptyMatcherNeverBlocks()
    {
        var matcher = IpBlockListMatcher.Empty;

        Assert.True(matcher.IsEmpty);
        Assert.False(matcher.IsBlocked(Ip("203.0.113.7"), out var rule));
        Assert.Null(rule);
    }

    [Fact]
    public void ASingleIpv4BlocksOnlyThatAddress()
    {
        var matcher = new IpBlockListMatcher([Rule("203.0.113.7")]);

        Assert.True(matcher.IsBlocked(Ip("203.0.113.7"), out _));
        Assert.False(matcher.IsBlocked(Ip("203.0.113.6"), out _));
        Assert.False(matcher.IsBlocked(Ip("203.0.113.8"), out _));
    }

    [Fact]
    public void ACidrRuleBlocksItsBoundariesInclusively()
    {
        var matcher = new IpBlockListMatcher([Rule("203.0.113.0/24")]);

        Assert.True(matcher.IsBlocked(Ip("203.0.113.0"), out _));
        Assert.True(matcher.IsBlocked(Ip("203.0.113.128"), out _));
        Assert.True(matcher.IsBlocked(Ip("203.0.113.255"), out _));
        Assert.False(matcher.IsBlocked(Ip("203.0.112.255"), out _));
        Assert.False(matcher.IsBlocked(Ip("203.0.114.0"), out _));
    }

    [Fact]
    public void AZeroPrefixBlocksEveryIpv4AddressButNotIpv6()
    {
        var matcher = new IpBlockListMatcher([Rule("0.0.0.0/0")]);

        Assert.True(matcher.IsBlocked(Ip("0.0.0.0"), out _));
        Assert.True(matcher.IsBlocked(Ip("203.0.113.7"), out _));
        Assert.False(matcher.IsBlocked(Ip("2001:db8::1"), out _));
    }

    [Fact]
    public void ARangeBlocksBothEndpointsInclusively()
    {
        var matcher = new IpBlockListMatcher([Rule("203.0.113.5-203.0.113.99")]);

        Assert.True(matcher.IsBlocked(Ip("203.0.113.5"), out _));
        Assert.True(matcher.IsBlocked(Ip("203.0.113.50"), out _));
        Assert.True(matcher.IsBlocked(Ip("203.0.113.99"), out _));
        Assert.False(matcher.IsBlocked(Ip("203.0.113.4"), out _));
        Assert.False(matcher.IsBlocked(Ip("203.0.113.100"), out _));
    }

    [Fact]
    public void AnIpv4MappedIpv6RequestMatchesTheIpv4Rule()
    {
        var matcher = new IpBlockListMatcher([Rule("203.0.113.7")]);

        Assert.True(matcher.IsBlocked(IPAddress.Parse("::ffff:203.0.113.7"), out _));
    }

    [Fact]
    public void Ipv6SinglesNetworksAndRangesMatch()
    {
        var matcher = new IpBlockListMatcher(
        [
            Rule("2001:db8::1"),
            Rule("2001:db8:a::/48"),
            Rule("2001:db8:b::-2001:db8:b::ff"),
        ]);

        Assert.True(matcher.IsBlocked(Ip("2001:db8::1"), out _));
        Assert.False(matcher.IsBlocked(Ip("2001:db8::2"), out _));

        Assert.True(matcher.IsBlocked(Ip("2001:db8:a::"), out _));
        Assert.True(matcher.IsBlocked(Ip("2001:db8:a:ffff:ffff:ffff:ffff:ffff"), out _));
        Assert.False(matcher.IsBlocked(Ip("2001:db8:c::"), out _)); // outside every rule

        Assert.True(matcher.IsBlocked(Ip("2001:db8:b::1"), out _));
        Assert.True(matcher.IsBlocked(Ip("2001:db8:b::ff"), out _));
        Assert.False(matcher.IsBlocked(Ip("2001:db8:b::100"), out _));
    }

    [Fact]
    public void OverlappingRangesAreMergedAndStillCoverEverything()
    {
        var matcher = new IpBlockListMatcher(
        [
            Rule("203.0.113.0-203.0.114.255"),
            Rule("203.0.114.128-203.0.116.0"),
        ]);

        Assert.True(matcher.IsBlocked(Ip("203.0.113.5"), out _));
        Assert.True(matcher.IsBlocked(Ip("203.0.115.7"), out _));
        Assert.True(matcher.IsBlocked(Ip("203.0.116.0"), out _)); // covered only by the second rule
        Assert.False(matcher.IsBlocked(Ip("203.0.116.1"), out _));
    }

    [Fact]
    public void TheMatchingRuleIsReported()
    {
        var matcher = new IpBlockListMatcher([Rule("203.0.113.0/24")]);

        Assert.True(matcher.IsBlocked(Ip("203.0.113.55"), out var rule));
        Assert.Equal("203.0.113.0/24", rule!.Value);
    }

    // ---- service: parsing, canonicalization, validation ----

    [Fact]
    public void RuleValuesAreCanonicalized()
    {
        var service = CreateService();

        var mapped = AddOk(service, "::ffff:198.51.100.4");
        Assert.Equal("198.51.100.4", mapped.Value);
        Assert.Equal(IpBlockRuleKind.Single, mapped.Kind);

        var cidr = AddOk(service, "2001:DB8::/32");
        Assert.Equal("2001:db8::/32", cidr.Value);
        Assert.Equal(IpBlockRuleKind.Cidr, cidr.Kind);

        Assert.Equal("203.0.113.7", AddOk(service, " 203.0.113.7 ").Value);

        var range = AddOk(service, "203.0.113.5 - 203.0.113.99");
        Assert.Equal("203.0.113.5-203.0.113.99", range.Value);
        Assert.Equal(IpBlockRuleKind.Range, range.Kind);
    }

    [Fact]
    public void InvalidRuleValuesAreRejectedWithClearErrors()
    {
        var service = CreateService();

        AssertRejected(service, "banana", "not a valid");
        AssertRejected(service, "", "Enter an IP address");
        AssertRejected(service, "203.0.113.5/24", "host bits");
        AssertRejected(service, "203.0.113.7/33", "not a valid");
        AssertRejected(service, "2001:db8::/129", "not a valid");
        AssertRejected(service, "10.0.0.9-10.0.0.1", "out of order");
        AssertRejected(service, "10.0.0.1-2001:db8::1", "mixes");
    }

    [Fact]
    public void HostBitsErrorsSuggestTheNetworkAddress()
    {
        var service = CreateService();

        var result = service.AddRule("203.0.113.5/24", null);

        Assert.False(result.Success);
        var error = Assert.Single(result.Errors);
        Assert.Contains("203.0.113.0/24", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DuplicateValuesAreRejected()
    {
        var service = CreateService();
        AddOk(service, "203.0.113.7");

        var duplicate = service.AddRule("203.0.113.7", "again");

        Assert.False(duplicate.Success);
        Assert.Contains("already blocked", Assert.Single(duplicate.Errors), StringComparison.OrdinalIgnoreCase);
        Assert.Single(service.Rules);
    }

    [Fact]
    public void RulesAndSettingsSurviveARestartAndACorruptFileNeverThrows()
    {
        var first = CreateService();
        first.AddRule("203.0.113.0/24", "scanners");
        first.AddRule("2001:db8::/32", null);
        first.SetTrustForwardedFor(true);

        var second = CreateService();
        Assert.Equal(2, second.Rules.Count);
        Assert.Equal("203.0.113.0/24", second.Rules[0].Value);
        Assert.Equal("scanners", second.Rules[0].Note);
        Assert.Equal(IpBlockRuleKind.Cidr, second.Rules[1].Kind);
        Assert.True(second.TrustForwardedFor);

        File.WriteAllText(
            System.IO.Path.Combine(_root.Path, IpBlockListService.BlockListFileName),
            "{ this is not json !!!");
        var third = CreateService();
        Assert.Empty(third.Rules);
        Assert.False(third.TrustForwardedFor);
    }

    [Fact]
    public void CheckAddressReportsTheMatchingRule()
    {
        var service = CreateService();
        service.AddRule("203.0.113.0/24", null);

        var blocked = service.CheckAddress("203.0.113.55");
        Assert.True(blocked.Valid);
        Assert.True(blocked.Blocked);
        Assert.Equal("203.0.113.0/24", blocked.Rule!.Value);

        var allowed = service.CheckAddress("198.51.100.1");
        Assert.True(allowed.Valid);
        Assert.False(allowed.Blocked);

        var invalid = service.CheckAddress("banana");
        Assert.False(invalid.Valid);
    }

    // ---- client IP resolution ----

    [Fact]
    public void TheDirectPeerIsUsedUnlessForwardedForIsTrusted()
    {
        var service = CreateService();
        service.AddRule("203.0.113.7", null);

        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = Ip("198.51.100.1");
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.7";

        Assert.False(service.CurrentMatcher.IsBlocked(service.ResolveClientIp(context)!, out _));

        service.SetTrustForwardedFor(true);
        Assert.True(service.CurrentMatcher.IsBlocked(service.ResolveClientIp(context)!, out _));
    }

    [Fact]
    public void AGarbageForwardedForFallsBackToTheDirectPeer()
    {
        var service = CreateService();
        service.AddRule("198.51.100.1", null);
        service.SetTrustForwardedFor(true);

        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = Ip("198.51.100.1");
        context.Request.Headers["X-Forwarded-For"] = "not-an-ip";

        Assert.True(service.CurrentMatcher.IsBlocked(service.ResolveClientIp(context)!, out _));
    }

    [Fact]
    public void AMappedRemoteAddressIsBlockedByTheIpv4Rule()
    {
        var service = CreateService();
        service.AddRule("203.0.113.7", null);

        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("::ffff:203.0.113.7");

        Assert.True(service.CurrentMatcher.IsBlocked(service.ResolveClientIp(context)!, out _));
    }

    // ---- middleware ----

    [Fact]
    public async Task BlockedRequestsGet403WithoutReachingNextAndAreLogged()
    {
        var service = CreateService();
        service.AddRule("203.0.113.7", null);
        var store = CreateStore();
        var invoked = false;
        var middleware = new IpBlockingMiddleware(_ => { invoked = true; return Task.CompletedTask; });

        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/some/proxied/path";
        context.Connection.RemoteIpAddress = Ip("203.0.113.7");

        await middleware.InvokeAsync(context, service, store);

        Assert.False(invoked);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        await store.FlushPendingAsync(CancellationToken.None);
        var entry = Assert.Single(store.GetAfter(0));
        Assert.Equal(StatusCodes.Status403Forbidden, entry.StatusCode);
        Assert.Equal("/some/proxied/path", entry.Path);
        Assert.Equal("203.0.113.7", entry.ClientIp);
        Assert.Contains("203.0.113.7", entry.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AllowedRequestsReachTheNextMiddleware()
    {
        var service = CreateService();
        service.AddRule("203.0.113.7", null);
        var store = CreateStore();
        var invoked = false;
        var middleware = new IpBlockingMiddleware(ctx =>
        {
            invoked = true;
            ctx.Response.StatusCode = 200;
            return Task.CompletedTask;
        });

        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = Ip("198.51.100.1");

        await middleware.InvokeAsync(context, service, store);

        Assert.True(invoked);
        Assert.Equal(200, context.Response.StatusCode);
    }

    [Fact]
    public async Task AnEmptyListPassesEverythingThroughWithoutLogging()
    {
        var service = CreateService();
        var store = CreateStore();
        var invoked = false;
        var middleware = new IpBlockingMiddleware(_ => { invoked = true; return Task.CompletedTask; });

        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = Ip("203.0.113.7");

        await middleware.InvokeAsync(context, service, store);

        Assert.True(invoked);
        await store.FlushPendingAsync(CancellationToken.None);
        Assert.Empty(store.GetAfter(0));
    }

    [Fact]
    public async Task ForwardedForBlocksWhenTrusted()
    {
        var service = CreateService();
        service.AddRule("203.0.113.7", null);
        service.SetTrustForwardedFor(true);
        var store = CreateStore();
        var invoked = false;
        var middleware = new IpBlockingMiddleware(_ => { invoked = true; return Task.CompletedTask; });

        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = Ip("10.0.0.1"); // a proxy peer
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.7";

        await middleware.InvokeAsync(context, service, store);

        Assert.False(invoked);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    // ---- helpers ----

    private static IpBlockRule AddOk(IpBlockListService service, string value)
    {
        var result = service.AddRule(value, null);
        Assert.True(result.Success, $"expected '{value}' to parse: {string.Join("; ", result.Errors)}");
        return result.Rule!;
    }

    private static void AssertRejected(IpBlockListService service, string value, string fragment)
    {
        var result = service.AddRule(value, null);
        Assert.False(result.Success, $"'{value}' should have been rejected");
        Assert.Contains(fragment, Assert.Single(result.Errors), StringComparison.OrdinalIgnoreCase);
    }
}
