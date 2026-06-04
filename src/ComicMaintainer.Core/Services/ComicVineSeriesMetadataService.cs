using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Core.Services;

public class ComicVineSeriesMetadataService : IExternalSeriesMetadataService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(12);
    private static readonly TimeSpan HealthCacheDuration = TimeSpan.FromSeconds(60);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<AppSettings> _settings;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ComicVineSeriesMetadataService> _logger;
    private readonly ProviderHealthTracker _health = new("ComicVine");

    public ComicVineSeriesMetadataService(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<AppSettings> settings,
        IMemoryCache cache,
        ILogger<ComicVineSeriesMetadataService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings;
        _cache = cache;
        _logger = logger;
    }

    public string ProviderName => "ComicVine";

    public async Task<ExternalSeriesMetadata?> LookupSeriesAsync(string seriesName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(seriesName))
        {
            return null;
        }

        var config = _settings.CurrentValue;
        if (!config.EnableExternalSeriesMetadata || string.IsNullOrWhiteSpace(config.ComicVineApiKey))
        {
            return null;
        }

        var cacheKey = $"comicvine-series:{Normalize(seriesName)}";
        if (_cache.TryGetValue(cacheKey, out ExternalSeriesMetadata? cached))
        {
            return cached;
        }

        var results = await SearchInternalAsync(config, seriesName, limit: 10, cancellationToken);
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
        if (!config.EnableExternalSeriesMetadata || string.IsNullOrWhiteSpace(config.ComicVineApiKey))
        {
            return Array.Empty<ExternalSeriesMetadata>();
        }

        return await SearchInternalAsync(config, query, Math.Clamp(limit, 1, 50), cancellationToken);
    }

    public async Task<ProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        var config = _settings.CurrentValue;
        var enabled = config.EnableExternalSeriesMetadata;
        var configured = enabled && !string.IsNullOrWhiteSpace(config.ComicVineApiKey)
            && !string.IsNullOrWhiteSpace(config.ComicVineBaseUrl);

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
            snapshot.StatusMessage = "Missing API key";
            return snapshot;
        }

        const string probeCacheKey = "comicvine-health-probe";
        if (_cache.TryGetValue(probeCacheKey, out (bool Reachable, string Message)? cached) && cached.HasValue)
        {
            snapshot.Reachable = cached.Value.Reachable;
            snapshot.StatusMessage = cached.Value.Message;
            return snapshot;
        }

        var (reachable, message) = await ProbeReachabilityAsync(config, cancellationToken);
        snapshot.Reachable = reachable;
        snapshot.StatusMessage = message;
        _cache.Set(probeCacheKey, (reachable, message), HealthCacheDuration);
        return snapshot;
    }

    private async Task<(bool reachable, string message)> ProbeReachabilityAsync(AppSettings config, CancellationToken cancellationToken)
    {
        try
        {
            // Use a tiny, well-formed search to verify the endpoint accepts our
            // credentials without consuming meaningful quota.
            var requestUri = BuildRequestUri(config, "ping", limit: 1);
            using var httpClient = _httpClientFactory.CreateClient(nameof(ComicVineSeriesMetadataService));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
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

    private async Task<IReadOnlyList<ExternalSeriesMetadata>> SearchInternalAsync(
        AppSettings config,
        string seriesName,
        int limit,
        CancellationToken cancellationToken)
    {
        try
        {
            var requestUri = BuildRequestUri(config, seriesName, limit);
            using var httpClient = _httpClientFactory.CreateClient(nameof(ComicVineSeriesMetadataService));
            using var response = await httpClient.GetAsync(requestUri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("ComicVine lookup failed for {SeriesName} with status code {StatusCode}", LoggingHelper.SanitizeForLog(seriesName), response.StatusCode);
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
            _logger.LogWarning(ex, "ComicVine lookup failed for {SeriesName}", LoggingHelper.SanitizeForLog(seriesName));
            _health.RecordFailure(ex.Message);
            return Array.Empty<ExternalSeriesMetadata>();
        }
    }

    private static string BuildRequestUri(AppSettings settings, string seriesName, int limit)
    {
        var query = Uri.EscapeDataString(seriesName);
        var apiKey = Uri.EscapeDataString(settings.ComicVineApiKey ?? string.Empty);
        var baseUrl = settings.ComicVineBaseUrl.TrimEnd('/');
        return $"{baseUrl}/search/?api_key={apiKey}&format=json&resources=volume&limit={limit}&field_list=name,aliases,image,deck,description&query={query}";
    }

    private static IReadOnlyList<ExternalSeriesMetadata> ParseResults(JsonElement root)
    {
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ExternalSeriesMetadata>();
        }

        var output = new List<ExternalSeriesMetadata>();
        foreach (var item in results.EnumerateArray())
        {
            var canonicalTitle = item.TryGetProperty("name", out var nameProperty)
                ? nameProperty.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(canonicalTitle))
            {
                continue;
            }

            var aliases = item.TryGetProperty("aliases", out var aliasesProperty)
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
            foreach (var alias in aliases)
            {
                if (!string.IsNullOrWhiteSpace(alias)
                    && !localizedTitles.Any(lt => string.Equals(lt.Title, alias, StringComparison.OrdinalIgnoreCase)))
                {
                    localizedTitles.Add(new LocalizedTitle(alias, "en"));
                }
            }

            output.Add(new ExternalSeriesMetadata
            {
                CanonicalTitle = canonicalTitle,
                Aliases = aliases,
                Source = "ComicVine",
                ImageUrl = imageUrl,
                ThumbnailUrl = thumbnailUrl,
                Synopsis = ExtractSynopsis(item),
                LocalizedTitles = localizedTitles
            });
        }

        return output;
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

        string? PickFirst(params string[] keys)
        {
            foreach (var key in keys)
            {
                if (imageObj.TryGetProperty(key, out var value)
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

        var image = PickFirst("super_url", "medium_url", "original_url", "screen_url", "small_url");
        var thumb = PickFirst("thumb_url", "icon_url", "small_url");
        return (image, thumb);
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

    private static string Normalize(string value)
    {
        var filtered = value
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray();

        return new string(filtered);
    }
}
