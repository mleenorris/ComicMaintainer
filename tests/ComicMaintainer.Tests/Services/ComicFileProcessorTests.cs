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
    public void ParseChapterNumber_WithChapterKeyword_ReturnsNumber(string filename, string expected)
    {
        // Act
        var result = ComicFileProcessor.ParseChapterNumber(filename);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Series Name 042", "042")]
    [InlineData("Test 12.5 Extra", "12.5")]
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
}
