using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.Tests.Utilities;

public class SeriesMatchScorerCoverageTests
{
    [Theory]
    [InlineData("!!!")]
    [InlineData("- : -")]
    public void Score_ReturnsZeroWhenTheQueryHasNoLettersOrDigits(string query)
    {
        Assert.Equal(0d, SeriesMatchScorer.Score(query, "!!!", new[] { "- : -" }));
    }

    [Fact]
    public void Score_IgnoresCandidatesWithNoLettersOrDigits()
    {
        Assert.Equal(0d, SeriesMatchScorer.Score("Batman", "???", new[] { "...", " ", "" }));
    }

    [Fact]
    public void Score_StillUsesAUsableAliasWhenTheCanonicalTitleIsOnlyPunctuation()
    {
        Assert.Equal(95d, SeriesMatchScorer.Score("Batman", "???", new[] { "BATMAN!" }));
    }
}
