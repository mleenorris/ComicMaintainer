using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.Tests.Utilities;

public class ComicVineIssueFillTests
{
    private static ComicVineIssueEntity Issue(
        string? name = "The Gotham Gambit",
        string? coverDate = "2016-08-01",
        string? summary = "Batman meets Gotham.",
        string? writers = "Tom King",
        string? arcs = """[{"id":56789,"name":"I Am Gotham"}]""")
        => new()
        {
            Id = 1,
            VolumeId = 91273,
            IssueNumber = "1",
            Name = name,
            CoverDate = coverDate,
            Summary = summary,
            SiteUrl = "https://comicvine.example/batman-1",
            Writers = writers,
            StoryArcsJson = arcs
        };

    private static ComicMetadata Tags(string? title = "Chapter 1", int? year = null, string? summary = null, long locked = 0, params string[] authors)
        => new() { Series = "Batman", Issue = "1", Title = title, Year = year, Summary = summary, Authors = [.. authors], UserLockedFieldsMask = locked };

    [Fact]
    public void For_FillsAPlaceholderTitle_MissingYear_Summary_Writers_AndTheEmptyElements()
    {
        var plan = ComicVineIssueFill.For(Tags(), Issue(), seriesSynopsis: null);

        Assert.Equal(
            ComicMetadataFieldFlags.Title | ComicMetadataFieldFlags.Year | ComicMetadataFieldFlags.Summary | ComicMetadataFieldFlags.Authors,
            plan.Fields);
        Assert.Equal("The Gotham Gambit", plan.Metadata.Title);
        Assert.Equal(2016, plan.Metadata.Year);
        Assert.Equal("Batman meets Gotham.", plan.Metadata.Summary);
        Assert.Equal(["Tom King"], plan.Metadata.Authors);
        Assert.Equal(new ProviderComicInfoFields(Web: "https://comicvine.example/batman-1", StoryArc: "I Am Gotham", Month: 8, ComicVineIssueId: 1), plan.Fill);
        Assert.False(plan.IsEmpty);
    }

    [Fact]
    public void For_KeepsWhatTheFileAlreadyHas()
    {
        var plan = ComicVineIssueFill.For(Tags("The Real Title", 2017, "A summary of its own", 0, "Scott Snyder"), Issue(), "Series synopsis.");

        Assert.Equal(ComicMetadataFieldFlags.None, plan.Fields);
    }

    [Fact]
    public void For_Replaces1970_AndASummaryThatIsOnlyTheSeriesSynopsis()
    {
        var plan = ComicVineIssueFill.For(Tags("The Real Title", 1970, "Series synopsis."), Issue(), "Series synopsis.");

        Assert.Equal(ComicMetadataFieldFlags.Year | ComicMetadataFieldFlags.Summary | ComicMetadataFieldFlags.Authors, plan.Fields);
    }

    [Fact]
    public void For_NeverChangesALockedField()
    {
        var locked = (long)(ComicMetadataFieldFlags.Title | ComicMetadataFieldFlags.Year | ComicMetadataFieldFlags.Summary | ComicMetadataFieldFlags.Authors);

        Assert.Equal(ComicMetadataFieldFlags.None, ComicVineIssueFill.For(Tags(locked: locked), Issue(), null).Fields);
    }

    [Fact]
    public void For_AnIssueWithoutDetails_FillsOnlyWhatItHas()
    {
        var plan = ComicVineIssueFill.For(Tags(), Issue(name: null, coverDate: null, summary: null, writers: null, arcs: null), null);

        Assert.Equal(ComicMetadataFieldFlags.None, plan.Fields);
        Assert.Equal(new ProviderComicInfoFields(Web: "https://comicvine.example/batman-1", ComicVineIssueId: 1), plan.Fill);
    }

    [Fact]
    public void For_ReplacesWhatAnIssueOfTheWrongVolumeWrote()
    {
        // The file was filled from a collected edition before it was matched to its real volume.
        var wrong = Issue(name: "All-New X-Men Vol. 1: Inevitable", coverDate: "2014-06-01", summary: "Collects issues 1-6.", writers: "Brian Michael Bendis");
        var filled = Tags("All-New X-Men Vol. 1: Inevitable", 2014, "Collects issues 1-6.", 0, "Brian Michael Bendis");

        var plan = ComicVineIssueFill.For(filled, Issue(), seriesSynopsis: null, previous: wrong);

        Assert.Equal(
            ComicMetadataFieldFlags.Title | ComicMetadataFieldFlags.Year | ComicMetadataFieldFlags.Summary | ComicMetadataFieldFlags.Authors,
            plan.Fields);
        Assert.Equal(("The Gotham Gambit", 2016, "Batman meets Gotham."), (plan.Metadata.Title, plan.Metadata.Year, plan.Metadata.Summary));
        Assert.Equal(["Tom King"], plan.Metadata.Authors);

        // What the user wrote since is kept.
        var edited = Tags("A title of my own", 2015, "My summary", 0, "Someone Else");
        Assert.Equal(ComicMetadataFieldFlags.None, ComicVineIssueFill.For(edited, Issue(), null, wrong).Fields);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("Chapter 8", true)]
    [InlineData("#8", true)]
    [InlineData("Issue #8", true)]
    [InlineData("v2 008 (2016)", true)]
    [InlineData("0_(2011)", true)]
    [InlineData("Batman 008", true)]
    [InlineData("The Gotham Gambit", false)]
    [InlineData("#Annual 2021", false)]
    public void IsPlaceholderTitle_KnowsATitleThatOnlyNamesTheIssue(string? title, bool expected)
    {
        Assert.Equal(expected, ComicVineIssueFill.IsPlaceholderTitle(title, "Batman", "8"));
    }

    [Theory]
    [InlineData("2016-08-01", 2016, 8)]
    [InlineData("2016", 2016, null)]
    [InlineData("2016-13-01", 2016, null)]
    [InlineData("0000-00-00", null, null)]
    [InlineData(" ", null, null)]
    [InlineData(null, null, null)]
    public void CoverDate_ReadsTheYearAndMonth(string? coverDate, int? year, int? month)
    {
        Assert.Equal((year, month), ComicVineIssueFill.CoverDate(coverDate));
    }

    [Fact]
    public void StoryArcs_RoundTripsAndToleratesBadJson()
    {
        var json = ComicVineIssueFill.StoryArcsJson([new ComicVineArcReference(5, "House of M")]);

        Assert.Equal([new ComicVineArcReference(5, "House of M")], ComicVineIssueFill.StoryArcs(json));
        Assert.Empty(ComicVineIssueFill.StoryArcs("not json"));
        Assert.Empty(ComicVineIssueFill.StoryArcs("null"));
        Assert.Empty(ComicVineIssueFill.StoryArcs(null));
    }

    [Fact]
    public void StoryArcsJson_WithLimit_DropsWholeArcsAndStaysReadable()
    {
        var arcs = Enumerable.Range(1, 400).Select(i => new ComicVineArcReference(i, $"Story arc number {i}")).ToList();

        var json = ComicVineIssueFill.StoryArcsJson(arcs, 4000);

        Assert.True(json.Length <= 4000);
        var read = ComicVineIssueFill.StoryArcs(json);
        Assert.NotEmpty(read);
        Assert.Equal(arcs.Take(read.Count), read);
        Assert.Equal(ComicVineIssueFill.StoryArcsJson(arcs.Take(2).ToList()), ComicVineIssueFill.StoryArcsJson(arcs.Take(2).ToList(), 4000));
    }

    [Fact]
    public void FillEmptyFields_WritesStoryArcAndMonth_OnlyIntoEmptyElements()
    {
        var existing = System.Xml.Linq.XDocument.Parse("<ComicInfo><Month>-1</Month><StoryArc>Kept</StoryArc></ComicInfo>");

        var filled = ComicInfoXml.FillEmptyFields(existing, new ProviderComicInfoFields(StoryArc: "House of M", Month: 8)).Root!;

        Assert.Equal("8", filled.Element("Month")?.Value);
        Assert.Equal("Kept", filled.Element("StoryArc")?.Value);
        Assert.Null(ComicInfoXml.FillEmptyFields(null, new ProviderComicInfoFields(Month: 13)).Root!.Element("Month"));
        Assert.Equal("House of M", ComicInfoXml.FillEmptyFields(null, new ProviderComicInfoFields(StoryArc: "House of M")).Root!.Element("StoryArc")?.Value);
    }
}
