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

public class SuwayomiServiceTests
{
    private static SuwayomiService CreateService(StubHttpMessageHandler handler, AppSettings settings)
    {
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler));

        var optionsMonitor = new Mock<IOptionsMonitor<AppSettings>>();
        optionsMonitor.Setup(options => options.CurrentValue).Returns(settings);

        return new SuwayomiService(
            httpClientFactory.Object,
            optionsMonitor.Object,
            Mock.Of<ILogger<SuwayomiService>>());
    }

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
    public async Task SearchSeriesAsync_RanksExactMatchesFirstAndIgnoresPunctuation()
    {
        var handler = new StubHttpMessageHandler(_ => Json("""
            {"data":{"mangas":{"nodes":[
                {"id":7,"title":"Another Series","inLibrary":true},
                {"id":3,"title":"Solo Leveling: Ragnarok","inLibrary":true},
                {"id":1,"title":"Solo Leveling!","inLibrary":true}
            ]}}}
            """));

        var service = CreateService(handler, EnabledSettings());

        var results = await service.SearchSeriesAsync("Solo Leveling");

        Assert.Equal(2, results.Count);
        Assert.Equal(1, results[0].Id);
        Assert.Equal(3, results[1].Id);
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
                return Json("""{"data":{"mangas":{"nodes":[{"id":4,"title":"Solo Leveling","inLibrary":true}]}}}""");
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
                return Json("""{"data":{"mangas":{"nodes":[{"id":4,"title":"Solo Leveling","inLibrary":true}]}}}""");
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
            Json("""{"data":{"mangas":{"nodes":[{"id":4,"title":"Something Else","inLibrary":true}]}}}"""));

        var service = CreateService(handler, EnabledSettings());

        var exception = await Assert.ThrowsAsync<SuwayomiException>(
            () => service.DownloadMissingIssuesAsync("Solo Leveling", new[] { 1 }));
        Assert.Contains("Solo Leveling", exception.Message);
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
                return Json("""{"data":{"mangas":{"nodes":[{"id":4,"title":"Solo Leveling","inLibrary":true}]}}}""");
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
