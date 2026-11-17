using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.Tests.Utilities;

public class ComicVolumeTests
{
    [Theory]
    [InlineData("2", 2, false)]
    [InlineData("02", 2, false)]
    [InlineData("v2", 2, false)]
    [InlineData("Vol. 2", 2, false)]
    [InlineData("volume 12", 12, false)]
    [InlineData("1990", 1990, true)]
    [InlineData(" 2005 ", 2005, true)]
    [InlineData("(2015)", 2015, true)]
    [InlineData("[2015]", 2015, true)]
    public void FromTag_ReadsNumbersAndStartYears(string tag, int value, bool isYear)
    {
        Assert.Equal(new ComicVolume(value, isYear), ComicVolume.FromTag(tag));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("1500")]
    [InlineData("Annual")]
    [InlineData("2.5")]
    [InlineData("(2)")]
    public void FromTag_IgnoresUnsetAndImplausibleValues(string? tag)
    {
        Assert.Null(ComicVolume.FromTag(tag));
    }

    [Theory]
    [InlineData("Green Lantern (1990)", 1990, true)]
    [InlineData("Green Lantern [1990]", 1990, true)]
    [InlineData("Green Lantern (1990-1995)", 1990, true)]
    [InlineData("Saga Vol. 2", 2, false)]
    [InlineData("Saga v2", 2, false)]
    [InlineData("Saga Volume 3", 3, false)]
    public void FromSeriesName_ReadsATrailingVolume(string series, int value, bool isYear)
    {
        Assert.Equal(new ComicVolume(value, isYear), ComicVolume.FromSeriesName(series));
    }

    [Theory]
    [InlineData("Green Lantern")]
    [InlineData("Spider-Man 2099")]
    [InlineData("1984")]
    [InlineData("Batman v Superman")]
    public void FromSeriesName_WithoutATrailingVolume_IsNull(string series)
    {
        Assert.Null(ComicVolume.FromSeriesName(series));
    }

    [Theory]
    [InlineData("Green Lantern (1990) 001.cbz", 1990, true)]
    [InlineData("Green Lantern (1990) - Chapter 0001.cbz", 1990, true)]
    [InlineData("Green Lantern (1990) #001 (2001) (Digital).cbz", 1990, true)]
    [InlineData("Saga v02 001.cbz", 2, false)]
    [InlineData("Saga Vol. 2 - Chapter 0001.cbz", 2, false)]
    [InlineData("One Piece v05 c042.cbz", 5, false)]
    public void FromFileName_ReadsAVolumeTokenOrAYearBeforeTheIssue(string fileName, int value, bool isYear)
    {
        Assert.Equal(new ComicVolume(value, isYear), ComicVolume.FromFileName(fileName));
    }

    [Theory]
    [InlineData("Saga 001 (2012).cbz")]                 // the cover year follows the issue
    [InlineData("Amazing Spider-Man 001 (1963) (c2c).cbz")]
    [InlineData("Series (2023).cbz")]                   // no issue number at all
    [InlineData("Green Lantern - Chapter 0001.cbz")]
    [InlineData("Batman v Superman 001.cbz")]
    [InlineData(null)]
    public void FromFileName_WithoutAVolume_IsNull(string? fileName)
    {
        Assert.Null(ComicVolume.FromFileName(fileName));
    }

    [Fact]
    public void Resolve_PrefersTheTag_ThenTheSeriesName_ThenTheFileName()
    {
        Assert.Equal(new ComicVolume(2005, true), ComicVolume.Resolve("2005", "Green Lantern (1990)", "Green Lantern v3 001.cbz"));
        Assert.Equal(new ComicVolume(1990, true), ComicVolume.Resolve(null, "Green Lantern (1990)", "Green Lantern v3 001.cbz"));
        Assert.Equal(new ComicVolume(3, false), ComicVolume.Resolve("-1", "Green Lantern", "Green Lantern v3 001.cbz"));
        Assert.Null(ComicVolume.Resolve(null, null, null));
    }

    [Fact]
    public void Names_TellYearsAndNumbersApart()
    {
        var year = new ComicVolume(1990, true);
        var number = new ComicVolume(2, false);

        Assert.Equal(("y1990", "(1990)", "1990"), (year.Key, year.Label, year.DisplayName));
        Assert.Equal(("v2", "Vol. 2", "Vol. 2"), (number.Key, number.Label, number.DisplayName));
    }

    [Theory]
    // No extension to strip: a decimal issue stays part of the name.
    [InlineData("Green Lantern (1990) 012.5", 1990, true)]
    [InlineData("All New X-Men v2 008 (2016)", 2, false)]
    public void FromFileNameStem_ReadsANameWithoutAnExtension(string stem, int value, bool isYear)
    {
        Assert.Equal(new ComicVolume(value, isYear), ComicVolume.FromFileNameStem(stem));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    [InlineData("Saga 001 (2012)")]
    public void FromFileNameStem_NoVolume_IsNull(string? stem)
    {
        Assert.Null(ComicVolume.FromFileNameStem(stem));
    }
}
