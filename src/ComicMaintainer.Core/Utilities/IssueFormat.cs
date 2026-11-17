namespace ComicMaintainer.Core.Utilities;

/// <summary>
/// The kinds of issue that have a numbering of their own, set in ComicInfo <c>&lt;Format&gt;</c>:
/// Annual #1, Special #1 and Giant-Size #1 are neither issue #1 nor each other. The series page
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

    /// <summary>The kinds, in the order the series page lists their sections after the regular issues.</summary>
    public static IReadOnlyList<string> Kinds { get; } = [Annual, Special, GiantSize];

    // Other spellings taggers write into <Format> for a kind.
    private static readonly Dictionary<string, string> OtherSpellings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Giant"] = GiantSize,
        ["Giant Size"] = GiantSize
    };

    /// <summary>
    /// The kind a <c>&lt;Format&gt;</c> value names, spelled as <see cref="Kinds"/> spell it
    /// (<c>annual</c> gives <c>Annual</c>, <c>Giant</c> gives <c>Giant-Size</c>); null for a
    /// blank value or any other format, such as <c>TPB</c> or <c>Digital</c>.
    /// </summary>
    public static string? FromFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return null;
        }

        var trimmed = format.Trim();
        return Kinds.FirstOrDefault(kind => string.Equals(kind, trimmed, StringComparison.OrdinalIgnoreCase))
               ?? OtherSpellings.GetValueOrDefault(trimmed);
    }

    /// <summary>
    /// The kind of an issue: the one its <c>&lt;Format&gt;</c> names; else <see cref="Annual"/>
    /// when its series, issue number or file name marks it as one (see
    /// <see cref="AnnualIssue.IsAnnual"/>), or its title names it as one (see
    /// <see cref="AnnualIssue.FromTitle"/>); else <see cref="GiantSize"/> when its series or file
    /// name (see <see cref="GiantSizeIssue.IsGiantSize"/>) or title marks it as one; else
    /// <see cref="Special"/> when its title names it as one (see <see cref="IssueKindWord.InTitle"/>).
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

        if (GiantSizeIssue.IsGiantSize(series, fileName) || IssueKindWord.GiantSize.InTitle(title, series, issue))
        {
            return GiantSize;
        }

        return IssueKindWord.Special.InTitle(title, series, issue) ? Special : null;
    }
}
