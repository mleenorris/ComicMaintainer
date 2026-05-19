using System.Diagnostics.CodeAnalysis;

namespace ComicMaintainer.Core.Models;

/// <summary>
/// Helpers for working with the preferred-language setting on a series. The
/// preference accepts a small fixed allow-list of BCP-47 primary language codes
/// (<c>en</c>, <c>ja</c>, <c>ko</c>, <c>zh</c>); regional / script variants
/// (e.g. <c>ja-Latn</c>, <c>zh-hk</c>) match their primary subtag.
/// </summary>
public static class SeriesLanguagePreference
{
    /// <summary>The set of language codes the UI / API will accept.</summary>
    public static readonly IReadOnlyList<string> AllowedLanguages = new[] { "en", "ja", "ko", "zh" };

    /// <summary>
    /// Normalize a free-form language preference string to one of the allowed
    /// primary subtags. Returns null when the input is empty, whitespace, or
    /// not in the allow-list.
    /// </summary>
    public static string? Normalize(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        var primary = ExtractPrimarySubtag(language);
        return AllowedLanguages.Contains(primary, StringComparer.OrdinalIgnoreCase)
            ? primary
            : null;
    }

    /// <summary>
    /// Returns true when a title tag (e.g. <c>ja-Latn</c>) belongs to the
    /// preferred language family (e.g. <c>ja</c>). A null/empty tag never
    /// matches; this means untagged synonyms are never auto-picked by the
    /// preference rule.
    /// </summary>
    public static bool Matches(string? preferred, string? titleLanguage)
    {
        if (string.IsNullOrWhiteSpace(preferred) || string.IsNullOrWhiteSpace(titleLanguage))
        {
            return false;
        }

        return string.Equals(
            ExtractPrimarySubtag(preferred),
            ExtractPrimarySubtag(titleLanguage),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Validate that the supplied language is either null/empty (meaning
    /// "clear / auto") or one of the allowed primary subtags.
    /// </summary>
    [return: NotNullIfNotNull(nameof(language))]
    public static string? ValidateOrThrow(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        var normalized = Normalize(language);
        if (normalized is null)
        {
            throw new ArgumentException(
                $"Unsupported language '{language}'. Allowed values: {string.Join(", ", AllowedLanguages)}.",
                nameof(language));
        }
        return normalized;
    }

    private static string ExtractPrimarySubtag(string tag)
    {
        var idx = tag.IndexOf('-');
        var primary = idx < 0 ? tag : tag[..idx];
        return primary.Trim().ToLowerInvariant();
    }
}
