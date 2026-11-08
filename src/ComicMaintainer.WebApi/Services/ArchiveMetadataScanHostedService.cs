using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Services;

namespace ComicMaintainer.WebApi.Services;

/// <summary>
/// Runs <see cref="ArchiveMetadataScanService"/> in the background so the library's cached
/// archive metadata catches up with the files on disk without any request opening an archive.
/// </summary>
/// <remarks>
/// The first pass starts <see cref="DefaultStartupDelay"/> after start-up (after the file
/// watcher's initial scan has had time to add new files). After that a pass runs whenever
/// the file store reports a change (checked every <see cref="DefaultPollInterval"/>, which
/// costs nothing while the library is idle) and at least every <see cref="FullRescanInterval"/>
/// to retry files an earlier pass could not record. A pass compares the cached stamps with
/// the sizes and modification times stored in the database and does not read the disk, so
/// an archive changed outside the application is re-read once the file watcher or a
/// library scan has refreshed those stored values.
/// </remarks>
public sealed class ArchiveMetadataScanHostedService : BackgroundService
{
    /// <summary>How long after start-up the first pass runs.</summary>
    public static readonly TimeSpan DefaultStartupDelay = TimeSpan.FromMinutes(1);

    /// <summary>How often the file store's change counter is checked.</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(30);

    /// <summary>Longest time between passes, whatever the change counter says.</summary>
    public static readonly TimeSpan FullRescanInterval = TimeSpan.FromMinutes(30);

    private readonly ArchiveMetadataScanService _scanner;
    private readonly IChangeVersionSource? _fileVersions;
    private readonly ILogger<ArchiveMetadataScanHostedService> _logger;
    private readonly TimeSpan _startupDelay;
    private readonly TimeSpan _pollInterval;
    private int _completedPasses;

    public ArchiveMetadataScanHostedService(
        ArchiveMetadataScanService scanner,
        IFileStoreService fileStore,
        ILogger<ArchiveMetadataScanHostedService> logger,
        TimeSpan? startupDelay = null,
        TimeSpan? pollInterval = null)
    {
        _scanner = scanner;
        _fileVersions = fileStore as IChangeVersionSource;
        _logger = logger;
        _startupDelay = startupDelay is { } delay
            ? (delay > TimeSpan.Zero ? delay : TimeSpan.Zero)
            : DefaultStartupDelay;
        _pollInterval = pollInterval is { } poll && poll > TimeSpan.Zero ? poll : DefaultPollInterval;
    }

    /// <summary>Completed passes, for diagnostics and tests.</summary>
    public int CompletedPasses => Volatile.Read(ref _completedPasses);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (_startupDelay > TimeSpan.Zero)
            {
                await Task.Delay(_startupDelay, stoppingToken);
            }

            long? scannedVersion = null;
            var lastPass = DateTime.MinValue;
            while (!stoppingToken.IsCancellationRequested)
            {
                var version = _fileVersions?.ChangeVersion;
                if (version is null || version != scannedVersion || DateTime.UtcNow - lastPass >= FullRescanInterval)
                {
                    // Captured before the pass: whatever the pass records bumps the counter,
                    // which runs one more (cheap) pass that finds nothing left to do.
                    scannedVersion = version;
                    lastPass = DateTime.UtcNow;
                    await RunPassAsync(stoppingToken);
                }

                await Task.Delay(_pollInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    private async Task RunPassAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _scanner.ScanAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Archive metadata scan failed; it is retried on the next pass");
        }
        finally
        {
            Interlocked.Increment(ref _completedPasses);
        }
    }
}
