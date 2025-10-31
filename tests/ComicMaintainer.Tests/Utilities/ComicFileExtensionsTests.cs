using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.Tests.Utilities;

public class ComicFileExtensionsTests
{
    [Theory]
    [InlineData("test.cbz", true)]
    [InlineData("test.CBZ", true)]
    [InlineData("test.cbr", true)]
    [InlineData("test.CBR", true)]
    [InlineData("test.zip", true)]
    [InlineData("test.ZIP", true)]
    [InlineData("test.rar", true)]
    [InlineData("test.RAR", true)]
    [InlineData("test.txt", false)]
    [InlineData("test.pdf", false)]
    [InlineData("test.jpg", false)]
    [InlineData("test", false)]
    public void IsComicArchive_VariousExtensions_ReturnsCorrectResult(string fileName, bool expected)
    {
        // Act
        var result = ComicFileExtensions.IsComicArchive(fileName);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("test.cbz", true)]
    [InlineData("test.CBZ", true)]
    [InlineData("test.cbr", false)]
    [InlineData("test.zip", false)]
    [InlineData("test.rar", false)]
    [InlineData("test.txt", false)]
    public void IsWritableArchive_VariousExtensions_ReturnsCorrectResult(string fileName, bool expected)
    {
        // Act
        var result = ComicFileExtensions.IsWritableArchive(fileName);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(".zip", ".cbz")]
    [InlineData(".ZIP", ".cbz")]
    [InlineData(".rar", ".cbr")]
    [InlineData(".RAR", ".cbr")]
    [InlineData(".cbz", ".cbz")]
    [InlineData(".cbr", ".cbr")]
    [InlineData(".CBZ", ".cbz")]
    [InlineData(".txt", ".txt")]
    public void NormalizeExtension_VariousExtensions_ReturnsNormalizedResult(string extension, string expected)
    {
        // Act
        var result = ComicFileExtensions.NormalizeExtension(extension);

        // Assert
        Assert.Equal(expected, result);
    }
}
