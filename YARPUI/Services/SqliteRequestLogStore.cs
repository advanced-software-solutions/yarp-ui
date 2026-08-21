using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace YARPUI.Services;

/// <summary>A single proxied request captured by <see cref="RequestLoggingMiddleware"/>.</summary>
public sealed record RequestLogEntry
{
    public long Seq { get; init; }
    public DateTime TimestampUtc { get; init; }
    public string Method { get; init; } = "";
    public string Path { get; init; } = "";
    public int? StatusCode { get; init; }
    public double DurationMs { get; init; }
    public string? RouteId { get; init; }
    public string? ClusterId { get; init; }
    public string? DestinationId { get; init; }
    public string? DestinationAddress { get; init; }
    public string? Error { get; init; }
    public string? ClientIp { get; init; }
}

/// <summary>History search parameters for the Logs page; all filters are optional.</summary>
public sealed record RequestLogQuery
{
    public long? FromMs { get; init; }
    public long? ToMs { get; init; }
    public string? RouteId { get; init; }
    public string? ClusterId { get; init; }
    public string? DestinationId { get; init; }
    public string Sort { get; init; } = "timestamp";
    public bool Descending { get; init; } = true;
    public int Limit { get; init; } = 500;
}

public sealed record RequestLogQueryResult(IReadOnlyList<RequestLogEntry> Entries, long Total);

/// <summary>Aggregated performance over a time window, served to the Logs performance panel.</summary>
public sealed record RequestLogStats(
    RequestLogStatsSummary Summary,
    IReadOnlyList<RequestLogRouteStats> Routes,
    IReadOnlyList<RequestLogBucket> Buckets);

public sealed record RequestLogStatsSummary(
    long Count,
    long ErrorCount,
    double AvgMs,
    double P50Ms,
    double P95Ms,
    double P99Ms,
    double MaxMs);

public sealed record RequestLogRouteStats(
    string RouteId,
    long Count,
    long ErrorCount,
    double AvgMs,
    double P95Ms,
    double MaxMs);

public sealed record RequestLogBucket(
    long StartMs,
    long Count,
    long ErrorCount,
    double AvgMs,
    double MaxMs);

/// <summary>
/// Stores proxied requests in a SQLite database (yarp-ui-logs.db in the data directory) so
/// they survive restarts and can be aggregated. Captures are queued to an in-memory channel
/// and written to disk in batches by <see cref="RequestLogWriter"/> — the request path never
/// touches the database. A retention policy (keep logs for N days, 0 = forever) is enforced
/// by <see cref="LogRetentionService"/> and can be changed live from the Logs page; the
/// saved value lives in the database's settings table.
/// </summary>
public sealed class SqliteRequestLogStore
{
    public const string DatabaseFileName = "yarp-ui-logs.db";
    public const int MaxRetentionDays = 3650;

    private const string RetentionDaysKey = "logRetentionDays";
    private const int ChannelCapacity = 10_000;
    private const int BatchSize = 500;
    private const int MaxPerFlush = 5_000;
    private const int MaxRoutesInStats = 20;
    public const int MaxQueryLimit = 1000;

    private readonly string _databasePath;
    private readonly int _defaultRetentionDays;
    private readonly ILogger<SqliteRequestLogStore> _logger;
    private readonly Channel<RequestLogEntry> _pending;

    public SqliteRequestLogStore(string databasePath, int defaultRetentionDays, ILogger<SqliteRequestLogStore> logger)
    {
        _databasePath = databasePath;
        _defaultRetentionDays = defaultRetentionDays;
        _logger = logger;

        _pending = Channel.CreateBounded<RequestLogEntry>(new BoundedChannelOptions(ChannelCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        InitializeDatabase();
    }

    // ---- capture ----

    /// <summary>Queues a captured request; the actual insert happens on the background writer.</summary>
    public void Add(
        string method,
        string path,
        int? statusCode,
        double durationMs,
        string? routeId,
        string? clusterId,
        string? destinationId,
        string? destinationAddress,
        string? error,
        string? clientIp = null)
    {
        _pending.Writer.TryWrite(new RequestLogEntry
        {
            TimestampUtc = DateTime.UtcNow,
            Method = method,
            Path = path,
            StatusCode = statusCode,
            DurationMs = durationMs,
            RouteId = routeId,
            ClusterId = clusterId,
            DestinationId = destinationId,
            DestinationAddress = destinationAddress,
            Error = error,
            ClientIp = clientIp,
        });
    }

    /// <summary>Inserts everything currently queued, in batches; returns the number of entries written.</summary>
    public async Task<int> FlushPendingAsync(CancellationToken cancellationToken)
    {
        var total = 0;
        var batch = new List<RequestLogEntry>(BatchSize);
        while (total < MaxPerFlush)
        {
            batch.Clear();
            while (batch.Count < BatchSize && _pending.Reader.TryRead(out var entry))
            {
                batch.Add(entry);
            }

            if (batch.Count == 0)
            {
                break;
            }

            await InsertBatchAsync(batch, cancellationToken);
            total += batch.Count;
            if (batch.Count < BatchSize)
            {
                break;
            }
        }

        return total;
    }

    // ---- reads ----

    /// <summary>Entries with <c>seq &gt; afterSeq</c>, oldest first (capped so one poll stays cheap).</summary>
    public IReadOnlyList<RequestLogEntry> GetAfter(long afterSeq)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT seq, timestamp_ms, method, path, status_code, duration_ms,
                   route_id, cluster_id, destination_id, destination_address, error, client_ip
            FROM request_logs
            WHERE seq > @after
            ORDER BY seq
            LIMIT 500
            """;
        command.Parameters.AddWithValue("@after", afterSeq);

        var entries = new List<RequestLogEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(ReadEntry(reader));
        }

        return entries;
    }

    // Whitelisted sort fields for Query — the values are the only SQL fragments built from user input.
    private static readonly Dictionary<string, string> SortColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["timestamp"] = "timestamp_ms",
        ["duration"] = "duration_ms",
        ["status"] = "status_code",
        ["method"] = "method",
        ["path"] = "path",
        ["route"] = "route_id",
        ["cluster"] = "cluster_id",
        ["destination"] = "destination_id",
        ["clientIp"] = "client_ip",
    };

    public static bool IsValidSortField(string? sort)
        => sort is not null && SortColumns.ContainsKey(sort);

    public static string SortFields => string.Join(", ", SortColumns.Keys);

    /// <summary>
    /// History search over stored entries (time range, route/cluster/destination filters, sort).
    /// Newest first by default; <see cref="RequestLogQueryResult.Total"/> counts every matching row.
    /// </summary>
    public RequestLogQueryResult Query(RequestLogQuery query)
    {
        if (!SortColumns.TryGetValue(query.Sort ?? "", out var sortColumn))
        {
            throw new ArgumentException($"Unknown sort field '{query.Sort}'.", nameof(query));
        }

        var conditions = new List<(string Sql, string Name, object Value)>();
        if (query.FromMs is { } from)
        {
            conditions.Add(("timestamp_ms >= @from", "@from", from));
        }
        if (query.ToMs is { } to)
        {
            conditions.Add(("timestamp_ms <= @to", "@to", to));
        }
        if (!string.IsNullOrWhiteSpace(query.RouteId))
        {
            conditions.Add(("route_id = @route", "@route", query.RouteId));
        }
        if (!string.IsNullOrWhiteSpace(query.ClusterId))
        {
            conditions.Add(("cluster_id = @cluster", "@cluster", query.ClusterId));
        }
        if (!string.IsNullOrWhiteSpace(query.DestinationId))
        {
            conditions.Add(("destination_id = @destination", "@destination", query.DestinationId));
        }

        var whereSql = conditions.Count == 0 ? "" : " WHERE " + string.Join(" AND ", conditions.Select(c => c.Sql));
        var direction = query.Descending ? "DESC" : "ASC";
        var limit = Math.Clamp(query.Limit, 1, MaxQueryLimit);

        using var connection = OpenConnection();

        long total;
        using (var count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM request_logs" + whereSql;
            foreach (var (_, name, value) in conditions)
            {
                count.Parameters.AddWithValue(name, value);
            }
            total = (long)(count.ExecuteScalar() ?? 0L);
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT seq, timestamp_ms, method, path, status_code, duration_ms,
                   route_id, cluster_id, destination_id, destination_address, error, client_ip
            FROM request_logs{whereSql}
            ORDER BY {sortColumn} {direction}, seq {direction}
            LIMIT @limit
            """;
        foreach (var (_, name, value) in conditions)
        {
            command.Parameters.AddWithValue(name, value);
        }
        command.Parameters.AddWithValue("@limit", limit);

        var entries = new List<RequestLogEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(ReadEntry(reader));
        }

        return new RequestLogQueryResult(entries, total);
    }

    private static RequestLogEntry ReadEntry(SqliteDataReader reader)
    {
        return new RequestLogEntry
        {
            Seq = reader.GetInt64(0),
            TimestampUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)).UtcDateTime,
            Method = reader.GetString(2),
            Path = reader.GetString(3),
            StatusCode = reader.IsDBNull(4) ? null : reader.GetInt32(4),
            DurationMs = reader.GetDouble(5),
            RouteId = reader.IsDBNull(6) ? null : reader.GetString(6),
            ClusterId = reader.IsDBNull(7) ? null : reader.GetString(7),
            DestinationId = reader.IsDBNull(8) ? null : reader.GetString(8),
            DestinationAddress = reader.IsDBNull(9) ? null : reader.GetString(9),
            Error = reader.IsDBNull(10) ? null : reader.GetString(10),
            ClientIp = reader.IsDBNull(11) ? null : reader.GetString(11),
        };
    }

    /// <summary>Aggregated performance for the Logs performance panel. A null window covers all time.</summary>
    public RequestLogStats GetStats(TimeSpan? window)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var cutoffMs = window is { } w ? nowMs - (long)w.TotalMilliseconds : 0L;

        using var connection = OpenConnection();

        long count;
        long errorCount;
        double avgMs;
        double maxMs;
        long minTs;
        long maxTs;

        using (var totals = connection.CreateCommand())
        {
            totals.CommandText = """
                SELECT COUNT(*), COALESCE(AVG(duration_ms), 0), COALESCE(MAX(duration_ms), 0),
                       COALESCE(SUM(CASE WHEN status_code IS NULL OR status_code >= 500 THEN 1 ELSE 0 END), 0),
                       COALESCE(MIN(timestamp_ms), 0), COALESCE(MAX(timestamp_ms), 0)
                FROM request_logs
                WHERE timestamp_ms >= @cutoff
                """;
            totals.Parameters.AddWithValue("@cutoff", cutoffMs);
            using var reader = totals.ExecuteReader();
            reader.Read();
            count = reader.GetInt64(0);
            avgMs = reader.GetDouble(1);
            maxMs = reader.GetDouble(2);
            errorCount = reader.GetInt64(3);
            minTs = reader.GetInt64(4);
            maxTs = reader.GetInt64(5);
        }

        if (count == 0)
        {
            return new RequestLogStats(
                new RequestLogStatsSummary(0, 0, 0, 0, 0, 0, 0),
                Array.Empty<RequestLogRouteStats>(),
                Array.Empty<RequestLogBucket>());
        }

        return FinishStats(
            connection,
            new RequestLogStatsSummary(count, errorCount, avgMs, 0, 0, 0, maxMs),
            cutoffMs, minTs, maxTs, window);
    }

    private RequestLogStats FinishStats(
        SqliteConnection connection,
        RequestLogStatsSummary baseSummary,
        long cutoffMs,
        long minTs,
        long maxTs,
        TimeSpan? window)
    {
        // Percentiles need sorted durations; routes need per-route durations. One ordered scan feeds both.
        var durations = new List<double>((int)Math.Min(baseSummary.Count, 100_000));
        var routes = new Dictionary<string, RouteAccumulator>(StringComparer.OrdinalIgnoreCase);

        using (var scan = connection.CreateCommand())
        {
            scan.CommandText = """
                SELECT duration_ms, status_code, COALESCE(route_id, '')
                FROM request_logs
                WHERE timestamp_ms >= @cutoff
                ORDER BY duration_ms
                """;
            scan.Parameters.AddWithValue("@cutoff", cutoffMs);
            using var reader = scan.ExecuteReader();
            while (reader.Read())
            {
                var duration = reader.GetDouble(0);
                durations.Add(duration);

                var routeId = reader.GetString(2);
                if (!routes.TryGetValue(routeId, out var route))
                {
                    route = new RouteAccumulator();
                    routes.Add(routeId, route);
                }

                route.Durations.Add(duration);
                if (reader.IsDBNull(1) || reader.GetInt32(1) >= 500)
                {
                    route.Errors++;
                }
            }
        }

        var routeStats = routes
            .Select(kv => new RequestLogRouteStats(
                kv.Key,
                kv.Value.Durations.Count,
                kv.Value.Errors,
                kv.Value.Durations.Count == 0 ? 0 : kv.Value.Durations.Average(),
                Percentile(kv.Value.Durations, 95),
                kv.Value.Durations.Count == 0 ? 0 : kv.Value.Durations.Max()))
            .OrderByDescending(r => r.P95Ms)
            .Take(MaxRoutesInStats)
            .ToList();

        var summary = baseSummary with
        {
            P50Ms = Percentile(durations, 50),
            P95Ms = Percentile(durations, 95),
            P99Ms = Percentile(durations, 99),
        };

        return new RequestLogStats(summary, routeStats, GetBuckets(connection, cutoffMs, minTs, maxTs, window));
    }

    private static IReadOnlyList<RequestLogBucket> GetBuckets(
        SqliteConnection connection, long cutoffMs, long minTs, long maxTs, TimeSpan? window)
    {
        long bucketMs;
        if (window is { } bounded)
        {
            bucketMs = bounded.TotalMinutes <= 5 ? 5_000
                : bounded.TotalMinutes <= 15 ? 10_000
                : bounded.TotalHours <= 1 ? 30_000
                : bounded.TotalHours <= 24 ? 600_000
                : 0;
        }
        else
        {
            bucketMs = 0;
        }

        if (bucketMs == 0)
        {
            bucketMs = Math.Max(1_000, (maxTs - minTs) / 100);
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (timestamp_ms / @bucket) * @bucket AS bucket_start,
                   COUNT(*), COALESCE(AVG(duration_ms), 0), COALESCE(MAX(duration_ms), 0),
                   COALESCE(SUM(CASE WHEN status_code IS NULL OR status_code >= 500 THEN 1 ELSE 0 END), 0)
            FROM request_logs
            WHERE timestamp_ms >= @cutoff
            GROUP BY bucket_start
            ORDER BY bucket_start
            """;
        command.Parameters.AddWithValue("@bucket", bucketMs);
        command.Parameters.AddWithValue("@cutoff", cutoffMs);

        var buckets = new List<RequestLogBucket>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            buckets.Add(new RequestLogBucket(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt64(4),
                reader.GetDouble(2),
                reader.GetDouble(3)));
        }

        return buckets;
    }

    /// <summary>Nearest-rank percentile over an ascending list (the list is sorted when coming from SQL).</summary>
    private static double Percentile(IReadOnlyList<double> sortedDurations, int percentile)
    {
        if (sortedDurations.Count == 0)
        {
            return 0;
        }

        var index = (int)Math.Ceiling(percentile / 100.0 * sortedDurations.Count) - 1;
        return sortedDurations[Math.Clamp(index, 0, sortedDurations.Count - 1)];
    }

    public void Clear()
    {
        using var connection = OpenConnection();
        Execute(connection, "DELETE FROM request_logs");
    }

    // ---- retention policy ----

    /// <summary>Current policy: days to keep logs (0 = forever). Saved UI values override the config default.</summary>
    public int GetRetentionDays()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = @key";
        command.Parameters.AddWithValue("@key", RetentionDaysKey);
        var result = command.ExecuteScalar();
        return result is string text && int.TryParse(text, out var days) && days >= 0
            ? Math.Min(days, MaxRetentionDays)
            : _defaultRetentionDays;
    }

    public void SetRetentionDays(int days)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO settings (key, value) VALUES (@key, @value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """;
        command.Parameters.AddWithValue("@key", RetentionDaysKey);
        command.Parameters.AddWithValue("@value", days.ToString());
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Deletes logs older than the current retention policy allows and returns how many rows
    /// were removed. Runs at startup, hourly from <see cref="LogRetentionService"/> and
    /// immediately after the policy changes from the UI.
    /// </summary>
    public int ApplyRetention()
    {
        var days = GetRetentionDays();
        if (days <= 0)
        {
            return 0;
        }

        var cutoffMs = DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeMilliseconds();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM request_logs WHERE timestamp_ms < @cutoff";
        command.Parameters.AddWithValue("@cutoff", cutoffMs);
        var deleted = command.ExecuteNonQuery();

        if (deleted > 0)
        {
            _logger.LogInformation(
                "Request log retention applied: deleted {Deleted} entries older than {Days} days.", deleted, days);
        }

        return deleted;
    }

    // ---- database plumbing ----

    private void InitializeDatabase()
    {
        try
        {
            OpenAndInitialize();
        }
        catch (SqliteException ex)
        {
            throw new InvalidOperationException(
                $"Cannot open the YARP UI request-log database at '{_databasePath}' ({ex.Message}). "
                + "The data directory must be writable by the process identity. Under IIS with the default "
                + "application pool identity, grant the pool 'Modify' on the data directory or point "
                + "YarpUi:DataDirectory at a writable folder.", ex);
        }
    }

    private void OpenAndInitialize()
    {
        using var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        Execute(connection, "PRAGMA journal_mode = WAL");

        Execute(connection, """
            CREATE TABLE IF NOT EXISTS request_logs (
                seq INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp_ms INTEGER NOT NULL,
                method TEXT NOT NULL,
                path TEXT NOT NULL,
                status_code INTEGER,
                duration_ms REAL NOT NULL,
                route_id TEXT,
                cluster_id TEXT,
                destination_id TEXT,
                destination_address TEXT,
                error TEXT,
                client_ip TEXT
            )
            """);
        MigrateLegacySchema(connection);
        Execute(connection, "CREATE INDEX IF NOT EXISTS ix_request_logs_timestamp ON request_logs (timestamp_ms)");
        Execute(connection, "CREATE INDEX IF NOT EXISTS ix_request_logs_route ON request_logs (route_id, timestamp_ms)");
        Execute(connection, "CREATE INDEX IF NOT EXISTS ix_request_logs_cluster ON request_logs (cluster_id, timestamp_ms)");
        Execute(connection, "CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL)");

        // First run: seed the policy from configuration so the UI has something to show/edit.
        using (var seed = connection.CreateCommand())
        {
            seed.CommandText = "INSERT OR IGNORE INTO settings (key, value) VALUES (@key, @value)";
            seed.Parameters.AddWithValue("@key", RetentionDaysKey);
            seed.Parameters.AddWithValue("@value", _defaultRetentionDays.ToString());
            seed.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Adds columns introduced after a database was first created (e.g. client_ip in 0.3.0) so
    /// existing installations upgrade in place instead of losing their history.
    /// </summary>
    private static void MigrateLegacySchema(SqliteConnection connection)
    {
        string[] requiredColumns = ["client_ip"];

        using var columns = connection.CreateCommand();
        columns.CommandText = "PRAGMA table_info(request_logs)";
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var reader = columns.ExecuteReader())
        {
            while (reader.Read())
            {
                present.Add(reader.GetString(1));
            }
        }

        foreach (var column in requiredColumns)
        {
            if (!present.Contains(column))
            {
                Execute(connection, $"ALTER TABLE request_logs ADD COLUMN {column} TEXT");
            }
        }
    }

    private async Task InsertBatchAsync(IReadOnlyList<RequestLogEntry> batch, CancellationToken cancellationToken)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO request_logs (timestamp_ms, method, path, status_code, duration_ms,
                                      route_id, cluster_id, destination_id, destination_address, error, client_ip)
            VALUES (@timestamp, @method, @path, @status, @duration, @route, @cluster, @destination, @address, @error, @clientIp)
            """;

        var timestamp = command.Parameters.Add("@timestamp", SqliteType.Integer);
        var method = command.Parameters.Add("@method", SqliteType.Text);
        var path = command.Parameters.Add("@path", SqliteType.Text);
        var status = command.Parameters.Add("@status", SqliteType.Integer);
        var duration = command.Parameters.Add("@duration", SqliteType.Real);
        var route = command.Parameters.Add("@route", SqliteType.Text);
        var cluster = command.Parameters.Add("@cluster", SqliteType.Text);
        var destination = command.Parameters.Add("@destination", SqliteType.Text);
        var address = command.Parameters.Add("@address", SqliteType.Text);
        var error = command.Parameters.Add("@error", SqliteType.Text);
        var clientIp = command.Parameters.Add("@clientIp", SqliteType.Text);

        foreach (var entry in batch)
        {
            timestamp.Value = new DateTimeOffset(entry.TimestampUtc).ToUnixTimeMilliseconds();
            method.Value = entry.Method;
            path.Value = entry.Path;
            status.Value = entry.StatusCode.HasValue ? entry.StatusCode.Value : DBNull.Value;
            duration.Value = entry.DurationMs;
            route.Value = entry.RouteId is null ? DBNull.Value : entry.RouteId;
            cluster.Value = entry.ClusterId is null ? DBNull.Value : entry.ClusterId;
            destination.Value = entry.DestinationId is null ? DBNull.Value : entry.DestinationId;
            address.Value = entry.DestinationAddress is null ? DBNull.Value : entry.DestinationAddress;
            error.Value = entry.Error is null ? DBNull.Value : entry.Error;
            clientIp.Value = entry.ClientIp is null ? DBNull.Value : entry.ClientIp;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        Execute(connection, "PRAGMA synchronous = NORMAL");
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed class RouteAccumulator
    {
        public List<double> Durations { get; } = new();
        public long Errors { get; set; }
    }
}

/// <summary>
/// Drains the capture channel into SQLite every ~200ms so the request path never waits on
/// the database, and flushes whatever is left when the host shuts down.
/// </summary>
public sealed class RequestLogWriter : BackgroundService
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(200);

    private readonly SqliteRequestLogStore _store;
    private readonly ILogger<RequestLogWriter> _logger;

    public RequestLogWriter(SqliteRequestLogStore store, ILogger<RequestLogWriter> logger)
    {
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(FlushInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _store.FlushPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to flush pending request log entries; they stay queued for the next attempt.");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // Shutdown: persist anything still queued so no captured requests are lost.
        try
        {
            await _store.FlushPendingAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to flush pending request log entries during shutdown.");
        }
    }
}
