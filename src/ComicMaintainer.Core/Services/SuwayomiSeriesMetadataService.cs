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
/// External series metadata provider backed by a Suwayomi-Server sidecar.
///
/// Suwayomi-Server (<see href="https://github.com/Suwayomi/Suwayomi-Server"/>)
/// is a JVM-based port of Tachiyomi/Mihon that loads community-maintained
/// source "extensions" (MangaDex, Comick, Bato, MangaPlus, Weebcentral, etc.)
/// and exposes them through a single GraphQL API at <c>POST /api/graphql</c>.
///
/// This provider does NOT embed those extensions in-process; it talks to a
/// separate Suwayomi container over HTTP. The user is expected to deploy
/// Suwayomi-Server alongside ComicMaintainer (see <c>docker-compose.dotnet.yml</c>)
/// and to install whichever source extensions they want via Suwayomi's own UI
/// before enabling this provider.
/// </summary>
public class SuwayomiSeriesMetadataService : IExternalSeriesMetadataService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(12);
    private static readonly TimeSpan SourceListCacheDuration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan HealthCacheDuration = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PerSourceTimeout = TimeSpan.FromSeconds(15);

    private const int MaxResultsPerSource = 5;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<AppSettings> _settings;
    private readonly IMemoryCache _cache;
    private readonly ILogger<SuwayomiSeriesMetadataService> _logger;
    private readonly ProviderHealthTracker _health = new("Suwayomi");

    public SuwayomiSeriesMetadataService(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<AppSettings> settings,
        IMemoryCache cache,
        ILogger<SuwayomiSeriesMetadataService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings;
        _cache = cache;
        _logger = logger;
    }

    public string ProviderName => "Suwayomi";

    public async Task<ExternalSeriesMetadata?> LookupSeriesAsync(string seriesName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(seriesName))
        {
            return null;
        }

        var config = _settings.CurrentValue;
        if (!IsConfigured(config))
        {
            return null;
        }

        var cacheKey = $"suwayomi-series:{Normalize(seriesName)}";
        if (_cache.TryGetValue(cacheKey, out ExternalSeriesMetadata? cached))
        {
            return cached;
        }

        var results = await SearchInternalAsync(config, seriesName, MaxResultsPerSource, cancellationToken);
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
        if (!IsConfigured(config))
        {
            return Array.Empty<ExternalSeriesMetadata>();
        }

        var perSource = Math.Clamp(limit, 1, MaxResultsPerSource);
        var results = await SearchInternalAsync(config, query, perSource, cancellationToken);
        return results.Take(Math.Clamp(limit, 1, 50)).ToList();
    }

    public async Task<ProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        var config = _settings.CurrentValue;
        var enabled = config.EnableSuwayomiMetadata;
        var configured = enabled && !string.IsNullOrWhiteSpace(config.SuwayomiBaseUrl);

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

        const string probeCacheKey = "suwayomi-health-probe";
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

    private static bool IsConfigured(AppSettings config)
        => config.EnableSuwayomiMetadata && !string.IsNullOrWhiteSpace(config.SuwayomiBaseUrl);

    private async Task<IReadOnlyList<ExternalSeriesMetadata>> SearchInternalAsync(
        AppSettings config,
        string seriesName,
        int perSourceLimit,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<long> sourceIds;
        try
        {
            sourceIds = await GetSourceIdsAsync(config, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Suwayomi source discovery failed for {SeriesName}", LoggingHelper.SanitizeForLog(seriesName));
            _health.RecordFailure(ex.Message);
            return Array.Empty<ExternalSeriesMetadata>();
        }

        if (sourceIds.Count == 0)
        {
            _logger.LogDebug("Suwayomi has no sources configured; skipping search for {SeriesName}", LoggingHelper.SanitizeForLog(seriesName));
            return Array.Empty<ExternalSeriesMetadata>();
        }

        var aggregated = new List<ExternalSeriesMetadata>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sourceId in sourceIds)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                var sourceResults = await SearchSingleSourceAsync(config, sourceId, seriesName, perSourceLimit, cancellationToken);
                foreach (var item in sourceResults)
                {
                    var dedupKey = $"{item.Source}|{Normalize(item.CanonicalTitle)}";
                    if (seen.Add(dedupKey))
                    {
                        aggregated.Add(item);
                    }
                }
                _health.RecordSuccess();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Suwayomi source {SourceId} search failed for {SeriesName}", sourceId, LoggingHelper.SanitizeForLog(seriesName));
                _health.RecordFailure(ex.Message);
            }
        }

        return aggregated;
    }

    private async Task<IReadOnlyList<long>> GetSourceIdsAsync(AppSettings config, CancellationToken cancellationToken)
    {
        var configured = ParseSourceIds(config.SuwayomiSourceIds);
        if (configured.Count > 0)
        {
            return configured;
        }

        const string sourceListCacheKey = "suwayomi-source-list";
        if (_cache.TryGetValue(sourceListCacheKey, out IReadOnlyList<long>? cached) && cached != null)
        {
            return cached;
        }

        const string query = "query { sources { nodes { id } } }";
        using var document = await ExecuteGraphQlAsync(config, query, variables: null, cancellationToken);
        var ids = new List<long>();
        if (document.RootElement.TryGetProperty("data", out var data)
            && data.TryGetProperty("sources", out var sources)
            && sources.TryGetProperty("nodes", out var nodes)
            && nodes.ValueKind == JsonValueKind.Array)
        {
            foreach (var node in nodes.EnumerateArray())
            {
                if (TryReadSourceId(node, out var id))
                {
                    ids.Add(id);
                }
            }
        }

        _cache.Set(sourceListCacheKey, (IReadOnlyList<long>)ids, SourceListCacheDuration);
        return ids;
    }

    private async Task<IReadOnlyList<ExternalSeriesMetadata>> SearchSingleSourceAsync(
        AppSettings config,
        long sourceId,
        string seriesName,
        int perSourceLimit,
        CancellationToken cancellationToken)
    {
        const string mutation = """
            mutation Search($source: LongString!, $query: String!) {
              fetchSourceManga(input: { source: $source, type: SEARCH, page: 1, query: $query }) {
                mangas { title }
              }
            }
            """;

        // GraphQL LongString scalar accepts string-encoded 64-bit integers.
        var variables = new Dictionary<string, object?>
        {
            ["source"] = sourceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["query"] = seriesName,
        };

        using var perSourceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        perSourceCts.CancelAfter(PerSourceTimeout);

        using var document = await ExecuteGraphQlAsync(config, mutation, variables, perSourceCts.Token);
        var sourceLabel = $"Suwayomi:{sourceId}";

        if (!document.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("fetchSourceManga", out var payload)
            || !payload.TryGetProperty("mangas", out var mangas)
            || mangas.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ExternalSeriesMetadata>();
        }

        var output = new List<ExternalSeriesMetadata>();
        foreach (var manga in mangas.EnumerateArray())
        {
            if (output.Count >= perSourceLimit)
            {
                break;
            }
            if (manga.TryGetProperty("title", out var titleElement)
                && titleElement.ValueKind == JsonValueKind.String)
            {
                var title = titleElement.GetString();
                if (!string.IsNullOrWhiteSpace(title))
                {
                    output.Add(new ExternalSeriesMetadata
                    {
                        CanonicalTitle = title,
                        Aliases = new List<string>(),
                        Source = sourceLabel
                    });
                }
            }
        }
        return output;
    }

    private async Task<JsonDocument> ExecuteGraphQlAsync(
        AppSettings config,
        string query,
        IDictionary<string, object?>? variables,
        CancellationToken cancellationToken)
    {
        var endpoint = BuildGraphQlEndpoint(config);
        using var httpClient = _httpClientFactory.CreateClient(nameof(SuwayomiSeriesMetadataService));

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        ApplyAuthHeader(request, config);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var bodyObject = new Dictionary<string, object?> { ["query"] = query };
        if (variables != null)
        {
            bodyObject["variables"] = variables;
        }
        var bodyJson = JsonSerializer.Serialize(bodyObject);
        request.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Suwayomi GraphQL HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (document.RootElement.TryGetProperty("errors", out var errors)
            && errors.ValueKind == JsonValueKind.Array
            && errors.GetArrayLength() > 0)
        {
            var firstError = errors[0];
            var message = firstError.TryGetProperty("message", out var m) ? m.GetString() : "GraphQL error";
            document.Dispose();
            throw new InvalidOperationException($"Suwayomi GraphQL error: {message}");
        }

        return document;
    }

    private async Task<(bool reachable, string message)> ProbeReachabilityAsync(AppSettings config, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var document = await ExecuteGraphQlAsync(config, "query { sources { totalCount } }", variables: null, cts.Token);
            return (true, "Reachable");
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

    private static string BuildGraphQlEndpoint(AppSettings settings)
    {
        var baseUrl = settings.SuwayomiBaseUrl.TrimEnd('/');
        return $"{baseUrl}/api/graphql";
    }

    private static void ApplyAuthHeader(HttpRequestMessage request, AppSettings settings)
    {
        if (string.IsNullOrEmpty(settings.SuwayomiUsername) || string.IsNullOrEmpty(settings.SuwayomiPassword))
        {
            return;
        }

        var raw = $"{settings.SuwayomiUsername}:{settings.SuwayomiPassword}";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
    }

    private static List<long> ParseSourceIds(string raw)
    {
        var output = new List<long>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return output;
        }
        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (long.TryParse(token, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var id))
            {
                output.Add(id);
            }
        }
        return output;
    }

    private static bool TryReadSourceId(JsonElement node, out long id)
    {
        id = 0;
        if (!node.TryGetProperty("id", out var idElement))
        {
            return false;
        }

        // Suwayomi exposes source IDs via the GraphQL LongString scalar (string-encoded
        // 64-bit integer), but historically also as raw numbers. Accept both shapes.
        if (idElement.ValueKind == JsonValueKind.String)
        {
            return long.TryParse(idElement.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out id);
        }
        if (idElement.ValueKind == JsonValueKind.Number)
        {
            return idElement.TryGetInt64(out id);
        }
        return false;
    }

    private static ExternalSeriesMetadata? PickBestMatch(IReadOnlyList<ExternalSeriesMetadata> candidates, string requestedSeriesName)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        var requestedNormalized = Normalize(requestedSeriesName);
        ExternalSeriesMetadata? bestExact = null;

        foreach (var metadata in candidates)
        {
            if (Normalize(metadata.CanonicalTitle) == requestedNormalized)
            {
                bestExact ??= metadata;
            }
        }

        return bestExact ?? candidates[0];
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
