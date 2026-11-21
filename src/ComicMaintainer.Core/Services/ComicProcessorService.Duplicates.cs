using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Re-checking and swapping duplicates. A file is flagged as a duplicate when the name processing
/// gives it already belongs to another file, and nothing else ever looks at it again: the
/// library-wide jobs leave duplicates out. So a flag outlives its cause, for example an Annual
/// that took a regular issue's name before Annuals were named as such. These work out the name
/// processing would give a file now, with the same steps processing takes, and act on that.
/// </summary>
public partial class ComicProcessorService
{
    private const string DuplicateRecheckAction = "Duplicate Re-check";

    public Task<Guid> RecheckDuplicatesAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default)
    {
        return QueueBatchJobAsync(
            filePaths,
            DuplicateRecheck.OperationName,
            "re-checking duplicate",
            RecheckDuplicateCoreAsync,
            "Re-check failed",
            cancellationToken);
    }

    public Task<DuplicateClash> FindDuplicateClashAsync(string filePath, CancellationToken cancellationToken = default) =>
        FindDuplicateClashCoreAsync(filePath, cancellationToken);

    public async Task<DuplicateSwapResult> SwapDuplicateAsync(string filePath, CancellationToken cancellationToken = default)
    {
        // A first look, without a lock, to learn which other file to lock as well; the swap
        // looks again once it holds both.
        var clash = await FindDuplicateClashCoreAsync(filePath, cancellationToken);
        if (clash is not { Status: DuplicateClashStatus.Clashes, ClashingPath: { } otherPath })
        {
            return NotClashing(clash);
        }

        return await RunExclusiveOnBothAsync(
            filePath,
            otherPath,
            token => SwapDuplicateCoreAsync(filePath, otherPath, token),
            cancellationToken);
    }

    /// <summary>
    /// One file of a re-check job. A file whose processed name no longer clashes loses its flag
    /// and is processed as any other file is, so it gets its proper name and tags. A file that
    /// still clashes keeps its flag and is left untouched; the job records which file it clashes with.
    /// </summary>
    private async Task<BatchItemResult> RecheckDuplicateCoreAsync(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            return await RecheckDuplicateUnguardedAsync(filePath, cancellationToken);
        }
        // A cancelled job didn't fail this file: leave it and its stored state as they are.
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // One file's failure must not end the job for the others.
            _logger.LogError(ex, "Duplicate re-check failed for {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return new BatchItemResult(false, "Re-check failed");
        }
    }

    private async Task<BatchItemResult> RecheckDuplicateUnguardedAsync(string filePath, CancellationToken cancellationToken)
    {
        if (await _fileStore.GetFileAsync(filePath, cancellationToken) is { IsDuplicate: false })
        {
            // Cleared since the job was started, by processing or another re-check.
            return true;
        }

        var clash = await FindDuplicateClashCoreAsync(filePath, cancellationToken);
        switch (clash.Status)
        {
            case DuplicateClashStatus.Clashes:
                await LogHistoryAsync(filePath, DuplicateRecheckAction, true,
                    $"Still a duplicate of {Path.GetFileName(clash.ClashingPath)}", cancellationToken);
                return new BatchItemResult(false, DuplicateRecheck.StillDuplicateError(clash.ClashingPath!));

            case DuplicateClashStatus.Unknown:
                await LogHistoryAsync(filePath, DuplicateRecheckAction, false, clash.Reason, cancellationToken);
                return new BatchItemResult(false, $"Could not work out the processed name: {clash.Reason}");
        }

        _logger.LogInformation(
            "Duplicate re-check: {FilePath} no longer clashes with another file; processing it",
            LoggingHelper.SanitizePathForLog(filePath));
        await _fileStore.MarkFileDuplicateAsync(filePath, false, cancellationToken);
        await LogHistoryAsync(filePath, DuplicateRecheckAction, true, "No longer a duplicate", cancellationToken);

        var processed = await ProcessFileCoreAsync(filePath, forceReprocess: true, cancellationToken);
        if (processed.Success)
        {
            return true;
        }

        // Another file took the name in the meantime, and processing flagged this one again.
        if (processed.Error?.Contains(DuplicateTargetMessage, StringComparison.Ordinal) == true
            && await FindDuplicateClashCoreAsync(processed.FilePath, cancellationToken) is { Status: DuplicateClashStatus.Clashes, ClashingPath: { } other })
        {
            return new BatchItemResult(false, DuplicateRecheck.StillDuplicateError(other));
        }

        return new BatchItemResult(false, processed.Error);
    }

    /// <summary>
    /// Works out the name processing would give <paramref name="filePath"/> now, without
    /// changing anything, by the steps <see cref="ProcessFileCoreAsync"/> takes: read the
    /// metadata, normalize it unless it already is, recover a chapter marked in the file name,
    /// and render the file name. Then looks for another file that already has that name.
    /// </summary>
    /// <remarks>
    /// The extension is the file's own. When normalizing converts a CBR, RAR or ZIP to CBZ the
    /// processed name ends in ".cbz" instead; processing itself still refuses to take a name
    /// another file has, so a wrong guess here never costs a file.
    /// </remarks>
    private async Task<DuplicateClash> FindDuplicateClashCoreAsync(string filePath, CancellationToken cancellationToken)
    {
        if (!TryResolveAllowedPath(filePath, out var resolvedPath))
        {
            return Unknown("File is outside the allowed directories");
        }

        if (!File.Exists(resolvedPath))
        {
            return Unknown("File not found");
        }

        if (!IsComicArchive(resolvedPath))
        {
            return Unknown("File is not a comic archive");
        }

        string resolvedTarget;
        if (!_settings.WatcherEnableRename)
        {
            // Processing keeps the file's name, which it already has.
            resolvedTarget = resolvedPath;
        }
        else
        {
            var read = await ReadMetadataAtResolvedPathAsync(resolvedPath, filePath, cancellationToken);
            var metadata = read.Metadata;
            if (metadata is null)
            {
                return Unknown(read.FailureReason ?? "The archive could not be read");
            }

            var originalSeries = metadata.Series;
            if (_settings.WatcherEnableNormalize && !await IsMetadataNormalizedAsync(metadata, filePath, mutateCache: false, cancellationToken))
            {
                metadata = await NormalizeMetadataAsync(metadata, filePath, mutateCache: false, cancellationToken);
            }

            if (string.IsNullOrEmpty(metadata.Series))
            {
                return Unknown(NoSeriesMessage(filePath, "No metadata or series information"));
            }

            await RecoverMarkedChapterAsync(filePath, metadata, cancellationToken);
            if (GenerateFileName(metadata, resolvedPath, originalSeries, await AnnualKeepsNoNumberAsync(metadata, filePath, cancellationToken)) is not { } generated)
            {
                return Unknown(FilenameGenerationFailedMessage);
            }

            resolvedTarget = generated;
        }

        var targetPath = SiblingOf(filePath, resolvedTarget);
        if (string.Equals(resolvedTarget, resolvedPath, StringComparison.Ordinal))
        {
            return new DuplicateClash(DuplicateClashStatus.Free, targetPath);
        }

        return FindTargetFile(resolvedTarget, resolvedPath) is { } clashing
            ? new DuplicateClash(DuplicateClashStatus.Clashes, targetPath, SiblingOf(filePath, ListedSpellingOf(clashing)))
            : new DuplicateClash(DuplicateClashStatus.Free, targetPath);

        static DuplicateClash Unknown(string reason) => new(DuplicateClashStatus.Unknown, null, Reason: reason);
    }

    /// <summary>
    /// The library path of <paramref name="resolvedSibling"/>, which is in the same directory as
    /// <paramref name="filePath"/>: the processed name never moves a file to another directory, so
    /// it is named through the same (possibly linked) folder as the file itself.
    /// </summary>
    private static string SiblingOf(string filePath, string resolvedSibling) =>
        Path.Join(Path.GetDirectoryName(filePath), Path.GetFileName(resolvedSibling));

    /// <summary>
    /// Gives the duplicate <paramref name="filePath"/> the name <paramref name="otherPath"/> has, and
    /// flags that other file as the duplicate instead. The other file is renamed aside first, to
    /// its own name with " (duplicate)" added, and then the duplicate is processed as any file is,
    /// which gives it the freed name and its tags. Nothing is deleted. When the duplicate does
    /// not end up with the name, the other file is moved back and both keep their flags.
    /// </summary>
    private async Task<DuplicateSwapResult> SwapDuplicateCoreAsync(string filePath, string otherPath, CancellationToken cancellationToken)
    {
        if (await _fileStore.GetFileAsync(filePath, cancellationToken) is not { IsDuplicate: true })
        {
            return new DuplicateSwapResult(false, Error: "The file is not flagged as a duplicate.", Conflict: true);
        }

        var clash = await FindDuplicateClashCoreAsync(filePath, cancellationToken);
        if (clash.Status != DuplicateClashStatus.Clashes)
        {
            return NotClashing(clash);
        }

        if (!string.Equals(clash.ClashingPath, otherPath, StringComparison.Ordinal))
        {
            return new DuplicateSwapResult(false, Error: "Another file has the name now. Compare the files again.", Conflict: true);
        }

        if (!TryResolveAllowedPath(otherPath, out var otherResolved) || new FileInfo(otherPath).LinkTarget is not null)
        {
            return new DuplicateSwapResult(false, Error: "The other file cannot be renamed.");
        }

        var otherWas = await _fileStore.GetFileAsync(otherPath, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        string asidePath;
        try
        {
            asidePath = MoveWithinDirectory(otherResolved, AsideNameFor(otherResolved));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Duplicate swap: could not rename {OtherPath} aside", LoggingHelper.SanitizePathForLog(otherPath));
            return new DuplicateSwapResult(false, Error: $"The other file could not be renamed: {ex.Message}");
        }

        // The other file has moved: from here the swap runs to its end, or back to its start,
        // whatever the request does meanwhile, so no file is left half swapped.
        var bookkeeping = CancellationToken.None;
        await MoveFileRecordAsync(otherPath, asidePath);
        FileProcessingResult processed;
        try
        {
            await MarkDuplicateAsync(asidePath, bookkeeping);
            await _fileStore.MarkFileDuplicateAsync(filePath, false, bookkeeping);
            processed = await ProcessFileCoreAsync(filePath, forceReprocess: true, bookkeeping);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Processing reports its own failures, but the bookkeeping around it can throw too. A throw
            // after the other file moved is a failed swap like a returned failure: put it back.
            _logger.LogError(ex, "Duplicate swap: {FilePath} threw after the other file was renamed aside", LoggingHelper.SanitizePathForLog(filePath));
            return new DuplicateSwapResult(false, Error: await UndoSwapAsync(
                filePath, otherPath, otherResolved, asidePath, otherWas, "The file could not be processed"));
        }

        if (string.Equals(processed.FilePath, filePath, StringComparison.Ordinal) && !processed.Success)
        {
            var reason = processed.Error ?? "The file could not be processed";
            return new DuplicateSwapResult(false, Error: await UndoSwapAsync(filePath, otherPath, otherResolved, asidePath, otherWas, reason));
        }

        await LogHistoryAsync(asidePath, "Duplicate Swap", true,
            $"Gave its name to {Path.GetFileName(filePath)} and is now the duplicate", bookkeeping);
        _notifications.LibraryChanged();
        return new DuplicateSwapResult(true, processed.FilePath, asidePath);
    }

    /// <summary>
    /// Puts the other file of a swap that did not happen back under its name with the flags it
    /// had, and flags the file that could not take the name as the duplicate again. Returns the
    /// error to report.
    /// </summary>
    private async Task<string> UndoSwapAsync(
        string filePath,
        string otherPath,
        string otherResolved,
        string asidePath,
        ComicFile? otherWas,
        string reason)
    {
        var bookkeeping = CancellationToken.None;
        await _fileStore.MarkFileDuplicateAsync(filePath, true, bookkeeping);

        try
        {
            if (!TryResolveAllowedPath(asidePath, out var asideResolved))
            {
                throw new IOException("The file no longer resolves inside the library.");
            }

            MoveWithinDirectory(asideResolved, otherResolved);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex,
                "Duplicate swap: {FilePath} could not take its name, and {OtherPath} could not be given its name back; it stays at {AsidePath}, flagged as a duplicate",
                LoggingHelper.SanitizePathForLog(filePath),
                LoggingHelper.SanitizePathForLog(otherPath),
                LoggingHelper.SanitizePathForLog(asidePath));
            return $"{reason}. The other file could not be given its name back and is now {Path.GetFileName(asidePath)}, flagged as a duplicate.";
        }

        await MoveFileRecordAsync(asidePath, otherPath);
        await _fileStore.MarkFileDuplicateAsync(otherPath, otherWas?.IsDuplicate ?? false, bookkeeping);
        await _fileStore.MarkFileRenamedAsync(otherPath, otherWas?.IsRenamed ?? false, bookkeeping);
        await LogHistoryAsync(filePath, "Duplicate Swap", false, reason, bookkeeping);
        return reason;
    }

    /// <summary>
    /// Renames the file at the physical <paramref name="sourcePath"/> to <paramref name="targetPath"/>
    /// in the same directory, never replacing another file, and returns its new library path.
    /// </summary>
    private string MoveWithinDirectory(string sourcePath, string targetPath)
    {
        using var directoryHandle = OpenRenameDirectory(sourcePath);
        if (!TryResolveAllowedPath(sourcePath, out var currentPath) || currentPath != sourcePath)
        {
            // A folder on the validated path was replaced by a link meanwhile.
            throw new IOException("The file path changed while it was being renamed.");
        }

        return GetAllowedLogicalPath(RenameOnDisk(directoryHandle, sourcePath, targetPath));
    }

    /// <summary>
    /// Moves a file's record (flags, read state, metadata edits) after the file. A failure is
    /// left to the file watcher and the next scan, as after any rename.
    /// </summary>
    private async Task MoveFileRecordAsync(string oldPath, string newPath)
    {
        try
        {
            await _fileStore.UpdateFilePathAsync(oldPath, newPath, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex,
                "Could not move the library record of {OldPath} to {NewPath}; it is left to the file watcher and the next scan",
                LoggingHelper.SanitizePathForLog(oldPath),
                LoggingHelper.SanitizePathForLog(newPath));
        }
    }

    /// <summary>
    /// The first free name for a file renamed aside: its own name with " (duplicate)", then
    /// " (duplicate 2)" and so on, so it stays next to the file that took its name.
    /// </summary>
    private string AsideNameFor(string resolvedPath)
    {
        var directory = Path.GetDirectoryName(resolvedPath)!;
        var stem = Path.GetFileNameWithoutExtension(resolvedPath);
        var extension = Path.GetExtension(resolvedPath);
        for (var attempt = 1; attempt <= 1000; attempt++)
        {
            var suffix = attempt == 1 ? " (duplicate)" : $" (duplicate {attempt})";
            var candidate = Path.Join(directory, ComicFileProcessor.SanitizeFileName(stem + suffix) + extension);
            if (!TargetFileExists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("No free name was found for the file.");
    }

    private static DuplicateSwapResult NotClashing(DuplicateClash clash) => clash.Status == DuplicateClashStatus.Free
        ? new DuplicateSwapResult(false, Error: "No other file has this file's name any more. Re-check it to clear its flag.", Conflict: true)
        : new DuplicateSwapResult(false, Error: $"Could not work out the processed name: {clash.Reason}");

    /// <summary>
    /// Like <see cref="RunExclusiveAsync{T}"/>, for an operation on two files: it holds both
    /// files' locks, taken in one order so two such operations cannot each wait for the other,
    /// and one processing slot.
    /// </summary>
    private async Task<T> RunExclusiveOnBothAsync<T>(
        string firstPath,
        string secondPath,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        // Paths that differ only in case share a lock, and the lock is not reentrant.
        var paths = new[] { firstPath, secondPath }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return await RunHoldingLocksAsync(paths, 0, operation, cancellationToken);
    }

    private async Task<T> RunHoldingLocksAsync<T>(
        IReadOnlyList<string> paths,
        int index,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        if (index < paths.Count)
        {
            using var lease = await _fileLock.AcquireAsync(paths[index], cancellationToken);
            return await RunHoldingLocksAsync(paths, index + 1, operation, cancellationToken);
        }

        await _processingSemaphore.WaitAsync(cancellationToken);
        try
        {
            return await operation(cancellationToken);
        }
        finally
        {
            _processingSemaphore.Release();
        }
    }
}
