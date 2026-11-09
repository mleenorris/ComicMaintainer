using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.Tests.Utilities;

/// <summary>
/// The series implied by a comic's location: its folder, or for a loose file in a library
/// root, its own file name.
/// </summary>
public class SeriesFromPathTests
{
    private static readonly string Root = Path.Join(Path.GetTempPath(), "watched_dir");
    private static readonly string Duplicates = Path.Join(Path.GetTempPath(), "watched_dir", "duplicates");

    [Theory]
    [InlineData("Batman 001.cbz", "Batman")]
    [InlineData("Batman 001 (2016) (Digital).cbz", "Batman")]
    [InlineData("Batman #12.cbz", "Batman")]
    [InlineData("Batman - Chapter 0012.cbz", "Batman")]
    [InlineData("Tower of God - Ch. 12.cbz", "Tower of God")]
    [InlineData("Tower of God Chapter 5.5.cbz", "Tower of God")]
    [InlineData("[Group] One Piece 1050 [1080p].cbz", "One Piece")]
    [InlineData("Saga v01 012.cbz", "Saga")]
    [InlineData("Saga Vol. 2 Chapter 12.cbz", "Saga")]
    [InlineData("20th Century Boys 001.cbz", "20th Century Boys")]
    [InlineData("Batman_Superman_001.cbz", "Batman Superman")]
    [InlineData("Batman No. 5.cbz", "Batman")]
    [InlineData("Watchmen.cbz", "Watchmen")]
    public void FromFileName_TakesTheTextBeforeTheChapterNumber(string fileName, string expected)
    {
        Assert.Equal(expected, SeriesFromPath.FromFileName(Path.Join(Root, fileName)));
    }

    [Theory]
    [InlineData("001.cbz")]
    [InlineData("Chapter 12.cbz")]
    [InlineData("#5.cbz")]
    [InlineData("Vol 1.cbz")]
    [InlineData("(2016).cbz")]
    public void FromFileName_NoSeriesInTheName_ReturnsNull(string fileName)
    {
        Assert.Null(SeriesFromPath.FromFileName(Path.Join(Root, fileName)));
    }

    [Fact]
    public void IsDirectlyInLibraryRoot_FileInTheWatchedOrDuplicatesRoot_IsTrue()
    {
        Assert.True(SeriesFromPath.IsDirectlyInLibraryRoot(Path.Join(Root, "Batman 001.cbz"), Root, Duplicates));
        Assert.True(SeriesFromPath.IsDirectlyInLibraryRoot(Path.Join(Duplicates, "Batman 001.cbz"), Root, Duplicates));
        Assert.True(SeriesFromPath.IsDirectlyInLibraryRoot(Path.Join(Root, "Batman 001.cbz"), Root + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void IsDirectlyInLibraryRoot_FileInASeriesFolder_IsFalse()
    {
        Assert.False(SeriesFromPath.IsDirectlyInLibraryRoot(Path.Join(Root, "Batman", "Batman 001.cbz"), Root, Duplicates));
        Assert.False(SeriesFromPath.IsDirectlyInLibraryRoot(Path.Join(Root, "Batman", "Batman 001.cbz"), null, " "));
        Assert.False(SeriesFromPath.IsDirectlyInLibraryRoot(string.Empty, Root));
    }

    [Fact]
    public void FromFolder_UsesTheNormalizedFolderNameButNotALibraryRoot()
    {
        Assert.Equal("Batman:The Dark Knight", SeriesFromPath.FromFolder(Path.Join(Root, "Batman_The Dark Knight", "001.cbz"), Root));
        Assert.Null(SeriesFromPath.FromFolder(Path.Join(Root, "Batman 001.cbz"), Root, Duplicates));
        Assert.Null(SeriesFromPath.FromFolder(Path.Join(Duplicates, "Batman 001.cbz"), Root, Duplicates));
    }

    // Paths the operating system would reject are not "in" anything.
    [Fact]
    public void IsDirectlyInLibraryRoot_UnusableFilePath_IsFalse()
    {
        Assert.False(SeriesFromPath.IsDirectlyInLibraryRoot("bad\0name.cbz", Root));
    }

    [Fact]
    public void IsDirectlyInLibraryRoot_AnUnusableRootIsSkipped()
    {
        Assert.True(SeriesFromPath.IsDirectlyInLibraryRoot(Path.Join(Root, "Batman 001.cbz"), "bad\0root", Root));
        Assert.False(SeriesFromPath.IsDirectlyInLibraryRoot(Path.Join(Root, "Batman 001.cbz"), "bad\0root"));
    }

    [Fact]
    public void IsDirectlyInLibraryRoot_ThePathOfADriveRoot_HasNoParentFolder()
    {
        var driveRoot = Path.GetPathRoot(Path.GetTempPath())!;

        Assert.False(SeriesFromPath.IsDirectlyInLibraryRoot(driveRoot, Root));
    }

    [Fact]
    public void FromFolder_AFileDirectlyOnADrive_HasNoSeriesFolder()
    {
        var driveRoot = Path.GetPathRoot(Path.GetTempPath())!;

        Assert.Null(SeriesFromPath.FromFolder(Path.Join(driveRoot, "Batman 001.cbz")));
    }

    [Fact]
    public void FromFileName_ANameThatIsOnlyAnExtension_HasNoSeries()
    {
        Assert.Null(SeriesFromPath.FromFileName(Path.Join(Root, ".cbz")));
    }
}
