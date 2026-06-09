using System.Net;
using System.Text;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class SuwayomiDownloadServiceTests
{
    private const string LibraryResponse = """
        {
          "data": {
            "mangas": {
              "nodes": [
                { "id": 7, "title": "Attack on Titan", "sourceId": 42, "source": { "displayName": "MangaSource" } },
                { "id": 9, "title": "Some Other Series", "sourceId": 43, "source": { "displayName": "OtherSource" } }
              ]
            }
          }
        }
        """;

    private const string ChaptersResponse = """
        {
          "data": {
            "chapters": {
              "nodes": [
                { "id": 100, "name": "Chapter 1", "chapterNumber": 1.0, "isDownloaded": true },
                { "id": 103, "name": "Chapter 3", "chapterNumber": 3.0, "isDownloaded": false },
                { "id": 104, "name": "Chapter 4", "chapterNumber": 4.0, "isDownloaded": false }
              ]
            }
          }
        }
        """;

    private const string EnqueueResponse = """
        { "data": { "enqueueChapterDownloads": { "clientMutationId": null } } }
        """;

    private static SuwayomiDownloadService CreateService(StubHttpMessageHandler handler, AppSettings settings)
    {
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler));

        var optionsMonitor = new Mock<IOptionsMonitor<AppSettings>>();
        optionsMonitor.Setup(o => o.CurrentValue).Returns(settings);

        return new SuwayomiDownloadService(
            httpClientFactory.Object,
            optionsMonitor.Object,
            Mock.Of<ILogger<SuwayomiDownloadService>>());
    }

    private static AppSettings EnabledSettings() => new()
    {
        EnableSuwayomiDownloads = true,
        SuwayomiBaseUrl = "http://suwayomi.example:4567"
    };

    [Fact]
    public async Task DownloadIssuesAsync_Disabled_ReturnsFailure()
    {
        var handler = new StubHttpMessageHandler(_ => Ok("{}"));
        var service = CreateService(handler, new AppSettings { EnableSuwayomiDownloads = false });

        var result = await service.DownloadIssuesAsync(new[] { "Attack on Titan" }, new[] { "3" });

        Assert.False(result.Success);
        Assert.Contains("disabled", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DownloadIssuesAsync_NoMatchingSeries_ReturnsFailure()
    {
        var handler = new StubHttpMessageHandler(RouteByBody);
        var service = CreateService(handler, EnabledSettings());

        var result = await service.DownloadIssuesAsync(new[] { "Totally Unrelated Title" }, new[] { "3" });

        Assert.False(result.Success);
        Assert.Contains("No matching series", result.Message);
    }

    [Fact]
    public async Task DownloadIssuesAsync_MatchesSeriesAndEnqueuesMissingChapter()
    {
        string? enqueueBody = null;
        var handler = new StubHttpMessageHandler(req =>
        {
            var body = req.Content!.ReadAsStringAsync().Result;
            if (body.Contains("enqueueChapterDownloads"))
            {
                enqueueBody = body;
            }
            return RouteByBody(req);
        });
        var service = CreateService(handler, EnabledSettings());

        var result = await service.DownloadIssuesAsync(new[] { "Attack on Titan" }, new[] { "3" });

        Assert.True(result.Success);
        Assert.NotNull(result.Match);
        Assert.Equal("Attack on Titan", result.Match!.Title);
        Assert.Equal("MangaSource", result.Match.SourceName);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("3", issue.Issue);
        Assert.True(issue.Enqueued);
        // The mutation must reference chapter id 103 (chapterNumber 3.0).
        Assert.NotNull(enqueueBody);
        Assert.Contains("103", enqueueBody!);
    }

    [Fact]
    public async Task DownloadIssuesAsync_AlreadyDownloadedChapter_IsNotEnqueued()
    {
        var handler = new StubHttpMessageHandler(RouteByBody);
        var service = CreateService(handler, EnabledSettings());

        // Issue #1 maps to chapter 100 which is already downloaded.
        var result = await service.DownloadIssuesAsync(new[] { "Attack on Titan" }, new[] { "1" });

        var issue = Assert.Single(result.Issues);
        Assert.False(issue.Enqueued);
        Assert.Contains("Already downloaded", issue.Status);
    }

    [Fact]
    public async Task DownloadIssuesAsync_ChapterNotAvailable_ReportsUnavailable()
    {
        var handler = new StubHttpMessageHandler(RouteByBody);
        var service = CreateService(handler, EnabledSettings());

        // Issue #99 does not exist in the chapter list.
        var result = await service.DownloadIssuesAsync(new[] { "Attack on Titan" }, new[] { "99" });

        var issue = Assert.Single(result.Issues);
        Assert.False(issue.Enqueued);
        Assert.Contains("Not available", issue.Status);
    }

    [Fact]
    public async Task DownloadIssuesAsync_MixedIssues_EnqueuesOnlyMissingAvailableChapters()
    {
        string? enqueueBody = null;
        var handler = new StubHttpMessageHandler(req =>
        {
            var body = req.Content!.ReadAsStringAsync().Result;
            if (body.Contains("enqueueChapterDownloads"))
            {
                enqueueBody = body;
            }
            return RouteByBody(req);
        });
        var service = CreateService(handler, EnabledSettings());

        var result = await service.DownloadIssuesAsync(new[] { "Attack on Titan" }, new[] { "1", "3", "4", "99" });

        Assert.True(result.Success);
        Assert.Equal(4, result.Issues.Count);
        Assert.Equal(2, result.Issues.Count(i => i.Enqueued)); // #3 and #4
        Assert.NotNull(enqueueBody);
        Assert.Contains("103", enqueueBody!);
        Assert.Contains("104", enqueueBody!);
        Assert.DoesNotContain("100", enqueueBody!); // #1 already downloaded
    }

    [Fact]
    public async Task GetAvailabilityAsync_Disabled_ReportsDisabled()
    {
        var handler = new StubHttpMessageHandler(_ => Ok("{}"));
        var service = CreateService(handler, new AppSettings { EnableSuwayomiDownloads = false });

        var availability = await service.GetAvailabilityAsync();

        Assert.False(availability.Enabled);
    }

    [Fact]
    public async Task GetAvailabilityAsync_EnabledAndConfigured_ReportsConfigured()
    {
        var handler = new StubHttpMessageHandler(_ => Ok("{}"));
        var service = CreateService(handler, EnabledSettings());

        var availability = await service.GetAvailabilityAsync();

        Assert.True(availability.Enabled);
        Assert.True(availability.Configured);
    }

    [Fact]
    public async Task GetAvailabilityAsync_InvalidBaseUrl_ReportsNotConfigured()
    {
        var handler = new StubHttpMessageHandler(_ => Ok("{}"));
        var service = CreateService(handler, new AppSettings
        {
            EnableSuwayomiDownloads = true,
            SuwayomiBaseUrl = "not-a-url"
        });

        var availability = await service.GetAvailabilityAsync();

        Assert.True(availability.Enabled);
        Assert.False(availability.Configured);
    }

    private static HttpResponseMessage RouteByBody(HttpRequestMessage request)
    {
        var body = request.Content!.ReadAsStringAsync().Result;
        if (body.Contains("enqueueChapterDownloads"))
        {
            return Ok(EnqueueResponse);
        }
        if (body.Contains("chapters"))
        {
            return Ok(ChaptersResponse);
        }
        if (body.Contains("mangas"))
        {
            return Ok(LibraryResponse);
        }
        return Ok("{\"data\":{}}");
    }

    private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

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
