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
}

/// <summary>
/// Keeps the most recent proxied requests in memory. Entries do not survive restarts;
/// the buffer is capped so long-running proxies don't grow without bound.
/// </summary>
public sealed class RequestLogStore
{
    private const int Capacity = 1000;

    private readonly object _sync = new();
    private readonly Queue<RequestLogEntry> _entries = new();
    private long _sequence;

    public RequestLogEntry Add(
        string method,
        string path,
        int? statusCode,
        double durationMs,
        string? routeId,
        string? clusterId,
        string? destinationId,
        string? destinationAddress,
        string? error)
    {
        lock (_sync)
        {
            var entry = new RequestLogEntry
            {
                Seq = ++_sequence,
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
            };

            _entries.Enqueue(entry);
            while (_entries.Count > Capacity)
            {
                _entries.Dequeue();
            }

            return entry;
        }
    }

    public IReadOnlyList<RequestLogEntry> GetAfter(long afterSeq)
    {
        lock (_sync)
        {
            return _entries.Where(e => e.Seq > afterSeq).ToList();
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
        }
    }
}
