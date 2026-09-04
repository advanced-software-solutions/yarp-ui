using System.Text.Json;
using Microsoft.Extensions.Localization;
using Yarp.ReverseProxy.Configuration;
using YARPUI.Resources;
using YARPUI.Services;
using YARPUI.Services.IpBlocking;

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

public sealed record IpBlockRuleRequest(string? Value, string? Note);

public sealed record IpBlockSettingsRequest(bool? TrustForwardedFor);

public sealed record IpBlockCheckRequest(string? Ip);

public sealed record IpBlockRuleResponse(string Id, string Kind, string Value, string? Note, DateTime CreatedAtUtc);

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

        group.MapPut("/config", async (HttpContext http, ProxyConfigService configService, IStringLocalizer<UIStrings> L) =>
        {
            ConfigUpdateRequest? request;
            try
            {
                request = await http.Request.ReadFromJsonAsync<ConfigUpdateRequest>();
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { errors = new[] { L["validation.bodyNotJson"].Value } });
            }

            if (request is null)
            {
                return Results.BadRequest(new { errors = new[] { L["validation.bodyEmpty"].Value } });
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

        // Live tailing (only `after`) streams new entries oldest-first. Any search parameter
        // switches to a paged history query: newest first by default, filterable by time range,
        // route/cluster/destination, free text and status class, paged with limit + offset.
        group.MapGet("/logs", (
            SqliteRequestLogStore store,
            IStringLocalizer<UIStrings> L,
            long? after,
            long? from,
            long? to,
            string? routeId,
            string? clusterId,
            string? destinationId,
            string? q,
            int? status,
            string? sort,
            bool? desc,
            int? limit,
            int? offset) =>
        {
            var search =
                from is not null || to is not null
                || !string.IsNullOrEmpty(routeId) || !string.IsNullOrEmpty(clusterId) || !string.IsNullOrEmpty(destinationId)
                || !string.IsNullOrEmpty(q) || status is not null
                || sort is not null || desc is not null || limit is not null || offset is not null;
            if (!search)
            {
                return Results.Json(new { entries = store.GetAfter(after ?? 0) });
            }

            if (sort is not null && !SqliteRequestLogStore.IsValidSortField(sort))
            {
                return Results.BadRequest(new { errors = new[] { L["validation.sortField", SqliteRequestLogStore.SortFields].Value } });
            }

            if (limit is < 1 or > SqliteRequestLogStore.MaxQueryLimit)
            {
                return Results.BadRequest(new { errors = new[] { L["validation.limitRange", SqliteRequestLogStore.MaxQueryLimit].Value } });
            }

            if (offset is < 0)
            {
                return Results.BadRequest(new { errors = new[] { L["validation.offsetRange"].Value } });
            }

            if (status is < 2 or > 5)
            {
                return Results.BadRequest(new { errors = new[] { L["validation.statusRange"].Value } });
            }

            var result = store.Query(new RequestLogQuery
            {
                FromMs = from,
                ToMs = to,
                RouteId = routeId,
                ClusterId = clusterId,
                DestinationId = destinationId,
                Search = q,
                StatusClass = status,
                Sort = sort ?? "timestamp",
                Descending = desc ?? true,
                Limit = limit ?? 500,
                Offset = offset ?? 0,
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

        group.MapPut("/logs/settings", async (HttpContext http, SqliteRequestLogStore store, IStringLocalizer<UIStrings> L) =>
        {
            LogSettingsUpdateRequest? request;
            try
            {
                request = await http.Request.ReadFromJsonAsync<LogSettingsUpdateRequest>();
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { errors = new[] { L["validation.bodyNotJson"].Value } });
            }

            if (request?.RetentionDays is null or < 0 or > SqliteRequestLogStore.MaxRetentionDays)
            {
                return Results.BadRequest(new
                {
                    errors = new[]
                    {
                        L["validation.retentionRange", SqliteRequestLogStore.MaxRetentionDays].Value,
                    },
                });
            }

            store.SetRetentionDays(request.RetentionDays.Value);
            store.ApplyRetention(); // apply the new policy immediately instead of waiting for the hourly pass
            return Results.Json(new { retentionDays = request.RetentionDays.Value });
        });

        // ---- IP blocking (camelCase payloads like the log endpoints) ----

        group.MapGet("/ipblocking", (IpBlockListService blockList) =>
        {
            return Results.Json(new
            {
                rules = blockList.Rules.Select(ToIpBlockRuleResponse).ToList(),
                settings = new { trustForwardedFor = blockList.TrustForwardedFor },
            });
        });

        group.MapPost("/ipblocking/rules", async (HttpContext http, IpBlockListService blockList, IStringLocalizer<UIStrings> L) =>
        {
            IpBlockRuleRequest? request;
            try
            {
                request = await http.Request.ReadFromJsonAsync<IpBlockRuleRequest>();
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { errors = new[] { L["validation.bodyNotJson"].Value } });
            }

            if (request is null)
            {
                return Results.BadRequest(new { errors = new[] { L["validation.bodyEmpty"].Value } });
            }

            var result = blockList.AddRule(request.Value, request.Note);
            if (!result.Success)
            {
                return Results.BadRequest(new { errors = result.Errors });
            }

            return Results.Json(new { rule = ToIpBlockRuleResponse(result.Rule!) });
        });

        group.MapDelete("/ipblocking/rules/{id}", (string id, IpBlockListService blockList) =>
        {
            return blockList.RemoveRule(id) ? Results.NoContent() : Results.NotFound();
        });

        group.MapPut("/ipblocking/settings", async (HttpContext http, IpBlockListService blockList, IStringLocalizer<UIStrings> L) =>
        {
            IpBlockSettingsRequest? request;
            try
            {
                request = await http.Request.ReadFromJsonAsync<IpBlockSettingsRequest>();
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { errors = new[] { L["validation.bodyNotJson"].Value } });
            }

            if (request?.TrustForwardedFor is null)
            {
                return Results.BadRequest(new { errors = new[] { L["validation.trustForwardedForRequired"].Value } });
            }

            blockList.SetTrustForwardedFor(request.TrustForwardedFor.Value);
            return Results.Json(new { trustForwardedFor = request.TrustForwardedFor.Value });
        });

        // Which rule (if any) a plain address would hit — powers the page's "test an address" box.
        group.MapPost("/ipblocking/check", async (HttpContext http, IpBlockListService blockList, IStringLocalizer<UIStrings> L) =>
        {
            IpBlockCheckRequest? request;
            try
            {
                request = await http.Request.ReadFromJsonAsync<IpBlockCheckRequest>();
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { errors = new[] { L["validation.bodyNotJson"].Value } });
            }

            if (request is null)
            {
                return Results.BadRequest(new { errors = new[] { L["validation.bodyEmpty"].Value } });
            }

            var result = blockList.CheckAddress(request.Ip);
            if (!result.Valid)
            {
                return Results.BadRequest(new { errors = new[] { result.Error } });
            }

            return Results.Json(new { blocked = result.Blocked, value = result.Rule?.Value });
        });

        return app;
    }

    private static IpBlockRuleResponse ToIpBlockRuleResponse(IpBlockRule rule) =>
        new(rule.Id, rule.Kind.ToString().ToLowerInvariant(), rule.Value, rule.Note, rule.CreatedAtUtc);

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
