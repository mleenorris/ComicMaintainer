namespace ComicMaintainer.Core.ErrorReporting;

/// <summary>
/// The single repository automated error reports are filed against.
/// </summary>
/// <remarks>
/// Deliberately a constant rather than an operator setting. ComicMaintainer is
/// developed in one place, so every field error belongs in that issue tracker —
/// reports filed anywhere else reach nobody who can fix the defect. Fixing the
/// destination also removes a whole class of silent failure: an operator cannot
/// mistype a repository name, point reporting at a repository the token has no
/// access to, or have the destination drift away from the code that produced
/// the error. The token remains operator-supplied, so nothing is sent until an
/// operator explicitly opts in and provides one.
/// </remarks>
public static class ErrorReportingDestination
{
    /// <summary>Owner of the repository issues are filed in.</summary>
    public const string Owner = "mleenorris";

    /// <summary>Name of the repository issues are filed in.</summary>
    public const string Repo = "ComicMaintainer";

    /// <summary><c>owner/repo</c>, for display and log messages.</summary>
    public const string Slug = $"{Owner}/{Repo}";
}
