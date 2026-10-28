using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Telemetry;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Xml.Linq;
using SharpCompress.Archives;
using SharpCompress.Archives.Zip;
using SharpCompress.Common;
using SharpCompress.Writers;
using SharpCompress.Writers.Zip;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Service for processing comic files with SharpCompress integration
/// </summary>
public partial class ComicProcessorService : IComicProcessorService, IDisposable
{
    private const string UnknownSeries = "Unknown Series";
    private readonly IOptionsMonitor<AppSettings> _settingsMonitor;
    private readonly ILogger<ComicProcessorService> _logger;
    private readonly IFileStoreService _fileStore;
    private readonly IEventBroadcaster? _eventBroadcaster;
    private readonly IExternalSeriesMetadataService? _externalSeriesMetadata;
    private readonly ISeriesMetadataCacheService? _seriesMetadataCache;
    private readonly IProcessingHistoryService _historyService;
    private readonly ISeriesNameResolver? _seriesNameResolver;
    private readonly IJobStateStore? _jobStateStore;
    private readonly ConcurrentDictionary<Guid, ProcessingJob> _jobs = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _jobCancellationTokens = new();
    private readonly ConcurrentDictionary<Guid, object> _jobSyncLocks = new();
    private readonly ConcurrentDictionary<Guid, JobStatus> _lastPersistedStatus = new();
    private readonly ConcurrentDictionary<Guid, DateTime> _lastPersistedAt = new();
    private readonly ConcurrentDictionary<Guid, bool> _persistedTerminalJobs = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _jobPersistenceLocks = new();
    private readonly ConcurrentDictionary<Guid, bool> _deletedPersistedJobs = new();

    /// <summary>
    /// Minimum spacing between durable progress writes for a job that has not changed status.
    /// </summary>
    private static readonly TimeSpan JobPersistenceInterval = TimeSpan.FromSeconds(5);
    private readonly IComicEmailService? _comicEmail;
    private readonly ComicMaintainerMetrics? _metrics;
    private readonly SemaphoreSlim _processingSemaphore;
    private readonly int _maxWorkers;
    private bool _disposed;

    /// <summary>
    /// Returns the current AppSettings snapshot. Settings are re-read on each access so that
    /// changes made via SettingsService take effect on the next operation without restarting
    /// the process.
    /// Note: <see cref="AppSettings.MaxWorkers"/> is captured into a fixed-size semaphore at
    /// construction and therefore still requires a restart to change.
    /// </summary>
    private AppSettings _settings => _settingsMonitor.CurrentValue;

    public ComicProcessorService(
        IOptionsMonitor<AppSettings> settings,
        ILogger<ComicProcessorService> logger,
        IFileStoreService fileStore,
        IProcessingHistoryService historyService,
        IEventBroadcaster? eventBroadcaster = null,
        IExternalSeriesMetadataService? externalSeriesMetadata = null,
        ISeriesMetadataCacheService? seriesMetadataCache = null,
        ISeriesNameResolver? seriesNameResolver = null,
        IJobStateStore? jobStateStore = null,
        IComicEmailService? comicEmail = null,
        ComicMaintainerMetrics? metrics = null)
    {
        _metrics = metrics;
        _settingsMonitor = settings;
        _logger = logger;
        _fileStore = fileStore;
        _historyService = historyService;
        _eventBroadcaster = eventBroadcaster;
        _externalSeriesMetadata = externalSeriesMetadata;
        _seriesMetadataCache = seriesMetadataCache;
        _seriesNameResolver = seriesNameResolver;
        _jobStateStore = jobStateStore;
        _comicEmail = comicEmail;
        _maxWorkers = Math.Max(1, _settingsMonitor.CurrentValue.MaxWorkers);
        _processingSemaphore = new SemaphoreSlim(_maxWorkers, _maxWorkers);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            foreach (var cancellationTokenSource in _jobCancellationTokens.Values)
            {
                TryCancel(cancellationTokenSource);
            }

            _processingSemaphore.Dispose();
        }

        _disposed = true;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Helper method to log processing history entries
    /// </summary>
    private async Task LogHistoryAsync(string filePath, string action, bool success, string? errorMessage = null, CancellationToken cancellationToken = default)
    {
        await _historyService.AddHistoryEntryAsync(new ProcessingHistoryEntry
        {
            Id = Guid.NewGuid(),
            FilePath = filePath,
            Action = action,
            Timestamp = DateTime.UtcNow,
            Success = success,
            ErrorMessage = errorMessage
        }, cancellationToken);
    }

    /// <summary>
    /// Helper method to log processing history entries with before/after metadata
    /// </summary>
    private async Task LogHistoryWithChangesAsync(
        string filePath, 
        string action, 
        bool success, 
        string? errorMessage,
        string? beforeFilename,
        string? afterFilename,
        ComicMetadata? beforeMetadata,
        ComicMetadata? afterMetadata,
        CancellationToken cancellationToken = default)
    {
        await _historyService.AddHistoryEntryAsync(new ProcessingHistoryEntry
        {
            Id = Guid.NewGuid(),
            FilePath = filePath,
            Action = action,
            Timestamp = DateTime.UtcNow,
            Success = success,
            ErrorMessage = errorMessage,
            BeforeFilename = beforeFilename,
            AfterFilename = afterFilename,
            BeforeTitle = beforeMetadata?.Title,
            AfterTitle = afterMetadata?.Title,
            BeforeSeries = beforeMetadata?.Series,
            AfterSeries = afterMetadata?.Series,
            BeforeIssue = beforeMetadata?.Issue,
            AfterIssue = afterMetadata?.Issue,
            BeforePublisher = beforeMetadata?.Publisher,
            AfterPublisher = afterMetadata?.Publisher,
            BeforeYear = beforeMetadata?.Year,
            AfterYear = afterMetadata?.Year,
            BeforeVolume = beforeMetadata?.Volume,
            AfterVolume = afterMetadata?.Volume
        }, cancellationToken);
    }
}
