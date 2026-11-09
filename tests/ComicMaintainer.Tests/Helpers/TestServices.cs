using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Core.Telemetry;
using ComicMaintainer.WebApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Moq;

namespace ComicMaintainer.Tests.Helpers;

/// <summary>
/// Builds services whose constructors require every collaborator production gives them, for
/// tests that only care about some of those collaborators.
/// </summary>
/// <remarks>
/// A collaborator a test leaves out is a loose mock that has nothing to report, a cache that
/// never holds anything, or a fresh lock, so it does not affect the behaviour under test. The
/// resolver a test leaves out is the real <see cref="SeriesNameResolver"/> over the same cache,
/// because display titles are computed by it.
/// </remarks>
public static class TestServices
{
    public static FileStoreService CreateFileStore(
        IOptionsMonitor<AppSettings> settings,
        ILogger<FileStoreService> logger,
        IDbContextFactory<ComicMaintainerDbContext> dbContextFactory,
        IEventBroadcaster? eventBroadcaster = null,
        IMemoryCache? memoryCache = null,
        IUserContextAccessor? userContext = null)
        => new(
            settings,
            logger,
            dbContextFactory,
            eventBroadcaster ?? Mock.Of<IEventBroadcaster>(),
            memoryCache ?? new NonCachingMemoryCache(),
            userContext ?? Mock.Of<IUserContextAccessor>());

    public static FileWatcherService CreateFileWatcher(
        IOptionsMonitor<AppSettings> settings,
        ILogger<FileWatcherService> logger,
        IFileStoreService fileStore,
        IComicProcessorService processor,
        IEventBroadcaster? eventBroadcaster = null,
        TimeProvider? timeProvider = null)
        => new(
            settings,
            logger,
            fileStore,
            processor,
            eventBroadcaster ?? Mock.Of<IEventBroadcaster>(),
            timeProvider);

    public static ComicProcessorService CreateComicProcessor(
        IOptionsMonitor<AppSettings> settings,
        ILogger<ComicProcessorService> logger,
        IFileStoreService fileStore,
        IProcessingHistoryService historyService,
        IEventBroadcaster? eventBroadcaster = null,
        IExternalSeriesMetadataService? externalSeriesMetadata = null,
        ISeriesMetadataCacheService? seriesMetadataCache = null,
        ISeriesNameResolver? seriesNameResolver = null,
        IBatchJobRegistry? jobRegistry = null,
        IComicEmailService? comicEmail = null,
        ComicMaintainerMetrics? metrics = null,
        ILoggerFactory? loggerFactory = null,
        IFileOperationLock? fileLock = null,
        ComicMaintainer.Core.Notifications.INotificationPublisher? notifications = null)
    {
        var cache = seriesMetadataCache ?? Mock.Of<ISeriesMetadataCacheService>();
        return new ComicProcessorService(
            settings,
            logger,
            fileStore,
            historyService,
            eventBroadcaster ?? Mock.Of<IEventBroadcaster>(),
            cache,
            seriesNameResolver ?? CreateSeriesNameResolver(
                settings,
                loggerFactory?.CreateLogger<SeriesNameResolver>() ?? NullLogger<SeriesNameResolver>.Instance,
                cache,
                externalSeriesMetadata),
            jobRegistry ?? CreateBatchJobRegistry(settings, NullLogger<BatchJobRegistry>.Instance, eventBroadcaster: eventBroadcaster, metrics: metrics),
            comicEmail ?? Mock.Of<IComicEmailService>(),
            fileLock ?? new FileOperationLock(),
            notifications ?? Mock.Of<ComicMaintainer.Core.Notifications.INotificationPublisher>(),
            metrics);
    }

    public static BatchJobRegistry CreateBatchJobRegistry(
        IOptionsMonitor<AppSettings> settings,
        ILogger<BatchJobRegistry> logger,
        IJobStateStore? jobStateStore = null,
        IEventBroadcaster? eventBroadcaster = null,
        ComicMaintainerMetrics? metrics = null)
        => new(
            settings,
            logger,
            jobStateStore ?? Mock.Of<IJobStateStore>(),
            eventBroadcaster ?? Mock.Of<IEventBroadcaster>(),
            metrics);

    public static SeriesNameResolver CreateSeriesNameResolver(
        IOptionsMonitor<AppSettings> settings,
        ILogger<SeriesNameResolver> logger,
        ISeriesMetadataCacheService? cache = null,
        IExternalSeriesMetadataService? externalMetadata = null)
        => new(
            settings,
            logger,
            cache ?? Mock.Of<ISeriesMetadataCacheService>(),
            externalMetadata ?? Mock.Of<IExternalSeriesMetadataService>());

    public static SeriesLibraryService CreateSeriesLibrary(
        IFileStoreService fileStore,
        ISeriesMetadataCacheService metadataCache,
        IOptionsMonitor<AppSettings> settings,
        ILogger<SeriesLibraryService> logger,
        ISeriesNameResolver? seriesNameResolver = null,
        IUserContextAccessor? userContext = null)
        => new(
            fileStore,
            metadataCache,
            settings,
            logger,
            seriesNameResolver ?? CreateSeriesNameResolver(settings, NullLogger<SeriesNameResolver>.Instance, metadataCache),
            userContext ?? Mock.Of<IUserContextAccessor>());

    public static DatabaseCleanupHostedService CreateDatabaseCleanup(
        IFileStoreService fileStore,
        IOptionsMonitor<AppSettings> appSettings,
        ILogger<DatabaseCleanupHostedService> logger,
        IDbContextFactory<ComicMaintainerDbContext>? dbContextFactory = null,
        TimeSpan? startupDelay = null,
        IProcessingHistoryService? historyService = null)
        => new(
            fileStore,
            appSettings,
            logger,
            dbContextFactory ?? CreateInMemoryDbContextFactory(),
            historyService ?? Mock.Of<IProcessingHistoryService>(),
            startupDelay);

    /// <summary>
    /// A resolver that names every file something it is not tagged with, so a job that only
    /// rewrites files whose tag is out of date picks all of them.
    /// </summary>
    public static ISeriesNameResolver ResolverThatRetagsEveryFile()
    {
        var resolver = new Mock<ISeriesNameResolver>();
        resolver
            .Setup(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<ComicMetadata?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesNameResolution { ResolvedSeries = "A name no file carries" });
        return resolver.Object;
    }

    private static IDbContextFactory<ComicMaintainerDbContext> CreateInMemoryDbContextFactory()
    {
        var databaseName = Guid.NewGuid().ToString("N");
        var factory = new Mock<IDbContextFactory<ComicMaintainerDbContext>>();
        factory
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ComicMaintainerDbContext(
                new DbContextOptionsBuilder<ComicMaintainerDbContext>().UseInMemoryDatabase(databaseName).Options));
        return factory.Object;
    }

    /// <summary>
    /// An <see cref="IMemoryCache"/> that never holds an entry, so reads always reach the
    /// database. Tests of the caching itself pass a real <see cref="MemoryCache"/>.
    /// </summary>
    public sealed class NonCachingMemoryCache : IMemoryCache
    {
        public ICacheEntry CreateEntry(object key) => new DiscardedEntry(key);

        public void Remove(object key)
        {
        }

        public bool TryGetValue(object key, out object? value)
        {
            value = null;
            return false;
        }

        public void Dispose()
        {
        }

        private sealed class DiscardedEntry(object key) : ICacheEntry
        {
            public object Key { get; } = key;

            public object? Value { get; set; }

            public DateTimeOffset? AbsoluteExpiration { get; set; }

            public TimeSpan? AbsoluteExpirationRelativeToNow { get; set; }

            public TimeSpan? SlidingExpiration { get; set; }

            public IList<IChangeToken> ExpirationTokens { get; } = new List<IChangeToken>();

            public IList<PostEvictionCallbackRegistration> PostEvictionCallbacks { get; } = new List<PostEvictionCallbackRegistration>();

            public CacheItemPriority Priority { get; set; }

            public long? Size { get; set; }

            public void Dispose()
            {
            }
        }
    }
}
