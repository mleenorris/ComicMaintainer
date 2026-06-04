using System.Net;
using System.Text;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class AniListMangaSeriesMetadataServiceTests
{
    private static AniListMangaSeriesMetadataService CreateService(
        StubHttpMessageHandler handler,
        AppSettings settings)
    {
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(handler));

        var optionsMonitor = new Mock<IOptionsMonitor<AppSettings>>();
        optionsMonitor.Setup(options => options.CurrentValue).Returns(settings);

        return new AniListMangaSeriesMetadataService(
            httpClientFactory.Object,
            optionsMonitor.Object,
            new MemoryCache(new MemoryCacheOptions()),
            Mock.Of<ILogger<AniListMangaSeriesMetadataService>>());
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
                        "english": "Attack on Titan",
                        "romaji": "Shingeki no Kyojin",
                        "native": "進撃の巨人"
                      },
                      "synonyms": ["AoT", "SnK"]
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

        var result = await service.LookupSeriesAsync("Attack on Titan");

        Assert.NotNull(result);
        Assert.Equal("Attack on Titan", result!.CanonicalTitle);
        Assert.Contains("Shingeki no Kyojin", result.Aliases);
        Assert.Contains("進撃の巨人", result.Aliases);
        Assert.Contains("AoT", result.Aliases);
        Assert.Contains("SnK", result.Aliases);
        Assert.Equal("AniListManga", result.Source);

        // LocalizedTitles are tagged: english=en, romaji=ja-Latn, native=ja,
        // synonyms have null language (no info available from AniList).
        Assert.Collection(result.LocalizedTitles,
            t => { Assert.Equal("Attack on Titan", t.Title); Assert.Equal("en", t.Language); },
            t => { Assert.Equal("Shingeki no Kyojin", t.Title); Assert.Equal("ja-Latn", t.Language); },
            t => { Assert.Equal("進撃の巨人", t.Title); Assert.Equal("ja", t.Language); },
            t => { Assert.Equal("AoT", t.Title); Assert.Null(t.Language); },
            t => { Assert.Equal("SnK", t.Title); Assert.Null(t.Language); });
    }

    [Fact]
    public async Task LookupSeriesAsync_PopulatesSynopsisAsPlainText()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
            {
              "data": {
                "Page": {
                  "media": [
                    {
                      "title": { "english": "Attack on Titan" },
                      "description": "Humanity fights <i>titans</i>.<br>Behind walls."
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

        var result = await service.LookupSeriesAsync("Attack on Titan");

        Assert.NotNull(result);
        Assert.False(string.IsNullOrWhiteSpace(result!.Synopsis));
        Assert.Contains("titans", result.Synopsis);
        Assert.DoesNotContain("<i>", result.Synopsis);
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
                        "english": "One Piece",
                        "romaji": "One Piece",
                        "native": "ワンピース"
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

        var result = await service.LookupSeriesAsync("ワンピース");

        Assert.NotNull(result);
        Assert.Equal("One Piece", result!.CanonicalTitle);
        Assert.Equal("AniListManga", result.Source);
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
                        "romaji": "Yotsuba to!",
                        "native": "よつばと！"
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

        var result = await service.LookupSeriesAsync("Yotsuba to!");

        Assert.NotNull(result);
        Assert.Equal("Yotsuba to!", result!.CanonicalTitle);
        Assert.Contains("よつばと！", result.Aliases);
        Assert.Equal("AniListManga", result.Source);
    }

    [Fact]
    public async Task LookupSeriesAsync_ReturnsNull_WhenDisabled()
    {
        var service = CreateService(
            new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)),
            new AppSettings { EnableAniListMetadata = false });

        var result = await service.LookupSeriesAsync("Attack on Titan");

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

        var result = await service.LookupSeriesAsync("NonExistentManga12345");

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

        var result = await service.LookupSeriesAsync("Attack on Titan");

        Assert.Null(result);
    }

    [Fact]
    public async Task LookupSeriesAsync_PostsGraphQlQueryWithJapaneseCountryFilter()
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

        await service.LookupSeriesAsync("Attack on Titan");

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.NotNull(capturedBody);
        // Verify the GraphQL query targets manga of Japanese origin
        // and includes the search term we passed in.
        Assert.Contains("countryOfOrigin", capturedBody!);
        Assert.Contains("JP", capturedBody);
        Assert.Contains("Attack on Titan", capturedBody);
    }

    [Fact]
    public async Task CheckHealthAsync_DisabledProvider_ReportsDisabled()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var service = CreateService(handler, new AppSettings { EnableAniListMetadata = false });

        var health = await service.CheckHealthAsync();

        Assert.Equal("AniListManga", health.Name);
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
