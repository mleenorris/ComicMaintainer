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
        int? offset = null,
        int? limit = null,
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

    /// <summary>
    /// Returns the distinct on-disk folders that contribute files to the
    /// given series card, with per-folder file counts and total sizes. Used
    /// by the per-series "Manage Folders" view so users can see how many
    /// folders back a series and decide whether to merge them. Returns null
    /// when the series id is not currently visible in the library.
    /// </summary>
    Task<SeriesFoldersResult?> GetFoldersForSeriesIdAsync(
        string seriesId,
        string? filter = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Bulk variant of <see cref="GetFoldersForSeriesIdAsync"/> that returns
    /// the on-disk folder set for every series currently visible in the
    /// library, keyed by series id. Used by the folder-combine pipeline to
    /// reconcile its own grouping with the authoritative series-card grouping
    /// without paying the cost of one library build per series.
    /// </summary>
    Task<IReadOnlyList<SeriesFoldersResult>> GetAllSeriesFolderGroupsAsync(
        string? filter = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the distinct on-disk folders that back the series whose
    /// external-image cache key matches <paramref name="normalizedKey"/>.
    /// Returns an empty list when no series currently in the library is
    /// associated with that key. Used by <see cref="ISeriesFolderCoverWriter"/>
    /// to push a copy of the cover image into each series folder on disk.
    /// </summary>
    Task<IReadOnlyList<SeriesFolderDto>> GetFoldersForNormalizedKeyAsync(
        string normalizedKey,
        CancellationToken cancellationToken = default);
}

