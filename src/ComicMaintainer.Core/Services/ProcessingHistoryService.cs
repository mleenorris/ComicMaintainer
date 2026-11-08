using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Service for managing processing history
/// </summary>
/// <remarks>
/// Registered as a singleton, so it keeps the number of stored entries once counted and
/// adjusts it as entries are added or pruned; the history pages then do not count the whole
/// table each time. Code that deletes rows itself (the factory reset) calls
/// <see cref="InvalidateTotalCount"/>; the count is also refreshed after
/// <see cref="TotalCountLifetime"/>, which bounds the drift from any other change to the table.
/// </remarks>
public class ProcessingHistoryService : IProcessingHistoryService
{
    /// <summary>Entries deleted per statement while pruning.</summary>
    internal const int PruneBatchSize = 2000;

    /// <summary>How long a counted total is trusted before it is counted again.</summary>
    internal static readonly TimeSpan TotalCountLifetime = TimeSpan.FromMinutes(5);

    private readonly IDbContextFactory<ComicMaintainerDbContext> _dbContextFactory;
    private readonly ILogger<ProcessingHistoryService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly object _totalLock = new();
    private int? _cachedTotal;
    private DateTimeOffset _cachedTotalAt;

    /// <summary>Bumped whenever the stored entries change, so a count that raced a change is not cached.</summary>
    private long _totalGeneration;

    public ProcessingHistoryService(
        IDbContextFactory<ComicMaintainerDbContext> dbContextFactory,
        ILogger<ProcessingHistoryService> logger,
        TimeProvider? timeProvider = null)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<(IEnumerable<ProcessingHistoryEntry> history, int total)> GetHistoryAsync(
        int limit = 50,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        // Validate input parameters
        if (limit < 1 || limit > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 1000");
        }

        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset cannot be negative");
        }

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var query = dbContext.ProcessingHistory.AsNoTracking();

        var total = await GetTotalAsync(query, cancellationToken);

        // Project directly into the DTO so EF Core only reads the columns we use
        // and avoids change-tracker overhead for read-only paged queries.
        var history = await query
            .OrderByDescending(h => h.Timestamp)
            .Skip(offset)
            .Take(limit)
            .Select(e => new ProcessingHistoryEntry
            {
                Id = e.EntryId,
                FilePath = e.FilePath,
                Action = e.Action,
                Timestamp = e.Timestamp,
                Success = e.Success,
                ErrorMessage = e.ErrorMessage,
                BeforeFilename = e.BeforeFilename,
                AfterFilename = e.AfterFilename,
                BeforeTitle = e.BeforeTitle,
                AfterTitle = e.AfterTitle,
                BeforeSeries = e.BeforeSeries,
                AfterSeries = e.AfterSeries,
                BeforeIssue = e.BeforeIssue,
                AfterIssue = e.AfterIssue,
                BeforePublisher = e.BeforePublisher,
                AfterPublisher = e.AfterPublisher,
                BeforeYear = e.BeforeYear,
                AfterYear = e.AfterYear,
                BeforeVolume = e.BeforeVolume,
                AfterVolume = e.AfterVolume
            })
            .ToListAsync(cancellationToken);

        return (history, total);
    }

    public async Task AddHistoryEntryAsync(
        ProcessingHistoryEntry entry,
        CancellationToken cancellationToken = default)
    {
        if (IsNoOp(entry))
        {
            _logger.LogDebug("Not recording a no-op {Action} for {FilePath}: {Note}",
                LoggingHelper.SanitizeForLog(entry.Action), LoggingHelper.SanitizePathForLog(entry.FilePath),
                LoggingHelper.SanitizeForLog(entry.ErrorMessage));
            return;
        }

        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

            var entity = new ProcessingHistoryEntity
            {
                EntryId = entry.Id != Guid.Empty ? entry.Id : Guid.NewGuid(),
                FilePath = entry.FilePath,
                Action = entry.Action,
                Timestamp = entry.Timestamp,
                Success = entry.Success,
                ErrorMessage = entry.ErrorMessage,
                BeforeFilename = entry.BeforeFilename,
                AfterFilename = entry.AfterFilename,
                BeforeTitle = entry.BeforeTitle,
                AfterTitle = entry.AfterTitle,
                BeforeSeries = entry.BeforeSeries,
                AfterSeries = entry.AfterSeries,
                BeforeIssue = entry.BeforeIssue,
                AfterIssue = entry.AfterIssue,
                BeforePublisher = entry.BeforePublisher,
                AfterPublisher = entry.AfterPublisher,
                BeforeYear = entry.BeforeYear,
                AfterYear = entry.AfterYear,
                BeforeVolume = entry.BeforeVolume,
                AfterVolume = entry.AfterVolume
            };

            dbContext.ProcessingHistory.Add(entity);
            await dbContext.SaveChangesAsync(cancellationToken);
            AdjustCachedTotal(+1);
        }
        catch (OperationCanceledException ex)
        {
            // Log cancellation but don't treat as error - operation was intentionally cancelled
            _logger.LogWarning(ex, "History entry addition was cancelled");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Log other errors but don't throw - history logging should not break processing
            _logger.LogError(ex, "Error adding processing history entry");
        }
    }

    public async Task<int> PruneAsync(int retentionDays, int maxEntries, CancellationToken cancellationToken = default)
    {
        if (retentionDays <= 0 && maxEntries <= 0)
        {
            return 0;
        }

        var removed = 0;
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

            if (retentionDays > 0)
            {
                var cutoff = _timeProvider.GetUtcNow().UtcDateTime.AddDays(-retentionDays);
                await DeleteInBatchesAsync(
                    dbContext.ProcessingHistory.Where(h => h.Timestamp < cutoff),
                    count => removed += count,
                    cancellationToken);
            }

            if (maxEntries > 0)
            {
                // The oldest entry that is kept; everything ordered before it goes. Read
                // through the Timestamp index, so this costs maxEntries index steps rather
                // than a scan of the table.
                var oldestKept = await dbContext.ProcessingHistory
                    .AsNoTracking()
                    .OrderByDescending(h => h.Timestamp)
                    .ThenByDescending(h => h.Id)
                    .Skip(maxEntries - 1)
                    .Select(h => new { h.Timestamp, h.Id })
                    .FirstOrDefaultAsync(cancellationToken);

                if (oldestKept is not null)
                {
                    await DeleteInBatchesAsync(
                        dbContext.ProcessingHistory.Where(h =>
                            h.Timestamp < oldestKept.Timestamp
                            || (h.Timestamp == oldestKept.Timestamp && h.Id < oldestKept.Id)),
                        count => removed += count,
                        cancellationToken);
                }
            }

            return removed;
        }
        finally
        {
            // Batches already deleted stay deleted when a later one fails or is cancelled.
            if (removed > 0)
            {
                InvalidateCachedTotal();
            }
        }
    }

    /// <inheritdoc />
    public void InvalidateTotalCount() => InvalidateCachedTotal();

    /// <summary>
    /// A successful normalize or rename that carries a note (why nothing was done, for
    /// example "File already normalized" or "Normalize disabled") and whose before and after
    /// values are all the same. Entries that record a change pass no note, failures are
    /// always kept, and a note on any other action records something that happened (such as
    /// a duplicate being detected).
    /// </summary>
    internal static bool IsNoOp(ProcessingHistoryEntry entry) =>
        entry.Success
        && !string.IsNullOrEmpty(entry.ErrorMessage)
        && entry.Action is "Normalize" or "Rename"
        && string.Equals(entry.BeforeFilename, entry.AfterFilename, StringComparison.Ordinal)
        && string.Equals(entry.BeforeTitle, entry.AfterTitle, StringComparison.Ordinal)
        && string.Equals(entry.BeforeSeries, entry.AfterSeries, StringComparison.Ordinal)
        && string.Equals(entry.BeforeIssue, entry.AfterIssue, StringComparison.Ordinal)
        && string.Equals(entry.BeforePublisher, entry.AfterPublisher, StringComparison.Ordinal)
        && entry.BeforeYear == entry.AfterYear
        && string.Equals(entry.BeforeVolume, entry.AfterVolume, StringComparison.Ordinal);

    /// <summary>
    /// Deletes the rows <paramref name="query"/> matches, oldest first, <see cref="PruneBatchSize"/>
    /// per statement (each its own short transaction), reporting each batch to
    /// <paramref name="deleted"/> as it is committed.
    /// </summary>
    private static async Task DeleteInBatchesAsync(
        IQueryable<ProcessingHistoryEntity> query,
        Action<int> deleted,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = await query
                .OrderBy(h => h.Timestamp)
                .Take(PruneBatchSize)
                .ExecuteDeleteAsync(cancellationToken);
            deleted(batch);
            if (batch < PruneBatchSize)
            {
                return;
            }
        }
    }

    private async Task<int> GetTotalAsync(IQueryable<ProcessingHistoryEntity> query, CancellationToken cancellationToken)
    {
        long generation;
        lock (_totalLock)
        {
            if (_cachedTotal is { } cached && _timeProvider.GetUtcNow() - _cachedTotalAt < TotalCountLifetime)
            {
                return cached;
            }
            generation = _totalGeneration;
        }

        var total = await query.CountAsync(cancellationToken);
        lock (_totalLock)
        {
            // An entry added or pruned while counting may or may not be in the count, so
            // only a count that raced no change is kept.
            if (generation == _totalGeneration)
            {
                _cachedTotal = total;
                _cachedTotalAt = _timeProvider.GetUtcNow();
            }
        }
        return total;
    }

    private void AdjustCachedTotal(int delta)
    {
        lock (_totalLock)
        {
            _totalGeneration++;
            if (_cachedTotal is { } cached)
            {
                _cachedTotal = Math.Max(0, cached + delta);
            }
        }
    }

    private void InvalidateCachedTotal()
    {
        lock (_totalLock)
        {
            _totalGeneration++;
            _cachedTotal = null;
        }
    }
}
