namespace ComicMaintainer.Core.Utilities;

/// <summary>
/// The kinds of issue that have a numbering of their own, set in ComicInfo <c>&lt;Format&gt;</c>:
/// Annual #1, Special #1, Giant-Size #1 and a One-Shot are neither issue #1 nor each other. The
/// user may name a kind of their own too, such as <c>Rebirth</c> for DC's Rebirth one-shots:
/// any <c>&lt;Format&gt;</c> value is a kind, except the words taggers write to describe an
/// edition of a regular issue (see <see cref="RegularFormats"/>). The series page
/// lists each kind in a section of its own, the missing-issues checks look at each apart, and
/// the filename format's <c>{format}</c> placeholder names it.
/// </summary>
public static class IssueFormat
{
    /// <summary>A yearly extra issue (see <see cref="AnnualIssue"/>).</summary>
    public const string Annual = "Annual";

    /// <summary>A special issue outside the main numbering.</summary>
    public const string Special = "Special";

    /// <summary>
    /// A longer extra issue (see <see cref="GiantSizeIssue"/>), spelled as Marvel prints it.
    /// <c>Giant</c> and <c>Giant Size</c> are read as it too.
    /// </summary>
    public const string GiantSize = "Giant-Size";

    /// <summary>
    /// A single issue published on its own, outside the series' numbering, spelled as ComicInfo
    /// and Marvel write it. <c>One Shot</c> and <c>Oneshot</c> are read as it too.
    /// </summary>
    public const string OneShot = "One-Shot";

    /// <summary>The kinds, in the order the series page lists their sections after the regular issues.</summary>
    public static IReadOnlyList<string> Kinds { get; } = [Annual, Special, GiantSize, OneShot];

    /// <summary>
    /// <c>&lt;Format&gt;</c> values that describe the edition of a regular issue, not a kind of its
    /// own: taggers such as ComicTagger and Mylar write them (<c>Series</c>, <c>Limited Series</c>,
    /// <c>Digital</c>, <c>TPB</c>), and they must not split a series into sections.
    /// </summary>
    public static IReadOnlySet<string> RegularFormats { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Series", "Main Series", "Ongoing", "Ongoing Series", "Limited Series", "Mini-Series", "Miniseries",
        "Maxi-Series", "Maxiseries", "Issue", "Comic", "Comic Book", "Chapter", "Volume", "Manga", "Manhwa",
        "Manhua", "Webtoon", "Web", "Web Comic", "Webcomic", "Digital", "Digital Chapter", "Print", "Magazine",
        "TPB", "Trade", "Trade Paperback", "Paperback", "HC", "Hardcover", "GN", "OGN", "Graphic Novel",
        "Omnibus", "Collection", "Collected Edition", "Tankobon", "Preview", "Variant", "Reprint",
    };

    // Other spellings taggers write into <Format> for a kind.
    private static readonly Dictionary<string, string> OtherSpellings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Giant"] = GiantSize,
        ["Giant Size"] = GiantSize,
        ["One Shot"] = OneShot,
        ["Oneshot"] = OneShot
    };

    /// <summary>
    /// The kind a <c>&lt;Format&gt;</c> value names: one of <see cref="Kinds"/>, spelled as they
    /// spell it (<c>annual</c> gives <c>Annual</c>, <c>Giant</c> gives <c>Giant-Size</c>), or a kind
    /// the user named, as written (<c>Rebirth</c>). Null for a blank value or one of
    /// <see cref="RegularFormats"/>, such as <c>TPB</c> or <c>Digital</c>.
    /// </summary>
    public static string? FromFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return null;
        }

        var trimmed = format.Trim();
        return Kinds.FirstOrDefault(kind => string.Equals(kind, trimmed, StringComparison.OrdinalIgnoreCase))
               ?? OtherSpellings.GetValueOrDefault(trimmed)
               ?? (RegularFormats.Contains(trimmed) ? null : trimmed);
    }

    /// <summary>
    /// The kind of an issue: the one its <c>&lt;Format&gt;</c> names; else <see cref="Annual"/>
    /// when its series, issue number or file name marks it as one (see
    /// <see cref="AnnualIssue.IsAnnual"/>), or its title names it as one (see
    /// <see cref="AnnualIssue.FromTitle"/>); else <see cref="GiantSize"/> or <see cref="OneShot"/>
    /// when its series or file name (see <see cref="IssueKindName"/>) or title marks it as one;
    /// else <see cref="Special"/> when its title names it as one (see <see cref="IssueKindWord.InTitle"/>).
    /// Null for a regular issue.
    /// </summary>
    public static string? Of(string? format, string? series, string? issue, string? fileName, string? title)
    {
        if (FromFormat(format) is { } named)
        {
            return named;
        }

        if (AnnualIssue.IsAnnual(series, issue, fileName) || AnnualIssue.FromTitle(title, issue))
        {
            return Annual;
        }

        if (IssueKindName.GiantSize.Names(series, fileName) || IssueKindWord.GiantSize.InTitle(title, series, issue))
        {
            return GiantSize;
        }

        if (IssueKindName.OneShot.Names(series, fileName) || IssueKindWord.OneShot.InTitle(title, series, issue))
        {
            return OneShot;
        }

        return IssueKindWord.Special.InTitle(title, series, issue) ? Special : null;
    }
}
