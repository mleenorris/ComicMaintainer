using System.Xml;

namespace ComicMaintainer.Core.Utilities;

/// <summary>
/// Cleans values taken from metadata providers (or passed back from a search result) before
/// they are stored or written into a ComicInfo document. Provider text is untrusted: it may
/// carry markup, characters XML cannot hold, or a link that is not a web address.
/// </summary>
public static class ProviderMetadataText
{
    /// <summary>Longest web address kept; matches the database column.</summary>
    public const int MaxSiteUrlLength = 2048;

    /// <summary>Longest single genre kept.</summary>
    public const int MaxGenreLength = 64;

    /// <summary>Most genres kept for one series.</summary>
    public const int MaxGenres = 32;

    // Limits on what is looked at, since a manual match passes these back through the client:
    // how many values, and how long one may be, markup included, to be worth cleaning.
    private const int MaxGenreCandidates = MaxGenres * 8;
    private const int MaxRawGenreLength = MaxGenreLength * 8;

    /// <summary>Longest origin language tag kept; matches the database column.</summary>
    public const int MaxOriginLength = 16;

    /// <summary>
    /// The absolute http or https address in <paramref name="value"/>, or null for anything
    /// else (a relative path, <c>javascript:</c>, an over-long address). Spaces and other
    /// unsafe characters come back percent-encoded, so the result is one whitespace-free token.
    /// </summary>
    public static string? HttpUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host))
        {
            return null;
        }

        var absolute = uri.AbsoluteUri;
        return absolute.Length > MaxSiteUrlLength ? null : absolute;
    }

    /// <summary>
    /// Plain-text genre names without blanks or repeats, keeping their order and at most
    /// <see cref="MaxGenres"/> of them. A comma inside a name becomes a space, because ComicInfo
    /// separates genres with commas.
    /// </summary>
    public static List<string> Genres(IEnumerable<string?>? values)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in (values ?? Enumerable.Empty<string?>()).Take(MaxGenreCandidates))
        {
            if (value?.Length > MaxRawGenreLength)
            {
                continue;
            }

            // Cleaned after normalizing, because decoding entities can produce a comma or a
            // control character; normalized again to collapse the spaces that leaves.
            var genre = SynopsisTextNormalizer.Normalize(
                RemoveInvalidXmlCharacters(SynopsisTextNormalizer.Normalize(value))?.Replace(',', ' '));
            if (genre is null || genre.Length > MaxGenreLength || !seen.Add(genre))
            {
                continue;
            }

            result.Add(genre);
            if (result.Count == MaxGenres)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>
    /// The lowercase primary subtag of a language tag ("zh-hk" gives "zh"), or null when
    /// <paramref name="value"/> does not start with two to eight ASCII letters.
    /// </summary>
    public static string? PrimaryLanguage(string? value)
    {
        var primary = value?.Trim().Split('-', 2)[0].ToLowerInvariant();
        return primary is { Length: >= 2 and <= 8 } && primary.All(char.IsAsciiLetterLower) ? primary : null;
    }

    /// <summary>
    /// <paramref name="value"/> without the characters an XML document cannot hold (most control
    /// characters and unpaired surrogates), which would make saving the document throw.
    /// </summary>
    public static string? RemoveInvalidXmlCharacters(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var text = new System.Text.StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (XmlConvert.IsXmlChar(character))
            {
                text.Append(character);
            }
            else if (index + 1 < value.Length && XmlConvert.IsXmlSurrogatePair(value[index + 1], character))
            {
                text.Append(character).Append(value[++index]);
            }
        }

        return text.ToString();
    }
}
