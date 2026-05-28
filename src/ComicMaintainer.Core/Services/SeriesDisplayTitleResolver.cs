using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Picks the single authoritative series name for a cached record. This is
/// the one rule used everywhere — the library display title, the value
/// written into per-file ComicInfo.xml <c>&lt;Series&gt;</c>, and the
/// metadata database — so the website and on-disk metadata can never
/// disagree. The rules are:
/// <list type="number">
///   <item>If the record has an explicit user-selected
///         <see cref="SeriesMetadataCacheRecord.SeriesName"/> (the "pinned"
///         name), return it verbatim. This is sticky: it wins over the
///         language-preference rule and survives refreshes until the user
///         picks a different name or reverts to automatic.</item>
///   <item>Otherwise, if a per-series preference (or, failing that, the
///         global default) matches one of the cached
///         <see cref="LocalizedTitle"/> entries, return the first matching
///         title.</item>
///   <item>Otherwise fall back to
///         <see cref="SeriesMetadataCacheRecord.CanonicalTitle"/>.</item>
/// </list>
/// <para>
/// The same resolver is used by <see cref="ComicProcessorService"/> when
/// rewriting per-file ComicInfo.xml <c>&lt;Series&gt;</c> metadata, so the
/// on-disk value stays in sync with the library's displayed title.
/// </para>
/// </summary>
public static class SeriesDisplayTitleResolver
{
    /// <summary>
    /// Resolve the series name for the given record.
    /// <paramref name="globalDefaultLanguage"/> may be null/empty to indicate
    /// "no global default".
    /// </summary>
    public static string Resolve(SeriesMetadataCacheRecord record, string? globalDefaultLanguage)
    {
        ArgumentNullException.ThrowIfNull(record);

        // Explicit user-selected (pinned) name always wins.
        if (!string.IsNullOrWhiteSpace(record.SeriesName))
        {
            return record.SeriesName.Trim();
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
