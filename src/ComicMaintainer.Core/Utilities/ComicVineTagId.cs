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

    /// <summary>The issue id in <paramref name="notes"/>: <c>[CVDB12345]</c>, <c>[Issue ID 12345]</c> or an issue page.</summary>
    public static int? FromNotes(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return null;
        }

        var token = NotesTokenPattern().Match(notes);
        return token.Success ? Id(token.Groups["id"].Value) : IssuePageId(notes);
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
    /// <paramref name="notes"/> recording issue <paramref name="issueId"/>: an id token already in it
    /// is kept, unless <paramref name="replace"/> is true, when it is changed to this issue (the file
    /// was filled from another issue before); otherwise <c>[CVDB12345]</c> is appended, after a space,
    /// so the notes a user or tagger wrote stay as they are.
    /// </summary>
    public static string WithIssueId(string? notes, int issueId, bool replace)
    {
        var token = $"[CVDB{issueId.ToString(CultureInfo.InvariantCulture)}]";
        if (string.IsNullOrWhiteSpace(notes))
        {
            return token;
        }

        if (NotesTokenPattern().IsMatch(notes))
        {
            return replace ? NotesTokenPattern().Replace(notes, token, 1) : notes;
        }

        return $"{notes.TrimEnd()} {token}";
    }

    private static int? Id(string digits)
        => int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;

    // "[CVDB12345]" or "[Issue ID 12345]", as ComicTagger writes them.
    [GeneratedRegex(@"\[\s*(?:CVDB\s*|Issue\s*ID\s*[:#]?\s*)(?<id>\d{1,9})\s*\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex NotesTokenPattern();

    // A ComicVine issue page: "https://comicvine.gamespot.com/batman-1/4000-12345/", "comicvine.com/issue/4000-12345".
    [GeneratedRegex(@"comicvine(?:\.gamespot)?\.com/\S*?(?<![\d-])4000-(?<id>\d{1,9})(?!\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex IssuePagePattern();

    // A ComicVine volume page: "https://comicvine.gamespot.com/batman/4050-796/".
    [GeneratedRegex(@"^(?:https?://)?(?:www\.)?comicvine(?:\.gamespot)?\.com/\S*?(?<![\d-])4050-\d{1,9}/?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex VolumePagePattern();
}
