using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Reqnroll;
using YARPASUI.Tests.Support;
using Yarp.ReverseProxy.Configuration;
using YARPUI.Api;
using YARPUI.Services;

namespace YARPASUI.Tests.StepDefinitions;

/// <summary>Steps around ProxyConfigService in standalone mode: seed loading, overlay, validation, persistence, reset.</summary>
[Binding]
internal sealed class ProxyConfigSteps(ProxyConfigTestContext ctx)
{
    // ---- given ----

    [Given("a standalone proxy configuration service")]
    public void GivenStandaloneService()
    {
        ctx.CreateService(attachMode: false);
    }

    [Given("an appsettings.json with this ReverseProxy section")]
    public void GivenAppSettingsSeed(string reverseProxySection)
    {
        ctx.EnsureDirectories();
        WriteSeedFile(ctx.ContentRoot, "appsettings.json", reverseProxySection);
    }

    [Given("an appsettings.Testing.json that changes the seed like this")]
    public void GivenEnvironmentOverride(string reverseProxySection)
    {
        WriteSeedFile(ctx.ContentRoot, "appsettings.Testing.json", reverseProxySection);
    }

    [Given("a data-directory appsettings.json with this ReverseProxy section")]
    public void GivenDataDirectoryOverride(string reverseProxySection)
    {
        WriteSeedFile(ctx.DataDirectory, "appsettings.json", reverseProxySection);
    }

    [Given("a yarp-ui.routes.json overlay with this content")]
    public void GivenOverlay(string overlayJson)
    {
        ctx.EnsureDirectories();
        File.WriteAllText(ctx.UiConfigPath, overlayJson);
    }

    [Given("a corrupt yarp-ui.routes.json file")]
    public void GivenCorruptOverlay()
    {
        ctx.EnsureDirectories();
        File.WriteAllText(ctx.UiConfigPath, "{ this is not valid json !!!");
    }

    [Given("the proxy validator rejects route {string} with {string}")]
    public void GivenValidatorRejectsRoute(string routeId, string message)
    {
        ctx.Validator.RouteFailures.Add((routeId, message));
    }

    [Given("the live proxy already runs route {string} pointing at the missing cluster {string}")]
    public void GivenDanglingLiveRoute(string routeId, string clusterId)
    {
        // Append to whatever the provider already runs (the seed), like an extra config source would.
        var current = ctx.Provider.GetConfig();
        ctx.Provider.Update(
            current.Routes.Append(new RouteConfig { RouteId = routeId, ClusterId = clusterId, Match = new RouteMatch { Path = "/" + routeId } }).ToList(),
            current.Clusters.ToList());
    }

    [Given("the configuration setting YarpUi:DataDirectory is {string}")]
    public void GivenDataDirectorySetting(string setting)
    {
        ctx.EnsureDirectories();
        ctx.DataDirectorySetting = setting;
    }

    // ---- when ----

    [When("the initial configuration is loaded")]
    public void WhenInitialConfigLoaded()
    {
        ctx.Loaded = ProxyConfigService.LoadInitial(ctx.DataDirectory, ctx.ContentRoot, ProxyConfigTestContext.EnvironmentName);
        // Mirrors AddYarpUi: the loaded document feeds the in-memory provider YARP runs on.
        ctx.Provider.Update(ctx.Loaded.Routes, ctx.Loaded.Clusters);
    }

    [Given("the live configuration is read")]
    [When("the live configuration is read")]
    public void WhenLiveConfigRead()
    {
        ctx.Live = ctx.Service!.GetLiveConfig();
    }

    [Given("I apply this configuration")]
    [When("I apply this configuration")]
    public async Task WhenApplyAsync(string json)
    {
        var request = JsonSerializer.Deserialize<ConfigUpdateRequest>(json, Json.Insensitive) ?? new ConfigUpdateRequest();
        ctx.Result = await ctx.Service!.ApplyAsync(request.Routes ?? [], request.Clusters ?? []);
        if (ctx.Result.Success)
        {
            ctx.Live = ctx.Service.GetLiveConfig();
        }
    }

    [When("I reset the configuration")]
    public async Task WhenResetAsync()
    {
        ctx.Result = await ctx.Service!.ResetAsync();
        if (ctx.Result.Success)
        {
            ctx.Live = ctx.Service.GetLiveConfig();
        }
    }

    [When("the data directory is resolved")]
    public void WhenDataDirectoryResolved()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["YarpUi:DataDirectory"] = ctx.DataDirectorySetting })
            .Build();
        ctx.ResolvedDataDirectory = ProxyConfigService.ResolveDataDirectory(configuration, ctx.ContentRoot);
    }

    // ---- then: contents ----

    [Then("the configuration contains routes")]
    public void ThenContainsRoutes(Table table)
    {
        foreach (var row in table.Rows)
        {
            var route = ctx.CurrentRoutes.FirstOrDefault(r => string.Equals(r.RouteId, row["RouteId"], StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(route);
            Assert.Equal(row["ClusterId"], route.ClusterId);
        }
    }

    [Then("the configuration does not contain route {string}")]
    public void ThenDoesNotContainRoute(string routeId)
    {
        Assert.DoesNotContain(ctx.CurrentRoutes, r => string.Equals(r.RouteId, routeId, StringComparison.OrdinalIgnoreCase));
    }

    [Then("the configuration contains clusters")]
    public void ThenContainsClusters(Table table)
    {
        foreach (var row in table.Rows)
        {
            var cluster = ctx.CurrentClusters.FirstOrDefault(c => string.Equals(c.ClusterId, row["ClusterId"], StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(cluster);
            var expectedDestinations = row["Destinations"].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Order().ToList();
            var actualDestinations = (cluster.Destinations?.Keys ?? []).Order().ToList();
            Assert.Equal(expectedDestinations, actualDestinations);
        }
    }

    [Then("the configuration does not contain cluster {string}")]
    public void ThenDoesNotContainCluster(string clusterId)
    {
        Assert.DoesNotContain(ctx.CurrentClusters, c => string.Equals(c.ClusterId, clusterId, StringComparison.OrdinalIgnoreCase));
    }

    [Then("cluster {string} has destination {string} at address {string}")]
    public void ThenClusterDestinationAddress(string clusterId, string destinationId, string address)
    {
        var cluster = ctx.CurrentClusters.FirstOrDefault(c => string.Equals(c.ClusterId, clusterId, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(cluster);
        var destination = cluster.Destinations!.GetValueOrDefault(destinationId);
        Assert.NotNull(destination);
        Assert.Equal(address, destination.Address);
    }

    [Then("every route and cluster is editable")]
    public void ThenEverythingEditable()
    {
        Assert.NotNull(ctx.Live);
        foreach (var route in ctx.Live.Routes)
        {
            Assert.Contains(route.RouteId, ctx.Live.EditableRouteIds);
        }

        foreach (var cluster in ctx.Live.Clusters)
        {
            Assert.Contains(cluster.ClusterId, ctx.Live.EditableClusterIds);
        }
    }

    [Then("the UI does not manage the configuration yet")]
    public void ThenNotManaged()
    {
        Assert.False(ctx.Service!.IsManagedByUi);
    }

    [Then("the UI manages the configuration")]
    public void ThenManaged()
    {
        Assert.True(ctx.Service!.IsManagedByUi);
    }

    // ---- then: results ----

    [Then("the apply succeeds")]
    public void ThenApplySucceeds()
    {
        Assert.NotNull(ctx.Result);
        Assert.True(ctx.Result.Success, string.Join("; ", ctx.Result.Errors));
    }

    [Then("the apply fails with an error containing {string}")]
    public void ThenApplyFailsWith(string fragment)
    {
        Assert.NotNull(ctx.Result);
        Assert.False(ctx.Result.Success);
        Assert.Contains(ctx.Result.Errors, e => e.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    [Then("the reset succeeds")]
    public void ThenResetSucceeds()
    {
        Assert.NotNull(ctx.Result);
        Assert.True(ctx.Result.Success, string.Join("; ", ctx.Result.Errors));
    }

    // ---- then: files ----

    [Then("the yarp-ui.routes.json file exists")]
    public void ThenOverlayExists()
    {
        Assert.True(File.Exists(ctx.UiConfigPath), $"expected {ctx.UiConfigPath}");
    }

    [Then("the yarp-ui.routes.json file does not exist")]
    public void ThenOverlayDoesNotExist()
    {
        Assert.False(File.Exists(ctx.UiConfigPath));
    }

    [Then("the yarp-ui.routes.json file contains routes")]
    public void ThenOverlayContainsRoutes(Table table)
    {
        var overlay = JsonNode.Parse(File.ReadAllText(ctx.UiConfigPath))!.AsObject();
        var routes = overlay["Routes"]!.AsObject();
        foreach (var row in table.Rows)
        {
            Assert.True(routes.ContainsKey(row["RouteId"]), $"overlay routes: {string.Join(", ", routes.Select(r => r.Key))}");
        }
    }

    [Then("the resolved data directory is the content root")]
    public void ThenResolvedIsContentRoot()
    {
        Assert.Equal(ctx.ContentRoot, ctx.ResolvedDataDirectory);
    }

    [Then("the resolved data directory is {string} under the content root")]
    public void ThenResolvedIsUnderContentRoot(string relativeName)
    {
        Assert.Equal(Path.GetFullPath(Path.Combine(ctx.ContentRoot, relativeName)), ctx.ResolvedDataDirectory);
    }

    [Then("the resolved data directory is the configured absolute path")]
    public void ThenResolvedIsAbsolute()
    {
        Assert.Equal(Path.GetFullPath(ctx.DataDirectorySetting!), ctx.ResolvedDataDirectory);
    }

    // ---- helpers ----

    private static void WriteSeedFile(string directory, string fileName, string reverseProxySection)
    {
        var root = new JsonObject { ["ReverseProxy"] = JsonNode.Parse(reverseProxySection) };
        File.WriteAllText(Path.Combine(directory, fileName), root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
