using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// The archive-metadata cache: <c>ComicFiles.Metadata</c> holds what the archive's
/// ComicInfo says (series, title, issue, volume, year, publisher, ...), stamped with the
/// stored size and modification time it was read at, so the library is built from the
/// database without opening archives during requests.
/// </summary>
/// <remarks>
/// The cache is filled from both directions: the processor records what it writes into an
/// archive (<see cref="RecordArchiveMetadataAsync"/>), and the background archive-metadata
/// scan reads every file whose cached metadata is missing or stale
/// (<see cref="GetFilesNeedingMetadataScanAsync"/>,
/// <see cref="RecordScannedArchiveMetadataAsync"/>). A user edit that has not been written to
/// the archive yet (<c>MetadataVersion</c> above <c>WrittenMetadataVersion</c>) is newer than
/// the archive, so it is never overwritten; the user's edit flags and field locks are kept.
/// </remarks>
public partial class FileStoreService
{
    /// <inheritdoc />
    public async Task RecordArchiveMetadataAsync(string filePath, ComicMetadata metadata, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        try
        {
            if (await RecordArchiveMetadataCoreAsync(filePath, metadata, expected: null, cancellationToken))
            {
                MarkChanged();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Never fail the write that already happened: the stamp no longer matches the
            // file, so the background scan reads the archive instead.
            _logger.LogDebug(ex, "Could not cache the metadata written to {FilePath}; the background scan will read it",
                SanitizeForLogging(filePath));
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ArchiveMetadataScanCandidate>> GetFilesNeedingMetadataScanAsync(
        int afterId,
        int max,
        CancellationToken cancellationToken = default)
    {
        if (max <= 0)
        {
            return Array.Empty<ArchiveMetadataScanCandidate>();
        }

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.ComicFiles
            .AsNoTracking()
            .Where(e => e.Id > afterId)
            .Where(e => e.MetadataScannedSize == null
                || e.MetadataScannedModified == null
                || e.MetadataScannedSize != e.FileSize
                || e.MetadataScannedModified != e.LastModified)
            // A user edit waiting to be written is newer than the archive.
            .Where(e => e.MetadataVersion <= e.WrittenMetadataVersion)
            .OrderBy(e => e.Id)
            .Take(max)
            .Select(e => new ArchiveMetadataScanCandidate(e.Id, e.FilePath, e.FileSize, e.LastModified))
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> RecordScannedArchiveMetadataAsync(
        ArchiveMetadataScanCandidate candidate,
        ComicMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(metadata);

        return await RecordArchiveMetadataCoreAsync(candidate.FilePath, metadata, candidate, cancellationToken);
    }

    /// <inheritdoc />
    public void PublishScannedArchiveMetadata() => MarkChanged();

    /// <summary>
    /// Stores <paramref name="metadata"/> as the file's cached archive metadata, stamped with
    /// the row's stored size and modification time. The check and the write run in one
    /// transaction, so a user edit that lands at the same time is never overwritten.
    /// </summary>
    /// <param name="expected">
    /// For a scan: the row as it was selected. Nothing is recorded when the row has changed
    /// since (the file was modified or replaced while it was being read).
    /// </param>
    /// <returns>True when the metadata was recorded.</returns>
    private async Task<bool> RecordArchiveMetadataCoreAsync(
        string filePath,
        ComicMetadata metadata,
        ArchiveMetadataScanCandidate? expected,
        CancellationToken cancellationToken)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var entity = await dbContext.ComicFiles.FirstOrDefaultAsync(e => e.FilePath == filePath, cancellationToken);
        if (entity is null)
        {
            return false;
        }

        if (expected is not null
            && (entity.Id != expected.Id || entity.FileSize != expected.FileSize || entity.LastModified != expected.LastModified))
        {
            return false;
        }

        if (entity.MetadataVersion > entity.WrittenMetadataVersion)
        {
            return false;
        }

        // Copy onto the row's own metadata instance (an owned entity) rather than replacing
        // it; the user's edit flag and field locks describe intent, not archive content.
        entity.Metadata ??= new ComicMetadata();
        CopyArchiveMetadata(entity.Metadata, metadata);
        entity.MetadataScannedSize = entity.FileSize;
        entity.MetadataScannedModified = entity.LastModified;
        entity.UpdatedAt = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await MirrorCommittedRowAsync(entity);
        return true;
    }

    /// <summary>Replaces every archive-backed field of <paramref name="target"/> with <paramref name="source"/>'s.</summary>
    private static void CopyArchiveMetadata(ComicMetadata target, ComicMetadata source)
    {
        target.Series = source.Series;
        target.Title = source.Title;
        target.Issue = source.Issue;
        target.Volume = source.Volume;
        target.Publisher = source.Publisher;
        target.Year = source.Year;
        target.Summary = source.Summary;
        // New list instances, so change tracking sees the converted columns change.
        target.Authors = new List<string>(source.Authors);
        target.Tags = new List<string>(source.Tags);
    }
}
