using System.Net;
using System.Text;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class MangaDexSeriesMetadataServiceTests
{
    private static MangaDexSeriesMetadataService CreateService(
        StubHttpMessageHandler handler,
        AppSettings settings)
    {
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(handler));

        var optionsMonitor = new Mock<IOptionsMonitor<AppSettings>>();
        optionsMonitor.Setup(options => options.CurrentValue).Returns(settings);

        return new MangaDexSeriesMetadataService(
            httpClientFactory.Object,
            optionsMonitor.Object,
            new MemoryCache(new MemoryCacheOptions()),
            Mock.Of<ILogger<MangaDexSeriesMetadataService>>());
    }
    [Fact]
    public async Task LookupSeriesAsync_ReturnsCanonicalTitleAndAliases()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
            {
              "data": [
                {
                  "attributes": {
                    "title": { "en": "Solo Leveling" },
                    "altTitles": [
                      { "ko": "나 혼자만 레벨업" },
                      { "ko-ro": "Na Honjaman Level Up" }
                    ]
                  }
                }
              ]
            }
            """, Encoding.UTF8, "application/json")
        });

        var service = CreateService(handler, new AppSettings
        {
            EnableMangaDexMetadata = true,
            MangaDexBaseUrl = "https://api.mangadex.example"
        });

        var result = await service.LookupSeriesAsync("Solo Leveling");

        Assert.NotNull(result);
        Assert.Equal("Solo Leveling", result!.CanonicalTitle);
        Assert.Contains("나 혼자만 레벨업", result.Aliases);
        Assert.Contains("Na Honjaman Level Up", result.Aliases);
        Assert.Equal("MangaDex", result.Source);

        // MangaDex altTitles are already language-tagged; we should pass
        // those tags through verbatim (incl. regional variants like ko-ro).
        Assert.Contains(result.LocalizedTitles, t => t.Title == "Solo Leveling" && t.Language == "en");
        Assert.Contains(result.LocalizedTitles, t => t.Title == "나 혼자만 레벨업" && t.Language == "ko");
        Assert.Contains(result.LocalizedTitles, t => t.Title == "Na Honjaman Level Up" && t.Language == "ko-ro");
    }

    [Fact]
    public async Task LookupSeriesAsync_MatchesByAlias()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
            {
              "data": [
                {
                  "attributes": {
                    "title": { "en": "Berserk" },
                    "altTitles": [
                      { "ja": "ベルセルク" }
                    ]
                  }
                }
              ]
            }
            """, Encoding.UTF8, "application/json")
        });

        var service = CreateService(handler, new AppSettings
        {
            EnableMangaDexMetadata = true,
            MangaDexBaseUrl = "https://api.mangadex.example"
        });

        var result = await service.LookupSeriesAsync("ベルセルク");

        Assert.NotNull(result);
        Assert.Equal("Berserk", result!.CanonicalTitle);
        Assert.Equal("MangaDex", result.Source);
    }

    [Fact]
    public async Task LookupSeriesAsync_ReturnsNull_WhenDisabled()
    {
        var service = CreateService(
            new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)),
            new AppSettings { EnableMangaDexMetadata = false });

        var result = await service.LookupSeriesAsync("Solo Leveling");

        Assert.Null(result);
    }

    [Fact]
    public async Task LookupSeriesAsync_ReturnsNull_WhenSeriesNameIsEmpty()
    {
        var service = CreateService(
            new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)),
            new AppSettings { EnableMangaDexMetadata = true, MangaDexBaseUrl = "https://api.mangadex.example" });

        var result = await service.LookupSeriesAsync(string.Empty);

        Assert.Null(result);
    }

    [Fact]
    public async Task LookupSeriesAsync_ReturnsNull_WhenNoResultsMatch()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{ "data": [] }""", Encoding.UTF8, "application/json")
        });

        var service = CreateService(handler, new AppSettings
        {
            EnableMangaDexMetadata = true,
            MangaDexBaseUrl = "https://api.mangadex.example"
        });

        var result = await service.LookupSeriesAsync("NonExistentSeries12345");

        Assert.Null(result);
    }

    [Fact]
    public async Task LookupSeriesAsync_ReturnsNull_WhenApiFails()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var service = CreateService(handler, new AppSettings
        {
            EnableMangaDexMetadata = true,
            MangaDexBaseUrl = "https://api.mangadex.example"
        });

        var result = await service.LookupSeriesAsync("Solo Leveling");

        Assert.Null(result);
    }

    [Fact]
    public async Task LookupSeriesAsync_FallsBackToFirstAvailableTitle_WhenNoEnglishTitle()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
            {
              "data": [
                {
                  "attributes": {
                    "title": { "ko": "신의 탑" },
                    "altTitles": [
                      { "en": "Tower of God" }
                    ]
                  }
                }
              ]
            }
            """, Encoding.UTF8, "application/json")
        });

        var service = CreateService(handler, new AppSettings
        {
            EnableMangaDexMetadata = true,
            MangaDexBaseUrl = "https://api.mangadex.example"
        });

        var result = await service.LookupSeriesAsync("신의 탑");

        Assert.NotNull(result);
        Assert.Equal("신의 탑", result!.CanonicalTitle);
        Assert.Contains("Tower of God", result.Aliases);
        Assert.Equal("MangaDex", result.Source);
    }

    [Fact]
    public async Task CheckHealthAsync_DisabledProvider_ReportsDisabled()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        var service = CreateService(handler, new AppSettings { EnableMangaDexMetadata = false });

        var health = await service.CheckHealthAsync();

        Assert.Equal("MangaDex", health.Name);
        Assert.False(health.Enabled);
        Assert.Null(health.Reachable);
        Assert.Equal("Disabled in settings", health.StatusMessage);
    }

    [Fact]
    public async Task CheckHealthAsync_HttpError_ReportsUnreachable()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable) { ReasonPhrase = "Service Unavailable" });
        var service = CreateService(handler, new AppSettings
        {
            EnableMangaDexMetadata = true,
            MangaDexBaseUrl = "https://api.mangadex.example"
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
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{\"data\":[]}", Encoding.UTF8, "application/json")
        });
        var service = CreateService(handler, new AppSettings
        {
            EnableMangaDexMetadata = true,
            MangaDexBaseUrl = "https://api.mangadex.example"
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
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
        var service = CreateService(handler, new AppSettings
        {
            EnableMangaDexMetadata = true,
            MangaDexBaseUrl = "https://api.mangadex.example"
        });

        await service.LookupSeriesAsync("Anything");
        var health = await service.CheckHealthAsync();

        Assert.True(health.FailureCount >= 1);
        Assert.NotNull(health.LastFailureUtc);
    }

    [Fact]
    public async Task LookupSeriesAsync_RetriesOnce_When429WithRetryAfter()
    {
        var callCount = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            callCount++;
            if (callCount == 1)
            {
                var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                // Use a very short Retry-After so the test does not hang.
                resp.Headers.TryAddWithoutValidation("Retry-After", "0");
                return resp;
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "data": [
                    {
                      "attributes": {
                        "title": { "en": "Berserk" },
                        "altTitles": []
                      }
                    }
                  ]
                }
                """, Encoding.UTF8, "application/json")
            };
        });

        var service = CreateService(handler, new AppSettings
        {
            EnableMangaDexMetadata = true,
            MangaDexBaseUrl = "https://api.mangadex.example"
        });

        var result = await service.LookupSeriesAsync("Berserk");

        Assert.Equal(2, callCount); // first 429, then retry
        Assert.NotNull(result);
        Assert.Equal("Berserk", result!.CanonicalTitle);
    }

    [Fact]
    public async Task LookupSeriesAsync_RecordsRateLimited_OnPersistent429()
    {
        var handler = new StubHttpMessageHandler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            resp.Headers.TryAddWithoutValidation("Retry-After", "0");
            return resp;
        });

        var service = CreateService(handler, new AppSettings
        {
            EnableMangaDexMetadata = true,
            MangaDexBaseUrl = "https://api.mangadex.example"
        });

        await service.LookupSeriesAsync("Anything");
        var health = await service.CheckHealthAsync();

        Assert.True(health.RateLimited);
        Assert.NotNull(health.RateLimitedUntilUtc);
        // Cached probe path runs SearchInternalAsync's status, but the rate-limit
        // override should produce a degraded status message.
        Assert.Equal("Degraded - rate limited", health.StatusMessage);
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
