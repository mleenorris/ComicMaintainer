using System.Net;
using System.Text.Json;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Core.Services;

public class ComicVineSeriesMetadataService : IExternalSeriesMetadataService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(12);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<AppSettings> _settings;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ComicVineSeriesMetadataService> _logger;

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
                _logger.LogWarning("ComicVine lookup failed for {SeriesName} with status code {StatusCode}", seriesName, response.StatusCode);
                return Array.Empty<ExternalSeriesMetadata>();
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
            return ParseResults(document.RootElement);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ComicVine lookup failed for {SeriesName}", seriesName);
            return Array.Empty<ExternalSeriesMetadata>();
        }
    }

    private static string BuildRequestUri(AppSettings settings, string seriesName, int limit)
    {
        var query = Uri.EscapeDataString(seriesName);
        var apiKey = Uri.EscapeDataString(settings.ComicVineApiKey ?? string.Empty);
        var baseUrl = settings.ComicVineBaseUrl.TrimEnd('/');
        return $"{baseUrl}/search/?api_key={apiKey}&format=json&resources=volume&limit={limit}&field_list=name,aliases&query={query}";
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

            output.Add(new ExternalSeriesMetadata
            {
                CanonicalTitle = canonicalTitle,
                Aliases = aliases,
                Source = "ComicVine"
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
