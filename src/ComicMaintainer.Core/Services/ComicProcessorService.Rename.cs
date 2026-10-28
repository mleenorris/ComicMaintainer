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
    private async Task<bool> RenameFileAsync(string filePath, CancellationToken cancellationToken)
    {
        await _processingSemaphore.WaitAsync(cancellationToken);
        try
        {
            return await RenameFileCoreAsync(filePath, false, cancellationToken);
        }
        finally
        {
            _processingSemaphore.Release();
        }
    }

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

            _logger.LogDebug("RenameFileAsync: Checking if file exists and is a comic archive: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));

            if (!File.Exists(filePath) || !IsComicArchive(filePath))
            {
                _logger.LogWarning("RenameFileAsync: File not found or not a comic archive: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                await LogHistoryAsync(filePath, "Rename", false, "File not found or not a comic archive", cancellationToken);
                return false;
            }

            _logger.LogDebug("RenameFileAsync: Extracting metadata from archive: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            
            // Extract metadata from the archive
            var metadata = await GetMetadataAsync(filePath, cancellationToken);
            
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
                await EnsureChapterMetadataPreservedAsync(filePath, metadata, cancellationToken);

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
                var newFilePath = GenerateFileName(metadata, filePath);
                if (newFilePath == null)
                {
                    await _fileStore.MarkFileRenamedAsync(filePath, false, cancellationToken);
                    await LogHistoryAsync(filePath, "Rename", false, FilenameGenerationFailedMessage, cancellationToken);
                    return false;
                }

                _logger.LogDebug("RenameFileAsync: Generated filename: {NewFilePath} (Original: {OriginalFilePath})", 
                    LoggingHelper.SanitizePathForLog(newFilePath), LoggingHelper.SanitizePathForLog(filePath));
                
                if (newFilePath == filePath)
                {
                    _logger.LogInformation("RenameFileAsync: File already has correct name: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                    await _fileStore.MarkFileRenamedAsync(filePath, true, cancellationToken);
                    var filename = Path.GetFileName(filePath);
                    await LogHistoryWithChangesAsync(filePath, "Rename", true, "File already correctly named",
                        filename, filename, metadata, metadata, cancellationToken);
                    return true;
                }
                
                try
                {
                    _logger.LogInformation(
                        "Renaming file from {OldPath} to {NewPath}",
                        LoggingHelper.SanitizePathForLog(filePath),
                        LoggingHelper.SanitizePathForLog(newFilePath));
                    
                    // Capture before/after filenames for history
                    var beforeFilename = Path.GetFileName(filePath);
                    var afterFilename = Path.GetFileName(newFilePath);

                    // Capture the old path's normalized state *before* the move so it can be
                    // re-asserted on the new path afterwards. UpdateFilePathAsync normally
                    // carries this state forward, but the FileSystemWatcher can observe the
                    // move as a Delete(old)+Create(new) pair and remove the authoritative
                    // old-path row before the processor migrates it. When that happens the
                    // merge in UpdateFilePathAsync has nothing to merge from and the new row
                    // keeps the watcher stub's default IsNormalized=false, so a
                    // normalize-then-rename sequence would leave the file "unprocessed" until
                    // it is normalized again. Re-stamping the captured state closes that gap.
                    var wasNormalized = await _fileStore.IsFileNormalizedAsync(filePath, cancellationToken);

                    // File.Move will throw IOException if target exists, which we catch and handle
                    // This avoids race condition from check-then-act pattern
                    File.Move(filePath, newFilePath);
                    
                    // Atomically remap the file-store row from the old path to the new path,
                    // preserving all processing state (IsRenamed/IsNormalized/SeriesMetadataVersion/
                    // IsDuplicate/IsRead/...) and avoiding the race with FileWatcherService's
                    // OnFileRenamed handler, which also calls UpdateFilePathAsync. Using
                    // RemoveFileAsync+AddFileAsync here previously left the old path tracked
                    // when the watcher's update interleaved with the processor's removal —
                    // the file then only disappeared from the file list on the next full
                    // library scan (reconcile-deletions pass). This matches the pattern
                    // already used by ProcessFileCoreAsync.
                    await _fileStore.UpdateFilePathAsync(filePath, newFilePath, cancellationToken);
                    await _fileStore.MarkFileRenamedAsync(newFilePath, true, cancellationToken);

                    // Re-assert the captured normalized state on the new path so it survives
                    // even if the watcher removed the old row before UpdateFilePathAsync ran.
                    if (wasNormalized)
                    {
                        await _fileStore.MarkFileNormalizedAsync(newFilePath, true, cancellationToken);
                    }
                    
                    _logger.LogInformation("File renamed successfully: {NewPath}", LoggingHelper.SanitizePathForLog(newFilePath));
                    await LogHistoryWithChangesAsync(newFilePath, "Rename", true, null, 
                        beforeFilename, afterFilename, metadata, metadata, cancellationToken);
                    return true;
                }
                catch (IOException ex) when (TargetFileExists(newFilePath, filePath))
                {
                    // Target file already exists - treat as duplicate
                    _logger.LogInformation(ex, "RenameFileAsync: Duplicate detected - target file already exists: {NewPath}", LoggingHelper.SanitizePathForLog(newFilePath));
                    await _fileStore.MarkFileRenamedAsync(filePath, false, cancellationToken);
                    await _fileStore.MarkFileDuplicateAsync(filePath, true, cancellationToken);
                    await LogHistoryAsync(filePath, "Duplicate Detection", true, "Target file already exists", cancellationToken);
                    return false;
                }
            }
            else
            {
                _logger.LogWarning("RenameFileAsync: No metadata or series information found for: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                await _fileStore.MarkFileRenamedAsync(filePath, false, cancellationToken);
                await LogHistoryAsync(filePath, "Rename", false, "No metadata or series information found", cancellationToken);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RenameFileAsync: Error renaming file: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            await _fileStore.MarkFileRenamedAsync(filePath, false, cancellationToken);
            await LogHistoryAsync(filePath, "Rename", false, ex.Message, cancellationToken);
            return false;
        }
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
            
            return Path.Combine(directory, filename + extension);
        }
        catch (Exception ex)
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
        // Secondary: an explicit "Ch/Chapter <n>" token in the original filename
        // (the strict pattern avoids false-positives on volume-only filenames).
        var originalNameStem = Path.GetFileNameWithoutExtension(originalPath);
        var chapterToPreserve = !string.IsNullOrEmpty(metadataIssue)
            ? metadataIssue
            : ComicFileProcessor.ParseChapterKeyword(originalNameStem);

        if (string.IsNullOrEmpty(chapterToPreserve))
        {
            return filenameStem;
        }

        // Check whether the rendered filename already contains an equivalent
        // chapter number. ParseChapterNumber prefers the chapter keyword and
        // otherwise picks the first non-bracketed number, which mirrors the
        // way the rendered template typically encodes the issue number.
        var renderedChapter = ComicFileProcessor.ParseChapterNumber(filenameStem);
        if (!string.IsNullOrEmpty(renderedChapter) &&
            ComicFileProcessor.ChapterNumbersEquivalent(renderedChapter, chapterToPreserve))
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
        var formatString = $"D{Math.Max(0, padding)}";
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

    private bool TargetFileExists(string targetPath, string? sourcePath = null)
    {
        if (File.Exists(targetPath))
        {
            return true;
        }

        var directory = Path.GetDirectoryName(targetPath);
        var targetFileName = Path.GetFileName(targetPath);
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
