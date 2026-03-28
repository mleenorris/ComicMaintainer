using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Reader.Interfaces;
using ComicMaintainer.Core.Reader.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Reader.Services;

/// <summary>
/// Manages reading session lifecycle. Incognito sessions are persisted for
/// analytics but must not overwrite durable <see cref="ReadingProgress"/>.
/// </summary>
public class ReadingSessionService : IReadingSessionService
{
    private readonly IDbContextFactory<ComicMaintainerDbContext> _dbContextFactory;
    private readonly IReadingProgressService _progressService;
    private readonly ILogger<ReadingSessionService> _logger;

    public ReadingSessionService(
        IDbContextFactory<ComicMaintainerDbContext> dbContextFactory,
        IReadingProgressService progressService,
        ILogger<ReadingSessionService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _progressService = progressService;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<ReadingSession> StartSessionAsync(
        string userId,
        string contentId,
        int startPage,
        bool incognito = false,
        CancellationToken cancellationToken = default)
    {
        var session = new ReadingSession
        {
            SessionId = Guid.NewGuid(),
            UserId = userId,
            ContentId = contentId,
            StartedAt = DateTime.UtcNow,
            StartPage = startPage,
            Incognito = incognito
        };

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        db.ReadingSessions.Add(new ReadingSessionEntity
        {
            SessionId = session.SessionId,
            UserId = session.UserId,
            ContentId = session.ContentId,
            StartedAt = session.StartedAt,
            StartPage = session.StartPage,
            Incognito = session.Incognito
        });
        await db.SaveChangesAsync(cancellationToken);

        _logger.LogDebug(incognito
            ? "Started incognito reading session {SessionId}."
            : "Started reading session {SessionId}.",
            session.SessionId);

        return session;
    }

    /// <inheritdoc/>
    public async Task<ReadingSession?> EndSessionAsync(Guid sessionId, int endPage, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.ReadingSessions
            .FirstOrDefaultAsync(s => s.SessionId == sessionId, cancellationToken);

        if (entity is null)
        {
            _logger.LogWarning("Attempted to end unknown reading session {SessionId}", sessionId);
            return null;
        }

        entity.EndedAt = DateTime.UtcNow;
        entity.EndPage = endPage;
        await db.SaveChangesAsync(cancellationToken);

        var session = MapToModel(entity);

        // Non-incognito sessions update durable progress
        if (!entity.Incognito)
        {
            var existing = await _progressService.GetProgressAsync(entity.UserId, entity.ContentId, cancellationToken);
            if (existing is not null && endPage > existing.CurrentPage)
            {
                ReadingProgressCalculator.ApplyProgress(existing, endPage, entity.EndedAt.Value);
                await _progressService.SaveProgressAsync(existing, cancellationToken);
            }
        }

        _logger.LogDebug("Ended reading session {SessionId} at page {Page}", sessionId, endPage);
        return session;
    }

    private static ReadingSession MapToModel(ReadingSessionEntity entity) => new()
    {
        SessionId = entity.SessionId,
        UserId = entity.UserId,
        ContentId = entity.ContentId,
        StartedAt = entity.StartedAt,
        EndedAt = entity.EndedAt,
        StartPage = entity.StartPage,
        EndPage = entity.EndPage,
        Incognito = entity.Incognito
    };
}
