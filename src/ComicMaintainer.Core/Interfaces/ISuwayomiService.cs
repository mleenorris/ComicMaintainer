using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Client for a <see href="https://github.com/Suwayomi/Suwayomi-Server">Suwayomi</see>
/// server, used to queue downloads for the issues a series in the local
/// library is missing.
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

    /// <summary>Returns the chapters Suwayomi knows about for a series.</summary>
    Task<IReadOnlyList<SuwayomiChapter>> GetChaptersAsync(
        int seriesId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queues downloads on the Suwayomi server for the given issue numbers of
    /// a series. When <paramref name="seriesId"/> is null the series is
    /// resolved by matching the Suwayomi library against
    /// <paramref name="seriesTitle"/> and, because Suwayomi may list the
    /// series under another name, <paramref name="alternateTitles"/>.
    /// </summary>
    /// <param name="seriesTitle">Local series title, used to match a Suwayomi library entry.</param>
    /// <param name="issueNumbers">Whole issue/chapter numbers that are missing locally.</param>
    /// <param name="seriesId">Optional explicit Suwayomi series id, bypassing title matching.</param>
    /// <param name="alternateTitles">
    /// Other titles of the local series (canonical title, aliases). They only
    /// count when one equals a Suwayomi title, ignoring case and punctuation,
    /// and such an exact match wins over a partial match on
    /// <paramref name="seriesTitle"/>.
    /// </param>
    Task<SuwayomiDownloadResult> DownloadMissingIssuesAsync(
        string? seriesTitle,
        IEnumerable<int> issueNumbers,
        int? seriesId = null,
        IEnumerable<string>? alternateTitles = null,
        CancellationToken cancellationToken = default);
}
