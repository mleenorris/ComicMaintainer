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
    // The tags' <Volume> follows the series unless the template places {volume} itself.
    [InlineData("{series} - Chapter {issue}", "Batman", "5", "Batman Vol. 1 - Chapter 0005.cbz")]
    [InlineData("{series} #{issue}", "Superman", "12", "Superman Vol. 1 #0012.cbz")]
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
        Assert.Contains("Zero_Hour__", result); // '/' '?' and '*' are each replaced individually
        Assert.Equal("Batman_ Year One - Zero_Hour__ #0010.cbz", result);
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
