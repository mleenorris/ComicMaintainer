using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;

namespace ComicMaintainer.Tests.Services;

public class SeriesFileMatcherTests
{
    private static readonly SeriesMetadataCacheRecord OnePiece = new()
    {
        NormalizedKey = "one-piece",
        CanonicalTitle = "One Piece",
        SeriesName = "ONE PIECE (Pinned)",
        Aliases = new List<string> { "Wan Pisu", " ", "one piece" },
        UserAliases = new List<string> { "OP" },
        LocalizedTitles = new List<LocalizedTitle> { new("ワンピース", "ja"), null!, new("  ", "en") },
    };

    private static ComicFile Untracked(string path) => new() { FilePath = path };

    private static ComicFile Edited(string path, string series) => new()
    {
        FilePath = path,
        Metadata = new ComicMetadata { Series = series },
    };

    [Fact]
    public void TitlesOf_ListsEveryNameTheSeriesIsKnownBy_Once()
    {
        Assert.Equal(
            new[] { "One Piece", "ONE PIECE (Pinned)", "Wan Pisu", "OP", "ワンピース" },
            SeriesFileMatcher.TitlesOf(OnePiece));
    }

    [Fact]
    public void KeysOf_AddsTheRequestedTitles_AndSkipsTitlesWithoutKeys()
    {
        var keys = SeriesFileMatcher.KeysOf(OnePiece, "One_Piece_Folder", "???", null);

        Assert.Equal(
            new[] { "one-piece", "one-piece-pinned", "wan-pisu", "op", "ワンピース", "one-piece-folder" }.OrderBy(k => k, StringComparer.Ordinal),
            keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void KeysOf_IncludesTheRecordsOwnKey_SoAManualMatchStillFindsItsFolder()
    {
        // Apply-match keeps the record under the folder title it was first
        // looked up by, while its titles become those of the matched entry.
        var matched = new SeriesMetadataCacheRecord
        {
            NormalizedKey = "kimetsu-no-yaiba",
            CanonicalTitle = "Demon Slayer",
            Aliases = new List<string> { "Demon Slayer: Blade of Demon Destruction" },
        };

        var keys = SeriesFileMatcher.KeysOf(matched);

        Assert.Contains("kimetsu-no-yaiba", keys);
        Assert.True(SeriesFileMatcher.BelongsTo(Untracked("/library/Kimetsu no Yaiba/c001.cbz"), keys));
        Assert.True(SeriesFileMatcher.BelongsTo(Untracked("/library/Demon Slayer/c001.cbz"), keys));
    }

    [Theory]
    [InlineData("unknown-series")]
    [InlineData("")]
    [InlineData("--")]
    public void KeysOf_SkipsAnOwnKeyThatNamesNoTitle(string normalizedKey)
    {
        var record = new SeriesMetadataCacheRecord { NormalizedKey = normalizedKey, CanonicalTitle = "Naruto" };

        Assert.Equal(new[] { "naruto" }, SeriesFileMatcher.KeysOf(record));
    }

    [Theory]
    [InlineData("/library/One Piece/c001.cbz")]
    [InlineData("/library/one_piece/c001.cbz")]
    [InlineData("/library/WAN PISU!/c001.cbz")]
    [InlineData("/library/ワンピース/c001.cbz")]
    public void BelongsTo_AFileWithoutTrackedMetadata_ByItsFolder(string path)
    {
        Assert.True(SeriesFileMatcher.BelongsTo(Untracked(path), SeriesFileMatcher.KeysOf(OnePiece)));
    }

    [Fact]
    public void BelongsTo_AnEditedFile_ByItsSeriesWhereverItLives()
    {
        var keys = SeriesFileMatcher.KeysOf(OnePiece);

        Assert.True(SeriesFileMatcher.BelongsTo(Edited("/library/Unsorted/c001.cbz", "wan-pisu"), keys));
        // The folder still counts when the edited <Series> names another series.
        Assert.True(SeriesFileMatcher.BelongsTo(Edited("/library/One Piece/c002.cbz", "Naruto"), keys));
        Assert.False(SeriesFileMatcher.BelongsTo(Edited("/library/Naruto/c001.cbz", "Naruto"), keys));
    }

    [Theory]
    [InlineData("/library/One Piece Party/c001.cbz")]
    [InlineData("/library/c001.cbz")]
    [InlineData("c001.cbz")]
    [InlineData("")]
    public void BelongsTo_OtherSeriesAndLooseFiles_DoNotMatch(string path)
    {
        Assert.False(SeriesFileMatcher.BelongsTo(Untracked(path), SeriesFileMatcher.KeysOf(OnePiece)));
    }

    [Fact]
    public void BelongsTo_NoKeys_MatchesNothing()
    {
        Assert.False(SeriesFileMatcher.BelongsTo(Untracked("/library/One Piece/c001.cbz"), new HashSet<string>()));
    }

    [Fact]
    public void SelectFilePaths_KeepsTheFirstOfEachPathInOrder()
    {
        var files = new[]
        {
            Untracked("/library/One Piece/c002.cbz"),
            Untracked("/library/Naruto/c001.cbz"),
            Untracked("/library/One Piece/c001.cbz"),
            Untracked("/library/One Piece/c002.cbz"),
            null!,
            Untracked("   "),
        };

        Assert.Equal(
            new[] { "/library/One Piece/c002.cbz", "/library/One Piece/c001.cbz" },
            SeriesFileMatcher.SelectFilePaths(files, SeriesFileMatcher.KeysOf(OnePiece)));
    }

    [Fact]
    public void SelectFilePaths_KeepsPathsThatDifferOnlyInCase()
    {
        // Two distinct files on a case-sensitive filesystem.
        var files = new[]
        {
            Untracked("/library/One Piece/c001.cbz"),
            Untracked("/library/One Piece/C001.cbz"),
        };

        Assert.Equal(
            new[] { "/library/One Piece/c001.cbz", "/library/One Piece/C001.cbz" },
            SeriesFileMatcher.SelectFilePaths(files, SeriesFileMatcher.KeysOf(OnePiece)));
    }

    [Theory]
    [InlineData("/library/Batman_ Year One/c001.cbz", "Batman: Year One")]
    [InlineData("/library/c001.cbz", "library")]
    [InlineData("c001.cbz", null)]
    [InlineData(null, null)]
    public void FolderSeriesOf_IsTheParentFolderNameAsDisplayed(string? path, string? expected)
    {
        Assert.Equal(expected, SeriesFileMatcher.FolderSeriesOf(path));
    }
}
