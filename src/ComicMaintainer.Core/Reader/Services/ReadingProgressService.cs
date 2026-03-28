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

        if (entity is null)
        {
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

        _logger.LogDebug("Saved reading progress for user {UserId}, content {ContentId}: page {Page}/{Total}",
            progress.UserId, progress.ContentId, progress.CurrentPage, progress.TotalPages);
    }

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
