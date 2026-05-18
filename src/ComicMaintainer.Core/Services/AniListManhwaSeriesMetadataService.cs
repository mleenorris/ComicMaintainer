using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// External series metadata provider for Manhwa (Korean-origin comics) backed by
/// the public AniList GraphQL API. Results are filtered with
/// <c>countryOfOrigin: "KR"</c> so this provider complements the existing
/// MangaDex (manga) and ComicVine (US comics) providers in the composite chain.
/// </summary>
public class AniListManhwaSeriesMetadataService : IExternalSeriesMetadataService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(12);
    private static readonly TimeSpan HealthCacheDuration = TimeSpan.FromSeconds(60);

    // If a real lookup/search succeeded within this window we trust that
    // signal over the synthetic reachability probe, which can spuriously
    // report unreachable (e.g. transient network blip, AniList rejecting
    // the probe shape) while normal traffic still works.
    private static readonly TimeSpan RecentSuccessWindow = TimeSpan.FromMinutes(5);

    // AniList GraphQL search query. We request the title in romaji/english/native
    // plus synonyms so we can build a rich alias list, and constrain the search
    // to MANGA media originating from Korea (i.e. manhwa).
    private const string SearchQuery = """
        query ($search: String, $perPage: Int) {
          Page(perPage: $perPage) {
            media(search: $search, type: MANGA, countryOfOrigin: "KR", sort: SEARCH_MATCH) {
              title { romaji english native }
              synonyms
              coverImage { extraLarge large medium }
            }
          }
        }
        """;

    // Lightweight query used for the reachability probe. Asks for a single
    // empty page so we don't pull any real data.
    private const string ProbeQuery = """
        query { Page(perPage: 1) { pageInfo { total } } }
        """;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<AppSettings> _settings;
    private readonly IMemoryCache _cache;
    private readonly ILogger<AniListManhwaSeriesMetadataService> _logger;
    private readonly ProviderHealthTracker _health = new("AniListManhwa");

    public AniListManhwaSeriesMetadataService(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<AppSettings> settings,
        IMemoryCache cache,
        ILogger<AniListManhwaSeriesMetadataService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings;
        _cache = cache;
        _logger = logger;
    }

    public string ProviderName => "AniListManhwa";

    public async Task<ExternalSeriesMetadata?> LookupSeriesAsync(string seriesName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(seriesName))
        {
            return null;
        }

        var config = _settings.CurrentValue;
        if (!config.EnableAniListMetadata)
        {
            return null;
        }

        var cacheKey = $"anilist-manhwa-series:{Normalize(seriesName)}";
        if (_cache.TryGetValue(cacheKey, out ExternalSeriesMetadata? cached))
        {
            return cached;
        }

        var results = await SearchInternalAsync(config, seriesName, perPage: 10, cancellationToken);
        var result = PickBestMatch(results, seriesName);
        if (result != null)
        {
            _cache.Set(cacheKey, result, CacheDuration);
        }

        return result;
    }

    public async Task<IReadOnlyList<ExternalSeriesMetadata>> SearchSeriesAsync(
        string query,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<ExternalSeriesMetadata>();
        }

        var config = _settings.CurrentValue;
        if (!config.EnableAniListMetadata)
        {
            return Array.Empty<ExternalSeriesMetadata>();
        }

        // AniList caps perPage at 50 for the Page connection.
        return await SearchInternalAsync(config, query, Math.Clamp(limit, 1, 50), cancellationToken);
    }

    private async Task<IReadOnlyList<ExternalSeriesMetadata>> SearchInternalAsync(
        AppSettings config,
        string seriesName,
        int perPage,
        CancellationToken cancellationToken)
    {
        try
        {
            using var httpClient = _httpClientFactory.CreateClient(nameof(AniListManhwaSeriesMetadataService));
            using var request = BuildGraphQlRequest(config, SearchQuery, new Dictionary<string, object?>
            {
                ["search"] = seriesName,
                ["perPage"] = perPage
            });

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "AniList manhwa lookup failed for {SeriesName} with status code {StatusCode}",
                    LoggingHelper.SanitizeForLog(seriesName),
                    response.StatusCode);
                _health.RecordFailure($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                return Array.Empty<ExternalSeriesMetadata>();
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
            var results = ParseResults(document.RootElement);
            _health.RecordSuccess();
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AniList manhwa lookup failed for {SeriesName}", LoggingHelper.SanitizeForLog(seriesName));
            _health.RecordFailure(ex.Message);
            return Array.Empty<ExternalSeriesMetadata>();
        }
    }

    public async Task<ProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        var config = _settings.CurrentValue;
        var enabled = config.EnableAniListMetadata;
        var configured = enabled && !string.IsNullOrWhiteSpace(config.AniListBaseUrl);

        var snapshot = _health.Snapshot();
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

        const string probeCacheKey = "anilist-manhwa-health-probe";
        if (_cache.TryGetValue(probeCacheKey, out (bool Reachable, string Message)? cached) && cached.HasValue)
        {
            snapshot.Reachable = cached.Value.Reachable;
            snapshot.StatusMessage = cached.Value.Message;
        }
        else
        {
            var (reachable, message) = await ProbeReachabilityAsync(config, cancellationToken);
            snapshot.Reachable = reachable;
            snapshot.StatusMessage = message;
            _cache.Set(probeCacheKey, (reachable, message), HealthCacheDuration);
        }

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

        return snapshot;
    }

    private async Task<(bool reachable, string message)> ProbeReachabilityAsync(AppSettings config, CancellationToken cancellationToken)
    {
        try
        {
            using var httpClient = _httpClientFactory.CreateClient(nameof(AniListManhwaSeriesMetadataService));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var request = BuildGraphQlRequest(config, ProbeQuery, variables: null);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (response.IsSuccessStatusCode)
            {
                return (true, "Reachable");
            }
            return (false, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        catch (OperationCanceledException)
        {
            return (false, "Timed out");
        }
        catch (HttpRequestException ex)
        {
            return (false, $"Network error: {ex.Message}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
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

    private static IReadOnlyList<ExternalSeriesMetadata> ParseResults(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("Page", out var page) ||
            !page.TryGetProperty("media", out var media) ||
            media.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ExternalSeriesMetadata>();
        }

        var output = new List<ExternalSeriesMetadata>();
        foreach (var item in media.EnumerateArray())
        {
            var canonicalTitle = ExtractCanonicalTitle(item);
            if (string.IsNullOrWhiteSpace(canonicalTitle))
            {
                continue;
            }

            var aliases = ExtractAliases(item, canonicalTitle);
            var (imageUrl, thumbnailUrl) = ExtractCoverImage(item);

            output.Add(new ExternalSeriesMetadata
            {
                CanonicalTitle = canonicalTitle,
                Aliases = aliases,
                Source = "AniListManhwa",
                ImageUrl = imageUrl,
                ThumbnailUrl = thumbnailUrl
            });
        }

        return output;
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

        string? PickFirst(params string[] keys)
        {
            foreach (var key in keys)
            {
                if (cover.TryGetProperty(key, out var value)
                    && value.ValueKind == JsonValueKind.String)
                {
                    var str = value.GetString();
                    if (!string.IsNullOrWhiteSpace(str))
                    {
                        return str;
                    }
                }
            }
            return null;
        }

        return (PickFirst("extraLarge", "large", "medium"), PickFirst("medium", "large"));
    }

    private static ExternalSeriesMetadata? PickBestMatch(IReadOnlyList<ExternalSeriesMetadata> candidates, string requestedSeriesName)
    {
        var requestedNormalized = Normalize(requestedSeriesName);
        ExternalSeriesMetadata? bestExact = null;
        ExternalSeriesMetadata? bestAlias = null;

        foreach (var metadata in candidates)
        {
            if (Normalize(metadata.CanonicalTitle) == requestedNormalized)
            {
                bestExact ??= metadata;
                continue;
            }

            if (metadata.Aliases.Any(alias => Normalize(alias) == requestedNormalized))
            {
                bestAlias ??= metadata;
            }
        }

        return bestExact ?? bestAlias ?? candidates.FirstOrDefault();
    }

    private static string? ExtractCanonicalTitle(JsonElement media)
    {
        if (!media.TryGetProperty("title", out var titleObj) || titleObj.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // Prefer English, then romaji, then native (manhwa originals are Korean
        // and may not always have an English localization yet).
        foreach (var key in new[] { "english", "romaji", "native" })
        {
            if (titleObj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var str = value.GetString();
                if (!string.IsNullOrWhiteSpace(str))
                {
                    return str;
                }
            }
        }

        return null;
    }

    private static List<string> ExtractAliases(JsonElement media, string canonicalTitle)
    {
        var canonicalNormalized = Normalize(canonicalTitle);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var aliases = new List<string>();

        if (media.TryGetProperty("title", out var titleObj) && titleObj.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in titleObj.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }
                var value = property.Value.GetString();
                if (string.IsNullOrWhiteSpace(value) || Normalize(value) == canonicalNormalized)
                {
                    continue;
                }
                if (seen.Add(value))
                {
                    aliases.Add(value);
                }
            }
        }

        if (media.TryGetProperty("synonyms", out var synonyms) && synonyms.ValueKind == JsonValueKind.Array)
        {
            foreach (var synonym in synonyms.EnumerateArray())
            {
                if (synonym.ValueKind != JsonValueKind.String)
                {
                    continue;
                }
                var value = synonym.GetString();
                if (string.IsNullOrWhiteSpace(value) || Normalize(value) == canonicalNormalized)
                {
                    continue;
                }
                if (seen.Add(value))
                {
                    aliases.Add(value);
                }
            }
        }

        return aliases;
    }

    private static string Normalize(string value)
    {
        var filtered = value
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray();

        return new string(filtered);
    }
}
