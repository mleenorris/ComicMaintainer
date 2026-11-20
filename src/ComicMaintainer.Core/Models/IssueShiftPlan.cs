namespace ComicMaintainer.Core.Models;

/// <summary>What shifting the issue numbers of some files would do (see <c>IComicProcessorService.PlanIssueShiftAsync</c>).</summary>
/// <param name="OrderedFiles">
/// The files in the order they are shifted: the lowest new number first when the numbers go
/// down, the highest first when they go up, so each takes a name the one before it freed.
/// </param>
/// <param name="Problems">Why the files cannot all be shifted, one line each; empty when they can.</param>
public sealed record IssueShiftPlan(IReadOnlyList<string> OrderedFiles, IReadOnlyList<string> Problems)
{
    /// <summary>True when the shift can go ahead.</summary>
    public bool CanShift => Problems.Count == 0 && OrderedFiles.Count > 0;
}

/// <summary>The files' issue numbers cannot all be shifted; nothing was written.</summary>
public sealed class IssueShiftRefusedException : InvalidOperationException
{
    public IssueShiftRefusedException(IReadOnlyList<string> problems)
        : base(Describe(problems))
    {
        Problems = problems;
    }

    /// <summary>Why, one line each.</summary>
    public IReadOnlyList<string> Problems { get; }

    private static string Describe(IReadOnlyList<string> problems) => problems.Count switch
    {
        0 => "No issue number to shift",
        1 => problems[0],
        _ => $"{problems[0]} (and {problems.Count - 1} more)",
    };
}
