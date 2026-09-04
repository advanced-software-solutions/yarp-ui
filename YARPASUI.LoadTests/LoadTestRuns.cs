using System.Text.Json;
using YARPASUI.LoadTests.Scenarios;
using YARPASUI.LoadTests.Support;

namespace YARPASUI.LoadTests;

/// <summary>
/// The load-test entry points. Both facts share one collection so NBomber sessions
/// never overlap. The default profile is <c>smoke</c> (fast, one short level);
/// <c>YARP_LOADTEST_PROFILE=full</c> runs the 10/25/50-session matrix that produces
/// the charts committed under docs/load-tests/.
///
/// Constraints encoded in the harness: at most 50 concurrent sessions (closed model,
/// <c>KeepConstant</c>) with aggregate throughput reported honestly as whatever those
/// sessions sustain — the 10k req/s figure is the target, not an assumption.
/// </summary>
[Collection("load-tests")]
public sealed class LoadTestRuns
{
    [Fact]
    public async Task SmokeLoadRun()
    {
        var profile = LoadTestProfile.Current;
        if (profile.IsFull)
        {
            Assert.Skip("YARP_LOADTEST_PROFILE=full — the full run already covers this path.");
        }

        if (!profile.UpstreamMode.Equals("local", StringComparison.OrdinalIgnoreCase)
            && !UpstreamBackend.IsEngineAvailable(out var engineReason))
        {
            Assert.Skip($"No container engine available: {engineReason}");
        }

        await RunAsync(profile);
    }

    [Fact]
    public async Task FullLoadRun()
    {
        var profile = LoadTestProfile.Current;
        if (!profile.IsFull)
        {
            Assert.Skip(
                $"YARP_LOADTEST_PROFILE is '{profile.Name}'. " +
                "Set YARP_LOADTEST_PROFILE=full for the full run (~15 min, writes docs/load-tests/).");
        }

        await RunAsync(profile);
    }

    private static async Task RunAsync(LoadTestProfile profile)
    {
        await using var upstream = await UpstreamBackend.StartAsync(profile);
        await using var app = await ProxyTestApp.StartAsync(upstream.BaseUrl);
        await Task.Delay(profile.AppWarmUp);

        using var client = LoadScenarios.CreateLoadClient();

        var reportRoot = Path.Combine(RepoPaths.ReportsRoot, $"{profile.Name}-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(reportRoot);

        var definitions = BuildDefinitions(profile, upstream.BaseUrl, app.BaseUrl);
        var results = new List<LevelResult>();

        foreach (var (name, url, isPost) in definitions)
        {
            foreach (var copies in profile.ConcurrencyLevels)
            {
                Console.WriteLine($"→ {name} @ {copies} sessions…");
                var result = LoadScenarios.RunLevel(
                    createScenario: () => isPost
                        ? LoadScenarios.CreatePostScenario(name, client, url)
                        : LoadScenarios.CreateGetScenario(name, client, url),
                    scenarioName: name,
                    copies: copies,
                    profile: profile,
                    reportFolder: Path.Combine(reportRoot, $"{name}-c{copies}"));
                results.Add(result);

                Console.WriteLine(
                    $"  {result.Rps:F0} req/s, p95 {result.P95Ms:F1} ms, errors {result.FailCount}");

                Assert.True(result.OkCount > 0, $"{name} @ {copies}: no successful requests");
                Assert.True(result.FailCount == 0,
                    $"{name} @ {copies}: {result.FailCount} failed requests (expected 0)");
            }
        }

        if (profile.IsFull)
        {
            // Persist the raw level results so charts can be re-rendered later
            // (YARP_LOADTEST_RENDER_FROM) without repeating the ~18-minute run.
            var resultsPath = Path.Combine(reportRoot, "results.json");
            await File.WriteAllTextAsync(
                resultsPath,
                JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));

            ChartRenderer.RenderAll(results, RepoPaths.DocsLoadTests, upstream.Kind, profile);
            CopyHeadlineReport(reportRoot, profile);
            Console.WriteLine($"Charts and results written to {RepoPaths.DocsLoadTests}");
        }
    }

    /// <summary>
    /// Re-renders the README charts from a saved results.json
    /// (YARP_LOADTEST_RENDER_FROM=&lt;path&gt;) — chart tweaks without a new load run.
    /// </summary>
    [Fact]
    public async Task RenderChartsFromSavedResults()
    {
        var path = Environment.GetEnvironmentVariable("YARP_LOADTEST_RENDER_FROM");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Assert.Skip("Set YARP_LOADTEST_RENDER_FROM=<results.json> to re-render charts from a saved run.");
        }

        var results = JsonSerializer.Deserialize<List<LevelResult>>(await File.ReadAllTextAsync(path));
        Assert.NotNull(results);
        Assert.NotEmpty(results);

        ChartRenderer.RenderCharts(results!, RepoPaths.DocsLoadTests);
        Console.WriteLine($"Charts re-rendered to {RepoPaths.DocsLoadTests}");
    }

    private static (string Name, string Url, bool IsPost)[] BuildDefinitions(
        LoadTestProfile profile, string upstreamUrl, string appUrl) =>
        profile.IsFull
            ?
            [
                (LoadScenarios.DirectGet, $"{upstreamUrl}/loadtest", false),
                (LoadScenarios.DirectPost, $"{upstreamUrl}/loadtest", true),
                (LoadScenarios.ProxyGet, $"{appUrl}/echo/loadtest", false),
                (LoadScenarios.ProxyPost, $"{appUrl}/echo-post/loadtest", true),
            ]
            :
            [
                (LoadScenarios.ProxyGet, $"{appUrl}/echo/loadtest", false),
            ];

    /// <summary>Publishes the headline interactive NBomber report (proxy GET at the highest level) next to the charts.</summary>
    private static void CopyHeadlineReport(string reportRoot, LoadTestProfile profile)
    {
        var headlineDir = Path.Combine(reportRoot, $"{LoadScenarios.ProxyGet}-c{profile.ConcurrencyLevels.Max()}");
        var html = Directory.EnumerateFiles(headlineDir, "*.html", SearchOption.AllDirectories).FirstOrDefault();
        if (html is not null)
        {
            Directory.CreateDirectory(RepoPaths.DocsLoadTests);
            File.Copy(html, Path.Combine(RepoPaths.DocsLoadTests, "report.html"), overwrite: true);
        }
    }
}
