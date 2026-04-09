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
    private readonly Mock<IExternalSeriesMetadataService> _externalMetadata = new();
    private readonly Mock<ILogger<SeriesLibraryService>> _logger = new();

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

        _externalMetadata.Setup(metadata => metadata.LookupSeriesAsync("Batman", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalSeriesMetadata
            {
                CanonicalTitle = "Batman",
                Aliases = new List<string> { "The Dark Knight" },
                Source = "ComicVine"
            });
        _externalMetadata.Setup(metadata => metadata.LookupSeriesAsync("The Dark Knight", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalSeriesMetadata
            {
                CanonicalTitle = "Batman",
                Aliases = new List<string> { "The Dark Knight" },
                Source = "ComicVine"
            });

        var service = new SeriesLibraryService(_fileStore.Object, _processor.Object, _externalMetadata.Object, _logger.Object);

        var result = await service.GetSeriesAsync(filter: "processed");

        Assert.Single(result.Series);
        Assert.Equal("Batman", result.Series[0].Title);
        Assert.Equal(2, result.Series[0].IssueCount);
        Assert.Contains("The Dark Knight", result.Series[0].Aliases);
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

        _externalMetadata.Setup(metadata => metadata.LookupSeriesAsync("Batman", It.IsAny<CancellationToken>()))
            .ReturnsAsync((ExternalSeriesMetadata?)null);

        var service = new SeriesLibraryService(_fileStore.Object, _processor.Object, _externalMetadata.Object, _logger.Object);

        var result = await service.GetSeriesAsync();

        Assert.Single(result.Series);
        Assert.Equal("Batman", result.Series[0].Title);
        Assert.Equal(2, result.Series[0].IssueCount);
        Assert.Contains("Batman: Year One", result.Series[0].Aliases);
        Assert.Contains("Batman (2011)", result.Series[0].Aliases);
    }
}
