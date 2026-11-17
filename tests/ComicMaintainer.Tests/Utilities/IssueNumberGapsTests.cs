using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.Tests.Utilities;

public class IssueNumberGapsTests
{
    [Fact]
    public void HighestCounted_NoNumbers_IsNull()
    {
        Assert.Null(IssueNumberGaps.HighestCounted([]));
    }

    [Fact]
    public void HighestCounted_OneNumbering_IsItsHighestNumber()
    {
        Assert.Equal(40, IssueNumberGaps.HighestCounted([1, 3, 40, 3]));
    }

    [Fact]
    public void HighestCounted_AMillionthIssue_IsLeftOut()
    {
        // Nightwing: about 160 issues and DC's #1,000,000.
        var numbers = Enumerable.Range(1, 160).Select(n => (long)n).Append(1_000_000);

        Assert.Equal(160, IssueNumberGaps.HighestCounted(numbers));
    }

    [Fact]
    public void HighestCounted_AnIssueNumberedByItsYear_IsLeftOut()
    {
        var numbers = Enumerable.Range(1, 190).Select(n => (long)n).Append(2021);

        Assert.Equal(190, IssueNumberGaps.HighestCounted(numbers));
    }

    [Fact]
    public void HighestCounted_AJumpUpToTheLimit_StaysInTheRun()
    {
        Assert.Equal(1001, IssueNumberGaps.HighestCounted([1, 1001]));
        Assert.Equal(1, IssueNumberGaps.HighestCounted([1, 1002]));
    }

    [Fact]
    public void HighestCounted_ALongRun_AllowsALongerJump()
    {
        // 300 numbers: a step of up to 1500 stays in the run.
        var numbers = Enumerable.Range(1, 299).Select(n => (long)n).Append(1_799);

        Assert.Equal(1_799, IssueNumberGaps.HighestCounted(numbers));
    }

    [Fact]
    public void HighestCounted_ALargerClusterAboveASmallerOne_IsTheRun()
    {
        // #1 and #2000-#2010: the gaps up to #2000 are real.
        var numbers = Enumerable.Range(2000, 11).Select(n => (long)n).Prepend(1);

        Assert.Equal(2010, IssueNumberGaps.HighestCounted(numbers));
    }

    [Fact]
    public void HighestCounted_ClustersOfOneSize_TakeTheLowest()
    {
        Assert.Equal(2, IssueNumberGaps.HighestCounted([1, 2, 5000, 5001]));
    }
}
