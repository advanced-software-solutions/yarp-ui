using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using YARPUI;

namespace YARPASUI.LoadTests.Support;

/// <summary>
/// Boots the real YARP UI app — the exact <c>YARPUI.Host</c> pipeline
/// (AddYarpUi + UseYarpUiRequestLogging + MapYarpUi + MapReverseProxy) — on a real
/// Kestrel listener with a dynamic port and a throwaway content root / data dir.
/// The seeded routes forward /echo and /echo-post to the given upstream base URL.
/// A real network stack (not TestServer) so NBomber's numbers are honest.
/// </summary>
internal sealed class ProxyTestApp : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly TempDir _root;

    public string BaseUrl { get; }

    private ProxyTestApp(WebApplication app, TempDir root, string baseUrl)
    {
        _app = app;
        _root = root;
        BaseUrl = baseUrl;
    }

    public static async Task<ProxyTestApp> StartAsync(string upstreamBaseUrl)
    {
        var root = new TempDir();
        var contentRoot = Path.Combine(root.Path, "content");
        var dataDir = Path.Combine(root.Path, "data");
        Directory.CreateDirectory(contentRoot);
        Directory.CreateDirectory(dataDir);

        await File.WriteAllTextAsync(
            Path.Combine(contentRoot, "appsettings.json"),
            BuildSeedJson(dataDir, upstreamBaseUrl));

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = contentRoot,
            // Development skips UseHsts/UseExceptionHandler; the proxied hot path
            // is identical either way and the request log stays quiet (see seed).
            EnvironmentName = Environments.Development,
            ApplicationName = typeof(ProxyTestApp).Assembly.GetName().Name,
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        // Same wiring as YARPASUI.Tests: expose the library's compiled Razor Pages
        // to this host so MapYarpUi has every page the real app serves.
        builder.Services.AddMvcCore().ConfigureApplicationPartManager(parts =>
        {
            var uiAssembly = typeof(YarpUiDefaults).Assembly;
            if (!parts.ApplicationParts.Any(p => p.Name == uiAssembly.GetName().Name))
            {
                parts.ApplicationParts.Add(new CompiledRazorAssemblyPart(uiAssembly));
            }
        });

        builder.AddYarpUi();

        var app = builder.Build();
        app.UseStaticFiles();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseYarpUiRequestLogging();   // request-log capture is part of the hot path under test
        app.MapYarpUi();
        app.MapReverseProxy();

        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();

        return new ProxyTestApp(app, root, address);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _root.Dispose();
    }

    /// <summary>
    /// Mirrors the YARPUI.Host seed (echo routes, PowerOfTwoChoices cluster) but points
    /// the cluster at the local upstream instead of postman-echo.com, and silences
    /// per-request logging that would otherwise throttle the run with console I/O.
    /// </summary>
    private static string BuildSeedJson(string dataDir, string upstreamBaseUrl) => $$"""
        {
          "Logging": {
            "LogLevel": {
              "Default": "Warning",
              "Microsoft.AspNetCore": "Warning",
              "Yarp": "Warning"
            }
          },
          "YarpUi": {
            "Auth": { "Username": "admin", "Password": "yarp-admin" },
            "DataDirectory": "{{dataDir.Replace(@"\", @"\\")}}"
          },
          "ReverseProxy": {
            "Routes": {
              "echo-get-route": {
                "ClusterId": "echo-cluster",
                "Order": 1,
                "Match": { "Path": "/echo/{**catch-all}" },
                "Transforms": [ { "PathRemovePrefix": "/echo" } ]
              },
              "echo-post-route": {
                "ClusterId": "echo-cluster",
                "Order": 2,
                "Match": { "Path": "/echo-post/{**catch-all}", "Methods": [ "POST", "PUT", "PATCH" ] },
                "Transforms": [ { "PathRemovePrefix": "/echo-post" } ]
              }
            },
            "Clusters": {
              "echo-cluster": {
                "LoadBalancingPolicy": "PowerOfTwoChoices",
                "Destinations": {
                  "upstream": { "Address": "{{upstreamBaseUrl}}/" }
                }
              }
            }
          }
        }
        """;
}
