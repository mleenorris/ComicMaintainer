using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

/// <summary>
/// Covers the library browsing endpoints of <see cref="FilesController"/>
/// (FilesController.Browse.cs).
/// </summary>
public sealed class FilesControllerBrowseTests
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "fc-n2a-tests", Guid.NewGuid().ToString("N"));
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<IComicProcessorService> _processor = new();
    private readonly Mock<IProcessingHistoryService> _history = new();
    private readonly Mock<ISeriesLibraryService> _seriesLibrary = new();
    private readonly Mock<IOptionsMonitor<AppSettings>> _settings = new();
    private readonly FilesController _controller;

    public FilesControllerBrowseTests()
    {
        _settings.Setup(s => s.CurrentValue).Returns(new AppSettings { WatchedDirectory = _root });
        _controller = new FilesController(
            _fileStore.Object,
            _processor.Object,
            _history.Object,
            _seriesLibrary.Object,
            NullLogger<FilesController>.Instance,
            _settings.Object);
    }

    private static object? Prop(object? value, string name) => value?.GetType().GetProperty(name)?.GetValue(value);

    private static bool HasProp(object? value, string name) => value?.GetType().GetProperty(name) is not null;

    private static void AssertStatus(ActionResult? result, int expectedStatus, object? expectedValue)
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(expectedStatus, objectResult.StatusCode);
        Assert.Equal(expectedValue, objectResult.Value);
    }

    private ComicFile File(string folder, string fileName, string series, DateTime modified) => new()
    {
        FilePath = Path.Combine(_root, folder, fileName),
        FileName = fileName,
        Directory = Path.Combine(_root, folder),
        LastModified = modified,
        Metadata = new ComicMetadata { Series = series }
    };

    // ------------------------------------------------------------------- GetSeries

    [Fact]
    public async Task GetSeries_LimitWithoutOffset_UsesOffsetModeStartingAtZero()
    {
        using var cts = new CancellationTokenSource();
        _fileStore.Setup(f => f.GetUnmarkedCountAsync(cts.Token)).ReturnsAsync(4);
        _seriesLibrary
            .Setup(s => s.GetSeriesSummariesAsync(null, null, 1, 100, "name", "asc", 0, 5, cts.Token))
            .ReturnsAsync(new SeriesSummaryResult { Page = 1, TotalPages = 3, TotalSeries = 12, Offset = 0 });

        var result = await _controller.GetSeries(limit: 5, cancellationToken: cts.Token);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(0, Prop(ok.Value, "offset"));
        Assert.Equal(5, Prop(ok.Value, "limit"));
        Assert.Equal(12, Prop(ok.Value, "total_series"));
        Assert.Equal(3, Prop(ok.Value, "total_pages"));
        Assert.Equal(4, Prop(ok.Value, "unmarked_count"));
        _seriesLibrary.Verify(s => s.GetSeriesSummariesAsync(null, null, 1, 100, "name", "asc", 0, 5, cts.Token), Times.Once);
    }

    [Fact]
    public async Task GetSeries_ExplicitOffsetAndLimit_ArePassedThrough()
    {
        _seriesLibrary
            .Setup(s => s.GetSeriesSummariesAsync("processed", "bat", 1, 100, "name", "desc", 10, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesSummaryResult { Offset = 10, TotalSeries = 40 });

        var result = await _controller.GetSeries(filter: "marked", search: "bat", direction: "desc", offset: 10, limit: 5);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(10, Prop(ok.Value, "offset"));
        Assert.Equal(5, Prop(ok.Value, "limit"));
        Assert.Equal(40, Prop(ok.Value, "total_series"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task GetSeries_NonPositiveLimit_UsesPageMode(int limit)
    {
        _seriesLibrary
            .Setup(s => s.GetSeriesSummariesAsync(null, null, 2, 25, "name", "asc", null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesSummaryResult { Page = 2, TotalPages = 2, TotalSeries = 30 });

        var result = await _controller.GetSeries(page: 2, per_page: 25, offset: 50, limit: limit);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(2, Prop(ok.Value, "page"));
        Assert.False(HasProp(ok.Value, "offset"));
        Assert.False(HasProp(ok.Value, "limit"));
        _seriesLibrary.Verify(s => s.GetSeriesSummariesAsync(null, null, 2, 25, "name", "asc", null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetSeries_LibraryThrows_Returns500()
    {
        _seriesLibrary
            .Setup(s => s.GetSeriesSummariesAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("snapshot missing"));

        var result = await _controller.GetSeries();

        AssertStatus(result.Result, 500, "Error retrieving series library");
    }

    // ------------------------------------------------------------------ GetFolders

    [Fact]
    public async Task GetFolders_StoreThrows_Returns500()
    {
        _fileStore
            .Setup(f => f.GetFolderSummariesAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db locked"));

        var result = await _controller.GetFolders();

        AssertStatus(result.Result, 500, "Error retrieving folders");
    }

    // -------------------------------------------------------------- GetFolderFiles

    [Fact]
    public async Task GetFolderFiles_NullEncodedPath_QueriesEmptyFolderKey()
    {
        var dtos = new List<FileDto>();
        _fileStore
            .Setup(f => f.GetFolderFilesAsync("", "unread", null, "name", "asc", It.IsAny<CancellationToken>()))
            .ReturnsAsync(dtos);

        var result = await _controller.GetFolderFiles(null!, filter: "unread");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("", Prop(ok.Value, "path"));
        Assert.Same(dtos, Prop(ok.Value, "files"));
    }

    [Fact]
    public async Task GetFolderFiles_StoreThrows_Returns500()
    {
        _fileStore
            .Setup(f => f.GetFolderFilesAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("db locked"));

        var result = await _controller.GetFolderFiles("Zm9v");

        AssertStatus(result.Result, 500, "Error retrieving folder files");
    }

    // --------------------------------------------------------- GetRootFolderFiles

    [Fact]
    public async Task GetRootFolderFiles_QueriesTheRootFolderKeyWithTheGivenQuery()
    {
        var dtos = new List<FileDto>();
        _fileStore
            .Setup(f => f.GetFolderFilesAsync("", "unread", "bat", "date", "desc", It.IsAny<CancellationToken>()))
            .ReturnsAsync(dtos);

        var result = await _controller.GetRootFolderFiles(filter: "unread", search: "bat", sort: "date", direction: "desc");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("", Prop(ok.Value, "path"));
        Assert.Same(dtos, Prop(ok.Value, "files"));
    }

    [Fact]
    public async Task GetRootFolderFiles_StoreThrows_Returns500()
    {
        _fileStore
            .Setup(f => f.GetFolderFilesAsync("", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("db locked"));

        var result = await _controller.GetRootFolderFiles();

        AssertStatus(result.Result, 500, "Error retrieving folder files");
    }

    // ------------------------------------------------------------- GetSeriesIssues

    [Fact]
    public async Task GetSeriesIssues_LibraryThrows_Returns500()
    {
        _seriesLibrary
            .Setup(s => s.GetSeriesIssuesAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("snapshot missing"));

        var result = await _controller.GetSeriesIssues("saga");

        AssertStatus(result.Result, 500, "Error retrieving series issues");
    }

    // ------------------------------------------------------------ GetSeriesFolders

    [Fact]
    public async Task GetSeriesFolders_UnknownSeries_ReturnsNotFound()
    {
        _seriesLibrary
            .Setup(s => s.GetFoldersForSeriesIdAsync("missing", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((SeriesFoldersResult?)null);

        var result = await _controller.GetSeriesFolders("missing");

        var notFound = Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Equal("Series not found", Prop(notFound.Value, "error"));
    }

    [Fact]
    public async Task GetSeriesFolders_LibraryThrows_Returns500()
    {
        _seriesLibrary
            .Setup(s => s.GetFoldersForSeriesIdAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("snapshot missing"));

        var result = await _controller.GetSeriesFolders("saga");

        AssertStatus(result.Result, 500, "Error retrieving series folders");
    }

    [Fact]
    public async Task GetSeriesFolders_FoldersFormCombinableGroup_ReturnsThatGroupsKeyAndSuggestion()
    {
        var older = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        _fileStore.Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<ComicFile>
        {
            File("Saga A", "Saga 001.cbz", "Saga", older),
            File("Saga B", "Saga 002.cbz", "Saga", newer)
        });
        var folders = new SeriesFoldersResult
        {
            Id = "saga",
            Title = "Saga",
            Folders = new List<SeriesFolderDto>
            {
                new() { Directory = Path.Combine(_root, "Saga A"), FileCount = 1 },
                new() { Directory = Path.Combine(_root, "Saga B"), FileCount = 1 }
            }
        };
        _seriesLibrary.Setup(s => s.GetFoldersForSeriesIdAsync("saga", null, It.IsAny<CancellationToken>())).ReturnsAsync(folders);

        var result = await _controller.GetSeriesFolders("saga");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("saga", Prop(ok.Value, "id"));
        Assert.Equal("Saga", Prop(ok.Value, "title"));
        Assert.Same(folders.Folders, Prop(ok.Value, "folders"));
        Assert.Equal("saga", Prop(ok.Value, "combine_group_key"));
        Assert.Equal(Path.Combine(_root, "Saga B"), Prop(ok.Value, "suggested_destination_directory"));
    }

    [Fact]
    public async Task GetSeriesFolders_GroupLookupFails_StillReturnsFoldersWithoutCombineKey()
    {
        _fileStore.Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("store not ready"));
        _seriesLibrary.Setup(s => s.GetFoldersForSeriesIdAsync("saga", null, It.IsAny<CancellationToken>())).ReturnsAsync(new SeriesFoldersResult
        {
            Id = "saga",
            Title = "Saga",
            Folders = new List<SeriesFolderDto>
            {
                new() { Directory = Path.Combine(_root, "Saga A"), FileCount = 1 },
                new() { Directory = Path.Combine(_root, "Saga B"), FileCount = 1 }
            }
        });

        var result = await _controller.GetSeriesFolders("saga");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("saga", Prop(ok.Value, "id"));
        Assert.Null(Prop(ok.Value, "combine_group_key"));
        Assert.Null(Prop(ok.Value, "suggested_destination_directory"));
    }

    // --------------------------------------------------------------- GetFileCounts

    public static TheoryData<Exception> RecoverableCountExceptions => new()
    {
        new InvalidOperationException("store not ready"),
        new IOException("disk"),
        new ArgumentException("bad path")
    };

    [Theory]
    [MemberData(nameof(RecoverableCountExceptions))]
    public async Task GetFileCounts_CombinableCountFailsWithRecoverableError_ReportsZero(Exception failure)
    {
        _fileStore.Setup(f => f.GetFileCountsAsync(It.IsAny<CancellationToken>())).ReturnsAsync((10, 6, 4, 1));
        _fileStore.Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>())).ThrowsAsync(failure);

        var result = await _controller.GetFileCounts();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(10, Prop(ok.Value, "total"));
        Assert.Equal(6, Prop(ok.Value, "processed"));
        Assert.Equal(4, Prop(ok.Value, "unprocessed"));
        Assert.Equal(1, Prop(ok.Value, "duplicates"));
        Assert.Equal(0, Prop(ok.Value, "combinableFolders"));
    }

    [Fact]
    public async Task GetFileCounts_CombinableCountFailsWithUnexpectedError_Returns500()
    {
        _fileStore.Setup(f => f.GetFileCountsAsync(It.IsAny<CancellationToken>())).ReturnsAsync((10, 6, 4, 1));
        _fileStore.Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new NotSupportedException("nope"));

        var result = await _controller.GetFileCounts();

        AssertStatus(result.Result, 500, "Error retrieving file counts");
    }

    [Fact]
    public async Task GetFileCounts_WithHttpContext_UsesRequestAbortedTokenAndCountsSourceFolders()
    {
        using var aborted = new CancellationTokenSource();
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { RequestAborted = aborted.Token }
        };
        var when = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        _fileStore.Setup(f => f.GetFileCountsAsync(It.IsAny<CancellationToken>())).ReturnsAsync((3, 0, 3, 0));
        _fileStore.Setup(f => f.GetAllFilesAsync(aborted.Token)).ReturnsAsync(new List<ComicFile>
        {
            File("Saga A", "Saga 001.cbz", "Saga", when),
            File("Saga B", "Saga 002.cbz", "Saga", when),
            File("Saga C", "Saga 003.cbz", "Saga", when)
        });

        var result = await _controller.GetFileCounts();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(2, Prop(ok.Value, "combinableFolders"));
        _fileStore.Verify(f => f.GetAllFilesAsync(aborted.Token), Times.Once);
    }

    [Fact]
    public async Task GetFileCounts_CountQueryThrows_Returns500()
    {
        _fileStore.Setup(f => f.GetFileCountsAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new NotSupportedException("nope"));

        var result = await _controller.GetFileCounts();

        AssertStatus(result.Result, 500, "Error retrieving file counts");
    }

    // ------------------------------------------------------------------- MapFilter

    [Theory]
    [InlineData("marked", "processed")]
    [InlineData("unmarked", "unprocessed")]
    [InlineData("duplicates", "duplicates")]
    [InlineData("renamed", "renamed")]
    [InlineData("normalized", "normalized")]
    [InlineData("read", "read")]
    [InlineData("unread", "unread")]
    [InlineData("matched", "matched")]
    [InlineData("unmatched", "unmatched")]
    [InlineData("missing", "missing")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("x", null)]
    [InlineData("Marked", null)]
    [InlineData("processed", null)]
    [InlineData("marke#", null)]
    [InlineData("unrea#", null)]
    [InlineData("######", null)]
    [InlineData("rename#", null)]
    [InlineData("matche#", null)]
    [InlineData("missin#", null)]
    [InlineData("#######", null)]
    [InlineData("duplicate#", null)]
    [InlineData("normalize#", null)]
    [InlineData("##########", null)]
    [InlineData("rea#", null)]
    [InlineData("unmarke#", null)]
    [InlineData("unmatche#", null)]
    public async Task GetFiles_MapsUiFilterNamesToStoreFilters(string? uiFilter, string? expectedStoreFilter)
    {
        _fileStore
            .Setup(f => f.GetFilesPageAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedFilesResult { Page = 1, TotalPages = 1, TotalFiles = 0 });

        var result = await _controller.GetFiles(filter: uiFilter);

        Assert.IsType<OkObjectResult>(result.Result);
        _fileStore.Verify(f => f.GetFilesPageAsync(expectedStoreFilter, null, "name", "asc", 1, 100, It.IsAny<CancellationToken>()), Times.Once);
    }
}
