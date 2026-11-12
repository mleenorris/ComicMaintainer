using System.Net;
using System.Text;
using System.Text.Json;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Automatic matching, re-fetching a match by id and GraphQL error handling
/// shared by both AniList providers. Each service gets its own generous
/// throttle, so these requests do not spend the process-wide AniList budget
/// the other AniList tests share.
/// </summary>
public sealed class AniListSeriesMetadataProviderBaseTests : IDisposable
{
    private const string BaseUrl = "https://graphql.anilist.example";
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly ProviderRateLimiter _limiter = new("AniList test", 1000, TimeSpan.FromSeconds(1));

    public void Dispose()
    {
        _cache.Dispose();
        _limiter.Dispose();
    }

    private AniListMangaSeriesMetadataService CreateManga(Func<HttpRequestMessage, string, HttpResponseMessage> respond)
        => new(Factory(respond), Options(), _cache, NullLogger<AniListMangaSeriesMetadataService>.Instance, metrics: null, _limiter);

    private AniListManhwaSeriesMetadataService CreateManhwa(Func<HttpRequestMessage, string, HttpResponseMessage> respond)
        => new(Factory(respond), Options(), _cache, NullLogger<AniListManhwaSeriesMetadataService>.Instance, metrics: null, _limiter);

    private static IOptionsMonitor<AppSettings> Options()
    {
        var options = new Mock<IOptionsMonitor<AppSettings>>();
        options.Setup(o => o.CurrentValue).Returns(new AppSettings { EnableAniListMetadata = true, AniListBaseUrl = BaseUrl });
        return options.Object;
    }

    private static IHttpClientFactory Factory(Func<HttpRequestMessage, string, HttpResponseMessage> respond)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(new StubHandler(respond), disposeHandler: true));
        return factory.Object;
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    [Fact]
    public async Task LookupSeriesAsync_ReturnsNull_WhenOnlyRelatedSeriesAreFound()
    {
        // AniList's SEARCH_MATCH returns the closest titles; none is the series.
        var service = CreateManga((_, _) => Json("""
            {"data":{"Page":{"media":[
              {"id":1,"title":{"english":"Naruto Shippuden"}},
              {"id":2,"title":{"english":"Boruto: Naruto Next Generations"}}
            ]}}}
            """));

        Assert.Null(await service.LookupSeriesAsync("Naruto"));
    }

    [Fact]
    public async Task LookupSeriesAsync_MatchesBySynonym_AndReportsTheMediaId()
    {
        var service = CreateManga((_, _) => Json("""
            {"data":{"Page":{"media":[
              {"id":16498,"title":{"english":"Attack on Titan","romaji":"Shingeki no Kyojin"},"synonyms":["SnK"]}
            ]}}}
            """));

        var result = await service.LookupSeriesAsync("Shingeki no Kyojin");

        Assert.NotNull(result);
        Assert.Equal("Attack on Titan", result!.CanonicalTitle);
        Assert.Equal("16498", result.SourceId);
        Assert.Equal("AniListManga", result.Source);
    }

    [Fact]
    public async Task LookupMatchAsync_FetchesTheMediaById()
    {
        string? body = null;
        var service = CreateManhwa((_, requestBody) =>
        {
            body = requestBody;
            return Json("""{"data":{"Media":{"id":105398,"title":{"english":"Solo Leveling","native":"나 혼자만 레벨업"}}}}""");
        });

        var result = await service.LookupMatchAsync("AniListManhwa", "105398", "Solo Leveling");

        Assert.NotNull(result);
        Assert.Equal("Solo Leveling", result!.CanonicalTitle);
        Assert.Equal("105398", result.SourceId);
        Assert.Equal("AniListManhwa", result.Source);
        using var document = JsonDocument.Parse(body!);
        Assert.Equal(105398, document.RootElement.GetProperty("variables").GetProperty("id").GetInt64());
        Assert.Contains("Media(id: $id, type: MANGA)", document.RootElement.GetProperty("query").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.OK)]
    public async Task LookupMatchAsync_ReturnsNull_WhenTheMediaNoLongerExists(HttpStatusCode status)
    {
        var service = CreateManga((_, _) => Json(
            """{"errors":[{"message":"Not Found.","status":404}],"data":{"Media":null}}""", status));

        Assert.Null(await service.LookupMatchAsync("AniListManga", "999999999", "Gone"));
    }

    [Theory]
    [InlineData(500, ExternalMetadataFailureKind.InvalidResponse)]
    [InlineData(429, ExternalMetadataFailureKind.RateLimited)]
    public async Task SearchSeriesAsync_GraphQlErrorWithoutData_IsAFailure(int errorStatus, ExternalMetadataFailureKind expected)
    {
        var service = CreateManga((_, _) => Json(
            $$"""{"errors":[{"message":"Something broke","status":{{errorStatus}}}],"data":null}"""));

        var failure = await Assert.ThrowsAsync<ExternalMetadataUnavailableException>(
            () => service.SearchSeriesAsync("Attack on Titan"));

        Assert.Equal(expected, failure.Kind);
        Assert.Contains("Something broke", failure.Message);
    }

    [Fact]
    public async Task SearchSeriesAsync_GraphQlErrorAlongsideData_KeepsTheData()
    {
        var service = CreateManga((_, _) => Json("""
            {"errors":[{"message":"Partial","status":500}],
             "data":{"Page":{"media":[{"id":1,"title":{"english":"Berserk"}}]}}}
            """));

        var results = await service.SearchSeriesAsync("Berserk");

        Assert.Equal("Berserk", Assert.Single(results).CanonicalTitle);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return _respond(request, body);
        }
    }
}
