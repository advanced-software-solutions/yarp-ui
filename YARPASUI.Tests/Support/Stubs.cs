using Yarp.ReverseProxy;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;

namespace YARPASUI.Tests.Support;

/// <summary>
/// Stands in for YARP's ConfigValidator in unit-level tests: passes everything unless a
/// scenario injects failures, so tests target ProxyConfigService's own rules specifically.
/// </summary>
internal sealed class StubConfigValidator : IConfigValidator
{
    public List<(string ItemId, string Message)> RouteFailures { get; } = [];
    public List<(string ItemId, string Message)> ClusterFailures { get; } = [];

    public ValueTask<IList<Exception>> ValidateRouteAsync(RouteConfig route)
    {
        return ValueTask.FromResult<IList<Exception>>(
            RouteFailures.Where(f => f.ItemId == route.RouteId)
                .Select(f => (Exception)new InvalidOperationException(f.Message)).ToList());
    }

    public ValueTask<IList<Exception>> ValidateClusterAsync(ClusterConfig cluster)
    {
        return ValueTask.FromResult<IList<Exception>>(
            ClusterFailures.Where(f => f.ItemId == cluster.ClusterId)
                .Select(f => (Exception)new InvalidOperationException(f.Message)).ToList());
    }
}

/// <summary>
/// Stands in for YARP's live proxy state in attach mode. Serves whatever the appsettings
/// files currently define (mirroring the host's config-file provider) plus optional
/// "custom provider" items that no file defines — those must stay read-only in the UI.
/// Reading the files on every call also lets ProxyConfigService.WaitForReloadAsync observe
/// the hot reload immediately after a write-back.
/// </summary>
internal sealed class StubProxyStateLookup : IProxyStateLookup
{
    private static readonly HttpMessageInvoker SharedInvoker = new(new UnreachableHandler());

    public Func<IReadOnlyList<RouteConfig>>? LoadRoutesFromFiles { get; set; }
    public Func<IReadOnlyList<ClusterConfig>>? LoadClustersFromFiles { get; set; }
    public List<RouteConfig> CustomRoutes { get; } = [];
    public List<ClusterConfig> CustomClusters { get; } = [];

    private IEnumerable<RouteConfig> AllRoutes =>
        (LoadRoutesFromFiles?.Invoke() ?? []).Concat(CustomRoutes);

    private IEnumerable<ClusterConfig> AllClusters =>
        (LoadClustersFromFiles?.Invoke() ?? []).Concat(CustomClusters);

    public IEnumerable<RouteModel> GetRoutes() =>
        AllRoutes.Select(r => new RouteModel(r, cluster: null, HttpTransformer.Default));

    public IEnumerable<ClusterState> GetClusters() =>
        AllClusters.Select(c => new ClusterState(c.ClusterId!, new ClusterModel(c, SharedInvoker)));

    public bool TryGetRoute(string id, out RouteModel? route)
    {
        route = GetRoutes().FirstOrDefault(r => string.Equals(r.Config.RouteId, id, StringComparison.OrdinalIgnoreCase));
        return route is not null;
    }

    public bool TryGetCluster(string id, out ClusterState? cluster)
    {
        cluster = GetClusters().FirstOrDefault(c => string.Equals(c.ClusterId, id, StringComparison.OrdinalIgnoreCase));
        return cluster is not null;
    }

    private sealed class UnreachableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new NotSupportedException("The stub never proxies anything.");
    }
}
