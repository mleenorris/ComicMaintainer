using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Telemetry;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Shared implementation of the AniList GraphQL providers. Each concrete
/// provider restricts searches to one <c>countryOfOrigin</c> (JP for manga,
/// KR for manhwa) and tags native titles with that country's language. Both
/// share one client-side rate limiter, because AniList's per-IP limit covers
/// every query against <c>graphql.anilist.co</c>.
/// </summary>
public abstract class AniListSeriesMetadataProviderBase : ExternalSeriesMetadataProviderBase
{
    // If a real lookup/search succeeded within this window we trust that
    // signal over the synthetic reachability probe, which can spuriously
    // report unreachable (e.g. transient network blip, AniList rejecting
    // the probe shape) while normal traffic still works.
    private static readonly TimeSpan RecentSuccessWindow = TimeSpan.FromMinutes(5);

    // The fields read for every candidate: the title in romaji/english/native
    // plus synonyms for a rich alias list, the description and cover images.
    private const string MediaFields = """
              id
              title { romaji english native }
              synonyms
              description(asHtml: false)
              coverImage { extraLarge large medium }
        """;

    // Lightweight query used for the reachability probe. Asks for a single
    // empty page so we don't pull any real data.
    private const string ProbeQuery = """
        query { Page(perPage: 1) { pageInfo { total } } }
        """;

    private readonly string _nativeLanguage;
    private readonly string _cacheName;
    private readonly string _searchQuery;
    private readonly Lazy<ProviderRateLimiter> _rateLimiter;

    /// <param name="providerName">Name reported as the provider and stored as the match source.</param>
    /// <param name="cacheName">Prefix of the provider's cache keys, e.g. <c>anilist-manga</c>.</param>
    /// <param name="countryOfOrigin">AniList country filter, e.g. <c>JP</c> or <c>KR</c>.</param>
    /// <param name="nativeLanguage">BCP-47 primary tag of the native titles, e.g. <c>ja</c> or <c>ko</c>.</param>
    /// <param name="rateLimiter">
    /// Throttle to use instead of the process-wide AniList limiter (tests only).
    /// </param>
    private protected AniListSeriesMetadataProviderBase(
        string providerName,
        string cacheName,
        string countryOfOrigin,
        string nativeLanguage,
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<AppSettings> settings,
        IMemoryCache cache,
        ILogger logger,
        ComicMaintainerMetrics? metrics,
        ProviderRateLimiter? rateLimiter = null)
        : base(providerName, httpClientFactory, settings, cache, logger, metrics)
    {
        _nativeLanguage = nativeLanguage;
        _cacheName = cacheName;
        // Constrain the search to MANGA media from the provider's country.
        _searchQuery = $$"""
            query ($search: String, $perPage: Int) {
              Page(perPage: $perPage) {
                media(search: $search, type: MANGA, countryOfOrigin: "{{countryOfOrigin}}", sort: SEARCH_MATCH) {
            {{MediaFields}}
                }
              }
            }
            """;
        _rateLimiter = new Lazy<ProviderRateLimiter>(
            () => rateLimiter ?? SharedRateLimiters.GetOrCreateAniList(Settings.CurrentValue.AniListRequestsPerMinute),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    private protected override string CacheKeyPrefix => $"{_cacheName}-series";

    private protected override ProviderRateLimiter RateLimiter => _rateLimiter.Value;

    private protected override bool CanQuery(AppSettings settings) => settings.EnableAniListMetadata;

    private protected override HttpRequestMessage CreateSearchRequest(AppSettings settings, string query, int limit)
        => BuildGraphQlRequest(settings, _searchQuery, new Dictionary<string, object?>
        {
            ["search"] = query,
            // AniList caps perPage at 50 for the Page connection.
            ["perPage"] = limit
        });

    private protected override HttpRequestMessage CreateGetByIdRequest(AppSettings settings, string sourceId)
    {
        // An id that is not a number cannot exist; 0 makes AniList answer 404.
        var id = long.TryParse(sourceId, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        return BuildGraphQlRequest(settings, $$"""
            query ($id: Int) {
              Media(id: $id, type: MANGA) {
            {{MediaFields}}
              }
            }
            """, new Dictionary<string, object?> { ["id"] = id });
    }

    private protected override IReadOnlyList<ExternalSeriesMetadata> ParseSearchResults(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("Page", out var page) ||
            page.ValueKind != JsonValueKind.Object ||
            !page.TryGetProperty("media", out var media) ||
            media.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ExternalSeriesMetadata>();
        }

        var output = new List<ExternalSeriesMetadata>();
        foreach (var item in media.EnumerateArray())
        {
            if (ParseMedia(item) is { } metadata)
            {
                output.Add(metadata);
            }
        }

        return output;
    }

    private protected override ExternalSeriesMetadata? ParseGetByIdResult(JsonElement root)
        => root.TryGetProperty("data", out var data)
           && data.ValueKind == JsonValueKind.Object
           && data.TryGetProperty("Media", out var media)
            ? ParseMedia(media)
            : null;

    /// <summary>
    /// GraphQL reports failures in an <c>errors</c> array. With no
    /// <c>data</c> alongside it the request failed, whatever the HTTP status;
    /// an error with status 429 is a throttle, and one with status 404 means
    /// "no such media" (answered as no result).
    /// </summary>
    private protected override ExternalMetadataUnavailableException? CheckResponseBody(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("errors", out var errors)
            || errors.ValueKind != JsonValueKind.Array
            || errors.GetArrayLength() == 0)
        {
            return null;
        }

        var hasData = root.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
            && data.EnumerateObject().Any(property => property.Value.ValueKind != JsonValueKind.Null);
        if (hasData)
        {
            return null;
        }

        var first = errors[0];
        var status = first.ValueKind == JsonValueKind.Object
                     && first.TryGetProperty("status", out var statusProperty)
                     && statusProperty.ValueKind == JsonValueKind.Number
                     && statusProperty.TryGetInt32(out var code)
            ? code
            : 0;
        if (status == 404)
        {
            return null;
        }

        var message = first.ValueKind == JsonValueKind.Object
                      && first.TryGetProperty("message", out var messageProperty)
                      && messageProperty.ValueKind == JsonValueKind.String
            ? messageProperty.GetString()
            : null;
        return new ExternalMetadataUnavailableException(
            ProviderName,
            status == 429 ? ExternalMetadataFailureKind.RateLimited : ExternalMetadataFailureKind.InvalidResponse,
            $"AniList answered with an error: {message ?? "unknown error"}");
    }

    public override async Task<ProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        var config = Settings.CurrentValue;
        var enabled = config.EnableAniListMetadata;
        var configured = enabled && !string.IsNullOrWhiteSpace(config.AniListBaseUrl);

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
            snapshot.StatusMessage = "Missing base URL";
            return snapshot;
        }

        await ApplyProbeAsync(
            snapshot,
            $"{_cacheName}-health-probe",
            () => BuildGraphQlRequest(config, ProbeQuery, variables: null),
            HealthCacheDuration,
            cancellationToken);

        // If the synthetic probe says we can't reach AniList but a real
        // lookup succeeded very recently, trust the real traffic — otherwise
        // the UI shows red even though metadata matching is working fine.
        if (snapshot.Reachable != true &&
            snapshot.LastSuccessUtc.HasValue &&
            DateTime.UtcNow - snapshot.LastSuccessUtc.Value <= RecentSuccessWindow)
        {
            snapshot.Reachable = true;
            snapshot.StatusMessage = "Reachable (recent lookup succeeded)";
        }

        // Override status when rate-limited so the UI shows a degraded badge.
        if (snapshot.RateLimited)
        {
            snapshot.StatusMessage = "Degraded - rate limited";
        }

        return snapshot;
    }

    private static HttpRequestMessage BuildGraphQlRequest(
        AppSettings settings,
        string query,
        IDictionary<string, object?>? variables)
    {
        var endpoint = settings.AniListBaseUrl.TrimEnd('/');
        var payload = new Dictionary<string, object?>
        {
            ["query"] = query
        };
        if (variables is not null)
        {
            payload["variables"] = variables;
        }

        var json = JsonSerializer.Serialize(payload);
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    /// <summary>Maps one AniList media object to a candidate; null when it has no title.</summary>
    private ExternalSeriesMetadata? ParseMedia(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var localizedTitles = ExtractLocalizedTitles(item, _nativeLanguage);
        var canonicalTitle = localizedTitles.FirstOrDefault()?.Title;
        if (string.IsNullOrWhiteSpace(canonicalTitle))
        {
            return null;
        }

        var aliases = localizedTitles.Skip(1).Select(lt => lt.Title).ToList();
        var (imageUrl, thumbnailUrl) = ExtractCoverImage(item);

        return new ExternalSeriesMetadata
        {
            CanonicalTitle = canonicalTitle,
            Aliases = aliases,
            Source = ProviderName,
            SourceId = ReadId(item),
            ImageUrl = imageUrl,
            ThumbnailUrl = thumbnailUrl,
            Synopsis = ExtractSynopsis(item),
            LocalizedTitles = localizedTitles
        };
    }

    /// <summary>Reads the AniList <c>description</c> field as plain text.</summary>
    private static string? ExtractSynopsis(JsonElement media)
    {
        if (media.TryGetProperty("description", out var description)
            && description.ValueKind == JsonValueKind.String)
        {
            return SynopsisTextNormalizer.Normalize(description.GetString());
        }
        return null;
    }

    /// <summary>
    /// Pulls the AniList coverImage object: prefers extraLarge / large for the
    /// poster image and the medium variant for the thumbnail.
    /// </summary>
    private static (string? Image, string? Thumbnail) ExtractCoverImage(JsonElement media)
    {
        if (!media.TryGetProperty("coverImage", out var cover) || cover.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        string? PickFirst(params string[] keys) => keys
            .Select(key => cover.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null)
            .FirstOrDefault(str => !string.IsNullOrWhiteSpace(str));

        return (PickFirst("extraLarge", "large", "medium"), PickFirst("medium", "large"));
    }

    /// <summary>
    /// Build the localized title list for a single AniList media item. The
    /// resulting list is ordered so the most user-friendly title comes first
    /// (English → Romaji → Native), with synonyms appended at the end as
    /// untagged entries (the AniList synonyms field doesn't include
    /// language tags).
    /// </summary>
    /// <param name="nativeLanguage">
    /// BCP-47 primary tag for the <c>native</c> title of this provider:
    /// "ja" for Manga (Japanese), "ko" for Manhwa (Korean).
    /// </param>
    private static List<LocalizedTitle> ExtractLocalizedTitles(JsonElement media, string nativeLanguage)
    {
        var result = new List<LocalizedTitle>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddIfNew(string? value, string? language)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (seen.Add(value))
            {
                result.Add(new LocalizedTitle(value, language));
            }
        }

        if (media.TryGetProperty("title", out var titleObj) && titleObj.ValueKind == JsonValueKind.Object)
        {
            // Prefer English (most familiar to library users) first, then
            // romaji (canonical Latin transliteration), then native script.
            // Romaji is the Latin-script form of the native language so we
            // tag it as e.g. "ja-Latn" / "ko-Latn".
            string? Get(string key) =>
                titleObj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString()
                    : null;

            AddIfNew(Get("english"), "en");
            AddIfNew(Get("romaji"), $"{nativeLanguage}-Latn");
            AddIfNew(Get("native"), nativeLanguage);
        }

        if (media.TryGetProperty("synonyms", out var synonyms) && synonyms.ValueKind == JsonValueKind.Array)
        {
            foreach (var synonym in synonyms.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.String))
            {
                AddIfNew(synonym.GetString(), null);
            }
        }

        return result;
    }
}
