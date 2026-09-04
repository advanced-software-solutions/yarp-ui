using System.Globalization;
using System.Runtime.InteropServices;
using ScottPlot;
using YARPASUI.LoadTests.Scenarios;

namespace YARPASUI.LoadTests.Support;

/// <summary>
/// Turns the measured <see cref="LevelResult"/>s into the artifacts committed under
/// docs/load-tests/: a throughput chart, a latency chart and a RESULTS.md summary.
/// ScottPlot keeps this pure .NET — no Python or JS toolchain needed to re-render.
/// </summary>
internal static class ChartRenderer
{
    private static readonly (string Name, string Label, ScottPlot.Color Color)[] SeriesOrder =
    [
        (LoadScenarios.DirectGet, "Direct GET (no proxy)", ScottPlot.Color.FromHex("#B8B8B8")),
        (LoadScenarios.DirectPost, "Direct POST (no proxy)", ScottPlot.Color.FromHex("#6E6E6E")),
        (LoadScenarios.ProxyGet, "GET via YARP UI", ScottPlot.Color.FromHex("#4682B4")),
        (LoadScenarios.ProxyPost, "POST via YARP UI", ScottPlot.Color.FromHex("#E08214")),
    ];

    public static void RenderAll(IReadOnlyList<LevelResult> results, string outDir, string upstreamKind, LoadTestProfile profile)
    {
        Directory.CreateDirectory(outDir);
        RenderThroughput(results, Path.Combine(outDir, "throughput.png"));
        RenderLatency(results, Path.Combine(outDir, "latency.png"));
        WriteResultsMarkdown(results, outDir, upstreamKind, profile);
    }

    /// <summary>Chart-only variant for re-rendering saved results without touching RESULTS.md.</summary>
    public static void RenderCharts(IReadOnlyList<LevelResult> results, string outDir)
    {
        Directory.CreateDirectory(outDir);
        RenderThroughput(results, Path.Combine(outDir, "throughput.png"));
        RenderLatency(results, Path.Combine(outDir, "latency.png"));
    }

    private static void RenderThroughput(IReadOnlyList<LevelResult> results, string path)
    {
        var levels = results.Select(r => r.Copies).Distinct().OrderBy(x => x).ToArray();
        var series = SeriesOrder.Where(s => results.Any(r => r.Scenario == s.Name)).ToArray();

        var plt = new Plot();
        plt.Title("YARP UI — sustained throughput by concurrency");
        plt.YLabel("requests / second");

        var groupWidth = series.Length + 1;
        var centers = new double[levels.Length];

        for (var j = 0; j < series.Length; j++)
        {
            var xs = new double[levels.Length];
            var ys = new double[levels.Length];
            for (var i = 0; i < levels.Length; i++)
            {
                xs[i] = i * groupWidth + j;
                centers[i] = i * groupWidth + (series.Length - 1) / 2.0;
                ys[i] = results.First(r => r.Scenario == series[j].Name && r.Copies == levels[i]).Rps;
            }

            var bars = plt.Add.Bars(xs, ys);
            bars.LegendText = series[j].Label;
            bars.Color = series[j].Color;
            for (var i = 0; i < bars.Bars.Count; i++)
            {
                bars.Bars[i].Label = ys[i].ToString("#,##0", CultureInfo.InvariantCulture);
            }

            bars.LabelsOnTop = true;
        }

        plt.Axes.SetLimits(bottom: 0);
        plt.Axes.Bottom.SetTicks(
            centers,
            levels.Select(c => $"{c} sessions").ToArray());
        plt.ShowLegend(Alignment.UpperLeft);
        plt.SavePng(path, 1000, 560);
    }

    private static void RenderLatency(IReadOnlyList<LevelResult> results, string path)
    {
        var proxy = results.Where(r => r.Scenario == LoadScenarios.ProxyGet).ToList();
        if (proxy.Count == 0)
        {
            return;
        }

        var levels = proxy.Select(r => r.Copies).Distinct().OrderBy(x => x).ToArray();
        var percentiles = new (Func<LevelResult, double> Value, string Label, ScottPlot.Color Color)[]
        {
            (r => r.P50Ms, "p50", ScottPlot.Color.FromHex("#2E8B57")),
            (r => r.P95Ms, "p95", ScottPlot.Color.FromHex("#D4A017")),
            (r => r.P99Ms, "p99", ScottPlot.Color.FromHex("#CD5C5C")),
        };

        var plt = new Plot();
        plt.Title("YARP UI — proxy GET latency by concurrency");
        plt.YLabel("milliseconds");

        var groupWidth = percentiles.Length + 1;
        var centers = new double[levels.Length];

        for (var j = 0; j < percentiles.Length; j++)
        {
            var p = percentiles[j];
            var xs = new double[levels.Length];
            var ys = new double[levels.Length];
            for (var i = 0; i < levels.Length; i++)
            {
                xs[i] = i * groupWidth + j;
                centers[i] = i * groupWidth + (percentiles.Length - 1) / 2.0;
                ys[i] = percentiles[j].Value(proxy.First(x => x.Copies == levels[i]));
            }

            var bars = plt.Add.Bars(xs, ys);
            bars.LegendText = percentiles[j].Label;
            bars.Color = percentiles[j].Color;
            for (var i = 0; i < bars.Bars.Count; i++)
            {
                bars.Bars[i].Label = ys[i].ToString("F1", CultureInfo.InvariantCulture);
            }

            bars.LabelsOnTop = true;
        }

        plt.Axes.SetLimits(bottom: 0);
        plt.Axes.Bottom.SetTicks(
            centers,
            levels.Select(c => $"{c} sessions").ToArray());
        plt.ShowLegend(Alignment.UpperLeft);
        plt.SavePng(path, 1000, 560);
    }

    private static void WriteResultsMarkdown(
        IReadOnlyList<LevelResult> results,
        string outDir,
        string upstreamKind,
        LoadTestProfile profile)
    {
        var ram = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024 * 1024);
        using var writer = new StreamWriter(Path.Combine(outDir, "RESULTS.md"));

        writer.WriteLine("# YARP UI load test results");
        writer.WriteLine();
        writer.WriteLine($"- **Date**: {DateTime.Now:yyyy-MM-dd HH:mm}");
        writer.WriteLine($"- **Upstream**: {upstreamKind}");
        writer.WriteLine("- **App under test**: in-process Kestrel — the exact `YARPUI.Host` pipeline (`AddYarpUi` + `UseYarpUiRequestLogging` + `MapYarpUi` + `MapReverseProxy`), request logging active");
        writer.WriteLine($"- **Machine**: {RuntimeInformation.OSDescription}, {Environment.ProcessorCount} logical processors, {ram:F0} GiB RAM");
        writer.WriteLine($"- **Load model**: closed — NBomber `KeepConstant`, warm-up {profile.WarmUp.TotalSeconds:F0}s, ramp {profile.Ramp.TotalSeconds:F0}s, sustain {profile.Sustain.TotalSeconds:F0}s per level (levels: {string.Join(", ", profile.ConcurrencyLevels)})");
        writer.WriteLine();
        writer.WriteLine("| Scenario | Sessions | Req/s | p50 (ms) | p95 (ms) | p99 (ms) | Errors |");
        writer.WriteLine("| --- | --- | ---: | ---: | ---: | ---: | ---: |");

        foreach (var r in results.OrderBy(r => r.Scenario).ThenBy(r => r.Copies))
        {
            writer.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "| {0} | {1} | {2:F0} | {3:F1} | {4:F1} | {5:F1} | {6} |",
                DisplayName(r.Scenario), r.Copies, r.Rps, r.P50Ms, r.P95Ms, r.P99Ms, r.FailCount));
        }
    }

    private static string DisplayName(string scenario) => scenario switch
    {
        LoadScenarios.DirectGet => "Direct GET (no proxy)",
        LoadScenarios.DirectPost => "Direct POST (no proxy)",
        LoadScenarios.ProxyGet => "GET via YARP UI",
        LoadScenarios.ProxyPost => "POST via YARP UI",
        _ => scenario,
    };
}
