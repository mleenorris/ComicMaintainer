using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.Tests.Utilities;

public class VolumeNumberingTests
{
    private sealed record Issue(string? Volume, string? Number, bool Annual = false, string? Kind = null);

    private static IReadOnlyList<VolumeNumbering.VolumeGroup<Issue>>? Groups(params Issue[] issues)
        => VolumeNumbering.SeparateRuns(
            issues,
            issue => ComicVolume.FromTag(issue.Volume),
            issue => issue.Number,
            issue => issue.Kind ?? (issue.Annual ? IssueFormat.Annual : null));

    [Fact]
    public void RestartingNumbering_IsGroupedByVolume_OldestFirst_UnknownLast()
    {
        var groups = Groups(
            new Issue("2005", "1"),
            new Issue(null, "7"),
            new Issue("1990", "1"),
            new Issue("1990", "2"),
            new Issue("2005", "2"));

        Assert.NotNull(groups);
        Assert.Equal(new int?[] { 1990, 2005, null }, groups!.Select(g => g.Volume.HasValue ? g.Volume.Value.Value : (int?)null).ToArray());
        Assert.Equal(new[] { "1", "2" }, groups[0].Items.Select(i => i.Number));
    }

    [Fact]
    public void NumberedVolumes_ComeBeforeStartYears()
    {
        var groups = Groups(new Issue("1990", "1"), new Issue("2", "1"), new Issue("1", "1"));

        Assert.Equal(new[] { "v1", "v2", "y1990" }, groups!.Select(g => g.Volume.GetValueOrDefault().Key));
    }

    [Fact]
    public void ContinuousNumberingAcrossVolumes_IsOneRun()
    {
        // A manga's tankōbon volumes: chapters keep counting from one volume to the next.
        Assert.Null(Groups(
            new Issue("1", "1"), new Issue("1", "2"),
            new Issue("2", "3"), new Issue("2", "4.5"),
            new Issue(null, "5")));
    }

    [Fact]
    public void FewerThanTwoKnownVolumes_IsOneRun()
    {
        Assert.Null(Groups(new Issue("1990", "1"), new Issue(null, "1"), new Issue(null, "2")));
        Assert.Null(Groups(new Issue(null, "1"), new Issue(null, "1")));
    }

    [Fact]
    public void ADecimalIssueSharesItsWholeNumber()
    {
        Assert.NotNull(Groups(new Issue("1990", "3"), new Issue("2005", "3.5")));
    }

    [Fact]
    public void Annuals_AreARunOfTheirOwn_AfterTheSeriesIssues()
    {
        var groups = Groups(
            new Issue(null, "1", Annual: true),
            new Issue(null, "1"),
            new Issue(null, "2"));

        Assert.NotNull(groups);
        Assert.Equal(new[] { false, true }, groups!.Select(g => g.IsAnnual));
        Assert.Equal(new[] { "1", "2" }, groups[0].Items.Select(i => i.Number));
        Assert.Null(groups[1].Volume);
    }

    [Fact]
    public void Annuals_AreSplitByVolumeToo_WhenTheirNumberingRestarts()
    {
        var groups = Groups(
            new Issue("1990", "1"), new Issue("2005", "1"),
            new Issue("1990", "1", Annual: true), new Issue("2005", "1", Annual: true));

        Assert.Equal(
            new[] { ("y1990", false), ("y2005", false), ("y1990", true), ("y2005", true) },
            groups!.Select(g => (g.Volume.GetValueOrDefault().Key, g.IsAnnual)).ToArray());
    }

    [Fact]
    public void Specials_AreARunOfTheirOwn_AfterTheAnnuals()
    {
        var groups = Groups(
            new Issue(null, "1", Kind: IssueFormat.Special),
            new Issue(null, "1", Annual: true),
            new Issue(null, "1"),
            new Issue(null, "2", Kind: "Special"));

        Assert.NotNull(groups);
        Assert.Equal(new string?[] { null, IssueFormat.Annual, IssueFormat.Special }, groups.Select(g => g.Kind));
        Assert.Equal(new[] { "1", "2" }, groups[2].Items.Select(i => i.Number));
        Assert.Equal(new[] { false, true, false }, groups.Select(g => g.IsAnnual));
    }

    [Fact]
    public void GiantSizeIssues_AreARunOfTheirOwn_AfterTheSpecials()
    {
        var groups = Groups(
            new Issue(null, "1", Kind: IssueFormat.GiantSize),
            new Issue(null, "1", Kind: IssueFormat.Special),
            new Issue(null, "1"),
            new Issue(null, "1", Annual: true),
            new Issue(null, "2025", Kind: IssueFormat.GiantSize));

        Assert.NotNull(groups);
        Assert.Equal(
            new string?[] { null, IssueFormat.Annual, IssueFormat.Special, IssueFormat.GiantSize },
            groups.Select(g => g.Kind));
        Assert.Equal(new[] { "1", "2025" }, groups[3].Items.Select(i => i.Number));
    }

    [Fact]
    public void AnUnknownKind_IsListedAfterTheKnownOnes()
    {
        var groups = Groups(
            new Issue(null, "1", Kind: "Preview"),
            new Issue(null, "1", Kind: IssueFormat.Special),
            new Issue(null, "1"));

        Assert.Equal(new string?[] { null, IssueFormat.Special, "Preview" }, groups!.Select(g => g.Kind));
    }

    [Fact]
    public void OnlyAnnuals_AreOneAnnualRun()
    {
        var groups = Groups(new Issue(null, "1", Annual: true), new Issue(null, "2", Annual: true));

        Assert.True(Assert.Single(groups!).IsAnnual);
    }

    [Theory]
    [InlineData("12", 12L)]
    [InlineData("012", 12L)]
    [InlineData("12.5", 12L)]
    [InlineData(" 0 ", 0L)]
    [InlineData("Annual", null)]
    [InlineData("-1", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void WholeIssueNumber_FloorsNumbersAndIgnoresLabels(string? issue, long? expected)
    {
        Assert.Equal(expected, VolumeNumbering.WholeIssueNumber(issue));
    }
}
