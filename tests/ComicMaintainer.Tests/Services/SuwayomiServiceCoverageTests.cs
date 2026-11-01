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

public sealed class SuwayomiServiceCoverageTests
{
    private const string BaseUrl = "http://suwayomi.example:4567";
    private const string UnexpectedResponse = "The Suwayomi server returned an unexpected response.";
    private const string UnexpectedChapterListResponse =
        "The Suwayomi server returned an unexpected response: data.chapters.nodes must be an array.";
    private readonly ListLogger _logger = new();

    private static AppSettings Settings(bool enabled = true, string? baseUrl = BaseUrl + "/") => new()
    {
        EnableSuwayomi = enabled,
        SuwayomiBaseUrl = baseUrl!
    };

    private SuwayomiService CreateService(GraphQlHandler handler, AppSettings? settings = null)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler, disposeHandler: false));
        var options = new Mock<IOptionsMonitor<AppSettings>>();
        options.Setup(o => o.CurrentValue).Returns(settings ?? Settings());
        return new SuwayomiService(factory.Object, options.Object, _logger);
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static string Library(string nodes, string pageInfo = """{"hasNextPage":false,"endCursor":null}""")
        => """{"data":{"mangas":{"nodes":""" + nodes + ""","pageInfo":""" + pageInfo + "}}}";

    /// <summary>
    /// A server that answers each GraphQL document with the matching canned
    /// response and fails the test for any document it was not given.
    /// </summary>
    private static GraphQlHandler Server(
        string? library = null,
        string? chapters = null,
        string? manga = null,
        string? enqueue = null,
        string? sources = null)
        => new(body =>
        {
            var json = body.Contains("mangas(", StringComparison.Ordinal) ? library
                : body.Contains("chapters(", StringComparison.Ordinal) ? chapters
                : body.Contains("manga(", StringComparison.Ordinal) ? manga
                : body.Contains("enqueueChapterDownloads", StringComparison.Ordinal) ? enqueue
                : body.Contains("sources", StringComparison.Ordinal) ? sources
                : null;
            return Json(json ?? throw new InvalidOperationException("Unexpected GraphQL document: " + body));
        });

    private static JsonElement Variables(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("variables").Clone();
    }

    [Theory]
    [InlineData(true, BaseUrl, true)]
    [InlineData(false, BaseUrl, false)]
    [InlineData(true, "ftp://suwayomi.example", false)]
    [InlineData(true, "", false)]
    public void IsConfigured_RequiresTheIntegrationEnabledWithAnHttpUrl(bool enabled, string baseUrl, bool expected)
    {
        var service = CreateService(Server(), Settings(enabled, baseUrl));

        Assert.Equal(expected, service.IsConfigured);
    }

    [Fact]
    public async Task GetStatusAsync_InvalidUrl_ReportsNotConfiguredWithoutCallingTheServer()
    {
        var handler = Server();
        var service = CreateService(handler, Settings(baseUrl: "not a url"));

        var status = await service.GetStatusAsync();

        Assert.True(status.Enabled);
        Assert.False(status.Configured);
        Assert.False(status.Connected);
        Assert.Equal("No valid Suwayomi server URL is configured.", status.Error);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(null, 2, new[] { 1, 2 })]
    [InlineData("   ", 20, new[] { 1, 2, 3 })]
    [InlineData("!!!", 0, new[] { 1 })]
    public async Task SearchSeriesAsync_QueryWithoutLettersOrDigits_ReturnsTheLibraryInOrderUpToTheLimit(
        string? query,
        int limit,
        int[] expectedIds)
    {
        var handler = Server(library: Library("""
            [{"id":1,"title":"Zeta","inLibrary":true},{"id":2,"title":"Alpha","inLibrary":true},{"id":3,"title":"Mid","inLibrary":true}]
            """));
        var service = CreateService(handler);

        var results = await service.SearchSeriesAsync(query, limit);

        Assert.Equal(expectedIds, results.Select(s => s.Id));
        var request = Assert.Single(handler.Requests);
        Assert.Equal(BaseUrl + "/api/graphql", request.Uri);
        Assert.Equal(500, Variables(request.Body).GetProperty("first").GetInt32());
    }

    [Fact]
    public async Task SearchSeriesAsync_RanksExactPrefixContainedAndContainingTitles()
    {
        var service = CreateService(Server(library: Library("""
            [
              {"id":6,"title":"Unrelated","inLibrary":true},
              {"id":5,"title":"!!!","inLibrary":true},
              {"id":4,"title":"Solo","inLibrary":true},
              {"id":3,"title":"The Solo Leveling Side Story","inLibrary":true},
              {"id":2,"title":"Solo Leveling: Ragnarok","inLibrary":true},
              {"id":1,"title":"Solo Leveling","inLibrary":true}
            ]
            """)));

        var results = await service.SearchSeriesAsync("Solo Leveling");

        Assert.Equal(new[] { 1, 2, 3, 4 }, results.Select(s => s.Id));
    }

    [Fact]
    public async Task SearchSeriesAsync_ReadsLibraryNodesDefensively()
    {
        var service = CreateService(Server(library: Library("""
            [
              1,
              {"title":"No Id"},
              {"id":"x","title":"Non-numeric Id"},
              {"id":1.5,"title":"Fractional Id"},
              {"id":"7","title":"String Id","inLibrary":true},
              {"id":8},
              {"id":9,"title":"Odd Flags","inLibrary":"yes","thumbnailUrl":"http://suwayomi.example/thumb/9"}
            ]
            """)));

        var results = await service.SearchSeriesAsync(null);

        Assert.Equal(new[] { 7, 8, 9 }, results.Select(s => s.Id));
        Assert.Equal(new[] { "String Id", "Series 8", "Odd Flags" }, results.Select(s => s.Title));
        Assert.Equal(new[] { true, false, false }, results.Select(s => s.InLibrary));
        Assert.Equal(new[] { null, null, "http://suwayomi.example/thumb/9" }, results.Select(s => s.ThumbnailUrl));
    }

    [Theory]
    [InlineData("""{"data":{}}""")]
    [InlineData("""{"data":{"mangas":null}}""")]
    [InlineData("""{"data":{"mangas":{}}}""")]
    [InlineData("""{"data":{"mangas":{"nodes":{}}}}""")]
    [InlineData("""{"data":{"mangas":{"nodes":[]}}}""")]
    [InlineData("""{"data":{"mangas":{"nodes":[],"pageInfo":null}}}""")]
    [InlineData("""{"data":{"mangas":{"nodes":[],"pageInfo":{"endCursor":"abc"}}}}""")]
    [InlineData("""{"data":{"mangas":{"nodes":[],"pageInfo":{"hasNextPage":"true","endCursor":"abc"}}}}""")]
    [InlineData("""{"data":{"mangas":{"nodes":[],"pageInfo":{"hasNextPage":true}}}}""")]
    [InlineData("""{"data":{"mangas":{"nodes":[],"pageInfo":{"hasNextPage":true,"endCursor":"  "}}}}""")]
    public async Task SearchSeriesAsync_MalformedLibraryPage_Throws(string page)
    {
        var handler = Server(library: page);
        var service = CreateService(handler);

        var exception = await Assert.ThrowsAsync<SuwayomiException>(() => service.SearchSeriesAsync("Solo"));

        Assert.Equal(UnexpectedResponse, exception.Message);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("null", UnexpectedResponse)]
    [InlineData("[]", UnexpectedResponse)]
    [InlineData("""{"data":null}""", UnexpectedResponse)]
    [InlineData("""{"data":[]}""", UnexpectedResponse)]
    [InlineData("""{"data":{}}""", UnexpectedChapterListResponse)]
    [InlineData("""{"data":{"chapters":null}}""", UnexpectedChapterListResponse)]
    [InlineData("""{"data":{"chapters":[]}}""", UnexpectedChapterListResponse)]
    [InlineData("""{"data":{"chapters":"Chapter 1"}}""", UnexpectedChapterListResponse)]
    [InlineData("""{"data":{"chapters":{}}}""", UnexpectedChapterListResponse)]
    [InlineData("""{"data":{"chapters":{"nodes":null}}}""", UnexpectedChapterListResponse)]
    [InlineData("""{"data":{"chapters":{"nodes":{}}}}""", UnexpectedChapterListResponse)]
    public async Task GetChaptersAsync_MalformedChapterList_Throws(string json, string expectedMessage)
    {
        var handler = Server(chapters: json);
        var service = CreateService(handler);

        var exception = await Assert.ThrowsAsync<SuwayomiException>(() => service.GetChaptersAsync(5));

        Assert.Equal(expectedMessage, exception.Message);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(5, Variables(request.Body).GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task GetChaptersAsync_ReadsNodesDefensively()
    {
        var service = CreateService(Server(chapters: """
            {"data":{"chapters":{"nodes":[
              1,
              {"name":"No Id"},
              {"id":"10"},
              {"id":11,"name":"Eleven and a half","chapterNumber":"11.5","isDownloaded":"no"},
              {"id":12,"name":"Unparseable","chapterNumber":"twelve","isDownloaded":true},
              {"id":14,"name":"Two","chapterNumber":2,"isDownloaded":false}
            ]}}}
            """));

        var chapters = await service.GetChaptersAsync(5);

        Assert.Equal(new[] { 10, 12, 14, 11 }, chapters.Select(c => c.Id));
        Assert.Equal(new[] { "Chapter 10", "Unparseable", "Two", "Eleven and a half" }, chapters.Select(c => c.Name));
        Assert.Equal(new[] { -1, -1, 2, 11.5 }, chapters.Select(c => c.ChapterNumber));
        Assert.Equal(new[] { false, true, false, false }, chapters.Select(c => c.IsDownloaded));
    }

    [Theory]
    [InlineData(new int[0])]
    [InlineData(new[] { -1, -5 })]
    public async Task DownloadMissingIssuesAsync_NoUsableIssueNumbers_ThrowsWithoutCallingTheServer(int[] issueNumbers)
    {
        var handler = Server();
        var service = CreateService(handler);

        var exception = await Assert.ThrowsAsync<SuwayomiException>(
            () => service.DownloadMissingIssuesAsync("Solo Leveling", issueNumbers));

        Assert.Equal("No missing issue numbers were supplied.", exception.Message);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData(null, 0)]
    [InlineData("", -3)]
    public async Task DownloadMissingIssuesAsync_WithoutATitleOrPositiveId_ThrowsWithoutCallingTheServer(string? title, int? seriesId)
    {
        var handler = Server();
        var service = CreateService(handler);

        var exception = await Assert.ThrowsAsync<SuwayomiException>(
            () => service.DownloadMissingIssuesAsync(title, new[] { 1 }, seriesId));

        Assert.Equal("A series title or Suwayomi series id is required.", exception.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task DownloadMissingIssuesAsync_SeriesIdInTheLibrary_IsUsedWithoutADirectLookup()
    {
        var handler = Server(
            library: Library("""[{"id":5,"title":"Other","inLibrary":true},{"id":4,"title":"Solo Leveling","inLibrary":true}]"""),
            chapters: """{"data":{"chapters":{"nodes":[{"id":101,"name":"Chapter 1","chapterNumber":1,"isDownloaded":false}]}}}""",
            enqueue: """{"data":{"enqueueChapterDownloads":{"clientMutationId":null}}}""");
        var service = CreateService(handler);

        var result = await service.DownloadMissingIssuesAsync("A different title", new[] { 1 }, seriesId: 4);

        Assert.Equal(4, result.Series!.Id);
        Assert.Equal(new[] { 101 }, result.QueuedChapters.Select(c => c.Id));
        Assert.DoesNotContain(handler.Requests, r => r.Body.Contains("manga(", StringComparison.Ordinal));
        var enqueue = Assert.Single(handler.Requests, r => r.Body.Contains("enqueueChapterDownloads", StringComparison.Ordinal));
        Assert.Equal(new[] { 101 }, Variables(enqueue.Body).GetProperty("ids").EnumerateArray().Select(e => e.GetInt32()));
    }

    [Fact]
    public async Task DownloadMissingIssuesAsync_SeriesIdMissingFromTheListing_IsLookedUpDirectly()
    {
        var handler = Server(
            library: Library("[]"),
            manga: """{"data":{"manga":{"id":"4","title":"Solo Leveling","inLibrary":true,"thumbnailUrl":"http://suwayomi.example/thumb/4"}}}""",
            chapters: """{"data":{"chapters":{"nodes":[{"id":101,"chapterNumber":1,"isDownloaded":true}]}}}""");
        var service = CreateService(handler);

        var result = await service.DownloadMissingIssuesAsync(null, new[] { 1 }, seriesId: 4);

        Assert.NotNull(result.Series);
        Assert.Equal(4, result.Series.Id);
        Assert.Equal("Solo Leveling", result.Series.Title);
        Assert.True(result.Series.InLibrary);
        Assert.Equal("http://suwayomi.example/thumb/4", result.Series.ThumbnailUrl);
        Assert.Equal(new[] { 1 }, result.AlreadyDownloadedIssueNumbers);
        Assert.Empty(result.QueuedChapters);
        var lookup = Assert.Single(handler.Requests, r => r.Body.Contains("manga(", StringComparison.Ordinal));
        Assert.Equal(4, Variables(lookup.Body).GetProperty("id").GetInt32());
        Assert.DoesNotContain(handler.Requests, r => r.Body.Contains("enqueueChapterDownloads", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{"data":{}}""")]
    [InlineData("""{"data":{"manga":null}}""")]
    [InlineData("""{"data":{"manga":{"title":"No Id","inLibrary":true}}}""")]
    public async Task DownloadMissingIssuesAsync_DirectLookupWithoutAUsableSeries_Throws(string manga)
    {
        var service = CreateService(Server(library: Library("[]"), manga: manga));

        var exception = await Assert.ThrowsAsync<SuwayomiException>(
            () => service.DownloadMissingIssuesAsync(null, new[] { 1 }, seriesId: 4));

        Assert.Equal("Suwayomi has no series with id 4.", exception.Message);
    }

    [Fact]
    public async Task DownloadMissingIssuesFromSnapshotAsync_MalformedChapterList_ReportsFailureWithoutQueueing()
    {
        // Regression: a null "chapters" threw an InvalidOperationException, which the Missing
        // Issues check does not catch, so one series' bad answer failed the whole check.
        var handler = Server(
            library: Library("""[{"id":4,"title":"Solo Leveling","inLibrary":true}]"""),
            chapters: """{"data":{"chapters":null}}""",
            sources: """{"data":{"sources":{"nodes":[]}}}""");
        var service = CreateService(handler);
        var snapshot = await service.GetLibrarySeriesAsync();

        var result = await service.DownloadMissingIssuesFromSnapshotAsync(
            "Solo Leveling", new[] { 1 }, snapshot);

        var failure = Assert.Single(result.DiscoveryFailures);
        Assert.Equal("Library: Solo Leveling", failure.Target);
        Assert.Equal(UnexpectedChapterListResponse, failure.Error);
        Assert.Equal(4, result.Series?.Id);
        Assert.Empty(result.QueuedChapters);
        Assert.Equal(new[] { 1 }, result.UnmatchedIssueNumbers);
        Assert.DoesNotContain(handler.Requests, r => r.Body.Contains("enqueueChapterDownloads", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Requests_WhenDisabled_ThrowWithoutCallingTheServer()
    {
        var handler = Server(library: Library("[]"), chapters: """{"data":{"chapters":{"nodes":[]}}}""");
        var service = CreateService(handler, Settings(enabled: false));

        var search = await Assert.ThrowsAsync<SuwayomiException>(() => service.SearchSeriesAsync("Solo"));
        var chapters = await Assert.ThrowsAsync<SuwayomiException>(() => service.GetChaptersAsync(1));

        Assert.Equal("The Suwayomi integration is disabled.", search.Message);
        Assert.Equal("The Suwayomi integration is disabled.", chapters.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Requests_WithAnInvalidUrl_ThrowWithoutCallingTheServer()
    {
        var handler = Server(library: Library("[]"));
        var service = CreateService(handler, Settings(baseUrl: "ftp://suwayomi.example"));

        var exception = await Assert.ThrowsAsync<SuwayomiException>(() => service.SearchSeriesAsync("Solo"));

        Assert.Equal("No valid Suwayomi server URL is configured.", exception.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Requests_CancelledByTheCaller_PropagateTheCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var service = CreateService(Server(library: Library("[]")));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.SearchSeriesAsync("Solo", cancellationToken: cts.Token));

        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public async Task Requests_TimingOutWithoutCallerCancellation_AreReportedAsUnreachable()
    {
        var timeout = new TaskCanceledException("The request timed out.");
        var service = CreateService(new GraphQlHandler(_ => throw timeout));

        var exception = await Assert.ThrowsAsync<SuwayomiException>(() => service.SearchSeriesAsync("Solo"));

        Assert.Equal("Could not reach the Suwayomi server: The request timed out.", exception.Message);
        Assert.Same(timeout, exception.InnerException);
        var warning = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal("Suwayomi request failed", warning.Message);
        Assert.Same(timeout, warning.Exception);
    }

    [Fact]
    public async Task Requests_HttpErrorStatus_ThrowsWithTheStatus()
    {
        var service = CreateService(new GraphQlHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("maintenance")
        }));

        var exception = await Assert.ThrowsAsync<SuwayomiException>(() => service.SearchSeriesAsync("Solo"));

        Assert.Equal("The Suwayomi server returned HTTP 503 (Service Unavailable).", exception.Message);
        var warning = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal("Suwayomi request returned HTTP 503", warning.Message);
    }

    [Fact]
    public async Task Requests_InvalidJson_ThrowsWithTheParseError()
    {
        var service = CreateService(new GraphQlHandler(_ => Json("{ not json")));

        var exception = await Assert.ThrowsAsync<SuwayomiException>(() => service.SearchSeriesAsync("Solo"));

        Assert.Equal("The Suwayomi server returned a response that was not valid JSON.", exception.Message);
        Assert.IsAssignableFrom<JsonException>(exception.InnerException);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"data":null}""")]
    [InlineData("""{"errors":[],"data":[]}""")]
    public async Task Requests_ResponseWithoutADataObject_ThrowsUnexpectedResponse(string json)
    {
        var service = CreateService(new GraphQlHandler(_ => Json(json)));

        var exception = await Assert.ThrowsAsync<SuwayomiException>(() => service.SearchSeriesAsync("Solo"));

        Assert.Equal(UnexpectedResponse, exception.Message);
        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public async Task Requests_ErrorsWithoutMessagesOrPaths_AreReportedAsAnUnknownRejection()
    {
        var service = CreateService(new GraphQlHandler(_ => Json("""{"errors":[{"message":"  "},{"message":null,"path":[]}]}""")));

        var exception = await Assert.ThrowsAsync<SuwayomiException>(() => service.SearchSeriesAsync("Solo"));

        Assert.Equal("The Suwayomi server rejected the request: unknown error", exception.Message);
        var error = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Error, error.Level);
        Assert.StartsWith("Suwayomi rejected the Library GraphQL request before running it;", error.Message);
        Assert.EndsWith(": unknown error", error.Message);
    }

    [Fact]
    public async Task Requests_NonObjectErrors_FailTheRequestWithoutReportingARejection()
    {
        var service = CreateService(new GraphQlHandler(_ => Json("""{"errors":["oops"]}""")));

        var exception = await Assert.ThrowsAsync<SuwayomiException>(() => service.SearchSeriesAsync("Solo"));

        Assert.Equal("The Suwayomi server rejected the request: unknown error", exception.Message);
        Assert.Empty(_logger.Entries);
    }

    private sealed class GraphQlHandler : HttpMessageHandler
    {
        private readonly Func<string, HttpResponseMessage> _respond;
        private readonly List<(string Uri, string Body)> _requests = new();

        public GraphQlHandler(Func<string, HttpResponseMessage> respond) => _respond = respond;

        public IReadOnlyList<(string Uri, string Body)> Requests
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
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            lock (_requests)
            {
                _requests.Add((request.RequestUri!.AbsoluteUri, body));
            }
            return _respond(body);
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
