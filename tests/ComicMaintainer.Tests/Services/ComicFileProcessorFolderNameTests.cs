using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// How file and folder names hold characters a name cannot (<see cref="ComicFileProcessor.SanitizeFileName"/>),
/// how the series folder is rendered (<see cref="ComicFileProcessor.FormatFolderName"/>), and how
/// folder names written by other tools are read back as series names.
/// </summary>
public sealed class ComicFileProcessorFolderNameTests
{
    [Theory]
    [InlineData("Solo Leveling: Ragnarok", "Solo Leveling - Ragnarok")]
    [InlineData("Wolverine: Old Man Logan (2011)", "Wolverine - Old Man Logan (2011)")]
    [InlineData("Batman : Year One", "Batman - Year One")]
    [InlineData("Re:Zero", "Re-Zero")]
    [InlineData("Fate/Zero", "Fate-Zero")]
    [InlineData("Fate \\ Zero | Stay", "Fate - Zero - Stay")]
    [InlineData("The \"Best\" Of", "The 'Best' Of")]
    [InlineData("What If? <Annual> *", "What If Annual ")]
    [InlineData("Ends with:", "Ends with - ")]
    [InlineData("Michael Turner’s Soulfire", "Michael Turner’s Soulfire")]
    [InlineData("Saga - Chapter 0001.cbz", "Saga - Chapter 0001.cbz")]
    public void SanitizeFileName_ReplacesWhatANameCannotHoldWithWhatReadsTheSame(string name, string expected)
    {
        Assert.Equal(expected, ComicFileProcessor.SanitizeFileName(name));
    }

    [Theory]
    [InlineData("???.cbz", "___.cbz")]
    [InlineData("*", "_")]
    public void SanitizeFileName_NothingLeftButTheExtension_KeepsAnUnderscoreForEachCharacter(string name, string expected)
    {
        Assert.Equal(expected, ComicFileProcessor.SanitizeFileName(name));
    }

    [Fact]
    public void FormatFilename_SeriesWithAColon_ReadsTheColonAsADash()
    {
        var name = ComicFileProcessor.FormatFilename(
            "{series} - Chapter {issue}", new ComicInfo { Series = "Green Lantern: New Guardians" }, "1");

        Assert.Equal("Green Lantern - New Guardians - Chapter 0001.cbz", name);
    }

    [Fact]
    public void FormatFilename_SeriesEndingInAColon_LeavesNoDoubleSeparator()
    {
        var name = ComicFileProcessor.FormatFilename(
            "{series} - Chapter {issue}", new ComicInfo { Series = "Batman:" }, "1");

        Assert.Equal("Batman - Chapter 0001.cbz", name);
    }

    [Theory]
    [InlineData("{series} ({volume_year})", "Green Lantern (2023)")]
    [InlineData("{series}", "Green Lantern")]
    [InlineData("{series} {volume_label}", "Green Lantern (2023)")]
    [InlineData("{publisher} - {series}", "DC Comics - Green Lantern")]
    // Issue placeholders would give every issue its own folder, so they render nothing.
    [InlineData("{series} {issue} {issue_no_pad} {title} {year}", "Green Lantern")]
    public void FormatFolderName_RendersTheSeriesFolder(string template, string expected)
    {
        var tags = new ComicInfo
        {
            Series = "Green Lantern",
            VolumeYear = 2023,
            Number = "5",
            Title = "Chapter 5",
            Year = 2024,
            Publisher = "DC Comics"
        };

        Assert.Equal(expected, ComicFileProcessor.FormatFolderName(template, tags, "Green Lantern 005.cbz"));
    }

    [Theory]
    [InlineData("{series} ({volume_year})", "Saga")]
    [InlineData("{series} [{volume_number}]", "Saga")]
    [InlineData("{publisher} - {series}", "Saga")]
    [InlineData("{series}  {format}  {volume_label}", "Saga")]
    public void FormatFolderName_EmptyPlaceholders_CollapseCleanly(string template, string expected)
    {
        Assert.Equal(expected, ComicFileProcessor.FormatFolderName(template, new ComicInfo { Series = "Saga" }));
    }

    [Fact]
    public void FormatFolderName_VolumeNumberAndAnnual_RenderTheirParts()
    {
        var tags = new ComicInfo { Series = "Green Lantern Annual", Volume = "2" };

        Assert.Equal("Green Lantern Annual Vol. 2", ComicFileProcessor.FormatFolderName("{series} {annual} {volume_label}", tags));
        Assert.Equal("Green Lantern v2", ComicFileProcessor.FormatFolderName("{series} v{volume_number}", tags));
        Assert.Equal("Green Lantern Annual", ComicFileProcessor.FormatFolderName("{series} {format}", tags));
    }

    [Theory]
    [InlineData("Solo Leveling: Ragnarok", "Solo Leveling - Ragnarok")]
    [InlineData("Fate/Zero", "Fate-Zero")]
    [InlineData("Saga...", "Saga")]
    public void FormatFolderName_NamesOneFolderWindowsCanHold(string series, string expected)
    {
        Assert.Equal(expected, ComicFileProcessor.FormatFolderName("{series}", new ComicInfo { Series = series }));
    }

    [Theory]
    [InlineData("{series}", null)]
    [InlineData("{series}", "   ")]
    [InlineData("{series}", "???")]
    [InlineData("({volume_year})", "Saga")]
    public void FormatFolderName_NothingToNameAFolderBy_IsNull(string template, string? series)
    {
        Assert.Null(ComicFileProcessor.FormatFolderName(template, new ComicInfo { Series = series }));
    }

    [Theory]
    [InlineData("Trash Of The Count’S Family", "Trash Of The Count’s Family")]
    [InlineData("Solo Leveling_ Ragnarok", "Solo Leveling: Ragnarok")]
    [InlineData("O'Brien", "O'Brien")]
    public void NormalizeSeriesName_FolderNamedByAnotherTool_ReadsAsTheSeries(string folder, string expected)
    {
        Assert.Equal(expected, ComicFileProcessor.NormalizeSeriesName(folder));
    }

    [Fact]
    public void NormalizeSeriesName_ForComparison_ACurlyApostropheMatchesAStraightOne()
    {
        Assert.Equal(
            ComicFileProcessor.NormalizeSeriesName("Michael Turner's Soulfire", forComparison: true),
            ComicFileProcessor.NormalizeSeriesName("Michael Turner’s Soulfire", forComparison: true));
    }
}
