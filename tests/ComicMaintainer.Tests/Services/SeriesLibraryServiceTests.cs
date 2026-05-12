using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class SeriesLibraryServiceTests
{
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<IComicProcessorService> _processor = new();
    private readonly Mock<ISeriesMetadataCacheService> _metadataCache = new();
    private readonly Mock<ILogger<SeriesLibraryService>> _logger = new();

    public SeriesLibraryServiceTests()
    {
        // Default: NormalizeKey mirrors the production sanitizer used by the real service.
        _metadataCache.Setup(m => m.NormalizeKey(It.IsAny<string>()))
            .Returns<string>(NormalizeKey);
        _metadataCache.Setup(m => m.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SeriesMetadataCacheRecord>());
    }

    [Fact]
    public async Task GetSeriesAsync_GroupsAlternateTitlesUnderCanonicalTitle()
    {
        var files = new List<ComicFile>
        {
            new() { FilePath = "/library/Batman/Batman 001.cbz", FileName = "Batman 001.cbz", FileSize = 100, LastModified = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
            new() { FilePath = "/library/Dark Knight/Dark Knight 002.cbz", FileName = "Dark Knight 002.cbz", FileSize = 200, LastModified = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc) }
        };

        _fileStore.Setup(store => store.GetFilteredFilesAsync("processed", It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);

        _processor.Setup(processor => processor.GetSeriesMetadataAsync(files[0].FilePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesMetadata { Series = "Batman", Issue = "1", Title = "Year One" });
        _processor.Setup(processor => processor.GetSeriesMetadataAsync(files[1].FilePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesMetadata { Series = "The Dark Knight", AlternateSeries = "Batman", Issue = "2", Title = "Part Two" });

        // Cached external metadata declares Batman with "Dark Knight" as an alias.
        _metadataCache.Setup(m => m.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SeriesMetadataCacheRecord>
            {
                new()
                {
                    NormalizedKey = "batman",
                    CanonicalTitle = "Batman",
                    Aliases = new List<string> { "Dark Knight", "The Dark Knight" },
                    UserAliases = new List<string>(),
                    Source = "ComicVine"
                }
            });

        var service = new SeriesLibraryService(_fileStore.Object, _processor.Object, _metadataCache.Object, _logger.Object);

        var result = await service.GetSeriesAsync(filter: "processed");

        Assert.Single(result.Series);
        Assert.Equal("Batman", result.Series[0].Title);
        Assert.Equal(2, result.Series[0].IssueCount);
        Assert.Contains(result.Series[0].Aliases, a => a.Equals("Dark Knight", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("ComicVine", result.Series[0].MetadataSource);
    }

    [Fact]
    public async Task GetSeriesAsync_GroupsFilesByFolderWhenMetadataVariesByIssue()
    {
        var files = new List<ComicFile>
        {
            new() { FilePath = "/library/Batman/Batman 001.cbz", FileName = "Batman 001.cbz", Directory = "/library/Batman", FileSize = 100, LastModified = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
            new() { FilePath = "/library/Batman/Batman 002.cbz", FileName = "Batman 002.cbz", Directory = "/library/Batman", FileSize = 200, LastModified = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc) }
        };

        _fileStore.Setup(store => store.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);

        _processor.Setup(processor => processor.GetSeriesMetadataAsync(files[0].FilePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesMetadata { Series = "Batman: Year One", Issue = "1", Title = "Year One Part 1" });
        _processor.Setup(processor => processor.GetSeriesMetadataAsync(files[1].FilePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesMetadata { Series = "Batman: Year One", AlternateSeries = "Batman (2011)", Issue = "2", Title = "Year One Part 2" });

        var service = new SeriesLibraryService(_fileStore.Object, _processor.Object, _metadataCache.Object, _logger.Object);

        var result = await service.GetSeriesAsync();

        Assert.Single(result.Series);
        Assert.Equal("Batman", result.Series[0].Title);
        Assert.Equal(2, result.Series[0].IssueCount);
        Assert.Contains("Batman: Year One", result.Series[0].Aliases);
        Assert.Contains("Batman (2011)", result.Series[0].Aliases);
    }

    [Fact]
    public async Task GetSeriesAsync_UsesCachedMetadataAndSkipsArchiveRead()
    {
        var files = new List<ComicFile>
        {
            new()
            {
                FilePath = "/library/Batman/Batman 001.cbz",
                FileName = "Batman 001.cbz",
                Directory = "/library/Batman",
                FileSize = 100,
                LastModified = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Metadata = new ComicMetadata { Series = "Batman", Issue = "1", Title = "Year One" }
            }
        };

        _fileStore.Setup(store => store.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);

        var service = new SeriesLibraryService(_fileStore.Object, _processor.Object, _metadataCache.Object, _logger.Object);

        var result = await service.GetSeriesAsync();

        Assert.Single(result.Series);
        Assert.Equal("Batman", result.Series[0].Title);
        Assert.Equal(1, result.Series[0].IssueCount);
        Assert.Equal("Year One", result.Series[0].Issues[0].Title);
        _processor.Verify(
            p => p.GetSeriesMetadataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetSeriesAsync_FallsBackToArchiveWhenCachedMetadataIsEmpty()
    {
        var files = new List<ComicFile>
        {
            new()
            {
                FilePath = "/library/Batman/Batman 001.cbz",
                FileName = "Batman 001.cbz",
                Directory = "/library/Batman",
                FileSize = 100,
                LastModified = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Metadata = new ComicMetadata() // empty
            }
        };

        _fileStore.Setup(store => store.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);

        _processor.Setup(processor => processor.GetSeriesMetadataAsync(files[0].FilePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesMetadata { Series = "Batman", Issue = "1", Title = "From Archive" });

        var service = new SeriesLibraryService(_fileStore.Object, _processor.Object, _metadataCache.Object, _logger.Object);

        var result = await service.GetSeriesAsync();

        Assert.Single(result.Series);
        Assert.Equal("From Archive", result.Series[0].Issues[0].Title);
        _processor.Verify(
            p => p.GetSeriesMetadataAsync(files[0].FilePath, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetSeriesAsync_MergesFoldersUsingUserAliases()
    {
        // Two folders ("Series A" and "Series B") with unrelated names should be
        // collapsed into a single series card when the user has added "Series B"
        // as an alias of "Series A".
        var files = new List<ComicFile>
        {
            new()
            {
                FilePath = "/library/Series A/Series A 001.cbz",
                FileName = "Series A 001.cbz",
                Directory = "/library/Series A",
                FileSize = 100,
                LastModified = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Metadata = new ComicMetadata { Series = "Series A", Issue = "1" }
            },
            new()
            {
                FilePath = "/library/Series B/Series B 002.cbz",
                FileName = "Series B 002.cbz",
                Directory = "/library/Series B",
                FileSize = 200,
                LastModified = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                Metadata = new ComicMetadata { Series = "Series B", Issue = "2" }
            }
        };

        _fileStore.Setup(store => store.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);

        _metadataCache.Setup(m => m.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SeriesMetadataCacheRecord>
            {
                new()
                {
                    NormalizedKey = "series-a",
                    CanonicalTitle = "Series A",
                    Aliases = new List<string>(),
                    UserAliases = new List<string> { "Series B" }
                }
            });

        var service = new SeriesLibraryService(_fileStore.Object, _processor.Object, _metadataCache.Object, _logger.Object);

        var result = await service.GetSeriesAsync();

        Assert.Single(result.Series);
        Assert.Equal("Series A", result.Series[0].Title);
        Assert.Equal(2, result.Series[0].IssueCount);
        Assert.Contains("Series B", result.Series[0].Aliases);
    }

    private static string NormalizeKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown-series";
        }
        var normalized = System.Text.RegularExpressions.Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "unknown-series" : normalized;
    }
}
