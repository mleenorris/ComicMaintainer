using System.Collections.Concurrent;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Combines the external metadata providers. A lookup asks every provider at
/// once and keeps the best-scoring match; a search merges every provider's
/// results; a manual-match refresh asks only the provider that supplied the
/// match.
/// </summary>
/// <remarks>
/// <para>
/// Each provider call runs concurrently with the others and is bounded by
/// <see cref="ProviderCallTimeout"/> (<see cref="SearchCallTimeout"/> for a
/// search, which a user is waiting on). A lookup only accepts a candidate that
/// scores at least <see cref="SeriesMatchScorer.AutomaticMatchThreshold"/>
/// against the requested name (an exact or alias match); among those the
/// highest score wins, and on a tie the provider listed first.
/// </para>
/// <para>
/// When a provider fails (throws, typically an
/// <see cref="ExternalMetadataUnavailableException"/>, or exceeds its time
/// budget), it is placed in an exponential-backoff cooldown window. While
/// cooling down, the provider is skipped rather than retried, so file
/// processing is never held up waiting on a known-bad upstream. Each
/// successive failure doubles the wait (capped at <see cref="MaxBackoff"/>),
/// and a provider that asked for a longer wait (Retry-After) gets that; an
/// answer from the provider, with or without results, clears the state. A
/// request held back by our own pacing
/// (<see cref="ExternalMetadataFailureKind.Paced"/>) never reached the provider,
/// so it neither starts nor clears a cooldown.
/// </para>
/// <para>
/// A lookup or search that produced nothing while at least one provider
/// failed, was paced or was cooling down throws an aggregate
/// <see cref="ExternalMetadataUnavailableException"/>: the series may well
/// exist at the provider that could not answer, so callers must not record
/// "not found".
/// </para>
/// </remarks>
public class CompositeExternalSeriesMetadataService : IExternalSeriesMetadataService
{
    /// <summary>Wait after the first failure before retrying a provider.</summary>
    public static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(30);

    /// <summary>Upper bound on the wait between retries for a failing provider.</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);

    /// <summary>
    /// Overall time one provider may take to answer one lookup, including its
    /// rate-limit queueing and retry. Providers bound each attempt themselves
    /// (<see cref="Configuration.AppSettings.MetadataProviderTimeoutSeconds"/>);
    /// this is the backstop so a lookup never waits on a provider longer. It
    /// leaves room for a full client-side pace window (AniList's is a minute)
    /// plus one attempt at the longest configurable timeout.
    /// </summary>
    public static readonly TimeSpan ProviderCallTimeout = TimeSpan.FromSeconds(100);

    /// <summary>
    /// Overall time one provider may take to answer one search. Searches back
    /// the manual-match and cover pickers, so this stays below the API's
    /// 30-second default request timeout and the picker shows what answered.
    /// </summary>
    public static readonly TimeSpan SearchCallTimeout = TimeSpan.FromSeconds(25);

    private readonly IReadOnlyList<IExternalSeriesMetadataService> _providers;
    private readonly ILogger<CompositeExternalSeriesMetadataService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _initialBackoff;
    private readonly TimeSpan _maxBackoff;
    private readonly TimeSpan _providerCallTimeout;
    private readonly TimeSpan _searchCallTimeout;
    private readonly ConcurrentDictionary<IExternalSeriesMetadataService, ProviderBackoffState> _backoff = new();

    public CompositeExternalSeriesMetadataService(
        IReadOnlyList<IExternalSeriesMetadataService> providers,
        ILogger<CompositeExternalSeriesMetadataService> logger)
        : this(providers, logger, TimeProvider.System, InitialBackoff, MaxBackoff)
    {
    }

    /// <summary>
    /// Test-only constructor that allows overriding the clock, the backoff
    /// schedule and the per-provider call budgets. Production callers should
    /// use the simpler overload.
    /// </summary>
    public CompositeExternalSeriesMetadataService(
        IReadOnlyList<IExternalSeriesMetadataService> providers,
        ILogger<CompositeExternalSeriesMetadataService> logger,
        TimeProvider timeProvider,
        TimeSpan initialBackoff,
        TimeSpan maxBackoff,
        TimeSpan? providerCallTimeout = null,
        TimeSpan? searchCallTimeout = null)
    {
        _providers = providers;
        _logger = logger;
        _timeProvider = timeProvider;
        _initialBackoff = initialBackoff;
        _maxBackoff = maxBackoff;
        _providerCallTimeout = providerCallTimeout ?? ProviderCallTimeout;
        _searchCallTimeout = searchCallTimeout ?? SearchCallTimeout;
    }

    public string ProviderName => "Composite";

    public IReadOnlyList<IExternalSeriesMetadataService> Providers => _providers;

    /// <inheritdoc />
    public async Task<ExternalSeriesMetadata?> LookupSeriesAsync(string seriesName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(seriesName))
        {
            return null;
        }

        var (outcomes, failures) = await QueryAllAsync(
            seriesName,
            (provider, token) => provider.LookupSeriesAsync(seriesName, token),
            _providerCallTimeout,
            cancellationToken);

        ExternalSeriesMetadata? best = null;
        var bestScore = 0d;
        foreach (var (provider, result) in outcomes)
        {
            if (result is null)
            {
                continue;
            }

            // Providers only return exact/alias matches, but the composite is
            // the gate for automatic matching, so it enforces the rule itself.
            var score = SeriesMatchScorer.Score(seriesName, result.CanonicalTitle, result.Aliases);
            if (score < SeriesMatchScorer.AutomaticMatchThreshold)
            {
                _logger.LogDebug(
                    "Ignoring {Provider} candidate {Candidate} for {SeriesName}: match score {Score} is below {Threshold}",
                    NameOf(provider),
                    LoggingHelper.SanitizeForLog(result.CanonicalTitle),
                    LoggingHelper.SanitizeForLog(seriesName),
                    score,
                    SeriesMatchScorer.AutomaticMatchThreshold);
                continue;
            }

            // Outcomes are in provider order, so a tie keeps the earlier provider.
            if (best is null || score > bestScore)
            {
                best = result;
                bestScore = score;
            }
        }

        if (best is null && failures.Count > 0)
        {
            throw new ExternalMetadataUnavailableException(failures);
        }

        return best;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Every available provider is asked for <paramref name="limit"/> results
    /// and the lists are interleaved in provider order (first result of each
    /// provider, then the second of each, ...) before taking
    /// <paramref name="limit"/>, so one provider that fills the limit cannot
    /// hide the others' candidates from the manual-match and cover pickers.
    /// </remarks>
    public async Task<IReadOnlyList<ExternalSeriesMetadata>> SearchSeriesAsync(
        string query,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0)
        {
            return Array.Empty<ExternalSeriesMetadata>();
        }

        var (outcomes, failures) = await QueryAllAsync(
            query,
            (provider, token) => provider.SearchSeriesAsync(query, limit, token),
            _searchCallTimeout,
            cancellationToken);

        var lists = outcomes
            .Select(outcome => outcome.Result ?? (IReadOnlyList<ExternalSeriesMetadata>)Array.Empty<ExternalSeriesMetadata>())
            .Where(list => list.Count > 0)
            .ToList();
        var merged = new List<ExternalSeriesMetadata>(limit);
        for (var index = 0; merged.Count < limit && lists.Any(list => index < list.Count); index++)
        {
            foreach (var list in lists.Where(list => index < list.Count))
            {
                if (merged.Count >= limit)
                {
                    break;
                }

                merged.Add(list[index]);
            }
        }

        if (merged.Count == 0 && failures.Count > 0)
        {
            throw new ExternalMetadataUnavailableException(failures);
        }

        return merged;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Only the provider named <paramref name="source"/> is asked; null when
    /// no configured provider has that name.
    /// </remarks>
    public async Task<ExternalSeriesMetadata?> LookupMatchAsync(
        string source,
        string? sourceId,
        string title,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var provider = _providers.FirstOrDefault(candidate =>
            string.Equals(candidate.ProviderName, source, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            return null;
        }

        if (IsCoolingDown(provider, out var retryAt))
        {
            throw CoolingDownFailure(provider, retryAt);
        }

        var outcome = await InvokeAsync(
            provider,
            title,
            (candidate, token) => candidate.LookupMatchAsync(source, sourceId, title, token),
            _providerCallTimeout,
            cancellationToken);
        return outcome.Failure is { } failure ? throw failure : outcome.Result;
    }

    /// <inheritdoc />
    public void ForgetNoMatch(string seriesName)
    {
        foreach (var provider in _providers)
        {
            provider.ForgetNoMatch(seriesName);
        }
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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
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

    /// <summary>
    /// Calls every provider that is not cooling down, concurrently, and
    /// returns the answers (in provider order) and the failures, which
    /// include the providers skipped while cooling down.
    /// </summary>
    private async Task<(List<(IExternalSeriesMetadataService Provider, T? Result)> Outcomes, List<ExternalMetadataUnavailableException> Failures)>
        QueryAllAsync<T>(
            string subject,
            Func<IExternalSeriesMetadataService, CancellationToken, Task<T>> call,
            TimeSpan callTimeout,
            CancellationToken cancellationToken)
        where T : class?
    {
        cancellationToken.ThrowIfCancellationRequested();
        var failures = new List<ExternalMetadataUnavailableException>();
        var calls = new List<Task<ProviderOutcome<T>>>(_providers.Count);
        foreach (var provider in _providers)
        {
            if (IsCoolingDown(provider, out var retryAt))
            {
                _logger.LogDebug(
                    "Skipping provider {ProviderType} for {Subject}; cooling down until {RetryAt:O}",
                    provider.GetType().Name,
                    LoggingHelper.SanitizeForLog(subject),
                    retryAt);
                failures.Add(CoolingDownFailure(provider, retryAt));
                continue;
            }

            calls.Add(InvokeAsync(provider, subject, call, callTimeout, cancellationToken));
        }

        var completed = await Task.WhenAll(calls);
        var outcomes = new List<(IExternalSeriesMetadataService Provider, T? Result)>(completed.Length);
        foreach (var outcome in completed)
        {
            if (outcome.Failure is { } failure)
            {
                failures.Add(failure);
            }
            else
            {
                outcomes.Add((outcome.Provider, outcome.Result));
            }
        }

        return (outcomes, failures);
    }

    /// <summary>
    /// Calls one provider within <paramref name="callTimeout"/>. An answer
    /// clears the provider's backoff; a failure (any exception other than the
    /// caller's cancellation, or running out of time) starts or extends it and
    /// is returned rather than thrown. A request held back by our own pacing
    /// is also returned, but leaves the backoff alone.
    /// </summary>
    private async Task<ProviderOutcome<T>> InvokeAsync<T>(
        IExternalSeriesMetadataService provider,
        string subject,
        Func<IExternalSeriesMetadataService, CancellationToken, Task<T>> call,
        TimeSpan callTimeout,
        CancellationToken cancellationToken)
        where T : class?
    {
        ExternalMetadataUnavailableException failure;
        using (var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            budget.CancelAfter(callTimeout);
            try
            {
                var result = await call(provider, budget.Token);
                ResetBackoff(provider);
                return new ProviderOutcome<T>(provider, result, null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Report the caller's own token rather than the linked one.
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
            catch (OperationCanceledException ex)
            {
                failure = new ExternalMetadataUnavailableException(
                    NameOf(provider),
                    ExternalMetadataFailureKind.Timeout,
                    $"{NameOf(provider)} did not answer within {callTimeout.TotalSeconds:F0}s",
                    innerException: ex);
            }
            catch (ExternalMetadataUnavailableException ex)
            {
                failure = ex;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failure = new ExternalMetadataUnavailableException(
                    NameOf(provider),
                    ExternalMetadataFailureKind.Unavailable,
                    $"{NameOf(provider)} failed: {ex.Message}",
                    innerException: ex);
            }
        }

        if (failure.Kind == ExternalMetadataFailureKind.Paced)
        {
            _logger.LogDebug(
                "Provider {ProviderType} not asked for {Subject}: {Reason}",
                provider.GetType().Name,
                LoggingHelper.SanitizeForLog(subject),
                LoggingHelper.SanitizeForLog(failure.Message));
            return new ProviderOutcome<T>(provider, default, failure);
        }

        var nextRetry = RecordFailure(provider, failure.RetryAfter);
        _logger.LogDebug(
            failure,
            "Provider {ProviderType} failed for {Subject}; next attempt at {RetryAt:O}",
            provider.GetType().Name,
            LoggingHelper.SanitizeForLog(subject),
            nextRetry);
        return new ProviderOutcome<T>(provider, default, failure);
    }

    private ExternalMetadataUnavailableException CoolingDownFailure(IExternalSeriesMetadataService provider, DateTimeOffset retryAt)
        => new(
            NameOf(provider),
            ExternalMetadataFailureKind.Unavailable,
            $"{NameOf(provider)} is skipped after recent failures until {retryAt:O}",
            retryAt - _timeProvider.GetUtcNow());

    /// <summary>The provider's name for messages; its type name when it reports none.</summary>
    private static string NameOf(IExternalSeriesMetadataService provider)
        => string.IsNullOrWhiteSpace(provider.ProviderName) ? provider.GetType().Name : provider.ProviderName;

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

    private DateTimeOffset RecordFailure(IExternalSeriesMetadataService provider, TimeSpan? retryAfter)
    {
        var now = _timeProvider.GetUtcNow();
        var requested = retryAfter is { } wait && wait > TimeSpan.Zero
            ? (wait > _maxBackoff ? _maxBackoff : wait)
            : TimeSpan.Zero;
        var updated = _backoff.AddOrUpdate(
            provider,
            _ => new ProviderBackoffState(1, now + Max(_initialBackoff, requested)),
            (_, existing) =>
            {
                var nextFailures = existing.ConsecutiveFailures + 1;
                var delayTicks = Math.Min(
                    _maxBackoff.Ticks,
                    _initialBackoff.Ticks * (long)Math.Pow(2, Math.Min(nextFailures - 1, 30)));
                return new ProviderBackoffState(nextFailures, now + Max(TimeSpan.FromTicks(delayTicks), requested));
            });
        return updated.NextAttemptUtc;

        static TimeSpan Max(TimeSpan left, TimeSpan right) => left >= right ? left : right;
    }

    private void ResetBackoff(IExternalSeriesMetadataService provider)
    {
        _backoff.TryRemove(provider, out _);
    }

    private readonly record struct ProviderBackoffState(int ConsecutiveFailures, DateTimeOffset NextAttemptUtc);

    private readonly record struct ProviderOutcome<T>(
        IExternalSeriesMetadataService Provider,
        T? Result,
        ExternalMetadataUnavailableException? Failure)
        where T : class?;
}
