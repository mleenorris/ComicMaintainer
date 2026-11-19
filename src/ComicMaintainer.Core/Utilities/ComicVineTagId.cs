using System.Globalization;
using System.Text.RegularExpressions;

namespace ComicMaintainer.Core.Utilities;

/// <summary>
/// The ComicVine issue id taggers record in ComicInfo, which names the issue exactly, with no
/// matching. ComicTagger writes it into <c>&lt;Notes&gt;</c> as <c>[CVDB12345]</c> (older versions
/// <c>[Issue ID 12345]</c>), and ComicTagger and Mylar write the issue's ComicVine page into
/// <c>&lt;Web&gt;</c> (<c>https://comicvine.gamespot.com/batman-1/4000-12345/</c>). The application
/// writes both once it has filled an issue from ComicVine (see <see cref="WithIssueId"/>), so the
/// match survives a rename, a move or a lost database.
/// </summary>
public static partial class ComicVineTagId
{
    /// <summary>
    /// The ComicVine issue id <paramref name="notes"/> and <paramref name="web"/> name; null when
    /// neither names one, or they name different issues (one of them is wrong, so neither is trusted).
    /// </summary>
    public static int? FromTags(string? notes, string? web)
    {
        var fromNotes = FromNotes(notes);
        var fromWeb = IssuePageId(web);
        if (fromNotes is { } a && fromWeb is { } b && a != b)
        {
            return null;
        }

        return fromNotes ?? fromWeb;
    }

    /// <summary>
    /// The issue id in <paramref name="notes"/>: <c>[CVDB12345]</c>, <c>[Issue ID 12345]</c> or an
    /// issue page. ComicTagger writes <c>[Issue ID 12345]</c> for every source it tags from, so that
    /// one counts only when the notes say the information came from Comic Vine, or name no source.
    /// </summary>
    public static int? FromNotes(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return null;
        }

        return ComicVineToken(notes) is { } token ? Id(token.Groups["id"].Value) : IssuePageId(notes);
    }

    /// <summary>The issue id of a ComicVine issue page in <paramref name="text"/> (<c>…/4000-12345/</c>); null otherwise.</summary>
    public static int? IssuePageId(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var page = IssuePagePattern().Match(text);
        return page.Success ? Id(page.Groups["id"].Value) : null;
    }

    /// <summary>True when <paramref name="web"/> is a ComicVine volume page (<c>…/4050-796/</c>), as the series' match writes it.</summary>
    public static bool IsVolumePage(string? web)
        => !string.IsNullOrWhiteSpace(web) && VolumePagePattern().IsMatch(web.Trim());

    /// <summary>
    /// What follows the id this application records after matching an issue by its series' names:
    /// its own guess, unlike a tagger's id. Kept in the archive, so it is known after a rename, a move
    /// or a lost database.
    /// </summary>
    public const string NameMatchMarker = "(matched by name in ComicMaintainer)";

    /// <summary>
    /// True when the ComicVine id in <paramref name="notes"/> is this application's own record of a
    /// name match (see <see cref="NameMatchMarker"/>), not a tagger's.
    /// </summary>
    public static bool IsNameMatch(string? notes)
        => !string.IsNullOrWhiteSpace(notes) && ComicVineToken(notes) is not null
           && notes.Contains(NameMatchMarker, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <paramref name="notes"/> recording issue <paramref name="issueId"/>: a ComicVine id already in
    /// it is kept, unless <paramref name="replace"/> is true, when it is changed to this issue (the file
    /// was filled from another issue before); otherwise <c>[CVDB12345]</c> is appended, after a space,
    /// so the notes a user or tagger wrote stay as they are. With <paramref name="matchedByName"/>,
    /// the id written is followed by <see cref="NameMatchMarker"/>; a tagger's id that is kept gets
    /// no marker.
    /// </summary>
    public static string WithIssueId(string? notes, int issueId, bool replace, bool matchedByName)
    {
        var token = $"[CVDB{issueId.ToString(CultureInfo.InvariantCulture)}]";
        var written = matchedByName ? $"{token} {NameMatchMarker}" : token;
        if (string.IsNullOrWhiteSpace(notes))
        {
            return written;
        }

        // Another source's id (Metron's "[Issue ID 12345]") is left alone.
        if (ComicVineToken(notes) is { } existing)
        {
            if (!replace)
            {
                return notes;
            }

            var replaced = string.Concat(notes.AsSpan(0, existing.Index), token, notes.AsSpan(existing.Index + existing.Length));
            var hasMarker = replaced.Contains(NameMatchMarker, StringComparison.OrdinalIgnoreCase);
            return matchedByName && !hasMarker
                ? replaced.Insert(existing.Index + token.Length, $" {NameMatchMarker}")
                : !matchedByName && hasMarker
                    ? MarkerPattern().Replace(replaced, string.Empty, 1)
                    : replaced;
        }

        return $"{notes.TrimEnd()} {written}";
    }

    /// <summary>
    /// <paramref name="web"/> naming issue <paramref name="issueId"/> at <paramref name="page"/>:
    /// the page when <paramref name="web"/> is empty or only the series' ComicVine page; with
    /// <paramref name="replace"/>, another ComicVine issue's page in it is changed to this one, and
    /// any other link beside it is kept. Otherwise <paramref name="web"/> as it is.
    /// </summary>
    public static string? WithIssuePage(string? web, string page, int issueId, bool replace)
    {
        if (string.IsNullOrWhiteSpace(web) || IsVolumePage(web))
        {
            return page;
        }

        if (replace && IssuePageId(web) is { } other && other != issueId)
        {
            return IssuePageUrlPattern().Replace(web, page, 1);
        }

        return web;
    }

    private static Match? ComicVineToken(string notes)
    {
        var cvdb = CvdbTokenPattern().Match(notes);
        if (cvdb.Success)
        {
            return cvdb;
        }

        var issueId = IssueIdTokenPattern().Match(notes);
        if (!issueId.Success)
        {
            return null;
        }

        // Only the source ComicTagger names counts, not "comicvine" said anywhere in the notes.
        var source = InfoFromPattern().Match(notes);
        return !source.Success || ComicVineSourcePattern().IsMatch(source.Groups["source"].Value) ? issueId : null;
    }

    private static int? Id(string digits)
        => int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;

    // "[CVDB12345]", ComicVine's own.
    [GeneratedRegex(@"\[\s*CVDB\s*(?<id>\d{1,9})\s*\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex CvdbTokenPattern();

    // "[Issue ID 12345]", as ComicTagger writes it for any source.
    [GeneratedRegex(@"\[\s*Issue\s*ID\s*[:#]?\s*(?<id>\d{1,9})\s*\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex IssueIdTokenPattern();

    // ComicTagger's "using info from Comic Vine".
    [GeneratedRegex(@"comic\s*vine", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ComicVineSourcePattern();

    // ComicTagger's "using info from <source> on <date>", naming where the tags came from.
    [GeneratedRegex(@"using\s+info\s+from\s+(?<source>[^.\[\]\r\n]*?)(?:\s+on\s|[.\[\]\r\n]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex InfoFromPattern();

    // The name match marker with the space before it, to take out.
    [GeneratedRegex(@"\s?\(matched by name in ComicMaintainer\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex MarkerPattern();

    // A whole ComicVine issue page link, to replace in Web.
    [GeneratedRegex(@"(?:https?://)?(?:www\.)?comicvine(?:\.gamespot)?\.com/\S*?(?<![\d-])4000-\d{1,9}/?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex IssuePageUrlPattern();

    // A ComicVine issue page: "https://comicvine.gamespot.com/batman-1/4000-12345/", "comicvine.com/issue/4000-12345".
    [GeneratedRegex(@"comicvine(?:\.gamespot)?\.com/\S*?(?<![\d-])4000-(?<id>\d{1,9})(?!\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex IssuePagePattern();

    // A ComicVine volume page: "https://comicvine.gamespot.com/batman/4050-796/".
    [GeneratedRegex(@"^(?:https?://)?(?:www\.)?comicvine(?:\.gamespot)?\.com/\S*?(?<![\d-])4050-\d{1,9}/?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex VolumePagePattern();
}
