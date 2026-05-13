using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Interfaces;

public interface IExternalSeriesMetadataService
{
    /// <summary>
    /// Display name of the provider (e.g. "ComicVine", "MangaDex"). Used by
    /// the provider-health UI. Default implementation returns the type name
    /// for back-compat with providers that have not been updated yet.
    /// </summary>
    string ProviderName => GetType().Name;

    Task<ExternalSeriesMetadata?> LookupSeriesAsync(string seriesName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Search the provider for multiple candidate series. Default implementation
    /// falls back to <see cref="LookupSeriesAsync"/> for providers that have not
    /// been updated yet.
    /// </summary>
    Task<IReadOnlyList<ExternalSeriesMetadata>> SearchSeriesAsync(
        string query,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        return LookupSeriesAsync(query, cancellationToken)
            .ContinueWith<IReadOnlyList<ExternalSeriesMetadata>>(
                t => t.Result is null ? Array.Empty<ExternalSeriesMetadata>() : new[] { t.Result },
                cancellationToken,
                TaskContinuationOptions.OnlyOnRanToCompletion,
                TaskScheduler.Default);
    }

    /// <summary>
    /// Returns a snapshot of the provider's configuration + recent runtime
    /// status, used to render a per-provider health indicator in the UI.
    /// Implementations should make this cheap and cache reachability probes
    /// for at least ~30s to avoid hammering external APIs.
    /// </summary>
    Task<ProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ProviderHealth
        {
            Name = ProviderName,
            Enabled = true,
            Configured = true,
            Reachable = null,
            StatusMessage = "Health check not implemented for this provider"
        });
    }
}

