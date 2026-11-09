using System.Net;
using System.Text;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// The series details each provider is asked for and how its answer maps onto
/// <see cref="ExternalSeriesMetadata"/>. The JSON follows each provider's documented shape.
/// </summary>
public sealed class ProviderSeriesDetailsTests : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly ProviderRateLimiter _limiter = new("Provider details test", 1000, TimeSpan.FromSeconds(1));
    private readonly List<string> _requests = new();

    public void Dispose()
    {
        _cache.Dispose();
        _limiter.Dispose();
    }

    private IHttpClientFactory Factory(string json)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(new StubHandler(json, _requests), disposeHandler: true));
        return factory.Object;
    }

    private static IOptionsMonitor<AppSettings> Options(AppSettings settings)
    {
        var options = new Mock<IOptionsMonitor<AppSettings>>();
        options.Setup(o => o.CurrentValue).Returns(settings);
        return options.Object;
    }

    private Task<ExternalSeriesMetadata?> LookupAniListAsync(string mediaFields, bool manhwa = false)
    {
        var json = """{"data":{"Page":{"media":[{"id":30002,"title":{"english":"Berserk"},""" + mediaFields + """}]}}}""";
        var options = Options(new AppSettings { EnableAniListMetadata = true, AniListBaseUrl = "https://graphql.anilist.example" });
        return manhwa
            ? new AniListManhwaSeriesMetadataService(Factory(json), options, _cache, NullLogger<AniListManhwaSeriesMetadataService>.Instance, metrics: null, _limiter).LookupSeriesAsync("Berserk")
            : new AniListMangaSeriesMetadataService(Factory(json), options, _cache, NullLogger<AniListMangaSeriesMetadataService>.Instance, metrics: null, _limiter).LookupSeriesAsync("Berserk");
    }

    private Task<ExternalSeriesMetadata?> LookupMangaDexAsync(string id, string attributes)
    {
        var json = $$$"""{"result":"ok","data":[{"id":{{{id}}},"attributes":{"title":{"en":"Berserk"},{{{attributes}}}}}]}""";
        var service = new MangaDexSeriesMetadataService(
            Factory(json),
            Options(new AppSettings { EnableMangaDexMetadata = true, MangaDexBaseUrl = "https://mangadex.example", MangaDexRequestsPerSecond = 1000 }),
            _cache,
            NullLogger<MangaDexSeriesMetadataService>.Instance);
        return service.LookupSeriesAsync("Berserk");
    }

    private Task<ExternalSeriesMetadata?> LookupComicVineAsync(string fields)
    {
        var json = $$"""{"status_code":1,"results":[{"id":796,"name":"Batman",{{fields}}}]}""";
        var service = new ComicVineSeriesMetadataService(
            Factory(json),
            Options(new AppSettings { EnableExternalSeriesMetadata = true, ComicVineApiKey = "key", ComicVineBaseUrl = "https://comicvine.example/api" }),
            _cache,
            NullLogger<ComicVineSeriesMetadataService>.Instance,
            metrics: null,
            _limiter);
        return service.LookupSeriesAsync("Batman");
    }

    [Fact]
    public async Task AniList_AsksForTheDetailsAndMapsThem()
    {
        var result = await LookupAniListAsync("""
            "siteUrl":"https://anilist.co/manga/30002",
            "genres":["Action","Drama"],
            "status":"FINISHED",
            "chapters":364,
            "countryOfOrigin":"JP"
            """);

        foreach (var field in new[] { "siteUrl", "genres", "status(version: 2)", "chapters", "countryOfOrigin" })
        {
            Assert.Contains(field, Assert.Single(_requests));
        }

        Assert.Equal("30002", result!.SourceId);
        Assert.Equal("https://anilist.co/manga/30002", result.SiteUrl);
        Assert.Equal(new[] { "Action", "Drama" }, result.Genres);
        Assert.Equal(SeriesPublicationStatus.Completed, result.PublicationStatus);
        Assert.Equal(364, result.TotalChapters);
        Assert.Equal("ja", result.Origin);
    }

    [Theory]
    [InlineData("FINISHED", "completed")]
    [InlineData("RELEASING", "ongoing")]
    [InlineData("HIATUS", "hiatus")]
    [InlineData("CANCELLED", "cancelled")]
    [InlineData("NOT_YET_RELEASED", "upcoming")]
    [InlineData("SOMETHING_NEW", null)]
    public async Task AniList_MapsItsStatusValues(string anilistStatus, string? expected)
    {
        var result = await LookupAniListAsync($"\"status\":\"{anilistStatus}\"");

        Assert.Equal(expected, result!.PublicationStatus);
    }

    [Theory]
    [InlineData("JP", "ja")]
    [InlineData("KR", "ko")]
    [InlineData("CN", "zh")]
    [InlineData("TW", "zh")]
    [InlineData("US", null)]
    public async Task AniList_MapsCountryToOriginalLanguage(string country, string? expected)
    {
        var result = await LookupAniListAsync($"\"countryOfOrigin\":\"{country}\"", manhwa: country == "KR");

        Assert.Equal(expected, result!.Origin);
    }

    [Fact]
    public async Task AniList_IgnoresMalformedDetailsAndKeepsTheCandidate()
    {
        var result = await LookupAniListAsync("""
            "siteUrl":"javascript:alert(1)",
            "genres":"Action",
            "status":42,
            "chapters":"364",
            "countryOfOrigin":null
            """);

        Assert.Equal("Berserk", result!.CanonicalTitle);
        Assert.Null(result.SiteUrl);
        Assert.Empty(result.Genres);
        Assert.Null(result.PublicationStatus);
        Assert.Null(result.TotalChapters);
        Assert.Null(result.Origin);
    }

    [Fact]
    public async Task AniList_DropsNonTextGenresAndZeroChapters()
    {
        var result = await LookupAniListAsync("""
            "genres":["Action",7,null,"action"," Drama "],
            "chapters":0
            """);

        Assert.Equal(new[] { "Action", "Drama" }, result!.Genres);
        Assert.Null(result.TotalChapters);
    }

    [Fact]
    public async Task MangaDex_MapsTheDetailsAndBuildsTheSiteLink()
    {
        var result = await LookupMangaDexAsync("\"801513ba-a712-498c-8f57-cae55b38cc92\"", """
            "status":"completed",
            "lastChapter":"374",
            "originalLanguage":"ja",
            "tags":[
              {"id":"1","type":"tag","attributes":{"name":{"en":"Action"},"group":"genre"}},
              {"id":"2","type":"tag","attributes":{"name":{"ko":"드라마"},"group":"genre"}},
              {"id":"3","type":"tag","attributes":{"name":{"en":"Demons"},"group":"theme"}},
              {"id":"4","type":"tag","attributes":{"name":{"en":"Long Strip"},"group":"format"}}
            ]
            """);

        Assert.Equal("https://mangadex.org/title/801513ba-a712-498c-8f57-cae55b38cc92", result!.SiteUrl);
        Assert.Equal(new[] { "Action", "드라마" }, result.Genres);
        Assert.Equal(SeriesPublicationStatus.Completed, result.PublicationStatus);
        Assert.Equal(374, result.TotalChapters);
        Assert.Equal("ja", result.Origin);
    }

    [Theory]
    [InlineData("\"107.5\"", null)]
    [InlineData("\"\"", null)]
    [InlineData("null", null)]
    [InlineData("\"0\"", null)]
    [InlineData("\"12\"", 12)]
    public async Task MangaDex_OnlyAWholeLastChapterIsATotal(string lastChapter, int? expected)
    {
        var result = await LookupMangaDexAsync("\"id-1\"", $"\"lastChapter\":{lastChapter}");

        Assert.Equal(expected, result!.TotalChapters);
    }

    [Theory]
    [InlineData("zh-hk", "zh")]
    [InlineData("KO", "ko")]
    [InlineData("x1", null)]
    public async Task MangaDex_KeepsThePrimaryLanguageOfTheOriginalLanguage(string language, string? expected)
    {
        var result = await LookupMangaDexAsync("\"id-1\"", $"\"originalLanguage\":\"{language}\"");

        Assert.Equal(expected, result!.Origin);
    }

    [Fact]
    public async Task MangaDex_UnknownStatusAndMalformedTagsAreIgnored()
    {
        var result = await LookupMangaDexAsync("\"id-1\"", """
            "status":"paused",
            "tags":[
              7,
              {"attributes":{"group":"genre"}},
              {"attributes":{"name":"Action","group":"genre"}},
              {"attributes":{"name":{"en":"Drama"},"group":"GENRE"}}
            ]
            """);

        Assert.Null(result!.PublicationStatus);
        Assert.Equal(new[] { "Drama" }, result.Genres);
    }

    [Fact]
    public async Task MangaDex_TagsThatAreNotAListGiveNoGenres()
    {
        var result = await LookupMangaDexAsync("\"id-1\"", "\"tags\":{}");

        Assert.Empty(result!.Genres);
    }

    [Fact]
    public async Task MangaDex_WithoutAnIdThereIsNoSiteLink()
    {
        var result = await LookupMangaDexAsync("null", "\"status\":\"ongoing\"");

        Assert.Null(result!.SiteUrl);
        Assert.Equal(SeriesPublicationStatus.Ongoing, result.PublicationStatus);
    }

    [Fact]
    public async Task ComicVine_AsksForTheLinkAndCountAndReportsNoStatusOrOrigin()
    {
        var result = await LookupComicVineAsync("""
            "site_detail_url":"https://comicvine.gamespot.com/batman/4050-796/",
            "count_of_issues":713
            """);

        var request = Assert.Single(_requests);
        Assert.Contains("site_detail_url", request);
        Assert.Contains("count_of_issues", request);
        Assert.Equal("https://comicvine.gamespot.com/batman/4050-796/", result!.SiteUrl);
        Assert.Equal(713, result.TotalChapters);
        Assert.Null(result.PublicationStatus);
        Assert.Null(result.Origin);
        Assert.Empty(result.Genres);
    }

    /// <summary>Records each request's URL and body, and answers with a fixed JSON body.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _json;
        private readonly List<string> _requests;

        public StubHandler(string json, List<string> requests)
        {
            _json = json;
            _requests = requests;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            _requests.Add($"{request.RequestUri} {body}");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json")
            };
        }
    }
}
