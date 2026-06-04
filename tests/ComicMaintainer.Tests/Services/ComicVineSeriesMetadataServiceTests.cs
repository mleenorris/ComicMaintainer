using System.Net;
using System.Text;
using ComicMaintainer.Core.Configuration;
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
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(handler));

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

        await service.LookupSeriesAsync("Anything");
        var health = await service.CheckHealthAsync();

        Assert.True(health.FailureCount >= 1);
        Assert.NotNull(health.LastFailureUtc);
    }

    private static ComicVineSeriesMetadataService CreateService(StubHttpMessageHandler handler, AppSettings settings)
    {
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(handler));

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
