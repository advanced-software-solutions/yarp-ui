namespace YARPUI.Services;

/// <summary>
/// Announces once at startup that the data directory was moved because the content root
/// is not writable by the process — typical under IIS, where the default application pool
/// identity has read-only access to the site folder. Without this, the request log
/// database fails to open with SQLite error 14 and takes the whole proxy down.
/// </summary>
internal sealed class DataDirectoryFallbackWarningService : IHostedService
{
    private readonly ILogger<DataDirectoryFallbackWarningService> _logger;
    private readonly string _fromDirectory;
    private readonly string _toDirectory;

    public DataDirectoryFallbackWarningService(
        ILogger<DataDirectoryFallbackWarningService> logger,
        string fromDirectory,
        string toDirectory)
    {
        _logger = logger;
        _fromDirectory = fromDirectory;
        _toDirectory = toDirectory;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            "The YARP UI data directory '{FromDirectory}' is not writable by this process — common under IIS, "
            + "where the default application pool identity cannot write to the site folder. Mutable state "
            + "(request logs, UI-managed routes) is stored in '{ToDirectory}' instead. Set YarpUi:DataDirectory "
            + "to choose a location explicitly, or grant the process identity write access to the site folder.",
            _fromDirectory, _toDirectory);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
