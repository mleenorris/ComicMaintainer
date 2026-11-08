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
    private async Task<bool> RenameFileCoreAsync(string filePath, bool forceReprocess, CancellationToken cancellationToken)
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
            var metadata = await GetMetadataAtResolvedPath(resolvedPath, filePath);

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
                var resolvedSeries = await ResolveNormalizedSeriesAsync(metadata, filePath, cancellationToken);
                if (!string.IsNullOrEmpty(resolvedSeries) && metadata.Series != resolvedSeries)
                {
                    _logger.LogDebug(
                        "RenameFileAsync: Using resolved series name for filename: {SeriesName}",
                        LoggingHelper.SanitizeForLog(resolvedSeries));
                    metadata.Series = resolvedSeries;
                }

                _logger.LogDebug("RenameFileAsync: Generating new filename for file: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                var newResolvedPath = GenerateFileName(metadata, resolvedPath);
                if (newResolvedPath == null)
                {
                    await _fileStore.MarkFileRenamedAsync(filePath, false, cancellationToken);
                    await LogHistoryAsync(filePath, "Rename", false, FilenameGenerationFailedMessage, cancellationToken);
                    return false;
                }

                _logger.LogDebug("RenameFileAsync: Generated filename: {NewFilePath} (Original: {OriginalFilePath})",
                    LoggingHelper.SanitizePathForLog(newResolvedPath), LoggingHelper.SanitizePathForLog(filePath));

                if (newResolvedPath == resolvedPath)
                {
                    _logger.LogInformation("RenameFileAsync: File already has correct name: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                    await _fileStore.MarkFileRenamedAsync(filePath, true, cancellationToken);
                    await ClearDuplicateFlagAsync(filePath, cancellationToken);
                    var filename = Path.GetFileName(filePath);
                    await LogHistoryWithChangesAsync(filePath, "Rename", true, "File already correctly named",
                        filename, filename, metadata, metadata, cancellationToken);
                    return true;
                }

                if (TargetFileExists(newResolvedPath, resolvedPath))
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
                await LogHistoryAsync(filePath, "Rename", false, NoSeriesMessage(filePath, "No metadata or series information found"), cancellationToken);
                return false;
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
        await _fileStore.UpdateFilePathAsync(oldPath, newPath, bookkeeping);
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

        await LogHistoryWithChangesAsync(newPath, "Rename", true, null,
            Path.GetFileName(oldPath), Path.GetFileName(newPath), metadata, metadata, bookkeeping);
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

    /// <summary>
    /// Renders the target path for <paramref name="originalPath"/> from <paramref name="metadata"/>.
    /// Returns null (after logging the error) when the filename cannot be generated; callers must
    /// treat that as a rename failure, never as "already correctly named".
    /// </summary>
    private string? GenerateFileName(ComicMetadata metadata, string originalPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(originalPath) ?? _settings.WatchedDirectory;
            var extension = Path.GetExtension(originalPath);
            
            // Convert ComicMetadata to ComicInfo for use with ComicFileProcessor
            var comicInfo = ComicInfo.FromMetadata(metadata);
            
            // Use ComicFileProcessor.FormatFilename to properly handle decimal issue numbers
            var filename = ComicFileProcessor.FormatFilename(
                _settings.FilenameFormat,
                comicInfo,
                metadata.Issue ?? "",
                extension,
                _settings.IssueNumberPadding);
            
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
            filename = EnsureChapterNumberPreserved(filename, metadata.Issue, originalPath);

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
    private bool TargetFileExists(string targetPath, string? sourcePath = null)
    {
        var directory = Path.GetDirectoryName(targetPath);
        var targetFileName = Path.GetFileName(targetPath);

        if (File.Exists(targetPath))
        {
            if (sourcePath is null || !IsCaseOnlyRename(sourcePath, targetPath))
            {
                return true;
            }

            try
            {
                return !IsSameFileUnderAnotherCase(
                    sourcePath,
                    targetPath,
                    Directory.EnumerateFiles(directory!).Select(Path.GetFileName).OfType<string>());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Without a listing the target cannot be told apart from the source. Don't flag
                // the file: the move itself never replaces another file, so a real collision
                // still fails the rename rather than losing a file.
                _logger.LogDebug(ex, "Unable to enumerate directory while checking a case-only rename: {TargetPath}",
                    LoggingHelper.SanitizePathForLog(targetPath));
                return false;
            }
        }

        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(targetFileName) || !Directory.Exists(directory))
        {
            return false;
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
                    return true;
                }
            }
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Unable to enumerate directory while checking duplicate target path: {TargetPath}",
                LoggingHelper.SanitizePathForLog(targetPath));
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogDebug(ex, "Access denied while checking duplicate target path: {TargetPath}",
                LoggingHelper.SanitizePathForLog(targetPath));
            return false;
        }

        return false;
    }
}
