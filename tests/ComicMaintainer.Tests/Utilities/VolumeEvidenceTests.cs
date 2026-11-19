using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.Tests.Utilities;

/// <summary>Weighing ComicVine volumes against what the library holds of a series volume.</summary>
public class VolumeEvidenceTests
{
    private static ExternalSeriesMetadata Volume(string id, int start, string publisher = "DC Comics", int? issues = 50, string name = "Batman")
        => new() { CanonicalTitle = name, StartYear = start, Publisher = publisher, SourceId = id, TotalChapters = issues };

    private static VolumeHoldings Held(int highest = 0, int? earliest = null, params string[] publishers)
        => new(publishers, earliest, highest, []);

    [Fact]
    public void Rank_ByStartYear_TakesTheSameYear_RulesOutOthers_AndPrefersTheLongestOfTheYear()
    {
        var ranked = VolumeEvidence.Rank(
            [Volume("1", 2016, issues: 158), Volume("2", 2016, issues: 1), Volume("3", 2015), Volume("4", 2011), Volume("5", 2016, name: "Batman Beyond")],
            "Batman", 2016, 0, null, Held());

        // The longest of 2016 is far longer than the one-shot beside it: +3. The one-shot loses 2.
        Assert.Equal(["1", "3", "2"], ranked.Select(candidate => candidate.Volume.SourceId));
        Assert.Contains("the longest volume of that name started in 2016", ranked[0].Reasons);
        Assert.Contains("a single issue: a one-shot or giveaway", ranked[2].Reasons);
        Assert.Equal((7d, 2d, 2d), (ranked[0].Score, ranked[1].Score, ranked[2].Score));
        Assert.Equal("1", VolumeEvidence.Decide(ranked).Choice!.Volume.SourceId);
    }

    [Fact]
    public void Rank_UsesThePublisher_TheIssueCount_TheEarliestYear_AndTheSeriesMatch()
    {
        var ranked = VolumeEvidence.Rank(
            [Volume("us", 2016, "DC Comics", 158), Volume("fr", 2016, "Urban Comics", 158), Volume("short", 2016, "DC Comics", 4)],
            "Batman", 2016, 0, "fr", Held(highest: 30, earliest: 2016, "DC Comics, Inc."));

        // The 4-issue volume cannot hold #30.
        Assert.Equal(["us", "fr"], ranked.Select(candidate => candidate.Volume.SourceId));
        // us: year 4, the longest of 2016 (the first of two of 158) 1, publisher 2, count 1, earliest 1;
        // fr: year 4, series match 3, publisher -3, count 1, earliest 1.
        Assert.Equal((9d, 6d), (ranked[0].Score, ranked[1].Score));
        Assert.Contains("published by Urban Comics, not DC Comics, Inc.", ranked[1].Reasons);
        Assert.Contains("the series' own ComicVine match", ranked[1].Reasons);
    }

    [Fact]
    public void Rank_ByNumber_FavoursThePublishersNthVolume_AndAVolumeStartedAfterTheIssues_LosesPoints()
    {
        var ranked = VolumeEvidence.Rank(
            [Volume("1940", 1940, issues: 713), Volume("2011", 2011, issues: 52), Volume("2016", 2016, issues: 158)],
            "Batman", 0, 3, null, Held(highest: 10, earliest: 2012));

        var third = ranked.Single(candidate => candidate.Volume.SourceId == "2016");
        Assert.Contains("the publisher's volume 3 of that name", third.Reasons);
        Assert.Contains("started in 2016, after your issues of 2012", third.Reasons);
        // 2016: Nth 4, count 1, too late -3 = 2; the others: count 1, earliest 1 = 2. Only the 2016
        // volume is named by the evidence, so it fits, but the dates say otherwise: the user decides.
        Assert.All(ranked, candidate => Assert.Equal(2d, candidate.Score));
        Assert.Equal((null, true), VolumeEvidence.Decide(ranked));
    }

    [Fact]
    public void Decide_TakesTheOnlyOrAClearlyBetterVolume_AndAsksWhenSeveralFit()
    {
        VolumeCandidate Scored(string id, double score, double identity = 4) => new(Volume(id, 2016), score, [], identity);

        Assert.Equal((null, false), VolumeEvidence.Decide([]));
        // Fitting is decided by the evidence that names a volume, not by the score.
        Assert.Equal((null, false), VolumeEvidence.Decide([Scored("a", 9, identity: 3)]));
        Assert.Equal("a", VolumeEvidence.Decide([Scored("a", 1)]).Choice!.Volume.SourceId);
        Assert.Equal("a", VolumeEvidence.Decide([Scored("a", 4)]).Choice!.Volume.SourceId);
        Assert.Equal("a", VolumeEvidence.Decide([Scored("a", 8), Scored("b", 5)]).Choice!.Volume.SourceId);
        Assert.Equal("a", VolumeEvidence.Decide([Scored("a", 8), Scored("b", 5, identity: 2)]).Choice!.Volume.SourceId);
        Assert.Equal((null, true), VolumeEvidence.Decide([Scored("a", 8), Scored("b", 6)]));
        // A volume that does not fit only stands in the way when it scores at least as high; one that
        // fits must be outscored by the margin.
        Assert.Equal((null, true), VolumeEvidence.Decide([Scored("a", 9, identity: 2), Scored("b", 5)]));
        Assert.Equal((null, true), VolumeEvidence.Decide([Scored("a", 5), Scored("b", 5, identity: 2)]));
        Assert.Equal("a", VolumeEvidence.Decide([Scored("a", 5), Scored("b", 4, identity: 2)]).Choice!.Volume.SourceId);
        // The evidence against the only fitting volume outweighs what names it.
        Assert.Equal((null, true), VolumeEvidence.Decide([Scored("a", 0)]));
    }

    [Fact]
    public void Decide_RealCases_ThatMustNotGoToReview()
    {
        // The series page is matched to Batman (1940); the files say Vol. 3, DC's 2016 volume.
        var numbered = VolumeEvidence.Rank(
            [Volume("1940", 1940, issues: 713), Volume("2011", 2011, issues: 52), Volume("2016", 2016, issues: 158)],
            "Batman", 0, 3, "1940", Held(highest: 10, earliest: 2016, "DC Comics"));
        Assert.Equal("2016", VolumeEvidence.Decide(numbered).Choice!.Volume.SourceId);

        // Marvel relaunches a year apart: Wolverine (2014) next to Wolverine (2013).
        var relaunch = VolumeEvidence.Rank(
            [Volume("2013", 2013, "Marvel", 13, "Wolverine"), Volume("2014", 2014, "Marvel", 12, "Wolverine")],
            "Wolverine", 2014, 0, null, Held(highest: 12, earliest: 2014, "Marvel"));
        Assert.Equal("2014", VolumeEvidence.Decide(relaunch).Choice!.Volume.SourceId);

        // A foreign edition of 2016 beside Marvel's own of 2015: the publisher outweighs the year.
        var foreign = VolumeEvidence.Rank(
            [Volume("panini", 2016, "Panini", 30, "Star Wars"), Volume("marvel", 2015, "Marvel", 75, "Star Wars")],
            "Star Wars", 2016, 0, null, Held(highest: 20, earliest: 2015, "Marvel"));
        Assert.Equal((null, true), VolumeEvidence.Decide(foreign));
    }

    [Fact]
    public void ReviewReason_SaysWhatCouldNotBeSettled()
    {
        VolumeCandidate Scored(string id, double score, double identity = 4) => new(Volume(id, 2016), score, [], identity);

        Assert.Equal("2 ComicVine volumes named Batman fit about equally well", VolumeEvidence.ReviewReason([Scored("a", 8), Scored("b", 6)], "Batman"));
        Assert.Equal("The evidence against the only ComicVine volume named Batman that fits outweighs what names it",
            VolumeEvidence.ReviewReason([Scored("a", 0)], "Batman"));
    }

    [Fact]
    public void CloseCalls_CountsTheVolumesAboutAsGoodAsTheBestFittingOne()
    {
        VolumeCandidate Scored(string id, double score, double identity = 4) => new(Volume(id, 2016), score, [], identity);

        Assert.Equal(0, VolumeEvidence.CloseCalls([Scored("a", 9, identity: 2)]));
        Assert.Equal(2, VolumeEvidence.CloseCalls([Scored("a", 8), Scored("b", 6), Scored("c", 1, identity: 0)]));
        Assert.Equal(3, VolumeEvidence.CloseCalls([Scored("a", 5), Scored("b", 6, identity: 0), Scored("c", 4)]));
    }

    [Fact]
    public void WithIssues_CoverYearsAndStoryTitles_TellVolumesApart()
    {
        var held = new VolumeHoldings(["DC Comics"], 2016, 3,
        [
            new HeldIssue("1", 2016, "I Am Gotham, Part One"),
            new HeldIssue("2", 2016, null),
            new HeldIssue("3", 2017, null),
            new HeldIssue("4", null, null),
        ]);
        var candidate = new VolumeCandidate(Volume("a", 2016), 5, ["started in 2016"]);

        var right = VolumeEvidence.WithIssues(candidate,
            [new ListedIssue("1", "2016-08-01", "I Am Gotham, Part One"), new ListedIssue("2", "2016-08-15", null), new ListedIssue("003", "2016-09-01", null)], held);
        Assert.Equal(5d + 4 + 1, right.Score);
        Assert.Contains("the cover years of 3 of 3 of your issues match", right.Reasons);
        Assert.Contains("a story title matches", right.Reasons);

        var wrong = VolumeEvidence.WithIssues(candidate,
            [new ListedIssue("1", "1940-04-01", "The Legend of the Batman"), new ListedIssue("2", "1940-06-01", null), new ListedIssue("3", "1940-08-01", null)], held);
        Assert.Equal(5d - 4, wrong.Score);

        // Fewer than two dates to compare say nothing.
        Assert.Equal(5d, VolumeEvidence.WithIssues(candidate, [new ListedIssue("1", "1940-04-01", null)], held).Score);
    }

    [Theory]
    [InlineData(2016, 2016)]
    [InlineData(1970, null)]
    [InlineData(0, null)]
    [InlineData(1899, null)]
    [InlineData(null, null)]
    public void KnownYear_IgnoresTheUnknownDateYear(int? year, int? expected)
    {
        Assert.Equal(expected, VolumeEvidence.KnownYear(year));
    }

    [Theory]
    [InlineData("2016-08-01", 2016)]
    [InlineData("1970-01-01", null)]
    [InlineData("20", null)]
    [InlineData(null, null)]
    public void CoverYear_ReadsComicVinesDate(string? date, int? expected)
    {
        Assert.Equal(expected, VolumeEvidence.CoverYear(date));
    }
}
