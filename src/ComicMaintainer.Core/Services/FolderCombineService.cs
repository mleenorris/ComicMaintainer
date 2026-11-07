using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Default <see cref="IFolderCombineService"/>.
/// </summary>
/// <remarks>
/// <para>Group discovery (FolderCombineService.Groups.cs) buckets tracked files
/// by series key, collapsing aliases through the series metadata cache
/// (FolderCombineService.Aliases.cs), then reconciles the result with the
/// series library and with folder names that differ only by a
/// non-distinguishing suffix.</para>
/// <para>A combine (FolderCombineService.Execution.cs) runs as a background
/// batch job: one item per file move, then a post-loop step that removes the
/// emptied source folders under the shared <see cref="ILibraryMutationLock"/>
/// (FolderCombineSourceCleaner decides what may go), persists aliases and
/// queues a normalize-and-rename of the combined folder.</para>
/// </remarks>
public sealed partial class FolderCombineService : IFolderCombineService
{
    internal const string CombinableGroupsCacheKey = "FolderCombineService.CombinableFolderGroups";
    private static readonly TimeSpan CombinableGroupsCacheMaxAge = TimeSpan.FromMinutes(5);

    private readonly IFileStoreService _fileStore;
    private readonly IComicProcessorService _processor;
    private readonly IProcessingHistoryService _historyService;
    private readonly ISeriesLibraryService _seriesLibrary;
    private readonly ISeriesMetadataCacheService _metadataCache;
    private readonly IEventBroadcaster _eventBroadcaster;
    private readonly ILibraryMutationLock _libraryMutationLock;
    private readonly IMemoryCache _memoryCache;
    private readonly IOptionsMonitor<AppSettings> _settings;
    private readonly ILogger<FolderCombineService> _logger;
    private readonly SemaphoreSlim _combinableGroupsBuildLock = new(1, 1);

    private sealed record CombinableGroupsCacheEntry(
        (long Files, long Metadata, string? Language) Versions,
        IReadOnlyList<CombinableFolderGroup> Groups);

    public FolderCombineService(
        IFileStoreService fileStore,
        IComicProcessorService processor,
        IProcessingHistoryService historyService,
        ISeriesLibraryService seriesLibrary,
        ISeriesMetadataCacheService metadataCache,
        IEventBroadcaster eventBroadcaster,
        ILibraryMutationLock libraryMutationLock,
        IMemoryCache memoryCache,
        IOptionsMonitor<AppSettings> settings,
        ILogger<FolderCombineService> logger)
    {
        _fileStore = fileStore;
        _processor = processor;
        _historyService = historyService;
        _seriesLibrary = seriesLibrary;
        _metadataCache = metadataCache;
        _eventBroadcaster = eventBroadcaster;
        _libraryMutationLock = libraryMutationLock;
        _memoryCache = memoryCache;
        _settings = settings;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The full rebuild scans every tracked file plus the metadata cache, so
    /// the result is cached until the file store or metadata cache reports a
    /// change (or the preferred language changes). Planning a combine always
    /// rebuilds, so it acts on fresh data.
    /// </remarks>
    public async Task<IReadOnlyList<CombinableFolderGroup>> GetCombinableFolderGroupsAsync(CancellationToken cancellationToken = default)
    {
        if (_fileStore is not IChangeVersionSource fileVersions
            || _metadataCache is not IChangeVersionSource metadataVersions)
        {
            return await BuildCombinableFolderGroupsAsync(cancellationToken);
        }

        (long, long, string?) CaptureVersions() =>
            (fileVersions.ChangeVersion, metadataVersions.ChangeVersion, _settings.CurrentValue?.DefaultPreferredLanguage);

        // Capture versions before building so a concurrent write marks the
        // stored entry stale rather than being silently lost.
        var versions = CaptureVersions();
        if (_memoryCache.TryGetValue(CombinableGroupsCacheKey, out CombinableGroupsCacheEntry? cached)
            && cached is not null && cached.Versions.Equals(versions))
        {
            return cached.Groups;
        }

        await _combinableGroupsBuildLock.WaitAsync(cancellationToken);
        try
        {
            versions = CaptureVersions();
            if (_memoryCache.TryGetValue(CombinableGroupsCacheKey, out cached)
                && cached is not null && cached.Versions.Equals(versions))
            {
                return cached.Groups;
            }

            var groups = await BuildCombinableFolderGroupsAsync(cancellationToken);
            _memoryCache.Set(
                CombinableGroupsCacheKey,
                new CombinableGroupsCacheEntry(versions, groups),
                CombinableGroupsCacheMaxAge);
            return groups;
        }
        finally
        {
            _combinableGroupsBuildLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<int> GetCombinableFolderCountAsync(CancellationToken cancellationToken = default)
    {
        var groups = await GetCombinableFolderGroupsAsync(cancellationToken);
        // Every folder beyond the suggested destination counts as a source.
        return groups.Sum(g => Math.Max(0, g.Folders.Count - 1));
    }

    /// <inheritdoc />
    public Task<FolderCombinePlanResult> PreviewAsync(FolderCombineRequest request, CancellationToken cancellationToken = default) =>
        BuildPlanAsync(request, includeSourceCleanup: true, cancellationToken);

    /// <summary>
    /// True when <paramref name="path"/> is inside the watched directory once
    /// links and junctions are resolved, on the path and every ancestor, so a
    /// link inside the library cannot reach an external target.
    /// </summary>
    private bool IsPathSafe(string path) =>
        LibraryPathValidator.TryResolveWithin(path, out _, _settings.CurrentValue.WatchedDirectory);

    /// <summary>
    /// The configured folders a combine must never remove, even when one of
    /// them is a source folder or lies inside one.
    /// </summary>
    private IReadOnlyList<string> GetProtectedDirectories()
    {
        var settings = _settings.CurrentValue;
        return new[]
            {
                settings.WatchedDirectory,
                settings.DuplicateDirectory,
                settings.SuwayomiDownloadsDirectory,
                settings.SuwayomiImportDirectory
            }
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Select(directory => directory!)
            .ToList();
    }

    private Task LogHistoryAsync(string filePath, string action, bool success, string? errorMessage = null) =>
        _historyService.AddHistoryEntryAsync(new ProcessingHistoryEntry
        {
            Id = Guid.NewGuid(),
            FilePath = filePath,
            Action = action,
            Timestamp = DateTime.UtcNow,
            Success = success,
            ErrorMessage = errorMessage
        });
}
