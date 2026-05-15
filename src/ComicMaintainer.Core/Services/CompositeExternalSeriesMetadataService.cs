using System.Collections.Concurrent;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Chains multiple external metadata providers, returning the first successful match.
/// </summary>
/// <remarks>
/// When a provider throws, it is placed in an exponential-backoff cooldown
/// window. While cooling down, the provider is skipped rather than retried —
/// the call is treated as a failed lookup so file processing is never held up
/// waiting on a known-bad upstream. Each successive failure doubles the wait
/// (capped at <see cref="MaxBackoff"/>); a successful call clears the state.
/// </remarks>
public class CompositeExternalSeriesMetadataService : IExternalSeriesMetadataService
{
    /// <summary>Wait after the first failure before retrying a provider.</summary>
    public static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(30);

    /// <summary>Upper bound on the wait between retries for a failing provider.</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);

    private readonly IReadOnlyList<IExternalSeriesMetadataService> _providers;
    private readonly ILogger<CompositeExternalSeriesMetadataService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _initialBackoff;
    private readonly TimeSpan _maxBackoff;
    private readonly ConcurrentDictionary<IExternalSeriesMetadataService, ProviderBackoffState> _backoff = new();

    public CompositeExternalSeriesMetadataService(
        IReadOnlyList<IExternalSeriesMetadataService> providers,
        ILogger<CompositeExternalSeriesMetadataService> logger)
        : this(providers, logger, TimeProvider.System, InitialBackoff, MaxBackoff)
    {
    }

    /// <summary>
    /// Test-only constructor that allows overriding the clock and backoff
    /// schedule. Production callers should use the simpler overload.
    /// </summary>
    public CompositeExternalSeriesMetadataService(
        IReadOnlyList<IExternalSeriesMetadataService> providers,
        ILogger<CompositeExternalSeriesMetadataService> logger,
        TimeProvider timeProvider,
        TimeSpan initialBackoff,
        TimeSpan maxBackoff)
    {
        _providers = providers;
        _logger = logger;
        _timeProvider = timeProvider;
        _initialBackoff = initialBackoff;
        _maxBackoff = maxBackoff;
    }

    public string ProviderName => "Composite";

    public IReadOnlyList<IExternalSeriesMetadataService> Providers => _providers;

    public async Task<ExternalSeriesMetadata?> LookupSeriesAsync(string seriesName, CancellationToken cancellationToken = default)
    {
        foreach (var provider in _providers)
        {
            if (IsCoolingDown(provider, out var retryAt))
            {
                _logger.LogDebug(
                    "Skipping provider {ProviderType} for {SeriesName}; cooling down until {RetryAt:O}",
                    provider.GetType().Name,
                    LoggingHelper.SanitizeForLog(seriesName),
                    retryAt);
                continue;
            }

            try
            {
                var result = await provider.LookupSeriesAsync(seriesName, cancellationToken);
                ResetBackoff(provider);
                if (result != null)
                {
                    return result;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var nextRetry = RecordFailure(provider);
                _logger.LogDebug(
                    ex,
                    "Provider {ProviderType} failed for {SeriesName}; next attempt at {RetryAt:O}",
                    provider.GetType().Name,
                    LoggingHelper.SanitizeForLog(seriesName),
                    nextRetry);
            }
        }

        return null;
    }

    public async Task<IReadOnlyList<ExternalSeriesMetadata>> SearchSeriesAsync(
        string query,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        var aggregated = new List<ExternalSeriesMetadata>();
        foreach (var provider in _providers)
        {
            if (IsCoolingDown(provider, out var retryAt))
            {
                _logger.LogDebug(
                    "Skipping provider {ProviderType} search for {Query}; cooling down until {RetryAt:O}",
                    provider.GetType().Name,
                    LoggingHelper.SanitizeForLog(query),
                    retryAt);
                continue;
            }

            try
            {
                var results = await provider.SearchSeriesAsync(query, limit, cancellationToken);
                ResetBackoff(provider);
                aggregated.AddRange(results);
                if (aggregated.Count >= limit)
                {
                    break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var nextRetry = RecordFailure(provider);
                _logger.LogDebug(
                    ex,
                    "Provider {ProviderType} search failed for {Query}; next attempt at {RetryAt:O}",
                    provider.GetType().Name,
                    LoggingHelper.SanitizeForLog(query),
                    nextRetry);
            }
        }

        return aggregated.Take(limit).ToList();
    }

    /// <summary>Returns a health snapshot for every chained provider, in order.</summary>
    public async Task<IReadOnlyList<ProviderHealth>> CheckAllHealthAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<ProviderHealth>(_providers.Count);
        foreach (var provider in _providers)
        {
            if (IsCoolingDown(provider, out var retryAt))
            {
                results.Add(new ProviderHealth
                {
                    Name = provider.ProviderName,
                    Enabled = true,
                    Configured = true,
                    Reachable = false,
                    StatusMessage = $"Backing off after recent failure; next attempt at {retryAt:O}"
                });
                continue;
            }

            try
            {
                results.Add(await provider.CheckHealthAsync(cancellationToken));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Health check failed for provider {ProviderType}", provider.GetType().Name);
                results.Add(new ProviderHealth
                {
                    Name = provider.ProviderName,
                    Enabled = false,
                    Configured = false,
                    Reachable = false,
                    StatusMessage = ex.Message
                });
            }
        }
        return results;
    }

    private bool IsCoolingDown(IExternalSeriesMetadataService provider, out DateTimeOffset retryAt)
    {
        if (_backoff.TryGetValue(provider, out var state) && state.NextAttemptUtc > _timeProvider.GetUtcNow())
        {
            retryAt = state.NextAttemptUtc;
            return true;
        }

        retryAt = default;
        return false;
    }

    private DateTimeOffset RecordFailure(IExternalSeriesMetadataService provider)
    {
        var now = _timeProvider.GetUtcNow();
        var updated = _backoff.AddOrUpdate(
            provider,
            _ => new ProviderBackoffState(1, now + _initialBackoff),
            (_, existing) =>
            {
                var nextFailures = existing.ConsecutiveFailures + 1;
                var delayTicks = Math.Min(
                    _maxBackoff.Ticks,
                    _initialBackoff.Ticks * (long)Math.Pow(2, Math.Min(nextFailures - 1, 30)));
                return new ProviderBackoffState(nextFailures, now + TimeSpan.FromTicks(delayTicks));
            });
        return updated.NextAttemptUtc;
    }

    private void ResetBackoff(IExternalSeriesMetadataService provider)
    {
        _backoff.TryRemove(provider, out _);
    }

    private readonly record struct ProviderBackoffState(int ConsecutiveFailures, DateTimeOffset NextAttemptUtc);
}
