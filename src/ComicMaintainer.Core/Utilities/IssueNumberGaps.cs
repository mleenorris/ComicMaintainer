namespace ComicMaintainer.Core.Utilities;

/// <summary>
/// How far up a run's numbering the missing-issues checks look for gaps. A number far beyond
/// the rest of its run does not mark where the run ends: DC's Nightwing #1,000,000 in a series
/// of about 160 issues, or an issue numbered by its year (#2021) among issues below 200.
/// Counting up to such a number reported nearly a million missing issues.
/// </summary>
/// <remarks>
/// The rule, which the series page shares (<c>highestCountedIssueNumber</c> in
/// <c>js/app/series.js</c>):
/// <list type="number">
/// <item>Sort the run's distinct whole issue numbers.</item>
/// <item>Split them into clusters wherever a number is more than
/// <see cref="MinOutlierJump"/> above the one before it, or <see cref="OutlierJumpPerIssue"/>
/// times the count of distinct numbers when that is more (a long run can have longer gaps).</item>
/// <item>The cluster with the most numbers is the run; of clusters the same size, the lowest.
/// Gaps count from #1 up to its highest number, so a smaller cluster below it is still part of
/// the run and the gaps before it are real.</item>
/// <item>Clusters above it are outliers: their issues are listed, but leave no gap before them.</item>
/// </list>
/// </remarks>
public static class IssueNumberGaps
{
    /// <summary>The smallest step between two issue numbers that splits them into clusters.</summary>
    public const long MinOutlierJump = 1000;

    /// <summary>For a long run, the step that splits it grows by this much per distinct number.</summary>
    public const long OutlierJumpPerIssue = 5;

    /// <summary>
    /// The highest issue number gaps are counted up to (see the class remarks), or null when
    /// there are no numbers.
    /// </summary>
    public static long? HighestCounted(IEnumerable<long> numbers)
    {
        ArgumentNullException.ThrowIfNull(numbers);
        var sorted = numbers.Distinct().Order().ToList();
        if (sorted.Count == 0)
        {
            return null;
        }

        var jump = Math.Max(MinOutlierJump, OutlierJumpPerIssue * sorted.Count);
        var start = 0;
        var largest = 0;
        var highest = sorted[0];
        for (var i = 1; i <= sorted.Count; i++)
        {
            if (i < sorted.Count && sorted[i] - sorted[i - 1] <= jump)
            {
                continue;
            }

            // sorted[start..i] is one cluster.
            if (i - start > largest)
            {
                largest = i - start;
                highest = sorted[i - 1];
            }

            start = i;
        }

        return highest;
    }
}
