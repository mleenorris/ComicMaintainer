using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Interfaces;

public interface IExternalSeriesMetadataService
{
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
}
