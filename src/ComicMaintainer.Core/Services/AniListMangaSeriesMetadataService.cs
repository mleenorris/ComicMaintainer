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
        : this(httpClientFactory, settings, cache, logger, metrics, rateLimiter: null)
    {
    }

    /// <summary>Creates the provider with its own throttle instead of the shared AniList one (tests only).</summary>
    internal AniListMangaSeriesMetadataService(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<AppSettings> settings,
        IMemoryCache cache,
        ILogger<AniListMangaSeriesMetadataService> logger,
        ComicMaintainerMetrics? metrics,
        ProviderRateLimiter? rateLimiter)
        : base("AniListManga", "anilist-manga", "JP", "ja", httpClientFactory, settings, cache, logger, metrics, rateLimiter)
    {
    }

    private protected override string HttpClientName => nameof(AniListMangaSeriesMetadataService);
}
