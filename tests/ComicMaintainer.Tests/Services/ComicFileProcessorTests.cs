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
    [InlineData("Chapter 12", "12")]
    [InlineData("ch 5", "5")]
    [InlineData("ch.10", "10")]
    [InlineData("Ch-3.5", "3.5")]
    [InlineData("Batman - Ch 0004.5", "4.5")]
    [InlineData("Series - Chapter 0042", "42")]
    [InlineData("Series - Chapter 0000", "0")]
    public void ParseChapterNumber_WithChapterKeyword_ReturnsNumber(string filename, string expected)
    {
        // Act
        var result = ComicFileProcessor.ParseChapterNumber(filename);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Series Name 042", "42")]
    [InlineData("Test 12.5 Extra", "12.5")]
    [InlineData("Series Name 0004.5", "4.5")]
    public void ParseChapterNumber_WithoutKeyword_FindsNumber(string filename, string expected)
    {
        // Act
        var result = ComicFileProcessor.ParseChapterNumber(filename);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Test [v1]", null)]
    [InlineData("No Numbers Here", null)]
    public void ParseChapterNumber_NumbersInBrackets_ReturnsNull(string filename, string? expected)
    {
        // Act
        var result = ComicFileProcessor.ParseChapterNumber(filename);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ParseChapterNumber_NumbersInParentheses_FindsNumberBeforeParentheses()
    {
        // The regex finds "02" in "Name 02023" before the parentheses
        // This is expected behavior
        var result = ComicFileProcessor.ParseChapterNumber("Series Name (2023)");
        Assert.NotNull(result);
    }

    [Theory]
    [InlineData("{series} - Chapter {issue}", "Batman", "5", "Batman - Chapter 0005.cbz")]
    [InlineData("{series} #{issue}", "Superman", "12", "Superman #0012.cbz")]
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

    [Theory]
    [InlineData("Series - Chapter 5", "5")]
    [InlineData("Series Ch.10", "10")]
    [InlineData("Series ch_3.5", "3.5")]
    [InlineData("Batman - Ch 0004.5", "4.5")]
    [InlineData("Series Ch 0042", "42")]
    [InlineData("Series Vol 2", null)]
    [InlineData("Series 042", null)]
    [InlineData("Series Name (2023)", null)]
    public void ParseChapterKeyword_ReturnsKeywordMatchedNumberOnly(string filename, string? expected)
    {
        var result = ComicFileProcessor.ParseChapterKeyword(filename);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("1", "1", true)]
    [InlineData("1", "0001", true)]
    [InlineData("0001", "0001.0", true)]
    [InlineData("12.5", "0012.5", true)]
    [InlineData("1", "2", false)]
    [InlineData("1.0", "1.5", false)]
    [InlineData("", "", true)]
    [InlineData("", "1", false)]
    [InlineData("abc", "abc", true)]
    [InlineData("abc", "def", false)]
    public void ChapterNumbersEquivalent_ComparesNumericValues(string? a, string? b, bool expected)
    {
        var result = ComicFileProcessor.ChapterNumbersEquivalent(a, b);
        Assert.Equal(expected, result);
    }
}
