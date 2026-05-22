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
                var appliedLanguage = !string.IsNullOrWhiteSpace(record.PreferredLanguage)
                    ? record.PreferredLanguage
                    : globalPreferred;
                if (mutateCache)
                {
                    await EnsureFolderNameIsAliasAsync(folderSeries, record.CanonicalTitle, cancellationToken);
                }
                return new SeriesNameResolution
                {
                    ResolvedSeries = resolved,
                    WinningStep = SeriesNameResolutionStep.MatchedCache,
                    Candidates = candidates,
                    FolderSeries = folderSeries,
                    MatchedCacheKey = record.NormalizedKey,
                    AppliedLanguage = appliedLanguage,
                    Explanation =
                        $"Matched cache record '{record.NormalizedKey}' (status='{record.LookupStatus}') matched candidate '{candidate}'; resolved to '{resolved}' under language '{appliedLanguage ?? "(none)"}'."
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
                var appliedLanguage = !string.IsNullOrWhiteSpace(folderRecord.PreferredLanguage)
                    ? folderRecord.PreferredLanguage
                    : globalPreferred;
                if (mutateCache)
                {
                    await EnsureFolderNameIsAliasAsync(folderSeries, folderRecord.CanonicalTitle, cancellationToken);
                }
                return new SeriesNameResolution
                {
                    ResolvedSeries = resolved,
                    WinningStep = SeriesNameResolutionStep.LocalizedTitleBackref,
                    Candidates = candidates,
                    FolderSeries = folderSeries,
                    MatchedCacheKey = folderRecord.NormalizedKey,
                    AppliedLanguage = appliedLanguage,
                    Explanation =
                        $"File <Series> '{metadata.Series}' matched localized-title on folder's cache record '{folderRecord.NormalizedKey}'; resolved to '{resolved}'."
                };
            }
        }

        // Step 4: external provider lookup.
        foreach (var candidate in candidates)
        {
            var external = await LookupExternalSeriesMetadataAsync(candidate, cancellationToken);
            if (!string.IsNullOrWhiteSpace(external?.CanonicalTitle))
            {
                var canonical = external!.CanonicalTitle.Trim();
                var transient = new SeriesMetadataCacheRecord
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
                var resolved = ResolveDisplayTitle(transient, globalPreferred);
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
                    MatchedCacheKey = null,
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

    private async Task<SeriesMetadataCacheRecord?> LookupUserCanonicalRecordAsync(string seriesName, CancellationToken cancellationToken)
    {
        if (_cache is null || string.IsNullOrWhiteSpace(seriesName)) return null;
        try
        {
            var key = _cache.NormalizeKey(seriesName);
            if (string.IsNullOrWhiteSpace(key)) return null;
            var record = await _cache.GetAsync(key, cancellationToken);
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
            var key = _cache.NormalizeKey(seriesName);
            if (string.IsNullOrWhiteSpace(key)) return null;
            var record = await _cache.GetAsync(key, cancellationToken);
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
