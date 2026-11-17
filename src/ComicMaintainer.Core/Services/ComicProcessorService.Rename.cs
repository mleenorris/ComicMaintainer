using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
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
    private async Task<BatchItemResult> RenameFileCoreAsync(string filePath, bool forceReprocess, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("RenameFileAsync: Starting rename for file: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));

            // Skip files that are already marked as renamed in the database.
            if (!forceReprocess && await _fileStore.IsFileRenamedAsync(filePath, cancellationToken))
            {
                _logger.LogInformation("RenameFileAsync: File already renamed (database), skipping: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                return true;
            }

            // As in ProcessFileCoreAsync, the file is read and moved through its validated
            // physical path, so a linked folder cannot redirect the rename outside the library,
            // while the caller's path stays the key for the file store and history.
            if (!TryResolveAllowedPath(filePath, out var resolvedPath))
            {
                _logger.LogWarning("RenameFileAsync: File is outside the allowed directories: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                await LogHistoryAsync(filePath, "Rename", false, "File is outside the allowed directories", cancellationToken);
                return false;
            }

            _logger.LogDebug("RenameFileAsync: Checking if file exists and is a comic archive: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));

            if (!File.Exists(resolvedPath) || !IsComicArchive(resolvedPath))
            {
                _logger.LogWarning("RenameFileAsync: File not found or not a comic archive: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                await LogHistoryAsync(filePath, "Rename", false, "File not found or not a comic archive", cancellationToken);
                return false;
            }

            if (new FileInfo(filePath).LinkTarget is not null)
            {
                // Renaming through the link would rename the file it points to.
                _logger.LogWarning("RenameFileAsync: File symlinks are not renamed: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                await _fileStore.MarkFileRenamedAsync(filePath, false, cancellationToken);
                await LogHistoryAsync(filePath, "Rename", false, "File symlinks are not processed", cancellationToken);
                return false;
            }

            _logger.LogDebug("RenameFileAsync: Extracting metadata from archive: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));

            // Extract metadata from the archive
            var metadataRead = await ReadMetadataAtResolvedPathAsync(resolvedPath, filePath, cancellationToken);
            var metadata = metadataRead.Metadata;

            if (metadata != null)
            {
                _logger.LogDebug("RenameFileAsync: Metadata extracted - Series: {Series}, Issue: {Issue}",
                    LoggingHelper.SanitizeForLog(metadata.Series),
                    LoggingHelper.SanitizeForLog(metadata.Issue));
            }
            else
            {
                _logger.LogDebug("RenameFileAsync: No metadata extracted from file: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            }

            // Rename file based on template if metadata is available
            if (metadata != null && !string.IsNullOrEmpty(metadata.Series))
            {
                // If the metadata has only the series tag set (no Issue/Number),
                // but the original filename contains an explicit "Ch/Chapter <n>"
                // token, recover that chapter number into the metadata so the
                // rename preserves it not only in the filename but also in
                // ComicInfo.xml.
                var preservedPath = await EnsureChapterMetadataPreservedAsync(filePath, metadata, cancellationToken);
                FollowMovedFile(preservedPath, ref filePath, ref resolvedPath);

                // Resolve the normalized series name before generating the new
                // filename so a standalone rename produces the exact same target
                // name as the full process pipeline (ProcessFileCoreAsync), where
                // the normalize phase resolves the series first and the rename
                // phase then renders the filename from that resolved name. Without
                // this, renaming via the rename endpoint / file-naming audit would
                // use the raw <Series> from ComicInfo.xml and could yield a
                // different filename than the watcher/process path for the same
                // file. The resolved name is applied in-memory for filename
                // generation only; rewriting ComicInfo.xml remains the normalize
                // operation's responsibility.
                var originalSeries = metadata.Series;
                var resolvedSeries = await ResolveNormalizedSeriesAsync(metadata, filePath, mutateCache: true, cancellationToken);
                if (!string.IsNullOrEmpty(resolvedSeries) && metadata.Series != resolvedSeries)
                {
                    _logger.LogDebug(
                        "RenameFileAsync: Using resolved series name for filename: {SeriesName}",
                        LoggingHelper.SanitizeForLog(resolvedSeries));
                    metadata.Series = resolvedSeries;
                }

                _logger.LogDebug("RenameFileAsync: Generating new filename for file: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                var (decision, target) = DecideRename(metadata, resolvedPath, originalSeries);
                if (decision == RenameDecision.NameFailed)
                {
                    await _fileStore.MarkFileRenamedAsync(filePath, false, cancellationToken);
                    await LogHistoryAsync(filePath, "Rename", false, FilenameGenerationFailedMessage, cancellationToken);
                    return false;
                }

                var newResolvedPath = target!;
                _logger.LogDebug("RenameFileAsync: Generated filename: {NewFilePath} (Original: {OriginalFilePath})",
                    LoggingHelper.SanitizePathForLog(newResolvedPath), LoggingHelper.SanitizePathForLog(filePath));

                if (decision == RenameDecision.AlreadyNamed)
                {
                    _logger.LogInformation("RenameFileAsync: File already has correct name: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                    await _fileStore.MarkFileRenamedAsync(filePath, true, cancellationToken);
                    await ClearDuplicateFlagAsync(filePath, cancellationToken);
                    var filename = Path.GetFileName(filePath);
                    await LogHistoryWithChangesAsync(filePath, "Rename", true, "File already correctly named",
                        filename, filename, metadata, metadata, cancellationToken);
                    return true;
                }

                if (decision == RenameDecision.TargetTaken)
                {
                    _logger.LogInformation("RenameFileAsync: Duplicate detected - target file already exists: {NewPath}", LoggingHelper.SanitizePathForLog(newResolvedPath));
                    await MarkDuplicateAsync(filePath, cancellationToken);
                    return false;
                }

                string newFilePath;
                bool wasNormalized;
                try
                {
                    _logger.LogInformation(
                        "Renaming file from {OldPath} to {NewPath}",
                        LoggingHelper.SanitizePathForLog(filePath),
                        LoggingHelper.SanitizePathForLog(newResolvedPath));

                    // Capture the old path's normalized state *before* the move so it can be
                    // re-asserted on the new path afterwards. UpdateFilePathAsync normally
                    // carries this state forward, but the FileSystemWatcher can observe the
                    // move as a Delete(old)+Create(new) pair and remove the authoritative
                    // old-path row before the processor migrates it. When that happens the
                    // merge in UpdateFilePathAsync has nothing to merge from and the new row
                    // keeps the watcher stub's default IsNormalized=false, so a
                    // normalize-then-rename sequence would leave the file "unprocessed" until
                    // it is normalized again. Re-stamping the captured state closes that gap.
                    wasNormalized = await _fileStore.IsFileNormalizedAsync(filePath, cancellationToken);

                    // A cancelled job leaves the file where it is rather than moving it. This is
                    // the last point the job's cancellation is observed for this file: once it
                    // has moved, its bookkeeping must finish (see RecordRenameAsync).
                    cancellationToken.ThrowIfCancellationRequested();

                    using var renameDirectoryHandle = OpenRenameDirectory(resolvedPath);
                    if (!TryResolveAllowedPath(resolvedPath, out var currentPath) || currentPath != resolvedPath)
                    {
                        // A folder on the validated path was replaced by a link meanwhile.
                        _logger.LogWarning("RenameFileAsync: File path changed during processing, not renaming: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                        await _fileStore.MarkFileRenamedAsync(filePath, false, cancellationToken);
                        await LogHistoryAsync(filePath, "Rename", false, "File path changed during processing", cancellationToken);
                        return false;
                    }

                    // The move never replaces an existing file: it throws instead, which is
                    // handled below, so a target created since the check above is not lost.
                    var movedPath = RenameOnDisk(renameDirectoryHandle, resolvedPath, newResolvedPath);
                    newFilePath = GetAllowedLogicalPath(movedPath);
                }
                catch (IOException ex) when (TargetFileExists(newResolvedPath, resolvedPath))
                {
                    // The target appeared between the check and the move - treat as duplicate
                    _logger.LogInformation(ex, "RenameFileAsync: Duplicate detected - target file already exists: {NewPath}", LoggingHelper.SanitizePathForLog(newResolvedPath));
                    await MarkDuplicateAsync(filePath, cancellationToken);
                    return false;
                }

                // The file has moved: record it without observing the job's cancellation.
                await RecordRenameAsync(filePath, newFilePath, wasNormalized, metadata);
                _logger.LogInformation("File renamed successfully: {NewPath}", LoggingHelper.SanitizePathForLog(newFilePath));
                return true;
            }
            else
            {
                _logger.LogWarning("RenameFileAsync: No metadata or series information found for: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                await _fileStore.MarkFileRenamedAsync(filePath, false, cancellationToken);
                await LogHistoryAsync(filePath, "Rename", false, metadataRead.FailureReason ?? NoSeriesMessage(filePath, "No metadata or series information found"), cancellationToken);
                return new BatchItemResult(false, metadataRead.FailureReason);
            }
        }
        // A cancelled job didn't fail this file: leave it and its stored state as they are.
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "RenameFileAsync: Error renaming file: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            await _fileStore.MarkFileRenamedAsync(filePath, false, cancellationToken);
            await LogHistoryAsync(filePath, "Rename", false, ex.Message, cancellationToken);
            return false;
        }
    }

    /// <summary>
    /// Records that the file at <paramref name="oldPath"/> was just renamed on disk to
    /// <paramref name="newPath"/>: moves its file-store row (with every user's read state),
    /// marks it renamed, re-asserts its normalized state and logs the rename.
    /// <para>
    /// This runs without the job's cancellation token. The file has already moved, so stopping
    /// half-way would leave the row pointing at a path that no longer exists and the moved file
    /// untracked, losing its read state and processing flags, whatever the cancellation asked for.
    /// </para>
    /// </summary>
    private async Task RecordRenameAsync(string oldPath, string newPath, bool normalized, ComicMetadata metadata)
    {
        var bookkeeping = CancellationToken.None;

        // Move the database record to the new path, preserving all processing state, so the
        // FileSystemWatcher's rename event sees the entry as already tracked and does not
        // re-queue it for processing.
        try
        {
            await _fileStore.UpdateFilePathAsync(oldPath, newPath, bookkeeping);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The file has moved and the rename succeeded, so this is not reported as a failed
            // rename. The file watcher's rename event and the next library scan follow the file.
            _logger.LogWarning(ex,
                "Could not move the library record of {OldPath} to {NewPath}; it is left to the file watcher and the next scan",
                LoggingHelper.SanitizePathForLog(oldPath),
                LoggingHelper.SanitizePathForLog(newPath));
        }

        await _fileStore.MarkFileRenamedAsync(newPath, true, bookkeeping);

        // Re-assert the normalized state on the new path so the move can't drop it if the
        // watcher removed the old row before UpdateFilePathAsync migrated it (Delete+Create
        // race), which would otherwise leave the file showing as unprocessed.
        if (normalized)
        {
            await _fileStore.MarkFileNormalizedAsync(newPath, true, bookkeeping);
        }

        // The file now holds its target name, so it is no longer a duplicate of another file.
        await ClearDuplicateFlagAsync(newPath, bookkeeping);

        // A move into another series folder names the folders too, or the entry would read
        // as a rename to the same name.
        var oldFolder = Path.GetDirectoryName(oldPath);
        var movedFolder = !string.Equals(oldFolder, Path.GetDirectoryName(newPath), StringComparison.Ordinal);
        await LogHistoryWithChangesAsync(newPath, "Rename", true, null,
            movedFolder ? WithFolderName(oldPath) : Path.GetFileName(oldPath),
            movedFolder ? WithFolderName(newPath) : Path.GetFileName(newPath),
            metadata, metadata, bookkeeping);

        if (movedFolder)
        {
            RemoveFolderIfEmptied(oldFolder);
        }

        _notifications.LibraryChanged();
    }

    /// <summary>"Folder/File.cbz": the name of <paramref name="path"/> with that of its folder.</summary>
    private static string WithFolderName(string path) =>
        $"{Path.GetFileName(Path.GetDirectoryName(path))}/{Path.GetFileName(path)}";

    /// <summary>
    /// Removes <paramref name="directory"/>, which a file just left for its series folder, when
    /// nothing worth keeping is left in it. It uses the folder combine's clean-up
    /// (<see cref="FolderCombineSourceCleaner"/>): only a folder holding nothing but disposable
    /// entries (its cover image, its ComicInfo.xml, operating-system metadata) is removed,
    /// never a link or one of the library's configured folders, and anything that appears
    /// meanwhile keeps it. A folder that still holds a comic is left without a full check:
    /// the other files of a series moved one by one leave it in turn.
    /// </summary>
    private void RemoveFolderIfEmptied(string? directory)
    {
        // Only a folder inside the watched folder is tidied, never the watched folder itself.
        var settings = _settings;
        if (string.IsNullOrEmpty(directory)
            || string.IsNullOrWhiteSpace(settings.WatchedDirectory)
            || !LibraryPathValidator.IsLexicallyWithin(directory, settings.WatchedDirectory)
            || string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(settings.WatchedDirectory)),
                LibraryPathValidator.PathComparison))
        {
            return;
        }

        try
        {
            if (!Directory.Exists(directory) || Directory.EnumerateFiles(directory).Any(IsComicArchive))
            {
                return;
            }

            var protectedDirectories = new[]
                {
                    settings.WatchedDirectory,
                    settings.DuplicateDirectory,
                    settings.SuwayomiDownloadsDirectory,
                    settings.SuwayomiImportDirectory
                }
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path!)
                .ToList();
            var cleanup = _libraryMutationLock.Execute(() => FolderCombineSourceCleaner.Remove(directory, protectedDirectories));
            if (cleanup.Removable)
            {
                _logger.LogInformation("Removed the folder {Directory}: its files moved to their series folders",
                    LoggingHelper.SanitizePathForLog(directory));
            }
            else
            {
                _logger.LogDebug("Kept the folder {Directory}: {Reason}",
                    LoggingHelper.SanitizePathForLog(directory),
                    LoggingHelper.SanitizeForLog(FolderCombineSourceCleaner.DescribeKeptFolder(cleanup)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file has moved; a folder that cannot be checked is simply kept.
            _logger.LogDebug(ex, "Could not check the folder {Directory} after moving a file out of it",
                LoggingHelper.SanitizePathForLog(directory));
        }
    }

    /// <summary>
    /// Flags a file whose target name belongs to another file as a duplicate.
    /// </summary>
    private async Task MarkDuplicateAsync(string filePath, CancellationToken cancellationToken)
    {
        await _fileStore.MarkFileRenamedAsync(filePath, false, cancellationToken);
        await _fileStore.MarkFileDuplicateAsync(filePath, true, cancellationToken);
        await LogHistoryAsync(filePath, "Duplicate Detection", true, "Target file already exists", cancellationToken);
    }

    /// <summary>
    /// Clears the duplicate flag of a file that holds its target name. A file is flagged when
    /// its target name is taken; once it holds that name it is not a duplicate any more, and
    /// keeping the flag would let "Delete All Duplicates" delete it.
    /// </summary>
    private async Task ClearDuplicateFlagAsync(string filePath, CancellationToken cancellationToken)
    {
        var tracked = await _fileStore.GetFileAsync(filePath, cancellationToken);
        if (tracked?.IsDuplicate == true)
        {
            await _fileStore.MarkFileDuplicateAsync(filePath, false, cancellationToken);
        }
    }

    /// <summary>
    /// Renames <paramref name="sourcePath"/> to <paramref name="targetPath"/> in the directory
    /// opened as <paramref name="directoryHandle"/>, never replacing another file, and returns
    /// the physical path of the renamed file.
    /// <para>
    /// On a file system that ignores case (Windows, macOS, most network shares and Docker
    /// Desktop bind mounts) a target that differs from the source only in case names the source
    /// itself, and a move that refuses to replace an existing file refuses to move a file onto
    /// itself. Such a rename goes through a unique temporary name instead: first to it, then
    /// from it to the target.
    /// </para>
    /// </summary>
    private string RenameOnDisk(SafeFileHandle? directoryHandle, string sourcePath, string targetPath)
    {
        if (!string.Equals(Path.GetDirectoryName(sourcePath), Path.GetDirectoryName(targetPath), StringComparison.Ordinal))
        {
            return MoveToSeriesFolder(sourcePath, targetPath);
        }

        if (!IsCaseOnlyRename(sourcePath, targetPath) || !File.Exists(targetPath))
        {
            return MoveFileInRenameDirectory(directoryHandle, sourcePath, targetPath);
        }

        // ".tmp_" names are ignored by the watcher and the library scans, so the intermediate
        // step is never tracked as a file of its own.
        var intermediatePath = Path.Join(
            Path.GetDirectoryName(sourcePath),
            $".tmp_rename_{Guid.NewGuid():N}{Path.GetExtension(sourcePath)}");
        var intermediate = MoveFileInRenameDirectory(directoryHandle, sourcePath, intermediatePath);
        try
        {
            return MoveFileInRenameDirectory(directoryHandle, intermediate, targetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Give the file its original name back rather than leave it under the hidden one.
            try
            {
                MoveFileInRenameDirectory(directoryHandle, intermediate, sourcePath);
            }
            catch (Exception restoreEx) when (restoreEx is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(restoreEx,
                    "Case-only rename of {SourcePath} failed and the file could not be given its name back; it is at {IntermediatePath}",
                    LoggingHelper.SanitizePathForLog(sourcePath),
                    LoggingHelper.SanitizePathForLog(intermediate));
            }

            throw;
        }
    }

    /// <summary>
    /// Moves <paramref name="sourcePath"/> into the series folder <paramref name="targetPath"/>
    /// is in (see <see cref="SeriesFolderFor"/>), creating the folder when it does not exist yet,
    /// and returns the moved file's physical path. Like a rename, the move never replaces another
    /// file: it throws instead. The library mutation lock is held throughout, so a folder combine
    /// never removes the folder between its creation and the move.
    /// </summary>
    private string MoveToSeriesFolder(string sourcePath, string targetPath)
    {
        var targetDirectory = Path.GetDirectoryName(targetPath)
            ?? throw new IOException("Cannot determine the target folder.");
        return _libraryMutationLock.Execute(() =>
        {
            var created = !Directory.Exists(targetDirectory);
            Directory.CreateDirectory(targetDirectory);
            try
            {
                // The folder must be a real folder inside the library: a link put in its place
                // would carry the file somewhere else.
                if (!TryResolveAllowedPath(targetDirectory, out var resolvedDirectory)
                    || !string.Equals(
                        Path.TrimEndingDirectorySeparator(resolvedDirectory),
                        Path.TrimEndingDirectorySeparator(targetDirectory),
                        LibraryPathValidator.PathComparison))
                {
                    throw new IOException("The series folder is not a folder inside the library.");
                }

                File.Move(sourcePath, targetPath, overwrite: false);
                return targetPath;
            }
            catch (Exception) when (created)
            {
                // Don't leave behind an empty folder made only for this file.
                RemoveCreatedFolder(targetDirectory);
                throw;
            }
        });
    }

    private void RemoveCreatedFolder(string directory)
    {
        try
        {
            // Not recursive: a file put in it meanwhile keeps it.
            Directory.Delete(directory, recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not remove the folder {Directory} after a failed move",
                LoggingHelper.SanitizePathForLog(directory));
        }
    }

    /// <summary>
    /// The series folder, directly under the watched folder, that <paramref name="originalPath"/>
    /// belongs in under <see cref="Configuration.AppSettings.FolderFormat"/>, or null to leave the
    /// file in its folder: no folder format is set, the file is outside the watched folder or in
    /// the duplicates folder, or the format renders no name (see
    /// <see cref="ComicFileProcessor.FormatFolderName"/>).
    /// </summary>
    /// <remarks>
    /// An existing folder whose name differs only in case is used as it is, so a file system
    /// that ignores case does not see a move into the folder the file is already in, and one
    /// that keeps case apart does not get a second folder for the series.
    /// </remarks>
    private string? SeriesFolderFor(ComicInfo comicInfo, string originalPath)
    {
        var settings = _settings;
        if (string.IsNullOrWhiteSpace(settings.FolderFormat) || string.IsNullOrWhiteSpace(settings.WatchedDirectory))
        {
            return null;
        }

        // originalPath is a physical path, so the folder is placed under the watched folder's
        // physical path too; GetAllowedLogicalPath maps the moved file back.
        var root = LibraryPathValidator.ResolveRealPath(Path.GetFullPath(settings.WatchedDirectory));
        if (!LibraryPathValidator.IsWithin(originalPath, root)
            || (!string.IsNullOrWhiteSpace(settings.DuplicateDirectory)
                && LibraryPathValidator.IsWithin(originalPath, settings.DuplicateDirectory)))
        {
            return null;
        }

        var folderName = ComicFileProcessor.FormatFolderName(settings.FolderFormat, comicInfo, Path.GetFileName(originalPath));
        return folderName is null ? null : ExistingFolderNamed(root, folderName);
    }

    /// <summary>
    /// The folder <paramref name="folderName"/> in <paramref name="root"/>: one of that exact
    /// name, else one whose name differs only in case, else the path of a new one.
    /// </summary>
    private string ExistingFolderNamed(string root, string folderName)
    {
        try
        {
            string? differentCase = null;
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(directory);
                if (string.Equals(name, folderName, StringComparison.Ordinal))
                {
                    return directory;
                }

                if (differentCase is null && string.Equals(name, folderName, StringComparison.OrdinalIgnoreCase))
                {
                    differentCase = directory;
                }
            }

            if (differentCase is not null)
            {
                return differentCase;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not list the folders of {Directory}", LoggingHelper.SanitizePathForLog(root));
        }

        return Path.Join(root, folderName);
    }

    /// <summary>
    /// True when <paramref name="targetPath"/> only changes the case of <paramref name="sourcePath"/>.
    /// </summary>
    private static bool IsCaseOnlyRename(string sourcePath, string targetPath) =>
        !string.Equals(sourcePath, targetPath, StringComparison.Ordinal)
        && string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="targetPath"/>, which exists, is <paramref name="sourcePath"/>
    /// itself under another case of its name, as on a file system that ignores case. The
    /// directory listing decides it: it shows every entry under its stored name, so a directory
    /// that keeps case apart and holds both files lists both names, while one that ignores case
    /// can only hold the one file. (Distinct names, or another directory, are never the same file.)
    /// </summary>
    internal static bool IsSameFileUnderAnotherCase(string sourcePath, string targetPath, IEnumerable<string> directoryEntryNames)
    {
        if (!string.Equals(Path.GetDirectoryName(sourcePath), Path.GetDirectoryName(targetPath), StringComparison.Ordinal))
        {
            return false;
        }

        var sourceName = Path.GetFileName(sourcePath);
        var targetName = Path.GetFileName(targetPath);
        if (!string.Equals(sourceName, targetName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(sourceName, targetName, StringComparison.Ordinal))
        {
            return true;
        }

        var listsSource = false;
        var listsTarget = false;
        foreach (var name in directoryEntryNames)
        {
            listsSource |= string.Equals(name, sourceName, StringComparison.Ordinal);
            listsTarget |= string.Equals(name, targetName, StringComparison.Ordinal);
        }

        return !(listsSource && listsTarget);
    }

    private const string FilenameGenerationFailedMessage = "Failed to generate new filename";

    /// <summary>What the rename phase does with a file (see <see cref="DecideRename"/>).</summary>
    private enum RenameDecision
    {
        /// <summary>No file name could be generated: the rename fails.</summary>
        NameFailed,

        /// <summary>The file already has its target name.</summary>
        AlreadyNamed,

        /// <summary>Another file has the target name: the file is flagged as a duplicate.</summary>
        TargetTaken,

        /// <summary>The file is renamed to the target.</summary>
        Rename
    }

    /// <summary>
    /// Decides the rename of the file at <paramref name="resolvedPath"/> from
    /// <paramref name="metadata"/>: its target path (see <see cref="GenerateFileName"/>), and
    /// whether it already has that name or another file does (see <see cref="TargetFileExists"/>).
    /// Processing, renaming and the processing preview all decide here, so a preview names the
    /// same target as the real run.
    /// </summary>
    private (RenameDecision Decision, string? Target) DecideRename(ComicMetadata metadata, string resolvedPath, string? originalSeries)
    {
        var target = GenerateFileName(metadata, resolvedPath, originalSeries);
        if (target is null)
        {
            return (RenameDecision.NameFailed, null);
        }

        if (target == resolvedPath)
        {
            return (RenameDecision.AlreadyNamed, target);
        }

        return (TargetFileExists(target, resolvedPath) ? RenameDecision.TargetTaken : RenameDecision.Rename, target);
    }

    /// <summary>
    /// Renders the target path for <paramref name="originalPath"/> from <paramref name="metadata"/>.
    /// Returns null (after logging the error) when the filename cannot be generated; callers must
    /// treat that as a rename failure, never as "already correctly named".
    /// </summary>
    /// <param name="originalSeries">
    /// The series as the file named it before the resolved name replaced it. Without a
    /// <c>&lt;Volume&gt;</c> tag, the volume comes from it ("Green Lantern (1990)"), then the file
    /// name, then a file name left in the title, then the folder name: the sources, in the
    /// order, normalize fills <c>&lt;Volume&gt;</c> from (see VolumeFromNames), so the name and
    /// the tag agree.
    /// </param>
    private string? GenerateFileName(ComicMetadata metadata, string originalPath, string? originalSeries = null)
    {
        try
        {
            var extension = Path.GetExtension(originalPath);

            // Convert ComicMetadata to ComicInfo for use with ComicFileProcessor
            var comicInfo = ComicInfo.FromMetadata(metadata);
            if (ComicVolume.FromTag(comicInfo.Volume) is null
                && VolumeFromNames(originalSeries ?? metadata.Series, metadata.Title, metadata.Issue, originalPath) is { } volume)
            {
                // In-memory only: the copy renders the name, the file's tags are untouched.
                comicInfo.Volume = volume.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            // With a folder format the file goes to its series folder; without one it stays put.
            var directory = SeriesFolderFor(comicInfo, originalPath)
                ?? Path.GetDirectoryName(originalPath)
                ?? _settings.WatchedDirectory;

            // An issue written as "Annual 1" is named by its number: the series names the Annual.
            var issue = AnnualIssue.IssueNumber(metadata.Issue);

            // With no number, a title names the issue in place of it (see FormatFilename). With
            // neither, the template can only give the series' name, which every such issue would
            // share, so the current name is kept: it is the best name there is. A name an earlier
            // rename left ending in a bare "- Chapter" is the exception, and is renamed.
            if (string.IsNullOrWhiteSpace(issue)
                && ComicFileProcessor.TitleInPlaceOfNumber(comicInfo.Title, comicInfo.Series) is null
                && !ComicFileProcessor.EndsInBareIssuePrefix(Path.GetFileNameWithoutExtension(originalPath)))
            {
                return originalPath;
            }

            // Use ComicFileProcessor.FormatFilename to properly handle decimal issue numbers
            var filename = ComicFileProcessor.FormatFilename(
                _settings.FilenameFormat,
                comicInfo,
                issue ?? "",
                extension,
                _settings.IssueNumberPadding,
                Path.GetFileName(originalPath));
            
            // Remove extension as FormatFilename adds it
            if (filename.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                filename = filename.Substring(0, filename.Length - extension.Length);
            }
            
            // Defensive safety net: ensure the chapter number that exists in
            // the original filename is never lost during rename. This protects
            // against:
            //   * Custom FilenameFormat templates that omit "{issue}" (e.g.
            //     "{series} - {title}").
            //   * Files whose ComicInfo.xml is present but has an empty or
            //     missing <Number>, leaving metadata.Issue null even though
            //     the original filename clearly contains a chapter number.
            // One-shots / volume-only files (no chapter in metadata and no
            // explicit "Ch/Chapter" keyword in the original filename) are
            // intentionally left untouched.
            filename = EnsureChapterNumberPreserved(filename, issue, originalPath);

            filename = ComicFileProcessor.SanitizeFileName(filename);
            
            return Path.Join(directory, filename + extension);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Error generating filename");
            return null;
        }
    }

    /// <summary>
    /// Ensures the chapter number from the original filename (or from
    /// <paramref name="metadataIssue"/>) is present in the new filename
    /// stem. Appends " - Chapter &lt;n&gt;" when it is missing so the
    /// chapter number is never silently dropped during rename.
    /// </summary>
    private string EnsureChapterNumberPreserved(string filenameStem, string? metadataIssue, string originalPath)
    {
        // Preferred chapter source: the authoritative metadata.Issue.
        // Secondary: an explicitly marked "Ch 5"/"#5" number in the original filename
        // (the strict parse avoids false-positives on volume-only filenames).
        var originalNameStem = Path.GetFileNameWithoutExtension(originalPath);
        var chapterToPreserve = !string.IsNullOrEmpty(metadataIssue)
            ? metadataIssue
            : IssueNumberParser.ParseMarked(originalNameStem);

        if (string.IsNullOrEmpty(chapterToPreserve))
        {
            return filenameStem;
        }

        // Check whether the rendered filename already contains an equivalent
        // chapter number, marked or as a standalone number outside brackets and
        // volume tokens, wherever the template put it.
        if (IssueNumberParser.ContainsIssueNumber(filenameStem, chapterToPreserve))
        {
            return filenameStem;
        }

        // FormatFilename keeps an issue it cannot read as a number (e.g. "1,000", "1,5",
        // "Extra") verbatim, so such a value already rendered into the name is preserved.
        if (!float.TryParse(chapterToPreserve, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out _) &&
            ContainsStandaloneToken(filenameStem, chapterToPreserve))
        {
            return filenameStem;
        }

        // Format the chapter number with the configured padding so the
        // appended value is consistent with how the rest of the codebase
        // renders chapter numbers.
        var formattedChapter = FormatChapterForFilename(chapterToPreserve, _settings.IssueNumberPadding);

        _logger.LogWarning(
            "Chapter number '{Chapter}' would be lost from filename during rename of {OriginalPath}; appending it to preserve the chapter.",
            LoggingHelper.SanitizeForLog(chapterToPreserve),
            LoggingHelper.SanitizePathForLog(originalPath));

        var separator = string.IsNullOrEmpty(filenameStem) ? string.Empty : " - ";
        return $"{filenameStem}{separator}Chapter {formattedChapter}";
    }

    private static bool ContainsStandaloneToken(string text, string token)
    {
        var index = text.IndexOf(token, StringComparison.Ordinal);
        while (index >= 0)
        {
            var end = index + token.Length;
            if (IsTokenBoundary(text, index - 1, -1) && IsTokenBoundary(text, end, 1))
            {
                return true;
            }

            index = text.IndexOf(token, index + 1, StringComparison.Ordinal);
        }

        return false;
    }

    // A position is a boundary unless it holds a letter/digit, or a "," / "." joining
    // another numeric component (e.g. the ",000" in "1,000,000").
    private static bool IsTokenBoundary(string text, int position, int direction)
    {
        if (position < 0 || position >= text.Length)
        {
            return true;
        }

        var c = text[position];
        if (char.IsLetterOrDigit(c))
        {
            return false;
        }

        var next = position + direction;
        return !((c == ',' || c == '.') && next >= 0 && next < text.Length && char.IsDigit(text[next]));
    }

    private static string FormatChapterForFilename(string chapter, int padding)
    {
        if (!float.TryParse(chapter, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var asFloat))
        {
            return chapter;
        }

        var integerPart = (int)asFloat;
        var formatString = $"D{ComicFileProcessor.ClampIssueNumberPadding(padding)}";
        var integerFormatted = integerPart.ToString(formatString,
            System.Globalization.CultureInfo.InvariantCulture);

        if (chapter.Contains('.'))
        {
            var parts = chapter.Split('.');
            var decimalPart = parts.Length > 1 ? parts[1].TrimEnd('0') : string.Empty;
            if (!string.IsNullOrEmpty(decimalPart))
            {
                return $"{integerFormatted}.{decimalPart}";
            }
        }

        return integerFormatted;
    }

    /// <summary>
    /// True when renaming <paramref name="sourcePath"/> to <paramref name="targetPath"/> would
    /// collide with another file: one with the target's name or, so a library behaves the same
    /// whether or not its file system ignores case, one whose name differs from it only in case.
    /// The source itself never counts. In particular a target that only changes the source's
    /// case "exists" on a file system that ignores case because it names the source, and
    /// treating that as a collision flagged the only copy of the file as a duplicate.
    /// </summary>
    private bool TargetFileExists(string targetPath, string? sourcePath = null) =>
        FindTargetFile(targetPath, sourcePath) is not null;

    /// <summary>
    /// The other file <see cref="TargetFileExists"/> finds in the way of renaming
    /// <paramref name="sourcePath"/> to <paramref name="targetPath"/>, or null when there is none.
    /// It can be spelled as asked for rather than as the directory lists it; see
    /// <see cref="ListedSpellingOf"/>. The duplicate re-check and compare name it to the user.
    /// </summary>
    private string? FindTargetFile(string targetPath, string? sourcePath = null)
    {
        var directory = Path.GetDirectoryName(targetPath);
        var targetFileName = Path.GetFileName(targetPath);

        if (File.Exists(targetPath))
        {
            if (sourcePath is null || !IsCaseOnlyRename(sourcePath, targetPath))
            {
                return targetPath;
            }

            try
            {
                return IsSameFileUnderAnotherCase(
                    sourcePath,
                    targetPath,
                    Directory.EnumerateFiles(directory!).Select(Path.GetFileName).OfType<string>())
                    ? null
                    : targetPath;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Without a listing the target cannot be told apart from the source. Don't flag
                // the file: the move itself never replaces another file, so a real collision
                // still fails the rename rather than losing a file.
                _logger.LogDebug(ex, "Unable to enumerate directory while checking a case-only rename: {TargetPath}",
                    LoggingHelper.SanitizePathForLog(targetPath));
                return null;
            }
        }

        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(targetFileName) || !Directory.Exists(directory))
        {
            return null;
        }

        var sourceFullPath = sourcePath == null ? null : Path.GetFullPath(sourcePath);

        try
        {
            foreach (var existingPath in Directory.EnumerateFiles(directory))
            {
                var existingFullPath = Path.GetFullPath(existingPath);
                if (sourceFullPath != null && string.Equals(existingFullPath, sourceFullPath, StringComparison.Ordinal))
                {
                    continue;
                }

                if (string.Equals(Path.GetFileName(existingPath), targetFileName, StringComparison.OrdinalIgnoreCase))
                {
                    return existingPath;
                }
            }
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Unable to enumerate directory while checking duplicate target path: {TargetPath}",
                LoggingHelper.SanitizePathForLog(targetPath));
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogDebug(ex, "Access denied while checking duplicate target path: {TargetPath}",
                LoggingHelper.SanitizePathForLog(targetPath));
            return null;
        }

        return null;
    }

    /// <summary>
    /// <paramref name="path"/>, which exists, spelled as its directory lists it: on a file system
    /// that ignores case the name asked for can differ in case from the file's own, and the file
    /// store knows the file by its own. The path as asked for when the listing cannot be read.
    /// </summary>
    private static string ListedSpellingOf(string path)
    {
        try
        {
            var name = Path.GetFileName(path);
            string? otherCase = null;
            foreach (var entry in Directory.EnumerateFiles(Path.GetDirectoryName(path)!))
            {
                var entryName = Path.GetFileName(entry);
                if (string.Equals(entryName, name, StringComparison.Ordinal))
                {
                    return entry;
                }

                if (otherCase is null && string.Equals(entryName, name, StringComparison.OrdinalIgnoreCase))
                {
                    otherCase = entry;
                }
            }

            return otherCase ?? path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return path;
        }
    }
}
