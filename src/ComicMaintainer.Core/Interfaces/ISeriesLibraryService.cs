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

    /// <summary>
    /// Fast, "summary only" variant used by the dynamic library view. Returns
    /// one card per series (no per-issue details) and skips disk-archive reads
    /// so very large libraries load incrementally instead of timing out.
    /// </summary>
    Task<SeriesSummaryResult> GetSeriesSummariesAsync(
        string? filter = null,
        string? search = null,
        int page = 1,
        int perPage = 100,
        string? sort = "name",
        string? direction = "asc",
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the full issue list for a single series card, paged. Returns
    /// null when no series with the given id is currently visible.
    /// </summary>
    Task<SeriesIssuesResult?> GetSeriesIssuesAsync(
        string seriesId,
        string? filter = null,
        int page = 1,
        int perPage = 100,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the canonical title + alias set associated with a series card
    /// id, so callers (e.g. metadata refresh) can act on every title that maps
    /// to that card. Returns an empty list if the id is not present.
    /// </summary>
    Task<IReadOnlyList<string>> GetTitlesForSeriesIdAsync(
        string seriesId,
        string? filter = null,
        CancellationToken cancellationToken = default);
}

