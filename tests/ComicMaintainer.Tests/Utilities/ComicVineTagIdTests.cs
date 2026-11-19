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
    [InlineData("Tagged. [Issue ID 999] then [CVDB998]", true, "Tagged. [CVDB12345] then [CVDB998]")]
    public void WithIssueId_RecordsTheIdAndKeepsTheNotes(string? notes, bool replace, string expected)
    {
        Assert.Equal(expected, ComicVineTagId.WithIssueId(notes, 12345, replace));
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
    public void For_RecordsTheIssue_AndReplacesATagNamingAnother()
    {
        var issue = new ComicMaintainer.Core.Data.ComicVineIssueEntity { Id = 12345, VolumeId = 1, IssueNumber = "1", SiteUrl = "https://comicvine.gamespot.com/batman-1/4000-12345/" };

        var plain = ComicVineIssueFill.For(new ComicMetadata { Series = "Batman", Issue = "1", Title = "A Story" }, issue, null).Fill;
        Assert.Equal(12345, plain.ComicVineIssueId);
        Assert.False(plain.ReplacesComicVineIssueId);

        var sameTag = ComicVineIssueFill.For(new ComicMetadata { Series = "Batman", Issue = "1", Title = "A Story", ComicVineIssueId = 12345 }, issue, null).Fill;
        Assert.False(sameTag.ReplacesComicVineIssueId);

        var otherTag = ComicVineIssueFill.For(new ComicMetadata { Series = "Batman", Issue = "1", Title = "A Story", ComicVineIssueId = 999 }, issue, null).Fill;
        Assert.True(otherTag.ReplacesComicVineIssueId);
    }
}
