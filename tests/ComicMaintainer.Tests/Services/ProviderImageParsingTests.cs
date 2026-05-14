using System.Net;
using System.Text;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Tests covering the new ImageUrl/ThumbnailUrl extraction added to each
/// external metadata provider. The existing per-provider test files cover
/// the base canonical-title/aliases parsing; these focus on the image bits.
/// </summary>
public class ProviderImageParsingTests
{
    [Fact]
    public async Task ComicVine_ParsesImageUrls_FromImageObject()
    {
        var json = """
        {
          "results": [
            {
              "name": "Batman",
              "aliases": "Dark Knight",
              "image": {
                "icon_url": "https://comicvine.example/icon.jpg",
                "thumb_url": "https://comicvine.example/thumb.jpg",
                "small_url": "https://comicvine.example/small.jpg",
                "medium_url": "https://comicvine.example/medium.jpg",
                "super_url": "https://comicvine.example/super.jpg"
              }
            }
          ]
        }
        """;
        var service = ComicVineHelper.Create(json);

        var result = await service.LookupSeriesAsync("Batman");

        Assert.NotNull(result);
        Assert.Equal("https://comicvine.example/super.jpg", result!.ImageUrl);
        Assert.Equal("https://comicvine.example/thumb.jpg", result.ThumbnailUrl);
    }

    [Fact]
    public async Task ComicVine_FallsBackToMediumUrl_WhenSuperMissing()
    {
        var json = """
        {
          "results": [
            {
              "name": "Batman",
              "image": {
                "medium_url": "https://comicvine.example/medium.jpg"
              }
            }
          ]
        }
        """;
        var service = ComicVineHelper.Create(json);

        var result = await service.LookupSeriesAsync("Batman");

        Assert.NotNull(result);
        Assert.Equal("https://comicvine.example/medium.jpg", result!.ImageUrl);
    }

    [Fact]
    public async Task ComicVine_ReturnsNullImageUrl_WhenImageMissing()
    {
        var json = """{ "results": [ { "name": "Batman" } ] }""";
        var service = ComicVineHelper.Create(json);

        var result = await service.LookupSeriesAsync("Batman");

        Assert.NotNull(result);
        Assert.Null(result!.ImageUrl);
    }

    [Fact]
    public async Task MangaDex_BuildsCoverUrl_FromCoverArtRelationship()
    {
        var json = """
        {
          "data": [
            {
              "id": "abc-123",
              "attributes": { "title": { "en": "One Piece" } },
              "relationships": [
                { "type": "author", "id": "x" },
                { "type": "cover_art", "id": "cover-1", "attributes": { "fileName": "cover.jpg" } }
              ]
            }
          ]
        }
        """;
        var service = MangaDexHelper.Create(json);

        var result = await service.LookupSeriesAsync("One Piece");

        Assert.NotNull(result);
        Assert.Equal("https://uploads.mangadex.org/covers/abc-123/cover.jpg", result!.ImageUrl);
        Assert.Equal("https://uploads.mangadex.org/covers/abc-123/cover.jpg.256.jpg", result.ThumbnailUrl);
    }

    [Fact]
    public async Task MangaDex_ReturnsNullImageUrl_WhenNoCoverArtRelationship()
    {
        var json = """
        {
          "data": [
            {
              "id": "abc-123",
              "attributes": { "title": { "en": "One Piece" } },
              "relationships": [ { "type": "author", "id": "x" } ]
            }
          ]
        }
        """;
        var service = MangaDexHelper.Create(json);

        var result = await service.LookupSeriesAsync("One Piece");

        Assert.NotNull(result);
        Assert.Null(result!.ImageUrl);
    }

    [Fact]
    public async Task MangaDex_StripsPathSeparators_FromCoverFilename()
    {
        // Defensive: provider data should never contain path separators in
        // the filename, but if it ever does, we must not let it escape the
        // covers directory or build a malformed URL.
        var json = """
        {
          "data": [
            {
              "id": "abc-123",
              "attributes": { "title": { "en": "x" } },
              "relationships": [
                { "type": "cover_art", "id": "c", "attributes": { "fileName": "../evil.jpg" } }
              ]
            }
          ]
        }
        """;
        var service = MangaDexHelper.Create(json);

        var result = await service.LookupSeriesAsync("x");

        Assert.NotNull(result);
        Assert.NotNull(result!.ImageUrl);
        // The "../" prefix must be stripped so the URL stays inside the
        // covers/{id}/ directory.
        Assert.DoesNotContain("..", result.ImageUrl!);
        Assert.StartsWith("https://uploads.mangadex.org/covers/abc-123/", result.ImageUrl);
    }

    [Fact]
    public async Task AniList_ParsesCoverImage_FromGraphQlResponse()
    {
        var json = """
        {
          "data": {
            "Page": {
              "media": [
                {
                  "title": { "english": "Solo Leveling", "romaji": "Na Honjaman Level Up", "native": "나 혼자만 레벨업" },
                  "synonyms": ["나 혼자만 렙업"],
                  "coverImage": {
                    "extraLarge": "https://anilist.example/extra.jpg",
                    "large": "https://anilist.example/large.jpg",
                    "medium": "https://anilist.example/medium.jpg"
                  }
                }
              ]
            }
          }
        }
        """;
        var service = AniListHelper.Create(json);

        var result = await service.LookupSeriesAsync("Solo Leveling");

        Assert.NotNull(result);
        Assert.Equal("https://anilist.example/extra.jpg", result!.ImageUrl);
        Assert.Equal("https://anilist.example/medium.jpg", result.ThumbnailUrl);
    }

    [Fact]
    public async Task AniList_ReturnsNullImageUrl_WhenCoverMissing()
    {
        var json = """
        {
          "data": {
            "Page": {
              "media": [
                {
                  "title": { "english": "X" },
                  "synonyms": []
                }
              ]
            }
          }
        }
        """;
        var service = AniListHelper.Create(json);

        var result = await service.LookupSeriesAsync("X");

        Assert.NotNull(result);
        Assert.Null(result!.ImageUrl);
    }

    private static class ComicVineHelper
    {
        public static ComicVineSeriesMetadataService Create(string responseJson)
        {
            var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            });
            var httpClientFactory = new Mock<IHttpClientFactory>();
            httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>()))
                .Returns(new HttpClient(handler));
            var settings = new Mock<IOptionsMonitor<AppSettings>>();
            settings.Setup(s => s.CurrentValue).Returns(new AppSettings
            {
                EnableExternalSeriesMetadata = true,
                ComicVineApiKey = "k",
                ComicVineBaseUrl = "https://comicvine.example/api"
            });
            return new ComicVineSeriesMetadataService(
                httpClientFactory.Object,
                settings.Object,
                new MemoryCache(new MemoryCacheOptions()),
                Mock.Of<ILogger<ComicVineSeriesMetadataService>>());
        }
    }

    private static class MangaDexHelper
    {
        public static MangaDexSeriesMetadataService Create(string responseJson)
        {
            var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            });
            var httpClientFactory = new Mock<IHttpClientFactory>();
            httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>()))
                .Returns(new HttpClient(handler));
            var settings = new Mock<IOptionsMonitor<AppSettings>>();
            settings.Setup(s => s.CurrentValue).Returns(new AppSettings
            {
                EnableMangaDexMetadata = true,
                MangaDexBaseUrl = "https://api.mangadex.org"
            });
            return new MangaDexSeriesMetadataService(
                httpClientFactory.Object,
                settings.Object,
                new MemoryCache(new MemoryCacheOptions()),
                Mock.Of<ILogger<MangaDexSeriesMetadataService>>());
        }
    }

    private static class AniListHelper
    {
        public static AniListManhwaSeriesMetadataService Create(string responseJson)
        {
            var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            });
            var httpClientFactory = new Mock<IHttpClientFactory>();
            httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>()))
                .Returns(new HttpClient(handler));
            var settings = new Mock<IOptionsMonitor<AppSettings>>();
            settings.Setup(s => s.CurrentValue).Returns(new AppSettings
            {
                EnableAniListManhwaMetadata = true,
                AniListBaseUrl = "https://graphql.anilist.co"
            });
            return new AniListManhwaSeriesMetadataService(
                httpClientFactory.Object,
                settings.Object,
                new MemoryCache(new MemoryCacheOptions()),
                Mock.Of<ILogger<AniListManhwaSeriesMetadataService>>());
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }
}
