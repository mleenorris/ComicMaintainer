using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Telemetry;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// External series metadata provider for Manga (Japanese-origin comics) backed by
/// the public AniList GraphQL API. Results are filtered with
/// <c>countryOfOrigin: "JP"</c>, complementing the sibling AniList Manhwa
/// provider (KR) and the MangaDex / ComicVine providers in the composite chain.
/// </summary>
public class AniListMangaSeriesMetadataService : AniListSeriesMetadataProviderBase
{
    public AniListMangaSeriesMetadataService(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<AppSettings> settings,
        IMemoryCache cache,
        ILogger<AniListMangaSeriesMetadataService> logger,
        ComicMaintainerMetrics? metrics = null)
        : base("AniListManga", "JP", "ja", httpClientFactory, settings, cache, logger, metrics)
    {
    }

    private protected override string HttpClientName => nameof(AniListMangaSeriesMetadataService);

    private protected override string CacheKeyPrefix => "anilist-manga-series";
}
