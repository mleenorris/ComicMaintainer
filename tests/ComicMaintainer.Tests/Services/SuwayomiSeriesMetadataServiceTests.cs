using System.Net;
using System.Text;
using System.Text.Json;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class SuwayomiSeriesMetadataServiceTests
{
    private const string BaseUrl = "http://suwayomi.example:4567";

    private static SuwayomiSeriesMetadataService CreateService(
        StubHttpMessageHandler handler,
        AppSettings settings)
    {
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, disposeHandler: false));

        var optionsMonitor = new Mock<IOptionsMonitor<AppSettings>>();
        optionsMonitor.Setup(options => options.CurrentValue).Returns(settings);

        return new SuwayomiSeriesMetadataService(
            httpClientFactory.Object,
            optionsMonitor.Object,
            new MemoryCache(new MemoryCacheOptions()),
            Mock.Of<ILogger<SuwayomiSeriesMetadataService>>());
    }

    private static AppSettings DefaultSettings(string sourceIds = "100") => new()
    {
        EnableSuwayomiMetadata = true,
        SuwayomiBaseUrl = BaseUrl,
        SuwayomiSourceIds = sourceIds,
    };

    [Fact]
    public async Task LookupSeriesAsync_ReturnsExactMatchFromConfiguredSource()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            // Verify endpoint and POST body shape
            Assert.Equal($"{BaseUrl}/api/graphql", request.RequestUri!.ToString());
            Assert.Equal(HttpMethod.Post, request.Method);
            return JsonResponse("""
                {
                  "data": {
                    "fetchSourceManga": {
                      "mangas": [
                        { "title": "Some Other Manga" },
                        { "title": "Solo Leveling" }
                      ]
                    }
                  }
                }
                """);
        });

        var service = CreateService(handler, DefaultSettings());

        var result = await service.LookupSeriesAsync("Solo Leveling");

        Assert.NotNull(result);
        Assert.Equal("Solo Leveling", result!.CanonicalTitle);
        Assert.Equal("Suwayomi:100", result.Source);
    }

    [Fact]
    public async Task LookupSeriesAsync_FallsBackToFirstResult_WhenNoExactMatch()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse("""
            {
              "data": {
                "fetchSourceManga": {
                  "mangas": [
                    { "title": "Berserk Deluxe Edition" }
                  ]
                }
              }
            }
            """));

        var service = CreateService(handler, DefaultSettings());

        var result = await service.LookupSeriesAsync("Berserk");

        Assert.NotNull(result);
        Assert.Equal("Berserk Deluxe Edition", result!.CanonicalTitle);
    }

    [Fact]
    public async Task LookupSeriesAsync_DiscoversSources_WhenNoneConfigured()
    {
        var calls = new List<string>();
        var handler = new StubHttpMessageHandler(request =>
        {
            var bodyText = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            calls.Add(bodyText);
            using var doc = JsonDocument.Parse(bodyText);
            var query = doc.RootElement.GetProperty("query").GetString() ?? string.Empty;
            if (query.Contains("sources", StringComparison.Ordinal))
            {
                return JsonResponse("""{ "data": { "sources": { "nodes": [ { "id": "42" }, { "id": 7 } ] } } }""");
            }
            return JsonResponse("""{ "data": { "fetchSourceManga": { "mangas": [ { "title": "Discovered Title" } ] } } }""");
        });

        var service = CreateService(handler, DefaultSettings(sourceIds: string.Empty));

        var result = await service.LookupSeriesAsync("Anything");

        Assert.NotNull(result);
        Assert.Equal("Discovered Title", result!.CanonicalTitle);
        // 1 sources query + 2 search mutations (one per discovered source)
        Assert.Equal(3, calls.Count);
    }

    [Fact]
    public async Task LookupSeriesAsync_AggregatesAcrossMultipleConfiguredSources()
    {
        var titlesPerCall = new Queue<string>(["From Source A", "Solo Leveling"]);
        var handler = new StubHttpMessageHandler(_ =>
        {
            var t = titlesPerCall.Dequeue();
            return JsonResponse($$"""{ "data": { "fetchSourceManga": { "mangas": [ { "title": "{{t}}" } ] } } }""");
        });

        var service = CreateService(handler, DefaultSettings(sourceIds: "1,2"));

        var result = await service.LookupSeriesAsync("Solo Leveling");

        Assert.NotNull(result);
        Assert.Equal("Solo Leveling", result!.CanonicalTitle);
        Assert.Equal("Suwayomi:2", result.Source);
    }

    [Fact]
    public async Task LookupSeriesAsync_ReturnsNull_WhenDisabled()
    {
        var service = CreateService(
            new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)),
            new AppSettings { EnableSuwayomiMetadata = false });

        var result = await service.LookupSeriesAsync("Solo Leveling");

        Assert.Null(result);
    }

    [Fact]
    public async Task LookupSeriesAsync_ReturnsNull_WhenSeriesNameIsEmpty()
    {
        var service = CreateService(
            new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)),
            DefaultSettings());

        var result = await service.LookupSeriesAsync(string.Empty);

        Assert.Null(result);
    }

    [Fact]
    public async Task LookupSeriesAsync_ReturnsNull_WhenSourceListIsEmpty()
    {
        var handler = new StubHttpMessageHandler(_ =>
            JsonResponse("""{ "data": { "sources": { "nodes": [] } } }"""));

        var service = CreateService(handler, DefaultSettings(sourceIds: string.Empty));

        var result = await service.LookupSeriesAsync("Anything");

        Assert.Null(result);
    }

    [Fact]
    public async Task LookupSeriesAsync_ReturnsNull_WhenGraphQlErrors()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse("""
            {
              "data": null,
              "errors": [ { "message": "Source 100 not found" } ]
            }
            """));

        var service = CreateService(handler, DefaultSettings());

        var result = await service.LookupSeriesAsync("Solo Leveling");

        Assert.Null(result);
    }

    [Fact]
    public async Task LookupSeriesAsync_ReturnsNull_WhenHttpFails()
    {
        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var service = CreateService(handler, DefaultSettings());

        var result = await service.LookupSeriesAsync("Solo Leveling");

        Assert.Null(result);
    }

    [Fact]
    public async Task SearchSeriesAsync_ReturnsRespectingLimit()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse("""
            {
              "data": {
                "fetchSourceManga": {
                  "mangas": [
                    { "title": "A" }, { "title": "B" }, { "title": "C" },
                    { "title": "D" }, { "title": "E" }, { "title": "F" }
                  ]
                }
              }
            }
            """));

        var service = CreateService(handler, DefaultSettings());

        var results = await service.SearchSeriesAsync("anything", limit: 3);

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.Equal("Suwayomi:100", r.Source));
    }

    [Fact]
    public async Task SearchSeriesAsync_DeduplicatesIdenticalTitlesAcrossSources()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse("""
            {
              "data": {
                "fetchSourceManga": {
                  "mangas": [ { "title": "Duplicate Title" } ]
                }
              }
            }
            """));

        // Two configured sources both returning the same title under different source labels
        var service = CreateService(handler, DefaultSettings(sourceIds: "1,2"));

        var results = await service.SearchSeriesAsync("anything");

        // Different sources -> not deduplicated (different Source label means different match)
        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Source == "Suwayomi:1");
        Assert.Contains(results, r => r.Source == "Suwayomi:2");
    }

    [Fact]
    public async Task CheckHealthAsync_DisabledProvider_ReportsDisabled()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var service = CreateService(handler, new AppSettings { EnableSuwayomiMetadata = false });

        var health = await service.CheckHealthAsync();

        Assert.Equal("Suwayomi", health.Name);
        Assert.False(health.Enabled);
        Assert.Null(health.Reachable);
        Assert.Equal("Disabled in settings", health.StatusMessage);
    }

    [Fact]
    public async Task CheckHealthAsync_HttpError_ReportsUnreachable()
    {
        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { ReasonPhrase = "Service Unavailable" });
        var service = CreateService(handler, DefaultSettings());

        var health = await service.CheckHealthAsync();

        Assert.True(health.Enabled);
        Assert.True(health.Configured);
        Assert.False(health.Reachable);
        Assert.Contains("503", health.StatusMessage);
    }

    [Fact]
    public async Task CheckHealthAsync_Reachable_ReportsHealthy()
    {
        var handler = new StubHttpMessageHandler(_ =>
            JsonResponse("""{ "data": { "sources": { "totalCount": 0 } } }"""));
        var service = CreateService(handler, DefaultSettings());

        var health = await service.CheckHealthAsync();

        Assert.True(health.Enabled);
        Assert.True(health.Configured);
        Assert.True(health.Reachable);
        Assert.Equal("Reachable", health.StatusMessage);
    }

    [Fact]
    public async Task LookupSeriesAsync_RecordsFailureCounters_OnHttpError()
    {
        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var service = CreateService(handler, DefaultSettings());

        await service.LookupSeriesAsync("Anything");
        var health = await service.CheckHealthAsync();

        Assert.True(health.FailureCount >= 1);
        Assert.NotNull(health.LastFailureUtc);
    }

    [Fact]
    public async Task LookupSeriesAsync_SendsBasicAuthHeader_WhenCredentialsConfigured()
    {
        AuthenticationHeaderCapture? captured = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            captured = new AuthenticationHeaderCapture(
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter);
            return JsonResponse("""{ "data": { "fetchSourceManga": { "mangas": [] } } }""");
        });

        var settings = DefaultSettings();
        settings.SuwayomiUsername = "user";
        settings.SuwayomiPassword = "pass";
        var service = CreateService(handler, settings);

        await service.LookupSeriesAsync("Anything");

        Assert.NotNull(captured);
        Assert.Equal("Basic", captured!.Scheme);
        var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes("user:pass"));
        Assert.Equal(expected, captured.Parameter);
    }

    private static HttpResponseMessage JsonResponse(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private sealed record AuthenticationHeaderCapture(string? Scheme, string? Parameter);

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
