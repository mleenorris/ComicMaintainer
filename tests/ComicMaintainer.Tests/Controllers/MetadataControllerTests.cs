using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

public class MetadataControllerTests
{
    private readonly Mock<ISeriesLibraryService> _library = new();
    private readonly Mock<ISeriesMetadataCacheService> _cache = new();
    private readonly Mock<ISeriesMetadataRefreshJobService> _refreshJobs = new();
    private readonly Mock<IExternalSeriesMetadataService> _external = new();
    private readonly MetadataController _controller;

    public MetadataControllerTests()
    {
        _cache.Setup(c => c.NormalizeKey(It.IsAny<string>())).Returns<string>(s => (s ?? string.Empty).ToLowerInvariant());
        _controller = new MetadataController(
            _library.Object,
            _cache.Object,
            _refreshJobs.Object,
            _external.Object,
            new Mock<ILogger<MetadataController>>().Object);
    }

    [Fact]
    public async Task RefreshAll_QueuesJobForEveryLibrarySeries()
    {
        _library.Setup(l => l.GetSeriesAsync(null, null, 1, -1, "name", "asc", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesLibraryResult
            {
                Series = new List<SeriesLibraryDto>
                {
                    new() { CanonicalTitle = "Batman" },
                    new() { CanonicalTitle = "Superman" }
                }
            });

        var jobId = Guid.NewGuid();
        _refreshJobs.Setup(j => j.StartAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(jobId);

        var result = await _controller.RefreshAll(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var idProp = ok.Value!.GetType().GetProperty("jobId");
        var totalProp = ok.Value.GetType().GetProperty("totalSeries");
        Assert.Equal(jobId, idProp!.GetValue(ok.Value));
        Assert.Equal(2, totalProp!.GetValue(ok.Value));
    }

    [Fact]
    public async Task MatchUnmatched_QueuesJobForOnlyUnmatchedSeries()
    {
        _library.Setup(l => l.GetSeriesAsync("unmatched", null, 1, -1, "name", "asc", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesLibraryResult
            {
                Series = new List<SeriesLibraryDto>
                {
                    new() { CanonicalTitle = "Unknown One" },
                    new() { CanonicalTitle = "Unknown Two" }
                }
            });

        var jobId = Guid.NewGuid();
        IEnumerable<string>? capturedTitles = null;
        _refreshJobs.Setup(j => j.StartAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<string>, CancellationToken>((titles, _) => capturedTitles = titles.ToList())
            .ReturnsAsync(jobId);

        var result = await _controller.MatchUnmatched(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var idProp = ok.Value!.GetType().GetProperty("jobId");
        var totalProp = ok.Value.GetType().GetProperty("totalSeries");
        Assert.Equal(jobId, idProp!.GetValue(ok.Value));
        Assert.Equal(2, totalProp!.GetValue(ok.Value));
        Assert.NotNull(capturedTitles);
        Assert.Equal(new[] { "Unknown One", "Unknown Two" }, capturedTitles!.ToArray());
        // Only the unmatched-filtered query should have been issued.
        _library.Verify(l => l.GetSeriesAsync("unmatched", null, 1, -1, "name", "asc", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MatchUnmatched_ReturnsZeroWhenNoUnmatchedSeries()
    {
        _library.Setup(l => l.GetSeriesAsync("unmatched", null, 1, -1, "name", "asc", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesLibraryResult { Series = new List<SeriesLibraryDto>() });

        var result = await _controller.MatchUnmatched(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var idProp = ok.Value!.GetType().GetProperty("jobId");
        var totalProp = ok.Value.GetType().GetProperty("totalSeries");
        Assert.Null(idProp!.GetValue(ok.Value));
        Assert.Equal(0, totalProp!.GetValue(ok.Value));
        _refreshJobs.Verify(j => j.StartAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RefreshSelected_RejectsEmptyBody()
    {
        var result = await _controller.RefreshSelected(new MetadataController.RefreshSelectedRequest(), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task RefreshOne_ReturnsCacheRecord()
    {
        var record = new SeriesMetadataCacheRecord { NormalizedKey = "batman", CanonicalTitle = "Batman", LookupStatus = "success" };
        _cache.Setup(c => c.RefreshAsync("Batman", It.IsAny<CancellationToken>())).ReturnsAsync(record);

        var result = await _controller.RefreshOne("Batman", queue: false, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Same(record, ok.Value);
    }

    [Fact]
    public async Task Search_ReturnsCandidatesFromProvider()
    {
        _external.Setup(e => e.SearchSeriesAsync("Bat", 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new ExternalSeriesMetadata { CanonicalTitle = "Batman", Aliases = new List<string> { "Dark Knight" }, Source = "ComicVine" },
                new ExternalSeriesMetadata { CanonicalTitle = "Batgirl", Source = "ComicVine" }
            });

        var result = await _controller.Search("Bat");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var resultsProp = ok.Value!.GetType().GetProperty("results")!.GetValue(ok.Value);
        var enumerable = Assert.IsAssignableFrom<System.Collections.IEnumerable>(resultsProp!);
        var list = enumerable.Cast<object>().ToList();
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public async Task Search_RejectsEmptyQuery()
    {
        var result = await _controller.Search("");
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task SetAliases_PersistsViaCacheService()
    {
        var record = new SeriesMetadataCacheRecord { NormalizedKey = "batman", CanonicalTitle = "Batman", UserAliases = new List<string> { "Dark Knight" } };
        _cache.Setup(c => c.SetUserAliasesAsync("Batman", It.IsAny<IEnumerable<string>>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);

        var result = await _controller.SetAliases(
            "Batman",
            new MetadataController.SetAliasesRequest { Aliases = new List<string> { "Dark Knight" } },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Same(record, ok.Value);
    }

    [Fact]
    public async Task RemoveAlias_DelegatesToCacheService()
    {
        var record = new SeriesMetadataCacheRecord { NormalizedKey = "batman", CanonicalTitle = "Batman" };
        _cache.Setup(c => c.RemoveUserAliasAsync("batman", "Dark Knight", It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);

        var result = await _controller.RemoveAlias("Batman", "Dark Knight", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Same(record, ok.Value);
    }

    [Fact]
    public async Task RefreshOne_QueueMode_StartsBackgroundJob()
    {
        var jobId = Guid.NewGuid();
        _refreshJobs.Setup(j => j.StartAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(jobId);

        var result = await _controller.RefreshOne("Batman", queue: true, CancellationToken.None);

        var accepted = Assert.IsType<AcceptedResult>(result.Result);
        var idProp = accepted.Value!.GetType().GetProperty("jobId");
        Assert.Equal(jobId, idProp!.GetValue(accepted.Value));
        _cache.Verify(c => c.RefreshAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RefreshFolder_ResolvesSeriesIdToTitlesAndQueuesJob()
    {
        _library.Setup(l => l.GetTitlesForSeriesIdAsync("batman", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "Batman", "Dark Knight" });
        var jobId = Guid.NewGuid();
        _refreshJobs.Setup(j => j.StartAsync(It.Is<IEnumerable<string>>(t => t.Contains("Batman") && t.Contains("Dark Knight")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(jobId);

        var result = await _controller.RefreshFolder(
            new MetadataController.RefreshFolderRequest { SeriesId = "batman" },
            CancellationToken.None);

        var accepted = Assert.IsType<AcceptedResult>(result.Result);
        var totalProp = accepted.Value!.GetType().GetProperty("totalSeries");
        Assert.Equal(2, totalProp!.GetValue(accepted.Value));
    }

    [Fact]
    public async Task RefreshFolder_ReturnsNotFound_WhenNothingResolves()
    {
        _library.Setup(l => l.GetTitlesForSeriesIdAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        var result = await _controller.RefreshFolder(
            new MetadataController.RefreshFolderRequest { SeriesId = "missing" },
            CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task RefreshFolder_RejectsEmptyRequest()
    {
        var result = await _controller.RefreshFolder(new MetadataController.RefreshFolderRequest(), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetProviders_ReturnsHealthSnapshot()
    {
        _external.Setup(e => e.CheckHealthAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderHealth
            {
                Name = "ComicVine",
                Enabled = true,
                Configured = true,
                Reachable = true,
                StatusMessage = "Reachable"
            });

        var result = await _controller.GetProviders(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var providersProp = ok.Value!.GetType().GetProperty("providers")!.GetValue(ok.Value);
        var providers = Assert.IsAssignableFrom<IEnumerable<ProviderHealth>>(providersProp!);
        var single = Assert.Single(providers);
        Assert.Equal("ComicVine", single.Name);
        Assert.True(single.Reachable);
    }

    [Fact]
    public async Task Search_ReturnsResultsScoredAndSortedByConfidence()
    {
        _external.Setup(e => e.SearchSeriesAsync("Batman", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ExternalSeriesMetadata>
            {
                new() { CanonicalTitle = "Naruto", Source = "MangaDex" },
                new() { CanonicalTitle = "Batman", Source = "ComicVine", ImageUrl = "https://x/b.jpg" },
                new() { CanonicalTitle = "Batman: Year One", Source = "ComicVine" }
            });

        var result = await _controller.Search("Batman", 10, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var resultsProp = ok.Value!.GetType().GetProperty("results")!.GetValue(ok.Value);
        var enumerable = Assert.IsAssignableFrom<System.Collections.IEnumerable>(resultsProp!);
        var list = enumerable.Cast<object>().ToList();
        Assert.Equal(3, list.Count);

        // Exact "Batman" canonical match must score 100 and come first.
        var firstScore = (double)list[0].GetType().GetProperty("match_score")!.GetValue(list[0])!;
        var firstTitle = (string)list[0].GetType().GetProperty("canonical_title")!.GetValue(list[0])!;
        Assert.Equal(100d, firstScore);
        Assert.Equal("Batman", firstTitle);

        // Image URL must be propagated so the client can echo it back when
        // applying the match.
        var imageUrlProp = list[0].GetType().GetProperty("image_url")!.GetValue(list[0]);
        Assert.Equal("https://x/b.jpg", imageUrlProp);

        // Naruto (no similarity) must score lower than Batman: Year One (substring).
        var lastScore = (double)list[2].GetType().GetProperty("match_score")!.GetValue(list[2])!;
        Assert.True(firstScore >= lastScore);
    }

    [Fact]
    public async Task ApplyMatch_QueuesNormalizeAndRenameJobForMatchingFiles()
    {
        var record = new SeriesMetadataCacheRecord
        {
            NormalizedKey = "batman",
            CanonicalTitle = "Batman: Year One",
            Aliases = new List<string> { "Year One" },
            Source = "ComicVine",
            LookupStatus = "manual_match"
        };
        _cache.Setup(c => c.ApplyExternalMatchAsync(
                "Batman",
                It.IsAny<ExternalSeriesMetadata>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);

        var processor = new Mock<IComicProcessorService>();
        var fileStore = new Mock<IFileStoreService>();

        // Three files: one whose metadata.Series matches "Batman",
        // one whose parent folder is "Batman", and one unrelated.
        var allFiles = new List<ComicFile>
        {
            new() { FilePath = "/library/Other/Batman 001.cbz",
                    Metadata = new ComicMetadata { Series = "Batman" } },
            new() { FilePath = "/library/Batman/Batman 002.cbz" },
            new() { FilePath = "/library/Other/Superman 001.cbz",
                    Metadata = new ComicMetadata { Series = "Superman" } }
        };
        fileStore.Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync(allFiles);

        List<string>? queuedPaths = null;
        processor.Setup(p => p.NormalizeAndRenameFilesAsync(
                    It.IsAny<IEnumerable<string>>(),
                    It.IsAny<CancellationToken>()))
                 .Callback<IEnumerable<string>, CancellationToken>((paths, _) => queuedPaths = paths.ToList())
                 .ReturnsAsync(Guid.NewGuid());

        var controller = new MetadataController(
            _library.Object,
            _cache.Object,
            _refreshJobs.Object,
            _external.Object,
            new Mock<ILogger<MetadataController>>().Object,
            processor.Object,
            fileStore.Object);

        var result = await controller.ApplyMatch(
            "Batman",
            new MetadataController.ApplyMatchRequest
            {
                CanonicalTitle = "Batman: Year One",
                Aliases = new List<string> { "Year One" },
                Source = "ComicVine"
            },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(queuedPaths);
        Assert.Equal(2, queuedPaths!.Count);
        Assert.Contains("/library/Other/Batman 001.cbz", queuedPaths);
        Assert.Contains("/library/Batman/Batman 002.cbz", queuedPaths);
        Assert.DoesNotContain("/library/Other/Superman 001.cbz", queuedPaths);
    }

    [Fact]
    public async Task ApplyMatch_StillReturnsRecord_WhenProcessorNotInjected()
    {
        var record = new SeriesMetadataCacheRecord
        {
            NormalizedKey = "batman",
            CanonicalTitle = "Batman: Year One",
            LookupStatus = "manual_match"
        };
        _cache.Setup(c => c.ApplyExternalMatchAsync(
                "Batman",
                It.IsAny<ExternalSeriesMetadata>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);

        // _controller was constructed without processor/fileStore, so the
        // post-match job is silently skipped and the response remains the
        // unchanged SeriesMetadataCacheRecord shape consumed by the UI.
        var result = await _controller.ApplyMatch(
            "Batman",
            new MetadataController.ApplyMatchRequest { CanonicalTitle = "Batman: Year One" },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Same(record, ok.Value);
    }

    [Fact]
    public async Task ApplyMatch_DelegatesToCacheService()
    {
        var record = new SeriesMetadataCacheRecord
        {
            NormalizedKey = "batman",
            CanonicalTitle = "Batman: Year One",
            Aliases = new List<string> { "Year One" },
            Source = "ComicVine",
            LookupStatus = "manual_match"
        };
        _cache.Setup(c => c.ApplyExternalMatchAsync(
                "Batman",
                It.Is<ExternalSeriesMetadata>(m =>
                    m.CanonicalTitle == "Batman: Year One"
                    && m.Source == "ComicVine"
                    && m.Aliases.Contains("Year One")),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);

        var result = await _controller.ApplyMatch(
            "Batman",
            new MetadataController.ApplyMatchRequest
            {
                CanonicalTitle = "Batman: Year One",
                Aliases = new List<string> { "Year One" },
                Source = "ComicVine"
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Same(record, ok.Value);
    }

    [Fact]
    public async Task ApplyMatch_RejectsEmptyCanonical()
    {
        var bad = await _controller.ApplyMatch("Batman", new MetadataController.ApplyMatchRequest(), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(bad.Result);

        var bad2 = await _controller.ApplyMatch("", new MetadataController.ApplyMatchRequest { CanonicalTitle = "X" }, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(bad2.Result);
    }

    [Fact]
    public async Task ClearExternal_DelegatesToCacheService()
    {
        var record = new SeriesMetadataCacheRecord
        {
            NormalizedKey = "batman",
            CanonicalTitle = "batman",
            LookupStatus = "cleared"
        };
        _cache.Setup(c => c.ClearExternalMetadataAsync("batman", It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);

        var result = await _controller.ClearExternal("Batman", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Same(record, ok.Value);
    }

    [Fact]
    public async Task ClearExternal_ReturnsNotFoundWhenMissing()
    {
        _cache.Setup(c => c.ClearExternalMetadataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SeriesMetadataCacheRecord?)null);

        var result = await _controller.ClearExternal("Batman", CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }
}
