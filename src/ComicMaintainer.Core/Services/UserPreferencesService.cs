using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Persists and retrieves per-user UI preferences using EF Core.
/// Mirrors the durable pattern used by <c>ReaderPreferenceService</c>.
/// </summary>
public class UserPreferencesService : IUserPreferencesService
{
    private readonly IDbContextFactory<ComicMaintainerDbContext> _dbContextFactory;
    private readonly ILogger<UserPreferencesService> _logger;

    public UserPreferencesService(
        IDbContextFactory<ComicMaintainerDbContext> dbContextFactory,
        ILogger<UserPreferencesService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<UserPreferences> GetPreferencesAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(userId))
        {
            throw new ArgumentException("User id is required.", nameof(userId));
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.UserPreferences
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);

        // No row yet: everything is unset so the caller applies app defaults.
        return entity is null
            ? new UserPreferences { UserId = userId }
            : MapToModel(entity);
    }

    /// <inheritdoc/>
    public async Task<UserPreferences> SavePreferencesAsync(UserPreferences update, CancellationToken cancellationToken = default)
    {
        if (update is null)
        {
            throw new ArgumentNullException(nameof(update));
        }

        if (string.IsNullOrEmpty(update.UserId))
        {
            throw new ArgumentException("User id is required.", nameof(update));
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.UserPreferences
            .FirstOrDefaultAsync(p => p.UserId == update.UserId, cancellationToken);

        var isNew = false;

        if (entity is null)
        {
            isNew = true;
            entity = new UserPreferencesEntity
            {
                UserId = update.UserId,
                CreatedAt = DateTime.UtcNow
            };
            db.UserPreferences.Add(entity);
        }

        // Partial-update semantics: only overwrite properties the caller supplied.
        if (update.Theme is not null) entity.Theme = update.Theme;
        if (update.PerPage is not null) entity.PerPage = update.PerPage;
        if (update.ReadingMode is not null) entity.ReadingMode = update.ReadingMode;
        if (update.LibraryViewMode is not null) entity.LibraryViewMode = update.LibraryViewMode;
        if (update.FilterMode is not null) entity.FilterMode = update.FilterMode;
        if (update.SortMode is not null) entity.SortMode = update.SortMode;
        entity.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);

        _logger.LogDebug("Saved user preferences record {UserPreferencesId} ({Operation}).",
            entity.Id,
            isNew ? "created" : "updated");

        return MapToModel(entity);
    }

    private static UserPreferences MapToModel(UserPreferencesEntity entity) => new()
    {
        UserId = entity.UserId,
        Theme = entity.Theme,
        PerPage = entity.PerPage,
        ReadingMode = entity.ReadingMode,
        LibraryViewMode = entity.LibraryViewMode,
        FilterMode = entity.FilterMode,
        SortMode = entity.SortMode
    };
}
