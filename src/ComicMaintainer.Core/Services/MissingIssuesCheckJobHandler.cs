using System.Text.Json;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Scheduled-job handler that periodically checks the library for series with
/// gaps in their whole-number issue numbering (the same gaps the series-detail
/// view shows as missing-issue placeholders). By default the check only
/// reports what is missing. When the <c>autoDownload</c> option is enabled and
/// the Suwayomi integration is configured, the missing issues of each series
/// are queued on the Suwayomi server, exactly as the series-detail
/// "Download from Suwayomi" action does.
/// </summary>
public class MissingIssuesCheckJobHandler : IScheduledJobHandler
{
    public const string Key = "missing-issues-check";

    // Series-level library filter that keeps only series with numbering gaps.
    private const string MissingIssuesFilter = "missing";

    // Cap on how many series are named in the last-run summary.
    private const int MaxSeriesNamedInSummary = 10;

    // Avoid expanding malformed metadata into an unbounded missing-issue list.
    private const int MaxIssueNumberRange = 100_000;

    private readonly ISeriesLibraryService _seriesLibrary;
    private readonly ISuwayomiService _suwayomi;
    private readonly ILogger<MissingIssuesCheckJobHandler> _logger;

    public MissingIssuesCheckJobHandler(
        ISeriesLibraryService seriesLibrary,
        ISuwayomiService suwayomi,
        ILogger<MissingIssuesCheckJobHandler> logger)
    {
        _seriesLibrary = seriesLibrary;
        _suwayomi = suwayomi;
        _logger = logger;
    }

    public string JobKey => Key;
    public string DisplayName => "Missing Issues Check";
    public string Description =>
        "Scans the library for series with gaps in their issue numbering (e.g. it has #1, #2 and #4 but not #3). The check is report-only unless autoDownload is enabled, in which case matching chapters are sought across the configured Suwayomi server's installed sources and queued.";

    // Default: disabled (so it doesn't run unprompted on existing installs) and daily when enabled.
    public ScheduledJobDefaults Defaults =>
        new(Enabled: false, IntervalMinutes: 60 * 24, OptionsJson: """{"autoDownload":false}""");

    public async Task<string> ExecuteAsync(string? optionsJson, CancellationToken cancellationToken)
    {
        var options = ParseOptions(optionsJson);

        var candidates = await _seriesLibrary.GetSeriesSummariesAsync(
            filter: MissingIssuesFilter,
            page: 1,
            perPage: -1,
            cancellationToken: cancellationToken);

        var issuesById = candidates.Series.Count == 0
            ? new Dictionary<string, SeriesIssuesResult>(StringComparer.Ordinal)
            : await _seriesLibrary.GetSeriesIssuesForIdsAsync(
                candidates.Series.Select(series => series.Id),
                cancellationToken);
        var seriesWithGaps = new List<(string Title, List<int> Missing, List<string> AlternateTitles)>();
        long totalMissing = 0;
        var skippedOversizedSeries = 0;
        var queuedChapters = 0;
        var downloadFailures = 0;
        var sourceCheckFailures = 0;
        var downloadEnabled = options.AutoDownload && _suwayomi.IsConfigured;

        foreach (var series in candidates.Series)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!issuesById.TryGetValue(series.Id, out var issues))
            {
                continue;
            }

            if (!TryComputeMissingIssueNumbers(
                    issues.Issues.Select(i => i.Issue), cancellationToken, out var missing))
            {
                skippedOversizedSeries++;
                _logger.LogWarning(
                    "Missing issues check: skipping {Title} because its issue range exceeds {Maximum}",
                    LoggingHelper.SanitizeForLog(series.Title),
                    MaxIssueNumberRange);
                continue;
            }

            if (missing.Count == 0)
            {
                continue;
            }

            var title = FirstNonBlank(series.Title, issues.Title, series.CanonicalTitle) ?? series.Id;
            var alternateTitles = new[] { issues.Title, issues.CanonicalTitle, series.CanonicalTitle }
                .Concat(issues.Aliases)
                .Concat(series.Aliases)
                .Where(t => !string.IsNullOrWhiteSpace(t) && !string.Equals(t, title, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            seriesWithGaps.Add((title, missing, alternateTitles));
            totalMissing += missing.Count;

            _logger.LogInformation(
                "Missing issues check: {Title} is missing {Count} issue(s)",
                LoggingHelper.SanitizeForLog(title),
                missing.Count);
        }

        if (downloadEnabled && seriesWithGaps.Count > 0)
        {
            SuwayomiLibrarySnapshot? librarySnapshot = null;
            try
            {
                librarySnapshot = await _suwayomi.GetLibrarySeriesAsync(cancellationToken);
            }
            catch (SuwayomiException ex)
            {
                downloadFailures = seriesWithGaps.Count;
                _logger.LogWarning(
                    "Missing issues check: could not load the Suwayomi library: {Message}",
                    LoggingHelper.SanitizeForLog(ex.Message));
            }

            if (librarySnapshot is not null)
            {
                foreach (var series in seriesWithGaps)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var result = await _suwayomi.DownloadMissingIssuesFromSnapshotAsync(
                            series.Title,
                            series.Missing,
                            librarySnapshot,
                            alternateTitles: series.AlternateTitles,
                            cancellationToken: cancellationToken);
                        queuedChapters += result.QueuedChapters.Count;
                        sourceCheckFailures += result.DiscoveryFailures.Count;
                    }
                    catch (SuwayomiException ex)
                    {
                        // A series Suwayomi doesn't know about (or a transient server
                        // problem) must not abort the check for the rest of the library.
                        downloadFailures++;
                        _logger.LogWarning(
                            "Missing issues check: Suwayomi download for {Title} failed: {Message}",
                            LoggingHelper.SanitizeForLog(series.Title),
                            LoggingHelper.SanitizeForLog(ex.Message));
                    }
                }
            }
        }

        return BuildSummary(
            candidates.Series.Count,
            seriesWithGaps,
            totalMissing,
            skippedOversizedSeries,
            options.AutoDownload,
            downloadEnabled,
            queuedChapters,
            downloadFailures,
            sourceCheckFailures);
    }

    /// <summary>
    /// Returns the whole issue numbers missing between #1 and the highest
    /// whole issue number present. Mirrors the series-detail view's
    /// <c>computeMissingIssueNumbers</c> so the scheduled check reports (and
    /// downloads) exactly the placeholders the user sees: only plain
    /// non-negative integers count as present; decimals ("1.5") and labels
    /// ("Annual") are ignored.
    /// </summary>
    public static List<int> ComputeMissingIssueNumbers(
        IEnumerable<string?> issueNumbers,
        CancellationToken cancellationToken = default)
    {
        if (!TryComputeMissingIssueNumbers(issueNumbers, cancellationToken, out var missing))
        {
            throw new ArgumentOutOfRangeException(
                nameof(issueNumbers), $"Issue numbers must not exceed {MaxIssueNumberRange}.");
        }

        return missing;
    }

    private static bool TryComputeMissingIssueNumbers(
        IEnumerable<string?> issueNumbers,
        CancellationToken cancellationToken,
        out List<int> missing)
    {
        ArgumentNullException.ThrowIfNull(issueNumbers);

        var present = new HashSet<int>();
        var max = 0;
        foreach (var text in issueNumbers.Select(raw => raw?.Trim()))
        {
            if (string.IsNullOrEmpty(text) || !text.All(char.IsAsciiDigit))
            {
                continue;
            }
            if (!int.TryParse(text, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var n))
            {
                continue;
            }
            present.Add(n);
            if (n > max) max = n;
        }

        if (max > MaxIssueNumberRange)
        {
            missing = new List<int>();
            return false;
        }

        missing = new List<int>();
        for (var n = 1; n <= max; n++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!present.Contains(n))
            {
                missing.Add(n);
            }
        }
        return true;
    }

    private static string BuildSummary(
        int checkedSeries,
        List<(string Title, List<int> Missing, List<string> AlternateTitles)> seriesWithGaps,
        long totalMissing,
        int skippedOversizedSeries,
        bool autoDownloadRequested,
        bool downloadEnabled,
        int queuedChapters,
        int downloadFailures,
        int sourceCheckFailures)
    {
        var outcomes = new List<string>();
        if (downloadEnabled && seriesWithGaps.Count > 0)
        {
            outcomes.Add($"Queued {queuedChapters} chapter(s) on Suwayomi");
            if (downloadFailures > 0)
            {
                outcomes.Add($"{downloadFailures} series could not be downloaded");
            }
            if (sourceCheckFailures > 0)
            {
                outcomes.Add($"{sourceCheckFailures} Suwayomi source check(s) failed");
            }
        }
        else if (autoDownloadRequested && !downloadEnabled)
        {
            outcomes.Add("autoDownload is enabled but the Suwayomi integration is not configured");
        }

        if (skippedOversizedSeries > 0)
        {
            outcomes.Add($"Skipped {skippedOversizedSeries} series with issue ranges over {MaxIssueNumberRange}");
        }

        var outcomeText = outcomes.Count == 0 ? string.Empty : string.Join("; ", outcomes) + ". ";
        if (seriesWithGaps.Count == 0)
        {
            var result = skippedOversizedSeries > 0
                ? $"Checked {checkedSeries} series; no other series are missing issues."
                : checkedSeries == 0
                    ? "No series are missing issues."
                    : $"Checked {checkedSeries} series: none are missing issues.";
            return outcomeText + result;
        }

        var named = string.Join("; ", seriesWithGaps
            .Take(MaxSeriesNamedInSummary)
            .Select(s => $"{s.Title} ({s.Missing.Count})"));
        if (seriesWithGaps.Count > MaxSeriesNamedInSummary)
        {
            named += $"; +{seriesWithGaps.Count - MaxSeriesNamedInSummary} more";
        }

        return $"{outcomeText}{seriesWithGaps.Count} series missing {totalMissing} issue(s): {named}.";
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static MissingIssuesCheckOptions ParseOptions(string? optionsJson)
    {
        if (string.IsNullOrWhiteSpace(optionsJson))
        {
            return new MissingIssuesCheckOptions();
        }
        try
        {
            return JsonSerializer.Deserialize<MissingIssuesCheckOptions>(
                       optionsJson,
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? new MissingIssuesCheckOptions();
        }
        catch (JsonException)
        {
            return new MissingIssuesCheckOptions();
        }
    }

    private sealed class MissingIssuesCheckOptions
    {
        public bool AutoDownload { get; set; }
    }
}
