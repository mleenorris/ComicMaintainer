using System.Xml.Linq;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.Tests.Utilities;

/// <summary>The ComicVine issue id taggers record in ComicInfo, read and written.</summary>
public class ComicVineTagIdTests
{
    [Theory]
    // ComicTagger's notes, new and old.
    [InlineData("Tagged with ComicTagger 1.6.0 using info from Comic Vine on 2024-01-01. [CVDB12345]", null, 12345)]
    [InlineData("Tagged with ComicTagger 1.3.0 using info from Comic Vine on 2019-01-01. [Issue ID 4242]", null, 4242)]
    [InlineData("[cvdb 77]", null, 77)]
    // ComicTagger writes "[Issue ID n]" for every source: only Comic Vine's counts.
    [InlineData("Tagged with ComicTagger 1.6.0 using info from Metron on 2024-01-01. [Issue ID 4242]", null, null)]
    [InlineData("Tagged with ComicTagger 1.6.0 using info from Comic Vine on 2024-01-01. [Issue ID 4242]", null, 4242)]
    [InlineData("Tagged with ComicTagger 1.6.0 using info from Grand Comics Database. [Issue ID 4242] [CVDB5]", null, 5)]
    // The source is the one ComicTagger names, not "comicvine" said elsewhere.
    [InlineData("Tagged with ComicTagger 1.6.0 using info from Metron on 2024-01-01. [Issue ID 4242] Also on comicvine.", null, null)]
    // The issue's page, in Web or in the notes.
    [InlineData(null, "https://comicvine.gamespot.com/batman-1-i-am-gotham-part-one/4000-543101/", 543101)]
    [InlineData(null, "http://www.comicvine.com/batman-1/4000-99/", 99)]
    [InlineData("See https://comicvine.gamespot.com/batman-1/4000-5/ for more", null, 5)]
    // Both agree.
    [InlineData("[CVDB12345]", "https://comicvine.gamespot.com/batman-1/4000-12345/", 12345)]
    // They disagree: neither is trusted.
    [InlineData("[CVDB12345]", "https://comicvine.gamespot.com/batman-2/4000-12346/", null)]
    // A volume page, another site, or nothing names no issue.
    [InlineData(null, "https://comicvine.gamespot.com/batman/4050-796/", null)]
    [InlineData("Scanned by someone. 4000-12 copies sold", "https://example.com/4000-12/", null)]
    [InlineData("[CVDB0]", null, null)]
    [InlineData(" ", " ", null)]
    [InlineData(null, null, null)]
    public void FromTags_ReadsTheIssueIdTaggersWrite(string? notes, string? web, int? expected)
    {
        Assert.Equal(expected, ComicVineTagId.FromTags(notes, web));
    }

    [Theory]
    [InlineData("https://comicvine.gamespot.com/batman/4050-796/", true)]
    [InlineData(" comicvine.gamespot.com/batman/4050-796 ", true)]
    [InlineData("https://comicvine.gamespot.com/batman-1/4000-12345/", false)]
    [InlineData("https://example.com/batman/4050-796/", false)]
    [InlineData(null, false)]
    public void IsVolumePage_KnowsTheSeriesPage(string? web, bool expected)
    {
        Assert.Equal(expected, ComicVineTagId.IsVolumePage(web));
    }

    [Theory]
    [InlineData(null, false, "[CVDB12345]")]
    [InlineData("Scanned by me.", false, "Scanned by me. [CVDB12345]")]
    [InlineData("Scanned by me.  ", false, "Scanned by me. [CVDB12345]")]
    // An id already there is kept, unless the file was filled from another issue.
    [InlineData("Tagged. [CVDB999]", false, "Tagged. [CVDB999]")]
    [InlineData("Tagged. [CVDB999]", true, "Tagged. [CVDB12345]")]
    [InlineData("Tagged. [Issue ID 999] then [CVDB998]", true, "Tagged. [Issue ID 999] then [CVDB12345]")]
    // Another source's id is left alone, and Comic Vine's added beside it.
    [InlineData("Tagged using info from Metron. [Issue ID 999]", true, "Tagged using info from Metron. [Issue ID 999] [CVDB12345]")]
    public void WithIssueId_RecordsTheIdAndKeepsTheNotes(string? notes, bool replace, string expected)
    {
        Assert.Equal(expected, ComicVineTagId.WithIssueId(notes, 12345, replace, matchedByName: false));
    }

    [Theory]
    // A name match is marked as the app's own, which survives in the archive.
    [InlineData(null, false, true, "[CVDB12345] (matched by name in ComicMaintainer)")]
    [InlineData("Scanned.", false, true, "Scanned. [CVDB12345] (matched by name in ComicMaintainer)")]
    // A tagger's id that is kept gets no marker.
    [InlineData("[CVDB999]", false, true, "[CVDB999]")]
    // Replacing a tagger's id with a name match marks it; replacing an own match keeps one marker.
    [InlineData("Tagged. [CVDB999]", true, true, "Tagged. [CVDB12345] (matched by name in ComicMaintainer)")]
    [InlineData("[CVDB999] (matched by name in ComicMaintainer)", true, true, "[CVDB12345] (matched by name in ComicMaintainer)")]
    // A tagger's id replacing an own match drops the marker.
    [InlineData("[CVDB999] (matched by name in ComicMaintainer)", true, false, "[CVDB12345]")]
    public void WithIssueId_MarksTheAppsOwnMatches(string? notes, bool replace, bool matchedByName, string expected)
    {
        var recorded = ComicVineTagId.WithIssueId(notes, 12345, replace, matchedByName);

        Assert.Equal(expected, recorded);
        Assert.Equal(expected.Contains(ComicVineTagId.NameMatchMarker, StringComparison.Ordinal), ComicVineTagId.IsNameMatch(recorded));
    }

    [Fact]
    public void IsNameMatch_NeedsTheMarkerAndAComicVineId()
    {
        Assert.False(ComicVineTagId.IsNameMatch("(matched by name in ComicMaintainer)"));
        Assert.False(ComicVineTagId.IsNameMatch("[CVDB5]"));
        Assert.False(ComicVineTagId.IsNameMatch(null));
        Assert.True(ComicVineTagId.IsNameMatch("[CVDB5] (Matched by name in ComicMaintainer)"));
        Assert.True(ComicInfoXml.Read(XDocument.Parse("<ComicInfo><Notes>[CVDB5] (matched by name in ComicMaintainer)</Notes></ComicInfo>"))!.ComicVineIssueMatchedByName);
    }

    [Theory]
    [InlineData(null, false, "https://comicvine.gamespot.com/b-1/4000-12345/")]
    [InlineData("https://comicvine.gamespot.com/batman/4050-796/", false, "https://comicvine.gamespot.com/b-1/4000-12345/")]
    [InlineData("https://example.com/mine", true, "https://example.com/mine")]
    [InlineData("https://comicvine.gamespot.com/b-9/4000-999/", false, "https://comicvine.gamespot.com/b-9/4000-999/")]
    [InlineData("https://comicvine.gamespot.com/b-9/4000-999/", true, "https://comicvine.gamespot.com/b-1/4000-12345/")]
    // Only the other issue's link is changed; links beside it are kept.
    [InlineData("https://example.com/mine https://comicvine.gamespot.com/b-9/4000-999/", true, "https://example.com/mine https://comicvine.gamespot.com/b-1/4000-12345/")]
    public void WithIssuePage_SetsTheIssuesPage_KeepingOtherLinks(string? web, bool replace, string expected)
    {
        Assert.Equal(expected, ComicVineTagId.WithIssuePage(web, "https://comicvine.gamespot.com/b-1/4000-12345/", 12345, replace));
    }

    [Fact]
    public void ReadingTheTags_GivesTheIssueId()
    {
        var document = XDocument.Parse("<ComicInfo><Series>Batman</Series><Notes>[CVDB12345]</Notes></ComicInfo>");

        Assert.Equal(12345, ComicInfoXml.Read(document)!.ComicVineIssueId);
        Assert.Equal(7, new ComicInfo { Web = "https://comicvine.gamespot.com/x/4000-7/" }.ToMetadata().ComicVineIssueId);
        Assert.Null(ComicInfoXml.Read(XDocument.Parse("<ComicInfo><Series>Batman</Series></ComicInfo>"))!.ComicVineIssueId);
    }

    [Fact]
    public void FillEmptyFields_RecordsTheIssue_InWebAndNotes()
    {
        const string page = "https://comicvine.gamespot.com/batman-1/4000-12345/";
        var fill = new ProviderComicInfoFields(Web: page, ComicVineIssueId: 12345);

        // Empty: both are written.
        var empty = ComicInfoXml.FillEmptyFields(XDocument.Parse("<ComicInfo><Series>Batman</Series></ComicInfo>"), fill).Root!;
        Assert.Equal(page, empty.Element("Web")?.Value);
        Assert.Equal("[CVDB12345]", empty.Element("Notes")?.Value);

        // The series' ComicVine page gives way to the issue's; the notes keep what they held.
        var series = ComicInfoXml.FillEmptyFields(
            XDocument.Parse("<ComicInfo><Notes>Scanned by me.</Notes><Web>https://comicvine.gamespot.com/batman/4050-91273/</Web></ComicInfo>"), fill).Root!;
        Assert.Equal(page, series.Element("Web")?.Value);
        Assert.Equal("Scanned by me. [CVDB12345]", series.Element("Notes")?.Value);

        // A page the user set is kept.
        var own = ComicInfoXml.FillEmptyFields(XDocument.Parse("<ComicInfo><Web>https://example.com/my-batman</Web></ComicInfo>"), fill).Root!;
        Assert.Equal("https://example.com/my-batman", own.Element("Web")?.Value);

        // Running it again changes nothing.
        var again = ComicInfoXml.FillEmptyFields(new XDocument(empty), fill);
        Assert.True(ComicInfoXml.AreEquivalent(new XDocument(empty), again));
    }

    [Fact]
    public void FillEmptyFields_ReplacesAnotherIssue_OnlyWhenTheFileWasFilledFromThisOne()
    {
        const string page = "https://comicvine.gamespot.com/batman-1/4000-12345/";
        var tagged = XDocument.Parse("<ComicInfo><Notes>[CVDB999]</Notes><Web>https://comicvine.gamespot.com/batman-9/4000-999/</Web></ComicInfo>");

        var kept = ComicInfoXml.FillEmptyFields(tagged, new ProviderComicInfoFields(Web: page, ComicVineIssueId: 12345)).Root!;
        Assert.Equal("[CVDB999]", kept.Element("Notes")?.Value);
        Assert.Equal("https://comicvine.gamespot.com/batman-9/4000-999/", kept.Element("Web")?.Value);

        var replaced = ComicInfoXml.FillEmptyFields(tagged, new ProviderComicInfoFields(Web: page, ComicVineIssueId: 12345, ReplacesComicVineIssueId: true)).Root!;
        Assert.Equal("[CVDB12345]", replaced.Element("Notes")?.Value);
        Assert.Equal(page, replaced.Element("Web")?.Value);
    }

    [Fact]
    public void For_RecordsTheIssue_AndReplacesATagOnlyWhenToldItIsWrong()
    {
        var issue = new ComicMaintainer.Core.Data.ComicVineIssueEntity { Id = 12345, VolumeId = 1, IssueNumber = "1", SiteUrl = "https://comicvine.gamespot.com/batman-1/4000-12345/" };
        var tagged = new ComicMetadata { Series = "Batman", Issue = "1", Title = "A Story", ComicVineIssueId = 999 };

        var plain = ComicVineIssueFill.For(new ComicMetadata { Series = "Batman", Issue = "1", Title = "A Story" }, issue, null, matchedByName: true).Fill;
        Assert.Equal((12345, false, true), (plain.ComicVineIssueId!.Value, plain.ReplacesComicVineIssueId, plain.ComicVineIssueMatchedByName));

        // A kept tag naming another issue: nothing is written that would name a second one.
        var kept = ComicVineIssueFill.For(tagged, issue, null, matchedByName: true).Fill;
        Assert.Equal((null, null, false, false), (kept.ComicVineIssueId, kept.Web, kept.ReplacesComicVineIssueId, kept.ComicVineIssueMatchedByName));

        var replaced = ComicVineIssueFill.For(tagged, issue, null, replacesTaggedIssue: true, matchedByName: true).Fill;
        Assert.Equal((12345, true), (replaced.ComicVineIssueId!.Value, replaced.ReplacesComicVineIssueId));
    }
}
