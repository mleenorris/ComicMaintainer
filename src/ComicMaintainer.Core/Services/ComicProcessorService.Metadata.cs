using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using System.Xml;
using System.Xml.Linq;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Writers.Zip;

namespace ComicMaintainer.Core.Services;

public partial class ComicProcessorService
{
    /// <summary>
    /// The ComicInfo.xml fields normalize owns. A normalize write changes only these elements.
    /// </summary>
    private const ComicMetadataFieldFlags NormalizedFields =
        ComicMetadataFieldFlags.Series | ComicMetadataFieldFlags.Title | ComicMetadataFieldFlags.Issue;

    public Task<ComicMetadata?> GetMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!TryResolveAllowedPath(filePath, out var resolvedPath))
                return Task.FromResult<ComicMetadata?>(null);

            return GetMetadataAtResolvedPath(resolvedPath, filePath, cancellationToken);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Error reading metadata from {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return Task.FromResult<ComicMetadata?>(null);
        }
    }

    private Task<ComicMetadata?> GetMetadataAtResolvedPath(string resolvedPath, string fallbackFilePath, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!TryResolveAllowedPath(resolvedPath, out var currentPath) ||
                !string.Equals(currentPath, resolvedPath, StringComparison.Ordinal) ||
                !File.Exists(resolvedPath) ||
                !IsComicArchive(resolvedPath))
            {
                return Task.FromResult<ComicMetadata?>(null);
            }

            using var archive = ArchiveFactory.OpenArchive(resolvedPath);

            // Look for ComicInfo.xml
            var comicInfoEntry = FindComicInfoEntry(archive, resolvedPath);

            var metadata = comicInfoEntry != null
                ? ParseComicInfoXml(ReadComicInfoXml(comicInfoEntry))
                : new ComicMetadata
                {
                    // Fallback: parse from the folder and file name
                    Series = ExtractSeriesFromFilename(fallbackFilePath),
                    Issue = IssueNumberParser.Parse(Path.GetFileNameWithoutExtension(fallbackFilePath))
                };

            return ComicFileExtensions.IsWritableArchive(resolvedPath)
                ? Task.FromResult(metadata)
                : ApplyPendingDatabaseEditAsync(fallbackFilePath, metadata, cancellationToken);
        }
        catch (ArchiveLimitExceededException ex)
        {
            LogArchiveRefused(fallbackFilePath, ex);
            return Task.FromResult<ComicMetadata?>(null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Error reading metadata from {FilePath}", LoggingHelper.SanitizePathForLog(fallbackFilePath));
            return Task.FromResult<ComicMetadata?>(null);
        }
    }

    public async Task<SeriesMetadata?> GetSeriesMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!TryResolveAllowedPath(filePath, out var resolvedPath))
                return null;

            if (!File.Exists(resolvedPath) || !IsComicArchive(resolvedPath))
                return null;

            SeriesMetadata? metadata;
            using (var archive = ArchiveFactory.OpenArchive(resolvedPath))
            {
                var comicInfoEntry = FindComicInfoEntry(archive, resolvedPath);
                metadata = comicInfoEntry != null
                    ? ParseSeriesMetadataXml(ReadComicInfoXml(comicInfoEntry))
                    : new SeriesMetadata
                    {
                        Series = ExtractSeriesFromFilename(filePath),
                        Issue = IssueNumberParser.Parse(Path.GetFileNameWithoutExtension(filePath)),
                        SeriesGroup = Path.GetFileName(Path.GetDirectoryName(filePath))
                    };
            }

            if (metadata is null || ComicFileExtensions.IsWritableArchive(resolvedPath))
            {
                return metadata;
            }

            var pending = await GetPendingDatabaseEditAsync(filePath, cancellationToken);
            if (pending?.Metadata is { } stored)
            {
                var locked = (ComicMetadataFieldFlags)stored.UserLockedFieldsMask;
                if (locked.HasFlag(ComicMetadataFieldFlags.Series)) metadata.Series = stored.Series;
                if (locked.HasFlag(ComicMetadataFieldFlags.Title)) metadata.Title = stored.Title;
                if (locked.HasFlag(ComicMetadataFieldFlags.Issue)) metadata.Issue = stored.Issue;
                if (locked.HasFlag(ComicMetadataFieldFlags.Volume)) metadata.Volume = stored.Volume;
                if (locked.HasFlag(ComicMetadataFieldFlags.Publisher)) metadata.Publisher = stored.Publisher;
                if (locked.HasFlag(ComicMetadataFieldFlags.Year)) metadata.Year = stored.Year;
            }

            return metadata;
        }
        catch (ArchiveLimitExceededException ex)
        {
            LogArchiveRefused(filePath, ex);
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Error reading series metadata from {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return null;
        }
    }

    /// <summary>
    /// The tracked file at <paramref name="filePath"/> when it holds user edits that have not been
    /// written into its archive (as a CBR, RAR or ZIP edited while converting them to CBZ is
    /// turned off does), otherwise null. Failures to read the store count as no pending edit.
    /// </summary>
    private async Task<ComicFile?> GetPendingDatabaseEditAsync(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            var tracked = await _fileStore.GetFileAsync(filePath, cancellationToken);
            return tracked is { Metadata: { IsUserEdited: true, UserLockedFieldsMask: not 0 } } &&
                   tracked.MetadataVersion > tracked.WrittenMetadataVersion
                ? tracked
                : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not read the stored metadata of {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return null;
        }
    }

    /// <summary>
    /// Returns <paramref name="metadata"/> with the fields of pending user edits stored in the
    /// database (see <see cref="GetPendingDatabaseEditAsync"/>) put over the archive's values, so
    /// an edit kept in the database is what the file shows.
    /// </summary>
    private async Task<ComicMetadata?> ApplyPendingDatabaseEditAsync(
        string filePath,
        ComicMetadata? metadata,
        CancellationToken cancellationToken)
    {
        if (metadata is null)
        {
            return null;
        }

        var pending = await GetPendingDatabaseEditAsync(filePath, cancellationToken);
        if (pending?.Metadata is not { } stored)
        {
            return metadata;
        }

        var effective = metadata.Clone();
        ComicMetadataFields.Copy(stored, effective, (ComicMetadataFieldFlags)stored.UserLockedFieldsMask);
        return effective;
    }

    /// <summary>
    /// Applies the non-empty fields of <paramref name="metadata"/> as a metadata edit; fields it
    /// leaves empty are kept as they are. See <see cref="ApplyMetadataEditAsync"/>.
    /// </summary>
    public async Task<bool> UpdateMetadataAsync(string filePath, ComicMetadata metadata, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var result = await ApplyMetadataEditAsync(filePath, metadata, ComicMetadataFields.Supplied(metadata), cancellationToken);
        return result.Succeeded;
    }

    public Task<MetadataWriteResult> ApplyMetadataEditAsync(
        string filePath,
        ComicMetadata metadata,
        ComicMetadataFieldFlags fields,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return RunExclusiveAsync(
            filePath,
            token => UpdateMetadataCoreAsync(filePath, metadata, fields, token, userEdit: true),
            cancellationToken);
    }

    public Task<MetadataWriteResult> WriteMetadataAsync(
        string filePath,
        ComicMetadata metadata,
        ComicMetadataFieldFlags fields,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return RunExclusiveAsync(
            filePath,
            token => UpdateMetadataCoreAsync(filePath, metadata, fields, token),
            cancellationToken);
    }

    /// <summary>
    /// Merges the <paramref name="fields"/> of <paramref name="metadata"/> into the archive's
    /// existing ComicInfo.xml and rewrites the archive, keeping every other element. The archive
    /// is left untouched when it already holds these values. <paramref name="filePath"/> is the
    /// caller's (logical) path; it keys the file store, while all file I/O goes through its
    /// validated physical path.
    /// </summary>
    /// <remarks>
    /// Only a CBZ is rewritten in place. A CBR, RAR or ZIP is written as a new CBZ when
    /// <see cref="AppSettings.ConvertRarToCbzOnWrite"/> is on (see <see cref="RewriteArchiveAsync"/>);
    /// when it is off the archive is never touched, and a <paramref name="userEdit"/> is recorded
    /// in the database instead. Edits such an archive holds only in the database are written into
    /// the CBZ it is converted to: a user edit's own fields take precedence over them, an automatic
    /// write's (normalize, backfill) do not.
    /// </remarks>
    private async Task<MetadataWriteResult> UpdateMetadataCoreAsync(
        string filePath,
        ComicMetadata metadata,
        ComicMetadataFieldFlags fields,
        CancellationToken cancellationToken,
        bool userEdit = false)
    {
        try
        {
            if (!TryResolveAllowedPath(filePath, out var physicalPath))
            {
                _logger.LogWarning(
                    "Cannot update metadata: file is outside the allowed directories: {FilePath}",
                    LoggingHelper.SanitizePathForLog(filePath));
                return MetadataWriteResult.Failed(filePath);
            }

            if (!File.Exists(physicalPath))
            {
                _logger.LogWarning(
                    "Cannot update metadata: file does not exist: {FilePath}",
                    LoggingHelper.SanitizePathForLog(filePath));
                return MetadataWriteResult.Failed(filePath);
            }

            if (!IsComicArchive(physicalPath))
            {
                _logger.LogWarning(
                    "Cannot update metadata: file is not a supported comic archive: {FilePath}",
                    LoggingHelper.SanitizePathForLog(filePath));
                return MetadataWriteResult.Failed(filePath);
            }

            if (fields == ComicMetadataFieldFlags.None)
            {
                _logger.LogDebug("No metadata fields to write for {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                return new MetadataWriteResult(MetadataWriteOutcome.Unchanged, filePath);
            }

            var convert = !ComicFileExtensions.IsWritableArchive(physicalPath);
            if (convert && !CanConvertToCbz(filePath))
            {
                if (userEdit)
                {
                    return await KeepMetadataEditInDatabaseAsync(filePath, physicalPath, metadata, fields, cancellationToken);
                }

                _logger.LogDebug(
                    "Not writing metadata into {FilePath}: this CBR/RAR/ZIP archive is not converted to CBZ",
                    LoggingHelper.SanitizePathForLog(filePath));
                return new MetadataWriteResult(MetadataWriteOutcome.NotWritable, filePath);
            }

            var values = metadata;
            var writeFields = fields;
            var pending = convert ? await GetPendingDatabaseEditAsync(filePath, cancellationToken) : null;
            if (pending?.Metadata is { } stored)
            {
                var locked = (ComicMetadataFieldFlags)stored.UserLockedFieldsMask;
                var requested = userEdit ? fields : fields & ~locked;
                values = stored.Clone();
                ComicMetadataFields.Copy(metadata, values, requested);
                writeFields = requested | locked;
            }

            var existing = ReadComicInfoDocument(physicalPath);
            var updated = ComicInfoXml.Merge(existing, values, writeFields);
            if (ComicInfoXml.AreEquivalent(existing, updated) ||
                (existing is null && !updated.Root!.HasElements))
            {
                _logger.LogDebug(
                    "ComicInfo.xml already holds these values, leaving the archive unchanged: {FilePath}",
                    LoggingHelper.SanitizePathForLog(filePath));
                return new MetadataWriteResult(MetadataWriteOutcome.Unchanged, filePath);
            }

            _logger.LogInformation("Updating metadata for: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            var result = await RewriteArchiveAsync(filePath, physicalPath, ComicInfoXml.ToUtf8Bytes(updated), cancellationToken);
            if (pending is not null)
            {
                await CompletePendingDatabaseEditAsync(result.FilePath, pending, metadata, userEdit ? fields : ComicMetadataFieldFlags.None);
            }

            // Cache everything the archive now holds, not only the fields just written, so series
            // views show it without opening the archive.
            await _fileStore.RecordArchiveMetadataAsync(result.FilePath, ComicInfoXml.Read(updated) ?? values, cancellationToken);

            _logger.LogInformation("Successfully updated metadata for: {FilePath}", LoggingHelper.SanitizePathForLog(result.FilePath));
            return result;
        }
        catch (ArchiveLimitExceededException ex)
        {
            LogArchiveRefused(filePath, ex);
            return MetadataWriteResult.Failed(filePath);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Error updating metadata for {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return MetadataWriteResult.Failed(filePath);
        }
    }

    /// <summary>
    /// True when the CBR, RAR or ZIP at <paramref name="filePath"/> may be converted to CBZ: the
    /// setting is on and the library entry is the archive itself. A file symlink is never
    /// converted, because the conversion works on the physical file: it would create the CBZ next
    /// to the link's target and delete the target, leaving the link in the library dangling.
    /// Linked folders are fine, since the converted file stays reachable through them.
    /// </summary>
    private bool CanConvertToCbz(string filePath)
    {
        if (!_settings.ConvertRarToCbzOnWrite)
        {
            return false;
        }

        if (new FileInfo(filePath).LinkTarget is null)
        {
            return true;
        }

        _logger.LogWarning(
            "Not converting {FilePath} to CBZ: it is a symbolic link, and converting it would delete the file the link points to",
            LoggingHelper.SanitizePathForLog(filePath));
        return false;
    }

    /// <summary>
    /// Records a user's edit of an archive that is not rewritten (a CBR, RAR or ZIP that is not
    /// converted to CBZ, see <see cref="CanConvertToCbz"/>) in the file's stored metadata, queued
    /// for the write that eventually converts it. Reads of the file show it from then on.
    /// </summary>
    private async Task<MetadataWriteResult> KeepMetadataEditInDatabaseAsync(
        string filePath,
        string physicalPath,
        ComicMetadata metadata,
        ComicMetadataFieldFlags fields,
        CancellationToken cancellationToken)
    {
        if (await _fileStore.GetFileAsync(filePath, cancellationToken) is null)
        {
            _logger.LogWarning(
                "Cannot keep the metadata edit of {FilePath} in the database: the file is not tracked",
                LoggingHelper.SanitizePathForLog(filePath));
            return MetadataWriteResult.Failed(filePath);
        }

        // What the file shows now (its archive plus earlier edits kept in the database) with this
        // edit applied, so a first stored record is complete.
        var values = await GetMetadataAtResolvedPath(physicalPath, filePath, cancellationToken) ?? new ComicMetadata();
        ComicMetadataFields.Copy(metadata, values, fields);
        await _fileStore.RecordUserMetadataEditAsync(filePath, values, fields, pendingWrite: true, cancellationToken);
        _logger.LogInformation(
            "Kept the metadata edit of {FilePath} in the database: this CBR/RAR/ZIP archive is not converted to CBZ",
            LoggingHelper.SanitizePathForLog(filePath));
        return new MetadataWriteResult(MetadataWriteOutcome.KeptInDatabase, filePath);
    }

    /// <summary>
    /// After edits held only in the database were written into the archive now at
    /// <paramref name="filePath"/>, brings the stored record in line with what was written
    /// (the <paramref name="userFields"/> a user edit set on top of them) and marks it written.
    /// Best effort: the archive is already correct.
    /// </summary>
    private async Task CompletePendingDatabaseEditAsync(
        string filePath,
        ComicFile pending,
        ComicMetadata metadata,
        ComicMetadataFieldFlags userFields)
    {
        try
        {
            var locked = (ComicMetadataFieldFlags)(pending.Metadata?.UserLockedFieldsMask ?? 0L);
            if ((userFields & locked) != ComicMetadataFieldFlags.None)
            {
                await _fileStore.RecordUserMetadataEditAsync(filePath, metadata, userFields & locked, pendingWrite: false);
            }

            await _fileStore.MarkFileBackfilledAsync(filePath, pending.MetadataVersion);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(
                ex,
                "Wrote the metadata kept in the database into {FilePath} but could not mark it written",
                LoggingHelper.SanitizePathForLog(filePath));
        }
    }

    public Task<MetadataWriteResult> RemoveMetadataAsync(string filePath, CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(filePath, token => RemoveMetadataCoreAsync(filePath, token), cancellationToken);

    private async Task<MetadataWriteResult> RemoveMetadataCoreAsync(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            if (!TryResolveAllowedPath(filePath, out var physicalPath))
            {
                _logger.LogWarning(
                    "Cannot remove metadata: file is outside the allowed directories: {FilePath}",
                    LoggingHelper.SanitizePathForLog(filePath));
                return MetadataWriteResult.Failed(filePath);
            }

            if (!File.Exists(physicalPath))
            {
                _logger.LogWarning(
                    "Cannot remove metadata: file does not exist: {FilePath}",
                    LoggingHelper.SanitizePathForLog(filePath));
                return MetadataWriteResult.Failed(filePath);
            }

            if (!IsComicArchive(physicalPath))
            {
                _logger.LogWarning(
                    "Cannot remove metadata: file is not a supported comic archive: {FilePath}",
                    LoggingHelper.SanitizePathForLog(filePath));
                return MetadataWriteResult.Failed(filePath);
            }

            if (!HasComicInfoEntry(physicalPath))
            {
                _logger.LogInformation("No metadata to remove from: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                return new MetadataWriteResult(MetadataWriteOutcome.Unchanged, filePath);
            }

            if (!ComicFileExtensions.IsWritableArchive(physicalPath) && !CanConvertToCbz(filePath))
            {
                _logger.LogWarning(
                    "Cannot remove metadata from {FilePath}: this CBR/RAR/ZIP archive is not converted to CBZ, so it is not rewritten",
                    LoggingHelper.SanitizePathForLog(filePath));
                return MetadataWriteResult.Failed(filePath);
            }

            _logger.LogInformation("Removing metadata for: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            var result = await RewriteArchiveAsync(filePath, physicalPath, comicInfoXml: null, cancellationToken);
            _logger.LogInformation("Successfully removed metadata for: {FilePath}", LoggingHelper.SanitizePathForLog(result.FilePath));
            return result;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Error removing metadata for {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return MetadataWriteResult.Failed(filePath);
        }
    }

    /// <summary>
    /// Writes the archive at <paramref name="physicalPath"/> again with <paramref name="comicInfoXml"/>
    /// as its ComicInfo.xml (none when null) and puts it in place of the original. A CBZ is
    /// replaced; any other archive is converted (see <see cref="ConvertToCbzAsync"/>).
    /// </summary>
    /// <remarks>
    /// The new archive is built in the original's own directory, under a temp name the file
    /// watcher and the library scan ignore, and flushed to disk before a same-directory rename
    /// replaces the original. A rename never crosses a mount point, so there is no copy fallback
    /// that truncates the original, and a crash at any point leaves either the old or the new
    /// archive intact (plus, at worst, a temp file that <see cref="ArchiveWriteRecoveryService"/>
    /// removes at the next startup).
    /// </remarks>
    private async Task<MetadataWriteResult> RewriteArchiveAsync(
        string logicalPath,
        string physicalPath,
        byte[]? comicInfoXml,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(physicalPath)
            ?? throw new IOException("Cannot determine the archive's directory.");
        var tempFile = ArchiveWriteRecoveryService.CreateTempPath(directory);

        try
        {
            // Write the new archive to the temp file; RewriteArchive releases all
            // file handles before returning, ahead of the file replacement.
            RewriteArchive(physicalPath, tempFile, comicInfoXml);

            // Give a brief moment for any file handles to be fully released
            await Task.Delay(FileHandleReleaseDelayMs, cancellationToken);

            if (!ComicFileExtensions.IsWritableArchive(physicalPath))
            {
                return await ConvertToCbzAsync(logicalPath, physicalPath, tempFile, cancellationToken);
            }

            // Atomically replace the original, retrying transient file locks
            // from file system watchers, readers or antivirus.
            await ReplaceFileWithRetryAsync(tempFile, physicalPath, cancellationToken);
            await RefreshStoredFileStatsAsync(logicalPath, cancellationToken);
            return new MetadataWriteResult(MetadataWriteOutcome.Written, logicalPath);
        }
        finally
        {
            // Clean up temp file if it still exists
            if (File.Exists(tempFile))
            {
                try
                {
                    File.Delete(tempFile);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Failed to delete temp file: {TempFile}", LoggingHelper.SanitizePathForLog(tempFile));
                }
            }
        }
    }

    /// <summary>
    /// Puts the rewritten archive <paramref name="tempFile"/> (a ZIP) next to the CBR, RAR or
    /// ZIP at <paramref name="physicalPath"/> as <c>&lt;name&gt;.cbz</c>, moves the file's library
    /// record (processing state, stored metadata, every user's read status and reading progress)
    /// to it and deletes the original.
    /// </summary>
    /// <remarks>
    /// The CBZ is created by a same-directory rename that never replaces an existing file, so a
    /// CBZ already there fails the conversion and both files are kept. The original is deleted
    /// only once the CBZ is in place and the record has moved. When the record cannot be moved
    /// or the original cannot be deleted, the conversion is undone (record moved back, CBZ
    /// removed) so the library never holds the file twice and later writes to the original do
    /// not fail on a leftover CBZ. Past the rename the steps are not cancelled, so a cancelled
    /// job cannot leave a half-finished conversion.
    /// </remarks>
    private async Task<MetadataWriteResult> ConvertToCbzAsync(
        string logicalPath,
        string physicalPath,
        string tempFile,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(physicalPath)
            ?? throw new IOException("Cannot determine the archive's directory.");
        var cbzName = Path.GetFileNameWithoutExtension(physicalPath) + ".cbz";
        var cbzPhysicalPath = Path.Join(directory, cbzName);
        var cbzLogicalPath = Path.Join(GetAllowedLogicalPath(directory), cbzName);
        if (Path.Exists(cbzPhysicalPath))
        {
            throw new IOException($"Cannot convert the archive to CBZ: {cbzName} already exists next to it.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Pins the checked directory for both the rename and the delete below.
        using var directoryHandle = OpenDeleteDirectory(physicalPath);
        var movedPath = MoveFileInRenameDirectory(OperatingSystem.IsLinux() ? directoryHandle : null, tempFile, cbzPhysicalPath);

        try
        {
            await _fileStore.UpdateFilePathAsync(logicalPath, cbzLogicalPath, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(
                ex,
                "Could not move the library record of {FilePath} to its new CBZ; keeping the original and removing the CBZ",
                LoggingHelper.SanitizePathForLog(logicalPath));
            RemoveUndoneCbz(directoryHandle, movedPath, cbzLogicalPath);
            throw;
        }

        try
        {
            DeleteFileInDirectory(directoryHandle, physicalPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex,
                "Could not delete {FilePath} after converting it to CBZ; keeping the original and removing the new CBZ",
                LoggingHelper.SanitizePathForLog(logicalPath));
            await MoveRecordBackAfterUndoneConversionAsync(cbzLogicalPath, logicalPath);
            RemoveUndoneCbz(directoryHandle, movedPath, cbzLogicalPath);
            throw;
        }

        await RefreshStoredFileStatsAsync(cbzLogicalPath, CancellationToken.None);
        _logger.LogInformation(
            "Converted {FilePath} to {CbzPath}",
            LoggingHelper.SanitizePathForLog(logicalPath),
            LoggingHelper.SanitizePathForLog(cbzLogicalPath));
        return new MetadataWriteResult(MetadataWriteOutcome.ConvertedToCbz, cbzLogicalPath);
    }

    /// <summary>
    /// Moves a file's library record back to the original after its conversion had to be undone.
    /// Best effort, so the new CBZ still gets removed and the caller still sees the first failure;
    /// when this fails, the next library scan picks the original up again.
    /// </summary>
    private async Task MoveRecordBackAfterUndoneConversionAsync(string cbzLogicalPath, string logicalPath)
    {
        try
        {
            await _fileStore.UpdateFilePathAsync(cbzLogicalPath, logicalPath, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(
                ex,
                "Could not move the library record back to {FilePath} after its conversion was undone",
                LoggingHelper.SanitizePathForLog(logicalPath));
        }
    }

    /// <summary>Removes the CBZ of a conversion that had to be undone. Best effort.</summary>
    private void RemoveUndoneCbz(SafeFileHandle directoryHandle, string cbzPhysicalPath, string cbzLogicalPath)
    {
        try
        {
            DeleteFileInDirectory(directoryHandle, cbzPhysicalPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex,
                "Could not remove {CbzPath} after the conversion was undone",
                LoggingHelper.SanitizePathForLog(cbzLogicalPath));
        }
    }

    /// <summary>
    /// Reads the archive's ComicInfo.xml, or returns null when it has none. Throws
    /// <see cref="InvalidDataException"/> when the document is malformed, so a write never
    /// replaces metadata it could not read.
    /// </summary>
    private static XDocument? ReadComicInfoDocument(string physicalPath)
    {
        using var archive = ArchiveFactory.OpenArchive(physicalPath);
        var entry = FindComicInfoEntry(archive, physicalPath);
        if (entry is null)
        {
            return null;
        }

        var xml = ReadComicInfoXml(entry);
        try
        {
            return ComicInfoXml.Parse(xml);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException(
                "The archive's ComicInfo.xml is malformed, so it is left as it is instead of being replaced.", ex);
        }
    }

    private static bool HasComicInfoEntry(string physicalPath)
    {
        using var archive = ArchiveFactory.OpenArchive(physicalPath);
        return FindComicInfoEntry(archive, physicalPath) is not null;
    }

    private static IArchiveEntry? FindComicInfoEntry(IArchive archive, string archivePath) =>
        ArchiveEntryReader.EnumerateEntries(archive, Path.GetFileName(archivePath)).FirstOrDefault(e =>
            e.Key?.Equals(ComicInfoXml.EntryName, StringComparison.OrdinalIgnoreCase) == true);

    /// <summary>
    /// Writes a zip to <paramref name="destinationPath"/> holding every non-directory entry of
    /// <paramref name="sourcePath"/> except ComicInfo.xml (same order, names and timestamps),
    /// followed by <paramref name="comicInfoXml"/> as ComicInfo.xml when it is not null.
    /// Entries are streamed one at a time, so memory use does not grow with entry or archive size.
    /// Throws if any entry cannot be copied, so callers never replace the original with an
    /// archive that is missing pages. The destination is created new (never overwritten), gets
    /// the source's Unix permissions, and is flushed to disk before this returns. A RAR source
    /// (being converted to CBZ) is read in order when it is solid, and its entry names get ZIP's
    /// '/' separators.
    /// </summary>
    private void RewriteArchive(string sourcePath, string destinationPath, byte[]? comicInfoXml)
    {
        using var sourceArchive = ArchiveFactory.OpenArchive(sourcePath);
        var normalizeSeparators = sourceArchive.Type != ArchiveType.Zip;
        using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using (var writer = new ZipWriter(destination, new ZipWriterOptions(CompressionType.Deflate) { LeaveStreamOpen = true }))
        {
            if (sourceArchive.IsSolid)
            {
                // A solid archive can only be decompressed from the start.
                using var reader = sourceArchive.ExtractAllEntries();
                while (reader.MoveToNextEntry())
                {
                    if (!reader.Entry.IsDirectory)
                    {
                        CopyArchiveEntry(writer, reader.Entry, reader.OpenEntryStream, normalizeSeparators);
                    }
                }
            }
            else
            {
                foreach (var entry in sourceArchive.Entries.Where(e => !e.IsDirectory))
                {
                    CopyArchiveEntry(writer, entry, entry.OpenEntryStream, normalizeSeparators);
                }
            }

            if (comicInfoXml != null)
            {
                using var xmlStream = new MemoryStream(comicInfoXml);
                writer.Write(ComicInfoXml.EntryName, xmlStream, DateTime.Now);
            }
        }

        // The zip is complete once the writer has written its central directory. Make it durable
        // before it is renamed over the original, so a power loss cannot leave an empty file there.
        destination.Flush(flushToDisk: true);
        CopyUnixFileMode(sourcePath, destinationPath);
    }

    /// <summary>
    /// Copies one file entry into <paramref name="writer"/> under its own name and timestamp,
    /// skipping the old ComicInfo.xml. Throws for an entry without a name.
    /// </summary>
    private static void CopyArchiveEntry(ZipWriter writer, IEntry entry, Func<Stream> openEntryStream, bool normalizeSeparators)
    {
        if (entry.Key == null)
        {
            throw new InvalidDataException("The archive contains an entry without a name, which cannot be copied.");
        }

        var name = normalizeSeparators ? entry.Key.Replace('\\', '/') : entry.Key;
        if (name.Equals(ComicInfoXml.EntryName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        using var entryStream = openEntryStream();
        writer.Write(name, entryStream, entry.LastModifiedTime);
    }

    /// <summary>
    /// Gives the rewritten archive the original's permission bits, so a replace does not change
    /// who may read the file. Best effort; ownership stays with the application's user.
    /// </summary>
    private void CopyUnixFileMode(string sourcePath, string destinationPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(destinationPath, File.GetUnixFileMode(sourcePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not copy the permissions of {FilePath} to its rewritten archive", LoggingHelper.SanitizePathForLog(sourcePath));
        }
    }

    /// <summary>
    /// Points <paramref name="filePath"/> and <paramref name="resolvedPath"/> at
    /// <paramref name="newFilePath"/>, where a metadata write left the file (a converted archive
    /// becomes a new CBZ). Returns false, leaving both unchanged, when the new path does not
    /// resolve inside the library.
    /// </summary>
    private bool FollowMovedFile(string newFilePath, ref string filePath, ref string resolvedPath)
    {
        if (string.Equals(newFilePath, filePath, StringComparison.Ordinal))
        {
            return true;
        }

        if (!TryResolveAllowedPath(newFilePath, out var movedPath))
        {
            _logger.LogWarning(
                "The rewritten archive {FilePath} does not resolve inside the library",
                LoggingHelper.SanitizePathForLog(newFilePath));
            return false;
        }

        filePath = newFilePath;
        resolvedPath = movedPath;
        return true;
    }

    /// <summary>
    /// Keeps the tracked FileSize/LastModified in sync after an in-place
    /// archive rewrite, so the library scan's modified-file detection
    /// doesn't flag our own writes. Best-effort.
    /// </summary>
    private async Task RefreshStoredFileStatsAsync(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            await _fileStore.RefreshFileStatsAsync(new[] { filePath }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Failed to refresh stored file stats for {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
        }
    }
}
