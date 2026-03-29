using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Interfaces;

public interface IExternalSeriesMetadataService
{
    Task<ExternalSeriesMetadata?> LookupSeriesAsync(string seriesName, CancellationToken cancellationToken = default);
}
