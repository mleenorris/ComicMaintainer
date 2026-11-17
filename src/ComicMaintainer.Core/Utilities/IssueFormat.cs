namespace ComicMaintainer.Core.Utilities;

/// <summary>
/// The kinds of issue that have a numbering of their own, set in ComicInfo <c>&lt;Format&gt;</c>:
/// Annual #1 and Special #1 are neither issue #1 nor each other. The series page lists each kind
/// in a section of its own, the missing-issues checks look at each apart, and the filename
/// format's <c>{format}</c> placeholder names it.
/// </summary>
public static class IssueFormat
{
    /// <summary>A yearly extra issue (see <see cref="AnnualIssue"/>).</summary>
    public const string Annual = "Annual";

    /// <summary>A special issue outside the main numbering.</summary>
    public const string Special = "Special";

    /// <summary>The kinds, in the order the series page lists their sections after the regular issues.</summary>
    public static IReadOnlyList<string> Kinds { get; } = [Annual, Special];

    /// <summary>
    /// The kind a <c>&lt;Format&gt;</c> value names, spelled as <see cref="Kinds"/> spell it
    /// (<c>annual</c> gives <c>Annual</c>); null for a blank value or any other format, such as
    /// <c>TPB</c> or <c>Digital</c>.
    /// </summary>
    public static string? FromFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return null;
        }

        var trimmed = format.Trim();
        return Kinds.FirstOrDefault(kind => string.Equals(kind, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The kind of an issue: the one its <c>&lt;Format&gt;</c> names, else <see cref="Annual"/>
    /// when its series, issue number or file name marks it as one (see
    /// <see cref="AnnualIssue.IsAnnual"/>), or its title names it as one (see
    /// <see cref="AnnualIssue.FromTitle"/>); null for a regular issue.
    /// </summary>
    public static string? Of(string? format, string? series, string? issue, string? fileName, string? title)
        => FromFormat(format) ??
           (AnnualIssue.IsAnnual(series, issue, fileName) || AnnualIssue.FromTitle(title, issue) ? Annual : null);
}
