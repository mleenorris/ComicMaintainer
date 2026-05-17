using System.Net;
using System.Text;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class AniListManhwaSeriesMetadataServiceTests
{
    private static AniListManhwaSeriesMetadataService CreateService(
        StubHttpMessageHandler handler,
        AppSettings settings)
    {
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(handler));

        var optionsMonitor = new Mock<IOptionsMonitor<AppSettings>>();
        optionsMonitor.Setup(options => options.CurrentValue).Returns(settings);

        return new AniListManhwaSeriesMetadataService(
            httpClientFactory.Object,
            optionsMonitor.Object,
            new MemoryCache(new MemoryCacheOptions()),
            Mock.Of<ILogger<AniListManhwaSeriesMetadataService>>());
    }

    [Fact]
    public async Task LookupSeriesAsync_ReturnsCanonicalTitleAndAliases()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
            {
              "data": {
                "Page": {
                  "media": [
                    {
                      "title": {
                        "english": "Solo Leveling",
                        "romaji": "Na Honjaman Level Up",
                        "native": "나 혼자만 레벨업"
                      },
                      "synonyms": ["I Alone Level-Up", "Only I Level Up"]
                    }
                  ]
                }
              }
            }
            """, Encoding.UTF8, "application/json")
        });

        var service = CreateService(handler, new AppSettings
        {
            EnableAniListMetadata = true,
            AniListBaseUrl = "https://graphql.anilist.example"
        });

        var result = await service.LookupSeriesAsync("Solo Leveling");

        Assert.NotNull(result);
        Assert.Equal("Solo Leveling", result!.CanonicalTitle);
        Assert.Contains("Na Honjaman Level Up", result.Aliases);
        Assert.Contains("나 혼자만 레벨업", result.Aliases);
        Assert.Contains("I Alone Level-Up", result.Aliases);
        Assert.Contains("Only I Level Up", result.Aliases);
        Assert.Equal("AniListManhwa", result.Source);
    }

    [Fact]
    public async Task LookupSeriesAsync_MatchesByAlias_NativeTitle()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
            {
              "data": {
                "Page": {
                  "media": [
                    {
                      "title": {
                        "english": "Tower of God",
                        "romaji": "Sin-ui Tap",
                        "native": "신의 탑"
                      },
                      "synonyms": []
                    }
                  ]
                }
              }
            }
            """, Encoding.UTF8, "application/json")
        });

        var service = CreateService(handler, new AppSettings
        {
            EnableAniListMetadata = true,
            AniListBaseUrl = "https://graphql.anilist.example"
        });

        var result = await service.LookupSeriesAsync("신의 탑");

        Assert.NotNull(result);
        Assert.Equal("Tower of God", result!.CanonicalTitle);
        Assert.Equal("AniListManhwa", result.Source);
    }

    [Fact]
    public async Task LookupSeriesAsync_FallsBackToRomaji_WhenNoEnglishTitle()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
            {
              "data": {
                "Page": {
                  "media": [
                    {
                      "title": {
                        "english": null,
                        "romaji": "Sin-ui Tap",
                        "native": "신의 탑"
                      },
                      "synonyms": []
                    }
                  ]
                }
              }
            }
            """, Encoding.UTF8, "application/json")
        });

        var service = CreateService(handler, new AppSettings
        {
            EnableAniListMetadata = true,
            AniListBaseUrl = "https://graphql.anilist.example"
        });

        var result = await service.LookupSeriesAsync("Sin-ui Tap");

        Assert.NotNull(result);
        Assert.Equal("Sin-ui Tap", result!.CanonicalTitle);
        Assert.Contains("신의 탑", result.Aliases);
        Assert.Equal("AniListManhwa", result.Source);
    }

    [Fact]
    public async Task LookupSeriesAsync_ReturnsNull_WhenDisabled()
    {
        var service = CreateService(
            new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)),
            new AppSettings { EnableAniListMetadata = false });

        var result = await service.LookupSeriesAsync("Solo Leveling");

        Assert.Null(result);
    }

    [Fact]
    public async Task LookupSeriesAsync_ReturnsNull_WhenSeriesNameIsEmpty()
    {
        var service = CreateService(
            new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)),
            new AppSettings { EnableAniListMetadata = true, AniListBaseUrl = "https://graphql.anilist.example" });

        var result = await service.LookupSeriesAsync(string.Empty);

        Assert.Null(result);
    }

    [Fact]
    public async Task LookupSeriesAsync_ReturnsNull_WhenNoResultsMatch()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{ "data": { "Page": { "media": [] } } }""", Encoding.UTF8, "application/json")
        });

        var service = CreateService(handler, new AppSettings
        {
            EnableAniListMetadata = true,
            AniListBaseUrl = "https://graphql.anilist.example"
        });

        var result = await service.LookupSeriesAsync("NonExistentManhwa12345");

        Assert.Null(result);
    }

    [Fact]
    public async Task LookupSeriesAsync_ReturnsNull_WhenApiFails()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var service = CreateService(handler, new AppSettings
        {
            EnableAniListMetadata = true,
            AniListBaseUrl = "https://graphql.anilist.example"
        });

        var result = await service.LookupSeriesAsync("Solo Leveling");

        Assert.Null(result);
    }

    [Fact]
    public async Task LookupSeriesAsync_PostsGraphQlQueryWithKoreanCountryFilter()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;

        var handler = new StubHttpMessageHandler(request =>
        {
            capturedRequest = request;
            capturedBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{ "data": { "Page": { "media": [] } } }""", Encoding.UTF8, "application/json")
            };
        });

        var service = CreateService(handler, new AppSettings
        {
            EnableAniListMetadata = true,
            AniListBaseUrl = "https://graphql.anilist.example"
        });

        await service.LookupSeriesAsync("Solo Leveling");

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.NotNull(capturedBody);
        // Verify the GraphQL query targets manga of Korean origin (manhwa)
        // and includes the search term we passed in.
        Assert.Contains("countryOfOrigin", capturedBody!);
        Assert.Contains("KR", capturedBody);
        Assert.Contains("Solo Leveling", capturedBody);
    }

    [Fact]
    public async Task CheckHealthAsync_DisabledProvider_ReportsDisabled()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var service = CreateService(handler, new AppSettings { EnableAniListMetadata = false });

        var health = await service.CheckHealthAsync();

        Assert.Equal("AniListManhwa", health.Name);
        Assert.False(health.Enabled);
        Assert.Null(health.Reachable);
        Assert.Equal("Disabled in settings", health.StatusMessage);
    }

    [Fact]
    public async Task CheckHealthAsync_HttpError_ReportsUnreachable()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { ReasonPhrase = "Service Unavailable" });
        var service = CreateService(handler, new AppSettings
        {
            EnableAniListMetadata = true,
            AniListBaseUrl = "https://graphql.anilist.example"
        });

        var health = await service.CheckHealthAsync();

        Assert.True(health.Enabled);
        Assert.True(health.Configured);
        Assert.False(health.Reachable);
        Assert.Contains("503", health.StatusMessage);
    }

    [Fact]
    public async Task CheckHealthAsync_Reachable_ReportsHealthy()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{ "data": { "Page": { "pageInfo": { "total": 0 } } } }""", Encoding.UTF8, "application/json")
        });
        var service = CreateService(handler, new AppSettings
        {
            EnableAniListMetadata = true,
            AniListBaseUrl = "https://graphql.anilist.example"
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
            EnableAniListMetadata = true,
            AniListBaseUrl = "https://graphql.anilist.example"
        });

        await service.LookupSeriesAsync("Anything");
        var health = await service.CheckHealthAsync();

        Assert.True(health.FailureCount >= 1);
        Assert.NotNull(health.LastFailureUtc);
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
