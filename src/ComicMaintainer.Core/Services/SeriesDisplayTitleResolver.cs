using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Picks the title to display for a series given its cached record and the
/// global default preferred language. The rules are:
/// <list type="number">
///   <item>If the user has overridden the canonical title
///         (<c>IsUserCanonical</c> = true), always return it. Explicit user
///         choice wins over any language rule.</item>
///   <item>If a per-series preference (or, failing that, the global default)
///         matches one of the cached <see cref="LocalizedTitle"/> entries,
///         return the first matching title.</item>
///   <item>Otherwise fall back to <see cref="SeriesMetadataCacheRecord.CanonicalTitle"/>.</item>
/// </list>
/// </summary>
public static class SeriesDisplayTitleResolver
{
    /// <summary>
    /// Resolve the title that should appear in the library for the given
    /// record. <paramref name="globalDefaultLanguage"/> may be null/empty to
    /// indicate "no global default".
    /// </summary>
    public static string Resolve(SeriesMetadataCacheRecord record, string? globalDefaultLanguage)
    {
        ArgumentNullException.ThrowIfNull(record);

        // Explicit user override always wins, regardless of language rules.
        if (record.IsUserCanonical && !string.IsNullOrWhiteSpace(record.CanonicalTitle))
        {
            return record.CanonicalTitle;
        }

        var preferred = SeriesLanguagePreference.Normalize(record.PreferredLanguage)
                        ?? SeriesLanguagePreference.Normalize(globalDefaultLanguage);

        if (!string.IsNullOrWhiteSpace(preferred) && record.LocalizedTitles is { Count: > 0 })
        {
            foreach (var localized in record.LocalizedTitles)
            {
                if (string.IsNullOrWhiteSpace(localized.Title))
                {
                    continue;
                }
                if (SeriesLanguagePreference.Matches(preferred, localized.Language))
                {
                    return localized.Title;
                }
            }
        }

        return record.CanonicalTitle ?? string.Empty;
    }
}
