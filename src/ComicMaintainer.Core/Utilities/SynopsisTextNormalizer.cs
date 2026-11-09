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
    /// <summary>Longest synopsis written into a ComicInfo Summary; every issue gets a copy.</summary>
    public const int MaxComicInfoSummaryLength = 2000;

    /// <summary>Longest synopsis kept; it is stored per series and sent with every library page.</summary>
    public const int MaxSynopsisLength = MaxComicInfoSummaryLength * 4;

    // How much of a description is looked at. The text is untrusted (a manual match passes it
    // back through the client), and past this point it would be cut off anyway.
    private const int MaxRawLength = MaxSynopsisLength * 8;

    // [label](https://...) in MangaDex descriptions. Handled first, because the BBCode pattern
    // would otherwise take the label for a tag. Brackets and parentheses are excluded from the
    // repeats so a long run of them stays linear.
    private static readonly Regex MarkdownLinkRegex = new(@"\[([^\[\]]*)\]\([^()]*\)", RegexOptions.Compiled);

    // What is left of such a link in a synopsis cached before links were handled.
    private static readonly Regex BareLinkRegex = new(@"\(\s*https?://[^()\s]*\s*\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Strips HTML tags (e.g. ComicVine <p>, MangaDex inline markup). An opening bracket ends the
    // attempt, so a run of them cannot make each one scan the rest of the text.
    private static readonly Regex HtmlTagRegex = new("<[^<>]+>", RegexOptions.Compiled);

    // Strips AniList BBCode tags such as [i], [/i], [b], [spoiler], [source]. Linear for the
    // same reason as the HTML pattern.
    private static readonly Regex BbCodeTagRegex = new(@"\[/?[a-zA-Z][^\[\]]*\]", RegexOptions.Compiled);

    // Collapses runs of whitespace (including the newlines BBCode/HTML leave behind).
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    // The marker of a list item.
    private static readonly Regex ListMarkerRegex = new(@"^[ \t]*[-*+][ \t]+", RegexOptions.Compiled | RegexOptions.Multiline);

    // A line of a links footer: a list item holding a link, or nothing but links.
    private static readonly Regex FooterLinkLineRegex = new(
        @"^(?:[-*+]\s.*\]\(https?://.*|(?:\[[^\[\]]*\]\([^()]*\)[\s|,/-]*)+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // The rule or "Links:" heading above such lines.
    private static readonly Regex FooterHeadingLineRegex = new(
        @"^(?:[-_*]{3,}|[*_#\s]*(?:external\s+)?links?[*_\s:]*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // A links footer in a synopsis cached before footers were removed, by now on one line:
    // "Links: - MU - Raw". The colon and bullet keep it from matching a sentence about links.
    private static readonly Regex CachedLinksFooterRegex = new(
        @"(?:^|\s)[*_]{0,2}(?:external\s+)?links?[*_]{0,2}\s*:[*_]{0,2}\s+[-*+]\s.*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));

    // AniList's ~!hidden text!~. Readers show a Summary openly, so what the provider hides is dropped.
    private static readonly Regex SpoilerRegex = new("~!.*?!~", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    // A horizontal rule, which is left inline once line breaks are collapsed.
    private static readonly Regex MarkdownRuleRegex = new(@"(?<=^|\s)(?:-{3,}|_{3,}|\*{3,})(?=\s|$)", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    // __bold__, **bold**, ~~struck~~ and any leftover ~!spoiler!~ marker.
    private static readonly Regex MarkdownEmphasisRegex = new(@"\*\*|__|~~|~!|!~", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    // *italic* and _italic_, only where the marker hugs a word so snake_case and "2 * 3" stay.
    private static readonly Regex MarkdownItalicRegex = new(@"(?<![\w*_])[*_](?=\S)(.+?)(?<=\S)[*_](?![\w*_])", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    // A trailing credit such as (Source: MangaUpdates).
    private static readonly Regex SourceNoteRegex = new(@"\(\s*source\s*:[^)]*\)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    /// <summary>
    /// Returns a trimmed plain-text synopsis of at most <see cref="MaxSynopsisLength"/>
    /// characters, without a trailing block of links or list markers, or <c>null</c> when the
    /// input is null, empty, or contains no meaningful text after stripping markup.
    /// </summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = raw.Length <= MaxRawLength ? raw : raw[..CutPoint(raw, MaxRawLength)];

        // Explicit line breaks become newlines, so words on adjacent lines don't run together
        // and the steps that go line by line see them.
        text = Regex.Replace(text, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);

        text = RemoveLinksFooter(text);
        text = ListMarkerRegex.Replace(text, string.Empty);
        text = MarkdownLinkRegex.Replace(text, "$1");

        text = HtmlTagRegex.Replace(text, " ");
        text = BbCodeTagRegex.Replace(text, " ");

        // Decode HTML entities (e.g. &amp;, &#39;, &quot;).
        text = WebUtility.HtmlDecode(text);

        text = WhitespaceRegex.Replace(text, " ").Trim();

        if (text.Length == 0)
        {
            return null;
        }

        return text.Length <= MaxSynopsisLength ? text : Truncate(text, MaxSynopsisLength);
    }

    /// <summary>
    /// A provider description as the text of a ComicInfo Summary: HTML, BBCode and Markdown
    /// removed, text the provider marks as a spoiler left out, characters XML cannot hold
    /// dropped, and at most <see cref="MaxComicInfoSummaryLength"/> characters, cut at a word
    /// where possible. Returns null when no text remains.
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

        text = SpoilerRegex.Replace(text, " ");
        text = CachedLinksFooterRegex.Replace(text, string.Empty);
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

        return text.Length <= MaxComicInfoSummaryLength ? text : Truncate(text, MaxComicInfoSummaryLength);
    }

    /// <summary>
    /// <paramref name="text"/> without the block of links MangaDex descriptions often end with.
    /// Must run while the links and line breaks are still there.
    /// </summary>
    private static string RemoveLinksFooter(string text)
    {
        var lines = text.Split('\n');
        var kept = lines.Length;
        var sawLink = false;
        for (; kept > 0; kept--)
        {
            var line = lines[kept - 1].Trim();
            if (FooterLinkLineRegex.IsMatch(line))
            {
                sawLink = true;
            }
            else if (line.Length > 0 && !FooterHeadingLineRegex.IsMatch(line))
            {
                break;
            }
        }

        return sawLink ? string.Join('\n', lines, 0, kept) : text;
    }

    private static string Truncate(string text, int maxLength)
    {
        const string Ellipsis = "...";
        var limit = CutPoint(text, maxLength - Ellipsis.Length);
        var cut = text.LastIndexOf(' ', limit);
        var head = text[..(cut > limit / 2 ? cut : limit)].TrimEnd();
        return head + Ellipsis;
    }

    /// <summary>
    /// Where to cut <paramref name="text"/> for at most <paramref name="length"/> characters
    /// without leaving half of a surrogate pair.
    /// </summary>
    private static int CutPoint(string text, int length) =>
        char.IsHighSurrogate(text[length - 1]) ? length - 1 : length;
}
