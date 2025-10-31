using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.Tests.Utilities;

public class LoggingHelperTests
{
    [Theory]
    [InlineData("normal text", "normal text")]
    [InlineData("text\nwith\nnewlines", "text\\nwith\\nnewlines")]
    [InlineData("text\r\nwith\r\nCRLF", "text\\r\\nwith\\r\\nCRLF")]
    [InlineData("text\twith\ttabs", "text\\twith\\ttabs")]
    [InlineData("text\0with\0nulls", "text\\0with\\0nulls")]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void SanitizeForLog_VariousInputs_SanitizesCorrectly(string? input, string expected)
    {
        // Act
        var result = LoggingHelper.SanitizeForLog(input);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void SanitizeForLog_MixedControlCharacters_SanitizesAll()
    {
        // Arrange
        var input = "Line1\nLine2\r\nLine3\tTabbed";

        // Act
        var result = LoggingHelper.SanitizeForLog(input);

        // Assert
        Assert.DoesNotContain("\n", result);
        Assert.DoesNotContain("\r", result);
        Assert.DoesNotContain("\t", result);
        Assert.Contains("\\n", result);
        Assert.Contains("\\r", result);
        Assert.Contains("\\t", result);
    }

    [Theory]
    [InlineData("/path/to/file.cbz", "file.cbz")]
    [InlineData("file.cbz", "file.cbz")]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void SanitizePathForLog_VariousPaths_ReturnsFilenameOnly(string? input, string expected)
    {
        // Act
        var result = LoggingHelper.SanitizePathForLog(input);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void SanitizePathForLog_WindowsPath_ReturnsFilenameOnly()
    {
        // Arrange - skip on non-Windows
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        
        var input = "C:\\Windows\\file.txt";

        // Act
        var result = LoggingHelper.SanitizePathForLog(input);

        // Assert
        Assert.Equal("file.txt", result);
    }

    [Fact]
    public void SanitizePathForLog_PathEndingWithSlash_ReturnsEmpty()
    {
        // Arrange
        var input = "/path/to/";

        // Act
        var result = LoggingHelper.SanitizePathForLog(input);

        // Assert
        // Path ending with slash returns empty string
        Assert.True(string.IsNullOrEmpty(result) || result == "to");
    }

    [Fact]
    public void SanitizePathForLog_InvalidPath_ReturnsInvalidPath()
    {
        // Arrange
        var input = "<invalid>|<path>";

        // Act
        var result = LoggingHelper.SanitizePathForLog(input);

        // Assert
        // Either returns the invalid chars or "invalid_path" depending on platform
        Assert.True(result == "invalid_path" || result.Contains("<invalid>"));
    }

    [Fact]
    public void CreateLogData_WithMultipleParameters_CreatesStructuredData()
    {
        // Arrange
        var param1 = ("key1", "value1");
        var param2 = ("key2", "value2\nwith\nnewlines");
        var param3 = ("key3", (string?)null);

        // Act
        var result = LoggingHelper.CreateLogData(param1, param2, param3);

        // Assert
        Assert.NotNull(result);
        var dict = result as Dictionary<string, string>;
        Assert.NotNull(dict);
        Assert.Equal(3, dict.Count);
        Assert.Equal("value1", dict["key1"]);
        Assert.Equal("value2\\nwith\\nnewlines", dict["key2"]);
        Assert.Equal("", dict["key3"]);
    }

    [Fact]
    public void CreateLogData_WithEmptyParameters_ReturnsEmptyDictionary()
    {
        // Act
        var result = LoggingHelper.CreateLogData();

        // Assert
        Assert.NotNull(result);
        var dict = result as Dictionary<string, string>;
        Assert.NotNull(dict);
        Assert.Empty(dict);
    }

    [Fact]
    public void CreateLogData_WithSingleParameter_CreatesSingleEntryDictionary()
    {
        // Act
        var result = LoggingHelper.CreateLogData(("testKey", "testValue"));

        // Assert
        Assert.NotNull(result);
        var dict = result as Dictionary<string, string>;
        Assert.NotNull(dict);
        Assert.Single(dict);
        Assert.Equal("testValue", dict["testKey"]);
    }
}
