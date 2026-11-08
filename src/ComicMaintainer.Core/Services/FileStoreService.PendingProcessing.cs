using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// The file watcher's durable record of the files it has queued for automatic processing, so
/// that work queued when the application stops is picked up again at the next start.
/// </summary>
public partial class FileStoreService
{
    /// <inheritdoc />
    public async Task SetProcessingPendingAsync(string filePath, bool pending, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            var entity = await dbContext.ComicFiles
                .FirstOrDefaultAsync(e => e.FilePath == filePath, cancellationToken);
            if (entity is null || entity.IsProcessingPending == pending)
            {
                return;
            }

            // Only this column changes, so a concurrent update of the row's other columns (the
            // processor stamping it, for one) is not overwritten.
            entity.IsProcessingPending = pending;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // The row was deleted meanwhile: the file is gone, so nothing is pending for it.
            _logger.LogDebug(ex, "File removed before its pending-processing flag was stored: {FilePath}", SanitizeForLogging(filePath));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Error storing the pending-processing flag of {FilePath}", SanitizeForLogging(filePath));
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetProcessingPendingFilesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            return await dbContext.ComicFiles
                .AsNoTracking()
                .Where(e => e.IsProcessingPending)
                .OrderBy(e => e.FilePath)
                .Select(e => e.FilePath)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error reading the files pending automatic processing");
            return [];
        }
    }
}
