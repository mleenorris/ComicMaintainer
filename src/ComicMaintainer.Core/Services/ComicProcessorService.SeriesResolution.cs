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
    private async Task<string> ResolveNormalizedSeriesAsync(ComicMetadata metadata, string filePath, CancellationToken cancellationToken)
    {
        // Delegate to ISeriesNameResolver when registered so the normalize
        // pipeline, audit handlers, library-scan retag pass, and diagnostic
        // endpoint all share the same documented priority. Falls back to
        // the legacy in-process logic when the resolver hasn't been
        // injected (e.g. in tests that construct the service directly).
        if (_seriesNameResolver is not null)
        {
            var resolution = await _seriesNameResolver.ResolveAsync(filePath, metadata, mutateCache: true, cancellationToken);
            return string.IsNullOrWhiteSpace(resolution.ResolvedSeries) ? UnknownSeries : resolution.ResolvedSeries;
        }

        var fallbackSeries = ExtractSeriesFromFilename(filePath);
        var candidateSeries = new[] { fallbackSeries, metadata.Series }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var globalPreferredLanguage = _settings.DefaultPreferredLanguage;

        // First pass: if any candidate has a user-selected series name, honor
        // it immediately without falling through to external lookups. This
        // guarantees that a user's explicit name choice wins over whatever an
        // external provider returns. SeriesDisplayTitleResolver returns the
        // user-selected name verbatim for that case.
        foreach (var candidate in candidateSeries)
        {
            var userSelectedRecord = await LookupUserSelectedRecordAsync(candidate, cancellationToken);
            if (userSelectedRecord is not null && !string.IsNullOrWhiteSpace(userSelectedRecord.SeriesName))
            {
                var resolved = ResolveDisplayTitle(userSelectedRecord, globalPreferredLanguage);
                await EnsureFolderNameIsAliasAsync(fallbackSeries, userSelectedRecord.CanonicalTitle, cancellationToken);
                return resolved;
            }
        }

        // Second pass: if any candidate has a cached match from an external
        // provider (or a previously-applied manual match), prefer that canonical
        // title over the folder-derived name. This ensures that once a series
        // has been matched, every subsequent normalization writes the matched
        // name into the file's metadata instead of the raw folder name. The
        // resolver picks the localized variant when a language preference is set.
        foreach (var candidate in candidateSeries)
        {
            var matchedRecord = await LookupMatchedRecordAsync(candidate, cancellationToken);
            if (matchedRecord is not null && !string.IsNullOrWhiteSpace(matchedRecord.CanonicalTitle))
            {
                var resolved = ResolveDisplayTitle(matchedRecord, globalPreferredLanguage);
                await EnsureFolderNameIsAliasAsync(fallbackSeries, matchedRecord.CanonicalTitle, cancellationToken);
                return resolved;
            }
        }

        // Defensive lookup: if `<Series>` already holds a localized title that
        // isn't separately indexed under the normalized-key map, fall back to
        // searching the cache record reachable via the folder name and see
        // whether the metadata.Series matches one of its LocalizedTitles. This
        // avoids losing the record once an earlier pass rewrote `<Series>` to
        // a non-canonical language variant.
        if (_seriesMetadataCache is not null
            && !string.IsNullOrWhiteSpace(metadata.Series)
            && !string.IsNullOrWhiteSpace(fallbackSeries)
            && !string.Equals(metadata.Series, fallbackSeries, StringComparison.OrdinalIgnoreCase))
        {
            var folderRecord = await LookupMatchedRecordAsync(fallbackSeries, cancellationToken)
                               ?? await LookupUserSelectedRecordAsync(fallbackSeries, cancellationToken);
            if (folderRecord is not null
                && folderRecord.LocalizedTitles is { Count: > 0 }
                && folderRecord.LocalizedTitles.Any(lt =>
                    !string.IsNullOrWhiteSpace(lt?.Title)
                    && string.Equals(lt!.Title, metadata.Series, StringComparison.OrdinalIgnoreCase)))
            {
                var resolved = ResolveDisplayTitle(folderRecord, globalPreferredLanguage);
                await EnsureFolderNameIsAliasAsync(fallbackSeries, folderRecord.CanonicalTitle, cancellationToken);
                return resolved;
            }
        }

        // Skip Step 4 entirely when the cache holds a "cleared" record for any
        // candidate — the user has explicitly cleared external metadata and
        // it must not be silently re-fetched. Only an explicit refresh /
        // manual match may bring external metadata back.
        var hasClearedRecord = await AnyCandidateClearedAsync(candidateSeries, cancellationToken);

        foreach (var candidate in hasClearedRecord ? (IEnumerable<string>)Array.Empty<string>() : candidateSeries)
        {
            var externalMetadata = await LookupExternalSeriesMetadataAsync(candidate, cancellationToken);
            if (!string.IsNullOrWhiteSpace(externalMetadata?.CanonicalTitle))
            {
                var canonical = externalMetadata!.CanonicalTitle.Trim();

                // Persist the lookup so subsequent normalize calls hit the
                // cache (the matched-record pass above) instead of re-querying
                // the provider on every file. Failure to persist must not
                // prevent the per-file normalize from proceeding.
                SeriesMetadataCacheRecord? persisted = null;
                if (_seriesMetadataCache is not null)
                {
                    try
                    {
                        persisted = await _seriesMetadataCache.PersistExternalLookupAsync(candidate, externalMetadata, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Failed to persist external lookup for series {SeriesName}",
                            LoggingHelper.SanitizeForLog(candidate));
                    }
                }

                // Resolve the display title from the persisted record when
                // available so per-series language preferences (if any) apply;
                // otherwise fall back to a transient projection of the lookup.
                var resolveSource = persisted ?? new SeriesMetadataCacheRecord
                {
                    CanonicalTitle = canonical,
                    LocalizedTitles = externalMetadata.LocalizedTitles is { Count: > 0 }
                        ? externalMetadata.LocalizedTitles
                            .Where(t => t is not null && !string.IsNullOrWhiteSpace(t.Title))
                            .Select(t => new LocalizedTitle(t.Title, t.Language))
                            .ToList()
                        : new List<LocalizedTitle>(),
                    PreferredLanguage = null
                };

                var resolved = ResolveDisplayTitle(resolveSource, globalPreferredLanguage);
                await EnsureFolderNameIsAliasAsync(fallbackSeries, canonical, cancellationToken);
                return resolved;
            }
        }

        // No external/cache metadata exists for this series. Per project policy,
        // only fall back to the folder-derived name when the file itself has no
        // <Series> value — otherwise preserve whatever the file already contains
        // so we don't overwrite a meaningful series with a folder name solely
        // because we couldn't find external metadata for it.
        if (!string.IsNullOrWhiteSpace(metadata.Series))
        {
            return metadata.Series.Trim();
        }
        return !string.IsNullOrWhiteSpace(fallbackSeries) ? fallbackSeries : UnknownSeries;
    }

    /// <summary>
    /// Run a cache record through <see cref="SeriesDisplayTitleResolver"/> to
    /// apply the per-series / global language preference rules, defending
    /// against an unexpectedly empty result by falling back to the canonical
    /// title (and ultimately <see cref="UnknownSeries"/>).
    /// </summary>
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
    /// Ensures that the file's folder-derived series name is present in the
    /// resolved series' cache record. If the cache contains no record for the
    /// resolved series yet, one is created with the folder name as the sole
    /// user alias. This lets the system "learn" alternative names automatically
    /// the first time we see a file whose folder name differs from its
    /// canonical series title.
    /// </summary>
    private async Task EnsureFolderNameIsAliasAsync(string folderSeriesName, string resolvedSeries, CancellationToken cancellationToken)
    {
        if (_seriesMetadataCache is null
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
            var resolvedKey = _seriesMetadataCache.NormalizeKey(resolvedSeries);
            if (string.IsNullOrWhiteSpace(resolvedKey))
            {
                return;
            }

            var record = await _seriesMetadataCache.GetAsync(resolvedKey, cancellationToken);

            // If the folder name is already known under any title on the
            // record (canonical title, provider alias, or existing user alias),
            // there is nothing to do.
            if (record is not null)
            {
                if (string.Equals(record.CanonicalTitle, folderSeriesName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                if (record.Aliases is not null
                    && record.Aliases.Any(a => string.Equals(a, folderSeriesName, StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }
                if (record.UserAliases is not null
                    && record.UserAliases.Any(a => string.Equals(a, folderSeriesName, StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }
            }

            // Merge the folder name into the existing user-alias list (or
            // start a fresh list when no record exists yet) and persist it.
            var mergedAliases = new List<string>();
            if (record?.UserAliases is { Count: > 0 } existing)
            {
                mergedAliases.AddRange(existing);
            }
            mergedAliases.Add(folderSeriesName.Trim());

            await _seriesMetadataCache.SetUserAliasesAsync(
                resolvedSeries,
                mergedAliases,
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

    private async Task<SeriesMetadataCacheRecord?> LookupUserSelectedRecordAsync(string seriesName, CancellationToken cancellationToken)
    {
        if (_seriesMetadataCache is null || string.IsNullOrWhiteSpace(seriesName))
        {
            return null;
        }

        try
        {
            var key = _seriesMetadataCache.NormalizeKey(seriesName);
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            var record = await _seriesMetadataCache.GetAsync(key, cancellationToken);
            if (record is not null && !string.IsNullOrWhiteSpace(record.SeriesName))
            {
                return record;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to consult series metadata cache for user-selected name {SeriesName}",
                LoggingHelper.SanitizeForLog(seriesName));
        }

        return null;
    }

    /// <summary>
    /// Returns the cached record for a series when it represents a confirmed
    /// match — either an external provider lookup that succeeded or a manually
    /// applied match. Differs from
    /// <see cref="LookupUserSelectedRecordAsync"/> in that it does not require
    /// the record to be flagged as user-canonical; any successful match is
    /// considered authoritative for normalization so that the matched name
    /// (rather than the raw folder name) ends up in file metadata. The full
    /// record is returned so the caller can apply language preference rules
    /// via <see cref="SeriesDisplayTitleResolver"/>.
    /// </summary>
    private async Task<SeriesMetadataCacheRecord?> LookupMatchedRecordAsync(string seriesName, CancellationToken cancellationToken)
    {
        if (_seriesMetadataCache is null || string.IsNullOrWhiteSpace(seriesName))
        {
            return null;
        }

        try
        {
            var key = _seriesMetadataCache.NormalizeKey(seriesName);
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            var record = await _seriesMetadataCache.GetAsync(key, cancellationToken);
            if (record is null || string.IsNullOrWhiteSpace(record.CanonicalTitle))
            {
                return null;
            }

            // Recognise any cache record whose status implies a positive match.
            // "success" comes from a provider lookup that returned a hit;
            // "manual_match" is set when the user explicitly picked a candidate
            // from the manual-match UI; "manual" is set when the user takes any
            // direct action on the record (e.g. setting a preferred language,
            // editing user aliases, or saving a canonical-title override) and
            // therefore represents an explicit user decision that should be
            // honoured when rewriting per-file <Series> metadata. Without
            // including "manual" here, setting a per-series preferred language
            // on a series that has never been externally matched would silently
            // fall through to the external-lookup path (or the raw folder name)
            // instead of running the cached record through SeriesDisplayTitleResolver.
            // A user-canonical override is already handled by
            // LookupUserSelectedRecordAsync but we accept it here too so
            // callers can use this method in isolation.
            var status = record.LookupStatus;
            var isMatched = !string.IsNullOrWhiteSpace(record.SeriesName)
                || string.Equals(status, "success", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "manual_match", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "manual", StringComparison.OrdinalIgnoreCase);

            return isMatched ? record : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to consult series metadata cache for matched title {SeriesName}",
                LoggingHelper.SanitizeForLog(seriesName));
        }

        return null;
    }

    private async Task<ExternalSeriesMetadata?> LookupExternalSeriesMetadataAsync(string seriesName, CancellationToken cancellationToken)
    {
        if (_externalSeriesMetadata is null || string.IsNullOrWhiteSpace(seriesName))
        {
            return null;
        }

        try
        {
            return await _externalSeriesMetadata.LookupSeriesAsync(seriesName, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve external metadata for series {SeriesName}", LoggingHelper.SanitizeForLog(seriesName));
            return null;
        }
    }

    /// <summary>
    /// True when any candidate has a cached record with
    /// <c>LookupStatus == "cleared"</c>. Used by the legacy
    /// <see cref="ResolveNormalizedSeriesAsync"/> fallback to honor an explicit
    /// user clear: a cleared series's external metadata must not be silently
    /// re-fetched on subsequent normalize runs.
    /// </summary>
    private async Task<bool> AnyCandidateClearedAsync(IReadOnlyList<string> candidates, CancellationToken cancellationToken)
    {
        if (_seriesMetadataCache is null || candidates.Count == 0) return false;
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try
            {
                var key = _seriesMetadataCache.NormalizeKey(candidate);
                if (string.IsNullOrWhiteSpace(key)) continue;
                var record = await _seriesMetadataCache.GetAsync(key, cancellationToken);
                if (record is not null
                    && string.Equals(record.LookupStatus, "cleared", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to consult series metadata cache for cleared-status check on {SeriesName}",
                    LoggingHelper.SanitizeForLog(candidate));
            }
        }
        return false;
    }
}
