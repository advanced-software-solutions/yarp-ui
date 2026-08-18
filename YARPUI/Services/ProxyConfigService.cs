using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Yarp.ReverseProxy.Configuration;

namespace YARPUI.Services;

/// <summary>
/// A set of routes and clusters ready to be applied to the running proxy.
/// The on-disk shape mirrors the appsettings.json "ReverseProxy" section
/// (routes and clusters keyed by id), while this in-memory shape uses lists.
/// </summary>
public sealed class ProxyConfigDocument
{
    public IReadOnlyList<RouteConfig> Routes { get; init; } = Array.Empty<RouteConfig>();
    public IReadOnlyList<ClusterConfig> Clusters { get; init; } = Array.Empty<ClusterConfig>();
}

public sealed record ConfigApplyResult(bool Success, IReadOnlyList<string> Errors);

/// <summary>
/// Owns the proxy configuration lifecycle:
///  - decides the initial source (yarp-ui.routes.json when present, otherwise the appsettings.json seed),
///  - validates and applies UI edits live via InMemoryConfigProvider (no restart),
///  - persists applied edits to yarp-ui.routes.json so they survive restarts.
/// </summary>
public sealed class ProxyConfigService
{
    public const string UiConfigFileName = "yarp-ui.routes.json";
    private const string SeedSectionName = "ReverseProxy";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonDocumentOptions LenientDocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _contentRoot;
    private readonly string _environmentName;
    private readonly InMemoryConfigProvider _provider;
    private readonly IConfigValidator _validator;
    private readonly ILogger<ProxyConfigService> _logger;
    private readonly object _sync = new();

    public ProxyConfigService(
        IHostEnvironment environment,
        InMemoryConfigProvider provider,
        IConfigValidator validator,
        ILogger<ProxyConfigService> logger)
    {
        _contentRoot = environment.ContentRootPath;
        _environmentName = environment.EnvironmentName;
        _provider = provider;
        _validator = validator;
        _logger = logger;
    }

    public string UiConfigPath => Path.Combine(_contentRoot, UiConfigFileName);

    /// <summary>True when the UI-managed file exists and therefore overrides appsettings.json.</summary>
    public bool IsManagedByUi => File.Exists(UiConfigPath);

    public IProxyConfig GetCurrent() => _provider.GetConfig();

    /// <summary>
    /// The configuration used at startup, before DI is available.
    /// A corrupt UI file falls back to the appsettings.json seed rather than taking the proxy down.
    /// </summary>
    public static ProxyConfigDocument LoadInitial(string contentRoot, string environmentName)
    {
        var uiPath = Path.Combine(contentRoot, UiConfigFileName);
        if (File.Exists(uiPath))
        {
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(uiPath), documentOptions: LenientDocumentOptions) as JsonObject;
                if (node is not null)
                {
                    return ParseDocument(node);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to read {UiConfigFileName} ({ex.Message}). Falling back to appsettings.json.");
            }
        }

        return LoadSeed(contentRoot, environmentName);
    }

    /// <summary>Reads the seed configuration from appsettings.json (+ optional environment file), merging the "ReverseProxy" sections.</summary>
    public static ProxyConfigDocument LoadSeed(string contentRoot, string environmentName)
    {
        var section = new JsonObject();

        foreach (var fileName in new[] { "appsettings.json", $"appsettings.{environmentName}.json" })
        {
            var path = Path.Combine(contentRoot, fileName);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var root = JsonNode.Parse(File.ReadAllText(path), documentOptions: LenientDocumentOptions) as JsonObject;
                if (root?[SeedSectionName] is JsonObject overrideSection)
                {
                    MergeInto(section, overrideSection);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to read {fileName} ({ex.Message}).");
            }
        }

        return ParseDocument(section);
    }

    /// <summary>Validates, applies live (no restart) and persists the given configuration.</summary>
    public async Task<ConfigApplyResult> ApplyAsync(IReadOnlyList<RouteConfig> routes, IReadOnlyList<ClusterConfig> clusters)
    {
        var errors = await ValidateAsync(routes, clusters);
        if (errors.Count > 0)
        {
            return new ConfigApplyResult(false, errors);
        }

        lock (_sync)
        {
            _provider.Update(routes, clusters);
            Persist(routes, clusters);
        }

        _logger.LogInformation(
            "Proxy configuration updated from the UI: {RouteCount} routes, {ClusterCount} clusters",
            routes.Count, clusters.Count);

        return new ConfigApplyResult(true, Array.Empty<string>());
    }

    /// <summary>Discards UI changes and returns to the appsettings.json seed configuration.</summary>
    public async Task<ConfigApplyResult> ResetToSeedAsync()
    {
        var seed = LoadSeed(_contentRoot, _environmentName);

        var errors = await ValidateAsync(seed.Routes, seed.Clusters);
        if (errors.Count > 0)
        {
            return new ConfigApplyResult(false, errors);
        }

        lock (_sync)
        {
            _provider.Update(seed.Routes, seed.Clusters);
            try
            {
                if (File.Exists(UiConfigPath))
                {
                    File.Delete(UiConfigPath);
                }
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Could not delete {Path}; the seed config is still applied live.", UiConfigPath);
            }
        }

        _logger.LogInformation("Proxy configuration reset to the appsettings.json seed.");
        return new ConfigApplyResult(true, Array.Empty<string>());
    }

    private async Task<List<string>> ValidateAsync(IReadOnlyList<RouteConfig> routes, IReadOnlyList<ClusterConfig> clusters)
    {
        var errors = new List<string>();

        if (routes.GroupBy(r => r.RouteId, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } duplicateRoute)
        {
            errors.Add($"Duplicate route id '{duplicateRoute.Key}'.");
        }

        if (clusters.GroupBy(c => c.ClusterId, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } duplicateCluster)
        {
            errors.Add($"Duplicate cluster id '{duplicateCluster.Key}'.");
        }

        var clusterIds = clusters.Select(c => c.ClusterId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var route in routes)
        {
            if (string.IsNullOrWhiteSpace(route.ClusterId))
            {
                errors.Add($"Route '{route.RouteId}' has no cluster assigned.");
            }
            else if (!clusterIds.Contains(route.ClusterId))
            {
                errors.Add($"Route '{route.RouteId}' references unknown cluster '{route.ClusterId}'.");
            }
        }

        foreach (var cluster in clusters)
        {
            if (cluster.Destinations?.GroupBy(d => d.Key, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } duplicateDestination)
            {
                errors.Add($"Cluster '{cluster.ClusterId}' has duplicate destination '{duplicateDestination.Key}'.");
            }
        }

        foreach (var route in routes)
        {
            foreach (var exception in await _validator.ValidateRouteAsync(route))
            {
                errors.Add($"Route '{route.RouteId}': {exception.Message}");
            }
        }

        foreach (var cluster in clusters)
        {
            foreach (var exception in await _validator.ValidateClusterAsync(cluster))
            {
                errors.Add($"Cluster '{cluster.ClusterId}': {exception.Message}");
            }
        }

        return errors;
    }

    private void Persist(IReadOnlyList<RouteConfig> routes, IReadOnlyList<ClusterConfig> clusters)
    {
        var root = new JsonObject
        {
            ["Routes"] = ToIdKeyedMap(routes, r => r.RouteId, "RouteId"),
            ["Clusters"] = ToIdKeyedMap(clusters, c => c.ClusterId, "ClusterId"),
        };

        var tmpPath = UiConfigPath + ".tmp";
        File.WriteAllText(tmpPath, root.ToJsonString(SerializerOptions));
        File.Move(tmpPath, UiConfigPath, overwrite: true);
    }

    private static JsonObject ToIdKeyedMap<T>(IReadOnlyList<T> items, Func<T, string?> idSelector, string idPropertyName)
    {
        var map = new JsonObject();
        foreach (var item in items)
        {
            var id = idSelector(item);
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var node = JsonSerializer.SerializeToNode(item, SerializerOptions) as JsonObject ?? new JsonObject();
            node.Remove(idPropertyName);
            map[id] = node;
        }

        return map;
    }

    private static void MergeInto(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source)
        {
            if (value is JsonObject sourceObject && target[key] is JsonObject targetObject)
            {
                MergeInto(targetObject, sourceObject);
            }
            else
            {
                target[key] = value?.DeepClone();
            }
        }
    }

    private static ProxyConfigDocument ParseDocument(JsonObject section)
    {
        var routes = new List<RouteConfig>();
        if (section["Routes"] is JsonObject routesMap)
        {
            foreach (var (id, node) in routesMap)
            {
                if (node is not JsonObject routeNode)
                {
                    continue;
                }

                var clone = (JsonObject)routeNode.DeepClone();
                clone["RouteId"] = id;
                if (clone.Deserialize<RouteConfig>(SerializerOptions) is { } route)
                {
                    routes.Add(route);
                }
            }
        }

        var clusters = new List<ClusterConfig>();
        if (section["Clusters"] is JsonObject clustersMap)
        {
            foreach (var (id, node) in clustersMap)
            {
                if (node is not JsonObject clusterNode)
                {
                    continue;
                }

                var clone = (JsonObject)clusterNode.DeepClone();
                clone["ClusterId"] = id;
                if (clone.Deserialize<ClusterConfig>(SerializerOptions) is { } cluster)
                {
                    clusters.Add(cluster);
                }
            }
        }

        return new ProxyConfigDocument { Routes = routes, Clusters = clusters };
    }
}
