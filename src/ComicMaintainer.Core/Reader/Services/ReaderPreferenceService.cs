using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Reader.Interfaces;
using ComicMaintainer.Core.Reader.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Reader.Services;

/// <summary>
/// Persists and retrieves per-user reader preferences using EF Core.
/// Returns sensible defaults when no preferences have been saved.
/// </summary>
public class ReaderPreferenceService : IReaderPreferenceService
{
    private readonly IDbContextFactory<ComicMaintainerDbContext> _dbContextFactory;
    private readonly ILogger<ReaderPreferenceService> _logger;

    public ReaderPreferenceService(
        IDbContextFactory<ComicMaintainerDbContext> dbContextFactory,
        ILogger<ReaderPreferenceService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<ReaderPreferences> GetPreferencesAsync(string userId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.ReaderPreferences
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);

        // Return defaults when no row exists yet
        if (entity is null)
        {
            return new ReaderPreferences { UserId = userId };
        }

        return MapToModel(entity);
    }

    /// <inheritdoc/>
    public async Task SavePreferencesAsync(ReaderPreferences preferences, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.ReaderPreferences
            .FirstOrDefaultAsync(p => p.UserId == preferences.UserId, cancellationToken);

        if (entity is null)
        {
            entity = new ReaderPreferencesEntity
            {
                UserId = preferences.UserId,
                CreatedAt = DateTime.UtcNow
            };
            db.ReaderPreferences.Add(entity);
        }

        entity.DefaultReaderMode = (int)preferences.DefaultReaderMode;
        entity.ReadingDirection = (int)preferences.ReadingDirection;
        entity.TapZonesEnabled = preferences.TapZonesEnabled;
        entity.AutoHideChrome = preferences.AutoHideChrome;
        entity.FitPreference = preferences.FitPreference;
        entity.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);

        _logger.LogDebug("Saved reader preferences for user {UserId}", preferences.UserId);
    }

    private static ReaderPreferences MapToModel(ReaderPreferencesEntity entity) => new()
    {
        UserId = entity.UserId,
        DefaultReaderMode = (ReaderMode)entity.DefaultReaderMode,
        ReadingDirection = (ReadingDirection)entity.ReadingDirection,
        TapZonesEnabled = entity.TapZonesEnabled,
        AutoHideChrome = entity.AutoHideChrome,
        FitPreference = entity.FitPreference
    };
}
