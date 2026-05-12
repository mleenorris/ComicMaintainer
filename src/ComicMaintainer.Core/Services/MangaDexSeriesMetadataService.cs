using System.Text.Json;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Core.Services;

public class MangaDexSeriesMetadataService : IExternalSeriesMetadataService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(12);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<AppSettings> _settings;
    private readonly IMemoryCache _cache;
    private readonly ILogger<MangaDexSeriesMetadataService> _logger;

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
                _logger.LogWarning("MangaDex lookup failed for {SeriesName} with status code {StatusCode}", seriesName, response.StatusCode);
                return Array.Empty<ExternalSeriesMetadata>();
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
            return ParseResults(document.RootElement);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MangaDex lookup failed for {SeriesName}", seriesName);
            return Array.Empty<ExternalSeriesMetadata>();
        }
    }

    private static string BuildRequestUri(AppSettings settings, string seriesName, int limit)
    {
        var query = Uri.EscapeDataString(seriesName);
        var baseUrl = settings.MangaDexBaseUrl.TrimEnd('/');
        return $"{baseUrl}/manga?title={query}&limit={limit}";
    }

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

            output.Add(new ExternalSeriesMetadata
            {
                CanonicalTitle = canonicalTitle,
                Aliases = aliases,
                Source = "MangaDex"
            });
        }

        return output;
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
