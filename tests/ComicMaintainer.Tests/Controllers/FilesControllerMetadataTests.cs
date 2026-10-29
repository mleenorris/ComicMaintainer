using System.Text;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.WebApi.Controllers;
using ComicMaintainer.WebApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

/// <summary>
/// Covers the metadata endpoints of <see cref="FilesController"/>
/// (FilesController.Metadata.cs) plus the before/after history logging helper.
/// </summary>
public sealed class FilesControllerMetadataTests : IDisposable
{
    private const string OutsidePath = "/etc/passwd";

    private readonly string _root;
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<IComicProcessorService> _processor = new();
    private readonly Mock<IProcessingHistoryService> _history = new();
    private readonly Mock<ISeriesLibraryService> _seriesLibrary = new();
    private readonly Mock<IOptionsMonitor<AppSettings>> _settings = new();
    private readonly Mock<IEventBroadcaster> _broadcaster = new();
    private readonly Mock<ISeriesNameResolver> _resolver = new();
    private readonly List<ProcessingHistoryEntry> _historyEntries = new();

    public FilesControllerMetadataTests()
    {
        _root = Path.Combine(AppContext.BaseDirectory, "fc-n2a-tests", Guid.NewGuid().ToString("N"));
        _settings.Setup(s => s.CurrentValue).Returns(new AppSettings { WatchedDirectory = _root });
        _history
            .Setup(h => h.AddHistoryEntryAsync(It.IsAny<ProcessingHistoryEntry>(), It.IsAny<CancellationToken>()))
            .Callback<ProcessingHistoryEntry, CancellationToken>((entry, _) =>
            {
                lock (_historyEntries)
                {
                    _historyEntries.Add(entry);
                }
            })
            .Returns(Task.CompletedTask);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private FilesController CreateController(
        IEventBroadcaster? broadcaster = null,
        ISeriesNameResolver? resolver = null,
        ScheduledJobsHostedService? scheduledJobs = null) =>
        new(
            _fileStore.Object,
            _processor.Object,
            _history.Object,
            _seriesLibrary.Object,
            NullLogger<FilesController>.Instance,
            _settings.Object,
            eventBroadcaster: broadcaster,
            seriesNameResolver: resolver,
            scheduledJobsHostedService: scheduledJobs);

    private string InRoot(params string[] parts) => Path.Combine(new[] { _root }.Concat(parts).ToArray());

    // Path theories use "{root}" for the watched directory, e.g. to build a
    // sibling directory that shares its name prefix.
    private string Expand(string path) => path.Replace("{root}", _root);

    private string CreateRealFile(string name)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        return path;
    }

    private static string Encode(string path) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(path)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static object? Prop(object? value, string name) => value?.GetType().GetProperty(name)?.GetValue(value);

    private static void AssertStatus(ActionResult? result, int expectedStatus, object? expectedValue)
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(expectedStatus, objectResult.StatusCode);
        Assert.Equal(expectedValue, objectResult.Value);
    }

    private ProcessingHistoryEntry SingleHistoryEntry()
    {
        lock (_historyEntries)
        {
            return Assert.Single(_historyEntries);
        }
    }

    private static ComicMetadata MetadataWithField(string field) => field switch
    {
        "series" => new ComicMetadata { Series = "Saga" },
        "title" => new ComicMetadata { Title = "Chapter One" },
        "issue" => new ComicMetadata { Issue = "1" },
        "volume" => new ComicMetadata { Volume = "2" },
        "publisher" => new ComicMetadata { Publisher = "Image" },
        "year" => new ComicMetadata { Year = 2012 },
        "summary" => new ComicMetadata { Summary = "A story" },
        "authors" => new ComicMetadata { Authors = new List<string> { "Brian K. Vaughan" } },
        "tags" => new ComicMetadata { Tags = new List<string> { "space" } },
        _ => new ComicMetadata()
    };

    private sealed class InMemoryDbFactory(DbContextOptions<ComicMaintainerDbContext> options)
        : IDbContextFactory<ComicMaintainerDbContext>
    {
        public ComicMaintainerDbContext CreateDbContext() => new(options);

        public Task<ComicMaintainerDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    // ---------------------------------------------------------------- GetMetadata

    [Fact]
    public async Task GetMetadata_PathOutsideWatchedDirectory_ReturnsBadRequestWithoutReading()
    {
        var result = await CreateController().GetMetadata(OutsidePath);

        AssertStatus(result.Result, 400, "File path is outside the allowed directory");
        _processor.Verify(p => p.GetMetadataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetMetadata_ProcessorThrows_Returns500()
    {
        var path = InRoot("Saga 001.cbz");
        _processor.Setup(p => p.GetMetadataAsync(path, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("archive is corrupt"));

        var result = await CreateController().GetMetadata(path);

        AssertStatus(result.Result, 500, "Error retrieving metadata");
    }

    // ---------------------------------------------------------- GetSeriesResolution

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task GetSeriesResolution_MissingPath_ReturnsBadRequest(string? filePath)
    {
        var result = await CreateController(resolver: _resolver.Object).GetSeriesResolution(filePath!, CancellationToken.None);

        AssertStatus(result.Result, 400, "File path is required");
        _resolver.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetSeriesResolution_PathOutsideWatchedDirectory_ReturnsBadRequest()
    {
        var result = await CreateController(resolver: _resolver.Object).GetSeriesResolution(OutsidePath, CancellationToken.None);

        AssertStatus(result.Result, 400, "File path is outside the allowed directory");
        _resolver.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetSeriesResolution_NoResolverRegistered_Returns503()
    {
        var result = await CreateController().GetSeriesResolution(InRoot("Saga 001.cbz"), CancellationToken.None);

        AssertStatus(result.Result, 503, "Series name resolver not available");
        _processor.Verify(p => p.GetMetadataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetSeriesResolution_ResolvedSeriesMatchesExistingTag_ReturnsFullPreviewWithoutMutatingCache()
    {
        var path = InRoot("Saga", "Saga 001.cbz");
        var metadata = new ComicMetadata { Series = "Saga" };
        using var cts = new CancellationTokenSource();
        _processor.Setup(p => p.GetMetadataAsync(path, cts.Token)).ReturnsAsync(metadata);
        _resolver
            .Setup(r => r.ResolveAsync(path, metadata, false, cts.Token))
            .ReturnsAsync(new SeriesNameResolution
            {
                ResolvedSeries = "SAGA",
                WinningStep = SeriesNameResolutionStep.MatchedCache,
                Candidates = new[] { "Saga", "SAGA" },
                FolderSeries = "Saga",
                MatchedCacheKey = "saga",
                AppliedLanguage = "en",
                Explanation = "Matched the alias cache"
            });

        var result = await CreateController(resolver: _resolver.Object).GetSeriesResolution(path, cts.Token);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(path, Prop(ok.Value, "filePath"));
        Assert.Equal("Saga", Prop(ok.Value, "actualSeries"));
        Assert.Equal("SAGA", Prop(ok.Value, "resolvedSeries"));
        Assert.Equal("MatchedCache", Prop(ok.Value, "winningStep"));
        Assert.Equal(new[] { "Saga", "SAGA" }, Assert.IsAssignableFrom<IReadOnlyList<string>>(Prop(ok.Value, "candidates")));
        Assert.Equal("Saga", Prop(ok.Value, "folderSeries"));
        Assert.Equal("saga", Prop(ok.Value, "matchedCacheKey"));
        Assert.Equal("en", Prop(ok.Value, "appliedLanguage"));
        Assert.Equal("Matched the alias cache", Prop(ok.Value, "explanation"));
        Assert.Equal(true, Prop(ok.Value, "matchesActual"));
        _resolver.Verify(r => r.ResolveAsync(path, metadata, false, cts.Token), Times.Once);
    }

    [Fact]
    public async Task GetSeriesResolution_FileHasNoMetadata_ReportsNoActualSeries()
    {
        var path = InRoot("Saga 001.cbz");
        _processor.Setup(p => p.GetMetadataAsync(path, It.IsAny<CancellationToken>())).ReturnsAsync((ComicMetadata?)null);
        _resolver
            .Setup(r => r.ResolveAsync(path, null, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesNameResolution { ResolvedSeries = "Saga", WinningStep = SeriesNameResolutionStep.FolderName });

        var result = await CreateController(resolver: _resolver.Object).GetSeriesResolution(path, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Null(Prop(ok.Value, "actualSeries"));
        Assert.Equal("FolderName", Prop(ok.Value, "winningStep"));
        Assert.Equal(false, Prop(ok.Value, "matchesActual"));
    }

    [Theory]
    [InlineData("Saga", "Paper Girls")]
    [InlineData("   ", "   ")]
    [InlineData(null, "Saga")]
    public async Task GetSeriesResolution_ActualSeriesBlankOrDifferent_DoesNotMatch(string? actualSeries, string resolvedSeries)
    {
        var path = InRoot("Saga 001.cbz");
        var metadata = new ComicMetadata { Series = actualSeries };
        _processor.Setup(p => p.GetMetadataAsync(path, It.IsAny<CancellationToken>())).ReturnsAsync(metadata);
        _resolver
            .Setup(r => r.ResolveAsync(path, metadata, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesNameResolution { ResolvedSeries = resolvedSeries });

        var result = await CreateController(resolver: _resolver.Object).GetSeriesResolution(path, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(actualSeries, Prop(ok.Value, "actualSeries"));
        Assert.Equal(false, Prop(ok.Value, "matchesActual"));
    }

    [Fact]
    public async Task GetSeriesResolution_ResolverThrows_Returns500()
    {
        var path = InRoot("Saga 001.cbz");
        _resolver
            .Setup(r => r.ResolveAsync(path, It.IsAny<ComicMetadata?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("resolver offline"));

        var result = await CreateController(resolver: _resolver.Object).GetSeriesResolution(path, CancellationToken.None);

        AssertStatus(result.Result, 500, "Error resolving series");
    }

    // -------------------------------------------------------------- UpdateMetadata

    [Fact]
    public async Task UpdateMetadata_PathOutsideWatchedDirectory_ReturnsBadRequestWithoutWriting()
    {
        var result = await CreateController(_broadcaster.Object).UpdateMetadata(OutsidePath, new ComicMetadata());

        AssertStatus(result, 400, "File path is outside the allowed directory");
        _processor.VerifyNoOtherCalls();
        _broadcaster.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UpdateMetadata_WriteFailsWithoutBroadcaster_ReturnsBadRequestAndRecordsFailure()
    {
        var path = InRoot("Saga 001.cbz");
        _processor.Setup(p => p.UpdateMetadataAsync(path, It.IsAny<ComicMetadata>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await CreateController().UpdateMetadata(path, new ComicMetadata { Series = "Saga" });

        AssertStatus(result, 400, "Failed to update metadata");
        var entry = SingleHistoryEntry();
        Assert.Equal(path, entry.FilePath);
        Assert.Equal("Update Metadata", entry.Action);
        Assert.False(entry.Success);
        Assert.Equal("Failed to update metadata", entry.ErrorMessage);
    }

    [Fact]
    public async Task UpdateMetadata_WriteFailsWithBroadcaster_BroadcastsFailure()
    {
        var path = InRoot("Saga 001.cbz");
        _processor.Setup(p => p.UpdateMetadataAsync(path, It.IsAny<ComicMetadata>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await CreateController(_broadcaster.Object).UpdateMetadata(path, new ComicMetadata());

        AssertStatus(result, 400, "Failed to update metadata");
        _broadcaster.Verify(b => b.BroadcastFileProcessedAsync("Saga 001.cbz", false, "Failed to update metadata"), Times.Once);
    }

    [Fact]
    public async Task UpdateMetadata_WriteFailsAndBroadcastThrows_StillReturnsBadRequest()
    {
        var path = InRoot("Saga 001.cbz");
        _processor.Setup(p => p.UpdateMetadataAsync(path, It.IsAny<ComicMetadata>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _broadcaster
            .Setup(b => b.BroadcastFileProcessedAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>()))
            .ThrowsAsync(new InvalidOperationException("hub down"));

        var result = await CreateController(_broadcaster.Object).UpdateMetadata(path, new ComicMetadata());

        AssertStatus(result, 400, "Failed to update metadata");
        Assert.False(SingleHistoryEntry().Success);
    }

    [Fact]
    public async Task UpdateMetadata_Success_LogsBeforeAndAfterValuesAndBroadcasts()
    {
        var path = InRoot("Saga 001.cbz");
        var before = new ComicMetadata { Title = "Old", Series = "Sag", Issue = "1", Publisher = "Imag", Year = 2011, Volume = "1" };
        var after = new ComicMetadata { Title = "New", Series = "Saga", Issue = "01", Publisher = "Image", Year = 2012, Volume = "2" };
        _processor.Setup(p => p.GetMetadataAsync(path, It.IsAny<CancellationToken>())).ReturnsAsync(before);
        _processor.Setup(p => p.UpdateMetadataAsync(path, after, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await CreateController(_broadcaster.Object).UpdateMetadata(path, after);

        Assert.IsType<OkResult>(result);
        var entry = SingleHistoryEntry();
        Assert.True(entry.Success);
        Assert.Null(entry.ErrorMessage);
        Assert.Equal("Saga 001.cbz", entry.BeforeFilename);
        Assert.Equal("Saga 001.cbz", entry.AfterFilename);
        Assert.Equal(("Old", "New"), (entry.BeforeTitle, entry.AfterTitle));
        Assert.Equal(("Sag", "Saga"), (entry.BeforeSeries, entry.AfterSeries));
        Assert.Equal(("1", "01"), (entry.BeforeIssue, entry.AfterIssue));
        Assert.Equal(("Imag", "Image"), (entry.BeforePublisher, entry.AfterPublisher));
        Assert.Equal(((int?)2011, (int?)2012), (entry.BeforeYear, entry.AfterYear));
        Assert.Equal(("1", "2"), (entry.BeforeVolume, entry.AfterVolume));
        _broadcaster.Verify(b => b.BroadcastFileProcessedAsync("Saga 001.cbz", true, null), Times.Once);
    }

    [Fact]
    public async Task UpdateMetadata_SuccessWithNullBody_LogsBeforeValuesOnly()
    {
        var path = InRoot("Saga 001.cbz");
        var before = new ComicMetadata { Title = "Old", Series = "Saga", Issue = "1", Publisher = "Image", Year = 2011, Volume = "1" };
        _processor.Setup(p => p.GetMetadataAsync(path, It.IsAny<CancellationToken>())).ReturnsAsync(before);
        _processor.Setup(p => p.UpdateMetadataAsync(path, It.IsAny<ComicMetadata>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await CreateController().UpdateMetadata(path, null!);

        Assert.IsType<OkResult>(result);
        var entry = SingleHistoryEntry();
        Assert.Equal(("Old", "Saga", "1", "Image", (int?)2011, "1"),
            (entry.BeforeTitle, entry.BeforeSeries, entry.BeforeIssue, entry.BeforePublisher, entry.BeforeYear, entry.BeforeVolume));
        Assert.Null(entry.AfterTitle);
        Assert.Null(entry.AfterSeries);
        Assert.Null(entry.AfterIssue);
        Assert.Null(entry.AfterPublisher);
        Assert.Null(entry.AfterYear);
        Assert.Null(entry.AfterVolume);
    }

    [Fact]
    public async Task UpdateMetadata_SuccessButBroadcastThrows_StillReturnsOk()
    {
        var path = InRoot("Saga 001.cbz");
        _processor.Setup(p => p.UpdateMetadataAsync(path, It.IsAny<ComicMetadata>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _broadcaster
            .Setup(b => b.BroadcastFileProcessedAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>()))
            .ThrowsAsync(new InvalidOperationException("hub down"));

        var result = await CreateController(_broadcaster.Object).UpdateMetadata(path, new ComicMetadata { Series = "Saga" });

        Assert.IsType<OkResult>(result);
        Assert.True(SingleHistoryEntry().Success);
    }

    [Fact]
    public async Task UpdateMetadata_ProcessorThrows_Returns500AndRecordsException()
    {
        var path = InRoot("Saga 001.cbz");
        _processor.Setup(p => p.UpdateMetadataAsync(path, It.IsAny<ComicMetadata>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("disk full"));

        var result = await CreateController(_broadcaster.Object).UpdateMetadata(path, new ComicMetadata());

        AssertStatus(result, 500, "Error updating metadata");
        var entry = SingleHistoryEntry();
        Assert.False(entry.Success);
        Assert.Equal("disk full", entry.ErrorMessage);
        _broadcaster.VerifyNoOtherCalls();
    }

    // --------------------------------------------------------------- PatchMetadata

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task PatchMetadata_MissingPath_ReturnsBadRequest(string? filePath)
    {
        var result = await CreateController().PatchMetadata(filePath!, new PatchMetadataRequest { Metadata = new ComicMetadata() }, CancellationToken.None);

        AssertStatus(result, 400, "File path is required");
        _fileStore.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PatchMetadata_PathOutsideWatchedDirectory_ReturnsBadRequest()
    {
        var result = await CreateController().PatchMetadata(OutsidePath, new PatchMetadataRequest { Metadata = new ComicMetadata() }, CancellationToken.None);

        AssertStatus(result, 400, "File path is outside the allowed directory");
        _fileStore.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PatchMetadata_NullBody_ReturnsBadRequest()
    {
        var result = await CreateController().PatchMetadata(InRoot("Saga 001.cbz"), null!, CancellationToken.None);

        AssertStatus(result, 400, "Metadata patch is required");
        _fileStore.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PatchMetadata_BodyWithoutMetadata_ReturnsBadRequest()
    {
        var result = await CreateController().PatchMetadata(
            InRoot("Saga 001.cbz"), new PatchMetadataRequest { LockFields = new List<string> { "series" } }, CancellationToken.None);

        AssertStatus(result, 400, "Metadata patch is required");
        _fileStore.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("none", ComicMetadataFieldFlags.None)]
    [InlineData("series", ComicMetadataFieldFlags.Series)]
    [InlineData("title", ComicMetadataFieldFlags.Title)]
    [InlineData("issue", ComicMetadataFieldFlags.Issue)]
    [InlineData("volume", ComicMetadataFieldFlags.Volume)]
    [InlineData("publisher", ComicMetadataFieldFlags.Publisher)]
    [InlineData("year", ComicMetadataFieldFlags.Year)]
    [InlineData("summary", ComicMetadataFieldFlags.Summary)]
    [InlineData("authors", ComicMetadataFieldFlags.Authors)]
    [InlineData("tags", ComicMetadataFieldFlags.Tags)]
    public async Task PatchMetadata_LocksEveryFieldPresentInThePatch(string field, ComicMetadataFieldFlags expected)
    {
        var path = InRoot("Saga 001.cbz");
        var patch = MetadataWithField(field);
        using var cts = new CancellationTokenSource();

        var result = await CreateController().PatchMetadata(path, new PatchMetadataRequest { Metadata = patch }, cts.Token);

        Assert.IsType<AcceptedResult>(result);
        _fileStore.Verify(f => f.ApplyUserMetadataEditAsync(path, patch, expected, cts.Token), Times.Once);
    }

    [Fact]
    public async Task PatchMetadata_AllFieldsPresent_LocksEveryField()
    {
        var path = InRoot("Saga 001.cbz");
        var patch = new ComicMetadata
        {
            Series = "Saga", Title = "One", Issue = "1", Volume = "1", Publisher = "Image", Year = 2012, Summary = "s",
            Authors = new List<string> { "BKV" }, Tags = new List<string> { "space" }
        };
        const ComicMetadataFieldFlags all = ComicMetadataFieldFlags.Series | ComicMetadataFieldFlags.Title
            | ComicMetadataFieldFlags.Issue | ComicMetadataFieldFlags.Volume | ComicMetadataFieldFlags.Publisher
            | ComicMetadataFieldFlags.Year | ComicMetadataFieldFlags.Summary | ComicMetadataFieldFlags.Authors
            | ComicMetadataFieldFlags.Tags;

        await CreateController().PatchMetadata(path, new PatchMetadataRequest { Metadata = patch }, CancellationToken.None);

        _fileStore.Verify(f => f.ApplyUserMetadataEditAsync(path, patch, all, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("series", ComicMetadataFieldFlags.Series)]
    [InlineData("  SERIES  ", ComicMetadataFieldFlags.Series)]
    [InlineData("Title", ComicMetadataFieldFlags.Title)]
    [InlineData("issue", ComicMetadataFieldFlags.Issue)]
    [InlineData("number", ComicMetadataFieldFlags.Issue)]
    [InlineData("volume", ComicMetadataFieldFlags.Volume)]
    [InlineData("publisher", ComicMetadataFieldFlags.Publisher)]
    [InlineData("year", ComicMetadataFieldFlags.Year)]
    [InlineData("summary", ComicMetadataFieldFlags.Summary)]
    [InlineData("Description", ComicMetadataFieldFlags.Summary)]
    [InlineData("authors", ComicMetadataFieldFlags.Authors)]
    [InlineData("writer", ComicMetadataFieldFlags.Authors)]
    [InlineData("writers", ComicMetadataFieldFlags.Authors)]
    [InlineData("tags", ComicMetadataFieldFlags.Tags)]
    [InlineData("colorist", ComicMetadataFieldFlags.None)]
    [InlineData("", ComicMetadataFieldFlags.None)]
    [InlineData("   ", ComicMetadataFieldFlags.None)]
    [InlineData(null, ComicMetadataFieldFlags.None)]
    public async Task PatchMetadata_ExplicitLockFieldNames_AreParsedCaseInsensitively(string? lockField, ComicMetadataFieldFlags expected)
    {
        var path = InRoot("Saga 001.cbz");
        var patch = new ComicMetadata();
        var body = new PatchMetadataRequest { Metadata = patch, LockFields = new List<string> { lockField! } };

        var result = await CreateController().PatchMetadata(path, body, CancellationToken.None);

        Assert.IsType<AcceptedResult>(result);
        _fileStore.Verify(f => f.ApplyUserMetadataEditAsync(path, patch, expected, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PatchMetadata_ExplicitLockFieldsAreCombinedWithEditedFields()
    {
        var path = InRoot("Saga 001.cbz");
        var patch = new ComicMetadata { Series = "Saga" };
        var body = new PatchMetadataRequest { Metadata = patch, LockFields = new List<string> { "tags", "unknown", "year" } };

        await CreateController().PatchMetadata(path, body, CancellationToken.None);

        _fileStore.Verify(f => f.ApplyUserMetadataEditAsync(
            path,
            patch,
            ComicMetadataFieldFlags.Series | ComicMetadataFieldFlags.Tags | ComicMetadataFieldFlags.Year,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PatchMetadata_Success_TriggersMetadataBackfillAndReturnsNewVersion()
    {
        var path = InRoot("Saga 001.cbz");
        var backfillStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Mock<IScheduledJobHandler>();
        handler.SetupGet(h => h.JobKey).Returns(MetadataBackfillJobHandler.Key);
        handler.SetupGet(h => h.DisplayName).Returns("Metadata backfill");
        handler.SetupGet(h => h.Description).Returns("Backfills metadata");
        handler.SetupGet(h => h.Defaults).Returns(new ScheduledJobDefaults(false, 0));
        handler
            .Setup(h => h.ExecuteAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                backfillStarted.TrySetResult(true);
                return Task.FromResult("done");
            });
        var dbFactory = new InMemoryDbFactory(new DbContextOptionsBuilder<ComicMaintainerDbContext>()
            .UseInMemoryDatabase($"fc-n2a-patch-{Guid.NewGuid():N}")
            .Options);
        var jobService = new ScheduledJobService(dbFactory, new[] { handler.Object }, NullLogger<ScheduledJobService>.Instance);
        var hostedService = new ScheduledJobsHostedService(jobService, NullLogger<ScheduledJobsHostedService>.Instance);
        _fileStore.Setup(f => f.GetFileAsync(path, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComicFile { FilePath = path, MetadataVersion = 7 });

        var result = await CreateController(scheduledJobs: hostedService)
            .PatchMetadata(path, new PatchMetadataRequest { Metadata = new ComicMetadata { Title = "One" } }, CancellationToken.None);

        var accepted = Assert.IsType<AcceptedResult>(result);
        Assert.Equal(path, Prop(accepted.Value, "filePath"));
        Assert.Equal(7, Prop(accepted.Value, "metadataVersion"));
        Assert.True(await backfillStarted.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Theory]
    [InlineData("Saga 002.cbz")]
    [InlineData("Saga 001.cbz")]
    public async Task PatchMetadata_WhileABackfillIsRunning_WritesTheEditBackAfterThatRun(string secondName)
    {
        // Regression test for #863: an edit made while a backfill was running never reached
        // the archive. Its trigger was dropped because a run was in progress, and that run
        // had read its batch before the edit. Covers editing another file, and editing again
        // the file the running backfill is writing.
        var first = InRoot("Saga 001.cbz");
        var second = InRoot(secondName);

        // Stands in for the files table: an edit bumps the file's version, and the file stays
        // pending until the backfill marks that version (or a later one) as written.
        var versions = new Dictionary<string, int>();
        var pending = new Dictionary<string, ComicFile>();
        var secondWrittenBack = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fileStore
            .Setup(f => f.ApplyUserMetadataEditAsync(It.IsAny<string>(), It.IsAny<ComicMetadata>(), It.IsAny<ComicMetadataFieldFlags>(), It.IsAny<CancellationToken>()))
            .Callback<string, ComicMetadata, ComicMetadataFieldFlags, CancellationToken>((path, patch, _, _) =>
            {
                lock (pending)
                {
                    versions[path] = versions.GetValueOrDefault(path) + 1;
                    pending[path] = new ComicFile { FilePath = path, MetadataVersion = versions[path], Metadata = patch.Clone() };
                }
            })
            .Returns(Task.CompletedTask);
        _fileStore
            .Setup(f => f.GetFilesNeedingBackfillAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int max, string? after, CancellationToken _) =>
            {
                lock (pending)
                {
                    return pending.Values
                        .Where(f => after is null || string.CompareOrdinal(f.FilePath, after) > 0)
                        .OrderBy(f => f.FilePath, StringComparer.Ordinal)
                        .Take(max)
                        .ToList();
                }
            });
        _fileStore
            .Setup(f => f.MarkFileBackfilledAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<string, int, CancellationToken>((path, version, _) =>
            {
                lock (pending)
                {
                    if (pending.TryGetValue(path, out var file) && file.MetadataVersion <= version)
                    {
                        pending.Remove(path);
                        if (path == second)
                        {
                            secondWrittenBack.TrySetResult();
                        }
                    }
                }
            })
            .Returns(Task.CompletedTask);

        // The backfill started by the first edit is still writing the first archive when the
        // second edit arrives.
        var writingFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;
        _processor
            .Setup(p => p.UpdateMetadataAsync(It.IsAny<string>(), It.IsAny<ComicMetadata>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                if (Interlocked.Increment(ref writes) == 1)
                {
                    writingFirst.TrySetResult();
                    await finishFirst.Task;
                }
                return true;
            });
        _resolver
            .Setup(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<ComicMetadata?>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesNameResolution());
        var backfill = new MetadataBackfillJobHandler(
            _fileStore.Object, _processor.Object, _resolver.Object, NullLogger<MetadataBackfillJobHandler>.Instance);
        var dbFactory = new InMemoryDbFactory(new DbContextOptionsBuilder<ComicMaintainerDbContext>()
            .UseInMemoryDatabase($"fc-n2a-patch-{Guid.NewGuid():N}")
            .Options);
        var jobService = new ScheduledJobService(dbFactory, new IScheduledJobHandler[] { backfill }, NullLogger<ScheduledJobService>.Instance);
        var controller = CreateController(
            scheduledJobs: new ScheduledJobsHostedService(jobService, NullLogger<ScheduledJobsHostedService>.Instance));

        var firstResult = await controller.PatchMetadata(
            first, new PatchMetadataRequest { Metadata = new ComicMetadata { Title = "One" } }, CancellationToken.None);
        await writingFirst.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var secondResult = await controller.PatchMetadata(
            second, new PatchMetadataRequest { Metadata = new ComicMetadata { Title = "Two" } }, CancellationToken.None);
        finishFirst.TrySetResult();

        Assert.IsType<AcceptedResult>(firstResult);
        Assert.IsType<AcceptedResult>(secondResult);
        await secondWrittenBack.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _processor.Verify(p => p.UpdateMetadataAsync(
            second, It.Is<ComicMetadata>(m => m.Title == "Two"), It.IsAny<CancellationToken>()), Times.Once);
        lock (pending)
        {
            Assert.Empty(pending);
        }
    }

    [Fact]
    public async Task PatchMetadata_FileNoLongerTrackedAfterEdit_ReportsVersionZero()
    {
        var path = InRoot("Saga 001.cbz");
        _fileStore.Setup(f => f.GetFileAsync(path, It.IsAny<CancellationToken>())).ReturnsAsync((ComicFile?)null);

        var result = await CreateController()
            .PatchMetadata(path, new PatchMetadataRequest { Metadata = new ComicMetadata { Title = "One" } }, CancellationToken.None);

        var accepted = Assert.IsType<AcceptedResult>(result);
        Assert.Equal(0, Prop(accepted.Value, "metadataVersion"));
    }

    [Fact]
    public async Task PatchMetadata_FileNotTracked_ReturnsNotFound()
    {
        var path = InRoot("Saga 001.cbz");
        _fileStore
            .Setup(f => f.ApplyUserMetadataEditAsync(path, It.IsAny<ComicMetadata>(), It.IsAny<ComicMetadataFieldFlags>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("not tracked"));

        var result = await CreateController()
            .PatchMetadata(path, new PatchMetadataRequest { Metadata = new ComicMetadata { Title = "One" } }, CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        Assert.Equal("File is not tracked", notFound.Value);
    }

    [Fact]
    public async Task PatchMetadata_UnexpectedFailure_Returns500()
    {
        var path = InRoot("Saga 001.cbz");
        _fileStore
            .Setup(f => f.ApplyUserMetadataEditAsync(path, It.IsAny<ComicMetadata>(), It.IsAny<ComicMetadataFieldFlags>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("db locked"));

        var result = await CreateController()
            .PatchMetadata(path, new PatchMetadataRequest { Metadata = new ComicMetadata { Title = "One" } }, CancellationToken.None);

        AssertStatus(result, 500, "Error patching metadata");
    }

    // ------------------------------------------------------------- GetFileTags

    [Theory]
    [InlineData("@@@")]
    [InlineData("")]
    public async Task GetFileTags_UndecodablePath_ReturnsBadRequest(string encoded)
    {
        var result = await CreateController().GetFileTags(encoded);

        AssertStatus(result.Result, 400, "Invalid file path");
        _processor.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(OutsidePath)]
    [InlineData("../../etc/passwd")]
    [InlineData("{root}-private/Saga 001.cbz")]
    public async Task GetFileTags_PathOutsideWatchedDirectory_ReturnsBadRequest(string path)
    {
        var result = await CreateController().GetFileTags(Encode(Expand(path)));

        AssertStatus(result.Result, 400, "File path is outside the allowed directory");
        _processor.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetFileTags_ProcessorThrows_Returns500()
    {
        var path = InRoot("Saga 001.cbz");
        _processor.Setup(p => p.GetMetadataAsync(path, It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("corrupt"));

        var result = await CreateController().GetFileTags(Encode(path));

        AssertStatus(result.Result, 500, "Error getting tags");
    }

    // ---------------------------------------------------------- UpdateFileTags

    [Fact]
    public async Task UpdateFileTags_UndecodablePath_ReturnsBadRequest()
    {
        var result = await CreateController(_broadcaster.Object).UpdateFileTags("@@@", new ComicMetadata());

        AssertStatus(result, 400, "Invalid file path");
        _processor.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(OutsidePath)]
    [InlineData("../../etc/passwd")]
    [InlineData("{root}-private/Saga 001.cbz")]
    public async Task UpdateFileTags_PathOutsideWatchedDirectory_ReturnsBadRequest(string path)
    {
        var result = await CreateController(_broadcaster.Object).UpdateFileTags(Encode(Expand(path)), new ComicMetadata { Series = "Saga" });

        AssertStatus(result, 400, "File path is outside the allowed directory");
        _processor.VerifyNoOtherCalls();
        _broadcaster.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UpdateFileTags_Success_BroadcastsFileProcessed()
    {
        var path = InRoot("Saga", "Saga 001.cbz");
        var metadata = new ComicMetadata { Series = "Saga" };
        _processor.Setup(p => p.UpdateMetadataAsync(path, metadata, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await CreateController(_broadcaster.Object).UpdateFileTags(Encode(path), metadata);

        Assert.IsType<OkResult>(result);
        _broadcaster.Verify(b => b.BroadcastFileProcessedAsync("Saga 001.cbz", true, null), Times.Once);
    }

    [Fact]
    public async Task UpdateFileTags_SuccessButBroadcastThrows_StillReturnsOk()
    {
        var path = InRoot("Saga 001.cbz");
        _processor.Setup(p => p.UpdateMetadataAsync(path, It.IsAny<ComicMetadata>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _broadcaster
            .Setup(b => b.BroadcastFileProcessedAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>()))
            .ThrowsAsync(new InvalidOperationException("hub down"));

        var result = await CreateController(_broadcaster.Object).UpdateFileTags(Encode(path), new ComicMetadata());

        Assert.IsType<OkResult>(result);
    }

    [Fact]
    public async Task UpdateFileTags_WriteFails_DoesNotBroadcast()
    {
        var path = InRoot("Saga 001.cbz");
        _processor.Setup(p => p.UpdateMetadataAsync(path, It.IsAny<ComicMetadata>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await CreateController(_broadcaster.Object).UpdateFileTags(Encode(path), new ComicMetadata());

        AssertStatus(result, 400, "Failed to update tags");
        _broadcaster.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UpdateFileTags_ProcessorThrows_Returns500()
    {
        var path = InRoot("Saga 001.cbz");
        _processor.Setup(p => p.UpdateMetadataAsync(path, It.IsAny<ComicMetadata>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("read-only"));

        var result = await CreateController().UpdateFileTags(Encode(path), new ComicMetadata());

        AssertStatus(result, 500, "Error updating tags");
    }

    // ------------------------------------------------------- RemoveFileMetadata

    [Fact]
    public async Task RemoveFileMetadata_UndecodablePath_ReturnsBadRequest()
    {
        var result = await CreateController().RemoveFileMetadata("@@@");

        AssertStatus(result, 400, "Invalid file path");
        _processor.VerifyNoOtherCalls();
        _history.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RemoveFileMetadata_PathOutsideWatchedDirectory_ReturnsBadRequest()
    {
        var result = await CreateController().RemoveFileMetadata(Encode(OutsidePath));

        AssertStatus(result, 400, "File path is outside the allowed directory");
        _processor.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RemoveFileMetadata_TraversalOutOfWatchedDirectory_ReturnsBadRequest()
    {
        var traversal = _root + "/../../outside.cbz";

        var result = await CreateController().RemoveFileMetadata(Encode(traversal));

        AssertStatus(result, 400, "File path is outside the allowed directory");
        _processor.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RemoveFileMetadata_FileDoesNotExist_ReturnsNotFound()
    {
        var result = await CreateController().RemoveFileMetadata(Encode(InRoot("missing.cbz")));

        Assert.IsType<NotFoundResult>(result);
        _processor.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RemoveFileMetadata_ProcessorReportsFailure_ReturnsBadRequestAndRecordsFailure()
    {
        var path = CreateRealFile("Saga 001.cbz");
        _processor.Setup(p => p.RemoveMetadataAsync(path, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await CreateController(_broadcaster.Object).RemoveFileMetadata(Encode(path));

        AssertStatus(result, 400, "Failed to remove metadata");
        var entry = SingleHistoryEntry();
        Assert.Equal(("RemoveMetadata", false, "RemoveMetadataAsync returned false"), (entry.Action, entry.Success, entry.ErrorMessage));
        _fileStore.VerifyNoOtherCalls();
        _broadcaster.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RemoveFileMetadata_Success_ClearsProcessedStateRecordsHistoryAndBroadcasts()
    {
        var path = CreateRealFile("Saga 001.cbz");
        using var cts = new CancellationTokenSource();
        _processor.Setup(p => p.RemoveMetadataAsync(path, cts.Token)).ReturnsAsync(true);
        _fileStore
            .Setup(f => f.ClearProcessedStatusAsync(It.Is<IEnumerable<string>>(paths => paths.SequenceEqual(new[] { path })), cts.Token))
            .ReturnsAsync(1);

        var result = await CreateController(_broadcaster.Object).RemoveFileMetadata(Encode(path), cts.Token);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(true, Prop(ok.Value, "success"));
        Assert.Equal(1, Prop(ok.Value, "cleared"));
        var entry = SingleHistoryEntry();
        Assert.Equal(("RemoveMetadata", true, (string?)null), (entry.Action, entry.Success, entry.ErrorMessage));
        _broadcaster.Verify(b => b.BroadcastFileProcessedAsync("Saga 001.cbz", true, null), Times.Once);
    }

    [Fact]
    public async Task RemoveFileMetadata_SuccessWithoutBroadcaster_ReturnsOk()
    {
        var path = CreateRealFile("Saga 002.cbz");
        _processor.Setup(p => p.RemoveMetadataAsync(path, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _fileStore.Setup(f => f.ClearProcessedStatusAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var result = await CreateController().RemoveFileMetadata(Encode(path));

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(0, Prop(ok.Value, "cleared"));
        Assert.True(SingleHistoryEntry().Success);
    }

    [Fact]
    public async Task RemoveFileMetadata_SuccessButBroadcastThrows_StillReturnsOk()
    {
        var path = CreateRealFile("Saga 003.cbz");
        _processor.Setup(p => p.RemoveMetadataAsync(path, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _broadcaster
            .Setup(b => b.BroadcastFileProcessedAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>()))
            .ThrowsAsync(new InvalidOperationException("hub down"));

        var result = await CreateController(_broadcaster.Object).RemoveFileMetadata(Encode(path));

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task RemoveFileMetadata_ProcessorThrows_Returns500AndRecordsException()
    {
        var path = CreateRealFile("Saga 004.cbz");
        _processor.Setup(p => p.RemoveMetadataAsync(path, It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("locked"));

        var result = await CreateController().RemoveFileMetadata(Encode(path));

        AssertStatus(result, 500, "Error removing metadata");
        var entry = SingleHistoryEntry();
        Assert.Equal((path, "RemoveMetadata", false, "locked"), (entry.FilePath, entry.Action, entry.Success, entry.ErrorMessage));
    }
}
