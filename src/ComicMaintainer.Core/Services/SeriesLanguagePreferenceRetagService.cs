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
/// <c>Metadata.Series</c> (or parent folder name) matches one of those titles,
/// and flags them for a metadata backfill via
/// <see cref="IFileStoreService.MarkFilesNeedingBackfillAsync"/>. The scheduled
/// metadata-backfill job then rewrites each file's ComicInfo.xml
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

        var titles = CollectTitlesForRecord(record);
        if (titles.Count == 0)
        {
            return 0;
        }

        return await MarkMatchingFilesForBackfillAsync(titles, cancellationToken);
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
            return 0;
        }

        return await MarkMatchingFilesForBackfillAsync(titles, cancellationToken);
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

    private async Task<int> MarkMatchingFilesForBackfillAsync(
        HashSet<string> titles,
        CancellationToken cancellationToken)
    {
        var files = await _fileStore.GetAllFilesAsync(cancellationToken);

        // Build a case-insensitive set of normalized keys for the titles we
        // care about so we can do a key-based comparison against folder names.
        // The cache's NormalizeKey applies the same case/whitespace/punctuation
        // folding the rest of the system uses, so a folder like "One_Piece"
        // matches a record canonicalised as "One Piece".
        var titleKeys = new HashSet<string>(
            titles.Select(t => _cache.NormalizeKey(t))
                  .Where(k => !string.IsNullOrWhiteSpace(k))!,
            StringComparer.OrdinalIgnoreCase);

        var matchedPaths = files
            .Where(f => !string.IsNullOrWhiteSpace(f.FilePath) && MatchesAnyTitle(f, titles, titleKeys))
            .Select(f => f.FilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

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

    /// <summary>
    /// Returns true when a tracked file should be considered part of the
    /// series identified by <paramref name="titles"/>. We first try the file's
    /// cached ComicInfo.xml <c>Series</c> value (when populated), then fall
    /// back to the parent-folder-derived series name normalized via the cache
    /// key so a folder rename / case difference / underscore-vs-colon doesn't
    /// hide the match. The folder fallback is necessary because the
    /// <see cref="ComicFile.Metadata"/> field is not currently populated for
    /// most tracked files, which would otherwise cause the retag scan to find
    /// zero matches and silently skip rewriting <c>&lt;Series&gt;</c>.
    /// </summary>
    private bool MatchesAnyTitle(ComicFile file, HashSet<string> titles, HashSet<string> titleKeys)
    {
        var metadataSeries = file.Metadata?.Series;
        if (!string.IsNullOrWhiteSpace(metadataSeries) && titles.Contains(metadataSeries))
        {
            return true;
        }

        if (titleKeys.Count == 0)
        {
            return false;
        }

        var folderName = Path.GetFileName(Path.GetDirectoryName(file.FilePath));
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return false;
        }

        var folderSeries = ComicFileProcessor.NormalizeSeriesName(folderName, forComparison: false);
        var folderKey = _cache.NormalizeKey(folderSeries);
        return !string.IsNullOrWhiteSpace(folderKey) && titleKeys.Contains(folderKey);
    }
}
