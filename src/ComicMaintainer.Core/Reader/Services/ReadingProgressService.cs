using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Reader.Interfaces;
using ComicMaintainer.Core.Reader.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Reader.Services;

/// <summary>
/// Persists and retrieves durable per-user reading progress using EF Core.
/// </summary>
public class ReadingProgressService : IReadingProgressService
{
    private readonly IDbContextFactory<ComicMaintainerDbContext> _dbContextFactory;
    private readonly ILogger<ReadingProgressService> _logger;

    public ReadingProgressService(
        IDbContextFactory<ComicMaintainerDbContext> dbContextFactory,
        ILogger<ReadingProgressService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<ReadingProgress?> GetProgressAsync(string userId, string contentId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.ReadingProgresses
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId && p.ContentId == contentId, cancellationToken);

        if (entity is null) return null;

        return MapToModel(entity);
    }

    /// <inheritdoc/>
    public async Task SaveProgressAsync(ReadingProgress progress, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.ReadingProgresses
            .FirstOrDefaultAsync(p => p.UserId == progress.UserId && p.ContentId == progress.ContentId, cancellationToken);

        var isNew = false;

        if (entity is null)
        {
            isNew = true;
            entity = new ReadingProgressEntity
            {
                UserId = progress.UserId,
                ContentId = progress.ContentId,
                CreatedAt = DateTime.UtcNow
            };
            db.ReadingProgresses.Add(entity);
        }

        entity.CurrentPage = progress.CurrentPage;
        entity.TotalPages = progress.TotalPages;
        entity.PercentComplete = progress.PercentComplete;
        entity.LastReadAt = progress.LastReadAt;
        entity.CompletedAt = progress.CompletedAt;
        entity.LastReaderMode = (int)progress.LastReaderMode;
        entity.ReadingDirection = (int)progress.ReadingDirection;
        entity.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);

        _logger.LogDebug("Saved reading progress record {ReadingProgressId} ({Operation}).",
            entity.Id,
            isNew ? "created" : "updated");
    }

    /// <inheritdoc/>
    /// <remarks>
    /// This runs on every page turn, so it is one context, two indexed lookups and
    /// a single <c>SaveChanges</c> (one SQLite write transaction), where the
    /// reader used to make five round trips and two commits through three
    /// contexts. When a concurrent request inserts one of the rows between the
    /// lookup and the save, the save is retried once against the row that won.
    /// </remarks>
    public async Task RecordPageAsync(string userId, string filePath, int page, int totalPages, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);

        var targetPage = totalPages > 0 ? Math.Min(page, totalPages) : page;

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        for (var attempt = 1; ; attempt++)
        {
            var now = DateTime.UtcNow;

            var status = await db.UserFileReadStatuses
                .FirstOrDefaultAsync(s => s.UserId == userId && s.FilePath == filePath, cancellationToken);
            if (status is null)
            {
                status = new UserFileReadStatusEntity { UserId = userId, FilePath = filePath, CreatedAt = now };
                db.UserFileReadStatuses.Add(status);
            }

            status.CurrentPage = targetPage;
            status.LastReadDate = now;
            status.UpdatedAt = now;

            if (totalPages > 0)
            {
                var entity = await db.ReadingProgresses
                    .FirstOrDefaultAsync(p => p.UserId == userId && p.ContentId == filePath, cancellationToken);
                if (entity is null)
                {
                    entity = new ReadingProgressEntity { UserId = userId, ContentId = filePath, CreatedAt = now };
                    db.ReadingProgresses.Add(entity);
                }

                var progress = MapToModel(entity);
                progress.TotalPages = totalPages;
                ReadingProgressCalculator.ApplyProgress(progress, targetPage, now);
                entity.CurrentPage = progress.CurrentPage;
                entity.TotalPages = progress.TotalPages;
                entity.PercentComplete = progress.PercentComplete;
                entity.LastReadAt = progress.LastReadAt;
                entity.CompletedAt = progress.CompletedAt;
                entity.UpdatedAt = now;
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateException ex) when (attempt == 1 && IsUniqueConstraintViolation(ex))
            {
                // Lost an insert race: start over against the rows that now exist.
                db.ChangeTracker.Clear();
                _logger.LogDebug("Reading progress row was created concurrently; retrying the save as an update.");
            }
        }
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException ex) =>
        ex.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 };

    private static ReadingProgress MapToModel(ReadingProgressEntity entity) => new()
    {
        UserId = entity.UserId,
        ContentId = entity.ContentId,
        CurrentPage = entity.CurrentPage,
        TotalPages = entity.TotalPages,
        PercentComplete = entity.PercentComplete,
        LastReadAt = entity.LastReadAt,
        CompletedAt = entity.CompletedAt,
        LastReaderMode = (ReaderMode)entity.LastReaderMode,
        ReadingDirection = (ReadingDirection)entity.ReadingDirection
    };
}
