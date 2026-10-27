using System.Text.Json;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json.Linq;

namespace ComicMaintainer.Tests.Controllers;

public sealed class MetadataControllerCoverageTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    private readonly Mock<ISeriesLibraryService> _library = new();
    private readonly Mock<ISeriesMetadataCacheService> _cache = new();
    private readonly Mock<ISeriesMetadataRefreshJobService> _refreshJobs = new();
    private readonly Mock<IExternalSeriesMetadataService> _external = new();
    private readonly Mock<IComicProcessorService> _processor = new();
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<ISeriesLanguagePreferenceRetagService> _retag = new();
    private readonly CancellationTokenSource _cts = new();

    public MetadataControllerCoverageTests()
    {
        // Case-, whitespace- and punctuation-insensitive, like the production key normaliser.
        _cache.Setup(c => c.NormalizeKey(It.IsAny<string?>()))
            .Returns<string?>(s => new string((s ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant());
    }

    public void Dispose() => _cts.Dispose();

    private MetadataController CreateController(
        IExternalSeriesMetadataService? external = null,
        bool withProcessor = false,
        bool withFileStore = false,
        bool withRetag = false,
        ISeriesMetadataRefreshJobService? refreshJobs = null,
        IHostApplicationLifetime? lifetime = null) =>
        new(
            _library.Object,
            _cache.Object,
            refreshJobs ?? _refreshJobs.Object,
            external ?? _external.Object,
            NullLogger<MetadataController>.Instance,
            withProcessor ? _processor.Object : null,
            withFileStore ? _fileStore.Object : null,
            withRetag ? _retag.Object : null,
            lifetime)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

    private static JObject Json(object? value) => JObject.FromObject(value!);

    private static void Assert500(object? result, string message)
    {
        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status500InternalServerError, obj.StatusCode);
        Assert.Equal(message, obj.Value);
    }

    private static void AssertBadRequest(object? result, string message) =>
        Assert.Equal(message, Assert.IsType<BadRequestObjectResult>(result).Value);

    private void SetupLibrary(params SeriesLibraryDto[] series) =>
        _library.Setup(l => l.GetSeriesAsync(
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesLibraryResult { Series = series.ToList() });

    private List<string> CaptureStartedTitles(Guid jobId)
    {
        var holder = new List<string>();
        _refreshJobs.Setup(r => r.StartAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<string>, CancellationToken>((titles, _) => holder.AddRange(titles))
            .ReturnsAsync(jobId);
        return holder;
    }

    private void SetupStartThrows() =>
        _refreshJobs.Setup(r => r.StartAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("queue unavailable"));

    // ---- RefreshAll / MatchUnmatched / RefreshSelected ----------------------------------

    [Fact]
    public async Task RefreshAll_LibraryFailure_Returns500WithoutQueueing()
    {
        _library.Setup(l => l.GetSeriesAsync(
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        var result = await CreateController().RefreshAll(CancellationToken.None);

        Assert500(result.Result, "Error queuing metadata refresh");
        _refreshJobs.Verify(r => r.StartAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MatchUnmatched_JobStartFailure_Returns500()
    {
        SetupLibrary(new SeriesLibraryDto { Title = "Batman", CanonicalTitle = "Batman" });
        SetupStartThrows();

        var result = await CreateController().MatchUnmatched(CancellationToken.None);

        Assert500(result.Result, "Error queuing metadata match");
    }

    [Fact]
    public async Task RefreshSelected_NullBody_ReturnsBadRequest()
    {
        var result = await CreateController().RefreshSelected(null!);

        AssertBadRequest(result.Result, "No series specified");
    }

    [Fact]
    public async Task RefreshSelected_NullSeriesList_ReturnsBadRequest()
    {
        var result = await CreateController().RefreshSelected(
            new MetadataController.RefreshSelectedRequest { Series = null! });

        AssertBadRequest(result.Result, "No series specified");
        _refreshJobs.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RefreshSelected_QueuesJobForRequestedSeries()
    {
        var jobId = Guid.NewGuid();
        var series = new List<string> { "Batman", "Saga" };
        _refreshJobs.Setup(r => r.StartAsync(series, It.IsAny<CancellationToken>())).ReturnsAsync(jobId);

        var result = await CreateController().RefreshSelected(
            new MetadataController.RefreshSelectedRequest { Series = series });

        var json = Json(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(jobId, json["jobId"]!.ToObject<Guid>());
        Assert.Equal(2, json["totalSeries"]!.Value<int>());
    }

    [Fact]
    public async Task RefreshSelected_JobStartFailure_Returns500()
    {
        SetupStartThrows();

        var result = await CreateController().RefreshSelected(
            new MetadataController.RefreshSelectedRequest { Series = new() { "Batman" } });

        Assert500(result.Result, "Error queuing metadata refresh");
    }

    // ---- RefreshOne ---------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RefreshOne_BlankTitle_ReturnsBadRequest(string title)
    {
        var result = await CreateController().RefreshOne(title, queue: false, CancellationToken.None);

        AssertBadRequest(result.Result, "Series title is required");
        _cache.Verify(c => c.RefreshAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RefreshOne_SynchronousLookupFailure_Returns500()
    {
        _cache.Setup(c => c.RefreshAsync("Batman", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("provider down"));

        var result = await CreateController().RefreshOne("Batman", queue: false, CancellationToken.None);

        Assert500(result.Result, "Error refreshing metadata");
    }

    [Fact]
    public async Task RefreshOne_QueuedLookupFailure_Returns500()
    {
        SetupStartThrows();

        var result = await CreateController().RefreshOne("Batman", queue: true, CancellationToken.None);

        Assert500(result.Result, "Error refreshing metadata");
        _cache.Verify(c => c.RefreshAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- RefreshFolder ------------------------------------------------------------------

    [Fact]
    public async Task RefreshFolder_NullBody_ReturnsBadRequest()
    {
        var result = await CreateController().RefreshFolder(null!, CancellationToken.None);

        AssertBadRequest(result.Result, "Series id or titles required");
    }

    [Fact]
    public async Task RefreshFolder_BlankIdAndEmptyTitles_ReturnsBadRequest()
    {
        var result = await CreateController().RefreshFolder(
            new MetadataController.RefreshFolderRequest { SeriesId = "  ", Titles = new() }, CancellationToken.None);

        AssertBadRequest(result.Result, "Series id or titles required");
        _library.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RefreshFolder_TitlesOnly_QueuesDedupedNonBlankTitles()
    {
        var jobId = Guid.NewGuid();
        var started = CaptureStartedTitles(jobId);

        var result = await CreateController().RefreshFolder(
            new MetadataController.RefreshFolderRequest { Titles = new() { "Batman", " ", "BATMAN", "Detective Comics" } },
            CancellationToken.None);

        var accepted = Assert.IsType<AcceptedResult>(result.Result);
        var json = Json(accepted.Value);
        Assert.Equal(jobId, json["jobId"]!.ToObject<Guid>());
        Assert.Equal(2, json["totalSeries"]!.Value<int>());
        Assert.Equal(new[] { "Batman", "Detective Comics" }, json["titles"]!.ToObject<string[]>());
        Assert.Equal(new[] { "Batman", "Detective Comics" }, started);
        _library.Verify(l => l.GetTitlesForSeriesIdAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RefreshFolder_SeriesIdAndTitles_MergesWithoutCaseInsensitiveDuplicates()
    {
        _library.Setup(l => l.GetTitlesForSeriesIdAsync("batman", null, _cts.Token))
            .ReturnsAsync(new List<string> { "Batman", "The Batman" });
        var started = CaptureStartedTitles(Guid.NewGuid());

        var result = await CreateController().RefreshFolder(
            new MetadataController.RefreshFolderRequest { SeriesId = "batman", Titles = new() { "the batman", "Batman Beyond" } },
            _cts.Token);

        Assert.IsType<AcceptedResult>(result.Result);
        Assert.Equal(new[] { "Batman", "The Batman", "Batman Beyond" }, started);
    }

    [Fact]
    public async Task RefreshFolder_SeriesIdWithEmptyTitleList_UsesResolvedTitles()
    {
        _library.Setup(l => l.GetTitlesForSeriesIdAsync("saga", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "Saga" });
        var started = CaptureStartedTitles(Guid.NewGuid());

        var result = await CreateController().RefreshFolder(
            new MetadataController.RefreshFolderRequest { SeriesId = "saga", Titles = new() }, CancellationToken.None);

        Assert.Equal(1, Json(Assert.IsType<AcceptedResult>(result.Result).Value)["totalSeries"]!.Value<int>());
        Assert.Equal(new[] { "Saga" }, started);
    }

    [Fact]
    public async Task RefreshFolder_OnlyBlankTitles_ReturnsNotFound()
    {
        var result = await CreateController().RefreshFolder(
            new MetadataController.RefreshFolderRequest { Titles = new() { " ", "\t" } }, CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Equal("Series not found", Json(notFound.Value)["error"]!.ToString());
        _refreshJobs.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RefreshFolder_SeriesIdLookupFailure_Returns500()
    {
        _library.Setup(l => l.GetTitlesForSeriesIdAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        var result = await CreateController().RefreshFolder(
            new MetadataController.RefreshFolderRequest { SeriesId = "batman" }, CancellationToken.None);

        Assert500(result.Result, "Error queuing metadata refresh");
    }

    // ---- Refresh job lifetime -----------------------------------------------------------

    [Fact]
    public async Task RefreshAll_Issue842_RequestTokenCancelledWhileTheJobRuns_JobStillCompletesAndRetags()
    {
        // Regression test for issue #842. Kestrel reuses a request's abort token for later
        // requests on the same keep-alive connection and cancels it when that connection closes.
        // A job queued on that token stopped after its in-flight title, ended Cancelled and
        // skipped re-tagging the files, although nobody had cancelled it.
        SetupLibrary(
            new SeriesLibraryDto { Title = "Alpha", CanonicalTitle = "Alpha" },
            new SeriesLibraryDto { Title = "Beta", CanonicalTitle = "Beta" },
            new SeriesLibraryDto { Title = "Gamma", CanonicalTitle = "Gamma" });
        var alphaStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishAlpha = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _cache.Setup(c => c.RefreshAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>(async (title, _) =>
            {
                if (title == "Alpha")
                {
                    alphaStarted.TrySetResult();
                    await finishAlpha.Task;
                }
                return new SeriesMetadataCacheRecord { CanonicalTitle = title, LookupStatus = "success" };
            });
        var jobEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _cache.Setup(c => c.CleanupStaleSiblingRecordsAsync(It.IsAny<CancellationToken>()))
            .Callback(() => jobEnded.TrySetResult())
            .ReturnsAsync(0);
        _fileStore.Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new ComicFile { FilePath = "/library/Alpha/c001.cbz", Metadata = new ComicMetadata { Series = "Alpha" } },
                new ComicFile { FilePath = "/library/Gamma/c001.cbz", Metadata = new ComicMetadata { Series = "Gamma" } },
            });
        var retagged = new TaskCompletionSource<List<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _processor.Setup(p => p.NormalizeFilesAsync(It.IsAny<IEnumerable<string>>(), true, It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<string>, bool, CancellationToken>((paths, _, _) => retagged.TrySetResult(paths.ToList()))
            .ReturnsAsync(Guid.NewGuid());
        var jobs = new SeriesMetadataRefreshJobService(
            _cache.Object,
            NullLogger<SeriesMetadataRefreshJobService>.Instance,
            fileStore: _fileStore.Object,
            processor: _processor.Object);

        var result = await CreateController(refreshJobs: jobs).RefreshAll(_cts.Token);
        var jobId = Json(Assert.IsType<OkObjectResult>(result.Result).Value)["jobId"]!.ToObject<Guid>();
        await alphaStarted.Task.WaitAsync(WaitTimeout);

        // The browser or a reverse proxy closes the connection the POST came in on.
        _cts.Cancel();
        finishAlpha.SetResult();

        await jobEnded.Task.WaitAsync(WaitTimeout);
        var job = jobs.GetJob(jobId)!;
        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal(3, job.ProcessedSeries);
        Assert.Equal(3, job.Successes);
        Assert.Equal(
            new[] { "/library/Alpha/c001.cbz", "/library/Gamma/c001.cbz" },
            await retagged.Task.WaitAsync(WaitTimeout));
    }

    [Theory]
    [InlineData("refresh-all")]
    [InlineData("match-unmatched")]
    [InlineData("refresh-selected")]
    [InlineData("refresh-one-queued")]
    [InlineData("refresh-folder")]
    public async Task QueuedRefresh_Issue842_JobRunsUntilAppShutdownNotUntilTheRequestEnds(string endpoint)
    {
        SetupLibrary(new SeriesLibraryDto { Title = "Batman", CanonicalTitle = "Batman" });
        _library.Setup(l => l.GetTitlesForSeriesIdAsync("batman", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "Batman" });
        var jobTokens = new List<CancellationToken>();
        _refreshJobs.Setup(r => r.StartAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<string>, CancellationToken>((_, token) => jobTokens.Add(token))
            .ReturnsAsync(Guid.NewGuid());
        using var stopping = new CancellationTokenSource();
        var lifetime = new Mock<IHostApplicationLifetime>();
        lifetime.SetupGet(l => l.ApplicationStopping).Returns(stopping.Token);
        var controller = CreateController(lifetime: lifetime.Object);

        _ = endpoint switch
        {
            "refresh-all" => await controller.RefreshAll(_cts.Token),
            "match-unmatched" => await controller.MatchUnmatched(_cts.Token),
            "refresh-selected" => await controller.RefreshSelected(
                new MetadataController.RefreshSelectedRequest { Series = new() { "Batman" } }),
            "refresh-one-queued" => await controller.RefreshOne("Batman", queue: true, _cts.Token),
            "refresh-folder" => await controller.RefreshFolder(
                new MetadataController.RefreshFolderRequest { SeriesId = "batman" }, _cts.Token),
            _ => throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, null),
        };

        var jobToken = Assert.Single(jobTokens);
        _cts.Cancel();
        Assert.False(jobToken.IsCancellationRequested);
        stopping.Cancel();
        Assert.True(jobToken.IsCancellationRequested);
    }

    [Fact]
    public async Task QueuedRefresh_WithoutApplicationLifetime_JobIsNotCancelledWithTheRequest()
    {
        var jobTokens = new List<CancellationToken>();
        _refreshJobs.Setup(r => r.StartAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<string>, CancellationToken>((_, token) => jobTokens.Add(token))
            .ReturnsAsync(Guid.NewGuid());

        await CreateController().RefreshOne("Batman", queue: true, _cts.Token);
        _cts.Cancel();

        Assert.False(Assert.Single(jobTokens).CanBeCanceled);
    }

    // ---- GetJob -------------------------------------------------------------------------

    [Fact]
    public void GetJob_UnknownJob_ReturnsNotFound()
    {
        var result = CreateController().GetJob(Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public void GetJob_KnownJob_ReturnsJob()
    {
        var jobId = Guid.NewGuid();
        var job = new MetadataRefreshJob();
        _refreshJobs.Setup(r => r.GetJob(jobId)).Returns(job);

        var result = CreateController().GetJob(jobId);

        Assert.Same(job, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Theory]
    [InlineData(JobStatus.Queued, "Queued")]
    [InlineData(JobStatus.Running, "Running")]
    [InlineData(JobStatus.Completed, "Completed")]
    [InlineData(JobStatus.Failed, "Failed")]
    [InlineData(JobStatus.Cancelled, "Cancelled")]
    [InlineData(JobStatus.Interrupted, "Interrupted")]
    public void GetJob_KnownJob_SendsTheStatusByName(JobStatus status, string expected)
    {
        // The UI's job poller lower-cases the status and stops on a terminal one, so it must
        // be the name, not the enum's number that MVC's web JSON defaults write for enums.
        var jobId = Guid.NewGuid();
        _refreshJobs.Setup(r => r.GetJob(jobId)).Returns(new MetadataRefreshJob { JobId = jobId, Status = status, TotalSeries = 3 });

        var value = Assert.IsType<OkObjectResult>(CreateController().GetJob(jobId).Result).Value;

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(expected, json.RootElement.GetProperty("status").GetString());
        Assert.Equal(3, json.RootElement.GetProperty("totalSeries").GetInt32());
        Assert.Equal(jobId, json.RootElement.GetProperty("jobId").GetGuid());
    }

    // ---- GetProviders -------------------------------------------------------------------

    private static IExternalSeriesMetadataService HealthProvider(ProviderHealth health)
    {
        var provider = new Mock<IExternalSeriesMetadataService>();
        provider.Setup(p => p.CheckHealthAsync(It.IsAny<CancellationToken>())).ReturnsAsync(health);
        return provider.Object;
    }

    private async Task<List<ProviderHealth>> GetCompositeProvidersAsync(params ProviderHealth[] health)
    {
        var composite = new CompositeExternalSeriesMetadataService(
            health.Select(HealthProvider).ToList(),
            NullLogger<CompositeExternalSeriesMetadataService>.Instance);

        var result = await CreateController(external: composite).GetProviders(_cts.Token);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        return Assert.IsType<List<ProviderHealth>>(ok.Value!.GetType().GetProperty("providers")!.GetValue(ok.Value));
    }

    [Fact]
    public async Task GetProviders_HealthCheckFailure_Returns500()
    {
        _external.Setup(e => e.CheckHealthAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("dns failure"));

        var result = await CreateController().GetProviders(CancellationToken.None);

        Assert500(result.Result, "Error retrieving provider health");
    }

    [Fact]
    public async Task GetProviders_Composite_CollapsesAniListVariantsIntoOneEntryAtFirstPosition()
    {
        var comicVine = new ProviderHealth { Name = "ComicVine", Enabled = true, Configured = true, Reachable = true };
        var mangaDex = new ProviderHealth { Name = "MangaDex", Enabled = true, Configured = false };

        var providers = await GetCompositeProvidersAsync(
            comicVine,
            new ProviderHealth { Name = "AniListManga", Enabled = false, Configured = false, SuccessCount = 1, FailureCount = 4 },
            mangaDex,
            new ProviderHealth { Name = "AniListManhwa", Enabled = true, Configured = true, SuccessCount = 2, FailureCount = 5 },
            new ProviderHealth { Name = "AniList", Enabled = false, Configured = false, SuccessCount = 3, FailureCount = 6 });

        Assert.Equal(new[] { "ComicVine", "AniList", "MangaDex" }, providers.Select(p => p.Name));
        Assert.Same(comicVine, providers[0]);
        Assert.Same(mangaDex, providers[2]);
        var aniList = providers[1];
        Assert.True(aniList.Enabled);
        Assert.True(aniList.Configured);
        Assert.Equal(6, aniList.SuccessCount);
        Assert.Equal(15, aniList.FailureCount);
    }

    [Fact]
    public async Task GetProviders_Composite_SingleAniListVariantIsRenamed()
    {
        var providers = await GetCompositeProvidersAsync(new ProviderHealth
        {
            Name = "AniListManhwa",
            Enabled = true,
            Configured = true,
            Reachable = false,
            StatusMessage = "HTTP 503",
            LastError = "Service unavailable",
            LastFailureUtc = T0,
            FailureCount = 2
        });

        var aniList = Assert.Single(providers);
        Assert.Equal("AniList", aniList.Name);
        Assert.True(aniList.Enabled);
        Assert.True(aniList.Configured);
        Assert.False(aniList.Reachable);
        Assert.Equal("HTTP 503", aniList.StatusMessage);
        Assert.Equal("Service unavailable", aniList.LastError);
        Assert.Equal(T0, aniList.LastFailureUtc);
        Assert.Null(aniList.LastSuccessUtc);
        Assert.Equal(2, aniList.FailureCount);
    }

    [Theory]
    [InlineData(true, "Reachable", false, "HTTP 503", true, "Reachable")]
    [InlineData(false, "HTTP 503", true, "OK", true, "Reachable")]
    [InlineData(null, "", true, "", true, "Reachable")]
    [InlineData(false, "Timeout", null, "Not checked", false, "Timeout")]
    [InlineData(null, "", false, "HTTP 503", false, "HTTP 503")]
    [InlineData(null, "Not checked", null, "", null, "Not checked")]
    [InlineData(null, "", null, "", null, "")]
    public async Task GetProviders_Composite_CombinesAniListReachability(
        bool? firstReachable, string firstMessage, bool? secondReachable, string secondMessage,
        bool? expectedReachable, string expectedMessage)
    {
        var providers = await GetCompositeProvidersAsync(
            new ProviderHealth { Name = "AniListManga", Reachable = firstReachable, StatusMessage = firstMessage },
            new ProviderHealth { Name = "AniListManhwa", Reachable = secondReachable, StatusMessage = secondMessage });

        var aniList = Assert.Single(providers);
        Assert.Equal(expectedReachable, aniList.Reachable);
        Assert.Equal(expectedMessage, aniList.StatusMessage);
    }

    private static DateTime? At(int? hours) => hours is null ? null : T0.AddHours(hours.Value);

    [Theory]
    // Second variant has no history: the first variant's timestamps and error are kept.
    [InlineData(0, 0, "first", null, null, "second", 0, 0, "first")]
    // First variant has no history: the second variant's timestamps and error are adopted.
    [InlineData(null, null, null, 1, 1, "second", 1, 1, "second")]
    // A newer failure without an error message keeps the previous error text.
    [InlineData(0, 0, "first", 2, 2, null, 2, 2, "first")]
    // Older timestamps never overwrite newer ones.
    [InlineData(5, 5, "first", 1, 1, "second", 5, 5, "first")]
    // A newer failure with an error message replaces the error text.
    [InlineData(0, 0, "first", 3, 3, "second", 3, 3, "second")]
    public async Task GetProviders_Composite_KeepsLatestAniListTimestamps(
        int? firstSuccess, int? firstFailure, string? firstError,
        int? secondSuccess, int? secondFailure, string? secondError,
        int? expectedSuccess, int? expectedFailure, string? expectedError)
    {
        var providers = await GetCompositeProvidersAsync(
            new ProviderHealth { Name = "AniListManga", LastSuccessUtc = At(firstSuccess), LastFailureUtc = At(firstFailure), LastError = firstError },
            new ProviderHealth { Name = "AniListManhwa", LastSuccessUtc = At(secondSuccess), LastFailureUtc = At(secondFailure), LastError = secondError });

        var aniList = Assert.Single(providers);
        Assert.Equal(At(expectedSuccess), aniList.LastSuccessUtc);
        Assert.Equal(At(expectedFailure), aniList.LastFailureUtc);
        Assert.Equal(expectedError, aniList.LastError);
    }

    // ---- Search -------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 1)]
    [InlineData(500, 50)]
    public async Task Search_ClampsLimitAndToleratesMissingCandidateLists(int limit, int expectedLimit)
    {
        _external.Setup(e => e.SearchSeriesAsync("Attack on Titan", expectedLimit, _cts.Token))
            .ReturnsAsync(new List<ExternalSeriesMetadata>
            {
                new()
                {
                    CanonicalTitle = "Attack on Titan",
                    Source = "AniList",
                    Aliases = null!,
                    LocalizedTitles = new()
                    {
                        null!,
                        new LocalizedTitle("  ", "en"),
                        new LocalizedTitle("進撃の巨人", "ja")
                    }
                },
                new() { CanonicalTitle = "Attack on Titan: Junior High", Source = "MangaDex", LocalizedTitles = null! }
            });

        var result = await CreateController().Search("Attack on Titan", limit, _cts.Token);

        var json = Json(Assert.IsType<OkObjectResult>(result.Result).Value);
        var results = Assert.IsType<JArray>(json["results"]);
        Assert.Equal(2, results.Count);
        var exact = results.Single(r => r["canonical_title"]!.ToString() == "Attack on Titan");
        Assert.Empty(exact["aliases"]!);
        var localized = Assert.Single(exact["localized_titles"]!);
        Assert.Equal("進撃の巨人", localized["title"]!.ToString());
        Assert.Equal("ja", localized["language"]!.ToString());
        var spinOff = results.Single(r => r["canonical_title"]!.ToString() == "Attack on Titan: Junior High");
        Assert.Empty(spinOff["localized_titles"]!);
        _external.Verify(e => e.SearchSeriesAsync("Attack on Titan", expectedLimit, _cts.Token), Times.Once);
    }

    [Fact]
    public async Task Search_ProviderFailure_Returns500()
    {
        _external.Setup(e => e.SearchSeriesAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("provider down"));

        var result = await CreateController().Search("Batman", 10, CancellationToken.None);

        Assert500(result.Result, "Error searching external metadata");
    }

    [Fact]
    public async Task Search_CallerCancelled_LetsTheCancellationThrough()
    {
        _cts.Cancel();
        _external.Setup(e => e.SearchSeriesAsync("Batman", 10, _cts.Token))
            .ThrowsAsync(new OperationCanceledException(_cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateController().Search("Batman", 10, _cts.Token));
    }

    [Fact]
    public async Task Search_CancellationTheCallerDidNotAskFor_Returns500()
    {
        _external.Setup(e => e.SearchSeriesAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("timed out"));

        var result = await CreateController().Search("Batman", 10, _cts.Token);

        Assert500(result.Result, "Error searching external metadata");
    }

    // ---- GetSeries ----------------------------------------------------------------------

    [Fact]
    public async Task GetSeries_BlankTitle_ReturnsBadRequest()
    {
        var result = await CreateController().GetSeries(" ", CancellationToken.None);

        AssertBadRequest(result.Result, "Series title is required");
    }

    [Fact]
    public async Task GetSeries_UncachedSeries_ReturnsPlaceholderRecord()
    {
        _cache.Setup(c => c.GetByTitleAsync("Batman: Year One", _cts.Token)).ReturnsAsync((SeriesMetadataCacheRecord?)null);

        var result = await CreateController().GetSeries("Batman: Year One", _cts.Token);

        var record = Assert.IsType<SeriesMetadataCacheRecord>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("batmanyearone", record.NormalizedKey);
        Assert.Equal("Batman: Year One", record.CanonicalTitle);
        Assert.Empty(record.Aliases);
        Assert.False(record.HasImage);
    }

    [Fact]
    public async Task GetSeries_CachedSeries_ReturnsStoredRecord()
    {
        var stored = new SeriesMetadataCacheRecord { NormalizedKey = "batman", CanonicalTitle = "Batman", Aliases = new() { "The Dark Knight" } };
        _cache.Setup(c => c.GetByTitleAsync("The Dark Knight", It.IsAny<CancellationToken>())).ReturnsAsync(stored);

        var result = await CreateController().GetSeries("The Dark Knight", CancellationToken.None);

        Assert.Same(stored, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    // ---- SetAliases / RemoveAlias -------------------------------------------------------

    [Fact]
    public async Task SetAliases_BlankTitle_ReturnsBadRequest()
    {
        var result = await CreateController().SetAliases(
            "", new MetadataController.SetAliasesRequest { Aliases = new() { "x" } }, CancellationToken.None);

        AssertBadRequest(result.Result, "Series title is required");
    }

    public static TheoryData<MetadataController.SetAliasesRequest?> RequestsWithoutAliases => new()
    {
        null,
        new MetadataController.SetAliasesRequest { Aliases = null! }
    };

    [Theory]
    [MemberData(nameof(RequestsWithoutAliases))]
    public async Task SetAliases_MissingAliasList_ClearsUserAliases(MetadataController.SetAliasesRequest? request)
    {
        var updated = new SeriesMetadataCacheRecord { NormalizedKey = "batman" };
        _cache.Setup(c => c.SetUserAliasesAsync("Batman", It.Is<IEnumerable<string>>(a => !a.Any()), _cts.Token))
            .ReturnsAsync(updated);

        var result = await CreateController().SetAliases("Batman", request!, _cts.Token);

        Assert.Same(updated, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task SetAliases_CacheFailure_Returns500()
    {
        _cache.Setup(c => c.SetUserAliasesAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("db locked"));

        var result = await CreateController().SetAliases(
            "Batman", new MetadataController.SetAliasesRequest { Aliases = new() { "Bats" } }, CancellationToken.None);

        Assert500(result.Result, "Error updating aliases");
    }

    [Theory]
    [InlineData("", "Bats")]
    [InlineData("  ", "Bats")]
    [InlineData("Batman", "")]
    [InlineData("Batman", "  ")]
    public async Task RemoveAlias_BlankTitleOrAlias_ReturnsBadRequest(string title, string alias)
    {
        var result = await CreateController().RemoveAlias(title, alias, CancellationToken.None);

        AssertBadRequest(result.Result, "Series title and alias are required");
        _cache.Verify(c => c.GetByTitleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RemoveAlias_UnknownSeries_ReturnsNotFound()
    {
        var result = await CreateController().RemoveAlias("Batman", "Bats", CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
        _cache.Verify(c => c.RemoveUserAliasAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RemoveAlias_RecordVanishesBeforeRemoval_ReturnsNotFound()
    {
        _cache.Setup(c => c.GetByTitleAsync("Batman: Year One", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesMetadataCacheRecord { NormalizedKey = "batman" });
        _cache.Setup(c => c.RemoveUserAliasAsync("batman", "Bats", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SeriesMetadataCacheRecord?)null);

        var result = await CreateController().RemoveAlias("Batman: Year One", "Bats", CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
        _cache.Verify(c => c.RemoveUserAliasAsync("batman", "Bats", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- ApplyMatch ---------------------------------------------------------------------

    private ExternalSeriesMetadata? _appliedMatch;

    private SeriesMetadataCacheRecord SetupApplyMatch(string seriesTitle, SeriesMetadataCacheRecord? record = null)
    {
        record ??= new SeriesMetadataCacheRecord { NormalizedKey = "batman", CanonicalTitle = "Batman: Year One" };
        _cache.Setup(c => c.ApplyExternalMatchAsync(seriesTitle, It.IsAny<ExternalSeriesMetadata>(), It.IsAny<CancellationToken>()))
            .Callback<string, ExternalSeriesMetadata, CancellationToken>((_, match, _) => _appliedMatch = match)
            .ReturnsAsync(record);
        return record;
    }

    private List<string> CaptureNormalizedPaths()
    {
        var paths = new List<string>();
        _processor.Setup(p => p.NormalizeFilesAsync(It.IsAny<IEnumerable<string>>(), true, It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<string>, bool, CancellationToken>((files, _, _) => paths.AddRange(files))
            .ReturnsAsync(Guid.NewGuid());
        return paths;
    }

    [Fact]
    public async Task ApplyMatch_TrimsAndFiltersCandidateBeforePersisting()
    {
        var record = SetupApplyMatch("Shingeki");

        var result = await CreateController().ApplyMatch(
            "Shingeki",
            new MetadataController.ApplyMatchRequest
            {
                CanonicalTitle = "  Attack on Titan  ",
                Aliases = new() { " Shingeki no Kyojin ", "", "   ", "AoT" },
                Source = "AniList",
                ImageUrl = "https://img.example/aot.jpg",
                ThumbnailUrl = "   ",
                LocalizedTitles = new()
                {
                    null!,
                    new() { Title = "  ", Language = "en" },
                    new() { Title = " 進撃の巨人 ", Language = " ja " },
                    new() { Title = "Attack on Titan", Language = "  " },
                    new() { Title = "L'Attaque des Titans", Language = null }
                }
            },
            CancellationToken.None);

        Assert.Same(record, Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.NotNull(_appliedMatch);
        Assert.Equal("Attack on Titan", _appliedMatch!.CanonicalTitle);
        Assert.Equal(new[] { "Shingeki no Kyojin", "AoT" }, _appliedMatch.Aliases);
        Assert.Equal("AniList", _appliedMatch.Source);
        Assert.Equal("https://img.example/aot.jpg", _appliedMatch.ImageUrl);
        Assert.Null(_appliedMatch.ThumbnailUrl);
        Assert.Equal(
            new[] { ("進撃の巨人", (string?)"ja"), ("Attack on Titan", null), ("L'Attaque des Titans", null) },
            _appliedMatch.LocalizedTitles.Select(t => (t.Title, t.Language)));
    }

    [Fact]
    public async Task ApplyMatch_OptionalCandidateFieldsMissing_UsesEmptyDefaults()
    {
        SetupApplyMatch("Saga");

        var result = await CreateController().ApplyMatch(
            "Saga",
            new MetadataController.ApplyMatchRequest
            {
                CanonicalTitle = "Saga",
                Aliases = null,
                Source = null,
                ImageUrl = null,
                ThumbnailUrl = "https://img.example/saga_t.jpg",
                LocalizedTitles = null
            },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Empty(_appliedMatch!.Aliases);
        Assert.Equal(string.Empty, _appliedMatch.Source);
        Assert.Null(_appliedMatch.ImageUrl);
        Assert.Equal("https://img.example/saga_t.jpg", _appliedMatch.ThumbnailUrl);
        Assert.Empty(_appliedMatch.LocalizedTitles);
    }

    [Fact]
    public async Task ApplyMatch_InvalidCandidate_ReturnsBadRequestWithMessage()
    {
        _cache.Setup(c => c.ApplyExternalMatchAsync(It.IsAny<string>(), It.IsAny<ExternalSeriesMetadata>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("Canonical title is too long"));

        var result = await CreateController().ApplyMatch(
            "Batman", new MetadataController.ApplyMatchRequest { CanonicalTitle = "Batman" }, CancellationToken.None);

        AssertBadRequest(result.Result, "Canonical title is too long");
    }

    [Fact]
    public async Task ApplyMatch_CacheFailure_Returns500()
    {
        _cache.Setup(c => c.ApplyExternalMatchAsync(It.IsAny<string>(), It.IsAny<ExternalSeriesMetadata>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("db locked"));

        var result = await CreateController(withProcessor: true, withFileStore: true).ApplyMatch(
            "Batman", new MetadataController.ApplyMatchRequest { CanonicalTitle = "Batman" }, CancellationToken.None);

        Assert500(result.Result, "Error applying match");
        _fileStore.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ApplyMatch_ProcessorWithoutFileStore_SkipsNormalizeJob()
    {
        var record = SetupApplyMatch("Batman");

        var result = await CreateController(withProcessor: true).ApplyMatch(
            "Batman", new MetadataController.ApplyMatchRequest { CanonicalTitle = "Batman: Year One" }, CancellationToken.None);

        Assert.Same(record, Assert.IsType<OkObjectResult>(result.Result).Value);
        _processor.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ApplyMatch_NormalizeQueueFailure_StillReturnsRecord()
    {
        var record = SetupApplyMatch("Batman");
        _fileStore.Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("file store offline"));

        var result = await CreateController(withProcessor: true, withFileStore: true).ApplyMatch(
            "Batman", new MetadataController.ApplyMatchRequest { CanonicalTitle = "Batman: Year One" }, CancellationToken.None);

        Assert.Same(record, Assert.IsType<OkObjectResult>(result.Result).Value);
        _processor.Verify(p => p.NormalizeFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ApplyMatch_QueuesNormalizeForFilesMatchedByAnyKnownNameOrFolder()
    {
        SetupApplyMatch("Batman", new SeriesMetadataCacheRecord
        {
            NormalizedKey = "batman",
            CanonicalTitle = "Batman: Year One",
            Aliases = new() { "Year One", " ", "!!!" },
            UserAliases = new() { "Bats" }
        });
        _fileStore.Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ComicFile>
            {
                new() { FilePath = "" },
                new() { FilePath = "/library/Other/Bats 001.cbz", Metadata = new ComicMetadata { Series = "BATS" } },
                new() { FilePath = "/library/Misc/Year One 01.cbz", Metadata = new ComicMetadata { Series = "Year-One" } },
                new() { FilePath = "/library/Batman_ Year One/Batman 001.cbz" },
                new() { FilePath = "/library/Batman/Batman 002.cbz", Metadata = new ComicMetadata { Series = "???" } },
                new() { FilePath = "/library/Superman/Superman 001.cbz", Metadata = new ComicMetadata { Series = "Superman" } },
                new() { FilePath = "/Batman 003.cbz" },
                new() { FilePath = "/library/___/Batman 004.cbz" }
            });
        var normalized = CaptureNormalizedPaths();

        var result = await CreateController(withProcessor: true, withFileStore: true).ApplyMatch(
            "Batman", new MetadataController.ApplyMatchRequest { CanonicalTitle = "Batman: Year One" }, _cts.Token);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(
            new[]
            {
                "/library/Other/Bats 001.cbz",
                "/library/Misc/Year One 01.cbz",
                "/library/Batman_ Year One/Batman 001.cbz",
                "/library/Batman/Batman 002.cbz"
            },
            normalized);
        _processor.Verify(p => p.NormalizeFilesAsync(It.IsAny<IEnumerable<string>>(), true, _cts.Token), Times.Once);
    }

    [Fact]
    public async Task ApplyMatch_RecordWithoutAliasLists_MatchesOnRequestedTitle()
    {
        SetupApplyMatch("Batman", new SeriesMetadataCacheRecord
        {
            NormalizedKey = "batman",
            CanonicalTitle = " ",
            Aliases = null!,
            UserAliases = null!
        });
        _fileStore.Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ComicFile>
            {
                new() { FilePath = "/library/Batman/Batman 001.cbz" },
                new() { FilePath = "/library/Saga/Saga 001.cbz" }
            });
        var normalized = CaptureNormalizedPaths();

        var result = await CreateController(withProcessor: true, withFileStore: true).ApplyMatch(
            "Batman", new MetadataController.ApplyMatchRequest { CanonicalTitle = "Batman" }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(new[] { "/library/Batman/Batman 001.cbz" }, normalized);
    }

    [Fact]
    public async Task ApplyMatch_NoFilesBelongToSeries_DoesNotQueueNormalize()
    {
        SetupApplyMatch("Batman");
        _fileStore.Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ComicFile>
            {
                new() { FilePath = "/library/Saga/Saga 001.cbz", Metadata = new ComicMetadata { Series = "Saga" } }
            });

        var result = await CreateController(withProcessor: true, withFileStore: true).ApplyMatch(
            "Batman", new MetadataController.ApplyMatchRequest { CanonicalTitle = "Batman: Year One" }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        _processor.Verify(p => p.NormalizeFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- ClearExternal ------------------------------------------------------------------

    [Fact]
    public async Task ClearExternal_BlankTitle_ReturnsBadRequest()
    {
        var result = await CreateController().ClearExternal(" ", CancellationToken.None);

        AssertBadRequest(result.Result, "Series title is required");
    }

    [Fact]
    public async Task ClearExternal_RecordVanishesBeforeClear_ReturnsNotFound()
    {
        _cache.Setup(c => c.GetByTitleAsync("Batman", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesMetadataCacheRecord { NormalizedKey = "batman" });
        _cache.Setup(c => c.ClearExternalMetadataAsync("batman", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SeriesMetadataCacheRecord?)null);

        var result = await CreateController().ClearExternal("Batman", CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task ClearExternal_CacheFailure_Returns500()
    {
        _cache.Setup(c => c.GetByTitleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("db locked"));

        var result = await CreateController().ClearExternal("Batman", CancellationToken.None);

        Assert500(result.Result, "Error clearing external metadata");
    }

    // ---- SetPreferredLanguage -----------------------------------------------------------

    [Fact]
    public async Task SetPreferredLanguage_BlankTitle_ReturnsBadRequest()
    {
        var result = await CreateController(withRetag: true).SetPreferredLanguage(
            "", new MetadataController.PreferredLanguageRequest { Language = "en" }, CancellationToken.None);

        AssertBadRequest(result.Result, "Series title is required");
        _retag.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SetPreferredLanguage_NullBodyWithoutRetagService_ClearsPreference()
    {
        var record = new SeriesMetadataCacheRecord { NormalizedKey = "saga" };
        _cache.Setup(c => c.SetPreferredLanguageAsync("Saga", null, _cts.Token)).ReturnsAsync(record);

        var result = await CreateController().SetPreferredLanguage("Saga", null, _cts.Token);

        Assert.Same(record, Assert.IsType<OkObjectResult>(result.Result).Value);
        _cache.Verify(c => c.SetPreferredLanguageAsync("Saga", null, _cts.Token), Times.Once);
    }

    [Fact]
    public async Task SetPreferredLanguage_NothingToRetag_ReturnsRecord()
    {
        var record = new SeriesMetadataCacheRecord { NormalizedKey = "saga" };
        _cache.Setup(c => c.SetPreferredLanguageAsync("Saga", "en", It.IsAny<CancellationToken>())).ReturnsAsync(record);
        _retag.Setup(r => r.QueueRetagForSeriesAsync(record, It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var result = await CreateController(withRetag: true).SetPreferredLanguage(
            "Saga", new MetadataController.PreferredLanguageRequest { Language = "en" }, CancellationToken.None);

        Assert.Same(record, Assert.IsType<OkObjectResult>(result.Result).Value);
        _retag.Verify(r => r.QueueRetagForSeriesAsync(record, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SetPreferredLanguage_RetagFailure_DoesNotFailRequest()
    {
        var record = new SeriesMetadataCacheRecord { NormalizedKey = "saga" };
        _cache.Setup(c => c.SetPreferredLanguageAsync("Saga", "ja", It.IsAny<CancellationToken>())).ReturnsAsync(record);
        _retag.Setup(r => r.QueueRetagForSeriesAsync(record, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("file store offline"));

        var result = await CreateController(withRetag: true).SetPreferredLanguage(
            "Saga", new MetadataController.PreferredLanguageRequest { Language = "ja" }, CancellationToken.None);

        Assert.Same(record, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task SetPreferredLanguage_UnsupportedLanguage_ReturnsBadRequestWithMessage()
    {
        _cache.Setup(c => c.SetPreferredLanguageAsync("Saga", "xx", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("Unsupported language 'xx'"));

        var result = await CreateController(withRetag: true).SetPreferredLanguage(
            "Saga", new MetadataController.PreferredLanguageRequest { Language = "xx" }, CancellationToken.None);

        AssertBadRequest(result.Result, "Unsupported language 'xx'");
        _retag.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SetPreferredLanguage_CacheFailure_Returns500()
    {
        _cache.Setup(c => c.SetPreferredLanguageAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("db locked"));

        var result = await CreateController().SetPreferredLanguage(
            "Saga", new MetadataController.PreferredLanguageRequest { Language = "en" }, CancellationToken.None);

        Assert500(result.Result, "Error setting preferred language");
    }

    // ---- SetSeriesName ------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task SetSeriesName_BlankTitle_ReturnsBadRequest(string title)
    {
        var result = await CreateController(withRetag: true).SetSeriesName(
            title, new MetadataController.SeriesNameRequest { Name = "Batman" }, CancellationToken.None);

        AssertBadRequest(result.Result, "Series title is required");
        _cache.Verify(c => c.SetSeriesNameAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SetSeriesName_RevertWithoutCachedRecord_ReturnsPlaceholderWithoutRetag()
    {
        _cache.Setup(c => c.SetSeriesNameAsync("One-Punch Man", null, _cts.Token))
            .ReturnsAsync((SeriesMetadataCacheRecord?)null);

        var result = await CreateController(withRetag: true).SetSeriesName("One-Punch Man", null, _cts.Token);

        var record = Assert.IsType<SeriesMetadataCacheRecord>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("onepunchman", record.NormalizedKey);
        Assert.Equal("One-Punch Man", record.CanonicalTitle);
        _retag.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(4)]
    [InlineData(0)]
    public async Task SetSeriesName_PinnedName_FlagsSeriesFilesForRetag(int flaggedFiles)
    {
        var record = new SeriesMetadataCacheRecord { NormalizedKey = "onepunchman", SeriesName = "One Punch-Man" };
        _cache.Setup(c => c.SetSeriesNameAsync("One-Punch Man", "One Punch-Man", _cts.Token)).ReturnsAsync(record);
        _retag.Setup(r => r.QueueRetagForSeriesAsync(record, _cts.Token)).ReturnsAsync(flaggedFiles);

        var result = await CreateController(withRetag: true).SetSeriesName(
            "One-Punch Man", new MetadataController.SeriesNameRequest { Name = "One Punch-Man" }, _cts.Token);

        Assert.Same(record, Assert.IsType<OkObjectResult>(result.Result).Value);
        _retag.Verify(r => r.QueueRetagForSeriesAsync(record, _cts.Token), Times.Once);
    }

    [Fact]
    public async Task SetSeriesName_RetagFailure_DoesNotFailRequest()
    {
        var record = new SeriesMetadataCacheRecord { NormalizedKey = "saga" };
        _cache.Setup(c => c.SetSeriesNameAsync("Saga", "Saga (2012)", It.IsAny<CancellationToken>())).ReturnsAsync(record);
        _retag.Setup(r => r.QueueRetagForSeriesAsync(It.IsAny<SeriesMetadataCacheRecord>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("file store offline"));

        var result = await CreateController(withRetag: true).SetSeriesName(
            "Saga", new MetadataController.SeriesNameRequest { Name = "Saga (2012)" }, CancellationToken.None);

        Assert.Same(record, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task SetSeriesName_WithoutRetagService_ReturnsRecord()
    {
        var record = new SeriesMetadataCacheRecord { NormalizedKey = "saga" };
        _cache.Setup(c => c.SetSeriesNameAsync("Saga", "Saga (2012)", It.IsAny<CancellationToken>())).ReturnsAsync(record);

        var result = await CreateController().SetSeriesName(
            "Saga", new MetadataController.SeriesNameRequest { Name = "Saga (2012)" }, CancellationToken.None);

        Assert.Same(record, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task SetSeriesName_InvalidName_ReturnsBadRequestWithMessage()
    {
        _cache.Setup(c => c.SetSeriesNameAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("Series name is too long"));

        var result = await CreateController(withRetag: true).SetSeriesName(
            "Saga", new MetadataController.SeriesNameRequest { Name = new string('x', 600) }, CancellationToken.None);

        AssertBadRequest(result.Result, "Series name is too long");
        _retag.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SetSeriesName_CacheFailure_Returns500()
    {
        _cache.Setup(c => c.SetSeriesNameAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("db locked"));

        var result = await CreateController().SetSeriesName(
            "Saga", new MetadataController.SeriesNameRequest { Name = "Saga" }, CancellationToken.None);

        Assert500(result.Result, "Error setting series name");
    }
}
