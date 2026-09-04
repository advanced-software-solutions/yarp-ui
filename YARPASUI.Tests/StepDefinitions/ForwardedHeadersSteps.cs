using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Reqnroll;
using YARPASUI.Tests.Support;
using YARPUI.Services;

namespace YARPASUI.Tests.StepDefinitions;

/// <summary>
/// HTTP-level steps for the opt-in YarpUi:ForwardedHeaders support. Scenarios boot the app
/// with the proxy pipeline (MapReverseProxy + UseYarpUiRequestLogging), like the standalone
/// host, so proxied requests produce real log entries.
/// </summary>
[Binding]
internal sealed class ForwardedHeadersSteps(ApiTestContext api)
{
    private string _lastPath = "";

    // ---- given ----

    [Given("a running standalone YARP UI app with forwarded headers")]
    public async Task GivenAppWithForwardedHeaders() => await BootApp(ForwardedHeadersSeed);

    [Given("a running standalone YARP UI app without forwarded headers")]
    public async Task GivenAppWithoutForwardedHeaders() => await BootApp(PlainSeed);

    // ---- when ----

    [When("I request {string} with CF-Connecting-IP {string}")]
    public async Task WhenRequestWithCustomHeader(string path, string clientIp) =>
        await SendAsync(path, new Dictionary<string, string> { ["CF-Connecting-IP"] = clientIp });

    [When("I request {string} with CF-Connecting-IP {string} and X-Forwarded-For {string}")]
    public async Task WhenRequestWithCustomHeaderAndForwardedFor(string path, string clientIp, string forwardedFor) =>
        await SendAsync(path, new Dictionary<string, string>
        {
            ["CF-Connecting-IP"] = clientIp,
            ["X-Forwarded-For"] = forwardedFor,
        });

    // ---- then ----

    [Then("the last log entry has status {int} and client IP {string}")]
    public async Task ThenLastLogEntry(int status, string clientIp)
    {
        var store = api.App!.App.Services.GetRequiredService<SqliteRequestLogStore>();
        await store.FlushPendingAsync(CancellationToken.None);
        var entry = store.GetAfter(0).LastOrDefault(e => e.Path == _lastPath);
        Assert.NotNull(entry);
        Assert.Equal(status, entry.StatusCode);
        Assert.Equal(clientIp, entry.ClientIp);
    }

    // ---- helpers ----

    private async Task BootApp(string seed)
    {
        api.App?.Dispose();
        api.App = await TestWebApp.CreateAsync(
            app =>
            {
                var json = seed.Replace(TestWebApp.DataDirToken, app.DataDirectory.Replace('\\', '/'));
                File.WriteAllText(app.SeedPath, json);
            },
            configureApp: app =>
            {
                app.UseYarpUiRequestLogging();
                app.MapReverseProxy();
            });
    }

    private async Task SendAsync(string path, Dictionary<string, string> headers)
    {
        _lastPath = path;
        api.Response = await api.App!.SendAsync(HttpMethod.Get, path, headers: headers);
    }

    private const string ForwardedHeadersSeed = """
        {
          "YarpUi": {
            "DataDirectory": "__DATA_DIR__",
            "Auth": { "Username": "admin", "Password": "correct-password" },
            "ForwardedHeaders": {
              "Enabled": true,
              "ForwardedForHeaderName": "CF-Connecting-IP"
            }
          },
          "ReverseProxy": {
            "Routes": {
              "sink-route": {
                "ClusterId": "sink-cluster",
                "Match": { "Path": "/proxied/{**catch-all}" }
              }
            },
            "Clusters": {
              "sink-cluster": {
                "Destinations": { "sink": { "Address": "http://127.0.0.1:9/" } }
              }
            }
          }
        }
        """;

    private const string PlainSeed = """
        {
          "YarpUi": {
            "DataDirectory": "__DATA_DIR__",
            "Auth": { "Username": "admin", "Password": "correct-password" }
          },
          "ReverseProxy": {
            "Routes": {
              "sink-route": {
                "ClusterId": "sink-cluster",
                "Match": { "Path": "/proxied/{**catch-all}" }
              }
            },
            "Clusters": {
              "sink-cluster": {
                "Destinations": { "sink": { "Address": "http://127.0.0.1:9/" } }
              }
            }
          }
        }
        """;
}
