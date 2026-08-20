using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Reqnroll;
using YARPASUI.Tests.Support;
using YARPUI.Services;

namespace YARPASUI.Tests.StepDefinitions;

/// <summary>HTTP-level steps against the management API (/api/yarp/*) on the in-memory test server.</summary>
[Binding]
internal sealed class ApiSteps(ApiTestContext ctx)
{
    private const string Username = "admin";
    private const string Password = "correct-password";

    // ---- given ----

    [Given("a running standalone YARP UI app configured with")]
    public async Task GivenRunningApp(string appsettingsJson)
    {
        ctx.App = await TestWebApp.CreateAsync(app =>
        {
            // Forward slashes keep the JSON valid on Windows; Path APIs normalize them.
            var dataDirectory = app.DataDirectory.Replace('\\', '/');
            File.WriteAllText(app.SeedPath, appsettingsJson.Replace(TestWebApp.DataDirToken, dataDirectory));
        });
    }

    [Given("I am signed in to the UI")]
    public async Task GivenSignedIn()
    {
        var response = await ctx.App!.SignInAsync(Username, Password);
        Assert.True(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod,
            $"sign-in failed: {(int)response.StatusCode}");
    }

    [Given("the proxy has logged these requests")]
    public async Task GivenProxyLogged(Table table)
    {
        var store = ctx.App!.App.Services.GetRequiredService<SqliteRequestLogStore>();
        foreach (var row in table.Rows)
        {
            store.Add(
                row["Method"],
                row["Path"],
                row["Status"] is "-" or "" ? null : int.Parse(row["Status"]),
                double.Parse(row["DurationMs"]),
                row["RouteId"] is "-" or "" ? null : row["RouteId"],
                clusterId: null,
                destinationId: null,
                destinationAddress: null,
                error: null);
        }

        await store.FlushPendingAsync(CancellationToken.None);
    }

    [Given("an entry from {int} days ago was written directly to the log database")]
    public void GivenOldDatabaseEntry(int daysAgo)
    {
        var store = ctx.App!.App.Services.GetRequiredService<SqliteRequestLogStore>();
        store.Add("GET", "/ancient", 200, 50, "ancientRoute", null, null, null, null);
        // Add() stamps "now"; age it by rewriting the timestamp after flushing.
        store.FlushPendingAsync(CancellationToken.None).GetAwaiter().GetResult();
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={ctx.App.DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE request_logs SET timestamp_ms = @ts WHERE route_id = 'ancientRoute'";
            command.Parameters.AddWithValue("@ts", DateTimeOffset.UtcNow.AddDays(-daysAgo).ToUnixTimeMilliseconds());
            command.ExecuteNonQuery();
        }
    }

    // ---- when ----

    [When("I GET {string}")]
    public async Task WhenGetAsync(string path)
    {
        await SendAsync(HttpMethod.Get, path);
    }

    [When("I POST {string}")]
    public async Task WhenPostAsync(string path)
    {
        await SendAsync(HttpMethod.Post, path);
    }

    [When("I PUT {string} with json")]
    public async Task WhenPutJsonAsync(string path, string json)
    {
        ctx.Response = await ctx.App!.SendAsync(HttpMethod.Put, path, json: json);
        await CaptureBodyAsync();
    }

    [When("I PUT {string} with invalid json")]
    public async Task WhenPutInvalidJsonAsync(string path)
    {
        await WhenPutJsonAsync(path, "{ this is not valid json !!!");
    }

    [When("I PUT {string} with a null json body")]
    public async Task WhenPutNullJsonAsync(string path)
    {
        await WhenPutJsonAsync(path, "null");
    }

    [When("I DELETE {string}")]
    public async Task WhenDeleteAsync(string path)
    {
        await SendAsync(HttpMethod.Delete, path);
    }

    // ---- then: response ----

    [Then("the response status is {int}")]
    public void ThenStatus(int status)
    {
        Assert.NotNull(ctx.Response);
        Assert.Equal(status, (int)ctx.Response!.StatusCode);
    }

    [Then("the response redirects to {string}")]
    public void ThenRedirectsTo(string location)
    {
        Assert.NotNull(ctx.Response);
        var actual = ctx.Response!.Headers.Location;
        Assert.NotNull(actual);
        // Auth redirects come back as absolute URIs; direct redirect results stay relative.
        Assert.Equal(location, actual!.IsAbsoluteUri ? actual.PathAndQuery : actual.ToString());
    }

    [Then("the response redirects to the login page")]
    public void ThenRedirectsToLogin()
    {
        Assert.NotNull(ctx.Response);
        Assert.Equal(HttpStatusCode.Redirect, ctx.Response!.StatusCode);
        var actual = ctx.Response.Headers.Location;
        Assert.NotNull(actual);
        var target = actual!.IsAbsoluteUri ? actual.PathAndQuery : actual.ToString();
        Assert.StartsWith("/login", target, StringComparison.OrdinalIgnoreCase);
    }

    [Then("the response stays inside the site")]
    public void ThenStaysInsideSite()
    {
        Assert.NotNull(ctx.Response);
        var location = ctx.Response!.Headers.Location?.ToString();
        Assert.True(location is not null && location.StartsWith("/", StringComparison.Ordinal) && !location.StartsWith("//", StringComparison.Ordinal),
            $"redirect left the site: {location}");
    }

    [Then("the response json routes are")]
    public void ThenJsonRoutes(Table table)
    {
        var routes = Json.Property(ctx.JsonRoot, "Routes").EnumerateArray().ToList();
        Assert.Equal(table.Rows.Count, routes.Count);
        foreach (var row in table.Rows)
        {
            Assert.Contains(routes, r =>
                string.Equals(Json.PropertyString(r, "RouteId"), row["RouteId"], StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Json.PropertyString(r, "ClusterId"), row["ClusterId"], StringComparison.OrdinalIgnoreCase));
        }
    }

    [Then("the response json editable route ids are")]
    public void ThenJsonEditableRoutes(Table table)
    {
        var editable = Json.Property(ctx.JsonRoot, "EditableRouteIds").EnumerateArray()
            .Select(v => v.GetString()).ToList();
        Assert.Equal(table.Rows.Select(r => r["RouteId"]).ToList(), editable);
    }

    [Then("the response json attach mode is {string}")]
    public void ThenJsonAttachMode(string value)
    {
        Assert.Equal(bool.Parse(value), Json.Property(ctx.JsonRoot, "AttachMode").GetBoolean());
    }

    [Then("the response json managed by UI flag is {string}")]
    public void ThenJsonManagedByUi(string value)
    {
        Assert.Equal(bool.Parse(value), Json.Property(ctx.JsonRoot, "ManagedByUi").GetBoolean());
    }

    [Then("the response json errors include {string}")]
    public void ThenJsonErrorsInclude(string fragment)
    {
        var errors = Json.Property(ctx.JsonRoot, "errors").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains(errors, e => e?.Contains(fragment, StringComparison.OrdinalIgnoreCase) == true);
    }

    [Then("the response json entries are")]
    public void ThenJsonEntries(Table table)
    {
        var entries = Json.Property(ctx.JsonRoot, "entries").EnumerateArray().ToList();
        Assert.Equal(table.Rows.Count, entries.Count);
        for (var i = 0; i < table.Rows.Count; i++)
        {
            Assert.Equal(table.Rows[i]["Method"], Json.PropertyString(entries[i], "Method"));
            Assert.Equal(table.Rows[i]["Path"], Json.PropertyString(entries[i], "Path"));
            Assert.Equal(table.Rows[i]["Status"], Json.PropertyString(entries[i], "StatusCode"));
        }
    }

    [Then("the response json entries count is {int}")]
    public void ThenJsonEntriesCount(int count)
    {
        Assert.Equal(count, Json.Property(ctx.JsonRoot, "entries").EnumerateArray().Count());
    }

    [Then("the response json stats count is {int}")]
    public void ThenJsonStatsCount(int count)
    {
        var summary = Json.Property(ctx.JsonRoot, "Summary");
        Assert.Equal(count, Json.Property(summary, "Count").GetInt64());
    }

    [Then("the response json retention days is {int}")]
    public void ThenJsonRetentionDays(int days)
    {
        Assert.Equal(days, Json.Property(ctx.JsonRoot, "retentionDays").GetInt32());
    }

    [Then("the persisted UI config file contains routes")]
    public void ThenPersistedUiConfigRoutes(Table table)
    {
        var overlay = JsonNode.Parse(File.ReadAllText(ctx.App!.UiConfigPath))!.AsObject();
        var routes = overlay["Routes"]!.AsObject();
        foreach (var row in table.Rows)
        {
            Assert.True(routes.ContainsKey(row["RouteId"]), $"UI config routes: {string.Join(", ", routes.Select(r => r.Key))}");
        }
    }

    [Then("the persisted UI config file does not exist")]
    public void ThenPersistedUiConfigMissing()
    {
        Assert.False(File.Exists(ctx.App!.UiConfigPath));
    }

    // ---- helpers ----

    private async Task SendAsync(HttpMethod method, string path)
    {
        ctx.Response = await ctx.App!.SendAsync(method, path);
        await CaptureBodyAsync();
    }

    private async Task CaptureBodyAsync()
    {
        ctx.Body = ctx.Response!.Content.Headers.ContentType is not null
            ? await ctx.Response.Content.ReadAsStringAsync()
            : null;
        ctx.Json = null;
        if (ctx.Body is not null && ctx.Body.TrimStart().StartsWith('{'))
        {
            ctx.Json = JsonDocument.Parse(ctx.Body);
        }
    }
}
