using ComicMaintainer.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace ComicMaintainer.Core.Services;

public partial class FileStoreService
{
    public async Task RecordUserMetadataEditAsync(
        string filePath,
        ComicMetadata values,
        ComicMetadataFieldFlags fields,
        bool pendingWrite,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await dbContext.ComicFiles.FirstOrDefaultAsync(e => e.FilePath == filePath, cancellationToken);
        if (entity is null)
        {
            throw new InvalidOperationException($"File is not tracked: {filePath}");
        }

        var now = DateTime.UtcNow;
        if (entity.Metadata is null)
        {
            // The first stored metadata is the whole snapshot, so readers of the stored record
            // (the series library) see the file's other fields too, not just the edited ones.
            entity.Metadata = values.Clone();
        }
        else
        {
            ComicMetadataFields.Copy(values, entity.Metadata, fields);
        }

        entity.Metadata.IsUserEdited = true;
        entity.Metadata.UserLockedFieldsMask |= (long)fields;
        if (pendingWrite && entity.MetadataVersion < int.MaxValue)
        {
            entity.MetadataVersion++;
        }

        entity.LastDbEditAt = now;
        entity.MetadataSource = FileMetadataSource.UserEdit.ToString();
        entity.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);
        MarkChanged();

        var updated = ToComicFile(entity);
        _files.AddOrUpdate(filePath, updated, (_, _) => updated);
        MarkChanged();
        await BroadcastFileListUpdateSafeAsync();
    }
}
