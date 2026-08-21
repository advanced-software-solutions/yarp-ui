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

    // The When alias serves scenarios that capture entries after reopening the store.
    [Given("these proxied requests were captured")]
    [When("these proxied requests were captured")]
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
                OptionalCell(row, "ClusterId"),
                OptionalCell(row, "DestinationId"),
                destinationAddress: null,
                error: null,
                clientIp: OptionalCell(row, "ClientIp"));
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

    /// <summary>Reverts the database to the pre-0.3.0 schema so migrations can be exercised.</summary>
    [Given("a legacy log database without the client IP column exists")]
    public void GivenLegacyDatabase()
    {
        // Drop the column rather than recreating the file — pooled SQLite connections
        // keep the database file locked for the rest of the scenario.
        using var connection = new SqliteConnection($"Data Source={ctx.DatabasePath}");
        connection.Open();
        ExecuteRaw(connection, "ALTER TABLE request_logs DROP COLUMN client_ip");
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO request_logs (timestamp_ms, method, path, status_code, duration_ms, route_id)
                VALUES (@timestamp, @method, @path, @status, @duration, @route)
                """;
            command.Parameters.AddWithValue("@timestamp", DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("@method", "GET");
            command.Parameters.AddWithValue("@path", "/legacy");
            command.Parameters.AddWithValue("@status", 200);
            command.Parameters.AddWithValue("@duration", 25);
            command.Parameters.AddWithValue("@route", "legacyRoute");
            command.ExecuteNonQuery();
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

    [When("the entries are queried")]
    public void WhenQueried()
    {
        RunQuery(new RequestLogQuery());
    }

    [When("the entries are queried with route {string}")]
    public void WhenQueriedByRoute(string routeId)
    {
        RunQuery(new RequestLogQuery { RouteId = routeId });
    }

    [When("the entries are queried with cluster {string}")]
    public void WhenQueriedByCluster(string clusterId)
    {
        RunQuery(new RequestLogQuery { ClusterId = clusterId });
    }

    [When("the entries are queried with destination {string}")]
    public void WhenQueriedByDestination(string destinationId)
    {
        RunQuery(new RequestLogQuery { DestinationId = destinationId });
    }

    [When("the entries are queried between {int} days ago and {int} days ago")]
    public void WhenQueriedBetween(int fromDaysAgo, int toDaysAgo)
    {
        var now = DateTimeOffset.UtcNow;
        RunQuery(new RequestLogQuery
        {
            FromMs = now.AddDays(-fromDaysAgo).ToUnixTimeMilliseconds(),
            ToMs = now.AddDays(-toDaysAgo).ToUnixTimeMilliseconds(),
        });
    }

    [When("the entries are queried sorted by {word} {word}")]
    public void WhenQueriedSorted(string field, string direction)
    {
        RunQuery(new RequestLogQuery { Sort = field, Descending = direction is "descending" or "desc" });
    }

    [When("the entries are queried with limit {int}")]
    public void WhenQueriedWithLimit(int limit)
    {
        RunQuery(new RequestLogQuery { Limit = limit });
    }

    private void RunQuery(RequestLogQuery query)
    {
        var result = ctx.Store!.Query(query);
        ctx.Entries = result.Entries;
        ctx.QueryTotal = result.Total;
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
            if (row.ContainsKey("Method"))
            {
                Assert.Equal(row["Method"], entry.Method);
            }
            if (row.ContainsKey("Path"))
            {
                Assert.Equal(row["Path"], entry.Path);
            }
            if (row.ContainsKey("Status"))
            {
                Assert.Equal(ParseStatus(row["Status"]), entry.StatusCode);
            }
            if (row.ContainsKey("DurationMs"))
            {
                Assert.Equal(double.Parse(row["DurationMs"]), entry.DurationMs, precision: 6);
            }
            if (row.ContainsKey("RouteId"))
            {
                Assert.Equal(NullIfDash(row["RouteId"]), entry.RouteId);
            }
            if (row.ContainsKey("ClusterId"))
            {
                Assert.Equal(NullIfDash(row["ClusterId"]), entry.ClusterId);
            }
            if (row.ContainsKey("DestinationId"))
            {
                Assert.Equal(NullIfDash(row["DestinationId"]), entry.DestinationId);
            }
            if (row.ContainsKey("ClientIp"))
            {
                Assert.Equal(NullIfDash(row["ClientIp"]), entry.ClientIp);
            }
        }
    }

    [Then("the returned entries count is {int}")]
    public void ThenEntriesCount(int count)
    {
        Assert.Equal(count, ctx.Entries.Count);
    }

    [Then("the query total is {long}")]
    public void ThenQueryTotal(long total)
    {
        Assert.Equal(total, ctx.QueryTotal);
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

    private static string? OptionalCell(DataTableRow row, string name) =>
        row.TryGetValue(name, out var value) ? NullIfDash(value) : null;

    private static void ExecuteRaw(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

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
