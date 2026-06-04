using System.Net;
using System.Text.RegularExpressions;

namespace ComicMaintainer.Core.Utilities;

/// <summary>
/// Converts provider-supplied series descriptions (which may contain HTML or
/// AniList-style BBCode plus source notes) into a clean plain-text synopsis
/// suitable for display at the top of the series page.
/// </summary>
public static class SynopsisTextNormalizer
{
    // Strips HTML tags (e.g. ComicVine <p>, MangaDex inline markup).
    private static readonly Regex HtmlTagRegex = new("<[^>]+>", RegexOptions.Compiled);

    // Strips AniList BBCode tags such as [i], [/i], [b], [spoiler], [source].
    private static readonly Regex BbCodeTagRegex = new(@"\[/?[a-zA-Z][^\]]*\]", RegexOptions.Compiled);

    // Collapses runs of whitespace (including the newlines BBCode/HTML leave behind).
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Returns a trimmed plain-text synopsis, or <c>null</c> when the input is
    /// null, empty, or contains no meaningful text after stripping markup.
    /// </summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = raw;

        // Convert explicit line breaks to spaces before stripping tags so words
        // on adjacent lines don't run together.
        text = Regex.Replace(text, @"<br\s*/?>", " ", RegexOptions.IgnoreCase);

        text = HtmlTagRegex.Replace(text, " ");
        text = BbCodeTagRegex.Replace(text, " ");

        // Decode HTML entities (e.g. &amp;, &#39;, &quot;).
        text = WebUtility.HtmlDecode(text);

        text = WhitespaceRegex.Replace(text, " ").Trim();

        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
