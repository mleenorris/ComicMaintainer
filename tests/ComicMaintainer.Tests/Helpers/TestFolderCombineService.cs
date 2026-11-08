using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Helpers;

/// <summary>
/// Builds a real <see cref="FolderCombineService"/> over a test's mocks, so
/// controller tests exercise the folder-combine endpoints end to end. Optional
/// collaborators default to inert stand-ins: a metadata cache without records,
/// an event broadcaster that does nothing, a fresh memory cache (only used when
/// both the file store and the metadata cache report change versions) and a
/// private library mutation lock.
/// </summary>
internal static class TestFolderCombineService
{
    public static FolderCombineService Create(
        IFileStoreService fileStore,
        IComicProcessorService processor,
        IProcessingHistoryService historyService,
        ISeriesLibraryService seriesLibrary,
        IOptionsMonitor<AppSettings> settings,
        ISeriesMetadataCacheService? metadataCache = null,
        IMemoryCache? memoryCache = null,
        IEventBroadcaster? eventBroadcaster = null,
        ILibraryMutationLock? libraryMutationLock = null,
        IFileOperationLock? fileOperationLock = null,
        ILogger<FolderCombineService>? logger = null) =>
        new(
            fileStore,
            processor,
            historyService,
            seriesLibrary,
            metadataCache ?? Mock.Of<ISeriesMetadataCacheService>(),
            eventBroadcaster ?? Mock.Of<IEventBroadcaster>(),
            libraryMutationLock ?? new LibraryMutationLock(),
            fileOperationLock ?? new FileOperationLock(),
            memoryCache ?? new MemoryCache(new MemoryCacheOptions()),
            settings,
            logger ?? NullLogger<FolderCombineService>.Instance);
}
