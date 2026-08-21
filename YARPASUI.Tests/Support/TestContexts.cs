using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Yarp.ReverseProxy.Configuration;
using YARPUI.Api;
using YARPUI.Resources;
using YARPUI.Services;

namespace YARPASUI.Tests.Support;

/// <summary>Per-scenario state for ProxyConfigService tests (shared across step classes via context injection).</summary>
internal sealed class ProxyConfigTestContext : IDisposable
{
    public const string EnvironmentName = "Testing";

    public TempDir? Root { get; private set; }
    public string ContentRoot { get; private set; } = "";
    public string DataDirectory { get; private set; } = "";
    public InMemoryConfigProvider Provider { get; } = new([], []);
    public StubConfigValidator Validator { get; } = new();
    public StubProxyStateLookup? StateLookup { get; private set; }
    public ProxyConfigService? Service { get; private set; }

    public ProxyConfigDocument? Loaded { get; set; }
    public LiveProxyConfig? Live { get; set; }
    public ConfigApplyResult? Result { get; set; }
    public string? ResolvedDataDirectory { get; set; }
    public string? DataDirectorySetting { get; set; }
    public string? OriginalAppSettings { get; set; }

    // Real localizer (neutral English resources) for validation-message assertions.
    private ServiceProvider? _serviceProvider;

    private IStringLocalizer<UIStrings> CreateLocalizer()
    {
        _serviceProvider ??= new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        return _serviceProvider.GetRequiredService<IStringLocalizer<UIStrings>>();
    }

    public IReadOnlyList<RouteConfig> CurrentRoutes => Live?.Routes ?? Loaded?.Routes ?? [];
    public IReadOnlyList<ClusterConfig> CurrentClusters => Live?.Clusters ?? Loaded?.Clusters ?? [];

    public string UiConfigPath => Path.Combine(DataDirectory, ProxyConfigService.UiConfigFileName);

    public void EnsureDirectories()
    {
        Root ??= new TempDir();
        ContentRoot = Path.Combine(Root.Path, "content");
        DataDirectory = Path.Combine(Root.Path, "data");
        Directory.CreateDirectory(ContentRoot);
        Directory.CreateDirectory(DataDirectory);
    }

    /// <summary>Builds the service the same way AddYarpUi does, but with test doubles for validator/live state.</summary>
    public ProxyConfigService CreateService(bool attachMode, string? dataDirectorySetting = null)
    {
        EnsureDirectories();

        var configurationSettings = new Dictionary<string, string?>
        {
            ["YarpUi:DataDirectory"] = dataDirectorySetting ?? DataDirectory,
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configurationSettings).Build();
        var environment = new HostingEnvironment
        {
            ApplicationName = "YARPASUI.Tests",
            EnvironmentName = EnvironmentName,
            ContentRootPath = ContentRoot,
        };

        if (attachMode)
        {
            // Mirrors AttachYarpUi: the host's file config plus the overlay are both live
            // sources, so the lookup serves both and reflects file writes immediately.
            StateLookup = new StubProxyStateLookup
            {
                LoadRoutesFromFiles = () => LoadSeedFiles().Routes
                    .Concat(ProxyConfigService.LoadOverlay(DataDirectory).Routes).ToList(),
                LoadClustersFromFiles = () => LoadSeedFiles().Clusters
                    .Concat(ProxyConfigService.LoadOverlay(DataDirectory).Clusters).ToList(),
            };
        }

        Service = new ProxyConfigService(
            configuration,
            environment,
            Provider,
            Validator,
            StateLookup,
            attachMode,
            NullLogger<ProxyConfigService>.Instance,
            CreateLocalizer());
        return Service;
    }

    public ProxyConfigDocument LoadSeedFiles() =>
        ProxyConfigService.LoadSeed(DataDirectory, ContentRoot, EnvironmentName);

    public void Dispose()
    {
        _serviceProvider?.Dispose();
        Root?.Dispose();
    }
}

/// <summary>Per-scenario state for SqliteRequestLogStore tests.</summary>
internal sealed class RequestLogTestContext : IDisposable
{
    public TempDir? Root { get; private set; }
    public string DatabasePath { get; private set; } = "";
    public SqliteRequestLogStore? Store { get; set; }
    public RequestLogStats? Stats { get; set; }
    public IReadOnlyList<RequestLogEntry> Entries { get; set; } = [];
    public long? QueryTotal { get; set; }

    public SqliteRequestLogStore CreateStore(int defaultRetentionDays)
    {
        Root ??= new TempDir();
        DatabasePath = Path.Combine(Root.Path, SqliteRequestLogStore.DatabaseFileName);
        Store = new SqliteRequestLogStore(DatabasePath, defaultRetentionDays, NullLogger<SqliteRequestLogStore>.Instance);
        return Store;
    }

    public void Dispose() => Root?.Dispose();
}

/// <summary>Per-scenario state for HTTP-level tests against the in-memory UI app.</summary>
internal sealed class ApiTestContext : IDisposable
{
    public TestWebApp? App { get; set; }
    public HttpResponseMessage? Response { get; set; }
    public string? Body { get; set; }
    public JsonDocument? Json { get; set; }

    public JsonElement JsonRoot
    {
        get
        {
            Assert.NotNull(Json);
            return Json.RootElement;
        }
    }

    public void Dispose() => App?.Dispose();
}
