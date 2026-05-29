using System.Text.Json;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Scheduled-job handler that checks each cached series record's currently
/// applied <c>&lt;Series&gt;</c> tag (across its tracked files) against the
/// expected display title for the configured preferred-language preference
/// (per-series <see cref="SeriesMetadataCacheRecord.PreferredLanguage"/> or
/// the global default). When the option <c>autoCorrect</c> is true, mismatched
/// series are queued for a forced retag through
/// <see cref="ISeriesLanguagePreferenceRetagService"/>; otherwise the job
/// only logs the mismatches and reports a count.
/// </summary>
public class SeriesLanguageAuditJobHandler : IScheduledJobHandler
{
    public const string Key = "series-language-audit";

    private readonly ISeriesMetadataCacheService _cache;
    private readonly IFileStoreService _fileStore;
    private readonly ISeriesLanguagePreferenceRetagService _retag;
    private readonly IOptionsMonitor<AppSettings> _appSettings;
    private readonly ILogger<SeriesLanguageAuditJobHandler> _logger;
    private readonly ISeriesNameResolver? _seriesNameResolver;

    public SeriesLanguageAuditJobHandler(
        ISeriesMetadataCacheService cache,
        IFileStoreService fileStore,
        ISeriesLanguagePreferenceRetagService retag,
        IOptionsMonitor<AppSettings> appSettings,
        ILogger<SeriesLanguageAuditJobHandler> logger,
        ISeriesNameResolver? seriesNameResolver = null)
    {
        _cache = cache;
        _fileStore = fileStore;
        _retag = retag;
        _appSettings = appSettings;
        _logger = logger;
        _seriesNameResolver = seriesNameResolver;
    }

    public string JobKey => Key;
    public string DisplayName => "Series Language Audit";
    public string Description =>
        "Checks each series' currently applied <Series> metadata against the expected name for the configured preferred-language preference (per-series or global default). Enable autoCorrect in options to queue mismatched series for a forced retag.";

    // Default: disabled (so existing installs don't auto-run it) and daily when enabled.
    public ScheduledJobDefaults Defaults =>
        new(Enabled: false, IntervalMinutes: 60 * 24, OptionsJson: """{"autoCorrect":false}""");

    public async Task<string> ExecuteAsync(string? optionsJson, CancellationToken cancellationToken)
    {
        var options = ParseOptions(optionsJson);
        var globalDefault = _appSettings.CurrentValue.DefaultPreferredLanguage;

        var records = await _cache.GetAllAsync(cancellationToken);
        var allFiles = (await _fileStore.GetAllFilesAsync(cancellationToken)).ToList();

        // Build a quick lookup from current file Metadata.Series -> file paths.
        // Series tags are matched case-insensitively (mirrors how the retag
        // service collects matching titles).
        var filesBySeries = new Dictionary<string, List<ComicFile>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in allFiles)
        {
            var seriesTag = file.Metadata?.Series;
            if (string.IsNullOrWhiteSpace(seriesTag))
            {
                continue;
            }
            if (!filesBySeries.TryGetValue(seriesTag, out var bucket))
            {
                bucket = new List<ComicFile>();
                filesBySeries[seriesTag] = bucket;
            }
            bucket.Add(file);
        }

        int seriesScanned = 0;
        int seriesWithMismatch = 0;
        int filesWithMismatch = 0;
        int seriesQueued = 0;

        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            seriesScanned++;

            // Route through ISeriesNameResolver when registered so the
            // language-audit's "expected" value matches the string the
            // normalize pipeline writes into ComicInfo.xml — the audit
            // exists to catch drift between cache state and on-disk
            // metadata, so it must use the same resolver as the writer.
            var expected = _seriesNameResolver is not null
                ? _seriesNameResolver.ResolveForRecord(record)
                : SeriesDisplayTitleResolver.Resolve(record, globalDefault);
            if (string.IsNullOrWhiteSpace(expected))
            {
                continue;
            }

            // Collect titles that identify files belonging to this series. We
            // intentionally include every alias / localized title (same set
            // used by SeriesLanguagePreferenceRetagService) so we can find
            // files whose <Series> has previously been written in another
            // language and now disagrees with the current preference.
            var titles = CollectTitlesForRecord(record);
            if (titles.Count == 0)
            {
                continue;
            }

            int recordMismatchFiles = 0;
            foreach (var title in titles)
            {
                if (string.Equals(title, expected, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (filesBySeries.TryGetValue(title, out var bucket))
                {
                    recordMismatchFiles += bucket.Count;
                }
            }

            if (recordMismatchFiles == 0)
            {
                continue;
            }

            seriesWithMismatch++;
            filesWithMismatch += recordMismatchFiles;
            _logger.LogInformation(
                "Series language mismatch: '{Canonical}' (preferred='{Preferred}', expected='{Expected}') has {Count} file(s) tagged with a different language.",
                LoggingHelper.SanitizeForLog(record.CanonicalTitle),
                LoggingHelper.SanitizeForLog(record.PreferredLanguage ?? globalDefault ?? "<none>"),
                LoggingHelper.SanitizeForLog(expected),
                recordMismatchFiles);

            if (options.AutoCorrect)
            {
                var flagged = await _retag.QueueRetagForSeriesAsync(record, cancellationToken);
                if (flagged > 0)
                {
                    seriesQueued++;
                }
            }
        }

        var summary = options.AutoCorrect
            ? $"Scanned {seriesScanned} series: {seriesWithMismatch} with language mismatch ({filesWithMismatch} file(s)); flagged {seriesQueued} series for metadata backfill."
            : $"Scanned {seriesScanned} series: {seriesWithMismatch} with language mismatch ({filesWithMismatch} file(s)). Enable autoCorrect to retag.";
        _logger.LogInformation("{Summary}", summary);
        return summary;
    }

    /// <summary>
    /// Same set used by <see cref="SeriesLanguagePreferenceRetagService"/>:
    /// canonical title + provider/user aliases + localized titles. These are
    /// the values the per-file <c>&lt;Series&gt;</c> tag could currently hold
    /// for a file that belongs to <paramref name="record"/>.
    /// </summary>
    private static HashSet<string> CollectTitlesForRecord(SeriesMetadataCacheRecord record)
    {
        var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(record.CanonicalTitle))
        {
            titles.Add(record.CanonicalTitle);
        }
        if (record.Aliases is { Count: > 0 })
        {
            foreach (var alias in record.Aliases)
            {
                if (!string.IsNullOrWhiteSpace(alias)) titles.Add(alias);
            }
        }
        if (record.UserAliases is { Count: > 0 })
        {
            foreach (var alias in record.UserAliases)
            {
                if (!string.IsNullOrWhiteSpace(alias)) titles.Add(alias);
            }
        }
        if (record.LocalizedTitles is { Count: > 0 })
        {
            foreach (var localized in record.LocalizedTitles)
            {
                if (!string.IsNullOrWhiteSpace(localized?.Title)) titles.Add(localized!.Title);
            }
        }
        return titles;
    }

    private static SeriesLanguageAuditOptions ParseOptions(string? optionsJson)
    {
        if (string.IsNullOrWhiteSpace(optionsJson))
        {
            return new SeriesLanguageAuditOptions();
        }
        try
        {
            return JsonSerializer.Deserialize<SeriesLanguageAuditOptions>(
                       optionsJson,
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? new SeriesLanguageAuditOptions();
        }
        catch
        {
            return new SeriesLanguageAuditOptions();
        }
    }

    private sealed class SeriesLanguageAuditOptions
    {
        public bool AutoCorrect { get; set; }
    }
}
