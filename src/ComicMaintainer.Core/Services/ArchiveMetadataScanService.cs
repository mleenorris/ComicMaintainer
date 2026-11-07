using System.Collections.Concurrent;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Fills the archive-metadata cache in the background: reads the ComicInfo of every tracked
/// file whose cached metadata is missing or stale and stores it in <c>ComicFiles.Metadata</c>,
/// so library requests never open archives.
/// </summary>
/// <remarks>
/// Files reach the scan when they are first added (by the watcher or the library scan job),
/// when their stored size or modification time changes, and for libraries that existed before
/// the cache did. Archives are read one at a time. An archive that cannot be read is
/// remembered with its size and modification time and skipped until either changes, so a
/// damaged file is not reopened on every pass.
/// </remarks>
public sealed class ArchiveMetadataScanService
{
    /// <summary>Files fetched from the database per query.</summary>
    internal const int BatchSize = 100;

    private readonly IFileStoreService _fileStore;
    private readonly IComicProcessorService _processor;
    private readonly ILogger<ArchiveMetadataScanService> _logger;

    /// <summary>Archives that could not be read, with the stored stats they had at the time.</summary>
    private readonly ConcurrentDictionary<string, (long Size, DateTime Modified)> _unreadable = new(StringComparer.Ordinal);

    public ArchiveMetadataScanService(
        IFileStoreService fileStore,
        IComicProcessorService processor,
        ILogger<ArchiveMetadataScanService> logger)
    {
        _fileStore = fileStore;
        _processor = processor;
        _logger = logger;
    }

    /// <summary>
    /// One pass over every file whose cached metadata is missing or stale. Throws
    /// <see cref="OperationCanceledException"/> when cancelled; what was recorded so far stays.
    /// </summary>
    public async Task<ArchiveMetadataScanResult> ScanAsync(CancellationToken cancellationToken)
    {
        int recorded = 0, unreadable = 0, skipped = 0;
        var afterId = 0;
        while (true)
        {
            var batch = await _fileStore.GetFilesNeedingMetadataScanAsync(afterId, BatchSize, cancellationToken);
            foreach (var candidate in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                afterId = candidate.Id;

                var stats = (candidate.FileSize, candidate.LastModified);
                if (_unreadable.TryGetValue(candidate.FilePath, out var failedAt) && failedAt == stats)
                {
                    continue;
                }

                var metadata = await _processor.GetMetadataAsync(candidate.FilePath, cancellationToken);
                if (metadata is null)
                {
                    _unreadable[candidate.FilePath] = stats;
                    unreadable++;
                    _logger.LogDebug("Archive metadata scan could not read {FilePath}; it is skipped until it changes",
                        LoggingHelper.SanitizePathForLog(candidate.FilePath));
                    continue;
                }

                _unreadable.TryRemove(candidate.FilePath, out _);
                if (await _fileStore.RecordScannedArchiveMetadataAsync(candidate, metadata, cancellationToken))
                {
                    recorded++;
                }
                else
                {
                    skipped++;
                }
            }

            if (batch.Count < BatchSize)
            {
                break;
            }
        }

        if (recorded > 0 || unreadable > 0)
        {
            _logger.LogInformation(
                "Archive metadata scan cached the metadata of {Recorded} file(s); {Unreadable} could not be read and {Skipped} changed while being read",
                recorded, unreadable, skipped);
        }

        return new ArchiveMetadataScanResult(recorded, unreadable, skipped);
    }
}
