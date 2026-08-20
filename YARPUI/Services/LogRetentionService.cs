namespace YARPUI.Services;

/// <summary>
/// Enforces the request log retention policy (keep logs for N days, 0 = forever): one pass at
/// startup and then once an hour. The policy lives in the log database's settings table and can
/// be changed live from the Logs page — changing it there also triggers an immediate purge,
/// so this timer only has to cover the passage of time.
/// </summary>
public sealed class LogRetentionService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly SqliteRequestLogStore _store;
    private readonly ILogger<LogRetentionService> _logger;

    public LogRetentionService(SqliteRequestLogStore store, ILogger<LogRetentionService> logger)
    {
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RunOnce();

        using var timer = new PeriodicTimer(Interval);
        while (true)
        {
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

            RunOnce();
        }
    }

    private void RunOnce()
    {
        try
        {
            _store.ApplyRetention();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Request log retention pass failed; it will be retried in an hour.");
        }
    }
}
