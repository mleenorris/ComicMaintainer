using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// The volume number lives in ComicInfo and the start year in the database: the series page
/// links the two across the issues of a series, and the filename format can name either.
/// </summary>
public class SeriesVolumeLinkingTests
{
    private static SeriesIssueDto Issue(string number, string? volume = null, int? volumeYear = null, string? fileName = null)
        => new() { Issue = number, Volume = volume, VolumeYear = volumeYear, FileName = fileName ?? $"x {number}.cbz" };

    [Fact]
    public void AnIssueWithBoth_LinksTheNumberAndTheYearForTheOthers()
    {
        var both = Issue("1", volume: "2", volumeYear: 2016);
        var yearOnly = Issue("2", volumeYear: 2016);
        var numberOnly = Issue("3", volume: "2");
        var otherVolume = Issue("1", volumeYear: 2004);

        SeriesLibraryService.LinkVolumeNumbersAndYears([both, yearOnly, numberOnly, otherVolume]);

        Assert.Equal(["v2", "v2", "v2", "y2004"], new[] { both, yearOnly, numberOnly, otherVolume }.Select(i => i.VolumeKey));
        Assert.Equal("Vol. 2 (2016)", both.VolumeLabel);
        Assert.Equal("Vol. 2 (2016)", yearOnly.VolumeLabel);
        Assert.Equal("Vol. 2 (2016)", numberOnly.VolumeLabel);
        Assert.Equal("2004", otherVolume.VolumeLabel);
    }

    [Fact]
    public void AYearLinkedToTwoNumbers_IsLeftAlone()
    {
        var a = Issue("1", volume: "1", volumeYear: 2016);
        var b = Issue("1", volume: "2", volumeYear: 2016);
        var yearOnly = Issue("2", volumeYear: 2016);
        var numberOnly = Issue("2", volume: "1");

        SeriesLibraryService.LinkVolumeNumbersAndYears([a, b, yearOnly, numberOnly]);

        Assert.Equal("y2016", yearOnly.VolumeKey);
        Assert.Equal("Vol. 1 (2016)", numberOnly.VolumeLabel);
    }

    [Fact]
    public void WithoutAnIssueThatHasBoth_NothingIsLinked_AndAStaleLinkIsCleared()
    {
        var yearOnly = Issue("1", volumeYear: 2016);
        yearOnly.LinkedVolume = new Core.Utilities.ComicVolume(9, IsYear: false);
        var numberOnly = Issue("1", volume: "2");

        SeriesLibraryService.LinkVolumeNumbersAndYears([yearOnly, numberOnly]);

        Assert.Equal("y2016", yearOnly.VolumeKey);
        Assert.Equal("Vol. 2", numberOnly.VolumeLabel);
        Assert.Null(Issue("1").VolumeLabel);
    }

    [Theory]
    // The number names the volume; the start year is the fallback, and each has its own placeholder.
    [InlineData("{series} {volume_label} - Chapter {issue}", "2", 2016, "Green Lantern Vol. 2 - Chapter 0001.cbz")]
    [InlineData("{series} {volume_label} - Chapter {issue}", null, 2016, "Green Lantern (2016) - Chapter 0001.cbz")]
    [InlineData("{series} ({volume_year}) v{volume_number} #{issue}", "2", 2016, "Green Lantern (2016) v2 #0001.cbz")]
    [InlineData("{series} ({volume_year}) #{issue}", "1990", null, "Green Lantern (1990) #0001.cbz")]
    [InlineData("{series} ({volume_year}) [{volume_number}] #{issue}", null, null, "Green Lantern #0001.cbz")]
    [InlineData("{series} {volume_label} #{issue}", "1990", 2016, "Green Lantern (2016) #0001.cbz")]
    public void FormatFilename_NamesTheVolumeNumberAndStartYear(string template, string? volume, int? volumeYear, string expected)
    {
        var tags = new ComicInfo { Series = "Green Lantern", Volume = volume, VolumeYear = volumeYear };

        Assert.Equal(expected, ComicFileProcessor.FormatFilename(template, tags, "1"));
    }

    [Fact]
    public void VolumeYear_IsNeverWrittenIntoComicInfo()
    {
        var metadata = new ComicMetadata { Series = "Saga", VolumeYear = 2012 };
        var document = Core.Utilities.ComicInfoXml.Merge(null, metadata, Core.Models.ComicMetadataFields.All);

        Assert.DoesNotContain("2012", document.ToString());
        Assert.Equal(2012, ComicInfo.FromMetadata(metadata).VolumeYear);
        Assert.Equal(ComicMetadataFieldFlags.VolumeYear, ComicMetadataFields.Parse("volumeYear"));
        Assert.Equal(ComicMetadataFieldFlags.VolumeYear, ComicMetadataFields.Parse("volume_year"));
        Assert.Equal(2012, metadata.Clone().VolumeYear);
        Assert.NotNull(AppSettings.DefaultFilenameFormat);
    }
}
