using ComicMaintainer.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// The year a file's volume started. ComicInfo's single <c>&lt;Volume&gt;</c> element holds the
/// volume number, so the start year is kept in the database only (see
/// <see cref="ComicMetadata.VolumeYear"/>). Archive metadata writes and scans never touch it.
/// </summary>
public partial class FileStoreService
{
    /// <inheritdoc />
    public async Task<bool> SetVolumeYearAsync(string filePath, int? volumeYear, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await dbContext.ComicFiles.FirstOrDefaultAsync(e => e.FilePath == filePath, cancellationToken);
        if (entity is null)
        {
            return false;
        }

        entity.Metadata ??= new ComicMetadata();
        if (entity.Metadata.VolumeYear == volumeYear)
        {
            return true;
        }

        entity.Metadata.VolumeYear = volumeYear;
        entity.UpdatedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        await MirrorCommittedRowAsync(entity);
        MarkChanged();
        return true;
    }
}
