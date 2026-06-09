using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Talks to a Suwayomi (Tachidesk) sidecar over its GraphQL API to enqueue
/// downloads of missing issues. The actual downloading is performed by
/// Suwayomi; this service only matches a series to a tracked manga in
/// Suwayomi's library and asks Suwayomi to download specific chapters.
/// </summary>
public interface ISuwayomiDownloadService
{
    /// <summary>
    /// Reports whether the integration is enabled and configured, optionally
    /// probing the Suwayomi server for reachability when <paramref name="probe"/>
    /// is true.
    /// </summary>
    Task<SuwayomiAvailability> GetAvailabilityAsync(bool probe = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds the series in Suwayomi's library that best matches the supplied
    /// candidate titles and enqueues downloads of the chapters whose chapter
    /// number matches one of <paramref name="issueNumbers"/>.
    /// </summary>
    /// <param name="candidateTitles">
    /// Known titles/aliases of the series in our library, used to find the
    /// matching tracked series in Suwayomi.
    /// </param>
    /// <param name="issueNumbers">Issue/chapter numbers to download.</param>
    Task<SuwayomiDownloadResult> DownloadIssuesAsync(
        IReadOnlyList<string> candidateTitles,
        IReadOnlyList<string> issueNumbers,
        CancellationToken cancellationToken = default);
}
