using ComicMaintainer.Core.Models;
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

    [Theory]
    [InlineData("Monster", "Monsters")]
    [InlineData("Batman", "Batman Beyond")]
    [InlineData("Naruto", "Boruto: Naruto Next Generations")]
    [InlineData("The Walking Dead", "Walking Dead")]
    public void SelectAutomaticMatch_RejectsNearMisses(string query, string candidate)
    {
        var candidates = new[] { new ExternalSeriesMetadata { CanonicalTitle = candidate } };

        Assert.Null(SeriesMatchScorer.SelectAutomaticMatch(candidates, query));
    }

    [Fact]
    public void SelectAutomaticMatch_TakesTheHighestScore_AndKeepsTheFirstOnATie()
    {
        var fuzzy = new ExternalSeriesMetadata { CanonicalTitle = "Berserk of Gluttony" };
        var alias = new ExternalSeriesMetadata { CanonicalTitle = "Kenpuu Denki", Aliases = new List<string> { "Berserk" } };
        var exact = new ExternalSeriesMetadata { CanonicalTitle = "BERSERK!" };
        var laterExact = new ExternalSeriesMetadata { CanonicalTitle = "Berserk" };

        var best = SeriesMatchScorer.SelectAutomaticMatch(new[] { fuzzy, alias, exact, laterExact }, "Berserk");

        Assert.NotNull(best);
        Assert.Same(exact, best.Value.Match);
        Assert.Equal(100d, best.Value.Score);
        Assert.Same(alias, SeriesMatchScorer.SelectAutomaticMatch(new[] { fuzzy, alias }, "Berserk")!.Value.Match);
    }

    [Theory]
    [InlineData(2023, 100d)]
    [InlineData(2022, 95d)]
    [InlineData(2024, 95d)]
    [InlineData(1960, 60d)]
    public void Score_QueryWithVolumeYear_ComparesTheStartYear(int startYear, double expected)
    {
        Assert.Equal(expected, SeriesMatchScorer.Score("Green Lantern (2023)", "Green Lantern", null, startYear));
    }

    [Fact]
    public void Score_QueryWithVolumeYear_UnknownStartYearStaysBelowTheAutomaticThreshold()
    {
        var score = SeriesMatchScorer.Score("Green Lantern (2023)", "Green Lantern", null, startYear: null);

        Assert.Equal(85d, score);
        Assert.True(score < SeriesMatchScorer.AutomaticMatchThreshold);
    }

    [Fact]
    public void Score_QueryWithVolumeYear_TitleCarryingTheYearStillMatchesExactly()
    {
        Assert.Equal(95d, SeriesMatchScorer.Score("Batman (1940)", "Batman", new[] { "Batman (1940)" }, startYear: null));
        Assert.Equal(100d, SeriesMatchScorer.Score("Batman (1940)", "Batman (1940)", null, startYear: 2016));
    }

    [Fact]
    public void Score_QueryWithoutYear_IgnoresTheStartYear()
    {
        Assert.Equal(100d, SeriesMatchScorer.Score("Green Lantern", "Green Lantern", null, startYear: 1960));
    }

    [Fact]
    public void SelectAutomaticMatch_QueryWithVolumeYear_PicksTheVolumeThatStartedThatYear()
    {
        var golden = new ExternalSeriesMetadata { CanonicalTitle = "Green Lantern", SourceId = "2839", StartYear = 1960 };
        var foreign = new ExternalSeriesMetadata { CanonicalTitle = "Green Lantern", SourceId = "142798", StartYear = 2019 };
        var unknown = new ExternalSeriesMetadata { CanonicalTitle = "Green Lantern", SourceId = "1" };
        var current = new ExternalSeriesMetadata { CanonicalTitle = "Green Lantern", SourceId = "150172", StartYear = 2023 };

        var best = SeriesMatchScorer.SelectAutomaticMatch(new[] { golden, foreign, unknown, current }, "Green Lantern (2023)");

        Assert.NotNull(best);
        Assert.Same(current, best.Value.Match);
        Assert.Null(SeriesMatchScorer.SelectAutomaticMatch(new[] { golden, foreign, unknown }, "Green Lantern (2023)"));
    }

    [Fact]
    public void SelectAutomaticMatch_NoCandidatesOrBlankQuery_ReturnsNull()
    {
        Assert.Null(SeriesMatchScorer.SelectAutomaticMatch(Array.Empty<ExternalSeriesMetadata>(), "Berserk"));
        Assert.Null(SeriesMatchScorer.SelectAutomaticMatch(new[] { new ExternalSeriesMetadata { CanonicalTitle = "Berserk" } }, " "));
    }
}
