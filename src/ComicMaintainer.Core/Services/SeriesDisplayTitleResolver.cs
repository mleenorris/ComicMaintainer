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
///   <item>For an English (<c>en</c>) preference only, fall through to
///         provider/user aliases (and finally <c>CanonicalTitle</c>) and pick
///         the first entry that looks like Basic Latin text. This catches the
///         common case where a provider supplied English alias strings but did
///         not tag them with a BCP-47 language. CJK preferences
///         (<c>ja</c>/<c>ko</c>/<c>zh</c>) deliberately require the BCP-47 tag
///         so a Romaji / Latin-script alias is never mistakenly picked.</item>
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

        // Script-based fallback for English: providers (and user aliases)
        // routinely supply English alias strings with no language tag, so the
        // BCP-47 match above misses them. Pick the first alias / canonical
        // title that looks like Basic Latin text. We intentionally do *not*
        // do this for ja/ko/zh because Latin-script aliases (e.g. Romaji)
        // would falsely satisfy the heuristic.
        if (string.Equals(preferred, "en", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var candidate in EnumerateAliasCandidates(record))
            {
                if (LooksLikeBasicLatin(candidate))
                {
                    return candidate;
                }
            }
        }

        return record.CanonicalTitle ?? string.Empty;
    }

    private static IEnumerable<string> EnumerateAliasCandidates(SeriesMetadataCacheRecord record)
    {
        // User aliases first (most likely to reflect the user's preferred
        // wording), then provider aliases, then the canonical title as a
        // last-resort candidate before the unconditional canonical fallback.
        if (record.UserAliases is { Count: > 0 })
        {
            foreach (var alias in record.UserAliases)
            {
                if (!string.IsNullOrWhiteSpace(alias)) yield return alias;
            }
        }
        if (record.Aliases is { Count: > 0 })
        {
            foreach (var alias in record.Aliases)
            {
                if (!string.IsNullOrWhiteSpace(alias)) yield return alias;
            }
        }
        if (!string.IsNullOrWhiteSpace(record.CanonicalTitle))
        {
            yield return record.CanonicalTitle;
        }
    }

    /// <summary>
    /// True when <paramref name="value"/> contains at least one ASCII letter
    /// and no non-Latin script characters (Hiragana, Katakana, Hangul, CJK
    /// ideographs, etc.). Whitespace, digits, and common punctuation are
    /// allowed. Used as a low-confidence English-detection heuristic for
    /// untagged alias strings.
    /// </summary>
    private static bool LooksLikeBasicLatin(string value)
    {
        var sawAsciiLetter = false;
        foreach (var rune in value.EnumerateRunes())
        {
            var cp = rune.Value;

            // ASCII letters count as positive evidence of Latin script.
            if ((cp >= 'A' && cp <= 'Z') || (cp >= 'a' && cp <= 'z'))
            {
                sawAsciiLetter = true;
                continue;
            }

            // Allow digits, whitespace, and common punctuation anywhere in
            // the string (so e.g. "Solo Leveling: Ragnarok!" still qualifies).
            if (cp < 0x80)
            {
                continue;
            }

            // Any non-ASCII codepoint that belongs to a non-Latin script is
            // disqualifying. We treat the common CJK / Korean / Japanese
            // ranges explicitly so that Romaji-with-macrons style strings
            // (Latin-1 Supplement / Latin Extended) are still accepted.
            if (IsNonLatinScript(cp))
            {
                return false;
            }
        }

        return sawAsciiLetter;
    }

    private static bool IsNonLatinScript(int codepoint)
    {
        // Hiragana
        if (codepoint >= 0x3040 && codepoint <= 0x309F) return true;
        // Katakana (+ phonetic extensions)
        if (codepoint >= 0x30A0 && codepoint <= 0x30FF) return true;
        if (codepoint >= 0x31F0 && codepoint <= 0x31FF) return true;
        // Katakana half-width forms
        if (codepoint >= 0xFF65 && codepoint <= 0xFF9F) return true;
        // Hangul Jamo / Syllables / Compatibility Jamo
        if (codepoint >= 0x1100 && codepoint <= 0x11FF) return true;
        if (codepoint >= 0x3130 && codepoint <= 0x318F) return true;
        if (codepoint >= 0xA960 && codepoint <= 0xA97F) return true;
        if (codepoint >= 0xAC00 && codepoint <= 0xD7AF) return true;
        // CJK Unified Ideographs (BMP + common extensions)
        if (codepoint >= 0x3400 && codepoint <= 0x4DBF) return true;
        if (codepoint >= 0x4E00 && codepoint <= 0x9FFF) return true;
        if (codepoint >= 0xF900 && codepoint <= 0xFAFF) return true; // CJK Compat Ideographs
        if (codepoint >= 0x20000 && codepoint <= 0x2FFFF) return true; // SIP CJK ext B-F
        if (codepoint >= 0x30000 && codepoint <= 0x3134F) return true; // CJK ext G
        // CJK Symbols & Punctuation (e.g. 「」、。)
        if (codepoint >= 0x3000 && codepoint <= 0x303F) return true;
        // Fullwidth Forms (Latin / digits)
        if (codepoint >= 0xFF00 && codepoint <= 0xFF60) return true;

        return false;
    }
}
