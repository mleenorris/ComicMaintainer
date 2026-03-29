using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Interfaces;

public interface ISeriesLibraryService
{
    Task<SeriesLibraryResult> GetSeriesAsync(
        string? filter = null,
        string? search = null,
        int page = 1,
        int perPage = 100,
        string? sort = "name",
        string? direction = "asc",
        CancellationToken cancellationToken = default);
}
