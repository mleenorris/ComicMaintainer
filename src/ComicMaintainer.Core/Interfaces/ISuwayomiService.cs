using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Client for a <see href="https://github.com/Suwayomi/Suwayomi-Server">Suwayomi</see>
/// server, used to queue downloads for the issues a series in the local
/// library is missing, and to find series on Suwayomi's sources and add them
/// to its library.
/// </summary>
public interface ISuwayomiService
{
    /// <summary>
    /// True when the integration is enabled and a base URL is configured.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>Probes the configured server and reports what it answered.</summary>
    Task<SuwayomiConnectionStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the series in the Suwayomi library whose title matches
    /// <paramref name="query"/>, best match first. An empty query returns the
    /// first <paramref name="limit"/> library entries.
    /// </summary>
    Task<IReadOnlyList<SuwayomiSeries>> SearchSeriesAsync(
        string? query,
        int limit = 20,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the series currently in the Suwayomi library.</summary>
    Task<SuwayomiLibrarySnapshot> GetLibrarySeriesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Returns the chapters Suwayomi knows about for a series.</summary>
    Task<IReadOnlyList<SuwayomiChapter>> GetChaptersAsync(
        int seriesId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queues downloads on the Suwayomi server for the given issue numbers of
    /// a series. When <paramref name="seriesId"/> is null the series is
    /// resolved by matching the Suwayomi library against
    /// <paramref name="seriesTitle"/> and, because Suwayomi may list the
    /// series under another name, <paramref name="alternateTitles"/>. Issues
    /// absent from matching library entries are then sought across the
    /// installed sources without following the source results.
    /// </summary>
    /// <param name="seriesTitle">Local series title, used to match a Suwayomi library entry.</param>
    /// <param name="issueNumbers">Whole issue/chapter numbers that are missing locally.</param>
    /// <param name="seriesId">Optional explicit Suwayomi series id, bypassing title matching.</param>
    /// <param name="alternateTitles">
    /// Other titles of the local series (canonical title, aliases). They only
    /// count when one equals a Suwayomi title, ignoring case and punctuation,
    /// and such an exact match wins over a partial match on
    /// <paramref name="seriesTitle"/>. Each installed source is searched by
    /// the main title first, then by these titles in order until one matches.
    /// </param>
    Task<SuwayomiDownloadResult> DownloadMissingIssuesAsync(
        string? seriesTitle,
        IEnumerable<int> issueNumbers,
        int? seriesId = null,
        IEnumerable<string>? alternateTitles = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queues downloads using a previously fetched library snapshot, avoiding
    /// a fresh paginated library request for each series in a batch. The
    /// installed source list is also reused across calls with the same
    /// snapshot.
    /// </summary>
    Task<SuwayomiDownloadResult> DownloadMissingIssuesFromSnapshotAsync(
        string? seriesTitle,
        IEnumerable<int> issueNumbers,
        SuwayomiLibrarySnapshot librarySnapshot,
        IEnumerable<string>? alternateTitles = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the catalogue sources installed on the Suwayomi server, by
    /// display name. Suwayomi's own Local source is left out.
    /// </summary>
    Task<IReadOnlyList<SuwayomiSource>> GetSourcesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks a Suwayomi source to search its site for <paramref name="query"/>.
    /// Results keep the source's order except that titles matching the query
    /// come first.
    /// </summary>
    Task<SuwayomiSourceSearchResult> SearchSourceAsync(
        long sourceId,
        string query,
        int page = 1,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a series to the Suwayomi library, unless it is already there.
    /// </summary>
    Task<SuwayomiAddToLibraryResult> AddToLibraryAsync(
        int seriesId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Refreshes a series' chapter list from its source and queues the
    /// chapters that are neither downloaded on the Suwayomi side nor in
    /// <paramref name="ownedIssueNumbers"/>: one chapter per number, and
    /// unnumbered chapters only when the series has no numbered ones.
    /// </summary>
    /// <param name="ownedIssueNumbers">
    /// Issue numbers the local library already has, such as "7" or "10.5";
    /// other labels are ignored.
    /// </param>
    Task<SuwayomiSeriesDownloadResult> QueueSeriesDownloadsAsync(
        int seriesId,
        IEnumerable<string?>? ownedIssueNumbers = null,
        CancellationToken cancellationToken = default);

    /// <summary>Fetches a series' cover image from the Suwayomi server.</summary>
    Task<SuwayomiThumbnail> GetThumbnailAsync(int seriesId, CancellationToken cancellationToken = default);
}
