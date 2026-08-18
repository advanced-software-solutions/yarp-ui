using System.Text.Json;
using Yarp.ReverseProxy.Configuration;
using YARPUI.Services;

namespace YARPUI.Api;

public sealed record ConfigResponse(
    IReadOnlyList<RouteConfig> Routes,
    IReadOnlyList<ClusterConfig> Clusters,
    bool ManagedByUi);

public sealed class ConfigUpdateRequest
{
    public IReadOnlyList<RouteConfig>? Routes { get; init; }
    public IReadOnlyList<ClusterConfig>? Clusters { get; init; }
}

public static class YarpApi
{
    // Config payloads keep the PascalCase shape used by appsettings.json and yarp-ui.routes.json.
    private static readonly JsonSerializerOptions ConfigJsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static IEndpointRouteBuilder MapYarpApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/yarp").RequireAuthorization();

        group.MapGet("/config", (ProxyConfigService configService) =>
        {
            var current = configService.GetCurrent();
            return Results.Json(new ConfigResponse(current.Routes, current.Clusters, configService.IsManagedByUi), ConfigJsonOptions);
        });

        group.MapPut("/config", async (HttpContext http, ProxyConfigService configService) =>
        {
            ConfigUpdateRequest? request;
            try
            {
                request = await http.Request.ReadFromJsonAsync<ConfigUpdateRequest>();
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { errors = new[] { "The request body is not valid JSON." } });
            }

            if (request is null)
            {
                return Results.BadRequest(new { errors = new[] { "The request body is empty." } });
            }

            var routes = request.Routes ?? Array.Empty<RouteConfig>();
            var clusters = request.Clusters ?? Array.Empty<ClusterConfig>();

            var result = await configService.ApplyAsync(routes, clusters);
            if (!result.Success)
            {
                return Results.BadRequest(new { errors = result.Errors });
            }

            var current = configService.GetCurrent();
            return Results.Json(new ConfigResponse(current.Routes, current.Clusters, configService.IsManagedByUi), ConfigJsonOptions);
        });

        group.MapPost("/config/reset", async (ProxyConfigService configService) =>
        {
            var result = await configService.ResetToSeedAsync();
            if (!result.Success)
            {
                return Results.BadRequest(new { errors = result.Errors });
            }

            var current = configService.GetCurrent();
            return Results.Json(new ConfigResponse(current.Routes, current.Clusters, configService.IsManagedByUi), ConfigJsonOptions);
        });

        group.MapGet("/logs", (RequestLogStore store, long? after) =>
        {
            return Results.Json(new { entries = store.GetAfter(after ?? 0) });
        });

        group.MapDelete("/logs", (RequestLogStore store) =>
        {
            store.Clear();
            return Results.NoContent();
        });

        return app;
    }
}
