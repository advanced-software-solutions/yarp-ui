using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Localization;
using Yarp.ReverseProxy;
using Yarp.ReverseProxy.Configuration;
using YARPUI.Resources;

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
/// came from) plus which of them the UI can edit.
/// </summary>
public sealed record LiveProxyConfig(
    IReadOnlyList<RouteConfig> Routes,
    IReadOnlyList<ClusterConfig> Clusters,
    IReadOnlySet<string> EditableRouteIds,
    IReadOnlySet<string> EditableClusterIds);

public sealed record ConfigApplyResult(bool Success, IReadOnlyList<string> Errors);

/// <summary>
/// Owns the proxy configuration lifecycle:
///  - standalone/embedded mode (owns the proxy): decides the initial source
///    (yarp-ui.routes.json when present, otherwise the appsettings.json seed);
///  - attach mode (host owns the proxy): shows the host's entire live configuration and
///    edits are written back into the appsettings.json files the items came from —
///    YARP hot-reloads those files, so edits go live without a restart and the host's
///    code (transforms, middleware, custom pipeline) is never touched. Items that do not
///    come from an appsettings file (e.g. a database-backed custom provider) stay read-only.
/// </summary>
public sealed class ProxyConfigService
{
    public const string UiConfigFileName = "yarp-ui.routes.json";
    public const string BackupSuffix = ".yarpui.bak";
    private const string SeedSectionName = "ReverseProxy";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Write-back keeps explicit nulls so the editor can clear fields it manages;
    // fields the editor does not model are preserved by merging onto the existing node.
    private static readonly JsonSerializerOptions WriteBackOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
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
    private readonly IStringLocalizer<UIStrings> _localizer;
    private readonly object _sync = new();

    public ProxyConfigService(
        IConfiguration configuration,
        IHostEnvironment environment,
        InMemoryConfigProvider provider,
        IConfigValidator validator,
        IProxyStateLookup? stateLookup,
        bool isAttachMode,
        ILogger<ProxyConfigService> logger,
        IStringLocalizer<UIStrings> localizer)
    {
        _dataDirectory = ResolveDataDirectory(configuration, environment.ContentRootPath);
        _contentRoot = environment.ContentRootPath;
        _environmentName = environment.EnvironmentName;
        _provider = provider;
        _validator = validator;
        _stateLookup = stateLookup;
        _isAttachMode = isAttachMode;
        _logger = logger;
        // The ResourceManager localizer resolves the culture per call, so sharing one
        // instance across requests (this service is a singleton) stays culture-correct.
        _localizer = localizer;
    }

    private string L(string key, params object[] args) => _localizer[key, args];

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
    /// Only the UI overlay file. In attach mode this only matters for compatibility with
    /// configurations created before write-back editing existed; they migrate into
    /// appsettings.json on the next save.
    /// </summary>
    public static ProxyConfigDocument LoadOverlay(string dataDirectory)
    {
        return LoadProxySectionFile(Path.Combine(dataDirectory, UiConfigFileName));
    }

    /// <summary>
    /// Reads the seed configuration from appsettings.json (+ optional environment file).
    /// Files in the data directory take precedence over the ones baked into the content root,
    /// so a volume-mounted appsettings.json can override the shipped defaults.
    /// </summary>
    public static ProxyConfigDocument LoadSeed(string dataDirectory, string contentRoot, string environmentName)
    {
        var merged = new JsonObject();
        foreach (var path in ConfigFileCandidates(dataDirectory, contentRoot, environmentName))
        {
            var section = ReadReverseProxySection(path);
            if (section is not null)
            {
                MergeInto(merged, section);
            }
        }

        return ParseDocument(merged);
    }

    // ---- current state ----

    /// <summary>
    /// Everything currently live in the proxy. In attach mode this is the combined view of all
    /// config sources; editable items are the ones defined in an appsettings file.
    /// Non-cluster entries under "Clusters" in appsettings (e.g. HttpClient/HttpRequest gateway
    /// settings parked there by the app) are hidden — they are gateway config, not proxy config.
    /// </summary>
    public LiveProxyConfig GetLiveConfig()
    {
        if (!_isAttachMode)
        {
            var current = _provider.GetConfig();
            var ownedClusters = current.Clusters.Where(IsProxyCluster).ToList();
            return new LiveProxyConfig(
                current.Routes,
                ownedClusters,
                current.Routes.Select(r => r.RouteId).ToHashSet(StringComparer.OrdinalIgnoreCase),
                ownedClusters.Select(c => c.ClusterId).ToHashSet(StringComparer.OrdinalIgnoreCase));
        }

        var liveRoutes = _stateLookup?.GetRoutes().Select(r => r.Config).ToList() ?? new List<RouteConfig>();
        var liveClusters = _stateLookup?.GetClusters().Select(c => c.Model.Config).Where(IsProxyCluster).ToList() ?? new List<ClusterConfig>();

        var (routeOrigins, clusterOrigins) = ScanOrigins();
        var editableRoutes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var editableClusters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var route in liveRoutes)
        {
            if (route.RouteId is not null && routeOrigins.ContainsKey(route.RouteId))
            {
                editableRoutes.Add(route.RouteId);
            }
        }
        foreach (var cluster in liveClusters)
        {
            if (cluster.ClusterId is not null && clusterOrigins.ContainsKey(cluster.ClusterId))
            {
                editableClusters.Add(cluster.ClusterId);
            }
        }

        return new LiveProxyConfig(liveRoutes, liveClusters, editableRoutes, editableClusters);
    }

    // ---- applying changes ----

    /// <summary>
    /// Validates and applies edits live (no restart).
    /// Standalone/embedded: updates the UI-owned in-memory provider and persists to yarp-ui.routes.json.
    /// Attach: writes each change back into the appsettings.json file the item came from and lets
    /// YARP hot-reload; only the edited items are rewritten, everything else in the files is preserved.
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
            if (_isAttachMode)
            {
                WriteBackToAppSettings(routes, clusters);
            }
            else
            {
                _provider.Update(routes, clusters);
                Persist(routes, clusters);
            }
        }

        if (_isAttachMode)
        {
            await WaitForReloadAsync();
            _logger.LogInformation(
                "Proxy configuration updated from the UI and written back to appsettings: {RouteCount} routes, {ClusterCount} clusters",
                routes.Count, clusters.Count);
        }
        else
        {
            _logger.LogInformation(
                "Proxy configuration updated from the UI: {RouteCount} routes, {ClusterCount} clusters",
                routes.Count, clusters.Count);
        }

        return new ConfigApplyResult(true, Array.Empty<string>());
    }

    /// <summary>
    /// Standalone/embedded: returns to the appsettings.json seed.
    /// Attach: restores every appsettings file from its .yarpui.bak backup (taken the first
    /// time the UI modified it) and removes the UI overlay file.
    /// </summary>
    public async Task<ConfigApplyResult> ResetAsync()
    {
        lock (_sync)
        {
            if (_isAttachMode)
            {
                RestoreBackups();
                TryDeleteFile(UiConfigPath);
            }
            else
            {
                var seed = LoadSeed(_dataDirectory, _contentRoot, _environmentName);
                var errors = ValidateAsync(seed.Routes, seed.Clusters).GetAwaiter().GetResult();
                if (errors.Count > 0)
                {
                    return new ConfigApplyResult(false, errors);
                }

                _provider.Update(seed.Routes, seed.Clusters);
                TryDeleteFile(UiConfigPath);
            }
        }

        if (_isAttachMode)
        {
            await WaitForReloadAsync();
        }

        _logger.LogInformation("Proxy configuration reset ({Mode}).", _isAttachMode ? "attach" : "seed");
        return new ConfigApplyResult(true, Array.Empty<string>());
    }

    private async Task<List<string>> ValidateAsync(IReadOnlyList<RouteConfig> routes, IReadOnlyList<ClusterConfig> clusters)
    {
        var errors = new List<string>();

        if (routes.GroupBy(r => r.RouteId, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } duplicateRoute)
        {
            errors.Add(L("validation.duplicateRouteId", duplicateRoute.Key ?? string.Empty));
        }

        if (clusters.GroupBy(c => c.ClusterId, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } duplicateCluster)
        {
            errors.Add(L("validation.duplicateClusterId", duplicateCluster.Key ?? string.Empty));
        }

        // Items the UI cannot edit (config from a custom provider, e.g. a database): the editor
        // never sends them back, and new/renamed items must not shadow them.
        var live = GetLiveConfig();
        var fixedRouteIds = live.Routes.Select(r => r.RouteId)
            .Where(id => id is not null && !live.EditableRouteIds.Contains(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fixedClusterIds = live.Clusters.Select(c => c.ClusterId)
            .Where(id => id is not null && !live.EditableClusterIds.Contains(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var route in routes.Where(r => !string.IsNullOrEmpty(r.RouteId)))
        {
            if (fixedRouteIds.Contains(route.RouteId!))
            {
                errors.Add(L("validation.routeNonFile", route.RouteId!));
            }
        }

        foreach (var cluster in clusters.Where(c => !string.IsNullOrEmpty(c.ClusterId)))
        {
            if (fixedClusterIds.Contains(cluster.ClusterId!))
            {
                errors.Add(L("validation.clusterNonFile", cluster.ClusterId!));
            }
        }

        // Routes may target any cluster: the ones being saved, or ones the app owns elsewhere.
        var allClusterIds = clusters.Select(c => c.ClusterId)
            .Concat(live.Clusters.Select(c => c.ClusterId))
            .Where(id => !string.IsNullOrEmpty(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var liveRouteCluster = live.Routes
            .Where(r => r.RouteId is not null)
            .GroupBy(r => r.RouteId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().ClusterId, StringComparer.OrdinalIgnoreCase);
        foreach (var route in routes)
        {
            if (string.IsNullOrWhiteSpace(route.ClusterId))
            {
                errors.Add(L("validation.routeNoCluster", route.RouteId ?? string.Empty));
            }
            else if (!allClusterIds.Contains(route.ClusterId) &&
                     !(liveRouteCluster.TryGetValue(route.RouteId ?? string.Empty, out var previous) &&
                       string.Equals(previous, route.ClusterId, StringComparison.OrdinalIgnoreCase)))
            {
                // A reference that already exists in the live config is a pre-existing condition
                // the editor didn't introduce; YARP itself only logs it and serves 502s for the route.
                errors.Add(L("validation.routeUnknownCluster", route.RouteId ?? string.Empty, route.ClusterId));
            }
        }

        // Deleting an editable cluster that a non-editable route still points at would break that route.
        var savedClusterIds = clusters.Select(c => c.ClusterId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var fixedRoute in live.Routes.Where(r => r.RouteId is not null && !live.EditableRouteIds.Contains(r.RouteId)))
        {
            var clusterId = fixedRoute.ClusterId ?? string.Empty;
            if (live.EditableClusterIds.Contains(clusterId) && !savedClusterIds.Contains(clusterId))
            {
                errors.Add(L("validation.clusterStillUsed", clusterId, fixedRoute.RouteId!));
            }
        }

        foreach (var cluster in clusters)
        {
            if (cluster.Destinations?.GroupBy(d => d.Key, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } duplicateDestination)
            {
                errors.Add(L("validation.duplicateDestination", cluster.ClusterId ?? string.Empty, duplicateDestination.Key));
            }
        }

        foreach (var route in routes)
        {
            foreach (var exception in await _validator.ValidateRouteAsync(route))
            {
                errors.Add(L("validation.routeInvalid", route.RouteId ?? string.Empty, exception.Message));
            }
        }

        foreach (var cluster in clusters)
        {
            foreach (var exception in await _validator.ValidateClusterAsync(cluster))
            {
                errors.Add(L("validation.clusterInvalid", cluster.ClusterId ?? string.Empty, exception.Message));
            }
        }

        return errors;
    }

    // ---- attach mode: origin tracking and appsettings write-back ----

    private static List<string> ConfigFileCandidates(string dataDirectory, string contentRoot, string environmentName)
    {
        var files = new List<string>
        {
            Path.Combine(contentRoot, "appsettings.json"),                       // lowest priority
            Path.Combine(contentRoot, $"appsettings.{environmentName}.json"),
            Path.Combine(dataDirectory, "appsettings.json"),
            Path.Combine(dataDirectory, $"appsettings.{environmentName}.json"), // highest priority
        };

        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private (Dictionary<string, string> Routes, Dictionary<string, string> Clusters) ScanOrigins()
    {
        var routeOrigins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var clusterOrigins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in ConfigFileCandidates(_dataDirectory, _contentRoot, _environmentName).Where(File.Exists))
        {
            var section = ReadReverseProxySection(path);
            if (section?["Routes"] is JsonObject routes)
            {
                foreach (var id in routes.Select(e => e.Key))
                {
                    routeOrigins[id] = path;
                }
            }

            if (section?["Clusters"] is JsonObject clusters)
            {
                foreach (var (id, body) in clusters)
                {
                    if (body is JsonObject node && IsProxyClusterNode(node))
                    {
                        clusterOrigins[id] = path;
                    }
                }
            }
        }

        // Legacy overlay items are editable too; saves migrate them into appsettings.json.
        // The overlay file keeps Routes/Clusters at the top level (see Persist), unlike
        // appsettings.json files where they live under the ReverseProxy section.
        if (File.Exists(UiConfigPath))
        {
            var overlay = LoadJsonObject(UiConfigPath);
            if (overlay?["Routes"] is JsonObject routes)
            {
                foreach (var id in routes.Select(e => e.Key))
                {
                    routeOrigins[id] = UiConfigPath;
                }
            }

            if (overlay?["Clusters"] is JsonObject clusters)
            {
                foreach (var (id, body) in clusters)
                {
                    if (body is JsonObject node && IsProxyClusterNode(node))
                    {
                        clusterOrigins[id] = UiConfigPath;
                    }
                }
            }
        }

        return (routeOrigins, clusterOrigins);
    }

    private void WriteBackToAppSettings(IReadOnlyList<RouteConfig> routes, IReadOnlyList<ClusterConfig> clusters)
    {
        var (routeOrigins, clusterOrigins) = ScanOrigins();
        var candidates = ConfigFileCandidates(_dataDirectory, _contentRoot, _environmentName);
        var baseFile = candidates[0];

        var desiredRouteIds = routes.Select(r => r.RouteId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var desiredClusterIds = clusters.Select(c => c.ClusterId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void RemoveIdFromFile(string path, string kind, string id)
        {
            var root = LoadJsonObject(path);
            // appsettings files keep items under the "ReverseProxy" section; the overlay
            // file keeps Routes/Clusters at the top level (see Persist).
            var map = (root?["ReverseProxy"]?[kind] as JsonObject) ?? (root?[kind] as JsonObject);
            if (map is not null && map.ContainsKey(id))
            {
                map.Remove(id);
                SaveJsonObject(path, root!, touched);
            }
        }

        // Desired items: write into the highest-priority file that defines them (or the base
        // file for new items) and drop duplicates from lower-priority files / the overlay.
        foreach (var route in routes)
        {
            var id = route.RouteId!;
            if (routeOrigins.TryGetValue(id, out var origin) && !string.Equals(origin, UiConfigPath, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var path in candidates.Where(p => File.Exists(p) && !string.Equals(p, origin, StringComparison.OrdinalIgnoreCase)))
                {
                    RemoveIdFromFile(path, "Routes", id);
                }

                UpdateItemInFile(origin, "Routes", id, SerializeRouteNode(route));
            }
            else
            {
                if (routeOrigins.ContainsKey(id))
                {
                    RemoveIdFromFile(UiConfigPath, "Routes", id); // migrate from legacy overlay
                }

                UpdateItemInFile(baseFile, "Routes", id, SerializeRouteNode(route));
            }
        }

        foreach (var cluster in clusters)
        {
            var id = cluster.ClusterId!;
            if (clusterOrigins.TryGetValue(id, out var origin) && !string.Equals(origin, UiConfigPath, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var path in candidates.Where(p => File.Exists(p) && !string.Equals(p, origin, StringComparison.OrdinalIgnoreCase)))
                {
                    RemoveIdFromFile(path, "Clusters", id);
                }

                UpdateItemInFile(origin, "Clusters", id, SerializeClusterNode(cluster));
            }
            else
            {
                if (clusterOrigins.ContainsKey(id))
                {
                    RemoveIdFromFile(UiConfigPath, "Clusters", id);
                }

                UpdateItemInFile(baseFile, "Clusters", id, SerializeClusterNode(cluster));
            }
        }

        // Removed items: delete from every file that defines them.
        foreach (var (id, _) in routeOrigins.Where(e => !desiredRouteIds.Contains(e.Key)))
        {
            foreach (var path in candidates.Where(File.Exists))
            {
                RemoveIdFromFile(path, "Routes", id);
            }

            RemoveIdFromFile(UiConfigPath, "Routes", id);
        }

        foreach (var (id, _) in clusterOrigins.Where(e => !desiredClusterIds.Contains(e.Key)))
        {
            foreach (var path in candidates.Where(File.Exists))
            {
                RemoveIdFromFile(path, "Clusters", id);
            }

            RemoveIdFromFile(UiConfigPath, "Clusters", id);
        }

        // Keep the in-memory overlay source in sync with the (possibly migrated/emptied) file.
        var remainingOverlay = LoadOverlay(_dataDirectory);
        _provider.Update(remainingOverlay.Routes, remainingOverlay.Clusters);
    }

    private void UpdateItemInFile(string path, string kind, string id, JsonObject itemNode)
    {
        var root = LoadJsonObject(path) ?? new JsonObject();
        var section = root[SeedSectionName] as JsonObject;
        if (section is null)
        {
            section = new JsonObject();
            root[SeedSectionName] = section;
        }

        var map = section[kind] as JsonObject;
        if (map is null)
        {
            map = new JsonObject();
            section[kind] = map;
        }

        // Merge onto the existing node: keys the payload carries are updated (explicit nulls
        // clear fields); keys it does not model keep their current value, and null-valued keys
        // that never existed are not introduced, so files stay close to their original shape.
        if (map[id] is JsonObject existing)
        {
            MergeNode(existing, itemNode);
        }
        else
        {
            map[id] = itemNode;
        }

        SaveJsonObject(path, root, touched: new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private static void MergeNode(JsonObject target, JsonObject incoming)
    {
        foreach (var (key, value) in incoming)
        {
            if (value is JsonObject incomingObject && target[key] is JsonObject targetObject)
            {
                MergeNode(targetObject, incomingObject);
            }
            else if (value is null && !target.ContainsKey(key))
            {
                // don't add "Key": null noise for fields the file never had
            }
            else
            {
                target[key] = value?.DeepClone();
            }
        }
    }

    private static JsonObject SerializeRouteNode(RouteConfig route)
    {
        var node = JsonSerializer.SerializeToNode(route, WriteBackOptions) as JsonObject ?? new JsonObject();
        node.Remove("RouteId");
        return node;
    }

    private static JsonObject SerializeClusterNode(ClusterConfig cluster)
    {
        var node = JsonSerializer.SerializeToNode(cluster, WriteBackOptions) as JsonObject ?? new JsonObject();
        node.Remove("ClusterId");
        return node;
    }

    private void RestoreBackups()
    {
        foreach (var path in ConfigFileCandidates(_dataDirectory, _contentRoot, _environmentName).Where(File.Exists))
        {
            var backup = path + BackupSuffix;
            if (File.Exists(backup))
            {
                File.Copy(backup, path, overwrite: true);
                File.Delete(backup);
            }
        }

        _provider.Update(Array.Empty<RouteConfig>(), Array.Empty<ClusterConfig>());
    }

    private async Task WaitForReloadAsync()
    {
        // The host's IConfiguration reloads asynchronously after the file write; YARP picks up
        // the change and swaps the config. Poll until the live state matches what the appsettings
        // files now contain — that is exactly what YARP is about to load, so comparing against it
        // (routes AND clusters, full serialized items) converges the moment the reload lands.
        static string RouteKey(RouteConfig r) => JsonSerializer.Serialize(r, WriteBackOptions);
        static string ClusterKey(ClusterConfig c) => JsonSerializer.Serialize(c, WriteBackOptions);

        for (var attempt = 0; attempt < 60; attempt++)
        {
            await Task.Delay(100);

            try
            {
                var live = GetLiveConfig();
                var fromFiles = LoadSeed(_dataDirectory, _contentRoot, _environmentName);
                var overlay = _provider.GetConfig();

                var expectedRoutes = fromFiles.Routes.Concat(overlay.Routes)
                    .Where(r => r.RouteId is not null)
                    .GroupBy(r => r.RouteId!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => RouteKey(g.First()), StringComparer.OrdinalIgnoreCase);
                foreach (var liveRoute in live.Routes.Where(r => r.RouteId is not null && !live.EditableRouteIds.Contains(r.RouteId)))
                {
                    expectedRoutes[liveRoute.RouteId!] = RouteKey(liveRoute);
                }

                var expectedClusters = fromFiles.Clusters.Concat(overlay.Clusters)
                    .Where(c => c.ClusterId is not null)
                    .GroupBy(c => c.ClusterId!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => ClusterKey(g.First()), StringComparer.OrdinalIgnoreCase);
                foreach (var liveCluster in live.Clusters.Where(c => c.ClusterId is not null && !live.EditableClusterIds.Contains(c.ClusterId)))
                {
                    expectedClusters[liveCluster.ClusterId!] = ClusterKey(liveCluster);
                }

                var actualRoutes = live.Routes
                    .Where(r => r.RouteId is not null)
                    .GroupBy(r => r.RouteId!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => RouteKey(g.First()), StringComparer.OrdinalIgnoreCase);
                var actualClusters = live.Clusters
                    .Where(c => c.ClusterId is not null)
                    .GroupBy(c => c.ClusterId!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => ClusterKey(g.First()), StringComparer.OrdinalIgnoreCase);

                if (expectedRoutes.Count == actualRoutes.Count &&
                    expectedRoutes.All(kv => actualRoutes.TryGetValue(kv.Key, out var actual) && actual == kv.Value) &&
                    expectedClusters.Count == actualClusters.Count &&
                    expectedClusters.All(kv => actualClusters.TryGetValue(kv.Key, out var actual) && actual == kv.Value))
                {
                    return;
                }
            }
            catch
            {
                // mid-reload inconsistency; retry
            }
        }
    }

    // ---- standalone mode: UI-owned file ----

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

    // ---- JSON helpers ----

    private static readonly HashSet<string> RecognizedClusterKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Destinations", "LoadBalancingPolicy", "SessionAffinity", "HealthCheck", "HttpRequest", "HttpClient", "Metadata"
    };

    /// <summary>
    /// True when the parsed entry is a real proxy cluster. Entries under "Clusters" whose body
    /// has no recognized cluster keys (e.g. HttpClient/HttpRequest gateway settings parked there
    /// by the app) are gateway configuration — the UI ignores them and never touches them in files.
    /// </summary>
    private static bool IsProxyCluster(ClusterConfig config)
    {
        return (config.Destinations?.Count > 0)
            || config.LoadBalancingPolicy is not null
            || config.HealthCheck is not null
            || config.SessionAffinity is not null;
    }

    private static bool IsProxyClusterNode(JsonObject node)
    {
        return node.Any(kv => RecognizedClusterKeys.Contains(kv.Key));
    }

    private static JsonObject? LoadJsonObject(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(path), documentOptions: LenientDocumentOptions) as JsonObject;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to read {path} ({ex.Message}).");
            return null;
        }
    }

    private static void SaveJsonObject(string path, JsonObject root, HashSet<string> touched)
    {
        // Keep the first-ever copy of every file the UI modifies so Reset can restore it.
        var backup = path + BackupSuffix;
        if (!File.Exists(backup))
        {
            File.Copy(path, backup);
        }

        // Write directly to the target (no tmp+rename): .NET's config file watcher reliably
        // raises change events for in-place writes, but can miss rename-replacements — and a
        // missed event would leave the proxy running the previous configuration.
        File.WriteAllText(path, root.ToJsonString(SerializerOptions));
        touched.Add(path);
    }

    private static JsonObject? ReadReverseProxySection(string path)
    {
        return LoadJsonObject(path)?[SeedSectionName] as JsonObject;
    }

    private static ProxyConfigDocument LoadProxySectionFile(string path)
    {
        if (!File.Exists(path))
        {
            return new ProxyConfigDocument();
        }

        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path), documentOptions: LenientDocumentOptions) as JsonObject;
            if (node is not null)
            {
                return ParseDocument(node);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to read {path} ({ex.Message}).");
        }

        return new ProxyConfigDocument();
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not delete {Path}.", path);
        }
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
                if (node is not JsonObject clusterNode || !IsProxyClusterNode(clusterNode))
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
