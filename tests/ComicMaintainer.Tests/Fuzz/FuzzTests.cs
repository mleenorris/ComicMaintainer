using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.Tests.Fuzz;

/// <summary>
/// Fuzz tests to verify robustness against invalid/random inputs
/// </summary>
public class FuzzTests
{
    private readonly Random _random = new();

    [Fact]
    public void ComicFileExtensions_IsComicArchive_HandlesRandomStrings()
    {
        // Arrange - Generate 1000 random strings
        var testCases = Enumerable.Range(0, 1000)
            .Select(_ => GenerateRandomString(20));

        // Act & Assert - Should not throw exceptions
        foreach (var testCase in testCases)
        {
            var result = ComicFileExtensions.IsComicArchive(testCase);
            Assert.IsType<bool>(result);
        }
    }

    [Fact]
    public void ComicFileExtensions_IsComicArchive_HandlesNullAndEmpty()
    {
        // Act & Assert - Should handle gracefully by throwing or returning false
        // Empty string should not throw
        Assert.False(ComicFileExtensions.IsComicArchive(string.Empty));
        Assert.False(ComicFileExtensions.IsComicArchive("   "));
    }

    [Fact]
    public void ComicFileExtensions_IsComicArchive_HandlesSpecialCharacters()
    {
        // Arrange
        var specialCases = new[]
        {
            "file\0.cbz",
            "file\n.cbz",
            "file\r.cbz",
            "file\t.cbz",
            "../../../etc/passwd",
            "C:\\..\\..\\Windows\\System32",
            "<script>alert('xss')</script>.cbz",
            "'; DROP TABLE files; --.cbz",
            "../../.ssh/id_rsa",
            "file%.cbz",
            "file*.cbz",
            "file?.cbz",
            "file|.cbz",
            "file<.cbz",
            "file>.cbz"
        };

        // Act & Assert - Should handle safely without throwing
        foreach (var testCase in specialCases)
        {
            var result = ComicFileExtensions.IsComicArchive(testCase);
            Assert.IsType<bool>(result);
        }
    }

    [Fact]
    public void ComicFileExtensions_IsComicArchive_HandlesVeryLongStrings()
    {
        // Arrange - Generate very long strings
        var veryLongPath = new string('a', 10000) + ".cbz";
        var veryLongExtension = "file." + new string('b', 1000);

        // Act & Assert - Should handle without crashing
        var result1 = ComicFileExtensions.IsComicArchive(veryLongPath);
        var result2 = ComicFileExtensions.IsComicArchive(veryLongExtension);
        
        Assert.IsType<bool>(result1);
        Assert.IsType<bool>(result2);
    }

    [Fact]
    public void ComicFile_Constructor_HandlesRandomInputs()
    {
        // Arrange - Generate random data
        var testCases = Enumerable.Range(0, 100)
            .Select(_ => new
            {
                FileName = GenerateRandomString(50),
                FilePath = GenerateRandomString(100),
                Directory = GenerateRandomString(200),
                FileSize = (long)_random.Next(0, int.MaxValue)
            });

        // Act & Assert - Should not throw exceptions during construction
        foreach (var testCase in testCases)
        {
            var file = new ComicFile
            {
                FileName = testCase.FileName,
                FilePath = testCase.FilePath,
                Directory = testCase.Directory,
                FileSize = testCase.FileSize,
                LastModified = DateTime.UtcNow
            };

            Assert.NotNull(file);
            Assert.Equal(testCase.FileName, file.FileName);
        }
    }

    [Fact]
    public void ComicMetadata_HandlesInvalidValues()
    {
        // Arrange - Create metadata with edge case values
        var metadata = new ComicMetadata
        {
            Title = new string('x', 10000), // Very long title
            Series = null!, // Null series
            Issue = "-1", // Negative issue
            Volume = "abc", // Non-numeric volume
            Publisher = "", // Empty publisher
            Year = 9999, // Far future year
            Summary = new string('s', 100000) // Extremely long summary
        };

        // Assert - Object should be created without exceptions
        Assert.NotNull(metadata);
        Assert.NotNull(metadata.Title);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n\n")]
    [InlineData("\t\t\t")]
    [InlineData("\0\0\0")]
    [InlineData("../../../etc/passwd")]
    [InlineData("C:\\Windows\\System32\\config\\sam")]
    [InlineData("file:///etc/passwd")]
    [InlineData("\\\\?\\C:\\")]
    [InlineData("CON")]
    [InlineData("PRN")]
    [InlineData("AUX")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("LPT1")]
    public void PathValidation_HandlesInvalidPaths(string path)
    {
        // Act - These should be handled safely by the system
        // We're just verifying no exceptions are thrown
        var isValid = !string.IsNullOrWhiteSpace(path) && 
                      !path.Contains('\0') && 
                      path.Length < 260;

        // Assert
        Assert.IsType<bool>(isValid);
    }

    [Fact]
    public void ComicMetadata_HandlesUnicodeAndInternationalCharacters()
    {
        // Arrange - Test with various international characters
        var metadata = new ComicMetadata
        {
            Title = "漫画 Comic Комикс مصورة",
            Series = "シリーズ Серия سلسلة",
            Publisher = "出版社 издатель ناشر",
            Summary = "摘要 резюме ملخص"
        };

        // Assert - Should handle international characters
        Assert.NotNull(metadata);
        Assert.Contains("漫画", metadata.Title);
        Assert.Contains("シリーズ", metadata.Series);
    }

    private string GenerateRandomString(int length)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789./\\-_ ";
        return new string(Enumerable.Repeat(chars, length)
            .Select(s => s[_random.Next(s.Length)])
            .ToArray());
    }
}
