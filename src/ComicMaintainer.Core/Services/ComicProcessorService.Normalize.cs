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

    private async Task<bool> NormalizeFileCoreAsync(string filePath, bool forceReprocess, CancellationToken cancellationToken) =>
        (await NormalizeFileWithResultAsync(filePath, forceReprocess, cancellationToken)).Succeeded;

    /// <summary>
    /// Normalizes one file and returns whether it succeeded and where the file is afterwards: a
    /// CBR/RAR/ZIP that normalize wrote is a new CBZ (see <see cref="UpdateMetadataCoreAsync"/>).
    /// </summary>
    private async Task<(bool Succeeded, string FilePath)> NormalizeFileWithResultAsync(string filePath, bool forceReprocess, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("NormalizeFileAsync: Starting normalize for file: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));

            // Skip files that are already marked as normalized in the database.
            if (!forceReprocess && await _fileStore.IsFileNormalizedAsync(filePath, cancellationToken))
            {
                _logger.LogInformation("NormalizeFileAsync: File already normalized (database), skipping: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                return (true, filePath);
            }

            _logger.LogDebug("NormalizeFileAsync: Checking if file exists and is a comic archive: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));

            if (!File.Exists(filePath) || !IsComicArchive(filePath))
            {
                _logger.LogWarning("NormalizeFileAsync: File not found or not a comic archive: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                await _fileStore.MarkFileNormalizedAsync(filePath, false, cancellationToken);
                await LogHistoryAsync(filePath, "Normalize", false, "File not found or not a comic archive", cancellationToken);
                return (false, filePath);
            }

            _logger.LogDebug("NormalizeFileAsync: Extracting metadata from archive: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            
            // Extract metadata from the archive
            var metadata = await GetMetadataAsync(filePath, cancellationToken);
            
            if (metadata == null)
            {
                _logger.LogWarning("NormalizeFileAsync: No metadata found for: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                await _fileStore.MarkFileNormalizedAsync(filePath, false, cancellationToken);
                await LogHistoryAsync(filePath, "Normalize", false, "No metadata found", cancellationToken);
                return (false, filePath);
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
                return (true, filePath);
            }
            
            _logger.LogDebug("NormalizeFileAsync: File needs normalization, updating metadata: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));

            // Capture before metadata state (current metadata)
            var beforeMetadata = metadata.Clone();
            var normalizedMetadata = await NormalizeMetadataAsync(metadata, filePath, cancellationToken);

            // Write only the fields normalize owns into ComicInfo.xml; every other element stays.
            var write = await UpdateMetadataCoreAsync(filePath, normalizedMetadata, NormalizedFields, cancellationToken);
            var success = write.Succeeded;
            var beforeFilename = Path.GetFileName(filePath);
            filePath = write.FilePath;
            await _fileStore.MarkFileNormalizedAsync(filePath, success, cancellationToken);

            if (success)
            {
                await StampSeriesMetadataVersionAsync(filePath, normalizedMetadata.Series, cancellationToken);
                _logger.LogInformation("NormalizeFileAsync: File normalized successfully: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                await LogHistoryWithChangesAsync(filePath, "Normalize", true, DescribeUnwrittenOutcome(write),
                    beforeFilename, Path.GetFileName(filePath), beforeMetadata, normalizedMetadata, cancellationToken);
            }
            else
            {
                _logger.LogWarning("NormalizeFileAsync: Failed to normalize file: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                await LogHistoryAsync(filePath, "Normalize", false, "Failed to update metadata", cancellationToken);
            }
            
            return (success, filePath);
        }
        // A cancelled job didn't fail this file: leave it and its stored state as they are.
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "NormalizeFileAsync: Error normalizing file: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            await _fileStore.MarkFileNormalizedAsync(filePath, false, cancellationToken);
            await LogHistoryAsync(filePath, "Normalize", false, ex.Message, cancellationToken);
            return (false, filePath);
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

        if (!hasSeries && !string.IsNullOrEmpty(expectedSeries))
        {
            // The series is missing but known from the folder or the series cache.
            return false;
        }
        // A missing <Number> is not normalized when the filename holds the issue
        // number: normalize recovers it into the metadata (see NormalizeMetadataAsync).
        var expectedIssue = metadata.Issue;
        if (string.IsNullOrEmpty(expectedIssue) &&
            !string.IsNullOrEmpty(IssueNumberParser.Parse(Path.GetFileNameWithoutExtension(filePath))))
        {
            return false;
        }

        // The issue field must not be zero padded. If the stored <Number> is
        // padded (e.g. "0012"), the file is not normalized and should be
        // re-processed so both <Number> and <Title> become unpadded.
        if (!string.IsNullOrEmpty(expectedIssue))
        {
            var strippedIssue = IssueNumberParser.StripLeadingZeros(expectedIssue);
            if (!string.Equals(strippedIssue, expectedIssue, StringComparison.Ordinal))
            {
                return false;
            }
            expectedIssue = strippedIssue;
        }

        // A real chapter title counts as normalized; only a missing title or a stale
        // generated "Chapter <n>" one is rewritten, so a title alone never forces a rewrite.
        var expectedTitle = NormalizedTitleFor(metadata.Title, expectedIssue);
        _logger.LogDebug(
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
    private static string CreateNormalizedTitle(string? issueNumber)
    {
        if (string.IsNullOrEmpty(issueNumber))
            return "Chapter Unknown";

        return $"Chapter {issueNumber}";
    }

    /// <summary>A title in the form normalize generates: "Chapter 5", "Chapter Unknown".</summary>
    private static readonly Regex GeneratedChapterTitlePattern = new(
        @"^\s*chapter\s+(?<value>\S+)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// The title normalize settles on. A real chapter title (what Suwayomi and most taggers write)
    /// is kept. A missing title becomes "Chapter &lt;issue&gt;", and so does a generated
    /// "Chapter &lt;n&gt;" title that no longer matches the issue number (for example
    /// "Chapter 0012" or "Chapter Unknown" for issue 12).
    /// </summary>
    private static string NormalizedTitleFor(string? title, string? issue)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return CreateNormalizedTitle(issue);
        }

        var generated = GeneratedChapterTitlePattern.Match(title);
        if (generated.Success &&
            !string.IsNullOrEmpty(issue) &&
            !string.Equals(generated.Groups["value"].Value, issue, StringComparison.Ordinal))
        {
            return CreateNormalizedTitle(issue);
        }

        return title;
    }

    /// <summary>
    /// The history note for a write that succeeded without rewriting the archive, or null.
    /// </summary>
    private static string? DescribeUnwrittenOutcome(MetadataWriteResult write) => write.Outcome switch
    {
        MetadataWriteOutcome.Unchanged => "ComicInfo.xml already up to date",
        MetadataWriteOutcome.NotWritable => "Archive not rewritten: converting CBR/RAR to CBZ is turned off",
        MetadataWriteOutcome.KeptInDatabase => "Kept in the database: converting CBR/RAR to CBZ is turned off",
        _ => null
    };

    /// <summary>
    /// When a file's metadata is missing the issue/chapter number but the
    /// original filename contains an explicitly marked chapter ("Ch 5", "#5"),
    /// recover the chapter number into the metadata's <c>Issue</c> field (and a
    /// missing title into "Chapter &lt;n&gt;") so the rename keeps the chapter.
    /// While normalize is enabled the recovered fields are also written into the
    /// archive's ComicInfo.xml; with normalize disabled the archive is never
    /// rewritten and the recovered chapter only shapes the new filename.
    /// Returns the file's path afterwards, which differs from
    /// <paramref name="filePath"/> only when the write converted the archive to CBZ.
    /// </summary>
    private async Task<string> EnsureChapterMetadataPreservedAsync(
        string filePath,
        ComicMetadata metadata,
        CancellationToken cancellationToken)
    {
        if (metadata == null || !string.IsNullOrEmpty(metadata.Issue))
        {
            return filePath;
        }

        var originalNameStem = Path.GetFileNameWithoutExtension(filePath);
        var parsedChapter = IssueNumberParser.ParseMarked(originalNameStem);
        if (string.IsNullOrEmpty(parsedChapter))
        {
            return filePath;
        }

        metadata.Issue = parsedChapter;
        metadata.Title = NormalizedTitleFor(metadata.Title, parsedChapter);

        if (!_settings.WatcherEnableNormalize)
        {
            _logger.LogDebug(
                "Using chapter number '{Chapter}' from the filename for the new name of {FilePath}; normalize is disabled, so ComicInfo.xml is left unchanged.",
                parsedChapter,
                LoggingHelper.SanitizePathForLog(filePath));
            return filePath;
        }

        _logger.LogInformation(
            "Recovering chapter number '{Chapter}' from filename into metadata for {FilePath}; setting Issue and Title='{Title}'.",
            parsedChapter,
            LoggingHelper.SanitizePathForLog(filePath),
            LoggingHelper.SanitizeForLog(metadata.Title));

        var write = await UpdateMetadataCoreAsync(
            filePath,
            metadata,
            ComicMetadataFieldFlags.Issue | ComicMetadataFieldFlags.Title,
            cancellationToken);
        if (!write.Succeeded)
        {
            _logger.LogWarning(
                "Failed to write recovered chapter number '{Chapter}' to metadata for {FilePath}.",
                parsedChapter,
                LoggingHelper.SanitizePathForLog(filePath));
        }

        return write.FilePath;
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
            var parsedIssue = IssueNumberParser.Parse(Path.GetFileNameWithoutExtension(filePath));
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
            var strippedIssue = IssueNumberParser.StripLeadingZeros(normalizedMetadata.Issue);
            if (!string.Equals(strippedIssue, normalizedMetadata.Issue, StringComparison.Ordinal))
            {
                _logger.LogDebug("Stripping issue padding: {Before} -> {After}",
                    LoggingHelper.SanitizeForLog(normalizedMetadata.Issue),
                    LoggingHelper.SanitizeForLog(strippedIssue));
                normalizedMetadata.Issue = strippedIssue;
            }
        }

        // Keep a real chapter title; fill in a missing one and correct a stale generated one.
        var normalizedTitle = NormalizedTitleFor(normalizedMetadata.Title, normalizedMetadata.Issue);
        if (!string.Equals(normalizedTitle, normalizedMetadata.Title, StringComparison.Ordinal))
        {
            _logger.LogDebug("Setting title to standard format: {Title}", LoggingHelper.SanitizeForLog(normalizedTitle));
            normalizedMetadata.Title = normalizedTitle;
        }
        
        return normalizedMetadata;
    }
}
