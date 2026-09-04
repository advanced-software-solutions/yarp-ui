using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using YARPUI.Services;

namespace YARPUI.Hosting;

/// <summary>
/// Schema of the opt-in <c>YarpUi:ForwardedHeaders</c> configuration section. When the whole app
/// sits behind a trusted front (a Cloudflare tunnel, nginx, another load balancer), the direct
/// connection address is the front's, not the visitor's; enabling this section installs ASP.NET
/// Core's forwarded-headers middleware in every hosting mode so <c>Connection.RemoteIpAddress</c>
/// — and with it IP blocking and the request log — reflects the real client.
/// </summary>
public sealed class YarpUiForwardedHeadersOptions
{
    /// <summary>
    /// Installs the middleware when true. Nothing changes in any hosting mode until this is set;
    /// apps that install their own <c>UseForwardedHeaders</c> should leave it off.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Header the trusted front carries the client IP in when it is not the standard
    /// <c>X-Forwarded-For</c> — Cloudflare's <c>CF-Connecting-IP</c>, Akamai's
    /// <c>True-Client-IP</c>. Left empty for fronts that speak the standard header.
    /// </summary>
    public string? ForwardedForHeaderName { get; set; }

    /// <summary>
    /// Additional direct peer addresses whose forwarded values are trusted, added to the loopback
    /// ASP.NET Core trusts by default (a tunnel process on the same machine needs nothing here).
    /// </summary>
    public IList<string> KnownProxies { get; set; } = [];

    /// <summary>
    /// Additional trusted peer networks in CIDR notation (e.g. the docker network a containerized
    /// front connects from).
    /// </summary>
    public IList<string> KnownNetworks { get; set; } = [];

    /// <summary>
    /// Trust forwarded values from any peer by clearing the known-proxy lists. Only safe when
    /// clients cannot reach the app except through the trusted front — the normal tunnel setup,
    /// where the app opens no inbound ports at all.
    /// </summary>
    public bool TrustAllProxies { get; set; }

    /// <summary>
    /// Builds the options the ASP.NET Core middleware runs with. Throws
    /// <see cref="InvalidOperationException"/> naming the first invalid <see cref="KnownProxies"/>
    /// or <see cref="KnownNetworks"/> entry — a typo'd trust range should fail startup loudly
    /// rather than silently leave forwarding off.
    /// </summary>
    public ForwardedHeadersOptions BuildAspNetCoreOptions()
    {
        var forwarded = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            // The default symmetry check rejects mismatched entry counts between X-Forwarded-For
            // and X-Forwarded-Proto, which is the common case when the front sends a single-value
            // custom header (CF-Connecting-IP) without appending to X-Forwarded-Proto. Evaluate
            // both headers independently instead.
            RequireHeaderSymmetry = false,
        };

        if (!string.IsNullOrWhiteSpace(ForwardedForHeaderName))
        {
            forwarded.ForwardedForHeaderName = ForwardedForHeaderName.Trim();
        }

        if (TrustAllProxies)
        {
            forwarded.KnownProxies.Clear();
            forwarded.KnownIPNetworks.Clear();
            return forwarded;
        }

        foreach (var proxy in KnownProxies)
        {
            if (!IPAddress.TryParse(proxy, out var address))
            {
                throw new InvalidOperationException(
                    $"YarpUi:ForwardedHeaders:KnownProxies contains an invalid IP address: '{proxy}'.");
            }

            forwarded.KnownProxies.Add(address);
        }

        foreach (var network in KnownNetworks)
        {
            if (!System.Net.IPNetwork.TryParse(network, out var parsed))
            {
                throw new InvalidOperationException(
                    $"YarpUi:ForwardedHeaders:KnownNetworks contains an invalid network (expected CIDR like '172.18.0.0/16'): '{network}'.");
            }

            forwarded.KnownIPNetworks.Add(parsed);
        }

        return forwarded;
    }
}

/// <summary>
/// Wiring for the opt-in forwarded-headers support. Must be registered before the other UI
/// startup filters: filters run first-registered-outermost, and the forwarded values have to be
/// resolved before the IP-blocking middleware matches on the client address.
/// </summary>
internal static class YarpUiForwardedHeaders
{
    public static void AddYarpUiForwardedHeaders(IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("YarpUi:ForwardedHeaders").Get<YarpUiForwardedHeadersOptions>();
        if (options?.Enabled != true)
        {
            return;
        }

        services.AddSingleton<IStartupFilter>(
            new YarpUiForwardedHeadersStartupFilter(options.BuildAspNetCoreOptions()));
    }
}

/// <summary>
/// Inserts the forwarded-headers middleware in front of every other UI middleware — IP blocking
/// runs after it, so block rules match the resolved client address — and marks the context so
/// request logging prefers the resolved <c>RemoteIpAddress</c> over the caller-controlled
/// X-Forwarded-For chain. Runs its own <see cref="ForwardedHeadersOptions"/> instance instead of
/// one resolved from DI, so it never mutates options a host registered for its own
/// <c>UseForwardedHeaders</c> call.
/// </summary>
internal sealed class YarpUiForwardedHeadersStartupFilter(ForwardedHeadersOptions options) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return builder => next(
            builder
                .UseMiddleware<ForwardedHeadersMiddleware>(Options.Create(options))
                .Use((context, following) =>
                {
                    context.Items[RequestClientIp.ForwardedResolvedItemKey] = true;
                    return following();
                }));
    }
}
