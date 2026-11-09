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
    // [label](https://...) in MangaDex descriptions. Handled first, because the BBCode pattern
    // would otherwise take the label for a tag. Brackets and parentheses are excluded from the
    // repeats so a long run of them stays linear.
    private static readonly Regex MarkdownLinkRegex = new(@"\[([^\[\]]*)\]\([^()]*\)", RegexOptions.Compiled);

    // What is left of such a link in a synopsis cached before links were handled.
    private static readonly Regex BareLinkRegex = new(@"\(\s*https?://[^()\s]*\s*\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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

        var text = MarkdownLinkRegex.Replace(raw, "$1");

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

    /// <summary>Longest synopsis written into a ComicInfo Summary; every issue gets a copy.</summary>
    public const int MaxComicInfoSummaryLength = 2000;

    // How much text the Markdown patterns look at. Anything beyond it is cut off anyway.
    private const int MaxMarkupScanLength = MaxComicInfoSummaryLength * 4;


    // A horizontal rule, which is left inline once line breaks are collapsed.
    private static readonly Regex MarkdownRuleRegex = new(@"(?<=^|\s)(?:-{3,}|_{3,}|\*{3,})(?=\s|$)", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    // __bold__, **bold**, ~~struck~~ and AniList's ~!spoiler!~ markers.
    private static readonly Regex MarkdownEmphasisRegex = new(@"\*\*|__|~~|~!|!~", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    // *italic* and _italic_, only where the marker hugs a word so snake_case and "2 * 3" stay.
    private static readonly Regex MarkdownItalicRegex = new(@"(?<![\w*_])[*_](?=\S)(.+?)(?<=\S)[*_](?![\w*_])", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    // A trailing credit such as (Source: MangaUpdates).
    private static readonly Regex SourceNoteRegex = new(@"\(\s*source\s*:[^)]*\)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    /// <summary>
    /// A provider description as the text of a ComicInfo Summary: HTML, BBCode and Markdown
    /// removed, characters XML cannot hold dropped, and at most
    /// <see cref="MaxComicInfoSummaryLength"/> characters, cut at a word where possible.
    /// Returns null when no text remains.
    /// </summary>
    public static string? ToComicInfoSummary(string? raw)
    {
        // Entities are decoded after tags are stripped, so text that was escaped twice can
        // come back as markup; a second pass removes it.
        var text = Normalize(Normalize(raw));
        if (text is null)
        {
            return null;
        }

        if (text.Length > MaxMarkupScanLength)
        {
            text = text[..MaxMarkupScanLength];
        }

        text = BareLinkRegex.Replace(text, " ");
        text = MarkdownRuleRegex.Replace(text, " ");
        text = MarkdownEmphasisRegex.Replace(text, string.Empty);
        text = MarkdownItalicRegex.Replace(text, "$1");
        text = SourceNoteRegex.Replace(text, string.Empty);

        // Last, because decoding an entity such as &#1; can produce these characters.
        text = ProviderMetadataText.RemoveInvalidXmlCharacters(text)!;
        text = WhitespaceRegex.Replace(text, " ").Trim();
        if (text.Length == 0)
        {
            return null;
        }

        return text.Length <= MaxComicInfoSummaryLength ? text : Truncate(text);
    }

    private static string Truncate(string text)
    {
        const string Ellipsis = "...";
        var limit = MaxComicInfoSummaryLength - Ellipsis.Length;

        // Do not leave half of a surrogate pair at the cut.
        if (char.IsHighSurrogate(text[limit - 1]))
        {
            limit--;
        }

        var cut = text.LastIndexOf(' ', limit);
        var head = text[..(cut > limit / 2 ? cut : limit)].TrimEnd();
        return head + Ellipsis;
    }
}
