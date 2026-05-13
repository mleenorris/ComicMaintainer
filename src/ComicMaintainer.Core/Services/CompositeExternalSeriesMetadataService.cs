using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
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

    public string ProviderName => "Composite";

    public IReadOnlyList<IExternalSeriesMetadataService> Providers => _providers;

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
                _logger.LogDebug(ex, "Provider {ProviderType} failed for {SeriesName}", provider.GetType().Name, LoggingHelper.SanitizeForLog(seriesName));
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
            try
            {
                var results = await provider.SearchSeriesAsync(query, limit, cancellationToken);
                aggregated.AddRange(results);
                if (aggregated.Count >= limit)
                {
                    break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Provider {ProviderType} search failed for {SeriesName}", provider.GetType().Name, LoggingHelper.SanitizeForLog(query));
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
}
