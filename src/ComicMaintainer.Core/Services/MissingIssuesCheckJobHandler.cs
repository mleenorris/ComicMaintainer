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
        "Scans the library for series with gaps in their issue numbering (e.g. it has #1, #2 and #4 but not #3). The check is report-only unless autoDownload is enabled, in which case the missing issues are queued on the configured Suwayomi server.";

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

        var seriesWithGaps = new List<(string Title, List<int> Missing)>();
        var totalMissing = 0;
        var queuedChapters = 0;
        var downloadFailures = 0;
        var downloadEnabled = options.AutoDownload && _suwayomi.IsConfigured;

        foreach (var series in candidates.Series)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var issues = await _seriesLibrary.GetSeriesIssuesAsync(
                series.Id,
                page: 1,
                perPage: -1,
                cancellationToken: cancellationToken);
            if (issues is null)
            {
                continue;
            }

            var missing = ComputeMissingIssueNumbers(issues.Issues.Select(i => i.Issue));
            if (missing.Count == 0)
            {
                continue;
            }

            var title = FirstNonBlank(series.Title, issues.Title, series.CanonicalTitle) ?? series.Id;
            seriesWithGaps.Add((title, missing));
            totalMissing += missing.Count;

            _logger.LogInformation(
                "Missing issues check: {Title} is missing {Count} issue(s)",
                LoggingHelper.SanitizeForLog(title),
                missing.Count);

            if (!downloadEnabled)
            {
                continue;
            }

            var alternateTitles = new[] { issues.Title, issues.CanonicalTitle, series.CanonicalTitle }
                .Concat(issues.Aliases)
                .Concat(series.Aliases)
                .Where(t => !string.IsNullOrWhiteSpace(t) && !string.Equals(t, title, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            try
            {
                var result = await _suwayomi.DownloadMissingIssuesAsync(
                    title,
                    missing,
                    seriesId: null,
                    alternateTitles: alternateTitles,
                    cancellationToken: cancellationToken);
                queuedChapters += result.QueuedChapters.Count;
            }
            catch (SuwayomiException ex)
            {
                // A series Suwayomi doesn't know about (or a transient server
                // problem) must not abort the check for the rest of the library.
                downloadFailures++;
                _logger.LogWarning(
                    "Missing issues check: Suwayomi download for {Title} failed: {Message}",
                    LoggingHelper.SanitizeForLog(title),
                    LoggingHelper.SanitizeForLog(ex.Message));
            }
        }

        return BuildSummary(candidates.Series.Count, seriesWithGaps, totalMissing, options.AutoDownload,
            downloadEnabled, queuedChapters, downloadFailures);
    }

    /// <summary>
    /// Returns the whole issue numbers missing between #1 and the highest
    /// whole issue number present. Mirrors the series-detail view's
    /// <c>computeMissingIssueNumbers</c> so the scheduled check reports (and
    /// downloads) exactly the placeholders the user sees: only plain
    /// non-negative integers count as present; decimals ("1.5") and labels
    /// ("Annual") are ignored.
    /// </summary>
    public static List<int> ComputeMissingIssueNumbers(IEnumerable<string?> issueNumbers)
    {
        var present = new HashSet<int>();
        var max = 0;
        foreach (var raw in issueNumbers)
        {
            var text = raw?.Trim();
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

        var missing = new List<int>();
        for (var n = 1; n <= max; n++)
        {
            if (!present.Contains(n))
            {
                missing.Add(n);
            }
        }
        return missing;
    }

    private static string BuildSummary(
        int checkedSeries,
        List<(string Title, List<int> Missing)> seriesWithGaps,
        int totalMissing,
        bool autoDownloadRequested,
        bool downloadEnabled,
        int queuedChapters,
        int downloadFailures)
    {
        if (seriesWithGaps.Count == 0)
        {
            return checkedSeries == 0
                ? "No series are missing issues."
                : $"Checked {checkedSeries} series: none are missing issues.";
        }

        var named = string.Join("; ", seriesWithGaps
            .Take(MaxSeriesNamedInSummary)
            .Select(s => $"{s.Title} ({s.Missing.Count})"));
        if (seriesWithGaps.Count > MaxSeriesNamedInSummary)
        {
            named += $"; +{seriesWithGaps.Count - MaxSeriesNamedInSummary} more";
        }

        var summary = $"{seriesWithGaps.Count} series missing {totalMissing} issue(s): {named}.";
        if (downloadEnabled)
        {
            summary += $" Queued {queuedChapters} chapter(s) on Suwayomi";
            summary += downloadFailures > 0 ? $"; {downloadFailures} series could not be downloaded." : ".";
        }
        else if (autoDownloadRequested)
        {
            summary += " autoDownload is enabled but the Suwayomi integration is not configured.";
        }
        return summary;
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
