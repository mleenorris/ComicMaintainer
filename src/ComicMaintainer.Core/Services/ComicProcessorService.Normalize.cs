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
    private async Task<bool> NormalizeFileAsync(string filePath, CancellationToken cancellationToken)
    {
        await _processingSemaphore.WaitAsync(cancellationToken);
        try
        {
            return await NormalizeFileCoreAsync(filePath, false, cancellationToken);
        }
        finally
        {
            _processingSemaphore.Release();
        }
    }

    private async Task<bool> NormalizeFileCoreAsync(string filePath, bool forceReprocess, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("NormalizeFileAsync: Starting normalize for file: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));

            // Skip files that are already marked as normalized in the database.
            if (!forceReprocess && await _fileStore.IsFileNormalizedAsync(filePath, cancellationToken))
            {
                _logger.LogInformation("NormalizeFileAsync: File already normalized (database), skipping: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                return true;
            }

            _logger.LogDebug("NormalizeFileAsync: Checking if file exists and is a comic archive: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));

            if (!File.Exists(filePath) || !IsComicArchive(filePath))
            {
                _logger.LogWarning("NormalizeFileAsync: File not found or not a comic archive: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                await _fileStore.MarkFileNormalizedAsync(filePath, false, cancellationToken);
                await LogHistoryAsync(filePath, "Normalize", false, "File not found or not a comic archive", cancellationToken);
                return false;
            }

            _logger.LogDebug("NormalizeFileAsync: Extracting metadata from archive: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            
            // Extract metadata from the archive
            var metadata = await GetMetadataAsync(filePath, cancellationToken);
            
            if (metadata == null)
            {
                _logger.LogWarning("NormalizeFileAsync: No metadata found for: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                await _fileStore.MarkFileNormalizedAsync(filePath, false, cancellationToken);
                await LogHistoryAsync(filePath, "Normalize", false, "No metadata found", cancellationToken);
                return false;
            }

            _logger.LogDebug("NormalizeFileAsync: Metadata extracted - Series: {Series}, Issue: {Issue}, Title: {Title}", 
                LoggingHelper.SanitizeForLog(metadata.Series),
                LoggingHelper.SanitizeForLog(metadata.Issue),
                LoggingHelper.SanitizeForLog(metadata.Title));

            // Check if metadata is already normalized (has ComicInfo.xml with valid data and series name matches folder)
            // If we successfully read metadata, it means ComicInfo.xml exists and is parseable
            var isNormalized = await IsMetadataNormalizedAsync(metadata, filePath, cancellationToken);
            _logger.LogDebug("NormalizeFileAsync: Metadata normalization check - IsNormalized: {IsNormalized}", isNormalized);
            
            if (isNormalized)
            {
                _logger.LogInformation("NormalizeFileAsync: File already has normalized metadata: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                await _fileStore.MarkFileNormalizedAsync(filePath, true, cancellationToken);
                await StampSeriesMetadataVersionAsync(filePath, metadata.Series, cancellationToken);
                var filename = Path.GetFileName(filePath);
                await LogHistoryWithChangesAsync(filePath, "Normalize", true, "File already normalized",
                    filename, filename, metadata, metadata, cancellationToken);
                return true;
            }
            
            _logger.LogDebug("NormalizeFileAsync: File needs normalization, updating metadata: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));

            // Capture before metadata state (current metadata)
            var beforeMetadata = metadata.Clone();
            var normalizedMetadata = await NormalizeMetadataAsync(metadata, filePath, cancellationToken);

            // Update metadata (normalize it by re-writing ComicInfo.xml)
            var success = await UpdateMetadataCoreAsync(filePath, normalizedMetadata, cancellationToken);
            await _fileStore.MarkFileNormalizedAsync(filePath, success, cancellationToken);
            
            if (success)
            {
                await StampSeriesMetadataVersionAsync(filePath, normalizedMetadata.Series, cancellationToken);
                _logger.LogInformation("NormalizeFileAsync: File normalized successfully: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                var filename = Path.GetFileName(filePath);
                // For normalize operations, we log the same metadata as before/after since we're ensuring
                // the existing metadata is written properly to ComicInfo.xml
                await LogHistoryWithChangesAsync(filePath, "Normalize", true, null, 
                    filename, filename, beforeMetadata, normalizedMetadata, cancellationToken);
            }
            else
            {
                _logger.LogWarning("NormalizeFileAsync: Failed to normalize file: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                await LogHistoryAsync(filePath, "Normalize", false, "Failed to update metadata", cancellationToken);
            }
            
            return success;
        }
        // A cancelled job didn't fail this file: leave it and its stored state as they are.
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NormalizeFileAsync: Error normalizing file: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            await _fileStore.MarkFileNormalizedAsync(filePath, false, cancellationToken);
            await LogHistoryAsync(filePath, "Normalize", false, ex.Message, cancellationToken);
            return false;
        }
    }

    /// <summary>
    /// Helper method to check if metadata is already normalized.
    /// Verifies that the file has ComicInfo.xml with minimum required fields to identify the comic.
    /// A file is considered normalized if it has valid metadata AND the series name matches the folder name.
    /// This avoids unnecessary re-writing of metadata when the file already has valid comic information.
    /// </summary>
    /// <param name="metadata">The metadata to check</param>
    /// <param name="filePath">The file path to check against folder name</param>
    /// <returns>True if the metadata meets minimum normalization requirements, false otherwise</returns>
    private async Task<bool> IsMetadataNormalizedAsync(ComicMetadata metadata, string filePath, CancellationToken cancellationToken)
    {
        // Metadata is considered normalized if it has at least Series OR (Title AND Issue)
        // This ensures we have enough information to identify the comic
        var hasSeries = !string.IsNullOrEmpty(metadata.Series);
        var hasTitleAndIssue = !string.IsNullOrEmpty(metadata.Title) && !string.IsNullOrEmpty(metadata.Issue);
        
        if (!hasSeries && !hasTitleAndIssue)
        {
            return false;
        }
        
        // Additionally check if the series name matches the folder name
        var expectedSeries = await ResolveNormalizedSeriesAsync(metadata, filePath, cancellationToken);
        if (hasSeries && metadata.Series != expectedSeries)
        {
            // Series exists but doesn't match folder name - not normalized
            return false;
        }
        // Compute the expected title using the same logic as NormalizeMetadataAsync:
        // if the ComicInfo issue is missing, fall back to parsing the chapter
        // number from the filename so the expected title becomes "Chapter <n>"
        // instead of "Chapter Unknown" (which would incorrectly mark files with
        // a discoverable chapter number in their filename as already normalized).
        var expectedIssue = metadata.Issue;
        if (string.IsNullOrEmpty(expectedIssue))
        {
            var parsedIssue = ComicFileProcessor.ParseChapterNumber(Path.GetFileNameWithoutExtension(filePath));
            if (!string.IsNullOrEmpty(parsedIssue))
            {
                expectedIssue = parsedIssue;
            }
        }

        // The issue field must not be zero padded. If the stored <Number> is
        // padded (e.g. "0012"), the file is not normalized and should be
        // re-processed so both <Number> and <Title> become unpadded.
        if (!string.IsNullOrEmpty(expectedIssue))
        {
            var strippedIssue = ComicFileProcessor.StripLeadingZeros(expectedIssue);
            if (!string.Equals(strippedIssue, expectedIssue, StringComparison.Ordinal))
            {
                return false;
            }
            expectedIssue = strippedIssue;
        }

        var expectedTitle = CreateNormalizedTitle(expectedIssue);
        _logger.LogInformation(
            "Checking title normalization for file: {FilePath}, Current metadata {title} Expected {expectedTitle}",
            LoggingHelper.SanitizePathForLog(filePath),
            LoggingHelper.SanitizeForLog(metadata.Title),
            LoggingHelper.SanitizeForLog(expectedTitle));
        if (!string.Equals(metadata.Title, expectedTitle, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    ///<summary>
    /// Helper method to create nomalized title from issue number
    /// </summary>
    private string CreateNormalizedTitle(string? issueNumber)
    {
        if (string.IsNullOrEmpty(issueNumber))
            return "Chapter Unknown";
        
        return $"Chapter {issueNumber}";
    }

    /// <summary>
    /// When a file's metadata is missing the issue/chapter number but the
    /// original filename contains an explicit "Ch"/"Chapter &lt;n&gt;" token,
    /// recover the chapter number into the metadata's <c>Issue</c> field and
    /// set <c>Title</c> to "Chapter &lt;n&gt;", then persist the updated
    /// metadata back to the archive's ComicInfo.xml. This ensures the chapter
    /// number is preserved in metadata (not just in the filename) during
    /// rename operations on files that only have the series tag set.
    /// Returns the (possibly updated) metadata instance and a flag indicating
    /// whether metadata was modified.
    /// </summary>
    private async Task<bool> EnsureChapterMetadataPreservedAsync(
        string filePath,
        ComicMetadata metadata,
        CancellationToken cancellationToken)
    {
        if (metadata == null || !string.IsNullOrEmpty(metadata.Issue))
        {
            return false;
        }

        var originalNameStem = Path.GetFileNameWithoutExtension(filePath);
        var parsedChapter = ComicFileProcessor.ParseChapterKeyword(originalNameStem);
        if (string.IsNullOrEmpty(parsedChapter))
        {
            return false;
        }

        var normalizedTitle = CreateNormalizedTitle(parsedChapter);

        _logger.LogInformation(
            "Recovering chapter number '{Chapter}' from filename into metadata for {FilePath}; setting Issue and Title='{Title}'.",
            parsedChapter,
            LoggingHelper.SanitizePathForLog(filePath),
            LoggingHelper.SanitizeForLog(normalizedTitle));

        metadata.Issue = parsedChapter;
        metadata.Title = normalizedTitle;

        var success = await UpdateMetadataCoreAsync(filePath, metadata, cancellationToken);
        if (!success)
        {
            _logger.LogWarning(
                "Failed to write recovered chapter number '{Chapter}' to metadata for {FilePath}.",
                parsedChapter,
                LoggingHelper.SanitizePathForLog(filePath));
        }

        return success;
    }

    ///<summary>
    /// Helper method to consolidate normalization functions
    /// </summary>
    private async Task<ComicMetadata> NormalizeMetadataAsync(ComicMetadata metadata, string filePath, CancellationToken cancellationToken)
    {
        var normalizedMetadata = metadata.Clone();

        var normalizedSeries = await ResolveNormalizedSeriesAsync(metadata, filePath, cancellationToken);
        if (!string.IsNullOrEmpty(normalizedSeries) && normalizedMetadata.Series != normalizedSeries)
        {
            _logger.LogDebug("Setting normalized series name: {SeriesName}", LoggingHelper.SanitizeForLog(normalizedSeries));
            normalizedMetadata.Series = normalizedSeries;
        }

        // If the ComicInfo metadata is missing the issue/chapter number, fall back
        // to parsing it from the filename so the chapter number isn't lost and the
        // normalized title becomes "Chapter <n>" instead of "Chapter Unknown".
        if (string.IsNullOrEmpty(normalizedMetadata.Issue))
        {
            var parsedIssue = ComicFileProcessor.ParseChapterNumber(Path.GetFileNameWithoutExtension(filePath));
            if (!string.IsNullOrEmpty(parsedIssue))
            {
                _logger.LogDebug("Setting issue number from filename: {Issue}", LoggingHelper.SanitizeForLog(parsedIssue));
                normalizedMetadata.Issue = parsedIssue;
            }
        }

        // The per-file metadata issue field is not zero padded (only the
        // generated filename is). Strip any leading zero padding so the
        // <Number> tag holds the bare issue number, e.g. "0012" -> "12",
        // "0012.5" -> "12.5".
        if (!string.IsNullOrEmpty(normalizedMetadata.Issue))
        {
            var strippedIssue = ComicFileProcessor.StripLeadingZeros(normalizedMetadata.Issue);
            if (!string.Equals(strippedIssue, normalizedMetadata.Issue, StringComparison.Ordinal))
            {
                _logger.LogDebug("Stripping issue padding: {Before} -> {After}",
                    LoggingHelper.SanitizeForLog(normalizedMetadata.Issue),
                    LoggingHelper.SanitizeForLog(strippedIssue));
                normalizedMetadata.Issue = strippedIssue;
            }
        }

        // Set title to standard format
        string normalizedTitle = CreateNormalizedTitle(normalizedMetadata.Issue);
        if(!normalizedTitle.Equals(normalizedMetadata.Title))
        {
            _logger.LogDebug("Setting title to standard format: {Title}", LoggingHelper.SanitizeForLog(normalizedTitle));
            normalizedMetadata.Title = normalizedTitle;
        }
        
        return normalizedMetadata;
    }
}
