using System.Net;
using System.Text;
using System.Text.Json;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.ErrorReporting;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class SuwayomiServiceTests
{
    private static SuwayomiService CreateService(
        StubHttpMessageHandler handler,
        AppSettings settings,
        ILogger<SuwayomiService>? logger = null)
    {
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler));

        var optionsMonitor = new Mock<IOptionsMonitor<AppSettings>>();
        optionsMonitor.Setup(options => options.CurrentValue).Returns(settings);

        return new SuwayomiService(
            httpClientFactory.Object,
            optionsMonitor.Object,
            logger ?? Mock.Of<ILogger<SuwayomiService>>());
    }

    /// <summary>
    /// Error-level logs are what raise an automated error report, so this is
    /// how a test tells "filed as a defect" apart from "shown to the user only".
    /// </summary>
    private static void VerifyErrorLogged(Mock<ILogger<SuwayomiService>> logger, Times times, string containing = "")
    {
        logger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state != null && state.ToString()!.Contains(containing)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            times);
    }

    /// <summary>
    /// Message templates of the Error entries logged so far. An error report is
    /// fingerprinted from its template, not the rendered message, so distinct
    /// templates are what make failures file as distinct issues.
    /// </summary>
    private static List<string?> ErrorTemplates(Mock<ILogger<SuwayomiService>> logger)
        => logger.Invocations
            .Where(invocation => invocation.Method.Name == nameof(ILogger.Log)
                && invocation.Arguments[0] is LogLevel.Error)
            .Select(invocation => (invocation.Arguments[2] as IEnumerable<KeyValuePair<string, object?>>)?
                .FirstOrDefault(property => property.Key == "{OriginalFormat}").Value as string)
            .ToList();

    private static AppSettings EnabledSettings() => new()
    {
        EnableSuwayomi = true,
        SuwayomiBaseUrl = "http://suwayomi.example:4567"
    };

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    [Fact]
    public async Task GetStatusAsync_ReportsDisabledWithoutCallingTheServer()
    {
        var called = false;
        var handler = new StubHttpMessageHandler(_ =>
        {
            called = true;
            return Json("""{"data":{"aboutServer":{"name":"Suwayomi","version":"1.0"}}}""");
        });

        var service = CreateService(handler, new AppSettings { EnableSuwayomi = false });

        var status = await service.GetStatusAsync();

        Assert.False(status.Enabled);
        Assert.False(status.Connected);
        Assert.False(called);
    }

    [Fact]
    public async Task GetStatusAsync_PostsToGraphQlEndpointAndReportsServerDetails()
    {
        Uri? requestUri = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            requestUri = request.RequestUri;
            return Json("""{"data":{"aboutServer":{"name":"Suwayomi-Server","version":"1.1.0"}}}""");
        });

        var service = CreateService(handler, EnabledSettings());

        var status = await service.GetStatusAsync();

        Assert.True(status.Connected);
        Assert.Equal("Suwayomi-Server", status.ServerName);
        Assert.Equal("1.1.0", status.Version);
        Assert.Equal("http://suwayomi.example:4567/api/graphql", requestUri?.ToString());
    }

    [Fact]
    public async Task GetStatusAsync_ReportsGraphQlErrorsInsteadOfThrowing()
    {
        var handler = new StubHttpMessageHandler(_ =>
            Json("""{"errors":[{"message":"Unauthorized"}]}"""));

        var service = CreateService(handler, EnabledSettings());

        var status = await service.GetStatusAsync();

        Assert.True(status.Enabled);
        Assert.False(status.Connected);
        Assert.Contains("Unauthorized", status.Error);
    }

    [Fact]
    public async Task GetStatusAsync_ReportsUnexpectedResponseShapeInsteadOfThrowing()
    {
        var handler = new StubHttpMessageHandler(_ => Json("""{"data":{}}"""));

        var service = CreateService(handler, EnabledSettings());

        var status = await service.GetStatusAsync();

        Assert.False(status.Connected);
        Assert.Contains("unexpected response", status.Error);
    }

    [Fact]
    public async Task SearchSeriesAsync_RanksExactMatchesFirstAndIgnoresPunctuation()
    {
        var handler = new StubHttpMessageHandler(_ => Json("""
            {"data":{"mangas":{"nodes":[
                {"id":7,"title":"Another Series","inLibrary":true},
                {"id":3,"title":"Solo Leveling: Ragnarok","inLibrary":true},
                {"id":1,"title":"Solo Leveling!","inLibrary":true}
            ],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}
            """));

        var service = CreateService(handler, EnabledSettings());

        var results = await service.SearchSeriesAsync("Solo Leveling");

        Assert.Equal(2, results.Count);
        Assert.Equal(1, results[0].Id);
        Assert.Equal(3, results[1].Id);
    }

    [Fact]
    public async Task SearchSeriesAsync_FollowsLibraryPaginationCursors()
    {
        var afterValues = new List<string?>();
        var handler = new StubHttpMessageHandler(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var document = JsonDocument.Parse(body);
            var after = document.RootElement.GetProperty("variables").GetProperty("after");
            afterValues.Add(after.ValueKind == JsonValueKind.String ? after.GetString() : null);

            return afterValues.Count == 1
                ? Json("""{"data":{"mangas":{"nodes":[{"id":1,"title":"First","inLibrary":true}],"pageInfo":{"hasNextPage":true,"endCursor":"next-page"}}}}""")
                : Json("""{"data":{"mangas":{"nodes":[{"id":2,"title":"Later Match","inLibrary":true}],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}""");
        });

        var service = CreateService(handler, EnabledSettings());

        var results = await service.SearchSeriesAsync("Later Match");

        Assert.Single(results);
        Assert.Equal(2, results[0].Id);
        Assert.Equal(new string?[] { null, "next-page" }, afterValues);
    }

    [Fact]
    public async Task SearchSeriesAsync_DeclaresTheLibraryCursorAsSuwayomisCursorScalar()
    {
        // Suwayomi types `mangas(after:)` with its own nullable `Cursor` scalar.
        // A `String` variable there fails validation before the query runs, and a
        // non-null one would reject the first page, which has no cursor yet.
        string? query = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var document = JsonDocument.Parse(body);
            query = document.RootElement.GetProperty("query").GetString();
            return Json("""{"data":{"mangas":{"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}""");
        });

        var service = CreateService(handler, EnabledSettings());

        await service.SearchSeriesAsync("Solo Leveling");

        Assert.NotNull(query);
        Assert.Matches(@"\$after\s*:\s*Cursor\s*[,)]", query);
        Assert.Contains("after: $after", query);
    }

    [Fact]
    public async Task DownloadMissingIssuesAsync_LogsSchemaRejectionsAsErrorsSoTheyAreReported()
    {
        // What Suwayomi returns when validation fails: no data, and an error
        // without a `path` because nothing was executed.
        var handler = new StubHttpMessageHandler(_ => Json("""
            {"errors":[{"message":"Validation error (VariableTypeMismatch@[mangas]) : Variable 'after' of type 'String' used in position expecting type 'Cursor'","locations":[{"line":1,"column":69}],"extensions":{}}]}
            """));
        var logger = new Mock<ILogger<SuwayomiService>>();

        var service = CreateService(handler, EnabledSettings(), logger.Object);

        var exception = await Assert.ThrowsAsync<SuwayomiException>(
            () => service.DownloadMissingIssuesAsync("Solo Leveling", new[] { 1 }));

        Assert.Contains("VariableTypeMismatch", exception.Message);
        VerifyErrorLogged(logger, Times.Once(), containing: "VariableTypeMismatch");
        VerifyErrorLogged(logger, Times.Once(), containing: "rejected the Library GraphQL request");
    }

    [Fact]
    public async Task SearchSeriesAsync_ReportsEveryErrorInASchemaRejection()
    {
        // Validation lists every mismatch in a document at once. An issue that
        // showed only the first would invite a partial fix, after which the rest
        // would merely bump that closed issue instead of being seen.
        var handler = new StubHttpMessageHandler(_ => Json("""
            {"errors":[
              {"message":"Validation error (VariableTypeMismatch@[mangas]) : Variable 'after' of type 'String' used in position expecting type 'Cursor'","locations":[{"line":1,"column":69}]},
              {"message":"Validation error (FieldUndefined@[mangas/nodes/thumbnailUrl]) : Field 'thumbnailUrl' in type 'MangaType' is undefined","locations":[{"line":3,"column":35}]}
            ]}
            """));
        var logger = new Mock<ILogger<SuwayomiService>>();

        var service = CreateService(handler, EnabledSettings(), logger.Object);

        var exception = await Assert.ThrowsAsync<SuwayomiException>(
            () => service.SearchSeriesAsync("Solo Leveling"));

        Assert.Contains("VariableTypeMismatch", exception.Message);
        VerifyErrorLogged(logger, Times.Once(), containing: "VariableTypeMismatch");
        VerifyErrorLogged(logger, Times.Once(), containing: "FieldUndefined");
    }

    [Fact]
    public async Task SchemaRejectionsOfDifferentDocumentsAreFiledAsSeparateIssues()
    {
        // With one template for every rejection, a later mismatch in another
        // document would share the first report's fingerprint and only bump that
        // issue instead of opening its own. Repeats of one document must still
        // share a fingerprint so they count as recurrences, not new issues.
        var handler = new StubHttpMessageHandler(_ => Json("""
            {"errors":[{"message":"Validation error (FieldUndefined@[chapters/nodes/isDownloaded]) : Field 'isDownloaded' is undefined","locations":[{"line":3,"column":40}]}]}
            """));
        var logger = new Mock<ILogger<SuwayomiService>>();

        var service = CreateService(handler, EnabledSettings(), logger.Object);

        await Assert.ThrowsAsync<SuwayomiException>(() => service.SearchSeriesAsync("Solo Leveling"));
        await Assert.ThrowsAsync<SuwayomiException>(() => service.GetChaptersAsync(1));
        await Assert.ThrowsAsync<SuwayomiException>(() => service.SearchSeriesAsync("Solo Leveling"));

        var fingerprints = ErrorTemplates(logger)
            .Select(template => ErrorReportFingerprint.Compute(null, null, template, typeof(SuwayomiService).FullName))
            .ToList();

        Assert.Equal(3, fingerprints.Count);
        Assert.NotEqual(fingerprints[0], fingerprints[1]);
        Assert.Equal(fingerprints[0], fingerprints[2]);
    }

    [Fact]
    public async Task GetStatusAsync_StillReportsSchemaRejectionsAlthoughItDoesNotThrow()
    {
        var handler = new StubHttpMessageHandler(_ => Json("""
            {"errors":[{"message":"Validation error (FieldUndefined@[aboutServer/name]) : Field 'name' in type 'AboutServerPayload' is undefined","locations":[{"line":1,"column":23}]}]}
            """));
        var logger = new Mock<ILogger<SuwayomiService>>();

        var service = CreateService(handler, EnabledSettings(), logger.Object);

        var status = await service.GetStatusAsync();

        Assert.False(status.Connected);
        Assert.Contains("FieldUndefined", status.Error);
        VerifyErrorLogged(logger, Times.Once(), containing: "FieldUndefined");
        VerifyErrorLogged(logger, Times.Once(), containing: "rejected the AboutServer GraphQL request");
    }

    [Fact]
    public async Task DownloadMissingIssuesAsync_DoesNotReportFieldErrorsSuchAsUnauthorized()
    {
        // Suwayomi raises resolver failures, its auth rejections included, as
        // field errors that carry a `path`. Those are for the user to fix on
        // their server, not defects to file against this project.
        var handler = new StubHttpMessageHandler(_ => Json("""
            {"errors":[{"message":"Exception while fetching data (/mangas) : Unauthorized","locations":[{"line":2,"column":3}],"path":["mangas"]}]}
            """));
        var logger = new Mock<ILogger<SuwayomiService>>();

        var service = CreateService(handler, EnabledSettings(), logger.Object);

        var exception = await Assert.ThrowsAsync<SuwayomiException>(
            () => service.DownloadMissingIssuesAsync("Solo Leveling", new[] { 1 }));

        Assert.Contains("Unauthorized", exception.Message);
        VerifyErrorLogged(logger, Times.Never());
    }

    [Fact]
    public async Task GetChaptersAsync_ParsesChaptersInNumberOrder()
    {
        var handler = new StubHttpMessageHandler(_ => Json("""
            {"data":{"chapters":{"nodes":[
                {"id":20,"name":"Chapter 2","chapterNumber":2,"isDownloaded":false},
                {"id":10,"name":"Chapter 1","chapterNumber":1,"isDownloaded":true}
            ]}}}
            """));

        var service = CreateService(handler, EnabledSettings());

        var chapters = await service.GetChaptersAsync(5);

        Assert.Collection(
            chapters,
            first =>
            {
                Assert.Equal(10, first.Id);
                Assert.Equal(1, first.ChapterNumber);
                Assert.True(first.IsDownloaded);
            },
            second =>
            {
                Assert.Equal(20, second.Id);
                Assert.False(second.IsDownloaded);
            });
    }

    [Fact]
    public async Task DownloadMissingIssuesAsync_QueuesOnlyPendingChaptersForRequestedIssues()
    {
        var payloads = new List<string>();
        var handler = new StubHttpMessageHandler(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            payloads.Add(body);

            if (body.Contains("mangas("))
            {
                return Json("""{"data":{"mangas":{"nodes":[{"id":4,"title":"Solo Leveling","inLibrary":true}],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}""");
            }

            if (body.Contains("chapters("))
            {
                return Json("""
                    {"data":{"chapters":{"nodes":[
                        {"id":41,"name":"Chapter 1","chapterNumber":1,"isDownloaded":true},
                        {"id":42,"name":"Chapter 2","chapterNumber":2,"isDownloaded":false},
                        {"id":43,"name":"Chapter 3.5","chapterNumber":3.5,"isDownloaded":false}
                    ]}}}
                    """);
            }

            return Json("""{"data":{"enqueueChapterDownloads":{"clientMutationId":null}}}""");
        });

        var service = CreateService(handler, EnabledSettings());

        var result = await service.DownloadMissingIssuesAsync("Solo Leveling", new[] { 1, 2, 3, 9 });

        Assert.Equal(4, result.Series!.Id);
        Assert.Equal(new[] { 42, 43 }, result.QueuedChapters.Select(c => c.Id).ToArray());
        Assert.Equal(new[] { 1 }, result.AlreadyDownloadedIssueNumbers.ToArray());
        Assert.Equal(new[] { 9 }, result.UnmatchedIssueNumbers.ToArray());

        var mutation = payloads.Last();
        using var document = JsonDocument.Parse(mutation);
        var ids = document.RootElement
            .GetProperty("variables")
            .GetProperty("ids")
            .EnumerateArray()
            .Select(e => e.GetInt32())
            .ToArray();
        Assert.Equal(new[] { 42, 43 }, ids);
    }

    [Fact]
    public async Task DownloadMissingIssuesAsync_DoesNotCallTheMutationWhenNothingMatches()
    {
        var requestCount = 0;
        var handler = new StubHttpMessageHandler(request =>
        {
            requestCount++;
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            if (body.Contains("mangas("))
            {
                return Json("""{"data":{"mangas":{"nodes":[{"id":4,"title":"Solo Leveling","inLibrary":true}],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}""");
            }

            if (body.Contains("chapters("))
            {
                return Json("""{"data":{"chapters":{"nodes":[]}}}""");
            }

            throw new InvalidOperationException("The download mutation should not be sent.");
        });

        var service = CreateService(handler, EnabledSettings());

        var result = await service.DownloadMissingIssuesAsync("Solo Leveling", new[] { 1 });

        Assert.Empty(result.QueuedChapters);
        Assert.Equal(new[] { 1 }, result.UnmatchedIssueNumbers.ToArray());
        Assert.Equal(2, requestCount);
    }

    [Fact]
    public async Task DownloadMissingIssuesAsync_ThrowsWhenTheSeriesIsNotInTheSuwayomiLibrary()
    {
        var handler = new StubHttpMessageHandler(_ =>
            Json("""{"data":{"mangas":{"nodes":[{"id":4,"title":"Something Else","inLibrary":true}],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}"""));

        var service = CreateService(handler, EnabledSettings());

        var exception = await Assert.ThrowsAsync<SuwayomiException>(
            () => service.DownloadMissingIssuesAsync("Solo Leveling", new[] { 1 }));
        Assert.Contains("Solo Leveling", exception.Message);
    }

    [Fact]
    public async Task DownloadMissingIssuesAsync_RejectsDirectIdOutsideLibrary()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return body.Contains("mangas(")
                ? Json("""{"data":{"mangas":{"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}""")
                : Json("""{"data":{"manga":{"id":4,"title":"Solo Leveling","inLibrary":false}}}""");
        });

        var service = CreateService(handler, EnabledSettings());

        var exception = await Assert.ThrowsAsync<SuwayomiException>(
            () => service.DownloadMissingIssuesAsync(null, new[] { 1 }, seriesId: 4));

        Assert.Contains("not in the library", exception.Message);
    }

    [Fact]
    public async Task DownloadMissingIssuesAsync_SendsBasicAuthWhenCredentialsAreConfigured()
    {
        string? authorization = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            authorization = request.Headers.Authorization?.ToString();
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            if (body.Contains("mangas("))
            {
                return Json("""{"data":{"mangas":{"nodes":[{"id":4,"title":"Solo Leveling","inLibrary":true}],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}""");
            }

            return Json("""{"data":{"chapters":{"nodes":[]}}}""");
        });

        var settings = EnabledSettings();
        settings.SuwayomiUsername = "reader";
        settings.SuwayomiPassword = "hunter2";
        var service = CreateService(handler, settings);

        await service.DownloadMissingIssuesAsync("Solo Leveling", new[] { 1 });

        var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes("reader:hunter2"));
        Assert.Equal($"Basic {expected}", authorization);
    }

    [Fact]
    public async Task DownloadMissingIssuesAsync_ThrowsWhenTheServerIsUnreachable()
    {
        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("connection refused"));

        var service = CreateService(handler, EnabledSettings());

        var exception = await Assert.ThrowsAsync<SuwayomiException>(
            () => service.DownloadMissingIssuesAsync("Solo Leveling", new[] { 1 }));
        Assert.Contains("Could not reach", exception.Message);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("not a url", false)]
    [InlineData("ftp://example.com", false)]
    [InlineData("http://localhost:4567", true)]
    [InlineData("https://suwayomi.example/", true)]
    public void TryBuildEndpoint_AcceptsOnlyAbsoluteHttpUrls(string? baseUrl, bool expected)
    {
        Assert.Equal(expected, SuwayomiService.TryBuildEndpoint(baseUrl, out _));
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }
}
