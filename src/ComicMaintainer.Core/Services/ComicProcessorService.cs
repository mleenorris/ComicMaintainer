using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Notifications;
using ComicMaintainer.Core.Telemetry;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
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
    private readonly IEventBroadcaster _eventBroadcaster;
    private readonly ISeriesMetadataCacheService _seriesMetadataCache;
    private readonly IProcessingHistoryService _historyService;
    private readonly ISeriesNameResolver _seriesNameResolver;
    private readonly IBatchJobRegistry _jobRegistry;
    private readonly IComicEmailService _comicEmail;
    private readonly ComicMaintainerMetrics? _metrics;
    private readonly SemaphoreSlim _processingSemaphore;
    private readonly IFileOperationLock _fileLock;
    private readonly INotificationPublisher _notifications;

    // Where a rewritten or renamed file is queued, so its new cover is generated before the library shows it.
    private readonly IFileCoverWarmupQueue? _coverWarmup;
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
        IEventBroadcaster eventBroadcaster,
        ISeriesMetadataCacheService seriesMetadataCache,
        ISeriesNameResolver seriesNameResolver,
        IBatchJobRegistry jobRegistry,
        IComicEmailService comicEmail,
        IFileOperationLock fileLock,
        INotificationPublisher notifications,
        ComicMaintainerMetrics? metrics = null,
        IFileCoverWarmupQueue? coverWarmup = null)
    {
        _notifications = notifications;
        _metrics = metrics;
        _coverWarmup = coverWarmup;
        _settingsMonitor = settings;
        _logger = logger;
        _fileStore = fileStore;
        _historyService = historyService;
        _eventBroadcaster = eventBroadcaster;
        _seriesMetadataCache = seriesMetadataCache;
        _seriesNameResolver = seriesNameResolver;
        _jobRegistry = jobRegistry;
        _comicEmail = comicEmail;
        _maxWorkers = Math.Max(1, _settingsMonitor.CurrentValue.MaxWorkers);
        _processingSemaphore = new SemaphoreSlim(_maxWorkers, _maxWorkers);
        _fileLock = fileLock;
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            // The jobs still running hold slots of the semaphore that is about to go away.
            _jobRegistry.CancelActiveJobs();
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
