using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;

namespace ComicMaintainer.Tests.Services;

public class ComicFileProcessorTests
{
    [Theory]
    [InlineData("Batman_The Dark Knight", "Batman:The Dark Knight", false)]
    [InlineData("Spider_Man", "Spider:Man", false)]
    [InlineData("Test_Series", "Test:Series", false)]
    public void NormalizeSeriesName_WithUnderscores_ReplacesWithColons(string input, string expected, bool forComparison)
    {
        // Act
        var result = ComicFileProcessor.NormalizeSeriesName(input, forComparison);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Batman's Adventures", "Batman\u0027s Adventures", true)]
    [InlineData("Test (*)", "Test", true)]
    [InlineData("Series [*]", "Series", true)]
    [InlineData("  Trimmed  ", "Trimmed", true)]
    public void NormalizeSeriesName_ForComparison_AppliesAdditionalTransformations(string input, string expected, bool forComparison)
    {
        // Act
        var result = ComicFileProcessor.NormalizeSeriesName(input, forComparison);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    // The volume goes only where the template places it.
    [InlineData("{series} - Chapter {issue}", "Batman", "5", "Batman - Chapter 0005.cbz")]
    [InlineData("{series} #{issue}", "Superman", "12", "Superman #0012.cbz")]
    [InlineData("{series} {volume_label} #{issue}", "Superman", "12", "Superman Vol. 1 #0012.cbz")]
    [InlineData("{series} v{volume} #{issue} - {title}", "Spiderman", "1", "Spiderman v1 #0001 - Test.cbz")]
    public void FormatFilename_WithVariousTemplates_FormatsCorrectly(
        string template, string series, string issue, string expected)
    {
        // Arrange
        var tags = new ComicInfo
        {
            Series = series,
            Title = "Test",
            Volume = "1",
            Year = 2023,
            Publisher = "DC"
        };

        // Act
        var result = ComicFileProcessor.FormatFilename(template, tags, issue);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    // A start year (tag or the file's own name) is written after the series, a number as "Vol. n".
    [InlineData("Green Lantern", "1990", null, "Green Lantern (1990) - Chapter 0001.cbz")]
    [InlineData("Saga", "2", null, "Saga Vol. 2 - Chapter 0001.cbz")]
    [InlineData("Green Lantern", null, "Green Lantern (1990) 001.cbz", "Green Lantern (1990) - Chapter 0001.cbz")]
    [InlineData("Green Lantern", null, "Green Lantern v2 001.cbz", "Green Lantern Vol. 2 - Chapter 0001.cbz")]
    // A series name that already ends in a volume keeps it once.
    [InlineData("Green Lantern (2023)", "2023", null, "Green Lantern (2023) - Chapter 0001.cbz")]
    [InlineData("Saga Vol. 2", "2", null, "Saga Vol. 2 - Chapter 0001.cbz")]
    // No volume anywhere, or only the cover year after the issue: the name is unchanged.
    [InlineData("Saga", null, "Saga 001 (2012).cbz", "Saga - Chapter 0001.cbz")]
    [InlineData("Saga", "-1", null, "Saga - Chapter 0001.cbz")]
    public void FormatFilename_AddsTheIssuesVolumeAfterTheSeries(string series, string? volume, string? sourceFileName, string expected)
    {
        var tags = new ComicInfo { Series = series, Volume = volume };

        Assert.Equal(expected, ComicFileProcessor.FormatFilename(
            AppSettings.DefaultFilenameFormat, tags, "1", ".cbz", 4, sourceFileName));
    }

    [Theory]
    // {annual} and {volume_label} go where the template puts them...
    [InlineData("{series} {annual} {volume_label} - Chapter {issue}", "Green Lantern Annual", "1990", "Green Lantern Annual (1990) - Chapter 0001.cbz")]
    [InlineData("{series} {volume_label} {annual} #{issue}", "Green Lantern Annual", "1990", "Green Lantern (1990) Annual #0001.cbz")]
    [InlineData("{series} #{issue} [{annual}]", "Green Lantern Annual", null, "Green Lantern #0001 [Annual].cbz")]
    // ...render nothing, with their brackets and separators, when they do not apply...
    [InlineData("{series} {annual} {volume_label} - Chapter {issue}", "Green Lantern", null, "Green Lantern - Chapter 0001.cbz")]
    [InlineData("{series} #{issue} [{annual}]", "Green Lantern", null, "Green Lantern #0001.cbz")]
    [InlineData("{series} ({volume}) #{issue}", "Green Lantern", null, "Green Lantern #0001.cbz")]
    [InlineData("{series} - {annual} - Chapter {issue}", "Green Lantern", null, "Green Lantern - Chapter 0001.cbz")]
    [InlineData("{series} - Chapter {issue} - {annual}", "Green Lantern", null, "Green Lantern - Chapter 0001.cbz")]
    // ...and are left out where the template leaves them out: {series} is the title alone.
    [InlineData("{series} #{issue}", "Green Lantern Annual (1990)", null, "Green Lantern #0001.cbz")]
    [InlineData("{series} {volume_label} #{issue}", "Green Lantern Annual (1990)", null, "Green Lantern (1990) #0001.cbz")]
    public void FormatFilename_PlacesTheAnnualAndVolumeWhereTheTemplateSays(string template, string series, string? volume, string expected)
    {
        var tags = new ComicInfo { Series = series, Volume = volume };

        Assert.Equal(expected, ComicFileProcessor.FormatFilename(template, tags, "1"));
    }

    [Theory]
    // {format} names an Annual or a Special from <Format>; {annual} names only an Annual.
    [InlineData(AppSettings.DefaultFilenameFormat, "Green Lantern", "Special", "1990", "Green Lantern Special (1990) - Chapter 0001.cbz")]
    [InlineData(AppSettings.DefaultFilenameFormat, "Green Lantern", "annual", null, "Green Lantern Annual - Chapter 0001.cbz")]
    [InlineData(AppSettings.DefaultFilenameFormat, "Green Lantern Annual", null, null, "Green Lantern Annual - Chapter 0001.cbz")]
    [InlineData(AppSettings.DefaultFilenameFormat, "Green Lantern", "TPB", null, "Green Lantern - Chapter 0001.cbz")]
    [InlineData("{series} {annual} #{issue}", "Green Lantern", "Special", null, "Green Lantern #0001.cbz")]
    [InlineData("{series} {annual} #{issue}", "Green Lantern", "Annual", null, "Green Lantern Annual #0001.cbz")]
    [InlineData("{series} [{format}] #{issue}", "Green Lantern", null, null, "Green Lantern #0001.cbz")]
    // A series already named for its Special is not named "Special Special".
    [InlineData(AppSettings.DefaultFilenameFormat, "Batman Special", "Special", null, "Batman Special - Chapter 0001.cbz")]
    [InlineData(AppSettings.DefaultFilenameFormat, "Special", "Special", null, "Special Special - Chapter 0001.cbz")]
    [InlineData(AppSettings.DefaultFilenameFormat, "Batman Special", null, null, "Batman Special - Chapter 0001.cbz")]
    public void FormatFilename_FormatPlaceholder_NamesTheIssuesKind(string template, string series, string? format, string? volume, string expected)
    {
        var tags = new ComicInfo { Series = series, Format = format, Volume = volume };

        Assert.Equal(expected, ComicFileProcessor.FormatFilename(template, tags, "1"));
    }

    [Theory]
    [InlineData("{series} - Chapter {issue}", AppSettings.DefaultFilenameFormat)]
    [InlineData(" {series} - Chapter {issue} ", AppSettings.DefaultFilenameFormat)]
    [InlineData("{series} {annual} {volume_label} - Chapter {issue}", AppSettings.DefaultFilenameFormat)]
    [InlineData("{series} {volume_label} - {annual} - Chapter {issue}", "{series} {volume_label} - {annual} - Chapter {issue}")]
    [InlineData("{series} #{issue}", "{series} #{issue}")]
    public void FilenameFormat_LegacyDefault_ReadsAsTheCurrentDefault(string configured, string expected)
    {
        Assert.Equal(expected, new AppSettings { FilenameFormat = configured }.FilenameFormat);
    }

    [Fact]
    public void FormatFilename_WithDecimalIssue_FormatsCorrectly()
    {
        // Arrange
        var template = "{series} - Chapter {issue}";
        var tags = new ComicInfo { Series = "Test Series" };

        // Act
        var result = ComicFileProcessor.FormatFilename(template, tags, "12.5");

        // Assert
        Assert.Contains("0012.5", result);
    }

    [Fact]
    public void FormatFilename_WithPadding_AppliesPaddingCorrectly()
    {
        // Arrange
        var template = "{series} - {issue}";
        var tags = new ComicInfo { Series = "Test" };

        // Act
        var result = ComicFileProcessor.FormatFilename(template, tags, "3", ".cbz", 6);

        // Assert
        Assert.Contains("000003", result);
    }

    [Fact]
    public void FormatFilename_WithUnknownPlaceholders_RemovesThem()
    {
        // Arrange
        var template = "{series} {unknown} {issue}";
        var tags = new ComicInfo { Series = "Test" };

        // Act
        var result = ComicFileProcessor.FormatFilename(template, tags, "1");

        // Assert
        Assert.DoesNotContain("{unknown}", result);
    }

    [Fact]
    public void FormatFilename_WithExtraSpaces_CleansUpSpaces()
    {
        // Arrange
        var template = "{series}    {issue}";
        var tags = new ComicInfo { Series = "Test" };

        // Act
        var result = ComicFileProcessor.FormatFilename(template, tags, "1");

        // Assert
        Assert.DoesNotContain("    ", result);
    }

    [Fact]
    public void FormatFilename_WithoutExtension_AddsExtension()
    {
        // Arrange
        var template = "{series} {issue}";
        var tags = new ComicInfo { Series = "Test" };

        // Act
        var result = ComicFileProcessor.FormatFilename(template, tags, "1");

        // Assert
        Assert.EndsWith(".cbz", result);
    }

    [Fact]
    public void FormatFilename_WithCbrExtension_UsesCbrExtension()
    {
        // Arrange
        var template = "{series} {issue}";
        var tags = new ComicInfo { Series = "Test" };

        // Act
        var result = ComicFileProcessor.FormatFilename(template, tags, "1", ".cbr");

        // Assert
        Assert.EndsWith(".cbr", result);
    }

    [Fact]
    public void FormatFilename_WithAllPlaceholders_ReplacesAll()
    {
        // Arrange
        var template = "{series} v{volume} #{issue} - {title} ({year}) [{publisher}]";
        var tags = new ComicInfo
        {
            Series = "Batman",
            Volume = "2",
            Title = "Dark Knight",
            Year = 2023,
            Publisher = "DC Comics"
        };

        // Act
        var result = ComicFileProcessor.FormatFilename(template, tags, "10");

        // Assert
        Assert.Contains("Batman", result);
        Assert.Contains("v2", result);
        Assert.Contains("0010", result);
        Assert.Contains("Dark Knight", result);
        Assert.Contains("2023", result);
        Assert.Contains("DC Comics", result);
    }

    [Fact]
    public void FormatFilename_WithInvalidFileNameCharacters_SanitizesCrossPlatformUnsafeCharacters()
    {
        // Arrange
        var template = "{series} - {title} #{issue}";
        var tags = new ComicInfo
        {
            Series = "Batman: Year One",
            Title = "Zero/Hour?*"
        };

        // Act
        var result = ComicFileProcessor.FormatFilename(template, tags, "10");

        // Assert
        // ": " reads as " - ", "/" as "-", and "?" and "*" are left out.
        Assert.Equal("Batman - Year One - Zero-Hour #0010.cbz", result);
    }

    [Fact]
    public void FormatFilename_WithNullTags_HandlesGracefully()
    {
        // Arrange
        var template = "{series} {issue}";
        var tags = new ComicInfo(); // All properties null

        // Act
        var result = ComicFileProcessor.FormatFilename(template, tags, "1");

        // Assert
        Assert.NotNull(result);
        Assert.EndsWith(".cbz", result);
    }
}
