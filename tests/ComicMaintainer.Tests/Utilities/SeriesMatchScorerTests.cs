using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.Tests.Utilities;

public class SeriesMatchScorerTests
{
    [Fact]
    public void Score_ExactCanonicalMatch_Returns100()
    {
        var score = SeriesMatchScorer.Score("Batman", "Batman", Array.Empty<string>());
        Assert.Equal(100d, score);
    }

    [Fact]
    public void Score_ExactCanonicalMatch_IgnoresCaseAndPunctuation()
    {
        var score = SeriesMatchScorer.Score("the dark knight!", "The Dark Knight", Array.Empty<string>());
        Assert.Equal(100d, score);
    }

    [Fact]
    public void Score_AliasExactMatch_ScoresHighButBelowCanonical()
    {
        var score = SeriesMatchScorer.Score("Dark Knight", "Batman", new[] { "Dark Knight" });
        Assert.InRange(score, 90d, 99d);
    }

    [Fact]
    public void Score_SubstringMatch_FallsInExpectedBand()
    {
        var score = SeriesMatchScorer.Score("Batman", "Batman: Year One", Array.Empty<string>());
        Assert.InRange(score, 70d, 90d);
    }

    [Fact]
    public void Score_FuzzyMatch_BelowSubstringBand()
    {
        var score = SeriesMatchScorer.Score("Batmen", "Batman", Array.Empty<string>());
        Assert.InRange(score, 1d, 70d);
    }

    [Fact]
    public void Score_NoSimilarity_ReturnsLow()
    {
        var score = SeriesMatchScorer.Score("Batman", "Naruto", Array.Empty<string>());
        Assert.InRange(score, 0d, 40d);
    }

    [Fact]
    public void Score_EmptyQuery_ReturnsZero()
    {
        Assert.Equal(0d, SeriesMatchScorer.Score("", "Batman", Array.Empty<string>()));
        Assert.Equal(0d, SeriesMatchScorer.Score("   ", "Batman", Array.Empty<string>()));
        Assert.Equal(0d, SeriesMatchScorer.Score(null, "Batman", Array.Empty<string>()));
    }

    [Fact]
    public void Score_EmptyCandidate_ReturnsZero()
    {
        Assert.Equal(0d, SeriesMatchScorer.Score("Batman", "", null));
    }

    [Fact]
    public void Score_PrefersBestOfCanonicalOrAlias()
    {
        // Canonical is a poor fuzzy match, but an alias matches exactly —
        // the scorer should pick the alias score.
        var score = SeriesMatchScorer.Score("Caped Crusader", "Batman", new[] { "Caped Crusader" });
        Assert.InRange(score, 90d, 99d);
    }
}
