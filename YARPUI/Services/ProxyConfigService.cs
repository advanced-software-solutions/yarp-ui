using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Yarp.ReverseProxy;
using Yarp.ReverseProxy.Configuration;

namespace YARPUI.Services;

/// <summary>
/// A validated snapshot of routes + clusters ready to be applied to the running proxy.
/// The on-disk shape mirrors the appsettings.json "ReverseProxy" section
/// (routes and clusters keyed by id), while this in-memory shape uses lists.
/// </summary>
public sealed class ProxyConfigDocument
{
    public IReadOnlyList<RouteConfig> Routes { get; init; } = Array.Empty<RouteConfig>();
    public IReadOnlyList<ClusterConfig> Clusters { get; init; } = Array.Empty<ClusterConfig>();
}

/// <summary>
/// The full view the UI presents: every live route/cluster (whatever config source it
/// came from) plus which of them are managed by the YARP UI overlay.
/// </summary>
public sealed record LiveProxyConfig(
    IReadOnlyList<RouteConfig> Routes,
    IReadOnlyList<ClusterConfig> Clusters,
    IReadOnlySet<string> ManagedRouteIds,
    IReadOnlySet<string> ManagedClusterIds);

public sealed record ConfigApplyResult(bool Success, IReadOnlyList<string> Errors);

/// <summary>
/// Owns the proxy configuration lifecycle:
///  - standalone/embedded mode (owns the proxy): decides the initial source
///    (yarp-ui.routes.json when present, otherwise the appsettings.json seed);
///  - attach mode (host owns the proxy): shows the host's entire live configuration
///    read-only and manages a separate overlay that merges alongside it;
///  - validates and applies UI edits live via InMemoryConfigProvider (no restart);
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

    private readonly string _dataDirectory;
    private readonly string _contentRoot;
    private readonly string _environmentName;
    private readonly InMemoryConfigProvider _provider;
    private readonly IConfigValidator _validator;
    private readonly IProxyStateLookup? _stateLookup;
    private readonly bool _isAttachMode;
    private readonly ILogger<ProxyConfigService> _logger;
    private readonly object _sync = new();

    public ProxyConfigService(
        IConfiguration configuration,
        IHostEnvironment environment,
        InMemoryConfigProvider provider,
        IConfigValidator validator,
        IProxyStateLookup? stateLookup,
        bool isAttachMode,
        ILogger<ProxyConfigService> logger)
    {
        _dataDirectory = ResolveDataDirectory(configuration, environment.ContentRootPath);
        _contentRoot = environment.ContentRootPath;
        _environmentName = environment.EnvironmentName;
        _provider = provider;
        _validator = validator;
        _stateLookup = stateLookup;
        _isAttachMode = isAttachMode;
        _logger = logger;
    }

    public string UiConfigPath => Path.Combine(_dataDirectory, UiConfigFileName);

    /// <summary>True when the UI-managed file exists and therefore overrides appsettings.json.</summary>
    public bool IsManagedByUi => File.Exists(UiConfigPath);

    public bool IsAttachMode => _isAttachMode;

    /// <summary>
    /// The directory that holds mutable configuration (the UI-managed file and, optionally, an
    /// overriding appsettings.json). Defaults to the content root; set YarpUi:DataDirectory
    /// (e.g. YarpUi__DataDirectory=/app/data in Docker) to point it at a volume.
    /// </summary>
    public static string ResolveDataDirectory(IConfiguration configuration, string contentRoot)
    {
        var configured = configuration["YarpUi:DataDirectory"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            return contentRoot;
        }

        return Path.GetFullPath(
            Path.IsPathRooted(configured) ? configured : Path.Combine(contentRoot, configured));
    }

    /// <summary>
    /// Standalone/embedded mode: the configuration used at startup, before DI is available.
    /// A corrupt UI file falls back to the appsettings.json seed rather than taking the proxy down.
    /// </summary>
    public static ProxyConfigDocument LoadInitial(string dataDirectory, string contentRoot, string environmentName)
    {
        var overlay = LoadOverlay(dataDirectory);
        if (overlay.Routes.Count > 0 || overlay.Clusters.Count > 0)
        {
            return overlay;
        }

        return LoadSeed(dataDirectory, contentRoot, environmentName);
    }

    /// <summary>
    /// Attach mode: only the UI overlay file is loaded — the host's own proxy configuration
    /// (appsettings, custom providers, …) is never read, seeded, or replaced.
    /// </summary>
    public static ProxyConfigDocument LoadOverlay(string dataDirectory)
    {
        var uiPath = Path.Combine(dataDirectory, UiConfigFileName);
        if (!File.Exists(uiPath))
        {
            return new ProxyConfigDocument();
        }

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
            Console.Error.WriteLine($"Failed to read {UiConfigFileName} ({ex.Message}). Starting with an empty overlay.");
        }

        return new ProxyConfigDocument();
    }

    /// <summary>
    /// Reads the seed configuration from appsettings.json (+ optional environment file).
    /// Files in the data directory take precedence over the ones baked into the content root,
    /// so a volume-mounted appsettings.json can override the shipped defaults.
    /// </summary>
    public static ProxyConfigDocument LoadSeed(string dataDirectory, string contentRoot, string environmentName)
    {
        var section = new JsonObject();

        var seedFiles = new List<string>
        {
            Path.Combine(contentRoot, "appsettings.json"),
            Path.Combine(contentRoot, $"appsettings.{environmentName}.json"),
            Path.Combine(dataDirectory, "appsettings.json"),
            Path.Combine(dataDirectory, $"appsettings.{environmentName}.json"),
        };

        foreach (var path in seedFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
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
                Console.Error.WriteLine($"Failed to read {path} ({ex.Message}).");
            }
        }

        return ParseDocument(section);
    }

    /// <summary>
    /// Everything currently live in the proxy: in attach mode this is the combined view of all
    /// config sources (host's own + overlay); otherwise the UI-owned configuration.
    /// </summary>
    public LiveProxyConfig GetLiveConfig()
    {
        var overlay = _provider.GetConfig();
        var managedRoutes = overlay.Routes.Select(r => r.RouteId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var managedClusters = overlay.Clusters.Select(c => c.ClusterId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!_isAttachMode)
        {
            return new LiveProxyConfig(overlay.Routes, overlay.Clusters, managedRoutes, managedClusters);
        }

        var routes = _stateLookup?.GetRoutes().Select(r => r.Config).ToList() ?? new List<RouteConfig>();
        var clusters = _stateLookup?.GetClusters().Select(c => c.Model.Config).ToList() ?? new List<ClusterConfig>();
        return new LiveProxyConfig(routes, clusters, managedRoutes, managedClusters);
    }

    /// <summary>
    /// Validates, applies live (no restart) and persists the given overlay configuration.
    /// In attach mode the argument is only the UI-managed subset — the host's own routes and
    /// clusters are validated against but never modified.
    /// </summary>
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
            "Proxy configuration updated from the UI: {RouteCount} overlay routes, {ClusterCount} overlay clusters",
            routes.Count, clusters.Count);

        return new ConfigApplyResult(true, Array.Empty<string>());
    }

    /// <summary>
    /// Standalone/embedded mode: discards UI changes and returns to the appsettings.json seed.
    /// Attach mode: clears the overlay entirely (the host's configuration is untouched).
    /// </summary>
    public async Task<ConfigApplyResult> ResetAsync()
    {
        if (_isAttachMode)
        {
            lock (_sync)
            {
                _provider.Update(Array.Empty<RouteConfig>(), Array.Empty<ClusterConfig>());
                TryDeleteUiConfigFile();
            }

            _logger.LogInformation("UI overlay cleared.");
            return new ConfigApplyResult(true, Array.Empty<string>());
        }

        var seed = LoadSeed(_dataDirectory, _contentRoot, _environmentName);

        var errors = await ValidateAsync(seed.Routes, seed.Clusters);
        if (errors.Count > 0)
        {
            return new ConfigApplyResult(false, errors);
        }

        lock (_sync)
        {
            _provider.Update(seed.Routes, seed.Clusters);
            TryDeleteUiConfigFile();
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

        // In attach mode the host owns routes/clusters of its own; the overlay must not shadow them
        // (YARP treats the same id in two config sources as a conflict).
        var overlayRouteIds = routes.Select(r => r.RouteId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var overlayClusterIds = clusters.Select(c => c.ClusterId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<RouteConfig> foreignRoutes = new();
        var foreignClusterIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (_isAttachMode && _stateLookup is not null)
        {
            var previousOverlayRouteIds = _provider.GetConfig().Routes.Select(r => r.RouteId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var previousOverlayClusterIds = _provider.GetConfig().Clusters.Select(c => c.ClusterId).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreignRoutes = _stateLookup.GetRoutes()
                .Select(r => r.Config)
                .Where(r => !previousOverlayRouteIds.Contains(r.RouteId ?? string.Empty))
                .ToList();

            foreach (var foreignCluster in _stateLookup.GetClusters().Select(c => c.Model.Config)
                         .Where(c => !previousOverlayClusterIds.Contains(c.ClusterId ?? string.Empty)))
            {
                foreignClusterIds.Add(foreignCluster.ClusterId ?? string.Empty);
            }

            foreach (var route in routes.Where(r => !string.IsNullOrEmpty(r.RouteId)))
            {
                if (foreignRoutes.Any(f => string.Equals(f.RouteId, route.RouteId, StringComparison.OrdinalIgnoreCase)))
                {
                    errors.Add($"Route '{route.RouteId}' already exists in the app's own configuration — choose a different id.");
                }
            }

            foreach (var cluster in clusters.Where(c => !string.IsNullOrEmpty(c.ClusterId)))
            {
                if (foreignClusterIds.Contains(cluster.ClusterId))
                {
                    errors.Add($"Cluster '{cluster.ClusterId}' already exists in the app's own configuration — choose a different id.");
                }
            }

            // Deleting an overlay cluster that a host-managed route still points at would break that route.
            foreach (var foreignRoute in foreignRoutes.Where(f => previousOverlayClusterIds.Contains(f.ClusterId ?? string.Empty)))
            {
                if (!overlayClusterIds.Contains(foreignRoute.ClusterId ?? string.Empty))
                {
                    errors.Add($"Cluster '{foreignRoute.ClusterId}' is still used by route '{foreignRoute.RouteId}' from the app's own configuration and cannot be removed.");
                }
            }
        }

        // Routes may target overlay clusters or (in attach mode) clusters owned by the host.
        var allClusterIds = overlayClusterIds.Union(foreignClusterIds, StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var route in routes)
        {
            if (string.IsNullOrWhiteSpace(route.ClusterId))
            {
                errors.Add($"Route '{route.RouteId}' has no cluster assigned.");
            }
            else if (!allClusterIds.Contains(route.ClusterId))
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

    private void TryDeleteUiConfigFile()
    {
        try
        {
            if (File.Exists(UiConfigPath))
            {
                File.Delete(UiConfigPath);
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not delete {Path}.", UiConfigPath);
        }
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
