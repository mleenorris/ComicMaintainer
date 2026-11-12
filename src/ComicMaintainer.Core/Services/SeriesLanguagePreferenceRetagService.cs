using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Default implementation of <see cref="ISeriesLanguagePreferenceRetagService"/>.
/// Finds the tracked files of the affected series with
/// <see cref="SeriesFileMatcher"/> (their <c>Metadata.Series</c> or
/// folder-derived series name has the series key of one of the series' titles:
/// canonical title, aliases or localized titles) and flags them for a metadata
/// backfill via <see cref="IFileStoreService.MarkFilesNeedingBackfillAsync"/>.
/// The scheduled metadata-backfill job then rewrites each file's ComicInfo.xml
/// <c>&lt;Series&gt;</c> from the DB-authoritative metadata. This keeps the
/// database as the source of truth and avoids the per-file churn of an eager
/// forced normalization job.
/// </summary>
public class SeriesLanguagePreferenceRetagService : ISeriesLanguagePreferenceRetagService
{
    private readonly IFileStoreService _fileStore;
    private readonly ISeriesMetadataCacheService _cache;
    private readonly IOptionsMonitor<AppSettings> _settings;
    private readonly ILogger<SeriesLanguagePreferenceRetagService> _logger;

    public SeriesLanguagePreferenceRetagService(
        IFileStoreService fileStore,
        ISeriesMetadataCacheService cache,
        IOptionsMonitor<AppSettings> settings,
        ILogger<SeriesLanguagePreferenceRetagService> logger)
    {
        _fileStore = fileStore;
        _cache = cache;
        _settings = settings;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int> QueueRetagForSeriesAsync(
        SeriesMetadataCacheRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (!_settings.CurrentValue.WatcherEnableNormalize)
        {
            _logger.LogWarning(
                "Preferred-language change for series '{Series}' will not rewrite per-file metadata because WatcherEnableNormalize is false.",
                LoggingHelper.SanitizeForLog(record.CanonicalTitle));
            return 0;
        }

        var seriesKeys = SeriesFileMatcher.KeysOf(record);
        if (seriesKeys.Count == 0)
        {
            return 0;
        }

        return await MarkMatchingFilesForBackfillAsync(seriesKeys, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> QueueRetagForGlobalDefaultAsync(CancellationToken cancellationToken = default)
    {
        if (!_settings.CurrentValue.WatcherEnableNormalize)
        {
            _logger.LogWarning(
                "Global default preferred-language change will not rewrite per-file metadata because WatcherEnableNormalize is false.");
            return 0;
        }

        var allRecords = await _cache.GetAllAsync(cancellationToken);
        var seriesKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in allRecords)
        {
            // Skip series that have explicitly opted in to a per-series
            // preference — those are unaffected by a global default change.
            if (!string.IsNullOrWhiteSpace(record.PreferredLanguage))
            {
                continue;
            }
            seriesKeys.UnionWith(SeriesFileMatcher.KeysOf(record));
        }

        if (seriesKeys.Count == 0)
        {
            return 0;
        }

        return await MarkMatchingFilesForBackfillAsync(seriesKeys, cancellationToken);
    }

    private async Task<int> MarkMatchingFilesForBackfillAsync(
        IReadOnlySet<string> seriesKeys,
        CancellationToken cancellationToken)
    {
        var files = await _fileStore.GetAllFilesAsync(cancellationToken);
        var matchedPaths = SeriesFileMatcher.SelectFilePaths(files, seriesKeys);

        if (matchedPaths.Count == 0)
        {
            _logger.LogInformation(
                "Preferred-language change matched no tracked files; nothing to backfill.");
            return 0;
        }

        var marked = await _fileStore.MarkFilesNeedingBackfillAsync(matchedPaths, cancellationToken);
        _logger.LogInformation(
            "Flagged {FileCount} file(s) for metadata backfill to apply updated preferred-language/series-name metadata.",
            marked);
        return marked;
    }
}
