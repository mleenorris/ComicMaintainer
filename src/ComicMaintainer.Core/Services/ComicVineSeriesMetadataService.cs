using ComicMaintainer.Core.Telemetry;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// External series metadata provider backed by the ComicVine API
/// (<c>/search</c> for volumes and <c>/volume/4050-{id}</c>). Requires
/// <see cref="AppSettings.EnableExternalSeriesMetadata"/> and an API key.
/// </summary>
/// <remarks>
/// ComicVine allows 200 requests per resource per hour and blocks clients
/// that send bursts ("velocity" detection), so requests are spaced at least a
/// second apart and limited to <see cref="AppSettings.ComicVineRequestsPerHour"/>;
/// once that budget is spent ComicVine reports itself rate limited (and is
/// skipped by the composite provider) until the window frees a permit.
/// Throttling answers (HTTP 420/429) and API error envelopes
/// (<c>status_code</c> other than 1) are failures, not empty results.
/// </remarks>
public class ComicVineSeriesMetadataService : ExternalSeriesMetadataProviderBase
{
    /// <summary>
    /// The health probe spends a search request from the hourly budget, so its
    /// result is reused much longer than for the other providers.
    /// </summary>
    private static readonly TimeSpan ComicVineHealthCacheDuration = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Back-off reported for an API rate-limit envelope (status 107), which
    /// lasts until ComicVine's hourly window moves on.
    /// </summary>
    private static readonly TimeSpan RateLimitEnvelopeBackoff = TimeSpan.FromMinutes(15);

    private const string FieldList = "id,name,aliases,image,deck,description,site_detail_url,count_of_issues";

    // ComicVine status_code values (https://comicvine.gamespot.com/api/documentation).
    private const int StatusOk = 1;
    private const int StatusInvalidApiKey = 100;
    private const int StatusObjectNotFound = 101;
    private const int StatusRateLimitExceeded = 107;

    private readonly Lazy<ProviderRateLimiter> _rateLimiter;

    public ComicVineSeriesMetadataService(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<AppSettings> settings,
        IMemoryCache cache,
        ILogger<ComicVineSeriesMetadataService> logger,
        ComicMaintainerMetrics? metrics = null)
        : this(httpClientFactory, settings, cache, logger, metrics, rateLimiter: null)
    {
    }

    /// <summary>Creates the provider with the given throttle instead of ComicVine's own (tests only).</summary>
    internal ComicVineSeriesMetadataService(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<AppSettings> settings,
        IMemoryCache cache,
        ILogger<ComicVineSeriesMetadataService> logger,
        ComicMaintainerMetrics? metrics,
        ProviderRateLimiter? rateLimiter)
        : base("ComicVine", httpClientFactory, settings, cache, logger, metrics)
    {
        _rateLimiter = new Lazy<ProviderRateLimiter>(
            () => rateLimiter ?? new ProviderRateLimiter(
                "ComicVine",
                permitLimit: 1,
                window: TimeSpan.FromSeconds(1),
                segmentsPerWindow: 1,
                budgetPermitLimit: Math.Max(1, Settings.CurrentValue.ComicVineRequestsPerHour),
                budgetWindow: TimeSpan.FromHours(1)),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    private protected override string HttpClientName => nameof(ComicVineSeriesMetadataService);

    private protected override string CacheKeyPrefix => "comicvine-series";

    private protected override ProviderRateLimiter RateLimiter => _rateLimiter.Value;

    private protected override bool CanQuery(AppSettings settings)
        => settings.EnableExternalSeriesMetadata && !string.IsNullOrWhiteSpace(settings.ComicVineApiKey);

    /// <summary>ComicVine answers HTTP 420 when it throttles a client, as well as the standard 429.</summary>
    private protected override bool IsRateLimitStatus(HttpStatusCode statusCode)
        => (int)statusCode == 420 || statusCode == HttpStatusCode.TooManyRequests;

    private protected override HttpRequestMessage CreateSearchRequest(AppSettings settings, string query, int limit)
        => CreateJsonRequest(BuildSearchUri(settings, query, limit));

    private protected override HttpRequestMessage CreateGetByIdRequest(AppSettings settings, string sourceId)
    {
        var apiKey = Uri.EscapeDataString(settings.ComicVineApiKey ?? string.Empty);
        var baseUrl = settings.ComicVineBaseUrl.TrimEnd('/');
        return CreateJsonRequest(
            $"{baseUrl}/volume/4050-{Uri.EscapeDataString(sourceId)}/?api_key={apiKey}&format=json&field_list={FieldList}");
    }

    private protected override IReadOnlyList<ExternalSeriesMetadata> ParseSearchResults(JsonElement root)
    {
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ExternalSeriesMetadata>();
        }

        return results.EnumerateArray().Select(item => ParseVolume(item)).OfType<ExternalSeriesMetadata>().ToList();
    }

    /// <summary>A volume detail answer carries the volume as the <c>results</c> object.</summary>
    private protected override ExternalSeriesMetadata? ParseGetByIdResult(JsonElement root)
        => root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Object
            ? ParseVolume(results)
            : null;

    /// <summary>
    /// Reads ComicVine's <c>status_code</c> envelope: anything other than 1
    /// (OK) is a failure, except 101 (object not found), which a by-id fetch
    /// answers as no result.
    /// </summary>
    private protected override ExternalMetadataUnavailableException? CheckResponseBody(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("status_code", out var statusProperty)
            || statusProperty.ValueKind != JsonValueKind.Number
            || !statusProperty.TryGetInt32(out var status)
            || status is StatusOk or StatusObjectNotFound)
        {
            return null;
        }

        var error = root.TryGetProperty("error", out var errorProperty) && errorProperty.ValueKind == JsonValueKind.String
            ? errorProperty.GetString()
            : null;
        var message = $"ComicVine answered status {status} ({error ?? "unknown error"})";
        return status switch
        {
            StatusInvalidApiKey => new ExternalMetadataUnavailableException(
                ProviderName, ExternalMetadataFailureKind.Configuration, $"{message}; check the ComicVine API key"),
            StatusRateLimitExceeded => new ExternalMetadataUnavailableException(
                ProviderName, ExternalMetadataFailureKind.RateLimited, message, RateLimitEnvelopeBackoff),
            _ => new ExternalMetadataUnavailableException(
                ProviderName, ExternalMetadataFailureKind.InvalidResponse, message),
        };
    }

    public override async Task<ProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        var config = Settings.CurrentValue;
        var enabled = config.EnableExternalSeriesMetadata;
        var configured = enabled && !string.IsNullOrWhiteSpace(config.ComicVineApiKey)
            && !string.IsNullOrWhiteSpace(config.ComicVineBaseUrl);

        var snapshot = Health.Snapshot();
        snapshot.Enabled = enabled;
        snapshot.Configured = configured;

        if (!enabled)
        {
            snapshot.Reachable = null;
            snapshot.StatusMessage = "Disabled in settings";
            return snapshot;
        }
        if (!configured)
        {
            snapshot.Reachable = null;
            snapshot.StatusMessage = "Missing API key";
            return snapshot;
        }

        // Use a tiny, well-formed search to verify the endpoint accepts our
        // credentials without consuming meaningful quota.
        await ApplyProbeAsync(
            snapshot,
            "comicvine-health-probe",
            () => CreateJsonRequest(BuildSearchUri(config, "ping", limit: 1)),
            ComicVineHealthCacheDuration,
            cancellationToken);

        if (snapshot.RateLimited)
        {
            snapshot.StatusMessage = "Degraded - rate limited";
        }
        return snapshot;
    }

    private static HttpRequestMessage CreateJsonRequest(string requestUri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static string BuildSearchUri(AppSettings settings, string seriesName, int limit)
    {
        var query = Uri.EscapeDataString(seriesName);
        var apiKey = Uri.EscapeDataString(settings.ComicVineApiKey ?? string.Empty);
        var baseUrl = settings.ComicVineBaseUrl.TrimEnd('/');
        return $"{baseUrl}/search/?api_key={apiKey}&format=json&resources=volume&limit={limit}&field_list={FieldList}&query={query}";
    }

    /// <summary>Maps one ComicVine volume to a candidate; null when it has no name.</summary>
    private static ExternalSeriesMetadata? ParseVolume(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var canonicalTitle = item.TryGetProperty("name", out var nameProperty) && nameProperty.ValueKind == JsonValueKind.String
            ? nameProperty.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(canonicalTitle))
        {
            return null;
        }

        var aliases = item.TryGetProperty("aliases", out var aliasesProperty) && aliasesProperty.ValueKind == JsonValueKind.String
            ? ParseAliases(aliasesProperty.GetString())
            : new List<string>();

        var (imageUrl, thumbnailUrl) = ExtractImageUrls(item);

        // ComicVine catalogs the English title of a volume; tag every
        // entry as English so it participates in the preferred-language
        // resolver when the user picks "en".
        var localizedTitles = new List<LocalizedTitle>
        {
            new LocalizedTitle(canonicalTitle, "en")
        };
        // Except also drops repeated aliases (case-insensitively), keeping the first spelling.
        localizedTitles.AddRange(aliases
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Except(new[] { canonicalTitle }, StringComparer.OrdinalIgnoreCase)
            .Select(alias => new LocalizedTitle(alias, "en")));

        return new ExternalSeriesMetadata
        {
            CanonicalTitle = canonicalTitle,
            Aliases = aliases,
            Source = "ComicVine",
            SourceId = ReadId(item),
            ImageUrl = imageUrl,
            ThumbnailUrl = thumbnailUrl,
            Synopsis = ExtractSynopsis(item),
            // ComicVine volumes have no genre, status or origin. Without a status the issue
            // count, which is only a count so far, is never written as a series total.
            SiteUrl = ProviderMetadataText.HttpUrl(ReadString(item, "site_detail_url")),
            TotalChapters = ReadPositiveInt(item, "count_of_issues"),
            LocalizedTitles = localizedTitles
        };
    }

    /// <summary>
    /// Reads the ComicVine description, preferring the short plain-text
    /// <c>deck</c> and falling back to the longer HTML <c>description</c>.
    /// </summary>
    private static string? ExtractSynopsis(JsonElement item)
    {
        if (item.TryGetProperty("deck", out var deck)
            && deck.ValueKind == JsonValueKind.String)
        {
            var normalizedDeck = SynopsisTextNormalizer.Normalize(deck.GetString());
            if (!string.IsNullOrWhiteSpace(normalizedDeck))
            {
                return normalizedDeck;
            }
        }

        if (item.TryGetProperty("description", out var description)
            && description.ValueKind == JsonValueKind.String)
        {
            return SynopsisTextNormalizer.Normalize(description.GetString());
        }

        return null;
    }

    /// <summary>
    /// Reads the ComicVine "image" object, which contains several pre-sized
    /// variants. We prefer the larger formats for the main image and the
    /// thumb/icon for the optional thumbnail.
    /// </summary>
    private static (string? Image, string? Thumbnail) ExtractImageUrls(JsonElement item)
    {
        if (!item.TryGetProperty("image", out var imageObj) || imageObj.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        string? PickFirst(params string[] keys) => keys
            .Select(key => imageObj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null)
            .FirstOrDefault(str => !string.IsNullOrWhiteSpace(str));

        var image = PickFirst("super_url", "medium_url", "original_url", "screen_url", "small_url");
        var thumb = PickFirst("thumb_url", "icon_url", "small_url");
        return (image, thumb);
    }

    private static List<string> ParseAliases(string? aliases)
    {
        if (string.IsNullOrWhiteSpace(aliases))
        {
            return new List<string>();
        }

        return aliases
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
