using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Reqnroll;
using YARPASUI.Tests.Support;
using YARPUI.Services;
using YARPUI.Services.IpBlocking;

namespace YARPASUI.Tests.StepDefinitions;

/// <summary>HTTP-level steps for IP blocking: the /api/yarp/ipblocking endpoints and the pipeline behavior.</summary>
[Binding]
internal sealed class IpBlockingSteps(ApiTestContext api, IpBlockingTestContext ip)
{
    // ---- given ----

    [Given("a running standalone YARP UI app with a retained data directory")]
    public async Task GivenRetainedApp()
    {
        api.App?.Dispose();
        ip.Root = new TempDir();
        api.App = await TestWebApp.CreateAsync(app =>
        {
            var seed = """
                {
                  "YarpUi": {
                    "DataDirectory": "__DATA_DIR__",
                    "Auth": { "Username": "admin", "Password": "correct-password" }
                  }
                }
                """.Replace(TestWebApp.DataDirToken, app.DataDirectory.Replace('\\', '/'));
            File.WriteAllText(app.SeedPath, seed);
        }, root: ip.Root);
    }

    [Given("the IP blocking rule {string} and forwarded-for trust are configured")]
    public async Task GivenRuleAndForwardedForTrust(string value)
    {
        await SendAsync(HttpMethod.Post, "/api/yarp/ipblocking/rules", JsonSerializer.Serialize(new { value }));
        Assert.True(api.Response!.IsSuccessStatusCode, $"failed to add rule: {(int)api.Response.StatusCode}");

        await SendAsync(HttpMethod.Put, "/api/yarp/ipblocking/settings", JsonSerializer.Serialize(new { trustForwardedFor = true }));
        Assert.True(api.Response!.IsSuccessStatusCode, $"failed to set trust: {(int)api.Response.StatusCode}");
    }

    // ---- when ----

    [When("I add the IP blocking rule {string}")]
    public async Task WhenAddRule(string value) =>
        await SendAsync(HttpMethod.Post, "/api/yarp/ipblocking/rules", JsonSerializer.Serialize(new { value }));

    [When("I remove the IP blocking rule {string}")]
    public async Task WhenRemoveRule(string value)
    {
        var id = await FindRuleIdAsync(value);
        Assert.NotNull(id);
        await SendAsync(HttpMethod.Delete, $"/api/yarp/ipblocking/rules/{id}");
    }

    [When("I remove an unknown IP blocking rule")]
    public async Task WhenRemoveUnknownRule() =>
        await SendAsync(HttpMethod.Delete, "/api/yarp/ipblocking/rules/does-not-exist");

    [When("I set the IP blocking forwarded-for setting to {string}")]
    public async Task WhenSetForwardedForTrust(string enabled) =>
        await SendAsync(HttpMethod.Put, "/api/yarp/ipblocking/settings",
            JsonSerializer.Serialize(new { trustForwardedFor = bool.Parse(enabled) }));

    [When("I check the address {string}")]
    public async Task WhenCheckAddress(string address) =>
        await SendAsync(HttpMethod.Post, "/api/yarp/ipblocking/check", JsonSerializer.Serialize(new { ip = address }));

    [When("I request {string} with X-Forwarded-For {string}")]
    public async Task WhenRequestWithForwardedFor(string path, string forwardedFor) =>
        await SendAsync(HttpMethod.Get, path, headers: new Dictionary<string, string> { ["X-Forwarded-For"] = forwardedFor });

    [When("the app is restarted")]
    public async Task WhenRestarted()
    {
        api.App!.Dispose();
        api.App = await TestWebApp.CreateAsync(root: ip.Root);
        var response = await api.App.SignInAsync("admin", "correct-password");
        Assert.True(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod,
            $"sign-in after restart failed: {(int)response.StatusCode}");
    }

    // ---- then ----

    [Then("the response json ip blocking rules count is {int}")]
    public void ThenRulesCount(int count) =>
        Assert.Equal(count, Json.Property(api.JsonRoot, "rules").EnumerateArray().Count());

    [Then("the ip blocking rules are")]
    public void ThenRulesAre(Table table)
    {
        var rules = Json.Property(api.JsonRoot, "rules").EnumerateArray().ToList();
        Assert.Equal(table.Rows.Count, rules.Count);
        for (var i = 0; i < table.Rows.Count; i++)
        {
            Assert.Equal(table.Rows[i]["Value"], Json.PropertyString(rules[i], "value"));
            if (table.Rows[i].ContainsKey("Kind"))
            {
                Assert.Equal(table.Rows[i]["Kind"], Json.PropertyString(rules[i], "kind"));
            }
        }
    }

    [Then("the response json trust forwarded for is {string}")]
    public void ThenTrustForwardedFor(string value) =>
        Assert.Equal(bool.Parse(value), Json.Property(Json.Property(api.JsonRoot, "settings"), "trustForwardedFor").GetBoolean());

    [Then("the block list file exists")]
    public void ThenBlockListFileExists() =>
        Assert.True(File.Exists(BlockListPath), $"expected {BlockListPath} to exist");

    [Then("the persisted block list file says trust forwarded for is {string}")]
    public void ThenPersistedTrustForwardedFor(string value)
    {
        var root = JsonNode.Parse(File.ReadAllText(BlockListPath))!.AsObject();
        Assert.Equal(bool.Parse(value), (bool)root["settings"]!["trustForwardedFor"]!);
    }

    [Then("the response json says the address is blocked by {string}")]
    public void ThenCheckBlocked(string value)
    {
        Assert.True(Json.Property(api.JsonRoot, "blocked").GetBoolean());
        Assert.Equal(value, Json.PropertyString(api.JsonRoot, "value"));
    }

    [Then("the response json says the address is not blocked")]
    public void ThenCheckAllowed() =>
        Assert.False(Json.Property(api.JsonRoot, "blocked").GetBoolean());

    [Then("the blocked request was logged with status {int} and client IP {string}")]
    public async Task ThenBlockedRequestWasLogged(int status, string clientIp)
    {
        var store = api.App!.App.Services.GetRequiredService<SqliteRequestLogStore>();
        await store.FlushPendingAsync(CancellationToken.None);
        Assert.Contains(store.GetAfter(0), e =>
            e.StatusCode == status &&
            e.ClientIp == clientIp &&
            e.Path == "/some/proxied/path" &&
            e.Error?.Contains("Blocked by IP rule", StringComparison.OrdinalIgnoreCase) == true);
    }

    // ---- helpers ----

    private string BlockListPath =>
        Path.Combine(api.App!.DataDirectory, IpBlockListService.BlockListFileName);

    private async Task SendAsync(HttpMethod method, string path, string? json = null, Dictionary<string, string>? headers = null)
    {
        api.Response = await api.App!.SendAsync(method, path, json: json, headers: headers);
        api.Body = api.Response.Content.Headers.ContentType is not null
            ? await api.Response.Content.ReadAsStringAsync()
            : null;
        api.Json = null;
        if (api.Body is not null && api.Body.TrimStart().StartsWith('{'))
        {
            api.Json = JsonDocument.Parse(api.Body);
        }
    }

    private async Task<string?> FindRuleIdAsync(string value)
    {
        var response = await api.App!.SendAsync(HttpMethod.Get, "/api/yarp/ipblocking");
        var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        foreach (var rule in doc.RootElement.GetProperty("rules").EnumerateArray())
        {
            if (rule.GetProperty("value").GetString() == value)
            {
                return rule.GetProperty("id").GetString();
            }
        }

        return null;
    }
}
