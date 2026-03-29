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

    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<AppSettings> _settings;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ComicVineSeriesMetadataService> _logger;

    public ComicVineSeriesMetadataService(
        HttpClient httpClient,
        IOptionsMonitor<AppSettings> settings,
        IMemoryCache cache,
        ILogger<ComicVineSeriesMetadataService> logger)
    {
        _httpClient = httpClient;
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

        try
        {
            var requestUri = BuildRequestUri(config, seriesName);
            using var response = await _httpClient.GetAsync(requestUri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("ComicVine lookup failed for {SeriesName} with status code {StatusCode}", seriesName, response.StatusCode);
                return null;
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
            var result = ParseBestMatch(document.RootElement, seriesName);
            if (result != null)
            {
                _cache.Set(cacheKey, result, CacheDuration);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ComicVine lookup failed for {SeriesName}", seriesName);
            return null;
        }
    }

    private static string BuildRequestUri(AppSettings settings, string seriesName)
    {
        var query = Uri.EscapeDataString(seriesName);
        var apiKey = Uri.EscapeDataString(settings.ComicVineApiKey ?? string.Empty);
        var baseUrl = settings.ComicVineBaseUrl.TrimEnd('/');
        return $"{baseUrl}/search/?api_key={apiKey}&format=json&resources=volume&limit=10&field_list=name,aliases&query={query}";
    }

    private static ExternalSeriesMetadata? ParseBestMatch(JsonElement root, string requestedSeriesName)
    {
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var requestedNormalized = Normalize(requestedSeriesName);
        ExternalSeriesMetadata? bestExact = null;
        ExternalSeriesMetadata? bestAlias = null;

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

            var metadata = new ExternalSeriesMetadata
            {
                CanonicalTitle = canonicalTitle,
                Aliases = aliases,
                Source = "ComicVine"
            };

            if (Normalize(canonicalTitle) == requestedNormalized)
            {
                bestExact ??= metadata;
                continue;
            }

            if (aliases.Any(alias => Normalize(alias) == requestedNormalized))
            {
                bestAlias ??= metadata;
            }
        }

        return bestExact ?? bestAlias;
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
