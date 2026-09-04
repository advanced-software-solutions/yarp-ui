using System.Text;
using NBomber.Contracts;
using NBomber.Contracts.Stats;
using NBomber.CSharp;
using NBomber.Http.CSharp;
using YARPASUI.LoadTests.Support;

namespace YARPASUI.LoadTests.Scenarios;

/// <summary>
/// NBomber scenario definitions. Four scenarios:
///  - <see cref="DirectGet"/>: GET straight to the upstream — the no-proxy baseline
///    that quantifies how much overhead YARP UI adds;
///  - <see cref="DirectPost"/>: POST straight to the upstream — same baseline for
///    the request-body path;
///  - <see cref="ProxyGet"/>: GET through the proxy's /echo route (hot path incl. request logging);
///  - <see cref="ProxyPost"/>: POST with a small JSON body through /echo-post.
///
/// Load model is closed (<c>Simulation.KeepConstant</c>): concurrency never exceeds
/// the level's session count (cap 50 by requirement), and throughput is whatever
/// those sessions actually sustain.
/// </summary>
internal static class LoadScenarios
{
    public const string DirectGet = "direct_get";
    public const string DirectPost = "direct_post";
    public const string ProxyGet = "proxy_get";
    public const string ProxyPost = "proxy_post";

    /// <summary>Shared client for all scenarios; connection pool mirrors the 50-session cap.</summary>
    public static HttpClient CreateLoadClient() => new(new SocketsHttpHandler
    {
        MaxConnectionsPerServer = 60, // a little headroom over the 50-session cap
        UseProxy = false,
        AllowAutoRedirect = false,
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(10),
    }, disposeHandler: true);

    public static ScenarioProps CreateGetScenario(string name, HttpClient client, string url) =>
        Scenario.Create(name, async context =>
        {
            var request = Http.CreateRequest("GET", url).WithHeader("Accept", "text/plain");
            return await Http.Send(client, request);
        });

    public static ScenarioProps CreatePostScenario(string name, HttpClient client, string url) =>
        Scenario.Create(name, async context =>
        {
            var request = Http.CreateRequest("POST", url)
                .WithHeader("Content-Type", "application/json")
                .WithBody(new StringContent("""{"payload":"yarp-ui-load-test"}""", Encoding.UTF8, "application/json"));
            return await Http.Send(client, request);
        });

    /// <summary>Runs one scenario at one concurrency level and reduces the stats.</summary>
    public static LevelResult RunLevel(
        Func<ScenarioProps> createScenario,
        string scenarioName,
        int copies,
        LoadTestProfile profile,
        string reportFolder)
    {
        var scenario = createScenario()
            .WithWarmUpDuration(profile.WarmUp)
            .WithLoadSimulations(
                Simulation.RampingConstant(copies: copies, during: profile.Ramp),
                Simulation.KeepConstant(copies: copies, during: profile.Sustain));

        var stats = NBomberRunner
            .RegisterScenarios(scenario)
            .WithTestSuite("yarp-ui-load-tests")
            .WithTestName($"{scenarioName} @ {copies} concurrent sessions")
            .WithReportFolder(reportFolder)
            .WithReportFormats(ReportFormat.Html, ReportFormat.Md, ReportFormat.Csv)
            .WithReportingInterval(TimeSpan.FromSeconds(10))
            .DisplayConsoleMetrics(false)
            .Run();

        return ParseStats(stats, scenarioName, copies);
    }

    private static LevelResult ParseStats(NodeStats stats, string scenarioName, int copies)
    {
        // NBomber v6 keeps scenario-level ok/fail stats (MeasurementsStats) with the
        // implicit step aggregated in — explicit Step.Run steps are not needed here.
        var scenario = stats.ScenarioStats.Single(s => s.ScenarioName == scenarioName);
        var ok = scenario.Ok.Request.Count;
        var fail = scenario.Fail.Request.Count;
        var latency = scenario.Ok.Latency;

        return new LevelResult(
            Scenario: scenarioName,
            Copies: copies,
            Rps: scenario.Ok.Request.RPS,
            P50Ms: latency.Percent50,
            P95Ms: latency.Percent95,
            P99Ms: latency.Percent99,
            OkCount: ok,
            FailCount: fail,
            DurationSec: scenario.Duration.TotalSeconds);
    }
}
