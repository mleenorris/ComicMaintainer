using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Telemetry;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// External series metadata provider for Manhwa (Korean-origin comics) backed by
/// the public AniList GraphQL API. Results are filtered with
/// <c>countryOfOrigin: "KR"</c> so this provider complements the
/// MangaDex (manga) and ComicVine (US comics) providers in the composite chain.
/// </summary>
public class AniListManhwaSeriesMetadataService : AniListSeriesMetadataProviderBase
{
    public AniListManhwaSeriesMetadataService(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<AppSettings> settings,
        IMemoryCache cache,
        ILogger<AniListManhwaSeriesMetadataService> logger,
        ComicMaintainerMetrics? metrics = null)
        : this(httpClientFactory, settings, cache, logger, metrics, rateLimiter: null)
    {
    }

    /// <summary>Creates the provider with its own throttle instead of the shared AniList one (tests only).</summary>
    internal AniListManhwaSeriesMetadataService(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<AppSettings> settings,
        IMemoryCache cache,
        ILogger<AniListManhwaSeriesMetadataService> logger,
        ComicMaintainerMetrics? metrics,
        ProviderRateLimiter? rateLimiter)
        : base("AniListManhwa", "anilist-manhwa", "KR", "ko", httpClientFactory, settings, cache, logger, metrics, rateLimiter)
    {
    }

    private protected override string HttpClientName => nameof(AniListManhwaSeriesMetadataService);
}
