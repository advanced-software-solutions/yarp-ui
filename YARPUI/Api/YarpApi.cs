using System.Text.Json;
using Yarp.ReverseProxy.Configuration;
using YARPUI.Services;

namespace YARPUI.Api;

public sealed record ConfigResponse(
    IReadOnlyList<RouteConfig> Routes,
    IReadOnlyList<ClusterConfig> Clusters,
    IReadOnlyList<string> EditableRouteIds,
    IReadOnlyList<string> EditableClusterIds,
    bool AttachMode,
    bool ManagedByUi);

public sealed class ConfigUpdateRequest
{
    public IReadOnlyList<RouteConfig>? Routes { get; init; }
    public IReadOnlyList<ClusterConfig>? Clusters { get; init; }
}

public sealed record LogSettingsUpdateRequest(int? RetentionDays);

public static class YarpApi
{
    // Config payloads keep the PascalCase shape used by appsettings.json and yarp-ui.routes.json.
    private static readonly JsonSerializerOptions ConfigJsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static IEndpointRouteBuilder MapYarpApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/yarp").RequireAuthorization(YarpUiDefaults.Policy);

        group.MapGet("/config", (ProxyConfigService configService) =>
        {
            return Results.Json(ToResponse(configService), ConfigJsonOptions);
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

            return Results.Json(ToResponse(configService), ConfigJsonOptions);
        });

        group.MapPost("/config/reset", async (ProxyConfigService configService) =>
        {
            var result = await configService.ResetAsync();
            if (!result.Success)
            {
                return Results.BadRequest(new { errors = result.Errors });
            }

            return Results.Json(ToResponse(configService), ConfigJsonOptions);
        });

        // Live tailing (only `after`) streams new entries oldest-first, as the Logs page polls it.
        // Any search parameter switches to a history query: newest first by default, filterable by
        // time range and route/cluster/destination.
        group.MapGet("/logs", (
            SqliteRequestLogStore store,
            long? after,
            long? from,
            long? to,
            string? routeId,
            string? clusterId,
            string? destinationId,
            string? sort,
            bool? desc,
            int? limit) =>
        {
            var search =
                from is not null || to is not null
                || !string.IsNullOrEmpty(routeId) || !string.IsNullOrEmpty(clusterId) || !string.IsNullOrEmpty(destinationId)
                || sort is not null || desc is not null || limit is not null;
            if (!search)
            {
                return Results.Json(new { entries = store.GetAfter(after ?? 0) });
            }

            if (sort is not null && !SqliteRequestLogStore.IsValidSortField(sort))
            {
                return Results.BadRequest(new { errors = new[] { $"sort must be one of: {SqliteRequestLogStore.SortFields}." } });
            }

            if (limit is < 1 or > SqliteRequestLogStore.MaxQueryLimit)
            {
                return Results.BadRequest(new { errors = new[] { $"limit must be between 1 and {SqliteRequestLogStore.MaxQueryLimit}." } });
            }

            var result = store.Query(new RequestLogQuery
            {
                FromMs = from,
                ToMs = to,
                RouteId = routeId,
                ClusterId = clusterId,
                DestinationId = destinationId,
                Sort = sort ?? "timestamp",
                Descending = desc ?? true,
                Limit = limit ?? 500,
            });
            return Results.Json(new { entries = result.Entries, total = result.Total });
        });

        group.MapDelete("/logs", (SqliteRequestLogStore store) =>
        {
            store.Clear();
            return Results.NoContent();
        });

        // Aggregates for the Logs performance panel. minutes=0 (or null) covers all time.
        group.MapGet("/logs/stats", (SqliteRequestLogStore store, int? minutes) =>
        {
            TimeSpan? window = minutes is > 0 ? TimeSpan.FromMinutes(minutes.Value) : null;
            return Results.Json(store.GetStats(window));
        });

        group.MapGet("/logs/settings", (SqliteRequestLogStore store) =>
        {
            return Results.Json(new { retentionDays = store.GetRetentionDays() });
        });

        group.MapPut("/logs/settings", async (HttpContext http, SqliteRequestLogStore store) =>
        {
            LogSettingsUpdateRequest? request;
            try
            {
                request = await http.Request.ReadFromJsonAsync<LogSettingsUpdateRequest>();
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { errors = new[] { "The request body is not valid JSON." } });
            }

            if (request?.RetentionDays is null or < 0 or > SqliteRequestLogStore.MaxRetentionDays)
            {
                return Results.BadRequest(new
                {
                    errors = new[]
                    {
                        $"retentionDays must be 0 (keep forever) or between 1 and {SqliteRequestLogStore.MaxRetentionDays}.",
                    },
                });
            }

            store.SetRetentionDays(request.RetentionDays.Value);
            store.ApplyRetention(); // apply the new policy immediately instead of waiting for the hourly pass
            return Results.Json(new { retentionDays = request.RetentionDays.Value });
        });

        return app;
    }

    private static ConfigResponse ToResponse(ProxyConfigService configService)
    {
        var live = configService.GetLiveConfig();
        return new ConfigResponse(
            live.Routes,
            live.Clusters,
            live.EditableRouteIds.ToList(),
            live.EditableClusterIds.ToList(),
            configService.IsAttachMode,
            configService.IsManagedByUi);
    }
}
