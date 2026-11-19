using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Utilities;

/// <summary>
/// The ComicVine issue id recorded in ComicInfo, which names the issue exactly, with no matching.
/// ComicTagger writes it into <c>&lt;Notes&gt;</c> as <c>[CVDB12345]</c> (older versions
/// <c>[Issue ID 12345]</c>), and ComicTagger and Mylar write the issue's ComicVine page into
/// <c>&lt;Web&gt;</c> (<c>https://comicvine.gamespot.com/batman-1/4000-12345/</c>). The application
/// records the issue it filled a file from too (see <see cref="WithIssueId"/>), so the match
/// survives a rename, a move or a lost database; when it matched the issue by the series' names, it
/// says so (<see cref="NameMatchMarker"/>), as that is its own guess, not a tagger's.
/// </summary>
public static partial class ComicVineTagId
{
    /// <summary>
    /// What follows the id this application records after matching an issue by its series' names,
    /// right after the id's token.
    /// </summary>
    public const string NameMatchMarker = "(matched by name in ComicMaintainer)";

    /// <summary>
    /// The ComicVine issue <paramref name="notes"/> and <paramref name="web"/> name, and where the id
    /// comes from (see <see cref="ComicVineIdSource"/>). A tagger's id in the notes is preferred to
    /// this application's own name match. Null with <see cref="ComicVineIdSource.None"/> when neither
    /// names one, or the notes and the web link name different issues (one of them is wrong, so
    /// neither is trusted).
    /// </summary>
    public static (int? Id, ComicVineIdSource Source) Read(string? notes, string? web)
    {
        var fromWeb = IssuePageId(web);
        int? fromNotes = null;
        var source = ComicVineIdSource.None;
        if (!string.IsNullOrWhiteSpace(notes))
        {
            if (TaggerToken(notes) is { } tagger)
            {
                (fromNotes, source) = (Id(tagger.Groups["id"].Value), ComicVineIdSource.Tagger);
            }
            else if (OwnTokenPattern().Match(notes) is { Success: true } own)
            {
                (fromNotes, source) = (Id(own.Groups["id"].Value), ComicVineIdSource.NameMatch);
            }
            else if (IssuePageId(notes) is { } page)
            {
                (fromNotes, source) = (page, ComicVineIdSource.Tagger);
            }
        }

        if (fromNotes is { } a)
        {
            return fromWeb is { } b && a != b ? (null, ComicVineIdSource.None) : (a, source);
        }

        return fromWeb is { } webId ? (webId, ComicVineIdSource.WebOnly) : (null, ComicVineIdSource.None);
    }

    /// <summary>The issue id <see cref="Read"/> finds, without its source.</summary>
    public static int? FromTags(string? notes, string? web) => Read(notes, web).Id;

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
    /// <paramref name="notes"/> recording issue <paramref name="issueId"/>. This application's own
    /// earlier records are taken out first: they are superseded by whatever is recorded now. Then a
    /// tagger's ComicVine id is kept, unless <paramref name="replace"/> is true, when it is changed to
    /// this issue (it was shown wrong); otherwise <c>[CVDB12345]</c> is appended, after a space, so the
    /// notes a user or tagger wrote stay as they are. With <paramref name="matchedByName"/>, the id
    /// written is followed by <see cref="NameMatchMarker"/>. Another source's id (Metron's
    /// <c>[Issue ID 12345]</c>) is never touched.
    /// </summary>
    public static string WithIssueId(string? notes, int issueId, bool replace, bool matchedByName)
    {
        var token = $"[CVDB{issueId.ToString(CultureInfo.InvariantCulture)}]";
        var written = matchedByName ? $"{token} {NameMatchMarker}" : token;
        var rest = string.IsNullOrWhiteSpace(notes) ? string.Empty : OwnTokenWithSpacePattern().Replace(notes, string.Empty).Trim();
        if (rest.Length == 0)
        {
            return written;
        }

        if (TaggerToken(rest) is { } tagger)
        {
            if (!replace)
            {
                // A tagger's id names the issue: nothing of this application's is added beside it.
                return string.Equals(rest, notes?.Trim(), StringComparison.Ordinal) ? notes! : rest;
            }

            return new StringBuilder(rest).Remove(tagger.Index, tagger.Length).Insert(tagger.Index, written).ToString();
        }

        return $"{rest} {written}";
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

    // A tagger's ComicVine id in the notes: "[CVDB12345]" not followed by the name match marker, or
    // "[Issue ID 12345]" when ComicTagger's "using info from" clause names Comic Vine, or none.
    private static Match? TaggerToken(string notes)
    {
        foreach (Match cvdb in CvdbTokenPattern().Matches(notes))
        {
            if (!notes.AsSpan(cvdb.Index + cvdb.Length).TrimStart().StartsWith(NameMatchMarker, StringComparison.OrdinalIgnoreCase))
            {
                return cvdb;
            }
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

    // This application's record of a name match: "[CVDB12345] (matched by name in ComicMaintainer)".
    [GeneratedRegex(@"\[\s*CVDB\s*(?<id>\d{1,9})\s*\]\s*\(matched by name in ComicMaintainer\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex OwnTokenPattern();

    // The same, with the space before it, to take out.
    [GeneratedRegex(@"\s?\[\s*CVDB\s*\d{1,9}\s*\]\s*\(matched by name in ComicMaintainer\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex OwnTokenWithSpacePattern();

    // "[Issue ID 12345]", as ComicTagger writes it for any source.
    [GeneratedRegex(@"\[\s*Issue\s*ID\s*[:#]?\s*(?<id>\d{1,9})\s*\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex IssueIdTokenPattern();

    // ComicTagger's "using info from Comic Vine".
    [GeneratedRegex(@"comic\s*vine", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ComicVineSourcePattern();

    // ComicTagger's "using info from <source> on <date>", naming where the tags came from.
    [GeneratedRegex(@"using\s+info\s+from\s+(?<source>[^.\[\]\r\n]*?)(?:\s+on\s|[.\[\]\r\n]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex InfoFromPattern();

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
