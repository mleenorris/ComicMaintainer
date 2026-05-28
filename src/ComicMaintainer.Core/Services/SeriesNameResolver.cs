using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Default implementation of <see cref="ISeriesNameResolver"/>. Mirrors the
/// resolution logic previously embedded in
/// <c>ComicProcessorService.ResolveNormalizedSeriesAsync</c> so the
/// normalize pipeline, audit handlers, and diagnostic endpoint produce the
/// same expected series for any given file.
/// </summary>
public class SeriesNameResolver : ISeriesNameResolver
{
    private const string UnknownSeries = "Unknown Series";

    private readonly IOptionsMonitor<AppSettings> _settings;
    private readonly ILogger<SeriesNameResolver> _logger;
    private readonly ISeriesMetadataCacheService? _cache;
    private readonly IExternalSeriesMetadataService? _externalMetadata;

    public SeriesNameResolver(
        IOptionsMonitor<AppSettings> settings,
        ILogger<SeriesNameResolver> logger,
        ISeriesMetadataCacheService? cache = null,
        IExternalSeriesMetadataService? externalMetadata = null)
    {
        _settings = settings;
        _logger = logger;
        _cache = cache;
        _externalMetadata = externalMetadata;
    }

    public async Task<SeriesNameResolution> ResolveAsync(
        string filePath,
        ComicMetadata? metadata,
        bool mutateCache = true,
        CancellationToken cancellationToken = default)
    {
        var folderSeries = ExtractFolderSeries(filePath);
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(folderSeries))
        {
            candidates.Add(folderSeries.Trim());
        }
        if (!string.IsNullOrWhiteSpace(metadata?.Series))
        {
            var trimmed = metadata!.Series!.Trim();
            if (!candidates.Any(c => string.Equals(c, trimmed, StringComparison.OrdinalIgnoreCase)))
            {
                candidates.Add(trimmed);
            }
        }
        var globalPreferred = _settings.CurrentValue.DefaultPreferredLanguage;

        // Step 1: user-canonical cache record.
        foreach (var candidate in candidates)
        {
            var record = await LookupUserCanonicalRecordAsync(candidate, cancellationToken);
            if (record is not null && !string.IsNullOrWhiteSpace(record.CanonicalTitle))
            {
                var resolved = ResolveDisplayTitle(record, globalPreferred);
                if (mutateCache)
                {
                    await EnsureFolderNameIsAliasAsync(folderSeries, record.CanonicalTitle, cancellationToken);
                }
                return new SeriesNameResolution
                {
                    ResolvedSeries = resolved,
                    WinningStep = SeriesNameResolutionStep.UserCanonical,
                    Candidates = candidates,
                    FolderSeries = folderSeries,
                    MatchedCacheKey = record.NormalizedKey,
                    AppliedLanguage = null,
                    Explanation =
                        $"User-canonical cache record '{record.NormalizedKey}' matched candidate '{candidate}'; canonical title '{record.CanonicalTitle}' used unconditionally."
                };
            }
        }

        // Step 2: matched cache record (successful or manual match).
        foreach (var candidate in candidates)
        {
            var record = await LookupMatchedRecordAsync(candidate, cancellationToken);
            if (record is not null && !string.IsNullOrWhiteSpace(record.CanonicalTitle))
            {
                var resolved = ResolveDisplayTitle(record, globalPreferred);
                var pinDecided = IsPinDecided(record);
                var appliedLanguage = pinDecided
                    ? null
                    : (!string.IsNullOrWhiteSpace(record.PreferredLanguage)
                        ? record.PreferredLanguage
                        : globalPreferred);
                if (mutateCache)
                {
                    await EnsureFolderNameIsAliasAsync(folderSeries, record.CanonicalTitle, cancellationToken);
                }
                return new SeriesNameResolution
                {
                    ResolvedSeries = resolved,
                    WinningStep = pinDecided
                        ? SeriesNameResolutionStep.PinnedLocalizedTitle
                        : SeriesNameResolutionStep.MatchedCache,
                    Candidates = candidates,
                    FolderSeries = folderSeries,
                    MatchedCacheKey = record.NormalizedKey,
                    AppliedLanguage = appliedLanguage,
                    Explanation = pinDecided
                        ? $"Matched cache record '{record.NormalizedKey}' has pinned localized title '{record.PinnedLocalizedTitle}'; pin wins over language preference."
                        : $"Matched cache record '{record.NormalizedKey}' (status='{record.LookupStatus}') matched candidate '{candidate}'; resolved to '{resolved}' under language '{appliedLanguage ?? "(none)"}'."
                };
            }
        }

        // Step 3: localized-title back-reference.
        if (_cache is not null
            && !string.IsNullOrWhiteSpace(metadata?.Series)
            && !string.IsNullOrWhiteSpace(folderSeries)
            && !string.Equals(metadata!.Series, folderSeries, StringComparison.OrdinalIgnoreCase))
        {
            var folderRecord = await LookupMatchedRecordAsync(folderSeries, cancellationToken)
                                ?? await LookupUserCanonicalRecordAsync(folderSeries, cancellationToken);
            if (folderRecord is not null
                && folderRecord.LocalizedTitles is { Count: > 0 }
                && folderRecord.LocalizedTitles.Any(lt =>
                    !string.IsNullOrWhiteSpace(lt?.Title)
                    && string.Equals(lt!.Title, metadata.Series, StringComparison.OrdinalIgnoreCase)))
            {
                var resolved = ResolveDisplayTitle(folderRecord, globalPreferred);
                var pinDecided = IsPinDecided(folderRecord);
                var appliedLanguage = pinDecided
                    ? null
                    : (!string.IsNullOrWhiteSpace(folderRecord.PreferredLanguage)
                        ? folderRecord.PreferredLanguage
                        : globalPreferred);
                if (mutateCache)
                {
                    await EnsureFolderNameIsAliasAsync(folderSeries, folderRecord.CanonicalTitle, cancellationToken);
                }
                return new SeriesNameResolution
                {
                    ResolvedSeries = resolved,
                    WinningStep = pinDecided
                        ? SeriesNameResolutionStep.PinnedLocalizedTitle
                        : SeriesNameResolutionStep.LocalizedTitleBackref,
                    Candidates = candidates,
                    FolderSeries = folderSeries,
                    MatchedCacheKey = folderRecord.NormalizedKey,
                    AppliedLanguage = appliedLanguage,
                    Explanation = pinDecided
                        ? $"File <Series> '{metadata.Series}' matched localized-title on folder's cache record '{folderRecord.NormalizedKey}', which has pinned localized title '{folderRecord.PinnedLocalizedTitle}'."
                        : $"File <Series> '{metadata.Series}' matched localized-title on folder's cache record '{folderRecord.NormalizedKey}'; resolved to '{resolved}'."
                };
            }
        }

        // Step 4: external provider lookup.
        // Skip when the user has explicitly cleared the cached external
        // metadata for this candidate — a "cleared" record must not be
        // silently re-populated by an automatic lookup. Only explicit user
        // actions (refresh / manual match) may repopulate external metadata.
        var hasClearedRecord = await AnyCandidateClearedAsync(candidates, cancellationToken);
        foreach (var candidate in hasClearedRecord ? (IEnumerable<string>)Array.Empty<string>() : candidates)
        {
            var external = await LookupExternalSeriesMetadataAsync(candidate, cancellationToken);
            if (!string.IsNullOrWhiteSpace(external?.CanonicalTitle))
            {
                var canonical = external!.CanonicalTitle.Trim();

                // Persist the lookup so subsequent normalize/resolve calls
                // read from the cache (Step 2) instead of re-querying the
                // provider. Honor the caller's mutateCache flag: read-only
                // resolution paths (e.g. previews) must not write back.
                SeriesMetadataCacheRecord? persisted = null;
                if (mutateCache && _cache is not null)
                {
                    try
                    {
                        persisted = await _cache.PersistExternalLookupAsync(candidate, external, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Failed to persist external lookup for series {SeriesName}",
                            LoggingHelper.SanitizeForLog(candidate));
                    }
                }

                var resolveSource = persisted ?? new SeriesMetadataCacheRecord
                {
                    CanonicalTitle = canonical,
                    LocalizedTitles = external.LocalizedTitles is { Count: > 0 }
                        ? external.LocalizedTitles
                            .Where(t => t is not null && !string.IsNullOrWhiteSpace(t.Title))
                            .Select(t => new LocalizedTitle(t.Title, t.Language))
                            .ToList()
                        : new List<LocalizedTitle>(),
                    PreferredLanguage = null,
                    IsUserCanonical = false
                };
                var resolved = ResolveDisplayTitle(resolveSource, globalPreferred);
                if (mutateCache)
                {
                    await EnsureFolderNameIsAliasAsync(folderSeries, canonical, cancellationToken);
                }
                return new SeriesNameResolution
                {
                    ResolvedSeries = resolved,
                    WinningStep = SeriesNameResolutionStep.ExternalLookup,
                    Candidates = candidates,
                    FolderSeries = folderSeries,
                    MatchedCacheKey = persisted?.NormalizedKey,
                    AppliedLanguage = globalPreferred,
                    Explanation =
                        $"External provider '{external.Source ?? "(unknown)"}' returned canonical '{canonical}' for candidate '{candidate}'; resolved to '{resolved}' under language '{globalPreferred ?? "(none)"}'."
                };
            }
        }

        // Step 5: preserve existing <Series> value.
        if (!string.IsNullOrWhiteSpace(metadata?.Series))
        {
            return new SeriesNameResolution
            {
                ResolvedSeries = metadata!.Series!.Trim(),
                WinningStep = SeriesNameResolutionStep.ExistingMetadata,
                Candidates = candidates,
                FolderSeries = folderSeries,
                Explanation =
                    $"Preserved existing <Series> value '{metadata.Series}' because no cache/external match was found."
            };
        }

        // Step 6: folder name.
        if (!string.IsNullOrWhiteSpace(folderSeries))
        {
            return new SeriesNameResolution
            {
                ResolvedSeries = folderSeries,
                WinningStep = SeriesNameResolutionStep.FolderName,
                Candidates = candidates,
                FolderSeries = folderSeries,
                Explanation =
                    $"Fell back to folder-derived series '{folderSeries}' (file has no <Series> and no cache/external match)."
            };
        }

        // Step 7: unknown sentinel.
        return new SeriesNameResolution
        {
            ResolvedSeries = UnknownSeries,
            WinningStep = SeriesNameResolutionStep.UnknownSentinel,
            Candidates = candidates,
            FolderSeries = folderSeries,
            Explanation = $"No candidate produced a series name; using '{UnknownSeries}' sentinel."
        };
    }

    /// <summary>
    /// Extract the immediate parent directory name as a series candidate,
    /// matching the previous <c>ExtractSeriesFromFilename</c> behaviour.
    /// </summary>
    internal static string ExtractFolderSeries(string filePath)
    {
        var folderName = Path.GetFileName(Path.GetDirectoryName(filePath));
        if (string.IsNullOrEmpty(folderName))
        {
            return UnknownSeries;
        }
        var series = ComicFileProcessor.NormalizeSeriesName(folderName, forComparison: false);
        return string.IsNullOrEmpty(series) ? UnknownSeries : series;
    }

    private static string ResolveDisplayTitle(SeriesMetadataCacheRecord record, string? globalPreferredLanguage)
    {
        var resolved = SeriesDisplayTitleResolver.Resolve(record, globalPreferredLanguage);
        if (!string.IsNullOrWhiteSpace(resolved))
        {
            return resolved.Trim();
        }
        if (!string.IsNullOrWhiteSpace(record.CanonicalTitle))
        {
            return record.CanonicalTitle.Trim();
        }
        return UnknownSeries;
    }

    /// <summary>
    /// True when the record's resolution would be decided by
    /// <see cref="SeriesMetadataCacheRecord.PinnedLocalizedTitle"/>: i.e.
    /// not user-canonical, and the pinned title is non-empty. Used to
    /// report <see cref="SeriesNameResolutionStep.PinnedLocalizedTitle"/>
    /// as the winning step on top of an otherwise MatchedCache /
    /// LocalizedTitleBackref match.
    /// </summary>
    private static bool IsPinDecided(SeriesMetadataCacheRecord record) =>
        !(record.IsUserCanonical && !string.IsNullOrWhiteSpace(record.CanonicalTitle))
        && !string.IsNullOrWhiteSpace(record.PinnedLocalizedTitle);

    /// <inheritdoc />
    public string ResolveForRecord(SeriesMetadataCacheRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var globalPreferred = _settings.CurrentValue.DefaultPreferredLanguage;
        return ResolveDisplayTitle(record, globalPreferred);
    }

    private async Task<SeriesMetadataCacheRecord?> LookupUserCanonicalRecordAsync(string seriesName, CancellationToken cancellationToken)
    {
        if (_cache is null || string.IsNullOrWhiteSpace(seriesName)) return null;
        try
        {
            var record = await GetRecordByKeyOrAliasAsync(seriesName, cancellationToken);
            return record is { IsUserCanonical: true } && !string.IsNullOrWhiteSpace(record.CanonicalTitle)
                ? record
                : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to consult cache for user-canonical title {SeriesName}",
                LoggingHelper.SanitizeForLog(seriesName));
            return null;
        }
    }

    private async Task<SeriesMetadataCacheRecord?> LookupMatchedRecordAsync(string seriesName, CancellationToken cancellationToken)
    {
        if (_cache is null || string.IsNullOrWhiteSpace(seriesName)) return null;
        try
        {
            var record = await GetRecordByKeyOrAliasAsync(seriesName, cancellationToken);
            if (record is null || string.IsNullOrWhiteSpace(record.CanonicalTitle)) return null;

            var status = record.LookupStatus;
            var isMatched = record.IsUserCanonical
                || string.Equals(status, "success", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "manual_match", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "manual", StringComparison.OrdinalIgnoreCase);
            return isMatched ? record : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to consult cache for matched title {SeriesName}",
                LoggingHelper.SanitizeForLog(seriesName));
            return null;
        }
    }

    /// <summary>
    /// Resolve a candidate series name to its cache record. First tries an
    /// authoritative exact-key match; if that misses, falls back to the cache's
    /// alias index so a file whose folder name or embedded <c>&lt;Series&gt;</c>
    /// is an alias / localized title (rather than the record's own normalized
    /// key) still resolves to the record that owns the series' canonical / pin /
    /// preferred-language settings. This keeps per-file metadata rewriting in
    /// sync with how the library groups and displays the series.
    /// </summary>
    private async Task<SeriesMetadataCacheRecord?> GetRecordByKeyOrAliasAsync(string seriesName, CancellationToken cancellationToken)
    {
        if (_cache is null || string.IsNullOrWhiteSpace(seriesName)) return null;

        var key = _cache.NormalizeKey(seriesName);
        if (!string.IsNullOrWhiteSpace(key))
        {
            var direct = await _cache.GetAsync(key, cancellationToken);
            if (direct is not null)
            {
                return direct;
            }
        }

        return await _cache.ResolveByTitleAsync(seriesName, cancellationToken);
    }

    private async Task<ExternalSeriesMetadata?> LookupExternalSeriesMetadataAsync(string seriesName, CancellationToken cancellationToken)
    {
        if (_externalMetadata is null || string.IsNullOrWhiteSpace(seriesName)) return null;
        try
        {
            return await _externalMetadata.LookupSeriesAsync(seriesName, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve external metadata for series {SeriesName}",
                LoggingHelper.SanitizeForLog(seriesName));
            return null;
        }
    }

    /// <summary>
    /// True when any of the supplied candidates resolves (via the normalized
    /// cache key) to a record whose <see cref="SeriesMetadataCacheRecord.LookupStatus"/>
    /// is <c>cleared</c>. Used by Step 4 (external provider lookup) to honor
    /// an explicit user clear: once a series's external metadata has been
    /// cleared, automatic re-fetches must not re-populate it. Only an
    /// explicit refresh or manual match (which set a non-cleared status)
    /// may bring external metadata back.
    /// </summary>
    private async Task<bool> AnyCandidateClearedAsync(IReadOnlyList<string> candidates, CancellationToken cancellationToken)
    {
        if (_cache is null || candidates.Count == 0) return false;
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try
            {
                var key = _cache.NormalizeKey(candidate);
                if (string.IsNullOrWhiteSpace(key)) continue;
                var record = await _cache.GetAsync(key, cancellationToken);
                if (record is not null
                    && string.Equals(record.LookupStatus, "cleared", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to consult cache for cleared-status check on {SeriesName}",
                    LoggingHelper.SanitizeForLog(candidate));
            }
        }
        return false;
    }

    /// <summary>
    /// Ensures that the file's folder-derived series name is present in the
    /// resolved series' cache record. If the cache contains no record for
    /// the resolved series yet, one is created with the folder name as the
    /// sole user alias.
    /// </summary>
    private async Task EnsureFolderNameIsAliasAsync(string folderSeriesName, string resolvedSeries, CancellationToken cancellationToken)
    {
        if (_cache is null
            || string.IsNullOrWhiteSpace(folderSeriesName)
            || string.IsNullOrWhiteSpace(resolvedSeries)
            || string.Equals(folderSeriesName, UnknownSeries, StringComparison.OrdinalIgnoreCase)
            || string.Equals(resolvedSeries, UnknownSeries, StringComparison.OrdinalIgnoreCase)
            || string.Equals(folderSeriesName, resolvedSeries, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            var resolvedKey = _cache.NormalizeKey(resolvedSeries);
            if (string.IsNullOrWhiteSpace(resolvedKey)) return;

            var record = await _cache.GetAsync(resolvedKey, cancellationToken);
            if (record is not null)
            {
                if (string.Equals(record.CanonicalTitle, folderSeriesName, StringComparison.OrdinalIgnoreCase)) return;
                if (record.Aliases is not null
                    && record.Aliases.Any(a => string.Equals(a, folderSeriesName, StringComparison.OrdinalIgnoreCase))) return;
                if (record.UserAliases is not null
                    && record.UserAliases.Any(a => string.Equals(a, folderSeriesName, StringComparison.OrdinalIgnoreCase))) return;
            }

            var mergedAliases = new List<string>();
            if (record?.UserAliases is { Count: > 0 } existing)
            {
                mergedAliases.AddRange(existing);
            }
            mergedAliases.Add(folderSeriesName.Trim());

            await _cache.SetUserAliasesAsync(
                resolvedSeries,
                mergedAliases,
                canonicalTitleOverride: null,
                cancellationToken);

            _logger.LogInformation(
                "Added folder name '{Folder}' as user alias for series '{Series}'",
                LoggingHelper.SanitizeForLog(folderSeriesName),
                LoggingHelper.SanitizeForLog(resolvedSeries));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to add folder name {Folder} as alias for series {Series}",
                LoggingHelper.SanitizeForLog(folderSeriesName),
                LoggingHelper.SanitizeForLog(resolvedSeries));
        }
    }
}
