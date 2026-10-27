using ComicMaintainer.Core.Services;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Covers the static folder-key helpers on <see cref="FileStoreService"/> that map file and
/// directory paths onto watched-directory-relative, forward-slash folder keys.
/// </summary>
public class FileStoreServiceFolderKeyTests
{
    [Theory]
    [InlineData("", "/library", "")]
    [InlineData("/", "/library", "")]
    [InlineData("Saga 001.cbz", "/library", "")]
    [InlineData("/library/Saga 001.cbz", "/library/", "")]
    [InlineData("/library/Image/Saga/Saga 001.cbz", "/library", "Image/Saga")]
    [InlineData("/elsewhere/Saga 001.cbz", "/library", "/elsewhere")]
    [InlineData("/library2/Saga 001.cbz", "/library", "/library2")]
    [InlineData("/data/Image/Saga 001.cbz", null, "/data/Image")]
    [InlineData("/data/Image/Saga 001.cbz", "", "/data/Image")]
    public void ComputeFolderKey_MapsFileDirectoryRelativeToWatchedDirectory(string filePath, string? watched, string expected)
    {
        Assert.Equal(expected, FileStoreService.ComputeFolderKey(filePath, watched));
    }

    [Fact]
    public void ComputeFolderKey_InvalidWatchedDirectory_ReturnsRootKey()
    {
        Assert.Equal(string.Empty, FileStoreService.ComputeFolderKey("/library/Saga/Saga 001.cbz", "/lib\0rary"));
    }

    [Theory]
    [InlineData(null, "/library", "")]
    [InlineData("", "/library", "")]
    [InlineData("/library/", "/library", "")]
    [InlineData("/library/Image/Saga", "/library/", "Image/Saga")]
    [InlineData("/elsewhere/Saga", "/library", "/elsewhere/Saga")]
    [InlineData("/library2", "/library", "/library2")]
    [InlineData("Image\\Saga", null, "Image/Saga")]
    [InlineData("Image\\Saga", "", "Image/Saga")]
    public void ComputeFolderKeyFromDirectory_MapsDirectoryRelativeToWatchedDirectory(string? directory, string? watched, string expected)
    {
        Assert.Equal(expected, FileStoreService.ComputeFolderKeyFromDirectory(directory, watched));
    }

    [Fact]
    public void ComputeFolderKeyFromDirectory_InvalidDirectory_ReturnsRootKey()
    {
        Assert.Equal(string.Empty, FileStoreService.ComputeFolderKeyFromDirectory("/library/Sa\0ga", "/library"));
    }

    [Fact]
    public void FolderKeys_FolderDifferingFromTheWatchedDirectoryOnlyInCase_FollowThePlatformsPathComparison()
    {
        // Regression: containment ignored case on every platform, so on a case-sensitive file
        // system "/Library/Image" counted as inside "/library" and Path.GetRelativePath (which
        // does honour case there) turned it into the key "../Library/Image".
        var expected = OperatingSystem.IsWindows() ? "Image" : "/Library/Image";

        Assert.Equal(expected, FileStoreService.ComputeFolderKey("/Library/Image/Saga 001.cbz", "/library"));
        Assert.Equal(expected, FileStoreService.ComputeFolderKeyFromDirectory("/Library/Image", "/library"));
    }
}
