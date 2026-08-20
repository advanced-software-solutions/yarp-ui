using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Reqnroll;
using YARPASUI.Tests.Support;
using YARPUI.Services;

namespace YARPASUI.Tests.StepDefinitions;

/// <summary>Steps around SqliteRequestLogStore: capture, persistence, performance stats and retention.</summary>
[Binding]
internal sealed class RequestLogSteps(RequestLogTestContext ctx)
{
    // ---- given ----

    [Given("a request log store with default retention {int} days")]
    public void GivenStore(int defaultRetentionDays)
    {
        ctx.CreateStore(defaultRetentionDays);
    }

    [Given("these proxied requests were captured")]
    public void GivenCapturedRequests(Table table)
    {
        foreach (var row in table.Rows)
        {
            ctx.Store!.Add(
                row["Method"],
                row["Path"],
                ParseStatus(row["Status"]),
                double.Parse(row["DurationMs"]),
                NullIfDash(row["RouteId"]),
                clusterId: null,
                destinationId: null,
                destinationAddress: null,
                error: null);
        }
    }

    [Given("{int} entries from {int} days ago were written directly to the database")]
    public void GivenOldEntriesWritten(int count, int daysAgo)
    {
        var timestamp = DateTimeOffset.UtcNow.AddDays(-daysAgo).ToUnixTimeMilliseconds();
        for (var i = 0; i < count; i++)
        {
            InsertRaw(ctx.DatabasePath, timestamp + i, "GET", "/old", 200, 100, "legacyRoute");
        }
    }

    // ---- when ----

    [When("the pending entries are flushed")]
    public async Task WhenFlushed()
    {
        await ctx.Store!.FlushPendingAsync(CancellationToken.None);
    }

    [When("all entries are read")]
    public void WhenAllEntriesRead()
    {
        ctx.Entries = ctx.Store!.GetAfter(0);
    }

    [When("the entries are read after sequence {long}")]
    public void WhenEntriesReadAfter(long after)
    {
        ctx.Entries = ctx.Store!.GetAfter(after);
    }

    [When("the store is reopened with default retention {int} days")]
    public void WhenStoreReopened(int defaultRetentionDays)
    {
        ctx.Store = new SqliteRequestLogStore(ctx.DatabasePath, defaultRetentionDays, NullLogger<SqliteRequestLogStore>.Instance);
    }

    [When("the log is cleared")]
    public void WhenCleared()
    {
        ctx.Store!.Clear();
    }

    [When("stats are computed over the last {int} minutes")]
    public void WhenStatsWindow(int minutes)
    {
        ctx.Stats = ctx.Store!.GetStats(TimeSpan.FromMinutes(minutes));
    }

    [When("stats are computed over all time")]
    public void WhenStatsAllTime()
    {
        ctx.Stats = ctx.Store!.GetStats(null);
    }

    [When("the retention policy is set to {int} days")]
    public void WhenRetentionSet(int days)
    {
        ctx.Store!.SetRetentionDays(days);
    }

    [When("retention is applied")]
    public void WhenRetentionApplied()
    {
        ctx.Store!.ApplyRetention();
    }

    // ---- then ----

    [Then("the returned entries are")]
    public void ThenEntriesAre(Table table)
    {
        Assert.Equal(table.Rows.Count, ctx.Entries.Count);
        for (var i = 0; i < table.Rows.Count; i++)
        {
            var row = table.Rows[i];
            var entry = ctx.Entries[i];
            Assert.Equal(row["Method"], entry.Method);
            Assert.Equal(row["Path"], entry.Path);
            Assert.Equal(ParseStatus(row["Status"]), entry.StatusCode);
            Assert.Equal(double.Parse(row["DurationMs"]), entry.DurationMs, precision: 6);
            Assert.Equal(NullIfDash(row["RouteId"]), entry.RouteId);
        }
    }

    [Then("the returned entries count is {int}")]
    public void ThenEntriesCount(int count)
    {
        Assert.Equal(count, ctx.Entries.Count);
    }

    [Then("the database contains {int} entries")]
    public void ThenDatabaseContains(int count)
    {
        Assert.Equal(count, ctx.Store!.GetAfter(0).Count);
    }

    [Then("the entries are ordered by ascending sequence numbers")]
    public void ThenEntriesOrdered()
    {
        Assert.Equal(ctx.Entries.Select(e => e.Seq).Order().ToList(), ctx.Entries.Select(e => e.Seq).ToList());
    }

    [Then("the stats summary shows")]
    public void ThenStatsSummary(Table table)
    {
        var row = table.Rows[0];
        var summary = ctx.Stats!.Summary;
        Assert.Equal(long.Parse(row["Count"]), summary.Count);
        Assert.Equal(long.Parse(row["Errors"]), summary.ErrorCount);
        Assert.Equal(double.Parse(row["AvgMs"]), summary.AvgMs, precision: 2);
        Assert.Equal(double.Parse(row["MaxMs"]), summary.MaxMs, precision: 6);
    }

    [Then("the stats percentiles are P50 {double} ms, P95 {double} ms and P99 {double} ms")]
    public void ThenStatsPercentiles(double p50, double p95, double p99)
    {
        var summary = ctx.Stats!.Summary;
        Assert.Equal(p50, summary.P50Ms, precision: 6);
        Assert.Equal(p95, summary.P95Ms, precision: 6);
        Assert.Equal(p99, summary.P99Ms, precision: 6);
    }

    [Then("the stats route aggregates are")]
    public void ThenStatsRoutes(Table table)
    {
        var routes = ctx.Stats!.Routes;
        Assert.Equal(table.Rows.Count, routes.Count);
        for (var i = 0; i < table.Rows.Count; i++)
        {
            Assert.Equal(table.Rows[i]["RouteId"], routes[i].RouteId);
            Assert.Equal(long.Parse(table.Rows[i]["Count"]), routes[i].Count);
            Assert.Equal(long.Parse(table.Rows[i]["Errors"]), routes[i].ErrorCount);
        }
    }

    [Then("the retention policy is {int} days")]
    public void ThenRetentionIs(int days)
    {
        Assert.Equal(days, ctx.Store!.GetRetentionDays());
    }

    // ---- helpers ----

    private static int? ParseStatus(string value) =>
        value is "-" or "" ? null : int.Parse(value);

    private static string? NullIfDash(string value) =>
        value is "-" or "" ? null : value;

    /// <summary>Inserts a row bypassing the store, so scenarios can control the timestamp (age).</summary>
    private static void InsertRaw(string databasePath, long timestampMs, string method, string path, int? status, double durationMs, string? routeId)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO request_logs (timestamp_ms, method, path, status_code, duration_ms, route_id)
            VALUES (@timestamp, @method, @path, @status, @duration, @route)
            """;
        command.Parameters.AddWithValue("@timestamp", timestampMs);
        command.Parameters.AddWithValue("@method", method);
        command.Parameters.AddWithValue("@path", path);
        command.Parameters.AddWithValue("@status", status.HasValue ? status.Value : DBNull.Value);
        command.Parameters.AddWithValue("@duration", durationMs);
        command.Parameters.AddWithValue("@route", (object?)routeId ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
}
