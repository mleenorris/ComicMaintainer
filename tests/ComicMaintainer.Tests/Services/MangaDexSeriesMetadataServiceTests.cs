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
