using System.Net;
using System.Text;
using System.Text.Json;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Finding series on Suwayomi's sources and adding them to its library:
/// listing sources, searching one, adding a series, queueing the chapters the
/// local library lacks, and proxying covers.
/// </summary>
public sealed class SuwayomiServiceSourceSearchTests
{
    private const string BaseUrl = "http://suwayomi.example:4567";
    private const string UnexpectedResponse = "The Suwayomi server returned an unexpected response.";
    private const int MaxThumbnailBytes = 10 * 1024 * 1024;
    private static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(30);
    private static readonly byte[] Jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };

    private readonly ListLogger _logger = new();
    private readonly List<HttpClient> _clients = new();

    private static AppSettings Settings(
        bool enabled = true,
        string baseUrl = BaseUrl + "/",
        string? username = null,
        string? password = null) => new()
    {
        EnableSuwayomi = enabled,
        SuwayomiBaseUrl = baseUrl,
        SuwayomiUsername = username,
        SuwayomiPassword = password
    };

    private SuwayomiService CreateService(SuwayomiHandler handler, AppSettings? settings = null)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(SuwayomiService.HttpClientName))
            .Returns(() =>
            {
                // Like the registered client: a short timeout for a server on
                // the local network.
                var client = new HttpClient(handler, disposeHandler: false) { Timeout = ClientTimeout };
                _clients.Add(client);
                return client;
            });
        var options = new Mock<IOptionsMonitor<AppSettings>>();
        options.Setup(o => o.CurrentValue).Returns(settings ?? Settings());
        return new SuwayomiService(factory.Object, options.Object, _logger);
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Image(byte[] content, string contentType = "image/jpeg")
    {
        var body = new ByteArrayContent(content);
        body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = body };
    }

    /// <summary>
    /// A server that answers each GraphQL operation with its canned response
    /// and fails the test for an operation it was not given.
    /// </summary>
    private static SuwayomiHandler Server(params (string Operation, string Json)[] responses)
    {
        var byOperation = responses.ToDictionary(r => r.Operation, r => r.Json, StringComparer.Ordinal);
        return new SuwayomiHandler(request =>
            Json(byOperation.TryGetValue(request.Operation, out var json)
                ? json
                : throw new InvalidOperationException("Unexpected GraphQL operation: " + request.Operation)));
    }

    private static string Sources(string nodes) => """{"data":{"sources":{"nodes":""" + nodes + "}}}";

    private static string Library(string nodes) =>
        """{"data":{"mangas":{"nodes":""" + nodes + ""","pageInfo":{"hasNextPage":false,"endCursor":null}}}}""";

    private static string SearchResults(string mangas, string hasNextPage = "false") =>
        """{"data":{"fetchSourceManga":{"mangas":""" + mangas + ""","hasNextPage":""" + hasNextPage + "}}}";

    private static string Manga(int id, string title, bool inLibrary) =>
        JsonSerializer.Serialize(new { data = new { manga = new { id, title, inLibrary, thumbnailUrl = default(string) } } });

    private static string FetchedChapters(string chapters) =>
        """{"data":{"fetchChapters":{"chapters":""" + chapters + "}}}";

    private const string Enqueued = """{"data":{"enqueueChapterDownloads":{"clientMutationId":null}}}""";

    [Fact]
    public async Task GetSourcesAsync_ListsTheCatalogueSourcesByDisplayNameWithoutTheLocalSource()
    {
        var handler = Server(("Sources", Sources("""
            [
              {"id":"0","name":"Local source","lang":"localsourcelang","displayName":"Local source"},
              {"id":"2499283573021220255","name":"MangaDex","lang":"en","displayName":"MangaDex (EN)"},
              {"id":"1998944621602463790","name":"MANGA Plus by SHUEISHA","lang":"all","displayName":"manga plus"},
              {"id":"2499283573021220255","name":"MangaDex","lang":"en","displayName":"MangaDex again"},
              {"id":7,"name":"Numeric","lang":"fr","displayName":""},
              {"id":"12","name":" ","lang":null,"displayName":null},
              {"id":"-5","name":"Negative","lang":"en","displayName":"Negative"},
              {"id":"1e3","name":"Exponent","lang":"en","displayName":"Exponent"},
              {"id":"abc","name":"Letters","lang":"en","displayName":"Letters"},
              {"name":"No id","lang":"en","displayName":"No id"},
              "not a source"
            ]
            """)));

        var sources = await CreateService(handler).GetSourcesAsync();

        Assert.Equal(
            new[] { "12", "1998944621602463790", "2499283573021220255", "7" },
            sources.Select(s => s.Id));
        Assert.Equal(new[] { "12", "manga plus", "MangaDex (EN)", "Numeric" }, sources.Select(s => s.DisplayName));
        Assert.Equal(new[] { "12", "MANGA Plus by SHUEISHA", "MangaDex", "Numeric" }, sources.Select(s => s.Name));
        Assert.Equal(new[] { "", "all", "en", "fr" }, sources.Select(s => s.Lang));
        var request = Assert.Single(handler.Requests);
        Assert.Equal(BaseUrl + "/api/graphql", request.Uri);
        Assert.Equal("Sources", request.Operation);
    }

    [Theory]
    [InlineData("""{"data":{}}""")]
    [InlineData("""{"data":{"sources":null}}""")]
    [InlineData("""{"data":{"sources":{"nodes":null}}}""")]
    [InlineData("""{"data":{"sources":{"nodes":{}}}}""")]
    public async Task GetSourcesAsync_ResponseWithoutASourceList_ThrowsUnexpectedResponse(string json)
    {
        var service = CreateService(new SuwayomiHandler(_ => Json(json)));

        var exception = await Assert.ThrowsAsync<SuwayomiException>(() => service.GetSourcesAsync());

        Assert.Equal(UnexpectedResponse, exception.Message);
    }

    [Fact]
    public async Task SearchSourceAsync_SendsTheSourceIdAsALongStringWithTheTrimmedTerm()
    {
        var handler = Server(("SearchSource", SearchResults("[]")));

        await CreateService(handler).SearchSourceAsync(long.MaxValue, "  Solo Leveling  ", 3);

        var request = Assert.Single(handler.Requests);
        Assert.Contains("fetchSourceManga(input: { source: $source, type: SEARCH, query: $query, page: $page })", request.Query);
        var source = request.Variables.GetProperty("source");
        Assert.Equal(JsonValueKind.String, source.ValueKind);
        Assert.Equal("9223372036854775807", source.GetString());
        Assert.Equal("Solo Leveling", request.Variables.GetProperty("query").GetString());
        Assert.Equal(3, request.Variables.GetProperty("page").GetInt32());
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(0, 1)]
    [InlineData(-4, 1)]
    [InlineData(7, 7)]
    public async Task SearchSourceAsync_AsksForAtLeastTheFirstPage(int page, int expected)
    {
        var handler = Server(("SearchSource", SearchResults("[]")));

        await CreateService(handler).SearchSourceAsync(42, "Solo", page);

        Assert.Equal(expected, Assert.Single(handler.Requests).Variables.GetProperty("page").GetInt32());
    }

    [Fact]
    public async Task SearchSourceAsync_RanksTheClosestTitlesFirstAndKeepsTheSourcesOrderWithinARank()
    {
        var handler = Server(("SearchSource", SearchResults("""
            [
              {"id":1,"title":"Solo Leveling: Ragnarok","inLibrary":false,"thumbnailUrl":"/api/v1/manga/1/thumbnail"},
              {"id":2,"title":"The Solo Leveling Side Story","inLibrary":false},
              {"id":3,"title":"Ore dake Level Up na Ken","inLibrary":false},
              {"id":4,"title":"Solo Leveling","inLibrary":true},
              {"id":1,"title":"Solo Leveling: Ragnarok (again)","inLibrary":false},
              {"id":5,"title":"Solo","inLibrary":false},
              {"id":6,"title":"solo-leveling","inLibrary":false},
              {"id":7,"title":"Solo Leveling Gate","inLibrary":false},
              {"title":"No id"},
              {"id":8}
            ]
            """, hasNextPage: "true")));

        var result = await CreateService(handler).SearchSourceAsync(42, "Solo Leveling");

        // Exact, then prefix, contained and containing titles; a title with no
        // overlap stays listed, since sources also match alternative titles.
        Assert.Equal(new[] { 4, 6, 1, 7, 2, 5, 3, 8 }, result.Series.Select(s => s.Id));
        Assert.True(result.HasNextPage);
        Assert.Equal("Solo Leveling: Ragnarok", result.Series[2].Title);
        Assert.Equal("/api/v1/manga/1/thumbnail", result.Series[2].ThumbnailUrl);
        Assert.True(result.Series[0].InLibrary);
        Assert.False(result.Series[1].InLibrary);
        Assert.Equal("Series 8", result.Series[^1].Title);
    }

    [Theory]
    [InlineData("""{"data":{"fetchSourceManga":{"mangas":[]}}}""")]
    [InlineData("""{"data":{"fetchSourceManga":{"mangas":[],"hasNextPage":null}}}""")]
    [InlineData("""{"data":{"fetchSourceManga":{"mangas":[],"hasNextPage":false}}}""")]
    public async Task SearchSourceAsync_WithoutAFurtherPage_SaysSo(string json)
    {
        var result = await CreateService(new SuwayomiHandler(_ => Json(json))).SearchSourceAsync(42, "Solo");

        Assert.Empty(result.Series);
        Assert.False(result.HasNextPage);
    }

    [Theory]
    [InlineData(0L, "Solo", "A Suwayomi source id is required.")]
    [InlineData(-1L, "Solo", "A Suwayomi source id is required.")]
    [InlineData(42L, null, "A search term is required.")]
    [InlineData(42L, "   ", "A search term is required.")]
    public async Task SearchSourceAsync_WithoutASourceOrTerm_ThrowsWithoutCallingTheServer(
        long sourceId,
        string? query,
        string expected)
    {
        var handler = Server();

        var exception = await Assert.ThrowsAsync<SuwayomiException>(
            () => CreateService(handler).SearchSourceAsync(sourceId, query!));

        Assert.Equal(expected, exception.Message);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("""{"data":{"fetchSourceManga":null}}""")]
    [InlineData("""{"data":{"fetchSourceManga":{"hasNextPage":false}}}""")]
    [InlineData("""{"data":{"fetchSourceManga":{"mangas":null,"hasNextPage":false}}}""")]
    public async Task SearchSourceAsync_ResponseWithoutASeriesList_ThrowsUnexpectedResponse(string json)
    {
        var service = CreateService(new SuwayomiHandler(_ => Json(json)));

        var exception = await Assert.ThrowsAsync<SuwayomiException>(() => service.SearchSourceAsync(42, "Solo"));

        Assert.Equal(UnexpectedResponse, exception.Message);
    }

    [Fact]
    public async Task DownloadMissingIssuesAsync_SearchesOtherSourcesWithoutFollowingTheFallbackSeries()
    {
        var handler = new SuwayomiHandler(request => request.Operation switch
        {
            "Library" => Json(Library("""
                [{"id":4,"title":"Solo Leveling","inLibrary":true}]
                """)),
            "Chapters" => Json("""
                {"data":{"chapters":{"nodes":[
                  {"id":41,"name":"Chapter 1","chapterNumber":1,"isDownloaded":false}
                ]}}}
                """),
            "Sources" => Json(Sources("""
                [
                  {"id":"10","name":"Followed","lang":"en","displayName":"Followed (EN)"},
                  {"id":"20","name":"Fallback","lang":"en","displayName":"Fallback (EN)"}
                ]
                """)),
            "SearchSource" when request.Variables.GetProperty("source").GetString() == "10" =>
                Json(SearchResults("""[{"id":4,"title":"Solo Leveling","inLibrary":true}]""")),
            "SearchSource" => Json(SearchResults("""
                [{"id":8,"title":"Solo Leveling","inLibrary":false}]
                """)),
            "FetchChapters" => Json(FetchedChapters("""
                [
                  {"id":82,"name":"Chapter 2","chapterNumber":2,"isDownloaded":false},
                  {"id":83,"name":"Chapter 3","chapterNumber":3,"isDownloaded":false}
                ]
                """)),
            "Enqueue" => Json(Enqueued),
            _ => throw new InvalidOperationException("Unexpected GraphQL operation: " + request.Operation)
        });

        var result = await CreateService(handler).DownloadMissingIssuesAsync(
            "Solo Leveling",
            new[] { 2, 3 });

        Assert.Equal(8, result.Series!.Id);
        Assert.Equal("Fallback (EN)", result.Series.Source);
        Assert.False(result.Series.InLibrary);
        Assert.Equal(new[] { 82, 83 }, result.QueuedChapters.Select(chapter => chapter.Id));
        Assert.Empty(result.UnmatchedIssueNumbers);
        Assert.Empty(result.DiscoveryFailures);
        Assert.Equal(2, handler.Requests.Count(request => request.Operation == "SearchSource"));
        Assert.DoesNotContain(handler.Requests, request => request.Operation == "AddToLibrary");
        var enqueue = Assert.Single(handler.Requests, request => request.Operation == "Enqueue");
        Assert.Equal(
            new[] { 82, 83 },
            enqueue.Variables.GetProperty("ids").EnumerateArray().Select(id => id.GetInt32()));
    }

    [Fact]
    public async Task DownloadMissingIssuesAsync_SearchesAliasesWhenTheTitleFindsNoExactSourceMatch()
    {
        var handler = new SuwayomiHandler(request => request.Operation switch
        {
            "Library" => Json(Library("""
                [{"id":4,"title":"Solo Leveling","inLibrary":true}]
                """)),
            "Chapters" => Json("""{"data":{"chapters":{"nodes":[]}}}"""),
            "Sources" => Json(Sources("""
                [{"id":"20","name":"Fallback","lang":"ja","displayName":"Fallback (JA)"}]
                """)),
            "SearchSource" when request.Variables.GetProperty("query").GetString() == "Solo Leveling" =>
                Json(SearchResults("""
                    [{"id":9,"title":"Solo Leveling: Ragnarok","inLibrary":false}]
                    """)),
            "SearchSource" when request.Variables.GetProperty("query").GetString() == "Ore dake Level Up na Ken" =>
                Json(SearchResults("""
                    [{"id":8,"title":"Ore dake Level Up na Ken","inLibrary":false}]
                    """)),
            "SearchSource" => throw new InvalidOperationException("Searching should stop after the first matching alias."),
            "FetchChapters" => Json(FetchedChapters("""
                [{"id":82,"name":"Chapter 2","chapterNumber":2,"isDownloaded":false}]
                """)),
            "Enqueue" => Json(Enqueued),
            _ => throw new InvalidOperationException("Unexpected GraphQL operation: " + request.Operation)
        });

        var result = await CreateService(handler).DownloadMissingIssuesAsync(
            "Solo Leveling",
            new[] { 2 },
            alternateTitles: new[] { "Ore dake Level Up na Ken", "Na Honjaman Level Up" });

        Assert.Equal(8, result.Series!.Id);
        Assert.Equal("Ore dake Level Up na Ken", result.Series.Title);
        Assert.Equal(new[] { 82 }, result.QueuedChapters.Select(chapter => chapter.Id));
        Assert.Equal(
            new[] { "Solo Leveling", "Ore dake Level Up na Ken" },
            handler.Requests
                .Where(request => request.Operation == "SearchSource")
                .Select(request => request.Variables.GetProperty("query").GetString()));
    }

    [Fact]
    public async Task DownloadMissingIssuesAsync_ReportsOneSourceFailureAndUsesAnother()
    {
        var handler = new SuwayomiHandler(request => request.Operation switch
        {
            "Library" => Json(Library("""
                [{"id":4,"title":"Solo Leveling","inLibrary":true}]
                """)),
            "Chapters" => Json("""{"data":{"chapters":{"nodes":[]}}}"""),
            "Sources" => Json(Sources("""
                [
                  {"id":"10","name":"Broken","lang":"en","displayName":"Broken (EN)"},
                  {"id":"20","name":"Working","lang":"en","displayName":"Working (EN)"}
                ]
                """)),
            "SearchSource" when request.Variables.GetProperty("source").GetString() == "10" =>
                Json("""{"errors":[{"message":"site unavailable","path":["fetchSourceManga"]}],"data":{"fetchSourceManga":null}}"""),
            "SearchSource" => Json(SearchResults("""
                [{"id":8,"title":"Solo Leveling","inLibrary":false}]
                """)),
            "FetchChapters" => Json(FetchedChapters("""
                [{"id":82,"name":"Chapter 2","chapterNumber":2,"isDownloaded":false}]
                """)),
            "Enqueue" => Json(Enqueued),
            _ => throw new InvalidOperationException("Unexpected GraphQL operation: " + request.Operation)
        });

        var result = await CreateService(handler).DownloadMissingIssuesAsync(
            "Solo Leveling",
            new[] { 2 });

        Assert.Equal(new[] { 82 }, result.QueuedChapters.Select(chapter => chapter.Id));
        var failure = Assert.Single(result.DiscoveryFailures);
        Assert.Equal("Broken (EN)", failure.Target);
        Assert.Contains("site unavailable", failure.Error);
    }

    [Fact]
    public async Task DownloadMissingIssuesAsync_DoesNotUseAPartialSourceMatchBesideAnExactLibraryMatch()
    {
        var handler = new SuwayomiHandler(request => request.Operation switch
        {
            "Library" => Json(Library("""
                [{"id":4,"title":"Solo Leveling","inLibrary":true}]
                """)),
            "Chapters" => Json("""{"data":{"chapters":{"nodes":[]}}}"""),
            "Sources" => Json(Sources("""
                [{"id":"20","name":"Fallback","lang":"en","displayName":"Fallback (EN)"}]
                """)),
            "SearchSource" => Json(SearchResults("""
                [
                  {"id":4,"title":"Solo Leveling","inLibrary":true},
                  {"id":8,"title":"Solo Leveling: Ragnarok","inLibrary":false}
                ]
                """)),
            _ => throw new InvalidOperationException("A sequel must not be fetched or queued.")
        });

        var result = await CreateService(handler).DownloadMissingIssuesAsync(
            "Solo Leveling",
            new[] { 2 });

        Assert.Equal(4, result.Series!.Id);
        Assert.Equal(new[] { 2 }, result.UnmatchedIssueNumbers);
        Assert.DoesNotContain(handler.Requests, request => request.Operation == "FetchChapters");
        Assert.DoesNotContain(handler.Requests, request => request.Operation == "Enqueue");
    }

    [Fact]
    public async Task RequestsThatMakeSuwayomiAskTheSourcesSite_WaitLongerThanTheClientDefault()
    {
        var service = CreateService(Server(
            ("Sources", Sources("[]")),
            ("SearchSource", SearchResults("[]")),
            ("FetchChapters", FetchedChapters("[]"))));

        await service.GetSourcesAsync();
        await service.SearchSourceAsync(42, "Solo");
        await service.QueueSeriesDownloadsAsync(5);

        Assert.Equal(
            new[] { ClientTimeout, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120) },
            _clients.Select(c => c.Timeout));
    }

    [Fact]
    public async Task AddToLibraryAsync_AddsASeriesThatIsNotInTheLibraryYet()
    {
        var handler = Server(
            ("Manga", Manga(5, "Solo Leveling", inLibrary: false)),
            ("AddToLibrary", """
                {"data":{"updateManga":{"manga":{"id":5,"title":"Solo Leveling","inLibrary":true,"thumbnailUrl":"/api/v1/manga/5/thumbnail"}}}}
                """));

        var result = await CreateService(handler).AddToLibraryAsync(5);

        Assert.False(result.AlreadyInLibrary);
        Assert.Equal(5, result.Series.Id);
        Assert.Equal("Solo Leveling", result.Series.Title);
        Assert.True(result.Series.InLibrary);
        Assert.Equal("/api/v1/manga/5/thumbnail", result.Series.ThumbnailUrl);
        Assert.Equal(new[] { "Manga", "AddToLibrary" }, handler.Requests.Select(r => r.Operation));
        var update = handler.Requests[1];
        Assert.Contains("updateManga(input: { id: $id, patch: { inLibrary: true } })", update.Query);
        Assert.Equal(5, update.Variables.GetProperty("id").GetInt32());
        var info = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Information, info.Level);
        Assert.Equal("Added series 5 (Solo Leveling) to the Suwayomi library", info.Message);
    }

    [Fact]
    public async Task AddToLibraryAsync_SeriesAlreadyInTheLibrary_IsLeftAlone()
    {
        // Setting inLibrary again would move the series' "added" date.
        var handler = Server(("Manga", Manga(5, "Solo Leveling", inLibrary: true)));

        var result = await CreateService(handler).AddToLibraryAsync(5);

        Assert.True(result.AlreadyInLibrary);
        Assert.Equal(5, result.Series.Id);
        Assert.Equal("Solo Leveling", result.Series.Title);
        Assert.Equal("Manga", Assert.Single(handler.Requests).Operation);
        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public async Task AddToLibraryAsync_UnknownSeries_ThrowsWithoutUpdatingAnything()
    {
        var handler = Server(("Manga", """{"data":{"manga":null}}"""));

        var exception = await Assert.ThrowsAsync<SuwayomiException>(() => CreateService(handler).AddToLibraryAsync(5));

        Assert.Equal("Suwayomi has no series with id 5.", exception.Message);
        Assert.Equal("Manga", Assert.Single(handler.Requests).Operation);
    }

    [Fact]
    public async Task AddToLibraryAsync_ServerThatLeavesTheSeriesOutOfItsLibrary_Throws()
    {
        var handler = Server(
            ("Manga", Manga(5, "Solo Leveling", inLibrary: false)),
            ("AddToLibrary", """{"data":{"updateManga":{"manga":{"id":5,"title":"Solo Leveling","inLibrary":false}}}}"""));

        var exception = await Assert.ThrowsAsync<SuwayomiException>(() => CreateService(handler).AddToLibraryAsync(5));

        Assert.Equal("The Suwayomi server did not add 'Solo Leveling' to its library.", exception.Message);
        Assert.Empty(_logger.Entries);
    }

    [Theory]
    [InlineData("""{"data":{}}""")]
    [InlineData("""{"data":{"updateManga":null}}""")]
    [InlineData("""{"data":{"updateManga":{"manga":null}}}""")]
    [InlineData("""{"data":{"updateManga":{"manga":{"title":"No id","inLibrary":true}}}}""")]
    public async Task AddToLibraryAsync_UnexpectedUpdateResponse_ThrowsUnexpectedResponse(string json)
    {
        var handler = Server(("Manga", Manga(5, "Solo Leveling", inLibrary: false)), ("AddToLibrary", json));

        var exception = await Assert.ThrowsAsync<SuwayomiException>(() => CreateService(handler).AddToLibraryAsync(5));

        Assert.Equal(UnexpectedResponse, exception.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task SeriesRequests_WithoutAValidSeriesId_ThrowWithoutCallingTheServer(int seriesId)
    {
        var handler = Server();
        var service = CreateService(handler);
        var expected = $"Suwayomi has no series with id {seriesId}.";

        Assert.Equal(expected, (await Assert.ThrowsAsync<SuwayomiException>(() => service.AddToLibraryAsync(seriesId))).Message);
        Assert.Equal(expected, (await Assert.ThrowsAsync<SuwayomiException>(() => service.QueueSeriesDownloadsAsync(seriesId))).Message);
        Assert.Equal(expected, (await Assert.ThrowsAsync<SuwayomiException>(() => service.GetThumbnailAsync(seriesId))).Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task QueueSeriesDownloadsAsync_QueuesOneChapterPerNumberTheLibraryLacks()
    {
        // Suwayomi stores chapter numbers as 32-bit floats: 10.1 arrives as
        // 10.100000381469727.
        var handler = Server(
            ("FetchChapters", FetchedChapters("""
                [
                  {"id":110,"name":"Chapter 11","chapterNumber":11,"isDownloaded":false},
                  {"id":101,"name":"Chapter 10.1","chapterNumber":10.100000381469727,"isDownloaded":false},
                  {"id":100,"name":"Chapter 10","chapterNumber":10,"isDownloaded":false},
                  {"id":91,"name":"Chapter 9 (group A)","chapterNumber":9,"isDownloaded":false},
                  {"id":92,"name":"Chapter 9 (group B)","chapterNumber":9,"isDownloaded":false},
                  {"id":81,"name":"Chapter 8","chapterNumber":8,"isDownloaded":true},
                  {"id":82,"name":"Chapter 8 (group B)","chapterNumber":8,"isDownloaded":false},
                  {"id":70,"name":"Chapter 7","chapterNumber":7,"isDownloaded":false},
                  {"id":1,"name":"Hiatus notice","chapterNumber":-1,"isDownloaded":false},
                  {"id":2,"name":"Extra","isDownloaded":false},
                  {"name":"No id","chapterNumber":12,"isDownloaded":false}
                ]
                """)),
            ("Enqueue", Enqueued));

        var result = await CreateService(handler).QueueSeriesDownloadsAsync(
            5,
            new[] { "007", "10.1", "Annual", null, " ", "1/2", "-8" });

        Assert.Equal(10, result.ChapterCount);
        Assert.Equal(new[] { 91, 100, 110 }, result.QueuedChapters.Select(c => c.Id));
        Assert.Equal(2, result.AlreadyInLocalLibraryCount);
        Assert.Equal(1, result.AlreadyDownloadedCount);
        Assert.Equal(1, result.DuplicateChapterCount);
        Assert.Equal(2, result.UnnumberedChapterCount);
        Assert.Equal(new[] { "FetchChapters", "Enqueue" }, handler.Requests.Select(r => r.Operation));
        Assert.Contains("fetchChapters(input: { mangaId: $id })", handler.Requests[0].Query);
        Assert.Equal(5, handler.Requests[0].Variables.GetProperty("id").GetInt32());
        Assert.Equal(
            new[] { 91, 100, 110 },
            handler.Requests[1].Variables.GetProperty("ids").EnumerateArray().Select(id => id.GetInt32()));
        var info = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Information, info.Level);
        Assert.Equal("Queued 3 Suwayomi chapter download(s) for series 5", info.Message);
    }

    [Fact]
    public async Task QueueSeriesDownloadsAsync_DistinctFractionalNumbers_AreQueuedSeparately()
    {
        // 10.001 and 10.002 as 32-bit floats; neither is the local issue 10.
        var handler = Server(
            ("FetchChapters", FetchedChapters("""
                [
                  {"id":100,"name":"Chapter 10","chapterNumber":10,"isDownloaded":false},
                  {"id":1001,"name":"Chapter 10.001","chapterNumber":10.00100040435791,"isDownloaded":false},
                  {"id":1002,"name":"Chapter 10.002","chapterNumber":10.001999855041504,"isDownloaded":false}
                ]
                """)),
            ("Enqueue", Enqueued));

        var result = await CreateService(handler).QueueSeriesDownloadsAsync(5, new[] { "10" });

        Assert.Equal(new[] { 1001, 1002 }, result.QueuedChapters.Select(c => c.Id));
        Assert.Equal(1, result.AlreadyInLocalLibraryCount);
        Assert.Equal(0, result.DuplicateChapterCount);
        Assert.Equal(
            new[] { 1001, 1002 },
            handler.Requests[1].Variables.GetProperty("ids").EnumerateArray().Select(id => id.GetInt32()));
    }

    [Fact]
    public async Task QueueSeriesDownloadsAsync_SeriesWithoutChapterNumbers_QueuesEveryChapterNotDownloaded()
    {
        var handler = Server(
            ("FetchChapters", FetchedChapters("""
                [
                  {"id":3,"name":"Oneshot","chapterNumber":-1,"isDownloaded":false},
                  {"id":4,"name":"Bonus","chapterNumber":-1,"isDownloaded":true},
                  {"id":5,"name":"Afterword","isDownloaded":false}
                ]
                """)),
            ("Enqueue", Enqueued));

        var result = await CreateService(handler).QueueSeriesDownloadsAsync(9, new[] { "1" });

        Assert.Equal(3, result.ChapterCount);
        Assert.Equal(new[] { 3, 5 }, result.QueuedChapters.Select(c => c.Id));
        Assert.Equal(1, result.AlreadyDownloadedCount);
        Assert.Equal(0, result.AlreadyInLocalLibraryCount);
        Assert.Equal(0, result.UnnumberedChapterCount);
        Assert.Equal(0, result.DuplicateChapterCount);
        Assert.Equal(
            new[] { 3, 5 },
            handler.Requests[1].Variables.GetProperty("ids").EnumerateArray().Select(id => id.GetInt32()));
    }

    [Theory]
    [InlineData("[]", 0, 0, 0)]
    [InlineData("""[{"id":10,"chapterNumber":1,"isDownloaded":true},{"id":20,"chapterNumber":2,"isDownloaded":false}]""", 2, 1, 1)]
    [InlineData("""[{"id":30,"chapterNumber":-1,"isDownloaded":true}]""", 1, 1, 0)]
    public async Task QueueSeriesDownloadsAsync_NothingMissing_QueuesNothing(
        string chapters,
        int chapterCount,
        int alreadyDownloaded,
        int alreadyInLocalLibrary)
    {
        // No Enqueue response: queueing an empty list would fail the test.
        var handler = Server(("FetchChapters", FetchedChapters(chapters)));

        var result = await CreateService(handler).QueueSeriesDownloadsAsync(5, new[] { "2" });

        Assert.Empty(result.QueuedChapters);
        Assert.Equal(chapterCount, result.ChapterCount);
        Assert.Equal(alreadyDownloaded, result.AlreadyDownloadedCount);
        Assert.Equal(alreadyInLocalLibrary, result.AlreadyInLocalLibraryCount);
        Assert.Equal("FetchChapters", Assert.Single(handler.Requests).Operation);
        Assert.Empty(_logger.Entries);
    }

    [Theory]
    [InlineData("""{"data":{"fetchChapters":null}}""")]
    [InlineData("""{"data":{"fetchChapters":{"chapters":null}}}""")]
    [InlineData("""{"data":{"fetchChapters":{"chapters":{}}}}""")]
    public async Task QueueSeriesDownloadsAsync_ResponseWithoutAChapterList_ThrowsUnexpectedResponse(string json)
    {
        var service = CreateService(new SuwayomiHandler(_ => Json(json)));

        var exception = await Assert.ThrowsAsync<SuwayomiException>(() => service.QueueSeriesDownloadsAsync(5));

        Assert.Equal(UnexpectedResponse, exception.Message);
    }

    [Theory]
    [InlineData("Sources")]
    [InlineData("SearchSource")]
    [InlineData("Manga")]
    [InlineData("AddToLibrary")]
    [InlineData("FetchChapters")]
    [InlineData("Enqueue")]
    public async Task RejectedDocuments_AreReportedUnderTheirOwnOperationName(string rejected)
    {
        var service = CreateService(new SuwayomiHandler(request => Json(
            request.Operation == rejected ? """{"errors":[{"message":"Validation error (FieldUndefined)"}]}"""
            : request.Operation == "Manga" ? Manga(5, "Solo Leveling", inLibrary: false)
            : request.Operation == "FetchChapters" ? FetchedChapters("""[{"id":50,"chapterNumber":1,"isDownloaded":false}]""")
            : throw new InvalidOperationException("Unexpected GraphQL operation: " + request.Operation))));

        Task Act() => rejected switch
        {
            "Sources" => service.GetSourcesAsync(),
            "SearchSource" => service.SearchSourceAsync(42, "Solo"),
            "Manga" or "AddToLibrary" => service.AddToLibraryAsync(5),
            _ => service.QueueSeriesDownloadsAsync(5)
        };

        var exception = await Assert.ThrowsAsync<SuwayomiException>(Act);

        Assert.Equal("The Suwayomi server rejected the request: Validation error (FieldUndefined)", exception.Message);
        var error = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Error, error.Level);
        Assert.StartsWith($"Suwayomi rejected the {rejected} GraphQL request before running it;", error.Message);
    }

    [Fact]
    public async Task GetThumbnailAsync_FetchesTheCoverBesideTheGraphQlEndpointWithTheServersCredentials()
    {
        var handler = new SuwayomiHandler(_ => Image(Jpeg));
        var service = CreateService(
            handler,
            Settings(baseUrl: "https://media.example/suwayomi/", username: "reader", password: "secret"));

        var thumbnail = await service.GetThumbnailAsync(42);

        Assert.Equal(Jpeg, thumbnail.Content);
        Assert.Equal("image/jpeg", thumbnail.ContentType);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://media.example/suwayomi/api/v1/manga/42/thumbnail", request.Uri);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("reader:secret")), request.Authorization);
        Assert.Equal("image/*", request.Accept);
    }

    [Fact]
    public async Task GetThumbnailAsync_WithoutAUserName_SendsNoCredentials()
    {
        var handler = new SuwayomiHandler(_ => Image(Jpeg));

        await CreateService(handler).GetThumbnailAsync(42);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(BaseUrl + "/api/v1/manga/42/thumbnail", request.Uri);
        Assert.Null(request.Authorization);
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xDB }, "image/jpeg")]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00 }, "image/png")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x37, 0x61 }, "image/gif")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, "image/gif")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0x24, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50 }, "image/webp")]
    public async Task GetThumbnailAsync_TakesTheContentTypeFromTheBytesNotTheServer(byte[] content, string expected)
    {
        var service = CreateService(new SuwayomiHandler(_ => Image(content, "text/html")));

        var thumbnail = await service.GetThumbnailAsync(42);

        Assert.Equal(expected, thumbnail.ContentType);
        Assert.Equal(content, thumbnail.Content);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0xFF, 0xD8 })]
    [InlineData(new byte[] { 0x3C, 0x68, 0x74, 0x6D, 0x6C, 0x3E })]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x38, 0x61 })]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0x24, 0x00, 0x00, 0x00, 0x57, 0x41, 0x56, 0x45 })]
    public async Task GetThumbnailAsync_ContentThatIsNotASupportedImage_Throws(byte[] content)
    {
        var service = CreateService(new SuwayomiHandler(_ => Image(content)));

        var exception = await Assert.ThrowsAsync<SuwayomiException>(() => service.GetThumbnailAsync(42));

        Assert.Equal("The Suwayomi server did not return a supported image.", exception.Message);
    }

    [Fact]
    public async Task GetThumbnailAsync_CoverDeclaredLargerThanTheCap_IsRefusedUnread()
    {
        var service = CreateService(new SuwayomiHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnreadableContent(MaxThumbnailBytes + 1)
        }));

        var exception = await Assert.ThrowsAsync<SuwayomiException>(() => service.GetThumbnailAsync(42));

        Assert.Equal("The Suwayomi cover is too large.", exception.Message);
    }

    [Fact]
    public async Task GetThumbnailAsync_CoverOfUndeclaredLength_IsReadUpToTheCap()
    {
        var atCap = new GeneratedStream(Jpeg, MaxThumbnailBytes);
        var service = CreateService(new SuwayomiHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(atCap)
        }));

        var thumbnail = await service.GetThumbnailAsync(42);

        Assert.Equal(MaxThumbnailBytes, thumbnail.Content.Length);
        Assert.Equal("image/jpeg", thumbnail.ContentType);
    }

    [Fact]
    public async Task GetThumbnailAsync_CoverOfUndeclaredLengthPastTheCap_StopsReadingAtTheCap()
    {
        var oversize = new GeneratedStream(Jpeg, 2L * MaxThumbnailBytes);
        var service = CreateService(new SuwayomiHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(oversize)
        }));

        var exception = await Assert.ThrowsAsync<SuwayomiException>(() => service.GetThumbnailAsync(42));

        Assert.Equal("The Suwayomi cover is too large.", exception.Message);
        Assert.InRange(oversize.BytesRead, MaxThumbnailBytes, MaxThumbnailBytes + 128 * 1024);
    }

    [Fact]
    public async Task GetThumbnailAsync_HttpErrorStatus_ThrowsWithTheStatus()
    {
        var service = CreateService(new SuwayomiHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        var exception = await Assert.ThrowsAsync<SuwayomiException>(() => service.GetThumbnailAsync(42));

        Assert.Equal("The Suwayomi server returned HTTP 404 (Not Found).", exception.Message);
    }

    [Fact]
    public async Task GetThumbnailAsync_UnreachableServer_IsLoggedQuietly()
    {
        // A source whose site is down fails every cover of a search; the
        // search itself reports the problem.
        var failure = new HttpRequestException("Connection refused");
        var service = CreateService(new SuwayomiHandler(_ => throw failure));

        var exception = await Assert.ThrowsAsync<SuwayomiException>(() => service.GetThumbnailAsync(42));

        Assert.Equal("Could not reach the Suwayomi server: Connection refused", exception.Message);
        Assert.Same(failure, exception.InnerException);
        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Same(failure, entry.Exception);
    }

    [Fact]
    public async Task GetThumbnailAsync_CancelledByTheCaller_PropagatesTheCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var service = CreateService(new SuwayomiHandler(_ => Image(Jpeg)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetThumbnailAsync(42, cts.Token));

        Assert.Empty(_logger.Entries);
    }

    [Theory]
    [InlineData(false, BaseUrl, "The Suwayomi integration is disabled.")]
    [InlineData(true, "not a url", "No valid Suwayomi server URL is configured.")]
    [InlineData(true, "ftp://suwayomi.example", "No valid Suwayomi server URL is configured.")]
    public async Task Requests_WithoutAUsableServer_ThrowWithoutCallingIt(bool enabled, string baseUrl, string expected)
    {
        var handler = Server();
        var service = CreateService(handler, Settings(enabled, baseUrl));

        Assert.Equal(expected, (await Assert.ThrowsAsync<SuwayomiException>(() => service.GetThumbnailAsync(42))).Message);
        Assert.Equal(expected, (await Assert.ThrowsAsync<SuwayomiException>(() => service.GetSourcesAsync())).Message);
        Assert.Equal(expected, (await Assert.ThrowsAsync<SuwayomiException>(() => service.SearchSourceAsync(42, "Solo"))).Message);
        Assert.Equal(expected, (await Assert.ThrowsAsync<SuwayomiException>(() => service.AddToLibraryAsync(5))).Message);
        Assert.Empty(handler.Requests);
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        string Uri,
        string? Body,
        string? Authorization,
        string Accept)
    {
        /// <summary>The name of the GraphQL operation, such as "Sources".</summary>
        public string Operation => Query.TrimStart()
            .Split(new[] { ' ', '(', '{', '\r', '\n' }, 3, StringSplitOptions.RemoveEmptyEntries)[1];

        public string Query => Field("query").GetString()!;

        public JsonElement Variables => Field("variables");

        private JsonElement Field(string name)
        {
            using var document = JsonDocument.Parse(Body ?? throw new InvalidOperationException("The request has no body."));
            return document.RootElement.GetProperty(name).Clone();
        }
    }

    private sealed class SuwayomiHandler : HttpMessageHandler
    {
        private readonly Func<RecordedRequest, HttpResponseMessage> _respond;
        private readonly List<RecordedRequest> _requests = new();

        public SuwayomiHandler(Func<RecordedRequest, HttpResponseMessage> respond) => _respond = respond;

        public IReadOnlyList<RecordedRequest> Requests
        {
            get
            {
                lock (_requests)
                {
                    return _requests.ToList();
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var recorded = new RecordedRequest(
                request.Method,
                request.RequestUri!.AbsoluteUri,
                body,
                request.Headers.Authorization?.ToString(),
                request.Headers.Accept.ToString());
            lock (_requests)
            {
                _requests.Add(recorded);
            }

            return _respond(recorded);
        }
    }

    /// <summary>Content that declares a length and fails the test if read.</summary>
    private sealed class UnreadableContent : HttpContent
    {
        public UnreadableContent(long length) => Headers.ContentLength = length;

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            throw new InvalidOperationException("The content was read.");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>
    /// A stream that does not report its length: <paramref name="prefix"/>
    /// followed by zeros, <paramref name="length"/> bytes in all.
    /// </summary>
    private sealed class GeneratedStream(byte[] prefix, long length) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Fill(buffer.AsSpan(offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Fill(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Fill(buffer.AsSpan(offset, count)));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Fill(Span<byte> destination)
        {
            var count = (int)Math.Min(destination.Length, length - BytesRead);
            var target = destination[..count];
            target.Clear();
            for (var i = 0; i < count && BytesRead + i < prefix.Length; i++)
            {
                target[i] = prefix[BytesRead + i];
            }

            BytesRead += count;
            return count;
        }
    }

    private sealed class ListLogger : ILogger<SuwayomiService>
    {
        private readonly List<(LogLevel Level, string Message, Exception? Exception)> _entries = new();

        public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries
        {
            get
            {
                lock (_entries)
                {
                    return _entries.ToList();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add((logLevel, formatter(state, exception), exception));
            }
        }
    }
}
