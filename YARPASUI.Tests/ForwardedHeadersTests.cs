using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using YARPUI.Hosting;

namespace YARPASUI.Tests;

/// <summary>
/// Covers the YarpUi:ForwardedHeaders schema and the ASP.NET Core options built from it.
/// The end-to-end behavior (middleware order, logging, IP blocking) lives in
/// ForwardedHeaders.feature.
/// </summary>
public sealed class ForwardedHeadersTests
{
    private static YarpUiForwardedHeadersOptions? Bind(params KeyValuePair<string, string?>[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        return configuration.GetSection("YarpUi:ForwardedHeaders").Get<YarpUiForwardedHeadersOptions>();
    }

    [Fact]
    public void SectionDisabledByDefault()
    {
        Assert.False(new YarpUiForwardedHeadersOptions().Enabled);
        Assert.Null(Bind(new KeyValuePair<string, string?>("YarpUi:Auth:Username", "admin")));
    }

    [Fact]
    public void CustomHeaderNameIsApplied()
    {
        var options = Bind(
            new KeyValuePair<string, string?>("YarpUi:ForwardedHeaders:Enabled", "true"),
            new KeyValuePair<string, string?>("YarpUi:ForwardedHeaders:ForwardedForHeaderName", "CF-Connecting-IP"));

        Assert.NotNull(options);
        Assert.True(options!.Enabled);
        Assert.Equal("CF-Connecting-IP", options.BuildAspNetCoreOptions().ForwardedForHeaderName);
    }

    [Fact]
    public void StandardHeaderUsedByDefault()
    {
        var options = Bind(new KeyValuePair<string, string?>("YarpUi:ForwardedHeaders:Enabled", "true"));

        var forwarded = options!.BuildAspNetCoreOptions();
        Assert.Equal("X-Forwarded-For", forwarded.ForwardedForHeaderName);
        Assert.True(forwarded.ForwardedHeaders.HasFlag(ForwardedHeaders.XForwardedFor));
        Assert.True(forwarded.ForwardedHeaders.HasFlag(ForwardedHeaders.XForwardedProto));
    }

    [Fact]
    public void SingleValueCustomHeadersDoNotRequireSymmetry()
    {
        // Cloudflare's CF-Connecting-For is one entry while X-Forwarded-Proto is absent or
        // unrelated; the default symmetry check would reject the pair wholesale.
        var options = Bind(new KeyValuePair<string, string?>("YarpUi:ForwardedHeaders:Enabled", "true"));

        Assert.False(options!.BuildAspNetCoreOptions().RequireHeaderSymmetry);
    }

    [Fact]
    public void KnownProxiesAreAddedOnTopOfTheLoopbackDefaults()
    {
        var options = Bind(
            new KeyValuePair<string, string?>("YarpUi:ForwardedHeaders:Enabled", "true"),
            new KeyValuePair<string, string?>("YarpUi:ForwardedHeaders:KnownProxies:0", "203.0.113.10"),
            new KeyValuePair<string, string?>("YarpUi:ForwardedHeaders:KnownProxies:1", "2001:db8::5"));

        var forwarded = options!.BuildAspNetCoreOptions();
        Assert.Contains(IPAddress.Parse("203.0.113.10"), forwarded.KnownProxies);
        Assert.Contains(IPAddress.Parse("2001:db8::5"), forwarded.KnownProxies);
        Assert.Contains(IPAddress.IPv6Loopback, forwarded.KnownProxies); // ASP.NET Core default
    }

    [Fact]
    public void KnownNetworksAreParsedFromCidr()
    {
        var options = Bind(
            new KeyValuePair<string, string?>("YarpUi:ForwardedHeaders:Enabled", "true"),
            new KeyValuePair<string, string?>("YarpUi:ForwardedHeaders:KnownNetworks:0", "172.18.0.0/16"));

        var forwarded = options!.BuildAspNetCoreOptions();
        Assert.Contains(System.Net.IPNetwork.Parse("172.18.0.0/16"), forwarded.KnownIPNetworks);
        Assert.NotEmpty(forwarded.KnownIPNetworks); // loopback default survives
    }

    [Fact]
    public void TrustAllProxiesClearsTheDefaultProxyLists()
    {
        var options = Bind(
            new KeyValuePair<string, string?>("YarpUi:ForwardedHeaders:Enabled", "true"),
            new KeyValuePair<string, string?>("YarpUi:ForwardedHeaders:TrustAllProxies", "true"));

        var forwarded = options!.BuildAspNetCoreOptions();
        Assert.Empty(forwarded.KnownProxies);
        Assert.Empty(forwarded.KnownIPNetworks);
    }

    [Fact]
    public void AnInvalidProxyIpFailsLoudly()
    {
        var options = new YarpUiForwardedHeadersOptions { KnownProxies = ["banana"] };

        var error = Assert.Throws<InvalidOperationException>(() => options.BuildAspNetCoreOptions());
        Assert.Contains("KnownProxies", error.Message);
        Assert.Contains("banana", error.Message);
    }

    [Fact]
    public void AnInvalidNetworkFailsLoudly()
    {
        var options = new YarpUiForwardedHeadersOptions { KnownNetworks = ["not-a-network"] };

        var error = Assert.Throws<InvalidOperationException>(() => options.BuildAspNetCoreOptions());
        Assert.Contains("KnownNetworks", error.Message);
        Assert.Contains("not-a-network", error.Message);
    }
}
