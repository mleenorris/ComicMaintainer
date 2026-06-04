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
    private readonly Lazy<ProviderRateLimiter> _rateLimiter;

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
        _rateLimiter = new Lazy<ProviderRateLimiter>(
            () => new ProviderRateLimiter(
                "MangaDex",
                Math.Max(1, _settings.CurrentValue.MangaDexRequestsPerSecond),
                TimeSpan.FromSeconds(1)),
            LazyThreadSafetyMode.ExecutionAndPublication);
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
            using var response = await RateLimitedHttpInvoker.SendAsync(
                httpClient,
                () => new HttpRequestMessage(HttpMethod.Get, requestUri),
                _rateLimiter.Value,
                _health,
                _logger,
                "MangaDex",
                HttpCompletionOption.ResponseContentRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("MangaDex lookup failed for {SeriesName} with status code {StatusCode}", LoggingHelper.SanitizeForLog(seriesName), response.StatusCode);
                if ((int)response.StatusCode == 429)
                {
                    _health.RecordRateLimited(TimeSpan.FromSeconds(5), $"HTTP 429 {response.ReasonPhrase}");
                }
                else
                {
                    _health.RecordFailure($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                }
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
            if (snapshot.RateLimited)
            {
                snapshot.StatusMessage = "Degraded - rate limited";
            }
            return snapshot;
        }

        var (reachable, message) = await ProbeReachabilityAsync(config, cancellationToken);
        snapshot.Reachable = reachable;
        snapshot.StatusMessage = message;
        _cache.Set(probeCacheKey, (reachable, message), HealthCacheDuration);

        // If we have observed a 429 within the back-off window, surface a
        // "degraded - rate limited" status even when reachability checks pass.
        if (snapshot.RateLimited)
        {
            snapshot.StatusMessage = "Degraded - rate limited";
        }
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
            using var response = await RateLimitedHttpInvoker.SendAsync(
                httpClient,
                () =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    return request;
                },
                _rateLimiter.Value,
                _health,
                _logger,
                "MangaDex",
                HttpCompletionOption.ResponseHeadersRead,
                cts.Token);
            if (response.IsSuccessStatusCode)
            {
                return (true, "Reachable");
            }
            if ((int)response.StatusCode == 429)
            {
                return (false, "Rate limited (HTTP 429)");
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

            var localizedTitles = ExtractLocalizedTitles(attributes);
            var canonicalTitle = localizedTitles.FirstOrDefault()?.Title;
            if (string.IsNullOrWhiteSpace(canonicalTitle))
            {
                continue;
            }

            var aliases = localizedTitles.Skip(1).Select(lt => lt.Title).ToList();
            var (imageUrl, thumbnailUrl) = ExtractCoverUrls(item);

            output.Add(new ExternalSeriesMetadata
            {
                CanonicalTitle = canonicalTitle,
                Aliases = aliases,
                Source = "MangaDex",
                ImageUrl = imageUrl,
                ThumbnailUrl = thumbnailUrl,
                Synopsis = ExtractSynopsis(attributes),
                LocalizedTitles = localizedTitles
            });
        }

        return output;
    }

    /// <summary>
    /// Reads the MangaDex localized <c>description</c> object, preferring the
    /// English entry and falling back to the first available language.
    /// </summary>
    private static string? ExtractSynopsis(JsonElement attributes)
    {
        if (!attributes.TryGetProperty("description", out var description)
            || description.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? raw = null;
        if (description.TryGetProperty("en", out var enDesc) && enDesc.ValueKind == JsonValueKind.String)
        {
            raw = enDesc.GetString();
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            foreach (var property in description.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(property.Value.GetString()))
                {
                    raw = property.Value.GetString();
                    break;
                }
            }
        }

        return SynopsisTextNormalizer.Normalize(raw);
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
            // Defensive: filename is provider-controlled. Use Path.GetFileName
            // to robustly strip any directory components (covers the
            // null-byte, encoded-separator, and parent-directory variants
            // beyond simple Replace), then drop residual leading dots.
            fileName = Path.GetFileName(fileName)
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

    /// <summary>
    /// Build a language-tagged title list from a MangaDex manga attributes
    /// object. The MangaDex API keys both <c>title</c> and <c>altTitles</c>
    /// entries by BCP-47 language code (e.g. <c>en</c>, <c>ja</c>,
    /// <c>ja-ro</c>, <c>ko</c>, <c>zh</c>, <c>zh-hk</c>). We preserve those
    /// codes verbatim; the preferred-language matcher collapses regional
    /// variants to their primary subtag.
    /// <para>
    /// English titles are always promoted to the front of the list (whether
    /// they come from <c>title.en</c> or from an <c>altTitles</c> entry).
    /// Because <see cref="ParseResults"/> picks the first localized title as
    /// the candidate's <see cref="ExternalSeriesMetadata.CanonicalTitle"/>,
    /// this means a series whose primary MangaDex title is a romanization
    /// (e.g. Chinese pinyin) but has an English alt-title will default to
    /// the English alt-title as its display name.
    /// </para>
    /// </summary>
    private static List<LocalizedTitle> ExtractLocalizedTitles(JsonElement attributes)
    {
        var englishTitles = new List<LocalizedTitle>();
        var otherTitles = new List<LocalizedTitle>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddIfNew(string? value, string? language)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (!seen.Add(value)) return;
            var entry = new LocalizedTitle(value, language);
            if (IsEnglish(language))
            {
                englishTitles.Add(entry);
            }
            else
            {
                otherTitles.Add(entry);
            }
        }

        if (attributes.TryGetProperty("title", out var titleObj) && titleObj.ValueKind == JsonValueKind.Object)
        {
            // Process the explicit English variant first so that, even if a
            // provider quirk repeats the same string under another language,
            // the English entry is the one we keep.
            if (titleObj.TryGetProperty("en", out var enTitle) && enTitle.ValueKind == JsonValueKind.String)
            {
                AddIfNew(enTitle.GetString(), "en");
            }
            foreach (var property in titleObj.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String) continue;
                AddIfNew(property.Value.GetString(), property.Name);
            }
        }

        if (attributes.TryGetProperty("altTitles", out var altTitles) && altTitles.ValueKind == JsonValueKind.Array)
        {
            foreach (var altTitleObj in altTitles.EnumerateArray())
            {
                if (altTitleObj.ValueKind != JsonValueKind.Object) continue;
                foreach (var property in altTitleObj.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.String) continue;
                    AddIfNew(property.Value.GetString(), property.Name);
                }
            }
        }

        // English first (preserving insertion order within each group) so the
        // first entry — which becomes the candidate's CanonicalTitle — is the
        // English title whenever the provider supplied one.
        var result = new List<LocalizedTitle>(englishTitles.Count + otherTitles.Count);
        result.AddRange(englishTitles);
        result.AddRange(otherTitles);
        return result;
    }

    /// <summary>
    /// Returns true when the BCP-47 language tag represents English (primary
    /// subtag <c>en</c>), e.g. <c>en</c>, <c>en-US</c>, <c>en-GB</c>.
    /// </summary>
    private static bool IsEnglish(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return false;
        var primary = language.Split('-', 2)[0];
        return string.Equals(primary, "en", StringComparison.OrdinalIgnoreCase);
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
