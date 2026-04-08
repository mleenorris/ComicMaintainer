using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Chains multiple external metadata providers, returning the first successful match.
/// </summary>
public class CompositeExternalSeriesMetadataService : IExternalSeriesMetadataService
{
    private readonly IReadOnlyList<IExternalSeriesMetadataService> _providers;
    private readonly ILogger<CompositeExternalSeriesMetadataService> _logger;

    public CompositeExternalSeriesMetadataService(
        IReadOnlyList<IExternalSeriesMetadataService> providers,
        ILogger<CompositeExternalSeriesMetadataService> logger)
    {
        _providers = providers;
        _logger = logger;
    }

    public async Task<ExternalSeriesMetadata?> LookupSeriesAsync(string seriesName, CancellationToken cancellationToken = default)
    {
        foreach (var provider in _providers)
        {
            try
            {
                var result = await provider.LookupSeriesAsync(seriesName, cancellationToken);
                if (result != null)
                {
                    return result;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Provider {ProviderType} failed for {SeriesName}", provider.GetType().Name, seriesName);
            }
        }

        return null;
    }
}
