using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Thread-safe rolling counters used by <see cref="Interfaces.IExternalSeriesMetadataService"/>
/// implementations to expose runtime health to the UI.
/// </summary>
internal sealed class ProviderHealthTracker
{
    private readonly object _gate = new();
    private readonly string _name;
    private long _successCount;
    private long _failureCount;
    private string? _lastError;
    private DateTime? _lastSuccessUtc;
    private DateTime? _lastFailureUtc;

    public ProviderHealthTracker(string name)
    {
        _name = name;
    }

    public void RecordSuccess()
    {
        lock (_gate)
        {
            _successCount++;
            _lastSuccessUtc = DateTime.UtcNow;
        }
    }

    public void RecordFailure(string? error)
    {
        lock (_gate)
        {
            _failureCount++;
            _lastFailureUtc = DateTime.UtcNow;
            _lastError = string.IsNullOrWhiteSpace(error) ? _lastError : error;
        }
    }

    /// <summary>
    /// Returns a populated <see cref="ProviderHealth"/> with the rolling
    /// counters captured atomically. Caller is expected to fill in
    /// <c>Enabled</c>, <c>Configured</c>, <c>Reachable</c>, and
    /// <c>StatusMessage</c> with provider-specific information.
    /// </summary>
    public ProviderHealth Snapshot()
    {
        lock (_gate)
        {
            return new ProviderHealth
            {
                Name = _name,
                SuccessCount = _successCount,
                FailureCount = _failureCount,
                LastError = _lastError,
                LastSuccessUtc = _lastSuccessUtc,
                LastFailureUtc = _lastFailureUtc
            };
        }
    }
}
