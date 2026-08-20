using System.Text.Json.Nodes;
using Reqnroll;
using YARPASUI.Tests.Support;
using Yarp.ReverseProxy.Configuration;
using YARPUI.Services;

namespace YARPASUI.Tests.StepDefinitions;

/// <summary>Steps around ProxyConfigService in attach mode: origin tracking, appsettings write-back, backups, restore.</summary>
[Binding]
internal sealed class AttachModeSteps(ProxyConfigTestContext ctx)
{
    private string BaseAppSettingsPath => Path.Combine(ctx.ContentRoot, "appsettings.json");

    // ---- given ----

    [Given("an attach-mode proxy configuration service with this appsettings.json")]
    public void GivenAttachService(string appsettingsJson)
    {
        ctx.EnsureDirectories();
        File.WriteAllText(BaseAppSettingsPath, appsettingsJson);
        ctx.OriginalAppSettings = appsettingsJson;
        ctx.CreateService(attachMode: true);
    }

    [Given("the live proxy also serves route {string} pointing at cluster {string} from a custom provider")]
    public void GivenCustomProviderRoute(string routeId, string clusterId)
    {
        var lookup = ctx.StateLookup!;
        lookup.CustomRoutes.Add(new RouteConfig
        {
            RouteId = routeId,
            ClusterId = clusterId,
            Match = new RouteMatch { Path = "/" + routeId + "/{**catch-all}" },
        });
        lookup.CustomClusters.Add(new ClusterConfig
        {
            ClusterId = clusterId,
            Destinations = new Dictionary<string, DestinationConfig>
            {
                ["only"] = new() { Address = $"https://{clusterId}.example.com" },
            },
        });
    }

    [Given("a legacy yarp-ui.routes.json overlay with this content")]
    public void GivenLegacyOverlay(string overlayJson)
    {
        File.WriteAllText(ctx.UiConfigPath, overlayJson);
    }

    // ---- then: editability ----

    [Then("route {string} is editable")]
    public void ThenRouteEditable(string routeId)
    {
        Assert.NotNull(ctx.Live);
        Assert.Contains(routeId, ctx.Live.EditableRouteIds);
    }

    [Then("route {string} is read-only")]
    public void ThenRouteReadOnly(string routeId)
    {
        Assert.NotNull(ctx.Live);
        Assert.DoesNotContain(routeId, ctx.Live.EditableRouteIds);
    }

    [Then("cluster {string} is editable")]
    public void ThenClusterEditable(string clusterId)
    {
        Assert.NotNull(ctx.Live);
        Assert.Contains(clusterId, ctx.Live.EditableClusterIds);
    }

    // ---- then: written-back files ----

    [Then("appsettings.json in the content root contains route {string} pointing at cluster {string}")]
    public void ThenFileContainsRoute(string routeId, string clusterId)
    {
        var route = ReadSection()["Routes"]?[routeId];
        Assert.NotNull(route);
        Assert.Equal(clusterId, route!["ClusterId"]!.GetValue<string>());
    }

    [Then("appsettings.json in the content root does not contain route {string}")]
    public void ThenFileDoesNotContainRoute(string routeId)
    {
        var routes = ReadSection()["Routes"];
        Assert.True(routes is null || routes[routeId] is null);
    }

    [Then("appsettings.json in the content root has cluster {string} with destination {string} at address {string}")]
    public void ThenFileHasClusterDestination(string clusterId, string destinationId, string address)
    {
        var destination = ReadSection()["Clusters"]?[clusterId]?["Destinations"]?[destinationId];
        Assert.NotNull(destination);
        Assert.Equal(address, destination!["Address"]!.GetValue<string>());
    }

    [Then("unrelated settings in appsettings.json are preserved")]
    public void ThenUnrelatedSettingsPreserved()
    {
        Assert.NotNull(ReadRoot()["Logging"]);
    }

    [Then("an appsettings.json.yarpui.bak backup exists")]
    public void ThenBackupExists()
    {
        Assert.True(File.Exists(BaseAppSettingsPath + ProxyConfigService.BackupSuffix));
    }

    [Then("appsettings.json is restored to its original content")]
    public void ThenFileRestored()
    {
        var original = JsonNode.Parse(ctx.OriginalAppSettings!);
        var current = JsonNode.Parse(File.ReadAllText(BaseAppSettingsPath));
        Assert.True(JsonNode.DeepEquals(original, current),
            "restored appsettings.json differs from the original:\n" + File.ReadAllText(BaseAppSettingsPath));
    }

    [Then("the overlay no longer defines route {string}")]
    public void ThenOverlayNoLongerDefines(string routeId)
    {
        if (!File.Exists(ctx.UiConfigPath))
        {
            return;
        }

        var overlay = JsonNode.Parse(File.ReadAllText(ctx.UiConfigPath));
        Assert.True(overlay?["Routes"]?[routeId] is null);
    }

    [Then("the overlay file is removed")]
    public void ThenOverlayRemoved()
    {
        Assert.False(File.Exists(ctx.UiConfigPath));
    }

    private JsonObject ReadRoot() => JsonNode.Parse(File.ReadAllText(BaseAppSettingsPath))!.AsObject();

    private JsonObject ReadSection() => ReadRoot()["ReverseProxy"]!.AsObject();
}
