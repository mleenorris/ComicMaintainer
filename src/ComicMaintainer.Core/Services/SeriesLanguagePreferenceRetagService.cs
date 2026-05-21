using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Default implementation of <see cref="ISeriesLanguagePreferenceRetagService"/>.
/// Builds a set of "matching titles" for the affected series (canonical title +
/// aliases + localized titles), scans the tracked file store for files whose
/// <c>Metadata.Series</c> matches one of those titles, and submits them as a
/// forced normalization job on <see cref="IComicProcessorService"/>. Mirrors
/// the pattern used by
/// <see cref="SeriesMetadataRefreshJobService"/>'s post-refresh retag step so
/// progress / events flow through the standard processing-job plumbing.
/// </summary>
public class SeriesLanguagePreferenceRetagService : ISeriesLanguagePreferenceRetagService
{
    private readonly IFileStoreService _fileStore;
    private readonly IComicProcessorService _processor;
    private readonly ISeriesMetadataCacheService _cache;
    private readonly IOptionsMonitor<AppSettings> _settings;
    private readonly ILogger<SeriesLanguagePreferenceRetagService> _logger;

    public SeriesLanguagePreferenceRetagService(
        IFileStoreService fileStore,
        IComicProcessorService processor,
        ISeriesMetadataCacheService cache,
        IOptionsMonitor<AppSettings> settings,
        ILogger<SeriesLanguagePreferenceRetagService> logger)
    {
        _fileStore = fileStore;
        _processor = processor;
        _cache = cache;
        _settings = settings;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Guid?> QueueRetagForSeriesAsync(
        SeriesMetadataCacheRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (!_settings.CurrentValue.WatcherEnableNormalize)
        {
            _logger.LogWarning(
                "Preferred-language change for series '{Series}' will not rewrite per-file metadata because WatcherEnableNormalize is false.",
                LoggingHelper.SanitizeForLog(record.CanonicalTitle));
            return null;
        }

        var titles = CollectTitlesForRecord(record);
        if (titles.Count == 0)
        {
            return null;
        }

        return await QueueRetagForTitlesAsync(titles, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Guid?> QueueRetagForGlobalDefaultAsync(CancellationToken cancellationToken = default)
    {
        if (!_settings.CurrentValue.WatcherEnableNormalize)
        {
            _logger.LogWarning(
                "Global default preferred-language change will not rewrite per-file metadata because WatcherEnableNormalize is false.");
            return null;
        }

        var allRecords = await _cache.GetAllAsync(cancellationToken);
        var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in allRecords)
        {
            // Skip series that have explicitly opted in to a per-series
            // preference — those are unaffected by a global default change.
            if (!string.IsNullOrWhiteSpace(record.PreferredLanguage))
            {
                continue;
            }
            foreach (var title in CollectTitlesForRecord(record))
            {
                titles.Add(title);
            }
        }

        if (titles.Count == 0)
        {
            return null;
        }

        return await QueueRetagForTitlesAsync(titles, cancellationToken);
    }

    /// <summary>
    /// Build the case-insensitive set of titles that should match a file's
    /// <c>Metadata.Series</c> to be considered part of <paramref name="record"/>.
    /// Includes the canonical title, every provider/user alias, and every
    /// localized title (so files whose <c>&lt;Series&gt;</c> has already been
    /// rewritten to a localized name are still picked up on a later
    /// preference change).
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

    private async Task<Guid?> QueueRetagForTitlesAsync(
        HashSet<string> titles,
        CancellationToken cancellationToken)
    {
        var files = await _fileStore.GetAllFilesAsync(cancellationToken);
        var matchedPaths = files
            .Where(f => !string.IsNullOrWhiteSpace(f.Metadata?.Series)
                        && titles.Contains(f.Metadata!.Series!))
            .Select(f => f.FilePath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (matchedPaths.Count == 0)
        {
            _logger.LogInformation(
                "Preferred-language change matched no tracked files; nothing to retag.");
            return null;
        }

        var jobId = await _processor.NormalizeFilesAsync(matchedPaths, forceReprocess: true, cancellationToken);
        _logger.LogInformation(
            "Queued normalization job {JobId} for {FileCount} files to apply updated preferred-language metadata.",
            jobId, matchedPaths.Count);
        return jobId;
    }
}
