using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.Tests.Utilities;

/// <summary>A kind of issue read from a series or file name: Giant-Size and One-Shot.</summary>
public class IssueKindNameTests
{
    [Theory]
    [InlineData("X-Men: Red One-Shot", null, true)]
    [InlineData("Batman Oneshot (2016)", null, true)]
    [InlineData("Batman", "Batman (One-Shot).cbz", true)]
    [InlineData("Batman", "Batman One Shot 001.cbz", true)]
    [InlineData("Batman", "Batman 001 [One-Shot].cbz", true)]
    // The words inside a name, or near them, do not count.
    [InlineData("One Shot Kill", null, false)]
    [InlineData("One-Shot Wolverine", null, false)]
    [InlineData("Everyone Shot First", null, false)]
    [InlineData("Batman", "Batman 001 (2016).cbz", false)]
    [InlineData(null, null, false)]
    public void OneShot_NamesTheKindAtEitherEndOrInBrackets(string? series, string? fileName, bool expected)
    {
        Assert.Equal(expected, IssueKindName.OneShot.Names(series, fileName));
    }

    [Theory]
    [InlineData("X-Men: Red One-Shot", "X-Men: Red")]
    [InlineData("One-Shot", "One-Shot")]
    public void OneShot_Without_DropsTheWords(string title, string expected)
    {
        Assert.Equal(expected, IssueKindName.OneShot.Without(title));
    }

    [Fact]
    public void GiantSize_KeepsItsRules()
    {
        Assert.True(IssueKindName.GiantSize.Names("Giant-Size X-Men (1975)", null));
        Assert.False(IssueKindName.GiantSize.Names("Giant Days", null));
        Assert.Equal("X-Men", IssueKindName.GiantSize.Without("Giant-Size X-Men"));
    }

    [Theory]
    // <Format> first; then the names and the title.
    [InlineData(null, "X-Men: Red One-Shot", "1", "x.cbz", null, "One-Shot")]
    [InlineData(null, "Batman", "1", "Batman (One-Shot).cbz", null, "One-Shot")]
    [InlineData(null, "Batman", "1", "Batman - Chapter 0001.cbz", "One-Shot 1", "One-Shot")]
    [InlineData(null, "Batman", "1", "Batman - Chapter 0001.cbz", "Everyone Shot First", null)]
    [InlineData("Annual", "X-Men: Red One-Shot", "1", "x.cbz", null, "Annual")]
    public void IssueFormatOf_RecognisesAOneShot(string? format, string series, string issue, string fileName, string? title, string? expected)
    {
        Assert.Equal(expected, IssueFormat.Of(format, series, issue, fileName, title));
    }
}
