using System.Net;
using System.Text;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class ComicVineSeriesMetadataServiceTests
{
    [Fact]
    public async Task LookupSeriesAsync_ReturnsCanonicalTitleAndAliases()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
            {
              "results": [
                {
                  "name": "Batman",
                  "aliases": "The Dark Knight\nBatman (1940)"
                }
              ]
            }
            """, Encoding.UTF8, "application/json")
        });

        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, disposeHandler: false));

        var optionsMonitor = new Mock<IOptionsMonitor<AppSettings>>();
        optionsMonitor.Setup(options => options.CurrentValue).Returns(new AppSettings
        {
            EnableExternalSeriesMetadata = true,
            ComicVineApiKey = "test-key",
            ComicVineBaseUrl = "https://comicvine.example/api"
        });

        var service = new ComicVineSeriesMetadataService(
            httpClientFactory.Object,
            optionsMonitor.Object,
            new MemoryCache(new MemoryCacheOptions()),
            Mock.Of<ILogger<ComicVineSeriesMetadataService>>());

        var result = await service.LookupSeriesAsync("The Dark Knight");

        Assert.NotNull(result);
        Assert.Equal("Batman", result!.CanonicalTitle);
        Assert.Contains("The Dark Knight", result.Aliases);
        Assert.Equal("ComicVine", result.Source);
    }

    [Fact]
    public async Task LookupSeriesAsync_PopulatesSynopsisPreferringDeck()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
            {
              "results": [
                {
                  "name": "Batman",
                  "deck": "The Dark Knight protects Gotham.",
                  "description": "<p>A much longer <i>HTML</i> description.</p>"
                }
              ]
            }
            """, Encoding.UTF8, "application/json")
        });

        var service = CreateService(handler, new AppSettings
        {
            EnableExternalSeriesMetadata = true,
            ComicVineApiKey = "test-key",
            ComicVineBaseUrl = "https://comicvine.example/api"
        });

        var result = await service.LookupSeriesAsync("Batman");

        Assert.NotNull(result);
        Assert.Equal("The Dark Knight protects Gotham.", result!.Synopsis);
    }

    [Fact]
    public async Task CheckHealthAsync_DisabledProvider_ReportsDisabled()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var service = CreateService(handler, new AppSettings
        {
            EnableExternalSeriesMetadata = false,
            ComicVineApiKey = "test-key",
            ComicVineBaseUrl = "https://comicvine.example/api"
        });

        var health = await service.CheckHealthAsync();

        Assert.Equal("ComicVine", health.Name);
        Assert.False(health.Enabled);
        Assert.False(health.Configured);
        Assert.Null(health.Reachable);
        Assert.Equal("Disabled in settings", health.StatusMessage);
    }

    [Fact]
    public async Task CheckHealthAsync_MissingApiKey_ReportsNotConfigured()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var service = CreateService(handler, new AppSettings
        {
            EnableExternalSeriesMetadata = true,
            ComicVineApiKey = null,
            ComicVineBaseUrl = "https://comicvine.example/api"
        });

        var health = await service.CheckHealthAsync();

        Assert.True(health.Enabled);
        Assert.False(health.Configured);
        Assert.Null(health.Reachable);
    }

    [Fact]
    public async Task CheckHealthAsync_HttpError_ReportsUnreachable()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { ReasonPhrase = "Unauthorized" });
        var service = CreateService(handler, new AppSettings
        {
            EnableExternalSeriesMetadata = true,
            ComicVineApiKey = "test-key",
            ComicVineBaseUrl = "https://comicvine.example/api"
        });

        var health = await service.CheckHealthAsync();

        Assert.True(health.Enabled);
        Assert.True(health.Configured);
        Assert.False(health.Reachable);
        Assert.Contains("401", health.StatusMessage);
    }

    [Fact]
    public async Task CheckHealthAsync_Reachable_ReportsHealthy()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"results\":[]}", Encoding.UTF8, "application/json")
        });
        var service = CreateService(handler, new AppSettings
        {
            EnableExternalSeriesMetadata = true,
            ComicVineApiKey = "test-key",
            ComicVineBaseUrl = "https://comicvine.example/api"
        });

        var health = await service.CheckHealthAsync();

        Assert.True(health.Enabled);
        Assert.True(health.Configured);
        Assert.True(health.Reachable);
        Assert.Equal("Reachable", health.StatusMessage);
    }

    [Fact]
    public async Task LookupSeriesAsync_RecordsFailureCounters_OnHttpError()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var service = CreateService(handler, new AppSettings
        {
            EnableExternalSeriesMetadata = true,
            ComicVineApiKey = "test-key",
            ComicVineBaseUrl = "https://comicvine.example/api"
        });

        await Assert.ThrowsAsync<ExternalMetadataUnavailableException>(() => service.LookupSeriesAsync("Anything"));
        var health = await service.CheckHealthAsync();

        Assert.True(health.FailureCount >= 1);
        Assert.NotNull(health.LastFailureUtc);
    }

    [Fact]
    public async Task LookupSeriesAsync_ParsesStartYearAndPublisher()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse("""
            {"status_code":1,"results":[
              {"id":150172,"name":"Green Lantern","start_year":"2023","publisher":{"id":10,"name":"DC Comics"}}
            ]}
            """));
        var service = CreateService(handler, EnabledSettings());

        var result = await service.LookupSeriesAsync("Green Lantern");

        Assert.NotNull(result);
        Assert.Equal(2023, result!.StartYear);
        Assert.Equal("DC Comics", result.Publisher);
    }

    [Theory]
    [InlineData("\"start_year\":2023", 2023)]
    [InlineData("\"start_year\":\" 1960 \"", 1960)]
    [InlineData("\"start_year\":null", null)]
    [InlineData("\"start_year\":\"unknown\"", null)]
    [InlineData("\"start_year\":\"0\"", null)]
    public async Task SearchSeriesAsync_ReadsStartYearLeniently(string field, int? expected)
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse(
            $$"""{"status_code":1,"results":[{"id":1,"name":"Green Lantern",{{field}},"publisher":null}]}"""));
        var service = CreateService(handler, EnabledSettings());

        var result = Assert.Single(await service.SearchSeriesAsync("Green Lantern"));

        Assert.Equal(expected, result.StartYear);
        Assert.Null(result.Publisher);
    }

    [Fact]
    public async Task LookupSeriesAsync_NameWithYear_PagesPastSameNamedVolumesToTheOneThatStartedThatYear()
    {
        // ComicVine ranks same-named volumes without regard to year: the first
        // "Green Lantern" is the 1960 run and the 2023 one is much further down.
        var requests = new List<Uri>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(request.RequestUri!);
            var page2 = request.RequestUri!.Query.Contains("&page=2", StringComparison.Ordinal);
            var volumes = page2
                ? new[] { (4363, 1990), (150172, 2023), (135228, 2021) }
                : Enumerable.Range(0, 50).Select(i => (2839 + i, 1960 + (i % 40))).ToArray();
            return JsonResponse(VolumesJson(volumes));
        });
        var service = CreateService(handler, EnabledSettings());

        var result = await service.LookupSeriesAsync("Green Lantern (2023)");

        Assert.NotNull(result);
        Assert.Equal("150172", result!.SourceId);
        Assert.Equal(2023, result.StartYear);
        Assert.Equal(2, requests.Count);
        // The year is left out of the search, which reads pages of 50.
        Assert.All(requests, uri =>
        {
            Assert.Contains("query=Green%20Lantern", uri.Query, StringComparison.Ordinal);
            Assert.DoesNotContain("2023", uri.Query, StringComparison.Ordinal);
            Assert.Contains("limit=50", uri.Query, StringComparison.Ordinal);
            Assert.Contains("start_year", uri.Query, StringComparison.Ordinal);
        });
        Assert.DoesNotContain("page=", requests[0].Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LookupSeriesAsync_NameWithYear_StopsAtTheFirstPageThatConfirmsTheYear()
    {
        var requests = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            requests++;
            return JsonResponse(VolumesJson(Enumerable.Range(0, 50).Select(i => (100 + i, i == 16 ? 2023 : 1960)).ToArray()));
        });
        var service = CreateService(handler, EnabledSettings());

        var result = await service.LookupSeriesAsync("Green Lantern (2023)");

        Assert.Equal("116", result?.SourceId);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task LookupSeriesAsync_NameWithYear_NoVolumeOfThatYear_IsNoMatch()
    {
        // Matching "Green Lantern (2023)" to the 1960 run is worse than no
        // match: it is written into every file of the folder.
        var requests = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            requests++;
            return JsonResponse(VolumesJson(new[] { (2839, 1960), (4363, 1990) }));
        });
        var service = CreateService(handler, EnabledSettings());

        Assert.Null(await service.LookupSeriesAsync("Green Lantern (2023)"));
        // A short page means the search ran out; no second page is asked for.
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task SearchSeriesAsync_QueryWithYear_PutsTheVolumeOfThatYearFirst()
    {
        Uri? requestUri = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            requestUri = request.RequestUri;
            return JsonResponse(VolumesJson(Enumerable.Range(0, 30).Select(i => (100 + i, i == 20 ? 2023 : 1960 + i)).ToArray()));
        });
        var service = CreateService(handler, EnabledSettings());

        var results = await service.SearchSeriesAsync("Green Lantern (2023)", limit: 10);

        Assert.Equal(10, results.Count);
        Assert.Equal("120", results[0].SourceId);
        Assert.Contains("limit=50", requestUri!.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("2023", requestUri.Query, StringComparison.Ordinal);
    }

    private static AppSettings EnabledSettings() => new()
    {
        EnableExternalSeriesMetadata = true,
        ComicVineApiKey = "test-key",
        ComicVineBaseUrl = "https://comicvine.example/api"
    };

    private static string VolumesJson(IEnumerable<(int Id, int StartYear)> volumes)
        => "{\"status_code\":1,\"results\":["
           + string.Join(",", volumes.Select(v =>
               $"{{\"id\":{v.Id},\"name\":\"Green Lantern\",\"start_year\":\"{v.StartYear}\",\"publisher\":{{\"name\":\"DC Comics\"}}}}"))
           + "]}";

    [Fact]
    public async Task SearchVolumesAsync_ReadsPagesUntilAShortOne()
    {
        var requests = new List<string>();
        static string Page(int count, int firstYear)
            => "{\"status_code\":1,\"results\":[" + string.Join(",", Enumerable.Range(0, count)
                .Select(i => $"{{\"name\":\"Batman\",\"start_year\":\"{firstYear + i}\",\"publisher\":{{\"name\":\"DC Comics\"}}}}")) + "]}";
        var handler = new StubHttpMessageHandler(request =>
        {
            var uri = request.RequestUri!.ToString();
            requests.Add(uri);
            return JsonResponse(uri.Contains("&page=2") ? Page(3, 1900) : Page(50, 1940));
        });
        var service = CreateService(handler, new AppSettings
        {
            EnableExternalSeriesMetadata = true,
            ComicVineApiKey = "test-key",
            ComicVineBaseUrl = "https://comicvine.example/api"
        });

        var volumes = await service.SearchVolumesAsync(" Batman ");

        Assert.Equal(53, volumes.Count);
        Assert.Equal(2, requests.Count);
        Assert.All(requests, uri => Assert.Contains("query=Batman", uri));
        Assert.Equal(1940, volumes[0].StartYear);
        Assert.Equal("DC Comics", volumes[0].Publisher);
    }

    [Theory]
    [InlineData(null, "Batman")]
    [InlineData("test-key", " ")]
    public async Task SearchVolumesAsync_NotConfiguredOrNoTitle_AsksNothing(string? apiKey, string title)
    {
        var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("no request expected"));
        var service = CreateService(handler, new AppSettings
        {
            EnableExternalSeriesMetadata = true,
            ComicVineApiKey = apiKey,
            ComicVineBaseUrl = "https://comicvine.example/api"
        });

        Assert.Empty(await service.SearchVolumesAsync(title));
    }

    [Fact]
    public async Task ListVolumeIssuesAsync_ReadsPagesOfAHundred_UpToTheRequestLimit()
    {
        var requests = new List<string>();
        static string Page(int count, int firstId)
            => "{\"status_code\":1,\"results\":[" + string.Join(",", Enumerable.Range(0, count).Select(i =>
                   $"{{\"id\":{firstId + i},\"issue_number\":\"{firstId + i}\",\"name\":\"Issue {firstId + i}\",\"cover_date\":\"2016-08-01\",\"deck\":\"Deck.\",\"site_detail_url\":\"https://comicvine.example/{firstId + i}\"}}"))
               + ",{\"id\":0,\"issue_number\":\"1\"},{\"id\":5}]}";
        var handler = new StubHttpMessageHandler(request =>
        {
            var uri = request.RequestUri!.ToString();
            requests.Add(uri);
            return JsonResponse(uri.Contains("offset=100") ? Page(3, 200) : Page(100, 1));
        });
        var service = CreateService(handler, ConfiguredSettings());

        var all = await service.ListVolumeIssuesAsync(91273, maxRequests: 5);
        var limited = await service.ListVolumeIssuesAsync(91273, maxRequests: 1);

        Assert.Equal((103, 2), (all.Value.Count, all.Requests));
        Assert.Equal((100, 1), (limited.Value.Count, limited.Requests));
        Assert.Contains("/issues/?api_key=test-key&format=json&filter=volume:91273", requests[0]);
        Assert.Contains("offset=0", requests[0]);
        Assert.Equal(new ComicVineIssueSummary(1, "1", "Issue 1", "2016-08-01", "Deck.", "https://comicvine.example/1"), all.Value[0]);
    }

    [Fact]
    public async Task GetIssueDetailsAsync_ReadsTheWritersAndStoryArcs()
    {
        var handler = new StubHttpMessageHandler(request => request.RequestUri!.AbsolutePath.Contains("4000-101")
            ? JsonResponse("""
              {"status_code":1,"results":{"id":101,
                "person_credits":[{"name":"Tom King","role":"writer"},{"name":"David Finch","role":"penciler, cover"},
                                  {"name":"TOM KING","role":"Writer, editor"},{"name":" ","role":"writer"},{"role":"writer"}],
                "story_arc_credits":[{"id":7,"name":"I Am Gotham"},{"id":7,"name":"I Am Gotham"},{"id":8},{"name":"No id"}]}}
              """)
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        var service = CreateService(handler, ConfiguredSettings());

        var details = await service.GetIssueDetailsAsync(101);

        Assert.Equal(["Tom King"], details!.Writers);
        Assert.Equal([new ComicVineArcReference(7, "I Am Gotham")], details.StoryArcs);
        Assert.Null(await service.GetIssueDetailsAsync(999));
    }

    [Fact]
    public async Task GetStoryArcAsync_ReadsTheIssuesInOrder()
    {
        var handler = new StubHttpMessageHandler(request => request.RequestUri!.AbsolutePath.Contains("4045-7")
            ? JsonResponse("""
              {"status_code":1,"results":{"id":7,"name":"I Am Gotham","deck":"Gotham and Gotham Girl.","site_detail_url":"https://comicvine.example/arc",
                "issues":[{"id":102,"name":"Part Two","issue_number":"2","volume":{"name":"Batman"}},{"id":101},{"name":"no id"}]}}
              """)
            : JsonResponse("""{"status_code":1,"results":{"id":9}}"""));
        var service = CreateService(handler, ConfiguredSettings());

        var arc = await service.GetStoryArcAsync(7);

        Assert.Equal(("I Am Gotham", "Gotham and Gotham Girl.", "https://comicvine.example/arc"), (arc!.Name, arc.Summary, arc.SiteUrl));
        Assert.Equal([new ComicVineArcIssue(102, "Part Two", "2", "Batman"), new ComicVineArcIssue(101, null, null, null)], arc.Issues);
        Assert.Null(await service.GetStoryArcAsync(9));
    }

    [Fact]
    public async Task CatalogCalls_WithoutAnApiKey_AskNothing()
    {
        var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("no request expected"));
        var service = CreateService(handler, new AppSettings { EnableExternalSeriesMetadata = true, ComicVineBaseUrl = "https://comicvine.example/api" });

        Assert.False(service.IsConfigured);
        Assert.Empty((await service.ListVolumeIssuesAsync(1, 5)).Value);
        Assert.Null(await service.GetIssueDetailsAsync(1));
        Assert.Null(await service.GetStoryArcAsync(1));
        Assert.True(CreateService(handler, ConfiguredSettings()).IsConfigured);
    }

    [Fact]
    public void CatalogParsers_TolerateAnswersWithoutResults()
    {
        using var empty = System.Text.Json.JsonDocument.Parse("""{"status_code":1}""");

        Assert.Empty(ComicVineSeriesMetadataService.ParseIssueList(empty.RootElement));
        Assert.Null(ComicVineSeriesMetadataService.ParseIssueDetails(empty.RootElement));
        Assert.Null(ComicVineSeriesMetadataService.ParseStoryArc(empty.RootElement));
    }

    private static AppSettings ConfiguredSettings() => new()
    {
        EnableExternalSeriesMetadata = true,
        ComicVineApiKey = "test-key",
        ComicVineBaseUrl = "https://comicvine.example/api"
    };

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static ComicVineSeriesMetadataService CreateService(StubHttpMessageHandler handler, AppSettings settings)
    {
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, disposeHandler: false));

        var optionsMonitor = new Mock<IOptionsMonitor<AppSettings>>();
        optionsMonitor.Setup(o => o.CurrentValue).Returns(settings);

        return new ComicVineSeriesMetadataService(
            httpClientFactory.Object,
            optionsMonitor.Object,
            new MemoryCache(new MemoryCacheOptions()),
            Mock.Of<ILogger<ComicVineSeriesMetadataService>>());
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
