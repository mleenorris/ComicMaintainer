using System.Text.Json;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Scheduled-job handler that walks every tracked comic file, reads its
/// embedded ComicInfo metadata, and records a <see cref="MetadataAuditFindingEntity"/>
/// row for any file with a missing chapter number or a series tag that
/// disagrees with the resolved expected series.
/// </summary>
public class MetadataAuditJobHandler : IScheduledJobHandler
{
    public const string Key = "metadata-audit";

    // Poll the spawned normalize batch job at this cadence while waiting for completion
    // during an autoCorrect run, mirroring FileNamingAuditJobHandler's pattern.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly IFileStoreService _fileStore;
    private readonly IComicProcessorService _processor;
    private readonly IDbContextFactory<ComicMaintainerDbContext> _dbContextFactory;
    private readonly ISeriesMetadataCacheService? _seriesCache;
    private readonly ISeriesNameResolver? _seriesNameResolver;
    private readonly IOptionsMonitor<AppSettings> _appSettings;
    private readonly ILogger<MetadataAuditJobHandler> _logger;

    public MetadataAuditJobHandler(
        IFileStoreService fileStore,
        IComicProcessorService processor,
        IDbContextFactory<ComicMaintainerDbContext> dbContextFactory,
        IOptionsMonitor<AppSettings> appSettings,
        ILogger<MetadataAuditJobHandler> logger,
        ISeriesMetadataCacheService? seriesCache = null,
        ISeriesNameResolver? seriesNameResolver = null)
    {
        _fileStore = fileStore;
        _processor = processor;
        _dbContextFactory = dbContextFactory;
        _appSettings = appSettings;
        _logger = logger;
        _seriesCache = seriesCache;
        _seriesNameResolver = seriesNameResolver;
    }

    public string JobKey => Key;
    public string DisplayName => "Metadata Audit";
    public string Description =>
        "Scans every tracked comic file and reports any with a missing chapter/issue number or a series tag that doesn't match the expected series name.";

    // Default: disabled (so it doesn't run unprompted on existing installs) and weekly when enabled.
    public ScheduledJobDefaults Defaults =>
        new(Enabled: false, IntervalMinutes: 60 * 24 * 7, OptionsJson: """{"autoCorrect":false}""");

    public async Task<string> ExecuteAsync(string? optionsJson, CancellationToken cancellationToken)
    {
        var options = ParseOptions(optionsJson);

        var files = (await _fileStore.GetAllFilesAsync(cancellationToken)).ToList();
        _logger.LogInformation("Metadata audit starting for {Count} file(s)", files.Count);

        var findings = new List<MetadataAuditFindingEntity>();
        var globalPreferred = _appSettings.CurrentValue.DefaultPreferredLanguage;
        var seriesMismatch = 0;
        var missingChapter = 0;
        var unreadable = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ComicMetadata? metadata;
            try
            {
                metadata = await _processor.GetMetadataAsync(file.FilePath, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read metadata for {Path}", file.FilePath);
                metadata = null;
            }

            if (metadata is null)
            {
                unreadable++;
                findings.Add(new MetadataAuditFindingEntity
                {
                    FilePath = file.FilePath,
                    FindingType = MetadataAuditFindingType.Unreadable.ToString(),
                    Details = "Metadata could not be read.",
                });
                continue;
            }

            // Check 1: Issue / chapter number must be set.
            if (string.IsNullOrWhiteSpace(metadata.Issue))
            {
                missingChapter++;
                findings.Add(new MetadataAuditFindingEntity
                {
                    FilePath = file.FilePath,
                    FindingType = MetadataAuditFindingType.MissingChapter.ToString(),
                    ActualSeries = metadata.Series,
                    ActualIssue = null,
                    Details = "ComicInfo <Number> tag is empty.",
                });
                await AddDiskDriftFindingIfNeededAsync(file.FilePath, metadata, findings, cancellationToken);
                continue;
            }

            // Check 2: Series tag must match the expected resolved name.
            var (expected, explanation) = await ResolveExpectedSeriesWithExplanationAsync(file.FilePath, metadata, globalPreferred, cancellationToken);
            if (!string.IsNullOrWhiteSpace(expected)
                && !string.IsNullOrWhiteSpace(metadata.Series)
                && !SeriesMatchesExpected(metadata.Series, expected))
            {
                seriesMismatch++;
                var details = $"Series '{metadata.Series}' does not match expected '{expected}'.";
                if (!string.IsNullOrWhiteSpace(explanation))
                {
                    details += " " + explanation;
                }
                // Cap to the EF-mapped column length so a long resolver
                // explanation doesn't cause a SaveChanges failure.
                if (details.Length > 1024) details = details.Substring(0, 1024);
                findings.Add(new MetadataAuditFindingEntity
                {
                    FilePath = file.FilePath,
                    FindingType = MetadataAuditFindingType.SeriesMismatch.ToString(),
                    ActualSeries = metadata.Series,
                    ExpectedSeries = expected,
                    ActualIssue = metadata.Issue,
                    Details = details,
                });
            }

            await AddDiskDriftFindingIfNeededAsync(file.FilePath, metadata, findings, cancellationToken);
        }

        // Persist findings: replace prior findings with the freshly computed set so the UI
        // always reflects the latest scan rather than accumulating stale rows.
        await using (var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            await db.MetadataAuditFindings.ExecuteDeleteAsync(cancellationToken);
            if (findings.Count > 0)
            {
                db.MetadataAuditFindings.AddRange(findings);
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        // Optional auto-correct: re-normalize the offending files via the existing pipeline.
        // We wait for the spawned normalize batch to reach a terminal state so the
        // scheduled job's last-run status (and summary) reflect the actual outcome
        // rather than just queue submission. Without this wait the audit would report
        // success the instant the job was queued, before any ComicInfo.xml was rewritten.
        var correctedSummary = string.Empty;
        if (options.AutoCorrect)
        {
            var toFix = findings
                .Where(f => f.FindingType == MetadataAuditFindingType.SeriesMismatch.ToString()
                            || f.FindingType == MetadataAuditFindingType.MissingChapter.ToString())
                .Select(f => f.FilePath)
                .Distinct()
                .ToList();
            if (toFix.Count > 0)
            {
                _logger.LogInformation("Metadata audit auto-correct: queueing {Count} file(s) for normalization", toFix.Count);
                var jobId = await _processor.NormalizeFilesAsync(toFix, forceReprocess: true, cancellationToken);

                ProcessingJob? job = null;
                while (!cancellationToken.IsCancellationRequested)
                {
                    job = _processor.GetJob(jobId);
                    if (job is null)
                    {
                        // Job record was evicted before we observed completion; treat as success
                        // since the normalize pipeline only persists state on success.
                        break;
                    }
                    if (job.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled)
                    {
                        break;
                    }
                    await Task.Delay(PollInterval, cancellationToken);
                }

                cancellationToken.ThrowIfCancellationRequested();

                correctedSummary = job is null
                    ? $" Auto-correct queued {toFix.Count} file(s); job record no longer available."
                    : $" Auto-correct: {job.ProcessedFiles}/{toFix.Count} normalized, {job.FailedFiles} failed (status: {job.Status}).";
            }
            else
            {
                correctedSummary = " Auto-correct: nothing to fix.";
            }
        }

        return $"Scanned {files.Count}: {seriesMismatch} series mismatch, {missingChapter} missing chapter, {unreadable} unreadable.{correctedSummary}";
    }


    private async Task AddDiskDriftFindingIfNeededAsync(
        string filePath,
        ComicMetadata diskMetadata,
        List<MetadataAuditFindingEntity> findings,
        CancellationToken cancellationToken)
    {
        var row = await _fileStore.GetFileAsync(filePath, cancellationToken);
        if (row?.Metadata?.IsUserEdited != true)
        {
            return;
        }

        var dbMetadata = row.Metadata;
        var flags = (ComicMetadataFieldFlags)dbMetadata.UserLockedFieldsMask;
        if (flags == ComicMetadataFieldFlags.None)
        {
            return;
        }

        if (!LockedFieldsDiffer(flags, diskMetadata, dbMetadata))
        {
            return;
        }

        findings.Add(new MetadataAuditFindingEntity
        {
            FilePath = filePath,
            FindingType = MetadataAuditFindingType.DiskDriftedFromDb.ToString(),
            ActualSeries = diskMetadata.Series,
            ExpectedSeries = dbMetadata.Series,
            ActualIssue = diskMetadata.Issue,
            Details = "Disk metadata diverges from user-edited DB record; backfill will rewrite.",
        });
    }

    private static bool LockedFieldsDiffer(ComicMetadataFieldFlags flags, ComicMetadata disk, ComicMetadata db)
    {
        if (flags.HasFlag(ComicMetadataFieldFlags.Series) && !StringEquals(disk.Series, db.Series)) return true;
        if (flags.HasFlag(ComicMetadataFieldFlags.Title) && !StringEquals(disk.Title, db.Title)) return true;
        if (flags.HasFlag(ComicMetadataFieldFlags.Issue) && !StringEquals(disk.Issue, db.Issue)) return true;
        if (flags.HasFlag(ComicMetadataFieldFlags.Volume) && !StringEquals(disk.Volume, db.Volume)) return true;
        if (flags.HasFlag(ComicMetadataFieldFlags.Publisher) && !StringEquals(disk.Publisher, db.Publisher)) return true;
        if (flags.HasFlag(ComicMetadataFieldFlags.Year) && disk.Year != db.Year) return true;
        if (flags.HasFlag(ComicMetadataFieldFlags.Summary) && !StringEquals(disk.Summary, db.Summary)) return true;
        if (flags.HasFlag(ComicMetadataFieldFlags.Authors) && !ListEquals(disk.Authors, db.Authors)) return true;
        if (flags.HasFlag(ComicMetadataFieldFlags.Tags) && !ListEquals(disk.Tags, db.Tags)) return true;
        return false;
    }

    private static bool StringEquals(string? left, string? right)
        => string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.Ordinal);

    private static bool ListEquals(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
        => (left ?? Array.Empty<string>()).SequenceEqual(right ?? Array.Empty<string>(), StringComparer.Ordinal);

    /// <summary>
    /// Backward-compatible thin wrapper around
    /// <see cref="ResolveExpectedSeriesWithExplanationAsync"/> that discards the
    /// explanation. Retained because external callers (and tests) reference
    /// this signature; new code should prefer the version that also returns
    /// the explanation so it can be surfaced to the user.
    /// </summary>
    private async Task<string?> ResolveExpectedSeriesAsync(
        string filePath,
        string? globalPreferredLanguage,
        CancellationToken cancellationToken)
    {
        var (expected, _) = await ResolveExpectedSeriesWithExplanationAsync(filePath, metadata: null, globalPreferredLanguage, cancellationToken);
        return expected;
    }

    /// <summary>
    /// Compute the expected series name for an audited file along with a
    /// short explanation of which resolution step produced it. Prefers the
    /// shared <see cref="ISeriesNameResolver"/> when available so the audit
    /// uses the exact same priority chain as the normalize pipeline; falls
    /// back to the legacy folder-name + cache-lookup logic when the resolver
    /// isn't registered.
    /// </summary>
    private async Task<(string? Expected, string? Explanation)> ResolveExpectedSeriesWithExplanationAsync(
        string filePath,
        ComicMetadata? metadata,
        string? globalPreferredLanguage,
        CancellationToken cancellationToken)
    {
        if (_seriesNameResolver is not null)
        {
            // mutateCache:false so the audit doesn't side-effect the alias
            // index just by looking at files.
            var resolution = await _seriesNameResolver.ResolveAsync(filePath, metadata, mutateCache: false, cancellationToken);
            return (resolution.ResolvedSeries, resolution.Explanation);
        }

        // Legacy fallback used only when no ISeriesNameResolver is registered.
        var folderName = Path.GetFileName(Path.GetDirectoryName(filePath));
        if (string.IsNullOrWhiteSpace(folderName)) return (null, null);
        var folderSeries = ComicFileProcessor.NormalizeSeriesName(folderName, forComparison: false);
        if (string.IsNullOrWhiteSpace(folderSeries)) return (null, null);

        if (_seriesCache is not null)
        {
            try
            {
                var key = _seriesCache.NormalizeKey(folderSeries);
                var record = string.IsNullOrEmpty(key) ? null : await _seriesCache.GetAsync(key, cancellationToken);
                if (record is not null && !string.IsNullOrWhiteSpace(record.CanonicalTitle))
                {
                    var resolved = SeriesDisplayTitleResolver.Resolve(record, globalPreferredLanguage);
                    if (!string.IsNullOrWhiteSpace(resolved)) return (resolved.Trim(), null);
                    return (record.CanonicalTitle.Trim(), null);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Series cache lookup failed for '{Folder}'", folderSeries);
            }
        }

        return (folderSeries, null);
    }

    /// <summary>
    /// Determine whether the file's actual series tag matches the expected
    /// resolved series. Applies the same comparison-time normalization that the
    /// rest of the site uses (<see cref="ComicFileProcessor.NormalizeSeriesName"/>
    /// with <c>forComparison: true</c>, which strips <c>(*)</c>/<c>[*]</c>
    /// suffixes, normalizes apostrophes, and trims) so the audit doesn't flag
    /// files the normalize pipeline already considers correct.
    /// </summary>
    private static bool SeriesMatchesExpected(string actualSeries, string expectedSeries)
    {
        var actual = ComicFileProcessor.NormalizeSeriesName(actualSeries, forComparison: true);
        var expected = ComicFileProcessor.NormalizeSeriesName(expectedSeries, forComparison: true);
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static MetadataAuditOptions ParseOptions(string? optionsJson)
    {
        if (string.IsNullOrWhiteSpace(optionsJson))
        {
            return new MetadataAuditOptions();
        }
        try
        {
            return JsonSerializer.Deserialize<MetadataAuditOptions>(
                       optionsJson,
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? new MetadataAuditOptions();
        }
        catch
        {
            return new MetadataAuditOptions();
        }
    }

    private sealed class MetadataAuditOptions
    {
        public bool AutoCorrect { get; set; }
    }
}
