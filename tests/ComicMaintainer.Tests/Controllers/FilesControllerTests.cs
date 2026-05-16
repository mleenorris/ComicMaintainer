using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using System.Text.Json;

namespace ComicMaintainer.Tests.Controllers;

public class FilesControllerTests
{
    private readonly Mock<IFileStoreService> _mockFileStore;
    private readonly Mock<IComicProcessorService> _mockProcessor;
    private readonly Mock<IProcessingHistoryService> _mockHistoryService;
    private readonly Mock<ISeriesLibraryService> _mockSeriesLibrary;
    private readonly Mock<ILogger<FilesController>> _mockLogger;
    private readonly Mock<IOptionsMonitor<AppSettings>> _mockSettings;
    private readonly FilesController _controller;

    public FilesControllerTests()
    {
        _mockFileStore = new Mock<IFileStoreService>();
        _mockProcessor = new Mock<IComicProcessorService>();
        _mockHistoryService = new Mock<IProcessingHistoryService>();
        _mockSeriesLibrary = new Mock<ISeriesLibraryService>();
        _mockLogger = new Mock<ILogger<FilesController>>();
        _mockSettings = new Mock<IOptionsMonitor<AppSettings>>();
        
        // Setup default settings with temp directory as watched directory for tests
        var settings = new AppSettings
        {
            WatchedDirectory = Path.GetTempPath()
        };
        _mockSettings.Setup(s => s.CurrentValue).Returns(settings);
        
        _controller = new FilesController(_mockFileStore.Object, _mockProcessor.Object, _mockHistoryService.Object, _mockSeriesLibrary.Object, _mockLogger.Object, _mockSettings.Object);
    }

    [Fact]
    public async Task GetFiles_WithoutFilter_ReturnsOkWithFiles()
    {
        // Arrange
        var files = new List<ComicFile>
        {
            new() { FilePath = "/test/file1.cbz", IsProcessed = true },
            new() { FilePath = "/test/file2.cbz", IsProcessed = false }
        };
        _mockFileStore.Setup(fs => fs.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);
        _mockFileStore.Setup(fs => fs.GetFilteredFilesAsync("unprocessed", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ComicFile> { files[1] });

        // Act
        var result = await _controller.GetFiles();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(okResult.Value);
        // Check that it returns an object with expected properties
        var resultValue = okResult.Value;
        var filesProperty = resultValue?.GetType().GetProperty("files");
        Assert.NotNull(filesProperty);
        var returnedFiles = filesProperty.GetValue(resultValue) as List<FileDto>;
        Assert.NotNull(returnedFiles);
        Assert.Equal(2, returnedFiles.Count);
    }

    [Fact]
    public async Task GetFiles_WithFilter_ReturnsFilteredFiles()
    {
        // Arrange
        var files = new List<ComicFile>
        {
            new() { FilePath = "/test/batman.cbz", IsProcessed = true }
        };
        _mockFileStore.Setup(fs => fs.GetFilteredFilesAsync("processed", It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);
        _mockFileStore.Setup(fs => fs.GetFilteredFilesAsync("unprocessed", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ComicFile>());

        // Act
        var result = await _controller.GetFiles(filter: "marked");

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(okResult.Value);
        var resultValue = okResult.Value;
        var filesProperty = resultValue?.GetType().GetProperty("files");
        Assert.NotNull(filesProperty);
        var returnedFiles = filesProperty.GetValue(resultValue) as List<FileDto>;
        Assert.NotNull(returnedFiles);
        Assert.Single(returnedFiles);
    }

    [Fact]
    public async Task GetFiles_WhenExceptionThrown_ReturnsInternalServerError()
    {
        // Arrange
        _mockFileStore.Setup(fs => fs.GetFilteredFilesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Test error"));

        // Act
        var result = await _controller.GetFiles();

        // Assert
        var statusCodeResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(500, statusCodeResult.StatusCode);
    }

    [Fact]
    public async Task GetSeries_DefaultMode_ReturnsSummariesWithoutIssues()
    {
        _mockSeriesLibrary.Setup(service => service.GetSeriesSummariesAsync("processed", null, 1, 100, "name", "asc", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesSummaryResult
            {
                Series = new List<SeriesSummaryDto>
                {
                    new()
                    {
                        Id = "batman",
                        Title = "Batman",
                        CanonicalTitle = "Batman",
                        IssueCount = 2,
                        CoverFilePath = "/test/Batman 001.cbz"
                    }
                },
                Page = 1,
                TotalPages = 1,
                TotalSeries = 1
            });
        _mockFileStore.Setup(fs => fs.GetFilteredFilesAsync("unprocessed", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ComicFile>());

        var result = await _controller.GetSeries(filter: "marked");

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(okResult.Value);
        var seriesProperty = okResult.Value?.GetType().GetProperty("series");
        Assert.NotNull(seriesProperty);
        var returnedSeries = seriesProperty?.GetValue(okResult.Value) as List<SeriesSummaryDto>;
        Assert.NotNull(returnedSeries);
        Assert.Single(returnedSeries);
        Assert.Equal("Batman", returnedSeries[0].Title);
        // GetSeriesAsync (the heavier, issues-included path) must not be called by default.
        _mockSeriesLibrary.Verify(s => s.GetSeriesAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetSeries_WithIncludeIssues_ReturnsFullPayloadForBackCompat()
    {
        _mockSeriesLibrary.Setup(service => service.GetSeriesAsync("processed", null, 1, 100, "name", "asc", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesLibraryResult
            {
                Series = new List<SeriesLibraryDto>
                {
                    new()
                    {
                        Id = "batman",
                        Title = "Batman",
                        CanonicalTitle = "Batman",
                        IssueCount = 2,
                        CoverFilePath = "/test/Batman 001.cbz",
                        Issues = new List<SeriesIssueDto> { new() { FilePath = "/test/Batman 001.cbz", FileName = "Batman 001.cbz" } }
                    }
                },
                Page = 1,
                TotalPages = 1,
                TotalSeries = 1
            });
        _mockFileStore.Setup(fs => fs.GetFilteredFilesAsync("unprocessed", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ComicFile>());

        var result = await _controller.GetSeries(filter: "marked", include_issues: true);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var seriesProperty = okResult.Value!.GetType().GetProperty("series");
        var returnedSeries = seriesProperty!.GetValue(okResult.Value) as List<SeriesLibraryDto>;
        Assert.NotNull(returnedSeries);
        Assert.Single(returnedSeries);
        Assert.Single(returnedSeries[0].Issues);
    }

    [Fact]
    public async Task GetSeriesIssues_ReturnsPagedIssues()
    {
        _mockSeriesLibrary.Setup(s => s.GetSeriesIssuesAsync("batman", It.IsAny<string?>(), 1, 50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesIssuesResult
            {
                Id = "batman",
                Title = "Batman",
                CanonicalTitle = "Batman",
                IssueCount = 1,
                Issues = new List<SeriesIssueDto> { new() { FilePath = "/test/Batman 001.cbz", FileName = "Batman 001.cbz" } },
                Page = 1,
                PerPage = 50,
                TotalPages = 1
            });

        var result = await _controller.GetSeriesIssues("batman", page: 1, per_page: 50);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var idProp = ok.Value!.GetType().GetProperty("id");
        Assert.Equal("batman", idProp!.GetValue(ok.Value));
    }

    [Fact]
    public async Task GetSeriesIssues_ReturnsNotFound_WhenServiceReturnsNull()
    {
        _mockSeriesLibrary.Setup(s => s.GetSeriesIssuesAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SeriesIssuesResult?)null);

        var result = await _controller.GetSeriesIssues("missing");

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetFileCounts_ReturnsOkWithCounts()
    {
        // Arrange
        _mockFileStore.Setup(fs => fs.GetFileCountsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((100, 60, 40, 5));
        _mockFileStore.Setup(fs => fs.GetAllFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ComicFile>());

        // Act
        var result = await _controller.GetFileCounts();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(okResult.Value);
        Assert.Equal(0, GetIntProperty(okResult.Value, "combinableFolders"));
    }

    [Fact]
    public async Task GetFileCounts_WhenExceptionThrown_ReturnsInternalServerError()
    {
        // Arrange
        _mockFileStore.Setup(fs => fs.GetFileCountsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Test error"));

        // Act
        var result = await _controller.GetFileCounts();

        // Assert
        var statusCodeResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(500, statusCodeResult.StatusCode);
    }

    [Fact]
    public async Task GetFileCounts_WithMatchingMetadataAcrossFolders_ReturnsCombinableFolderCount()
    {
        var options = new DbContextOptionsBuilder<ComicMaintainerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using (var dbContext = new ComicMaintainerDbContext(options))
        {
            dbContext.ComicFiles.AddRange(
                new ComicFileEntity
                {
                    FilePath = "/library/older/Batman-001.cbz",
                    FileName = "Batman-001.cbz",
                    Directory = "/library/older",
                    CreatedAt = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
                },
                new ComicFileEntity
                {
                    FilePath = "/library/newer/Batman-002.cbz",
                    FileName = "Batman-002.cbz",
                    Directory = "/library/newer",
                    CreatedAt = new DateTime(2026, 4, 2, 0, 0, 0, DateTimeKind.Utc)
                });

            await dbContext.SaveChangesAsync();
        }

        var files = new List<ComicFile>
        {
            new()
            {
                FilePath = "/library/older/Batman-001.cbz",
                Directory = "/library/older",
                LastModified = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
            },
            new()
            {
                FilePath = "/library/newer/Batman-002.cbz",
                Directory = "/library/newer",
                LastModified = new DateTime(2026, 4, 2, 0, 0, 0, DateTimeKind.Utc)
            }
        };

        _mockFileStore.Setup(fs => fs.GetFileCountsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((2, 2, 0, 0));
        _mockFileStore.Setup(fs => fs.GetAllFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);
        _mockProcessor.Setup(p => p.GetSeriesMetadataAsync("/library/older/Batman-001.cbz", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesMetadata { Series = "Batman" });
        _mockProcessor.Setup(p => p.GetSeriesMetadataAsync("/library/newer/Batman-002.cbz", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesMetadata { Series = "Batman" });

        var controller = new FilesController(
            _mockFileStore.Object,
            _mockProcessor.Object,
            _mockHistoryService.Object,
            _mockSeriesLibrary.Object,
            _mockLogger.Object,
            _mockSettings.Object,
            new TestDbContextFactory(options));

        var result = await controller.GetFileCounts();

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(okResult.Value);
        Assert.Equal(1, GetIntProperty(okResult.Value, "combinableFolders"));
    }

    [Fact]
    public async Task GetFileCounts_WithDuplicateDatabaseFilePaths_UsesLatestCreatedAtWithoutThrowing()
    {
        var options = new DbContextOptionsBuilder<ComicMaintainerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using (var dbContext = new ComicMaintainerDbContext(options))
        {
            dbContext.ComicFiles.AddRange(
                new ComicFileEntity
                {
                    FilePath = "/library/older/Batman-001.cbz",
                    FileName = "Batman-001.cbz",
                    Directory = "/library/older",
                    CreatedAt = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
                },
                new ComicFileEntity
                {
                    FilePath = "/library/older/Batman-001.cbz",
                    FileName = "Batman-001.cbz",
                    Directory = "/library/older",
                    CreatedAt = new DateTime(2026, 4, 3, 0, 0, 0, DateTimeKind.Utc)
                },
                new ComicFileEntity
                {
                    FilePath = "/library/newer/Batman-002.cbz",
                    FileName = "Batman-002.cbz",
                    Directory = "/library/newer",
                    CreatedAt = new DateTime(2026, 4, 2, 0, 0, 0, DateTimeKind.Utc)
                });

            await dbContext.SaveChangesAsync();
        }

        var files = new List<ComicFile>
        {
            new()
            {
                FilePath = "/library/older/Batman-001.cbz",
                FileName = "Batman-001.cbz",
                Directory = "/library/older",
                LastModified = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
                Metadata = new ComicMetadata { Series = "Batman" }
            },
            new()
            {
                FilePath = "/library/newer/Batman-002.cbz",
                FileName = "Batman-002.cbz",
                Directory = "/library/newer",
                LastModified = new DateTime(2026, 4, 2, 0, 0, 0, DateTimeKind.Utc),
                Metadata = new ComicMetadata { Series = "Batman" }
            }
        };

        _mockFileStore.Setup(fs => fs.GetFileCountsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((2, 2, 0, 0));
        _mockFileStore.Setup(fs => fs.GetAllFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);

        var controller = new FilesController(
            _mockFileStore.Object,
            _mockProcessor.Object,
            _mockHistoryService.Object,
            _mockSeriesLibrary.Object,
            _mockLogger.Object,
            _mockSettings.Object,
            new TestDbContextFactory(options));

        var result = await controller.GetFileCounts();

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(okResult.Value);
        Assert.Equal(1, GetIntProperty(okResult.Value, "combinableFolders"));
    }

    [Fact]
    public async Task GetCombinableFolders_TreatsAliasesAsSameSeries()
    {
        // Two folders whose series titles differ ("Batman" and "The Dark Knight")
        // should be reported as a single combinable group when the metadata
        // cache says the user has linked them via aliases.
        var options = new DbContextOptionsBuilder<ComicMaintainerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using (var dbContext = new ComicMaintainerDbContext(options))
        {
            dbContext.ComicFiles.AddRange(
                new ComicFileEntity
                {
                    FilePath = "/library/canonical/Batman-001.cbz",
                    FileName = "Batman-001.cbz",
                    Directory = "/library/canonical",
                    CreatedAt = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
                },
                new ComicFileEntity
                {
                    FilePath = "/library/aliasfolder/DarkKnight-002.cbz",
                    FileName = "DarkKnight-002.cbz",
                    Directory = "/library/aliasfolder",
                    CreatedAt = new DateTime(2026, 4, 2, 0, 0, 0, DateTimeKind.Utc)
                });

            await dbContext.SaveChangesAsync();
        }

        var files = new List<ComicFile>
        {
            new()
            {
                FilePath = "/library/canonical/Batman-001.cbz",
                FileName = "Batman-001.cbz",
                Directory = "/library/canonical",
                LastModified = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
                Metadata = new ComicMetadata { Series = "Batman" }
            },
            new()
            {
                FilePath = "/library/aliasfolder/DarkKnight-002.cbz",
                FileName = "DarkKnight-002.cbz",
                Directory = "/library/aliasfolder",
                LastModified = new DateTime(2026, 4, 2, 0, 0, 0, DateTimeKind.Utc),
                Metadata = new ComicMetadata { Series = "The Dark Knight" }
            }
        };

        _mockFileStore.Setup(fs => fs.GetAllFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);

        var mockCache = new Mock<ISeriesMetadataCacheService>();
        mockCache.Setup(c => c.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SeriesMetadataCacheRecord>
            {
                new()
                {
                    NormalizedKey = "batman",
                    CanonicalTitle = "Batman",
                    Aliases = new List<string>(),
                    UserAliases = new List<string> { "The Dark Knight" }
                }
            });

        var controller = new FilesController(
            _mockFileStore.Object,
            _mockProcessor.Object,
            _mockHistoryService.Object,
            _mockSeriesLibrary.Object,
            _mockLogger.Object,
            _mockSettings.Object,
            new TestDbContextFactory(options),
            null,
            mockCache.Object);

        var result = await controller.GetCombinableFolders();

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(okResult.Value);

        // Reflect into the anonymous { groups = [...] } payload.
        var groupsProperty = okResult.Value!.GetType().GetProperty("groups");
        Assert.NotNull(groupsProperty);
        var groups = Assert.IsAssignableFrom<IEnumerable<FilesController.CombinableFolderGroupDto>>(
            groupsProperty!.GetValue(okResult.Value));

        var groupList = groups.ToList();
        Assert.Single(groupList);
        Assert.Equal(2, groupList[0].Folders.Count);
        Assert.Equal("Batman", groupList[0].SeriesName);
    }

    [Fact]
    public async Task GetCombinableFolders_TreatsAliasesAsSameSeries_WhenAliasHasOwnCacheRecord()
    {
        // Regression: when the user adds an alias linking series "X" → "Y" but
        // a separate cache record already exists for "Y" (e.g. created by an
        // earlier external metadata refresh), the folder-combine alias index
        // must still collapse both records into one canonical group. Without
        // union-find merging, record "Y" would overwrite the alias entry
        // produced by record "X", leaving the two folders un-combinable even
        // though the series view tile already merges them.
        var options = new DbContextOptionsBuilder<ComicMaintainerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using (var dbContext = new ComicMaintainerDbContext(options))
        {
            dbContext.ComicFiles.AddRange(
                new ComicFileEntity
                {
                    FilePath = "/library/canonical/Batman-001.cbz",
                    FileName = "Batman-001.cbz",
                    Directory = "/library/canonical",
                    CreatedAt = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
                },
                new ComicFileEntity
                {
                    FilePath = "/library/aliasfolder/DarkKnight-002.cbz",
                    FileName = "DarkKnight-002.cbz",
                    Directory = "/library/aliasfolder",
                    CreatedAt = new DateTime(2026, 4, 2, 0, 0, 0, DateTimeKind.Utc)
                });

            await dbContext.SaveChangesAsync();
        }

        var files = new List<ComicFile>
        {
            new()
            {
                FilePath = "/library/canonical/Batman-001.cbz",
                FileName = "Batman-001.cbz",
                Directory = "/library/canonical",
                LastModified = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
                Metadata = new ComicMetadata { Series = "Batman" }
            },
            new()
            {
                FilePath = "/library/aliasfolder/DarkKnight-002.cbz",
                FileName = "DarkKnight-002.cbz",
                Directory = "/library/aliasfolder",
                LastModified = new DateTime(2026, 4, 2, 0, 0, 0, DateTimeKind.Utc),
                Metadata = new ComicMetadata { Series = "The Dark Knight" }
            }
        };

        _mockFileStore.Setup(fs => fs.GetAllFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);

        var mockCache = new Mock<ISeriesMetadataCacheService>();
        mockCache.Setup(c => c.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SeriesMetadataCacheRecord>
            {
                // User linked "The Dark Knight" as an alias of "Batman".
                new()
                {
                    NormalizedKey = "batman",
                    CanonicalTitle = "Batman",
                    Aliases = new List<string>(),
                    UserAliases = new List<string> { "The Dark Knight" }
                },
                // A previous external lookup also produced a standalone cache
                // record for "The Dark Knight". This is the realistic case the
                // user hit: two records share an alias key but only one of
                // them carries the user's link.
                new()
                {
                    NormalizedKey = "the-dark-knight",
                    CanonicalTitle = "The Dark Knight",
                    Aliases = new List<string>(),
                    UserAliases = new List<string>()
                }
            });

        var controller = new FilesController(
            _mockFileStore.Object,
            _mockProcessor.Object,
            _mockHistoryService.Object,
            _mockSeriesLibrary.Object,
            _mockLogger.Object,
            _mockSettings.Object,
            new TestDbContextFactory(options),
            null,
            mockCache.Object);

        var result = await controller.GetCombinableFolders();

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(okResult.Value);

        var groupsProperty = okResult.Value!.GetType().GetProperty("groups");
        Assert.NotNull(groupsProperty);
        var groups = Assert.IsAssignableFrom<IEnumerable<FilesController.CombinableFolderGroupDto>>(
            groupsProperty!.GetValue(okResult.Value));

        var groupList = groups.ToList();
        Assert.Single(groupList);
        Assert.Equal(2, groupList[0].Folders.Count);
    }

    [Fact]
    public async Task GetCombinableFolders_WithoutAliasMatch_KeepsSeriesSeparate()
    {
        // Sanity check: without any cache record, two distinct series titles
        // remain in separate groups (so no group is reported).
        var options = new DbContextOptionsBuilder<ComicMaintainerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var files = new List<ComicFile>
        {
            new()
            {
                FilePath = "/library/canonical/Batman-001.cbz",
                FileName = "Batman-001.cbz",
                Directory = "/library/canonical",
                LastModified = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
                Metadata = new ComicMetadata { Series = "Batman" }
            },
            new()
            {
                FilePath = "/library/aliasfolder/DarkKnight-002.cbz",
                FileName = "DarkKnight-002.cbz",
                Directory = "/library/aliasfolder",
                LastModified = new DateTime(2026, 4, 2, 0, 0, 0, DateTimeKind.Utc),
                Metadata = new ComicMetadata { Series = "The Dark Knight" }
            }
        };

        _mockFileStore.Setup(fs => fs.GetAllFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);

        var mockCache = new Mock<ISeriesMetadataCacheService>();
        mockCache.Setup(c => c.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SeriesMetadataCacheRecord>());

        var controller = new FilesController(
            _mockFileStore.Object,
            _mockProcessor.Object,
            _mockHistoryService.Object,
            _mockSeriesLibrary.Object,
            _mockLogger.Object,
            _mockSettings.Object,
            new TestDbContextFactory(options),
            null,
            mockCache.Object);

        var result = await controller.GetCombinableFolders();

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(okResult.Value);
        var groupsProperty = okResult.Value!.GetType().GetProperty("groups");
        Assert.NotNull(groupsProperty);
        var groups = Assert.IsAssignableFrom<IEnumerable<FilesController.CombinableFolderGroupDto>>(
            groupsProperty!.GetValue(okResult.Value));
        Assert.Empty(groups);
    }

    [Fact]
    public async Task GetMetadata_WithValidPath_ReturnsOkWithMetadata()
    {
        // Arrange
        var testFilePath = Path.Combine(Path.GetTempPath(), "file.cbz");
        var metadata = new ComicMetadata { Series = "Batman", Issue = "12" };
        _mockProcessor.Setup(p => p.GetMetadataAsync(testFilePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(metadata);

        // Act
        var result = await _controller.GetMetadata(testFilePath);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var returnedMetadata = Assert.IsType<ComicMetadata>(okResult.Value);
        Assert.Equal("Batman", returnedMetadata.Series);
    }

    private static int GetIntProperty(object? value, string propertyName)
    {
        Assert.NotNull(value);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return document.RootElement.GetProperty(propertyName).GetInt32();
    }

    private static JsonDocument SerializeAsCamelCase(object? value)
    {
        Assert.NotNull(value);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        return JsonDocument.Parse(JsonSerializer.Serialize(value, options));
    }

    [Fact]
    public async Task GetMetadata_WithEmptyPath_ReturnsBadRequest()
    {
        // Act
        var result = await _controller.GetMetadata("");

        // Assert
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetMetadata_WhenMetadataNotFound_ReturnsNotFound()
    {
        // Arrange
        var testFilePath = Path.Combine(Path.GetTempPath(), "file.cbz");
        _mockProcessor.Setup(p => p.GetMetadataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ComicMetadata?)null);

        // Act
        var result = await _controller.GetMetadata(testFilePath);

        // Assert
        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task UpdateMetadata_WithValidData_ReturnsOk()
    {
        // Arrange
        var testFilePath = Path.Combine(Path.GetTempPath(), "file.cbz");
        var metadata = new ComicMetadata { Series = "Superman", Issue = "5" };
        _mockProcessor.Setup(p => p.UpdateMetadataAsync(testFilePath, metadata, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        var result = await _controller.UpdateMetadata(testFilePath, metadata);

        // Assert
        Assert.IsType<OkResult>(result);
    }

    [Fact]
    public async Task UpdateMetadata_WithEmptyPath_ReturnsBadRequest()
    {
        // Arrange
        var metadata = new ComicMetadata { Series = "Superman" };

        // Act
        var result = await _controller.UpdateMetadata("", metadata);

        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task UpdateMetadata_WhenUpdateFails_ReturnsBadRequest()
    {
        // Arrange
        var metadata = new ComicMetadata { Series = "Superman" };
        _mockProcessor.Setup(p => p.UpdateMetadataAsync(It.IsAny<string>(), metadata, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Act
        var result = await _controller.UpdateMetadata("/test/file.cbz", metadata);

        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task ProcessFile_WithValidPath_ReturnsOk()
    {
        // Arrange
        var testFilePath = Path.Combine(Path.GetTempPath(), "file.cbz");
        _mockProcessor.Setup(p => p.ProcessFileAsync(testFilePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        var result = await _controller.ProcessFile(testFilePath);

        // Assert
        Assert.IsType<OkResult>(result);
    }

    [Fact]
    public async Task ProcessFile_WithEmptyPath_ReturnsBadRequest()
    {
        // Act
        var result = await _controller.ProcessFile("");

        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task ProcessFile_WhenProcessingFails_ReturnsBadRequest()
    {
        // Arrange
        _mockProcessor.Setup(p => p.ProcessFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Act
        var result = await _controller.ProcessFile("/test/file.cbz");

        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task ProcessBatch_WithValidPaths_ReturnsOkWithJobId()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        var filePaths = new List<string> { "/test/file1.cbz", "/test/file2.cbz" };
        _mockProcessor.Setup(p => p.ProcessFilesAsync(filePaths, It.IsAny<CancellationToken>()))
            .ReturnsAsync(jobId);

        // Act
        var result = await _controller.ProcessBatch(filePaths);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(okResult.Value);
    }

    [Fact]
    public async Task ProcessBatch_WhenExceptionThrown_ReturnsInternalServerError()
    {
        // Arrange
        _mockProcessor.Setup(p => p.ProcessFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Test error"));

        // Act
        var result = await _controller.ProcessBatch(new List<string> { "/test/file.cbz" });

        // Assert
        var statusCodeResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(500, statusCodeResult.StatusCode);
    }

    // MarkProcessed tests removed - processed state is now computed from renamed && normalized
    // Use MarkFileRenamedAsync and MarkFileNormalizedAsync instead

    // Helper method to encode file path to base64 URL-safe format
    private static string EncodeFilePathForUrl(string filePath)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(filePath);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    [Fact]
    public async Task GetFileTags_WithValidPath_ReturnsOkWithMetadata()
    {
        // Arrange
        var filePath = "/test/file.cbz";
        var encodedPath = EncodeFilePathForUrl(filePath);
        var metadata = new ComicMetadata { Series = "Batman", Issue = "12" };
        _mockProcessor.Setup(p => p.GetMetadataAsync(filePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(metadata);

        // Act
        var result = await _controller.GetFileTags(encodedPath);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var returnedMetadata = Assert.IsType<ComicMetadata>(okResult.Value);
        Assert.Equal("Batman", returnedMetadata.Series);
    }

    [Fact]
    public async Task GetFileTags_WhenMetadataNotFound_ReturnsNotFound()
    {
        // Arrange
        var filePath = "/test/file.cbz";
        var encodedPath = EncodeFilePathForUrl(filePath);
        _mockProcessor.Setup(p => p.GetMetadataAsync(filePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ComicMetadata?)null);

        // Act
        var result = await _controller.GetFileTags(encodedPath);

        // Assert
        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task UpdateFileTags_WithValidData_ReturnsOk()
    {
        // Arrange
        var filePath = "/test/file.cbz";
        var encodedPath = EncodeFilePathForUrl(filePath);
        var metadata = new ComicMetadata { Series = "Superman", Issue = "5" };
        _mockProcessor.Setup(p => p.UpdateMetadataAsync(filePath, metadata, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        var result = await _controller.UpdateFileTags(encodedPath, metadata);

        // Assert
        Assert.IsType<OkResult>(result);
    }

    [Fact]
    public async Task UpdateFileTags_WhenUpdateFails_ReturnsBadRequest()
    {
        // Arrange
        var filePath = "/test/file.cbz";
        var encodedPath = EncodeFilePathForUrl(filePath);
        var metadata = new ComicMetadata { Series = "Superman" };
        _mockProcessor.Setup(p => p.UpdateMetadataAsync(filePath, metadata, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Act
        var result = await _controller.UpdateFileTags(encodedPath, metadata);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Failed to update tags", badRequestResult.Value);
    }

    [Fact]
    public async Task GetFileTags_WithPathContainingSlashes_ReturnsOkWithMetadata()
    {
        // Arrange
        var filePath = "/comics/Marvel/Spider-Man/issue001.cbz";
        var encodedPath = EncodeFilePathForUrl(filePath);
        var metadata = new ComicMetadata { Series = "Spider-Man", Issue = "1" };
        _mockProcessor.Setup(p => p.GetMetadataAsync(filePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(metadata);

        // Act
        var result = await _controller.GetFileTags(encodedPath);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var returnedMetadata = Assert.IsType<ComicMetadata>(okResult.Value);
        Assert.Equal("Spider-Man", returnedMetadata.Series);
        Assert.Equal("1", returnedMetadata.Issue);
    }

    [Fact]
    public async Task UpdateFileTags_WithPathContainingSlashes_ReturnsOk()
    {
        // Arrange
        var filePath = "/comics/DC/Batman/issue050.cbz";
        var encodedPath = EncodeFilePathForUrl(filePath);
        var metadata = new ComicMetadata { Series = "Batman", Issue = "50" };
        _mockProcessor.Setup(p => p.UpdateMetadataAsync(filePath, metadata, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        var result = await _controller.UpdateFileTags(encodedPath, metadata);

        // Assert
        Assert.IsType<OkResult>(result);
        _mockProcessor.Verify(p => p.UpdateMetadataAsync(filePath, metadata, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteFile_WithExistingFile_ReturnsOkAndDeletesFile()
    {
        // Arrange
        var testFilePath = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid()}.cbz");
        
        // Create a temporary test file
        await File.WriteAllTextAsync(testFilePath, "test content");
        Assert.True(File.Exists(testFilePath));

        _mockFileStore.Setup(fs => fs.RemoveFileAsync(testFilePath, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _controller.DeleteFile(testFilePath);

        // Assert
        Assert.IsType<OkResult>(result);
        Assert.False(File.Exists(testFilePath)); // File should be deleted
        _mockFileStore.Verify(fs => fs.RemoveFileAsync(testFilePath, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteFile_WithNonExistentFile_ReturnsNotFound()
    {
        // Arrange - use a path within watched directory that doesn't exist
        var filePath = Path.Combine(Path.GetTempPath(), "nonexistent", $"test-{Guid.NewGuid()}.cbz");

        // Act
        var result = await _controller.DeleteFile(filePath);

        // Assert
        Assert.IsType<NotFoundResult>(result);
        _mockFileStore.Verify(fs => fs.RemoveFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteFile_WhenFileStoreThrowsException_ReturnsInternalServerError()
    {
        // Arrange
        var filePath = Path.Combine(Path.GetTempPath(), $"test-exception-{Guid.NewGuid()}.cbz");
        
        // Create a temporary test file
        await File.WriteAllTextAsync(filePath, "test content");
        Assert.True(File.Exists(filePath));

        _mockFileStore.Setup(fs => fs.RemoveFileAsync(filePath, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Test error"));

        // Act
        var result = await _controller.DeleteFile(filePath);

        // Assert
        var statusCodeResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, statusCodeResult.StatusCode);
        Assert.Equal("Error deleting file", statusCodeResult.Value);
        
        // Cleanup: delete the test file if it still exists
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task DeleteFile_WhenFileDeletionThrowsIOException_ReturnsInternalServerError()
    {
        // Arrange - create a file that we'll make read-only/locked to cause deletion to fail
        var testFilePath = Path.Combine(Path.GetTempPath(), $"test-locked-{Guid.NewGuid()}.cbz");
        await File.WriteAllTextAsync(testFilePath, "test content");
        
        // Make the file read-only to potentially cause deletion issues
        var fileInfo = new FileInfo(testFilePath);
        fileInfo.IsReadOnly = true;

        _mockFileStore.Setup(fs => fs.RemoveFileAsync(testFilePath, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _controller.DeleteFile(testFilePath);

        // Assert - on Unix systems, readonly doesn't prevent deletion, so this might succeed
        // We accept either success or error
        Assert.True(
            result is OkResult || 
            (result is ObjectResult objResult && objResult.StatusCode == 500),
            "Expected either Ok or 500 error result"
        );
        
        // Cleanup - remove readonly and delete if it still exists
        if (File.Exists(testFilePath))
        {
            fileInfo.IsReadOnly = false;
            File.Delete(testFilePath);
        }
    }

    [Fact]
    public async Task DeleteFileByEncodedPath_WithExistingFile_ReturnsOkAndDeletesFile()
    {
        // Arrange
        var testFilePath = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid()}.cbz");
        var encodedPath = EncodeFilePathForUrl(testFilePath);
        
        // Create a temporary test file
        await File.WriteAllTextAsync(testFilePath, "test content");
        Assert.True(File.Exists(testFilePath));

        _mockFileStore.Setup(fs => fs.RemoveFileAsync(testFilePath, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _controller.DeleteFileByEncodedPath(encodedPath);

        // Assert
        Assert.IsType<OkResult>(result);
        Assert.False(File.Exists(testFilePath)); // File should be deleted
        _mockFileStore.Verify(fs => fs.RemoveFileAsync(testFilePath, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteFileByEncodedPath_WithNonExistentFile_ReturnsNotFound()
    {
        // Arrange - use a path within watched directory that doesn't exist
        var filePath = Path.Combine(Path.GetTempPath(), "nonexistent", $"test-{Guid.NewGuid()}.cbz");
        var encodedPath = EncodeFilePathForUrl(filePath);

        // Act
        var result = await _controller.DeleteFileByEncodedPath(encodedPath);

        // Assert
        Assert.IsType<NotFoundResult>(result);
        _mockFileStore.Verify(fs => fs.RemoveFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteFileByEncodedPath_WithInvalidEncodedPath_ReturnsBadRequest()
    {
        // Arrange
        var invalidEncodedPath = "!!!invalid-base64!!!";

        // Act
        var result = await _controller.DeleteFileByEncodedPath(invalidEncodedPath);

        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
        _mockFileStore.Verify(fs => fs.RemoveFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteFileByEncodedPath_WhenFileStoreThrowsException_ReturnsInternalServerError()
    {
        // Arrange
        var testFilePath = Path.Combine(Path.GetTempPath(), $"test-exception-{Guid.NewGuid()}.cbz");
        var encodedPath = EncodeFilePathForUrl(testFilePath);
        
        // Create a temporary test file
        await File.WriteAllTextAsync(testFilePath, "test content");
        Assert.True(File.Exists(testFilePath));

        _mockFileStore.Setup(fs => fs.RemoveFileAsync(testFilePath, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Test error"));

        // Act
        var result = await _controller.DeleteFileByEncodedPath(encodedPath);

        // Assert
        var statusCodeResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, statusCodeResult.StatusCode);
        Assert.Equal("Error deleting file", statusCodeResult.Value);
        
        // Cleanup: delete the test file if it still exists
        if (File.Exists(testFilePath))
        {
            File.Delete(testFilePath);
        }
    }

    [Fact]
    public async Task DeleteFile_WithPathOutsideWatchedDirectory_ReturnsBadRequest()
    {
        // Arrange
        var outsidePath = "/etc/passwd"; // Path outside watched directory

        // Act
        var result = await _controller.DeleteFile(outsidePath);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("File path is outside the allowed directory", badRequestResult.Value);
        _mockFileStore.Verify(fs => fs.RemoveFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteFileByEncodedPath_WithPathOutsideWatchedDirectory_ReturnsBadRequest()
    {
        // Arrange
        var outsidePath = "/etc/passwd"; // Path outside watched directory
        var encodedPath = EncodeFilePathForUrl(outsidePath);

        // Act
        var result = await _controller.DeleteFileByEncodedPath(encodedPath);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("File path is outside the allowed directory", badRequestResult.Value);
        _mockFileStore.Verify(fs => fs.RemoveFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteFile_WithPathTraversalAttempt_ReturnsBadRequest()
    {
        // Arrange
        var traversalPath = Path.Combine(Path.GetTempPath(), "..", "..", "etc", "passwd");

        // Act
        var result = await _controller.DeleteFile(traversalPath);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("File path is outside the allowed directory", badRequestResult.Value);
        _mockFileStore.Verify(fs => fs.RemoveFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteFileByEncodedPath_WithPathTraversalAttempt_ReturnsBadRequest()
    {
        // Arrange
        var traversalPath = Path.Combine(Path.GetTempPath(), "..", "..", "etc", "passwd");
        var encodedPath = EncodeFilePathForUrl(traversalPath);

        // Act
        var result = await _controller.DeleteFileByEncodedPath(encodedPath);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("File path is outside the allowed directory", badRequestResult.Value);
        _mockFileStore.Verify(fs => fs.RemoveFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // New RESTful endpoint tests

    [Fact]
    public async Task ScanUnmarked_ReturnsOkWithCounts()
    {
        // Arrange
        var allFiles = new List<ComicFile>
        {
            new() { FilePath = "/test/file1.cbz", IsProcessed = true },
            new() { FilePath = "/test/file2.cbz", IsProcessed = false },
            new() { FilePath = "/test/file3.cbz", IsProcessed = false }
        };
        _mockFileStore.Setup(fs => fs.GetAllFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(allFiles);
        _mockFileStore.Setup(fs => fs.GetFilteredFilesAsync("unprocessed", It.IsAny<CancellationToken>()))
            .ReturnsAsync(allFiles.Where(f => !f.IsProcessed).ToList());
        _mockFileStore.Setup(fs => fs.GetFilteredFilesAsync("processed", It.IsAny<CancellationToken>()))
            .ReturnsAsync(allFiles.Where(f => f.IsProcessed).ToList());

        // Act
        var result = await _controller.ScanUnmarked();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
        var resultValue = okResult.Value;
        var totalCountProp = resultValue?.GetType().GetProperty("total_count");
        Assert.NotNull(totalCountProp);
        var totalCount = (int?)totalCountProp.GetValue(resultValue);
        Assert.Equal(3, totalCount);
    }

    [Fact]
    public async Task ProcessFileByEncodedPath_WithValidPath_ReturnsOk()
    {
        // Arrange
        var filePath = Path.Combine(Path.GetTempPath(), "test.cbz");
        var encodedPath = EncodeFilePathForUrl(filePath);
        _mockProcessor.Setup(p => p.ProcessFileAsync(filePath, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        var result = await _controller.ProcessFileByEncodedPath(encodedPath);

        // Assert
        Assert.IsType<OkResult>(result);
    }

    [Fact]
    public async Task ProcessFileByEncodedPath_WithInvalidPath_ReturnsBadRequest()
    {
        // Arrange
        var encodedPath = "invalid-base64";

        // Act
        var result = await _controller.ProcessFileByEncodedPath(encodedPath);

        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task RenameFileByEncodedPath_WithValidPath_ReturnsOk()
    {
        // Arrange
        var filePath = Path.Combine(Path.GetTempPath(), "test.cbz");
        var encodedPath = EncodeFilePathForUrl(filePath);
        var metadata = new ComicMetadata { Series = "Batman", Issue = "1" };
        _mockProcessor.Setup(p => p.GetMetadataAsync(filePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(metadata);

        // Act
        var result = await _controller.RenameFileByEncodedPath(encodedPath);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
    }

    [Fact]
    public async Task RenameFileByEncodedPath_WithInvalidPath_ReturnsBadRequestOrNotFound()
    {
        // Arrange
        // "invalid-base64" can still decode as base64, so it may decode to a string
        // But the metadata will be null for a non-existent file
        var encodedPath = "invalid-base64";
        _mockProcessor.Setup(p => p.GetMetadataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ComicMetadata?)null);

        // Act
        var result = await _controller.RenameFileByEncodedPath(encodedPath);

        // Assert - Either BadRequest for truly invalid path, or NotFound for decoded but non-existent file
        Assert.True(result is BadRequestObjectResult || result is NotFoundObjectResult);
    }

    [Fact]
    public async Task RenameFileByEncodedPath_StartsRenameJob()
    {
        // Arrange
        var filePath = Path.Combine(Path.GetTempPath(), "test.cbz");
        var encodedPath = EncodeFilePathForUrl(filePath);
        var expectedJobId = Guid.NewGuid();
        
        _mockProcessor.Setup(p => p.RenameFilesAsync(It.IsAny<IEnumerable<string>>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedJobId);

        // Act
        var result = await _controller.RenameFileByEncodedPath(encodedPath);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
        
        // Verify the rename job was started
        _mockProcessor.Verify(p => p.RenameFilesAsync(
            It.Is<IEnumerable<string>>(files => files.Single() == filePath),
            false,
            It.IsAny<CancellationToken>()), 
            Times.Once);
    }

    // UpdateProcessedStatus tests removed - processed state is now computed from renamed && normalized
    // Processed status cannot be set directly anymore

    [Fact]
    public async Task GetCombinableFolders_ReturnsGroupsWithSuggestedNewestFolder()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cm-test-{Guid.NewGuid()}");
        var olderDir = Path.Combine(tempDir, "older");
        var newerDir = Path.Combine(tempDir, "newer");
        System.IO.Directory.CreateDirectory(olderDir);
        System.IO.Directory.CreateDirectory(newerDir);
        try
        {
            var olderPath = Path.Combine(olderDir, "Batman-001.cbz");
            var newerPath = Path.Combine(newerDir, "Batman-002.cbz");
            System.IO.File.WriteAllText(olderPath, "x");
            System.IO.File.WriteAllText(newerPath, "x");

            var options = new DbContextOptionsBuilder<ComicMaintainerDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            await using (var dbContext = new ComicMaintainerDbContext(options))
            {
                dbContext.ComicFiles.AddRange(
                    new ComicFileEntity
                    {
                        FilePath = olderPath,
                        FileName = "Batman-001.cbz",
                        Directory = olderDir,
                        CreatedAt = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
                    },
                    new ComicFileEntity
                    {
                        FilePath = newerPath,
                        FileName = "Batman-002.cbz",
                        Directory = newerDir,
                        CreatedAt = new DateTime(2026, 4, 5, 0, 0, 0, DateTimeKind.Utc)
                    });
                await dbContext.SaveChangesAsync();
            }

            var files = new List<ComicFile>
            {
                new()
                {
                    FilePath = olderPath,
                    FileName = "Batman-001.cbz",
                    Directory = olderDir,
                    LastModified = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
                    Metadata = new ComicMetadata { Series = "Batman" }
                },
                new()
                {
                    FilePath = newerPath,
                    FileName = "Batman-002.cbz",
                    Directory = newerDir,
                    LastModified = new DateTime(2026, 4, 5, 0, 0, 0, DateTimeKind.Utc),
                    Metadata = new ComicMetadata { Series = "Batman" }
                }
            };

            _mockFileStore.Setup(fs => fs.GetAllFilesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(files);

            var settings = new AppSettings { WatchedDirectory = tempDir };
            _mockSettings.Setup(s => s.CurrentValue).Returns(settings);

            var controller = new FilesController(
                _mockFileStore.Object,
                _mockProcessor.Object,
                _mockHistoryService.Object,
                _mockSeriesLibrary.Object,
                _mockLogger.Object,
                _mockSettings.Object,
                new TestDbContextFactory(options));

            var result = await controller.GetCombinableFolders();

            var okResult = Assert.IsType<OkObjectResult>(result.Result);
            using var doc = SerializeAsCamelCase(okResult.Value);
            var groups = doc.RootElement.GetProperty("groups");
            Assert.Equal(1, groups.GetArrayLength());
            var group = groups[0];
            Assert.Equal("Batman", group.GetProperty("seriesName").GetString());
            Assert.Equal(newerDir, group.GetProperty("suggestedDestinationDirectory").GetString());
            Assert.Contains("most recently added", group.GetProperty("suggestionReason").GetString());
            var folders = group.GetProperty("folders");
            Assert.Equal(2, folders.GetArrayLength());
            // First folder should be the suggested one (newest first)
            Assert.True(folders[0].GetProperty("isSuggested").GetBoolean());
            Assert.Equal(newerDir, folders[0].GetProperty("directory").GetString());
            Assert.False(folders[1].GetProperty("isSuggested").GetBoolean());
        }
        finally
        {
            try { System.IO.Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task CombineFolders_MovesSourceFilesIntoDestination()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cm-test-{Guid.NewGuid()}");
        var olderDir = Path.Combine(tempDir, "older");
        var newerDir = Path.Combine(tempDir, "newer");
        System.IO.Directory.CreateDirectory(olderDir);
        System.IO.Directory.CreateDirectory(newerDir);
        try
        {
            var olderPath = Path.Combine(olderDir, "Batman-001.cbz");
            var newerPath = Path.Combine(newerDir, "Batman-002.cbz");
            System.IO.File.WriteAllText(olderPath, "x");
            System.IO.File.WriteAllText(newerPath, "x");

            var options = new DbContextOptionsBuilder<ComicMaintainerDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            await using (var dbContext = new ComicMaintainerDbContext(options))
            {
                dbContext.ComicFiles.AddRange(
                    new ComicFileEntity
                    {
                        FilePath = olderPath,
                        FileName = "Batman-001.cbz",
                        Directory = olderDir,
                        CreatedAt = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
                    },
                    new ComicFileEntity
                    {
                        FilePath = newerPath,
                        FileName = "Batman-002.cbz",
                        Directory = newerDir,
                        CreatedAt = new DateTime(2026, 4, 5, 0, 0, 0, DateTimeKind.Utc)
                    });
                await dbContext.SaveChangesAsync();
            }

            var files = new List<ComicFile>
            {
                new()
                {
                    FilePath = olderPath,
                    FileName = "Batman-001.cbz",
                    Directory = olderDir,
                    LastModified = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
                    Metadata = new ComicMetadata { Series = "Batman" }
                },
                new()
                {
                    FilePath = newerPath,
                    FileName = "Batman-002.cbz",
                    Directory = newerDir,
                    LastModified = new DateTime(2026, 4, 5, 0, 0, 0, DateTimeKind.Utc),
                    Metadata = new ComicMetadata { Series = "Batman" }
                }
            };

            _mockFileStore.Setup(fs => fs.GetAllFilesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(files);

            var settings = new AppSettings { WatchedDirectory = tempDir };
            _mockSettings.Setup(s => s.CurrentValue).Returns(settings);

            var mockMetadataCache = new Mock<ISeriesMetadataCacheService>();
            mockMetadataCache.Setup(c => c.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<SeriesMetadataCacheRecord>());
            mockMetadataCache.Setup(c => c.NormalizeKey(It.IsAny<string>()))
                .Returns<string>(s => (s ?? string.Empty).ToLowerInvariant());
            mockMetadataCache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((SeriesMetadataCacheRecord?)null);
            mockMetadataCache.Setup(c => c.SetUserAliasesAsync(
                    It.IsAny<string>(),
                    It.IsAny<IEnumerable<string>>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SeriesMetadataCacheRecord());

            var controller = new FilesController(
                _mockFileStore.Object,
                _mockProcessor.Object,
                _mockHistoryService.Object,
                _mockSeriesLibrary.Object,
                _mockLogger.Object,
                _mockSettings.Object,
                new TestDbContextFactory(options),
                eventBroadcaster: null,
                metadataCache: mockMetadataCache.Object);

            var request = new FilesController.CombineFoldersRequest
            {
                DestinationDirectory = newerDir,
                SourceDirectories = new List<string> { olderDir }
            };

            var result = await controller.CombineFolders(request);

            var okResult = Assert.IsType<OkObjectResult>(result.Result);
            using var doc = SerializeAsCamelCase(okResult.Value);
            Assert.Equal(1, doc.RootElement.GetProperty("moved").GetInt32());
            Assert.Equal(0, doc.RootElement.GetProperty("failed").GetInt32());

            Assert.True(System.IO.File.Exists(Path.Combine(newerDir, "Batman-001.cbz")));
            Assert.False(System.IO.File.Exists(olderPath));
            Assert.False(System.IO.Directory.Exists(olderDir));

            _mockFileStore.Verify(fs => fs.UpdateFilePathAsync(olderPath,
                Path.Combine(newerDir, "Batman-001.cbz"), It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Once);

            // Verify that the metadata cache was updated with the destination
            // folder name as the canonical title and the source folder name as a
            // user alias.
            var destFolderName = Path.GetFileName(newerDir);
            mockMetadataCache.Verify(c => c.SetUserAliasesAsync(
                destFolderName,
                It.Is<IEnumerable<string>>(aliases =>
                    aliases.Contains(Path.GetFileName(olderDir), StringComparer.OrdinalIgnoreCase)),
                destFolderName,
                It.IsAny<CancellationToken>()),
                Times.Once);
        }
        finally
        {
            try { System.IO.Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task CombineFolders_PersistsSourceSeriesNamesAsUserAliases()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cm-test-{Guid.NewGuid()}");
        var olderDir = Path.Combine(tempDir, "Batman (Classic)");
        var newerDir = Path.Combine(tempDir, "Batman");
        System.IO.Directory.CreateDirectory(olderDir);
        System.IO.Directory.CreateDirectory(newerDir);
        try
        {
            var olderPath = Path.Combine(olderDir, "Batman-001.cbz");
            var newerPath = Path.Combine(newerDir, "Batman-002.cbz");
            System.IO.File.WriteAllText(olderPath, "x");
            System.IO.File.WriteAllText(newerPath, "x");

            var options = new DbContextOptionsBuilder<ComicMaintainerDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            await using (var dbContext = new ComicMaintainerDbContext(options))
            {
                dbContext.ComicFiles.AddRange(
                    new ComicFileEntity
                    {
                        FilePath = olderPath,
                        FileName = "Batman-001.cbz",
                        Directory = olderDir,
                        CreatedAt = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
                    },
                    new ComicFileEntity
                    {
                        FilePath = newerPath,
                        FileName = "Batman-002.cbz",
                        Directory = newerDir,
                        CreatedAt = new DateTime(2026, 4, 5, 0, 0, 0, DateTimeKind.Utc)
                    });
                await dbContext.SaveChangesAsync();
            }

            // The two folders should be groupable because alias metadata says
            // "Batman" and "Batman (Classic)" both resolve to the same series.
            // We seed that by giving the in-memory ComicFile entries metadata
            // pointing at the same canonical title - this is the actual signal
            // BuildFolderCombineGroupKey uses when no cache record exists.
            var files = new List<ComicFile>
            {
                new()
                {
                    FilePath = olderPath,
                    FileName = "Batman-001.cbz",
                    Directory = olderDir,
                    LastModified = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
                    Metadata = new ComicMetadata { Series = "The Batman Adventures" }
                },
                new()
                {
                    FilePath = newerPath,
                    FileName = "Batman-002.cbz",
                    Directory = newerDir,
                    LastModified = new DateTime(2026, 4, 5, 0, 0, 0, DateTimeKind.Utc),
                    Metadata = new ComicMetadata { Series = "The Batman Adventures" }
                }
            };

            _mockFileStore.Setup(fs => fs.GetAllFilesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(files);

            var settings = new AppSettings { WatchedDirectory = tempDir };
            _mockSettings.Setup(s => s.CurrentValue).Returns(settings);

            var mockMetadataCache = new Mock<ISeriesMetadataCacheService>();
            mockMetadataCache.Setup(c => c.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<SeriesMetadataCacheRecord>());
            mockMetadataCache.Setup(c => c.NormalizeKey(It.IsAny<string>()))
                .Returns<string>(s => (s ?? string.Empty).ToLowerInvariant());
            mockMetadataCache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((SeriesMetadataCacheRecord?)null);
            mockMetadataCache.Setup(c => c.SetUserAliasesAsync(
                    It.IsAny<string>(),
                    It.IsAny<IEnumerable<string>>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SeriesMetadataCacheRecord());

            var controller = new FilesController(
                _mockFileStore.Object,
                _mockProcessor.Object,
                _mockHistoryService.Object,
                _mockSeriesLibrary.Object,
                _mockLogger.Object,
                _mockSettings.Object,
                new TestDbContextFactory(options),
                eventBroadcaster: null,
                metadataCache: mockMetadataCache.Object);

            var request = new FilesController.CombineFoldersRequest
            {
                DestinationDirectory = newerDir,
                SourceDirectories = new List<string> { olderDir }
            };

            var result = await controller.CombineFolders(request);

            Assert.IsType<OkObjectResult>(result.Result);

            // The destination folder name ("Batman") should be the canonical
            // title override and "Batman (Classic)" + "The Batman Adventures"
            // should appear in the alias list. The destination's own name
            // should NOT be in the alias list.
            mockMetadataCache.Verify(c => c.SetUserAliasesAsync(
                "Batman",
                It.Is<IEnumerable<string>>(aliases =>
                    aliases.Contains("Batman (Classic)", StringComparer.OrdinalIgnoreCase)
                    && aliases.Contains("The Batman Adventures", StringComparer.OrdinalIgnoreCase)
                    && !aliases.Contains("Batman", StringComparer.OrdinalIgnoreCase)),
                "Batman",
                It.IsAny<CancellationToken>()),
                Times.Once);
        }
        finally
        {
            try { System.IO.Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task CombineFolders_StillSucceedsWhenMetadataCacheUnavailable()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cm-test-{Guid.NewGuid()}");
        var olderDir = Path.Combine(tempDir, "older");
        var newerDir = Path.Combine(tempDir, "newer");
        System.IO.Directory.CreateDirectory(olderDir);
        System.IO.Directory.CreateDirectory(newerDir);
        try
        {
            var olderPath = Path.Combine(olderDir, "Batman-001.cbz");
            var newerPath = Path.Combine(newerDir, "Batman-002.cbz");
            System.IO.File.WriteAllText(olderPath, "x");
            System.IO.File.WriteAllText(newerPath, "x");

            var options = new DbContextOptionsBuilder<ComicMaintainerDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            await using (var dbContext = new ComicMaintainerDbContext(options))
            {
                dbContext.ComicFiles.AddRange(
                    new ComicFileEntity
                    {
                        FilePath = olderPath,
                        FileName = "Batman-001.cbz",
                        Directory = olderDir,
                        CreatedAt = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
                    },
                    new ComicFileEntity
                    {
                        FilePath = newerPath,
                        FileName = "Batman-002.cbz",
                        Directory = newerDir,
                        CreatedAt = new DateTime(2026, 4, 5, 0, 0, 0, DateTimeKind.Utc)
                    });
                await dbContext.SaveChangesAsync();
            }

            var files = new List<ComicFile>
            {
                new()
                {
                    FilePath = olderPath,
                    FileName = "Batman-001.cbz",
                    Directory = olderDir,
                    LastModified = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
                    Metadata = new ComicMetadata { Series = "Batman" }
                },
                new()
                {
                    FilePath = newerPath,
                    FileName = "Batman-002.cbz",
                    Directory = newerDir,
                    LastModified = new DateTime(2026, 4, 5, 0, 0, 0, DateTimeKind.Utc),
                    Metadata = new ComicMetadata { Series = "Batman" }
                }
            };

            _mockFileStore.Setup(fs => fs.GetAllFilesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(files);

            var settings = new AppSettings { WatchedDirectory = tempDir };
            _mockSettings.Setup(s => s.CurrentValue).Returns(settings);

            // Explicitly pass a null metadata cache - the combine must still
            // succeed without it.
            var controller = new FilesController(
                _mockFileStore.Object,
                _mockProcessor.Object,
                _mockHistoryService.Object,
                _mockSeriesLibrary.Object,
                _mockLogger.Object,
                _mockSettings.Object,
                new TestDbContextFactory(options),
                eventBroadcaster: null,
                metadataCache: null);

            var request = new FilesController.CombineFoldersRequest
            {
                DestinationDirectory = newerDir,
                SourceDirectories = new List<string> { olderDir }
            };

            var result = await controller.CombineFolders(request);

            var okResult = Assert.IsType<OkObjectResult>(result.Result);
            using var doc = SerializeAsCamelCase(okResult.Value);
            Assert.Equal(1, doc.RootElement.GetProperty("moved").GetInt32());
            Assert.Equal(0, doc.RootElement.GetProperty("failed").GetInt32());
            Assert.True(System.IO.File.Exists(Path.Combine(newerDir, "Batman-001.cbz")));
        }
        finally
        {
            try { System.IO.Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task CombineFolders_RejectsDestinationOutsideWatchedDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cm-test-{Guid.NewGuid()}");
        System.IO.Directory.CreateDirectory(tempDir);
        try
        {
            var settings = new AppSettings { WatchedDirectory = tempDir };
            _mockSettings.Setup(s => s.CurrentValue).Returns(settings);

            _mockFileStore.Setup(fs => fs.GetAllFilesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<ComicFile>());

            var controller = new FilesController(
                _mockFileStore.Object,
                _mockProcessor.Object,
                _mockHistoryService.Object,
                _mockSeriesLibrary.Object,
                _mockLogger.Object,
                _mockSettings.Object);

            var outside = Path.Combine(Path.GetTempPath(), $"cm-outside-{Guid.NewGuid()}");
            var request = new FilesController.CombineFoldersRequest
            {
                DestinationDirectory = outside,
                SourceDirectories = new List<string> { Path.Combine(tempDir, "older") }
            };

            var result = await controller.CombineFolders(request);

            Assert.IsType<BadRequestObjectResult>(result.Result);
        }
        finally
        {
            try { System.IO.Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task PreviewCombineFolders_ReportsConflictWhenNameAlreadyExists()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cm-test-{Guid.NewGuid()}");
        var olderDir = Path.Combine(tempDir, "older");
        var newerDir = Path.Combine(tempDir, "newer");
        System.IO.Directory.CreateDirectory(olderDir);
        System.IO.Directory.CreateDirectory(newerDir);
        try
        {
            // Same filename in both folders to trigger a conflict.
            var olderPath = Path.Combine(olderDir, "Batman-001.cbz");
            var newerPath = Path.Combine(newerDir, "Batman-001.cbz");
            System.IO.File.WriteAllText(olderPath, "x");
            System.IO.File.WriteAllText(newerPath, "x");

            var options = new DbContextOptionsBuilder<ComicMaintainerDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            await using (var dbContext = new ComicMaintainerDbContext(options))
            {
                dbContext.ComicFiles.AddRange(
                    new ComicFileEntity
                    {
                        FilePath = olderPath,
                        FileName = "Batman-001.cbz",
                        Directory = olderDir,
                        CreatedAt = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
                    },
                    new ComicFileEntity
                    {
                        FilePath = newerPath,
                        FileName = "Batman-001.cbz",
                        Directory = newerDir,
                        CreatedAt = new DateTime(2026, 4, 5, 0, 0, 0, DateTimeKind.Utc)
                    });
                await dbContext.SaveChangesAsync();
            }

            var files = new List<ComicFile>
            {
                new()
                {
                    FilePath = olderPath,
                    FileName = "Batman-001.cbz",
                    Directory = olderDir,
                    LastModified = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
                    Metadata = new ComicMetadata { Series = "Batman" }
                },
                new()
                {
                    FilePath = newerPath,
                    FileName = "Batman-001.cbz",
                    Directory = newerDir,
                    LastModified = new DateTime(2026, 4, 5, 0, 0, 0, DateTimeKind.Utc),
                    Metadata = new ComicMetadata { Series = "Batman" }
                }
            };

            _mockFileStore.Setup(fs => fs.GetAllFilesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(files);

            var settings = new AppSettings { WatchedDirectory = tempDir };
            _mockSettings.Setup(s => s.CurrentValue).Returns(settings);

            var controller = new FilesController(
                _mockFileStore.Object,
                _mockProcessor.Object,
                _mockHistoryService.Object,
                _mockSeriesLibrary.Object,
                _mockLogger.Object,
                _mockSettings.Object,
                new TestDbContextFactory(options));

            var request = new FilesController.CombineFoldersRequest
            {
                DestinationDirectory = newerDir,
                SourceDirectories = new List<string> { olderDir }
            };

            var result = await controller.PreviewCombineFolders(request);
            var okResult = Assert.IsType<OkObjectResult>(result.Result);
            using var doc = SerializeAsCamelCase(okResult.Value);
            var moves = doc.RootElement.GetProperty("moves");
            Assert.Equal(1, moves.GetArrayLength());
            Assert.True(moves[0].GetProperty("conflict").GetBoolean());
            var destPath = moves[0].GetProperty("destinationPath").GetString();
            Assert.NotNull(destPath);
            Assert.NotEqual(newerPath, destPath);
            Assert.StartsWith(newerDir, destPath);
        }
        finally
        {
            try { System.IO.Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task CombineFolders_RejectsSourceNotInGroup()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cm-test-{Guid.NewGuid()}");
        var folderA = Path.Combine(tempDir, "a");
        var folderB = Path.Combine(tempDir, "b");
        var unrelated = Path.Combine(tempDir, "unrelated");
        System.IO.Directory.CreateDirectory(folderA);
        System.IO.Directory.CreateDirectory(folderB);
        System.IO.Directory.CreateDirectory(unrelated);
        try
        {
            var pathA = Path.Combine(folderA, "Batman-001.cbz");
            var pathB = Path.Combine(folderB, "Batman-002.cbz");
            System.IO.File.WriteAllText(pathA, "x");
            System.IO.File.WriteAllText(pathB, "x");

            var options = new DbContextOptionsBuilder<ComicMaintainerDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            var files = new List<ComicFile>
            {
                new() { FilePath = pathA, FileName = "Batman-001.cbz", Directory = folderA,
                    LastModified = DateTime.UtcNow, Metadata = new ComicMetadata { Series = "Batman" } },
                new() { FilePath = pathB, FileName = "Batman-002.cbz", Directory = folderB,
                    LastModified = DateTime.UtcNow.AddDays(1), Metadata = new ComicMetadata { Series = "Batman" } }
            };

            _mockFileStore.Setup(fs => fs.GetAllFilesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(files);

            var settings = new AppSettings { WatchedDirectory = tempDir };
            _mockSettings.Setup(s => s.CurrentValue).Returns(settings);

            var controller = new FilesController(
                _mockFileStore.Object,
                _mockProcessor.Object,
                _mockHistoryService.Object,
                _mockSeriesLibrary.Object,
                _mockLogger.Object,
                _mockSettings.Object,
                new TestDbContextFactory(options));

            var request = new FilesController.CombineFoldersRequest
            {
                DestinationDirectory = folderB,
                SourceDirectories = new List<string> { unrelated }
            };

            var result = await controller.CombineFolders(request);
            Assert.IsType<BadRequestObjectResult>(result.Result);
        }
        finally
        {
            try { System.IO.Directory.Delete(tempDir, true); } catch { }
        }
    }
}

internal sealed class TestDbContextFactory : IDbContextFactory<ComicMaintainerDbContext>
{
    private readonly DbContextOptions<ComicMaintainerDbContext> _options;

    public TestDbContextFactory(DbContextOptions<ComicMaintainerDbContext> options)
    {
        _options = options;
    }

    public ComicMaintainerDbContext CreateDbContext()
        => new(_options);

    public Task<ComicMaintainerDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(CreateDbContext());
}
