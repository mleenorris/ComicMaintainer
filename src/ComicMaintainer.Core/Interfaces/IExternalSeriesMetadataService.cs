using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// An external series metadata provider (or the composite of all of them).
/// </summary>
/// <remarks>
/// Every lookup distinguishes "the provider answered that it has no match"
/// (null or an empty list) from "the provider could not answer" (an
/// <see cref="ExternalMetadataUnavailableException"/>). Callers must never
/// record a failure as "not found".
/// </remarks>
public interface IExternalSeriesMetadataService
{
    /// <summary>
    /// Display name of the provider (e.g. "ComicVine", "MangaDex"). Used by
    /// the provider-health UI and stored as <see cref="ExternalSeriesMetadata.Source"/>.
    /// Default implementation returns the type name for back-compat with
    /// providers that have not been updated yet.
    /// </summary>
    string ProviderName => GetType().Name;

    /// <summary>
    /// Looks up the series named <paramref name="seriesName"/> for an automatic
    /// match. Returns only a candidate whose canonical title or an alias
    /// matches the name (see <see cref="Utilities.SeriesMatchScorer.SelectAutomaticMatch"/>),
    /// or null when there is none.
    /// </summary>
    /// <exception cref="ExternalMetadataUnavailableException">The provider could not answer.</exception>
    Task<ExternalSeriesMetadata?> LookupSeriesAsync(string seriesName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Search the provider for multiple candidate series. Default implementation
    /// falls back to <see cref="LookupSeriesAsync"/> for providers that have not
    /// been updated yet; a failed lookup fails the search with the same exception.
    /// </summary>
    /// <exception cref="ExternalMetadataUnavailableException">The provider could not answer.</exception>
    async Task<IReadOnlyList<ExternalSeriesMetadata>> SearchSeriesAsync(
        string query,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        var result = await LookupSeriesAsync(query, cancellationToken);
        return result is null ? Array.Empty<ExternalSeriesMetadata>() : new[] { result };
    }

    /// <summary>
    /// Re-fetches the entry a series was matched to, from the provider that
    /// supplied it only: <paramref name="source"/> is the stored
    /// <see cref="ExternalSeriesMetadata.Source"/> and <paramref name="sourceId"/>
    /// its <see cref="ExternalSeriesMetadata.SourceId"/>, when known. Without
    /// an id the provider looks up <paramref name="title"/> (the matched
    /// canonical title) the way <see cref="LookupSeriesAsync"/> does. Returns
    /// null when this provider is not <paramref name="source"/>, is disabled,
    /// or no longer has the entry. Other providers are never consulted, so a
    /// refresh cannot replace a user's manual match with another series.
    /// </summary>
    /// <exception cref="ExternalMetadataUnavailableException">The provider could not answer.</exception>
    Task<ExternalSeriesMetadata?> LookupMatchAsync(
        string source,
        string? sourceId,
        string title,
        CancellationToken cancellationToken = default)
    {
        return string.Equals(source, ProviderName, StringComparison.OrdinalIgnoreCase)
            ? LookupSeriesAsync(title, cancellationToken)
            : Task.FromResult<ExternalSeriesMetadata?>(null);
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
