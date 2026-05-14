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

public class MangaDexSeriesMetadataService : IExternalSeriesMetadataService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(12);
    private static readonly TimeSpan HealthCacheDuration = TimeSpan.FromSeconds(60);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<AppSettings> _settings;
    private readonly IMemoryCache _cache;
    private readonly ILogger<MangaDexSeriesMetadataService> _logger;
    private readonly ProviderHealthTracker _health = new("MangaDex");

    public MangaDexSeriesMetadataService(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<AppSettings> settings,
        IMemoryCache cache,
        ILogger<MangaDexSeriesMetadataService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings;
        _cache = cache;
        _logger = logger;
    }

    public string ProviderName => "MangaDex";

    public async Task<ExternalSeriesMetadata?> LookupSeriesAsync(string seriesName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(seriesName))
        {
            return null;
        }

        var config = _settings.CurrentValue;
        if (!config.EnableMangaDexMetadata)
        {
            return null;
        }

        var cacheKey = $"mangadex-series:{Normalize(seriesName)}";
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
        if (!config.EnableMangaDexMetadata)
        {
            return Array.Empty<ExternalSeriesMetadata>();
        }

        return await SearchInternalAsync(config, query, Math.Clamp(limit, 1, 50), cancellationToken);
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
            using var httpClient = _httpClientFactory.CreateClient(nameof(MangaDexSeriesMetadataService));
            using var response = await httpClient.GetAsync(requestUri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("MangaDex lookup failed for {SeriesName} with status code {StatusCode}", LoggingHelper.SanitizeForLog(seriesName), response.StatusCode);
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
            _logger.LogWarning(ex, "MangaDex lookup failed for {SeriesName}", LoggingHelper.SanitizeForLog(seriesName));
            _health.RecordFailure(ex.Message);
            return Array.Empty<ExternalSeriesMetadata>();
        }
    }

    public async Task<ProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        var config = _settings.CurrentValue;
        var enabled = config.EnableMangaDexMetadata;
        var configured = enabled && !string.IsNullOrWhiteSpace(config.MangaDexBaseUrl);

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

        const string probeCacheKey = "mangadex-health-probe";
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
            var requestUri = BuildRequestUri(config, "ping", limit: 1);
            using var httpClient = _httpClientFactory.CreateClient(nameof(MangaDexSeriesMetadataService));
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

    private static string BuildRequestUri(AppSettings settings, string seriesName, int limit)
    {
        var query = Uri.EscapeDataString(seriesName);
        var baseUrl = settings.MangaDexBaseUrl.TrimEnd('/');
        // Include the cover_art relationship so we can build a poster URL for
        // the local series-image cache without a follow-up request.
        return $"{baseUrl}/manga?title={query}&limit={limit}&includes[]=cover_art";
    }

    private const string MangaDexCdnBase = "https://uploads.mangadex.org";

    private static IReadOnlyList<ExternalSeriesMetadata> ParseResults(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ExternalSeriesMetadata>();
        }

        var output = new List<ExternalSeriesMetadata>();
        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("attributes", out var attributes))
            {
                continue;
            }

            var canonicalTitle = ExtractCanonicalTitle(attributes);
            if (string.IsNullOrWhiteSpace(canonicalTitle))
            {
                continue;
            }

            var aliases = ExtractAliases(attributes);
            var (imageUrl, thumbnailUrl) = ExtractCoverUrls(item);

            output.Add(new ExternalSeriesMetadata
            {
                CanonicalTitle = canonicalTitle,
                Aliases = aliases,
                Source = "MangaDex",
                ImageUrl = imageUrl,
                ThumbnailUrl = thumbnailUrl
            });
        }

        return output;
    }

    /// <summary>
    /// Locates the cover_art relationship in a MangaDex manga record and
    /// returns the public CDN URL of the cover (and a smaller thumbnail
    /// variant). Returns (null, null) when no cover relationship is present.
    /// </summary>
    private static (string? Image, string? Thumbnail) ExtractCoverUrls(JsonElement item)
    {
        if (!item.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.String)
        {
            return (null, null);
        }
        var mangaId = idProp.GetString();
        if (string.IsNullOrWhiteSpace(mangaId))
        {
            return (null, null);
        }

        if (!item.TryGetProperty("relationships", out var rels) || rels.ValueKind != JsonValueKind.Array)
        {
            return (null, null);
        }

        foreach (var rel in rels.EnumerateArray())
        {
            if (!rel.TryGetProperty("type", out var typeProp)
                || typeProp.ValueKind != JsonValueKind.String
                || !string.Equals(typeProp.GetString(), "cover_art", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!rel.TryGetProperty("attributes", out var coverAttrs)
                || coverAttrs.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            if (!coverAttrs.TryGetProperty("fileName", out var fileNameProp)
                || fileNameProp.ValueKind != JsonValueKind.String)
            {
                continue;
            }
            var fileName = fileNameProp.GetString();
            if (string.IsNullOrWhiteSpace(fileName))
            {
                continue;
            }
            // Defensive: filename is provider-controlled but should never
            // contain path separators or parent-directory references. Strip
            // them and any leading dots to keep the URL well-formed even if
            // the provider data is hostile.
            fileName = fileName
                .Replace("/", string.Empty)
                .Replace("\\", string.Empty)
                .Replace("..", string.Empty)
                .TrimStart('.');
            if (string.IsNullOrWhiteSpace(fileName))
            {
                continue;
            }
            var image = $"{MangaDexCdnBase}/covers/{Uri.EscapeDataString(mangaId)}/{Uri.EscapeDataString(fileName)}";
            // MangaDex supports server-side thumbnails by appending a size token.
            var thumb = $"{image}.256.jpg";
            return (image, thumb);
        }

        return (null, null);
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

    private static string? ExtractCanonicalTitle(JsonElement attributes)
    {
        if (!attributes.TryGetProperty("title", out var titleObj) || titleObj.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // Prefer English title, then fall back to first available
        if (titleObj.TryGetProperty("en", out var enTitle) && enTitle.ValueKind == JsonValueKind.String)
        {
            return enTitle.GetString();
        }

        foreach (var property in titleObj.EnumerateObject())
        {
            var value = property.Value.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static List<string> ExtractAliases(JsonElement attributes)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var aliases = new List<string>();

        if (!attributes.TryGetProperty("altTitles", out var altTitles) || altTitles.ValueKind != JsonValueKind.Array)
        {
            return aliases;
        }

        foreach (var altTitleObj in altTitles.EnumerateArray())
        {
            if (altTitleObj.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var property in altTitleObj.EnumerateObject())
            {
                var value = property.Value.GetString();
                if (!string.IsNullOrWhiteSpace(value) && seen.Add(value))
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
