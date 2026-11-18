namespace ComicMaintainer.Core.Utilities;

/// <summary>
/// Giant-Size issues: longer extra issues with a numbering of their own (<c>Giant-Size X-Men</c>
/// #1 is not X-Men #1). Marvel puts the words before the title, so a series or file name marks
/// one when it starts with "Giant-Size" or "Giant Size", or ends with it. "Giant" alone does not
/// count there: <c>Giant Days</c> and <c>The Iron Giant</c> are series of their own. A title
/// marks one too (see <see cref="IssueKindWord.GiantSize"/>).
/// </summary>
public static class GiantSizeIssue
{
    /// <summary>
    /// True when <paramref name="series"/>, or the series part of <paramref name="fileName"/>
    /// (see <see cref="SeriesFromPath.FromFileName"/>), names a Giant-Size issue.
    /// </summary>
    public static bool IsGiantSize(string? series, string? fileName)
        => IssueKindName.GiantSize.Names(series, fileName);

    /// <summary>
    /// True when <paramref name="series"/> starts with "Giant-Size" or "Giant Size", or ends with
    /// it before any trailing volume (<c>Giant-Size X-Men (1975)</c>, <c>X-Men Giant-Size</c>).
    /// </summary>
    public static bool IsGiantSizeSeries(string? series)
        => IssueKindName.GiantSize.NamesSeries(series);

    /// <summary>
    /// <paramref name="title"/> without its "Giant-Size" (<c>Giant-Size X-Men</c> gives
    /// <c>X-Men</c>), so the filename format's <c>{series}</c> and <c>{format}</c> do not name it
    /// twice. Unchanged when it holds nothing else.
    /// </summary>
    public static string WithoutGiantSize(string title)
        => IssueKindName.GiantSize.Without(title);
}
