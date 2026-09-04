using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace YARPASUI.LoadTests.Support;

/// <summary>
/// The upstream backend the proxy forwards to during load tests. The default is a
/// <see href="https://hub.docker.com/r/traefik/whoami">traefik/whoami</see> container
/// started through Testcontainers (podman works as the engine), so results are not
/// dominated by WAN latency the way the seeded postman-echo.com destination would be.
/// Set <c>YARP_LOADTEST_UPSTREAM=local</c> for an in-process Kestrel echo instead
/// (escape hatch for machines with no container engine).
/// </summary>
internal sealed class UpstreamBackend : IAsyncDisposable
{
    public const string Image = "traefik/whoami:v1.12.0";
    private const int ContainerPort = 80;

    private readonly IContainer? _container;
    private readonly WebApplication? _localApp;

    public string BaseUrl { get; }
    public string Kind { get; }

    private UpstreamBackend(string baseUrl, string kind, IContainer? container = null, WebApplication? localApp = null)
    {
        BaseUrl = baseUrl;
        Kind = kind;
        _container = container;
        _localApp = localApp;
    }

    public static async Task<UpstreamBackend> StartAsync(LoadTestProfile profile)
    {
        if (profile.UpstreamMode.Equals("local", StringComparison.OrdinalIgnoreCase))
        {
            return await StartLocalAsync();
        }

        EnsureContainerEngine();
        var container = new ContainerBuilder(Image)
            .WithPortBinding(ContainerPort, assignRandomHostPort: true)
            .WithWaitStrategy(
                Wait.ForUnixContainer()
                    .UntilHttpRequestIsSucceeded(request => request.ForPort(ContainerPort)))
            .Build();

        await container.StartAsync();
        var url = $"http://{container.Hostname}:{container.GetMappedPublicPort(ContainerPort)}";

        // Fail fast with a clear cause instead of 10k confusing errors inside NBomber.
        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var body = await probe.GetStringAsync(url);
        if (!body.Contains("Hostname", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unexpected response from {Image} probe: {body[..Math.Min(200, body.Length)]}");
        }

        return new UpstreamBackend(url, $"container ({Image})", container);
    }

    private static async Task<UpstreamBackend> StartLocalAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.Map("/{**path}", (HttpContext context) =>
            Results.Text($"Hostname: local-echo{Environment.NewLine}Method: {context.Request.Method}", "text/plain"));
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new UpstreamBackend(address, "local (in-process Kestrel)", localApp: app);
    }

    /// <summary>
    /// Testcontainers needs a reachable Docker-compatible engine. On this repo's dev
    /// machines that is podman: a running podman machine exposes the docker_engine
    /// named pipe on Windows (or a unix socket on Linux) that Testcontainers picks
    /// up by default. Throws with actionable guidance when nothing is reachable.
    /// </summary>
    private static void EnsureContainerEngine()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOCKER_HOST")))
        {
            return; // Explicitly configured; trust it.
        }

        var engineReady = OperatingSystem.IsWindows()
            ? Directory.EnumerateFiles(@"\\.\pipe\")
                .Any(p => p.EndsWith("docker_engine", StringComparison.OrdinalIgnoreCase))
            : File.Exists("/var/run/docker.sock");

        if (engineReady)
        {
            // Ryuk (Testcontainers' resource reaper) is unreliable under rootless
            // podman; this harness disposes its containers explicitly anyway.
            Environment.SetEnvironmentVariable("TESTCONTAINERS_RYUK_DISABLED", "true");
            return;
        }

        throw new InvalidOperationException(
            "No Docker-compatible engine is reachable for Testcontainers. " +
            "On this machine podman is installed — run `podman machine start` first, " +
            $"or set DOCKER_HOST, or fall back to the in-process upstream with YARP_LOADTEST_UPSTREAM=local.");
    }

    /// <summary>Non-throwing variant used by the smoke test to skip when no engine exists.</summary>
    public static bool IsEngineAvailable(out string reason)
    {
        try
        {
            EnsureContainerEngine();
            reason = "";
            return true;
        }
        catch (InvalidOperationException ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }

        if (_localApp is not null)
        {
            await _localApp.StopAsync();
            await _localApp.DisposeAsync();
        }
    }
}
