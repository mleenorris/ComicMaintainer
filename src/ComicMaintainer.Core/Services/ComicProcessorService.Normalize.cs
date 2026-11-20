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
    private async Task<bool> NormalizeFileCoreAsync(string filePath, bool forceReprocess, CancellationToken cancellationToken) =>
        (await NormalizeFileWithResultAsync(filePath, forceReprocess, cancellationToken)).Succeeded;

    private async Task<BatchItemResult> NormalizeFileItemAsync(string filePath, bool forceReprocess, CancellationToken cancellationToken)
    {
        var result = await NormalizeFileWithResultAsync(filePath, forceReprocess, cancellationToken);
        return new BatchItemResult(result.Succeeded, result.Error);
    }

    /// <summary>
    /// The outcome of normalizing one file.
    /// </summary>
    /// <param name="Succeeded">True when the file's metadata is normalized.</param>
    /// <param name="FilePath">
    /// Where the file is afterwards: a CBR/RAR/ZIP that normalize wrote is a new CBZ
    /// (see <see cref="UpdateMetadataCoreAsync"/>).
    /// </param>
    /// <param name="ArchiveNotWritable">
    /// True when the file needs normalizing but is a CBR/RAR/ZIP that is not rewritten because
    /// converting them to CBZ is turned off (see <see cref="DeferUnwritableNormalizeAsync"/>).
    /// </param>
    /// <param name="Error">
    /// Why normalizing failed, when that can be told to users: see
    /// <see cref="MetadataWriteResult.FailureReason"/>.
    /// </param>
    private readonly record struct NormalizeFileResult(bool Succeeded, string FilePath, bool ArchiveNotWritable = false, string? Error = null);

    /// <summary>
    /// Normalizes one file and returns whether it succeeded and where the file is afterwards.
    /// </summary>
    private async Task<NormalizeFileResult> NormalizeFileWithResultAsync(string filePath, bool forceReprocess, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("NormalizeFileAsync: Starting normalize for file: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));

            // Skip files that are already marked as normalized in the database.
            if (!forceReprocess && await _fileStore.IsFileNormalizedAsync(filePath, cancellationToken))
            {
                _logger.LogInformation("NormalizeFileAsync: File already normalized (database), skipping: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                return new(true, filePath);
            }

            _logger.LogDebug("NormalizeFileAsync: Checking if file exists and is a comic archive: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));

            if (!File.Exists(filePath) || !IsComicArchive(filePath))
            {
                _logger.LogWarning("NormalizeFileAsync: File not found or not a comic archive: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                await _fileStore.MarkFileNormalizedAsync(filePath, false, cancellationToken);
                await LogHistoryAsync(filePath, "Normalize", false, "File not found or not a comic archive", cancellationToken);
                return new(false, filePath);
            }

            _logger.LogDebug("NormalizeFileAsync: Extracting metadata from archive: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            
            // Extract metadata from the archive
            var metadataRead = await ReadMetadataAsync(filePath, cancellationToken);
            var metadata = metadataRead.Metadata;

            if (metadata == null)
            {
                _logger.LogWarning("NormalizeFileAsync: No metadata found for: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                await _fileStore.MarkFileNormalizedAsync(filePath, false, cancellationToken);
                await LogHistoryAsync(filePath, "Normalize", false, metadataRead.FailureReason ?? "No metadata found", cancellationToken);
                return new(false, filePath, Error: metadataRead.FailureReason);
            }

            _logger.LogDebug("NormalizeFileAsync: Metadata extracted - Series: {Series}, Issue: {Issue}, Title: {Title}", 
                LoggingHelper.SanitizeForLog(metadata.Series),
                LoggingHelper.SanitizeForLog(metadata.Issue),
                LoggingHelper.SanitizeForLog(metadata.Title));

            // Check if metadata is already normalized (has ComicInfo.xml with valid data and series name matches folder)
            // If we successfully read metadata, it means ComicInfo.xml exists and is parseable
            var plan = await PlanNormalizeAsync(metadata, filePath, mutateCache: true, cancellationToken);
            _logger.LogDebug("NormalizeFileAsync: Metadata normalization check - IsNormalized: {IsNormalized}", plan.AlreadyNormalized);

            if (plan.AlreadyNormalized)
            {
                _logger.LogInformation("NormalizeFileAsync: File already has normalized metadata: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                await _fileStore.MarkFileNormalizedAsync(filePath, true, cancellationToken);
                if (await FillFromProviderAsync(filePath, metadata, plan.ProviderFill, cancellationToken) is { Succeeded: true } filled)
                {
                    filePath = filled.FilePath;
                }

                await StampSeriesMetadataVersionAsync(filePath, metadata.Series, cancellationToken);
                var filename = Path.GetFileName(filePath);
                await LogHistoryWithChangesAsync(filePath, "Normalize", true, "File already normalized",
                    filename, filename, metadata, metadata, cancellationToken);
                return new(true, filePath);
            }

            _logger.LogDebug("NormalizeFileAsync: File needs normalization, updating metadata: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));

            var beforeMetadata = plan.Before;
            var normalizedMetadata = plan.Normalized;

            // Write only the fields normalize owns into ComicInfo.xml, plus any provider details
            // that fill an empty element, so the archive is rewritten once; every other element stays.
            var write = await UpdateMetadataCoreAsync(filePath, normalizedMetadata, plan.Fields, cancellationToken, providerFill: plan.ProviderFill);
            if (write.Outcome == MetadataWriteOutcome.NotWritable)
            {
                await DeferUnwritableNormalizeAsync(filePath, normalizedMetadata.Series, cancellationToken);
                return new(false, filePath, ArchiveNotWritable: true);
            }

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
                await LogHistoryAsync(filePath, "Normalize", false, write.FailureReason ?? "Failed to update metadata", cancellationToken);
            }

            return new(success, filePath, Error: write.FailureReason);
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
            return new(false, filePath);
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
    /// <param name="mutateCache">Passed to the series name resolver: false for a preview, which must change nothing.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>True if the metadata meets minimum normalization requirements, false otherwise</returns>
    private async Task<bool> IsMetadataNormalizedAsync(ComicMetadata metadata, string filePath, bool mutateCache, CancellationToken cancellationToken)
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
        var expectedSeries = await ResolveNormalizedSeriesAsync(metadata, filePath, mutateCache, cancellationToken);
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
        // "Annual 1" is stored as "1" (see NormalizeMetadataAsync).
        if (AnnualIssue.IsAnnualIssueText(metadata.Issue))
        {
            return false;
        }

        // A volume number a tagger put in <Number> is corrected from the file name (see IssueMistakenForVolumeAsync).
        if (await IssueMistakenForVolumeAsync(metadata, filePath, cancellationToken) is not null)
        {
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

        // Every name the series goes by, to recognise an old file name left in the title.
        var seriesNames = await SeriesNamesForTitleAsync(metadata, expectedSeries, metadata.Issue, cancellationToken);

        // A tie-in's suffix the names give ("001.MU") still has to be written into <Number>.
        if (IssueSuffixToFill(metadata, filePath, seriesNames) is not null)
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
        var expectedTitle = NormalizedTitleFor(metadata, expectedIssue, await IsTitleLockedAsync(filePath, cancellationToken), seriesNames);
        _logger.LogDebug(
            "Checking title normalization for file: {FilePath}, Current metadata {title} Expected {expectedTitle}",
            LoggingHelper.SanitizePathForLog(filePath),
            LoggingHelper.SanitizeForLog(metadata.Title),
            LoggingHelper.SanitizeForLog(expectedTitle));
        if (!string.Equals(metadata.Title, expectedTitle, StringComparison.Ordinal))
        {
            return false;
        }

        // A volume or cover year named by the series, file, title or folder name still has to be written.
        if (VolumeToWrite(metadata, filePath, seriesNames) is not null
            || VolumeYearToFill(metadata, filePath, seriesNames) is not null
            || YearToFill(metadata, filePath, seriesNames) is not null)
        {
            return false;
        }

        // An Annual named by its series, number or file name still has to be marked in <Format>.
        if (FormatToFill(metadata, filePath) is not null)
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
    /// The title normalize settles on for <paramref name="metadata"/> with issue
    /// <paramref name="issue"/>. A real chapter title (what Suwayomi and most taggers write) is
    /// kept. A missing title becomes "Chapter &lt;issue&gt;", and so does a generated
    /// "Chapter &lt;n&gt;" title that no longer matches the issue number (for example
    /// "Chapter 0012" or "Chapter Unknown" for issue 12), and an old file name a tagger left as
    /// the title (<c>v4 010 (2018)</c>, see <see cref="FileNameTitle.IsFileName"/>), even one naming
    /// another issue, as the tags are trusted over it: its volume and cover year are read in the
    /// same normalize, and the generated title is a placeholder ComicVine's issue title replaces. An old file name is kept when
    /// <paramref name="titleLocked"/>: the user set the title by editing it. The file name may
    /// start with any of <paramref name="seriesNames"/> (see <see cref="SeriesNamesForTitleAsync"/>).
    /// </summary>
    private static string NormalizedTitleFor(ComicMetadata metadata, string? issue, bool titleLocked, IReadOnlyCollection<string?> seriesNames)
    {
        var title = metadata.Title;
        if (string.IsNullOrWhiteSpace(title))
        {
            return CreateNormalizedTitle(issue);
        }

        if (!titleLocked && !string.IsNullOrEmpty(issue) && FileNameTitle.IsFileName(title, seriesNames, issue))
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
    /// True when the user locked the file's title by editing it. The lock is kept in the database,
    /// not in the archive, so it is read from the stored record.
    /// </summary>
    private Task<bool> IsTitleLockedAsync(string filePath, CancellationToken cancellationToken)
        => IsFieldLockedAsync(filePath, ComicMetadataFieldFlags.Title, cancellationToken);

    private async Task<bool> IsFieldLockedAsync(string filePath, ComicMetadataFieldFlags field, CancellationToken cancellationToken)
        => await GetTrackedFileAsync(filePath, cancellationToken) is { Metadata: { } stored }
           && ((ComicMetadataFieldFlags)stored.UserLockedFieldsMask).HasFlag(field);

    /// <summary>
    /// The file name's issue number when the tags hold the volume's number as the issue: some
    /// taggers write <c>Vol. 2 004 (2023)</c> as Number 2, Volume 2, so every issue of the volume
    /// becomes #2 and all but one are left as duplicates. It is taken as that mistake only when
    /// <c>&lt;Number&gt;</c> equals <c>&lt;Volume&gt;</c> and the file name gives another issue
    /// right after a volume token naming the same volume (see
    /// <see cref="IssueNumberParser.ParseVolumeAndIssue"/>), so a collected edition tagged Number 1,
    /// Volume 1 ("100 Bullets Vol. 1 (2000)") is left alone; and never when the user locked the
    /// issue. Null otherwise: the tags are trusted over the file name.
    /// </summary>
    private async Task<string?> IssueMistakenForVolumeAsync(ComicMetadata metadata, string filePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(metadata.Issue) || !IssueNumberParser.AreEquivalent(metadata.Issue.Trim(), metadata.Volume?.Trim()))
        {
            return null;
        }

        if (IssueNumberParser.ParseVolumeAndIssue(Path.GetFileNameWithoutExtension(filePath)) is not { } named
            || !IssueNumberParser.AreEquivalent(named.Volume, metadata.Volume!.Trim())
            || IssueNumberParser.AreEquivalent(named.Issue, metadata.Issue.Trim()))
        {
            return null;
        }

        return await IsFieldLockedAsync(filePath, ComicMetadataFieldFlags.Issue, cancellationToken) ? null : named.Issue;
    }

    /// <summary>
    /// The history note for a write that succeeded without rewriting the archive, or null.
    /// </summary>
    private static string? DescribeUnwrittenOutcome(MetadataWriteResult write) => write.Outcome switch
    {
        MetadataWriteOutcome.Unchanged => "ComicInfo.xml already up to date",
        MetadataWriteOutcome.KeptInDatabase => "Kept in the database: this CBR/RAR/ZIP is not converted to CBZ",
        _ => null
    };

    /// <summary>
    /// The history note for a normalize that could not be written (see
    /// <see cref="DeferUnwritableNormalizeAsync"/>).
    /// </summary>
    internal const string NormalizeNotWritableMessage =
        "Not normalized: this CBR/RAR/ZIP was left unchanged because converting it to CBZ is turned off or it is a symbolic link";

    /// <summary>
    /// Records a normalize whose result could not be written: the file is a CBR, RAR or ZIP that
    /// is not converted to CBZ (see <see cref="CanConvertToCbz"/>). The file stays marked as not
    /// normalized, so it shows as unprocessed and the next Process or Normalize writes it once
    /// the setting is turned on; nothing is lost by not storing the normalized values, since
    /// they are worked out again then. It is still stamped with the current series-metadata
    /// version, so the library scan's stale-metadata pass does not retry it on every run while
    /// the setting is off.
    /// </summary>
    private async Task DeferUnwritableNormalizeAsync(string filePath, string? normalizedSeries, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Not normalizing {FilePath}: this CBR/RAR/ZIP archive is not converted to CBZ, so it stays unprocessed",
            LoggingHelper.SanitizePathForLog(filePath));
        await _fileStore.MarkFileNormalizedAsync(filePath, false, cancellationToken);
        await StampSeriesMetadataVersionAsync(filePath, normalizedSeries, cancellationToken);
        await LogHistoryAsync(filePath, "Normalize", false, NormalizeNotWritableMessage, cancellationToken);
    }

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
        if (metadata == null || !RecoverMarkedChapter(filePath, metadata))
        {
            return filePath;
        }

        var parsedChapter = metadata.Issue;
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

    /// <summary>
    /// Puts an explicitly marked chapter number in the file name ("Ch 5", "#5") into
    /// <paramref name="metadata"/> when it has no issue number, and a missing title into
    /// "Chapter &lt;n&gt;", so the rename keeps the chapter. Returns true when it changed
    /// <paramref name="metadata"/>. Only the in-memory values change; see
    /// <see cref="EnsureChapterMetadataPreservedAsync"/> for the write.
    /// </summary>
    private static bool RecoverMarkedChapter(string filePath, ComicMetadata metadata)
    {
        if (!string.IsNullOrEmpty(metadata.Issue))
        {
            return false;
        }

        var parsedChapter = IssueNumberParser.ParseMarked(Path.GetFileNameWithoutExtension(filePath));
        if (string.IsNullOrEmpty(parsedChapter))
        {
            return false;
        }

        metadata.Issue = parsedChapter;
        metadata.Title = NormalizedTitleFor(metadata, parsedChapter, titleLocked: true, [metadata.Series]);
        return true;
    }

    /// <summary>
    /// What the normalize phase decides for a file, before anything is written. Processing,
    /// normalizing and the processing preview all work from it, so a preview shows exactly
    /// what the real run writes.
    /// </summary>
    /// <param name="AlreadyNormalized">
    /// True when the file's metadata is already normalized (see <see cref="IsMetadataNormalizedAsync"/>):
    /// only empty elements are filled from the provider.
    /// </param>
    /// <param name="Before">The metadata as read from the file.</param>
    /// <param name="Normalized">The metadata normalize settles on; <paramref name="Before"/> itself when already normalized.</param>
    /// <param name="Fields">The fields normalize writes (see <see cref="NormalizeWriteFields"/>); none when already normalized.</param>
    /// <param name="ProviderFill">Provider details for empty elements, or null (see <see cref="ResolveProviderFillAsync"/>).</param>
    private sealed record NormalizePlan(
        bool AlreadyNormalized,
        ComicMetadata Before,
        ComicMetadata Normalized,
        ComicMetadataFieldFlags Fields,
        ProviderComicInfoFields? ProviderFill);

    /// <summary>
    /// Works out what normalize would write into the file at <paramref name="filePath"/>, whose
    /// metadata is <paramref name="metadata"/>, without writing anything.
    /// </summary>
    /// <param name="mutateCache">
    /// Passed to the series name resolver. A real run lets it record the folder name as an alias
    /// of the matched series; a preview passes false so it changes nothing.
    /// </param>
    private async Task<NormalizePlan> PlanNormalizeAsync(ComicMetadata metadata, string filePath, bool mutateCache, CancellationToken cancellationToken)
    {
        if (await IsMetadataNormalizedAsync(metadata, filePath, mutateCache, cancellationToken))
        {
            var fill = await ResolveProviderFillAsync(filePath, metadata.Series, cancellationToken);
            return new NormalizePlan(true, metadata, metadata, ComicMetadataFieldFlags.None, fill);
        }

        var before = metadata.Clone();
        var normalized = await NormalizeMetadataAsync(metadata, filePath, mutateCache, cancellationToken);
        var providerFill = await ResolveProviderFillAsync(filePath, normalized.Series, cancellationToken);
        return new NormalizePlan(false, before, normalized, NormalizeWriteFields(before, normalized), providerFill);
    }

    ///<summary>
    /// Helper method to consolidate normalization functions
    /// </summary>
    private async Task<ComicMetadata> NormalizeMetadataAsync(ComicMetadata metadata, string filePath, bool mutateCache, CancellationToken cancellationToken)
    {
        var normalizedMetadata = metadata.Clone();

        var normalizedSeries = await ResolveNormalizedSeriesAsync(metadata, filePath, mutateCache, cancellationToken);
        if (!string.IsNullOrEmpty(normalizedSeries) && normalizedMetadata.Series != normalizedSeries)
        {
            _logger.LogDebug("Setting normalized series name: {SeriesName}", LoggingHelper.SanitizeForLog(normalizedSeries));
            normalizedMetadata.Series = normalizedSeries;
        }

        // An issue written as "Annual 1" is stored as "1": the series names the Annual
        // ("Green Lantern Annual", see ResolveNormalizedSeriesAsync), as ComicVine has it.
        if (AnnualIssue.IsAnnualIssueText(normalizedMetadata.Issue))
        {
            normalizedMetadata.Issue = AnnualIssue.IssueNumber(normalizedMetadata.Issue);
        }

        if (await IssueMistakenForVolumeAsync(metadata, filePath, cancellationToken) is { } fromFileName)
        {
            _logger.LogDebug("Replacing an issue number that repeats the volume with the file name's: {Before} -> {After}",
                LoggingHelper.SanitizeForLog(normalizedMetadata.Issue),
                LoggingHelper.SanitizeForLog(fromFileName));
            normalizedMetadata.Issue = fromFileName;
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

        // Every name the series goes by, to recognise an old file name left in the title.
        var seriesNames = await SeriesNamesForTitleAsync(metadata, normalizedSeries, normalizedMetadata.Issue, cancellationToken);

        // A tie-in numbered inside the series keeps its suffix: <Number>1</Number> with a file
        // named "All New X-Men v2 001.MU" is Monsters Unleashed #1.MU, not issue #1.
        if (IssueSuffixToFill(normalizedMetadata, filePath, seriesNames) is { } suffixedIssue)
        {
            _logger.LogDebug("Setting the tie-in's issue number from the file name or title: {Issue}", LoggingHelper.SanitizeForLog(suffixedIssue));
            normalizedMetadata.Issue = suffixedIssue;
        }

        // Keep a real chapter title; fill in a missing one and correct a stale generated one.
        var normalizedTitle = NormalizedTitleFor(normalizedMetadata, normalizedMetadata.Issue, await IsTitleLockedAsync(filePath, cancellationToken), seriesNames);
        if (!string.Equals(normalizedTitle, normalizedMetadata.Title, StringComparison.Ordinal))
        {
            _logger.LogDebug("Setting title to standard format: {Title}", LoggingHelper.SanitizeForLog(normalizedTitle));
            normalizedMetadata.Title = normalizedTitle;
        }

        // Read from the series as the file had it: resolving the series above may have
        // dropped its "(1995)".
        if (VolumeToWrite(metadata, filePath, seriesNames) is { } volume)
        {
            _logger.LogDebug("Setting the volume number from the series, file, title or folder name: {Volume}", LoggingHelper.SanitizeForLog(volume.Number ?? "(none)"));
            normalizedMetadata.Volume = volume.Number;
        }

        if (VolumeYearToFill(metadata, filePath, seriesNames) is { } volumeYear)
        {
            _logger.LogDebug("Storing the volume's start year: {VolumeYear}", volumeYear);
            normalizedMetadata.VolumeYear = volumeYear;
        }

        if (YearToFill(metadata, filePath, seriesNames) is { } year)
        {
            _logger.LogDebug("Setting year from the cover date in the file name or title: {Year}", year);
            normalizedMetadata.Year = year;
        }

        if (FormatToFill(metadata, filePath) is { } format)
        {
            _logger.LogDebug("Setting format: {Format}", format);
            normalizedMetadata.Format = format;
        }

        return normalizedMetadata;
    }

    /// <summary>
    /// The <c>&lt;Format&gt;</c> normalize writes into an empty one, or null: <c>Annual</c>,
    /// <c>Giant-Size</c> or <c>Special</c> for an issue its series, number, file name or title
    /// marks as one (see <see cref="IssueFormat.Of"/>), so every tool that reads
    /// <c>&lt;Format&gt;</c> sees it. A format already set is never replaced.
    /// </summary>
    private static string? FormatToFill(ComicMetadata metadata, string filePath)
        => string.IsNullOrWhiteSpace(metadata.Format)
            ? IssueFormat.Of(null, metadata.Series, metadata.Issue, Path.GetFileName(filePath), metadata.Title)
            : null;

    /// <summary>
    /// The volumes an issue's names give, in the order they are trusted: a year or volume token
    /// at the end of <paramref name="series"/>, then the file name (see
    /// <see cref="ComicVolume.FromFileName"/>), then a file name left in <paramref name="title"/>
    /// (see <see cref="FileNameTitle.IsFileName"/>; a volume runs across its issues, so one naming
    /// a neighbouring issue still gives it), then the folder name. They fill only an unset
    /// <c>&lt;Volume&gt;</c>: a volume the tags hold is trusted over any name. Each source names a volume
    /// number (<c>v2</c>), a start year (<c>(2016)</c>) or nothing.
    /// </summary>
    internal static IEnumerable<ComicVolume> VolumesFromNames(string? series, string? title, string? issue, string filePath)
        => VolumesFromNames(series, title, issue, filePath, [series]);

    /// <summary>
    /// <see cref="VolumesFromNames(string?, string?, string?, string)"/>, with a file name in the
    /// title recognised by any of <paramref name="seriesNames"/> (see <see cref="SeriesNamesForTitleAsync"/>).
    /// </summary>
    private static IEnumerable<ComicVolume> VolumesFromNames(string? series, string? title, string? issue, string filePath, IReadOnlyCollection<string?> seriesNames)
    {
        ComicVolume?[] found =
        [
            ComicVolume.FromSeriesName(series),
            ComicVolume.FromFileName(Path.GetFileName(filePath)),
            FileNameTitle.IsFileName(title, seriesNames, issue) ? ComicVolume.FromFileNameStem(title) : null,
            ComicVolume.FromSeriesName(Path.GetFileName(Path.GetDirectoryName(filePath)))
        ];
        return found.OfType<ComicVolume>();
    }

    /// <summary>
    /// The volume the rename names an issue by when its <c>&lt;Volume&gt;</c> names none: the first
    /// volume number its names give (see <see cref="VolumesFromNames"/>), else the first start year.
    /// </summary>
    internal static ComicVolume? VolumeFromNames(string? series, string? title, string? issue, string filePath)
    {
        var volumes = VolumesFromNames(series, title, issue, filePath).ToList();
        return volumes.Where(volume => !volume.IsYear).Cast<ComicVolume?>().FirstOrDefault()
               ?? volumes.Cast<ComicVolume?>().FirstOrDefault();
    }

    /// <summary>
    /// The cover year normalize writes into an empty <c>&lt;Year&gt;</c>, or null: a bracketed
    /// year after the issue number in the file name (<c>Saga 001 (2012)</c>), else in a file name
    /// left in the title (<c>All New X-Men v2 008 (2016)</c>) that names this very issue (see
    /// <see cref="TitleNamesThisIssue"/>); see <see cref="FileNameTitle.CoverYear"/>.
    /// A year before the issue number is the volume's start year and is never used: #12 of a 1995
    /// volume did not come out in 1995. A year already set is never replaced, except
    /// <see cref="UnknownYear"/>.
    /// </summary>
    private static int? YearToFill(ComicMetadata metadata, string filePath, IReadOnlyCollection<string?> seriesNames)
    {
        if (metadata.Year is > 0 and not UnknownYear)
        {
            return null;
        }

        var cover = FileNameTitle.CoverYear(Path.GetFileNameWithoutExtension(filePath))
                    ?? (TitleNamesThisIssue(metadata, seriesNames)
                        ? FileNameTitle.CoverYear(metadata.Title)
                        : null);
        return cover == metadata.Year ? null : cover;
    }

    /// <summary>
    /// True when <paramref name="metadata"/>'s title is an old file name of this very issue: its
    /// number is <c>&lt;Number&gt;</c> (see <see cref="FileNameTitle.IsLeftoverFileName"/>) and any
    /// volume it names is the one <c>&lt;Volume&gt;</c> holds. A title that disagrees with the tags
    /// is another issue's name (<c>v2 008 (2016)</c> on volume 1 is not this issue's), so nothing
    /// that issue alone has, its cover year or tie-in suffix, is read from it: the tags are trusted.
    /// </summary>
    private static bool TitleNamesThisIssue(ComicMetadata metadata, IReadOnlyCollection<string?> seriesNames)
    {
        if (!FileNameTitle.IsLeftoverFileName(metadata.Title, seriesNames, metadata.Issue))
        {
            return false;
        }

        return ComicVolume.FromTag(metadata.Volume) is not { } tagged
               || IsVolumeUnset(metadata.Volume)
               || ComicVolume.FromFileNameStem(metadata.Title!) is not { } named
               || named.IsYear != tagged.IsYear
               || named.Value == tagged.Value;
    }

    /// <summary>
    /// 1970, the year of the Unix epoch: what some downloaders and taggers write into
    /// <c>&lt;Year&gt;</c> when an issue's date is unknown (a date of 0). Normalize treats it as
    /// unset when the file name gives a cover year, and keeps it otherwise.
    /// </summary>
    private const int UnknownYear = 1970;

    /// <summary>
    /// The issue number with a tie-in's letter suffix ("1.MU", see <see cref="IssueNumberParser"/>)
    /// when <c>&lt;Number&gt;</c> holds only its number ("1") and the file name, or an old file name
    /// left in the title, gives that number with a suffix; otherwise null. A number already
    /// suffixed, or a different number, is never changed.
    /// </summary>
    private static string? IssueSuffixToFill(ComicMetadata metadata, string filePath, IReadOnlyCollection<string?> seriesNames)
    {
        if (string.IsNullOrWhiteSpace(metadata.Issue) || IssueNumberParser.SplitSuffix(metadata.Issue) is not null)
        {
            return null;
        }

        string?[] names =
        [
            Path.GetFileNameWithoutExtension(filePath),
            TitleNamesThisIssue(metadata, seriesNames) ? metadata.Title : null
        ];
        foreach (var name in names)
        {
            if (IssueNumberParser.Parse(name) is { } parsed
                && IssueNumberParser.SplitSuffix(parsed) is { } suffixed
                && IssueNumberParser.AreEquivalent(suffixed.Number, metadata.Issue))
            {
                return parsed;
            }
        }

        return null;
    }

    /// <summary>
    /// The names an old file name left in the title may start with: the series as the file has
    /// it, the name normalize resolves it to, and, from the series cache, its canonical title, the
    /// name the user chose, and its ComicVine and user aliases (<c>ASM</c> for
    /// <c>The Amazing Spider-Man</c>). The cache is read only when the first two do not already
    /// make the title a file name, and a failed read leaves just those two.
    /// </summary>
    private async Task<IReadOnlyCollection<string?>> SeriesNamesForTitleAsync(
        ComicMetadata metadata, string? resolvedSeries, string? issue, CancellationToken cancellationToken)
    {
        var names = new List<string?> { metadata.Series, resolvedSeries };
        if (string.IsNullOrWhiteSpace(metadata.Title) || string.IsNullOrWhiteSpace(issue)
            || FileNameTitle.IsFileName(metadata.Title, names, issue))
        {
            return names;
        }

        foreach (var series in new[] { metadata.Series, resolvedSeries }
                     .Where(name => !string.IsNullOrWhiteSpace(name))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (await _seriesMetadataCache.GetByTitleAsync(series!, cancellationToken) is { } record)
                {
                    names.AddRange([record.CanonicalTitle, record.SeriesName, record.ResolvedSeriesName, .. record.Aliases, .. record.UserAliases]);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                _logger.LogDebug(ex, "Could not read the aliases of series {Series}", LoggingHelper.SanitizeForLog(series));
            }
        }

        return names;
    }

    /// <summary>The <c>&lt;Volume&gt;</c> normalize writes: a volume number, or null to remove the element.</summary>
    private readonly record struct VolumeWrite(string? Number);

    /// <summary>
    /// The <c>&lt;Volume&gt;</c> normalize writes, or null to leave it as it is. <c>&lt;Volume&gt;</c>
    /// holds only the volume number, as Komga and Kavita read it, so:
    /// <list type="bullet">
    /// <item>an empty one, or ComicInfo's "unset" (-1 or 0), gets the first number the issue's names
    /// give (<c>Saga Vol. 2</c>, <c>All New X-Men v2 008</c>; see <see cref="VolumesFromNames"/>);</item>
    /// <item>one holding a start year (<c>1990</c>, <c>(1990)</c>), as ComicTagger and older versions
    /// wrote it, gets that number too, or is removed when no name gives one. The year itself is kept
    /// in the database (see <see cref="VolumeYearToFill"/>);</item>
    /// <item>a number already set is never replaced.</item>
    /// </list>
    /// </summary>
    private static VolumeWrite? VolumeToWrite(ComicMetadata metadata, string filePath, IReadOnlyCollection<string?> seriesNames)
    {
        var holdsYear = ComicVolume.FromTag(metadata.Volume) is { IsYear: true };
        if (!holdsYear && !IsVolumeUnset(metadata.Volume))
        {
            return null;
        }

        var number = VolumesFromNames(metadata.Series, metadata.Title, metadata.Issue, filePath, seriesNames)
            .Where(volume => !volume.IsYear)
            .Select(volume => volume.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .FirstOrDefault();
        if (number is not null)
        {
            return new VolumeWrite(number);
        }

        return holdsYear ? new VolumeWrite(null) : null;
    }

    /// <summary>
    /// The year the issue's volume started, which normalize stores in the database when none is
    /// stored (see <see cref="ComicMetadata.VolumeYear"/>), or null. A series named for its volume,
    /// as <c>The Astonishing X-Men (1995)</c> (volume 1) and <c>(1999)</c> (volume 2), gives that
    /// year. It is read from a <c>&lt;Volume&gt;</c> that holds a year, as ComicTagger can write it,
    /// then the issue's names (see <see cref="VolumesFromNames"/>). Without it the year would be
    /// lost once normalize replaces the series with its resolved name. The start year never goes
    /// into <c>&lt;Year&gt;</c>: #12 of a 1995 volume did not come out in 1995 (see <see cref="YearToFill"/>).
    /// </summary>
    private static int? VolumeYearToFill(ComicMetadata metadata, string filePath, IReadOnlyCollection<string?> seriesNames)
    {
        if (metadata.VolumeYear is not null)
        {
            return null;
        }

        if (ComicVolume.FromTag(metadata.Volume) is { IsYear: true } tagged)
        {
            return tagged.Value;
        }

        return VolumesFromNames(metadata.Series, metadata.Title, metadata.Issue, filePath, seriesNames)
            .Where(volume => volume.IsYear)
            .Select(volume => (int?)volume.Value)
            .FirstOrDefault();
    }

    /// <summary>True for a blank <c>&lt;Volume&gt;</c> or ComicInfo's "unset" (-1 or 0).</summary>
    private static bool IsVolumeUnset(string? volume)
    {
        var current = volume?.Trim();
        return string.IsNullOrEmpty(current)
               || (int.TryParse(current, System.Globalization.NumberStyles.AllowLeadingSign,
                       System.Globalization.CultureInfo.InvariantCulture, out var number) && number <= 0);
    }

    /// <summary>
    /// The ComicInfo.xml elements a normalize write changes: the ones normalize owns, plus
    /// <c>&lt;Volume&gt;</c>, <c>&lt;Year&gt;</c> and <c>&lt;Format&gt;</c> when normalize filled them
    /// (see <see cref="VolumeToWrite"/>, <see cref="YearToFill"/> and <see cref="FormatToFill"/>), so
    /// a file whose volume, year and format were not touched keeps those elements exactly as they were.
    /// </summary>
    private static ComicMetadataFieldFlags NormalizeWriteFields(ComicMetadata before, ComicMetadata normalized)
    {
        var fields = NormalizedFields;
        if (!string.Equals(before.Volume, normalized.Volume, StringComparison.Ordinal))
        {
            fields |= ComicMetadataFieldFlags.Volume;
        }

        if (before.Year != normalized.Year)
        {
            fields |= ComicMetadataFieldFlags.Year;
        }

        if (!string.Equals(before.Format, normalized.Format, StringComparison.Ordinal))
        {
            fields |= ComicMetadataFieldFlags.Format;
        }

        // Stored in the database only (see UpdateMetadataCoreAsync).
        if (before.VolumeYear != normalized.VolumeYear)
        {
            fields |= ComicMetadataFieldFlags.VolumeYear;
        }

        return fields;
    }
}
