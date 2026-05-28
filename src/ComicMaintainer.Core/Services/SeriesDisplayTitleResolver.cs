using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Picks the title to display for a series given its cached record and the
/// global default preferred language. The rules are:
/// <list type="number">
///   <item>If the user has overridden the canonical title
///         (<c>IsUserCanonical</c> = true), always return it. Explicit user
///         choice wins over any language rule.</item>
///   <item>If the record has a non-empty <see cref="SeriesMetadataCacheRecord.PinnedLocalizedTitle"/>,
///         return it verbatim. This is the user's "pin this exact title"
///         override; it wins over the language-preference rule but loses to
///         <c>IsUserCanonical</c>.</item>
///   <item>If a per-series preference (or, failing that, the global default)
///         matches one of the cached <see cref="LocalizedTitle"/> entries,
///         return the first matching title.</item>
///   <item>Otherwise fall back to <see cref="SeriesMetadataCacheRecord.CanonicalTitle"/>.</item>
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
    /// Resolve the title that should appear in the library for the given
    /// record. <paramref name="globalDefaultLanguage"/> may be null/empty to
    /// indicate "no global default".
    /// </summary>
    public static string Resolve(SeriesMetadataCacheRecord record, string? globalDefaultLanguage)
    {
        ArgumentNullException.ThrowIfNull(record);

        // PR 1 of 3 — when the new source-of-truth field is populated, it
        // is the answer. The defaulter (or, for UserSelected rows, the
        // user's explicit pick) has already encoded the precedence rules
        // below. We keep the legacy chain as a safety net for pre-migration
        // rows that haven't been backfilled yet; PR 3 removes it.
        if (!string.IsNullOrWhiteSpace(record.SeriesName))
        {
            return record.SeriesName;
        }

        // Explicit user override always wins, regardless of language rules.
        if (record.IsUserCanonical && !string.IsNullOrWhiteSpace(record.CanonicalTitle))
        {
            return record.CanonicalTitle;
        }

        // User-pinned localized title wins over the language-preference rule.
        // Represents an explicit "always show this exact title for this
        // series" choice — e.g. the romaji variant — that should not be
        // displaced when the user changes their general language preference.
        if (!string.IsNullOrWhiteSpace(record.PinnedLocalizedTitle))
        {
            return record.PinnedLocalizedTitle;
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
