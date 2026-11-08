using System.Net;
using System.Text.Json;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Telemetry;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Shared plumbing for the HTTP-backed external metadata providers
/// (ComicVine, MangaDex, AniList): input and settings checks, the per-provider
/// lookup cache, rate-limited requests with a per-attempt timeout, the choice
/// of an automatic match, re-fetching a matched entry by its id, and the
/// mapping of every transport, HTTP and response-format error to
/// <see cref="ExternalMetadataUnavailableException"/>.
/// </summary>
/// <remarks>
/// A provider that answers with no usable result returns <c>null</c> (lookup)
/// or an empty list (search). A provider that fails to answer throws, so the
/// composite provider can back off from it and callers never record an outage
/// as "not found".
/// </remarks>
public abstract class ExternalSeriesMetadataProviderBase : IExternalSeriesMetadataService
{
    /// <summary>How long a matched lookup is served from the in-memory cache.</summary>
    protected static readonly TimeSpan CacheDuration = TimeSpan.FromHours(12);

    /// <summary>
    /// How long a lookup that found no match is remembered, so every file of an
    /// unmatched folder does not query the provider again during a batch.
    /// </summary>
    protected static readonly TimeSpan NotFoundCacheDuration = TimeSpan.FromMinutes(30);

    /// <summary>How long a reachability probe result is reused.</summary>
    protected static readonly TimeSpan HealthCacheDuration = TimeSpan.FromSeconds(60);

    /// <summary>Overall budget of a reachability probe, including any 429 retry.</summary>
    protected static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Longest a reachability probe waits for a client-side permit.</summary>
    private static readonly TimeSpan ProbePermitWait = TimeSpan.FromSeconds(2);

    /// <summary>Upper limit of the configurable per-attempt request timeout, in seconds.</summary>
    internal const int MaxRequestTimeoutSeconds = 30;

    /// <summary>Number of search results considered when choosing an automatic match.</summary>
    protected const int LookupCandidateCount = 10;

    /// <summary>Largest page size the providers accept for a search.</summary>
    protected const int MaxSearchLimit = 50;

    /// <summary>Rate-limit back-off recorded when the provider gave no Retry-After.</summary>
    private static readonly TimeSpan DefaultRateLimitBackoff = TimeSpan.FromSeconds(5);

    private readonly IHttpClientFactory _httpClientFactory;

    private protected ExternalSeriesMetadataProviderBase(
        string providerName,
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<AppSettings> settings,
        IMemoryCache cache,
        ILogger logger,
        ComicMaintainerMetrics? metrics)
    {
        ProviderName = providerName;
        _httpClientFactory = httpClientFactory;
        Settings = settings;
        Cache = cache;
        Logger = logger;
        Health = new ProviderHealthTracker(providerName, metrics);
    }

    /// <inheritdoc />
    public string ProviderName { get; }

    /// <summary>Current application settings.</summary>
    protected IOptionsMonitor<AppSettings> Settings { get; }

    /// <summary>Cache shared by lookups and reachability probes.</summary>
    protected IMemoryCache Cache { get; }

    /// <summary>Logger of the concrete provider.</summary>
    protected ILogger Logger { get; }

    /// <summary>Rolling success/failure counters surfaced by the health panel.</summary>
    private protected ProviderHealthTracker Health { get; }

    /// <summary>Named <see cref="HttpClient"/> registered for the provider.</summary>
    private protected abstract string HttpClientName { get; }

    /// <summary>Prefix of the provider's cache keys, e.g. <c>mangadex-series</c>.</summary>
    private protected abstract string CacheKeyPrefix { get; }

    /// <summary>Client-side throttle for the provider's API.</summary>
    private protected abstract ProviderRateLimiter RateLimiter { get; }

    /// <summary>
    /// True when the provider is enabled and configured well enough to send
    /// lookups. A disabled provider answers every lookup with "no match".
    /// </summary>
    private protected abstract bool CanQuery(AppSettings settings);

    /// <summary>Builds the search request for <paramref name="query"/>.</summary>
    private protected abstract HttpRequestMessage CreateSearchRequest(AppSettings settings, string query, int limit);

    /// <summary>Parses a successful search response into candidates, in the provider's order.</summary>
    private protected abstract IReadOnlyList<ExternalSeriesMetadata> ParseSearchResults(JsonElement root);

    /// <summary>Builds the request that fetches the entry with id <paramref name="sourceId"/>.</summary>
    private protected abstract HttpRequestMessage CreateGetByIdRequest(AppSettings settings, string sourceId);

    /// <summary>Parses a successful by-id response; null when it holds no entry.</summary>
    private protected abstract ExternalSeriesMetadata? ParseGetByIdResult(JsonElement root);

    /// <summary>
    /// Inspects a successfully parsed response body for an API-level error
    /// (an error envelope sent with HTTP 200). Returns the failure to throw,
    /// or null when the body carries results.
    /// </summary>
    private protected virtual ExternalMetadataUnavailableException? CheckResponseBody(JsonElement root) => null;

    /// <summary>True for the HTTP status codes the provider uses to throttle clients.</summary>
    private protected virtual bool IsRateLimitStatus(HttpStatusCode statusCode)
        => statusCode == HttpStatusCode.TooManyRequests;

    /// <inheritdoc />
    /// <remarks>
    /// Returns only a candidate that matches <paramref name="seriesName"/>
    /// exactly or by alias (see <see cref="SeriesMatchScorer.SelectAutomaticMatch"/>);
    /// a fuzzy top hit is "no match". Matches are cached for
    /// <see cref="CacheDuration"/>, misses for <see cref="NotFoundCacheDuration"/>;
    /// failures are not cached.
    /// </remarks>
    public async Task<ExternalSeriesMetadata?> LookupSeriesAsync(string seriesName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(seriesName))
        {
            return null;
        }

        var config = Settings.CurrentValue;
        if (!CanQuery(config))
        {
            return null;
        }

        var key = LookupCacheKey(seriesName);
        var cacheKey = $"{CacheKeyPrefix}:{key}";
        if (Cache.TryGetValue(cacheKey, out ExternalSeriesMetadata? cached))
        {
            return cached;
        }

        var missKey = MissCacheKey(key);
        if (Cache.TryGetValue(missKey, out _))
        {
            return null;
        }

        var results = await SearchCoreAsync(config, seriesName, LookupCandidateCount, maxPermitWait: null, cancellationToken);
        var match = SeriesMatchScorer.SelectAutomaticMatch(results, seriesName)?.Match;
        if (match != null)
        {
            Cache.Set(cacheKey, match, CacheDuration);
        }
        else
        {
            Cache.Set(missKey, true, NotFoundCacheDuration);
        }

        return match;
    }

    /// <inheritdoc />
    public void ForgetNoMatch(string seriesName)
    {
        if (!string.IsNullOrWhiteSpace(seriesName))
        {
            Cache.Remove(MissCacheKey(LookupCacheKey(seriesName)));
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// A user is waiting for the answer, so the wait for a client-side permit
    /// is limited to <see cref="RateLimitedHttpInvoker.InteractivePermitWait"/>.
    /// </remarks>
    public async Task<IReadOnlyList<ExternalSeriesMetadata>> SearchSeriesAsync(
        string query,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<ExternalSeriesMetadata>();
        }

        var config = Settings.CurrentValue;
        if (!CanQuery(config))
        {
            return Array.Empty<ExternalSeriesMetadata>();
        }

        return await SearchCoreAsync(
            config,
            query,
            Math.Clamp(limit, 1, MaxSearchLimit),
            RateLimitedHttpInvoker.InteractivePermitWait,
            cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// With a <paramref name="sourceId"/> the entry is fetched by id (null
    /// when the provider no longer has it); without one, legacy matches fall
    /// back to <see cref="LookupSeriesAsync"/> on this provider only.
    /// </remarks>
    public async Task<ExternalSeriesMetadata?> LookupMatchAsync(
        string source,
        string? sourceId,
        string title,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(source, ProviderName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var config = Settings.CurrentValue;
        if (!CanQuery(config))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(sourceId))
        {
            return await LookupSeriesAsync(title, cancellationToken);
        }

        var id = sourceId.Trim();
        return await SendAsync(
            config,
            () => CreateGetByIdRequest(config, id),
            $"{title} (id {id})",
            ParseGetByIdResult,
            maxPermitWait: null,
            cancellationToken,
            notFoundIsEmpty: true);
    }

    /// <inheritdoc />
    public abstract Task<ProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Per-attempt request timeout from the settings, clamped to 1 to
    /// <see cref="MaxRequestTimeoutSeconds"/> seconds (the provider HttpClients'
    /// own timeout is the same 30 seconds).
    /// </summary>
    internal static TimeSpan RequestTimeout(AppSettings settings)
        => TimeSpan.FromSeconds(Math.Clamp(settings.MetadataProviderTimeoutSeconds, 1, MaxRequestTimeoutSeconds));

    /// <summary>
    /// Lowercase letters and digits only, the same folding the match scorer
    /// applies, so every spelling it treats as equal shares a cache entry.
    /// </summary>
    private static string LookupCacheKey(string seriesName)
        => new(seriesName.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private string MissCacheKey(string lookupKey) => $"{CacheKeyPrefix}-miss:{lookupKey}";

    private protected HttpClient CreateClient() => _httpClientFactory.CreateClient(HttpClientName);

    private async Task<IReadOnlyList<ExternalSeriesMetadata>> SearchCoreAsync(
        AppSettings config,
        string query,
        int limit,
        TimeSpan? maxPermitWait,
        CancellationToken cancellationToken)
        => await SendAsync(
               config,
               () => CreateSearchRequest(config, query, limit),
               query,
               ParseSearchResults,
               maxPermitWait,
               cancellationToken)
           ?? Array.Empty<ExternalSeriesMetadata>();

    /// <summary>
    /// Sends a rate-limited, time-limited request and parses the JSON answer.
    /// Every way the request can fail (transport, timeout, non-success status,
    /// throttling, malformed or error body) is logged, recorded in the health
    /// counters and thrown as <see cref="ExternalMetadataUnavailableException"/>.
    /// Only the caller's own cancellation is rethrown as is.
    /// </summary>
    /// <param name="maxPermitWait">
    /// Longest wait for a client-side permit; null for the limiter's default,
    /// which covers its whole window.
    /// </param>
    /// <param name="notFoundIsEmpty">
    /// True when HTTP 404 means "no such entry" (a by-id fetch): it then
    /// returns null instead of failing.
    /// </param>
    private protected async Task<T?> SendAsync<T>(
        AppSettings config,
        Func<HttpRequestMessage> requestFactory,
        string subject,
        Func<JsonElement, T?> parse,
        TimeSpan? maxPermitWait,
        CancellationToken cancellationToken,
        bool notFoundIsEmpty = false)
        where T : class
    {
        try
        {
            using var httpClient = CreateClient();
            using var response = await RateLimitedHttpInvoker.SendAsync(
                httpClient,
                requestFactory,
                RateLimiter,
                Health,
                Logger,
                ProviderName,
                HttpCompletionOption.ResponseContentRead,
                RequestTimeout(config),
                cancellationToken,
                IsRateLimitStatus,
                maxPermitWait);

            if (notFoundIsEmpty && response.StatusCode == HttpStatusCode.NotFound)
            {
                Health.RecordSuccess();
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw CreateStatusFailure(response);
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            T? result;
            try
            {
                using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
                var bodyFailure = CheckResponseBody(document.RootElement);
                if (bodyFailure is not null)
                {
                    throw bodyFailure;
                }

                result = parse(document.RootElement);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
            {
                // JsonElement throws InvalidOperationException when the body
                // has an unexpected shape (e.g. a property read on a string).
                throw new ExternalMetadataUnavailableException(
                    ProviderName,
                    ExternalMetadataFailureKind.InvalidResponse,
                    $"{ProviderName} returned a response that could not be read: {ex.Message}",
                    innerException: ex);
            }

            Health.RecordSuccess();
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller gave up: neither "no match" nor a provider failure.
            throw;
        }
        catch (ExternalMetadataUnavailableException failure)
        {
            RecordFailure(failure, subject);
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A cancellation the caller did not ask for is a timeout (this is
            // how HttpClient reports its own Timeout, e.g. while reading the body).
            var detail = string.IsNullOrWhiteSpace(ex.Message) ? string.Empty : $": {ex.Message}";
            var failure = ex is OperationCanceledException
                ? new ExternalMetadataUnavailableException(
                    ProviderName,
                    ExternalMetadataFailureKind.Timeout,
                    $"{ProviderName} request timed out{detail}",
                    innerException: ex)
                : new ExternalMetadataUnavailableException(
                    ProviderName,
                    ExternalMetadataFailureKind.Unavailable,
                    $"{ProviderName} request failed{detail}",
                    innerException: ex);
            RecordFailure(failure, subject);
            throw failure;
        }
    }

    /// <summary>
    /// Maps a non-success HTTP status to the matching failure: the provider's
    /// throttling statuses to <see cref="ExternalMetadataFailureKind.RateLimited"/>
    /// (carrying its <c>Retry-After</c>), 401/403 to
    /// <see cref="ExternalMetadataFailureKind.Configuration"/>, and anything
    /// else to <see cref="ExternalMetadataFailureKind.Unavailable"/>.
    /// </summary>
    private protected ExternalMetadataUnavailableException CreateStatusFailure(HttpResponseMessage response)
    {
        var description = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd();
        if (IsRateLimitStatus(response.StatusCode))
        {
            return new ExternalMetadataUnavailableException(
                ProviderName,
                ExternalMetadataFailureKind.RateLimited,
                $"{ProviderName} rate limited the request ({description})",
                RateLimitedHttpInvoker.GetRetryAfter(response) is { } retryAfter && retryAfter > TimeSpan.Zero ? retryAfter : null);
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new ExternalMetadataUnavailableException(
                ProviderName,
                ExternalMetadataFailureKind.Configuration,
                $"{ProviderName} rejected the request ({description}); check its settings");
        }

        return new ExternalMetadataUnavailableException(
            ProviderName,
            ExternalMetadataFailureKind.Unavailable,
            $"{ProviderName} answered {description}");
    }

    private void RecordFailure(ExternalMetadataUnavailableException failure, string subject)
    {
        if (failure.Kind == ExternalMetadataFailureKind.Paced)
        {
            // Our own request pacing: nothing was sent, so the provider's
            // health is unaffected.
            Logger.LogDebug(
                "{Provider} lookup for {SeriesName} not sent: {Reason}",
                ProviderName,
                LoggingHelper.SanitizeForLog(subject),
                LoggingHelper.SanitizeForLog(failure.Message));
            return;
        }

        Logger.LogWarning(
            failure.InnerException,
            "{Provider} lookup failed for {SeriesName}: {Reason}",
            ProviderName,
            LoggingHelper.SanitizeForLog(subject),
            LoggingHelper.SanitizeForLog(failure.Message));
        if (failure.Kind == ExternalMetadataFailureKind.RateLimited)
        {
            Health.RecordRateLimited(
                failure.RetryAfter is { } retryAfter && retryAfter > TimeSpan.Zero ? retryAfter : DefaultRateLimitBackoff,
                failure.Message, recordFailedRequest: true);
        }
        else
        {
            Health.RecordFailure(failure.Message);
        }
    }

    /// <summary>
    /// Sends a reachability probe (bounded by <see cref="ProbeTimeout"/>) and
    /// describes the outcome for the health panel. Probe failures are not
    /// recorded as failed lookups. Reachability is unknown (null) when our own
    /// request pacing left no room to send the probe.
    /// </summary>
    private protected async Task<(bool? Reachable, string Message)> ProbeAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        try
        {
            using var httpClient = CreateClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(ProbeTimeout);
            using var response = await RateLimitedHttpInvoker.SendAsync(
                httpClient,
                requestFactory,
                RateLimiter,
                Health,
                Logger,
                ProviderName,
                HttpCompletionOption.ResponseHeadersRead,
                ProbeTimeout,
                cts.Token,
                IsRateLimitStatus,
                ProbePermitWait);
            if (response.IsSuccessStatusCode)
            {
                return (true, "Reachable");
            }
            if (IsRateLimitStatus(response.StatusCode))
            {
                return (false, $"Rate limited (HTTP {(int)response.StatusCode})");
            }
            return (false, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller gave up; that says nothing about the provider, so
            // nothing is cached and the next check probes again.
            throw;
        }
        catch (OperationCanceledException)
        {
            return (false, "Timed out");
        }
        catch (ExternalMetadataUnavailableException ex) when (ex.Kind == ExternalMetadataFailureKind.Timeout)
        {
            return (false, "Timed out");
        }
        catch (ExternalMetadataUnavailableException ex) when (ex.Kind == ExternalMetadataFailureKind.RateLimited)
        {
            return (false, "Rate limited");
        }
        catch (ExternalMetadataUnavailableException ex) when (ex.Kind == ExternalMetadataFailureKind.Paced)
        {
            return (null, "Not checked: requests are at the local pace limit");
        }
        catch (HttpRequestException ex)
        {
            return (false, $"Network error: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Fills the reachability part of a health snapshot from the cached or a
    /// fresh probe result.
    /// </summary>
    private protected async Task ApplyProbeAsync(
        ProviderHealth snapshot,
        string probeCacheKey,
        Func<HttpRequestMessage> probeRequestFactory,
        TimeSpan cacheDuration,
        CancellationToken cancellationToken)
    {
        if (Cache.TryGetValue(probeCacheKey, out (bool Reachable, string Message)? cached) && cached.HasValue)
        {
            snapshot.Reachable = cached.Value.Reachable;
            snapshot.StatusMessage = cached.Value.Message;
            return;
        }

        var (reachable, message) = await ProbeAsync(probeRequestFactory, cancellationToken);
        snapshot.Reachable = reachable;
        snapshot.StatusMessage = message;
        if (reachable is { } known)
        {
            Cache.Set(probeCacheKey, (known, message), cacheDuration);
        }
    }

    /// <summary>Reads a string or integer JSON id as a string; null when absent.</summary>
    private protected static string? ReadId(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var id))
        {
            return null;
        }

        return id.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(id.GetString()) ? null : id.GetString(),
            JsonValueKind.Number => id.GetRawText(),
            _ => null
        };
    }
}
