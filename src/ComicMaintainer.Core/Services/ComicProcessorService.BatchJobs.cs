using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SharpCompress.Archives;
using SharpCompress.Archives.Zip;
using SharpCompress.Common;
using SharpCompress.Writers;
using SharpCompress.Writers.Zip;

namespace ComicMaintainer.Core.Services;

public partial class ComicProcessorService
{
    public Task<Guid> ProcessFilesAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default)
    {
        return ProcessFilesAsync(filePaths, false, cancellationToken);
    }

    public Task<Guid> ProcessFilesAsync(IEnumerable<string> filePaths, bool forceReprocess, CancellationToken cancellationToken = default)
    {
        return QueueBatchJobAsync(
            filePaths,
            "ProcessFilesAsync",
            "processing",
            async (filePath, token) => await ProcessFileCoreAsync(filePath, forceReprocess, token),
            "Processing failed",
            cancellationToken);
    }

    public Task<Guid> RenameFilesAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default)
    {
        return RenameFilesAsync(filePaths, false, cancellationToken);
    }

    public Task<Guid> RenameFilesAsync(IEnumerable<string> filePaths, bool forceReprocess, CancellationToken cancellationToken = default)
    {
        return QueueBatchJobAsync(
            filePaths,
            "RenameFilesAsync",
            "renaming",
            async (filePath, token) => await RenameFileCoreAsync(filePath, forceReprocess, token),
            "Rename failed",
            cancellationToken);
    }

    public Task<Guid> NormalizeFilesAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default)
    {
        return NormalizeFilesAsync(filePaths, false, cancellationToken);
    }

    public Task<Guid> NormalizeFilesAsync(IEnumerable<string> filePaths, bool forceReprocess, CancellationToken cancellationToken = default)
    {
        return QueueBatchJobAsync(
            filePaths,
            "NormalizeFilesAsync",
            "normalizing",
            async (filePath, token) => await NormalizeFileCoreAsync(filePath, forceReprocess, token),
            "Normalize failed",
            cancellationToken);
    }

    public Task<Guid> UpdateMetadataAsync(IEnumerable<string> filePaths, ComicMetadata metadata, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return UpdateMetadataAsync(filePaths, metadata, ComicMetadataFields.Supplied(metadata), cancellationToken);
    }

    public Task<Guid> UpdateMetadataAsync(
        IEnumerable<string> filePaths,
        ComicMetadata metadata,
        ComicMetadataFieldFlags fields,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        // Every file gets the same partial edit; fields outside the mask keep each file's own value.
        return QueueBatchJobAsync(
            filePaths,
            "UpdateMetadataAsync",
            "updating metadata for",
            async (file, token) => (await UpdateMetadataCoreAsync(file, metadata, fields, token, userEdit: true)).Succeeded,
            "Update metadata failed",
            cancellationToken);
    }

    public Task<Guid> RemoveMetadataFromFilesAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default)
    {
        return QueueBatchJobAsync(
            filePaths,
            "RemoveMetadataFromFilesAsync",
            "removing metadata from",
            async (filePath, token) =>
            {
                var removed = await RemoveMetadataCoreAsync(filePath, token);
                if (!removed.Succeeded)
                {
                    return false;
                }

                // Mark the file (at its new path, when a CBR/RAR became a CBZ) as
                // unprocessed so the rename/normalize pipeline will re-evaluate it on
                // the next run, mirroring the per-file DELETE /api/files/{path}/metadata behavior.
                try
                {
                    await _fileStore.ClearProcessedStatusAsync(new[] { removed.FilePath }, token);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _logger.LogWarning(
                        ex,
                        "RemoveMetadataFromFilesAsync: failed to clear processed status for {FilePath}",
                        LoggingHelper.SanitizePathForLog(filePath));
                }

                return true;
            },
            "Remove metadata failed",
            cancellationToken);
    }

    public Task<Guid> DeleteFilesAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default)
    {
        // Paths usually ignore case on Windows while the file store matches them exactly, so
        // there the tracked spellings of a deleted file are looked up, from one listing per job.
        var trackedPaths = new Lazy<Task<ILookup<string, string>>>(async () =>
            (await _fileStore.GetAllFilesAsync(CancellationToken.None))
                .ToLookup(f => f.FilePath, f => f.FilePath, StringComparer.OrdinalIgnoreCase));

        return QueueBatchJobAsync(
            filePaths,
            "DeleteFilesAsync",
            "deleting",
            async (filePath, token) =>
            {
                try
                {
                    if (!TryResolveAllowedEntry(filePath, out var entryPath))
                    {
                        _logger.LogWarning(
                            "DeleteFilesAsync: file is outside the allowed directories: {FilePath}",
                            LoggingHelper.SanitizePathForLog(filePath));
                        return false;
                    }

                    // The library path of the entry that is deleted, which differs from the
                    // requested path when that goes through a linked directory.
                    var entryDirectory = Path.GetDirectoryName(entryPath)!;
                    var canonicalPath = Path.Join(GetAllowedLogicalPath(entryDirectory), Path.GetFileName(entryPath));
                    var requestedPaths = new[] { filePath, canonicalPath };
                    var storePaths = requestedPaths.Distinct(StringComparer.Ordinal).ToArray();
                    if (OperatingSystem.IsWindows())
                    {
                        // A differently cased tracked path names this entry only where its
                        // directories ignore case, which a Windows directory can be set not to,
                        // so it must open as the same entry. Checked before the entry is deleted;
                        // when it is already missing, so must the tracked path be.
                        var tracked = await trackedPaths.Value;
                        var entryIdentity = NativeFileSystem.TryReadWindowsEntryPath(entryPath);
                        storePaths = storePaths
                            .Concat(requestedPaths
                                .SelectMany(path => tracked[path])
                                .Where(path => string.Equals(
                                    NativeFileSystem.TryReadWindowsEntryPath(path), entryIdentity, StringComparison.Ordinal)))
                            .Distinct(StringComparer.Ordinal)
                            .ToArray();
                    }

                    // Open the checked directory now, so the entry is deleted from it even if
                    // the directory is replaced (e.g. by a link out of the library) meanwhile.
                    using var directoryHandle = Directory.Exists(entryDirectory) ? OpenDeleteDirectory(entryPath) : null;
                    var entryInDirectory = GetEntryPathInDirectory(directoryHandle, entryPath);

                    // Only files are deleted: a directory, or a link to one, would take every
                    // comic beneath it with it. Directory.Exists follows links, while Path.Exists
                    // is also true for a link whose target is missing.
                    if (Directory.Exists(entryInDirectory))
                    {
                        _logger.LogWarning(
                            "DeleteFilesAsync: not deleting a directory: {FilePath}",
                            LoggingHelper.SanitizePathForLog(filePath));
                        await LogHistoryAsync(canonicalPath, "Delete", false, "Not a file", token);
                        return false;
                    }

                    if (Path.Exists(entryInDirectory))
                    {
                        // Remove from file store first (unlikely to fail), then
                        // delete the physical file. This order prevents orphaned
                        // file-store entries if the disk delete fails.
                        foreach (var storePath in storePaths)
                            await _fileStore.RemoveFileAsync(storePath, token);
                        DeleteFileInDirectory(directoryHandle, entryPath);
                        await LogHistoryAsync(canonicalPath, "Delete", true, null, token);
                        return true;
                    }

                    // File already missing on disk — still scrub the store entry
                    // so the library list reflects reality.
                    try
                    {
                        foreach (var storePath in storePaths)
                            await _fileStore.RemoveFileAsync(storePath, token);
                    }
                    catch (Exception removeEx) when (removeEx is not OutOfMemoryException)
                    {
                        _logger.LogDebug(removeEx,
                            "DeleteFilesAsync: file-store cleanup failed for missing file {FilePath}",
                            LoggingHelper.SanitizePathForLog(filePath));
                    }
                    await LogHistoryAsync(canonicalPath, "Delete", false, "File not found on disk", token);
                    return false;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _logger.LogWarning(ex,
                        "DeleteFilesAsync: error deleting file {FilePath}",
                        LoggingHelper.SanitizePathForLog(filePath));
                    await LogHistoryAsync(filePath, "Delete", false, ex.Message, token);
                    return false;
                }
            },
            "Delete failed",
            cancellationToken);
    }

    public Task<Guid> NormalizeAndRenameFilesAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default)
    {
        // For each file, normalize first (so ComicInfo.xml has the series name derived
        // from the current folder) and then rename so the filename uses that fresh
        // series name. Both steps force reprocessing because files moved by a folder
        // combine may already be marked normalized/renamed in the database under their
        // previous (stale) series.
        return QueueBatchJobAsync(
            filePaths,
            "NormalizeAndRenameFilesAsync",
            "normalizing and renaming",
            async (filePath, token) =>
            {
                // Normalize can convert a CBR/RAR to a CBZ; the rename follows the file there.
                var normalized = await NormalizeFileWithResultAsync(filePath, true, token);
                if (!normalized.Succeeded && !normalized.ArchiveNotWritable)
                {
                    return false;
                }

                // A CBR/RAR/ZIP that is not rewritten while converting to CBZ is off is still
                // renamed (a rename never rewrites the archive), but stays not normalized.
                var renamed = await RenameFileCoreAsync(normalized.FilePath, true, token);
                return renamed && normalized.Succeeded;
            },
            "Normalize and rename failed",
            cancellationToken);
    }

    /// <summary>
    /// Starts a batch job in the job registry whose item operation is one of this service's
    /// file operations. Each runs under the file's lock and a shared processing slot (see
    /// <see cref="RunExclusiveAsync"/>), so a job never races the watcher, the API or another
    /// job on the same archive, and concurrent jobs stay within MaxWorkers.
    /// </summary>
    private Task<Guid> QueueBatchJobAsync(
        IEnumerable<string> filePaths,
        string operationName,
        string actionDescription,
        Func<string, CancellationToken, Task<BatchItemResult>> fileOperation,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        var definition = new BatchJobDefinition(
            operationName,
            actionDescription,
            (filePath, token) => RunExclusiveAsync(filePath, itemToken => fileOperation(filePath, itemToken), token),
            failureMessage);

        return Task.FromResult(_jobRegistry.StartJob(definition, filePaths, cancellationToken));
    }
}
