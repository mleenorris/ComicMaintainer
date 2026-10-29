using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Adds a series found on a Suwayomi source to the Suwayomi library and
/// queues downloads for the chapters the local library does not have.
/// </summary>
public interface ISuwayomiSeriesAddService
{
    /// <summary>
    /// Adds the series to the Suwayomi library (unless it is already there)
    /// and queues its chapters, leaving out the issues of a local series with
    /// the same title. Throws <see cref="SuwayomiException"/> when either step
    /// fails; adding again after a failed queue is harmless.
    /// </summary>
    Task<SuwayomiAddSeriesResult> AddSeriesAsync(int seriesId, CancellationToken cancellationToken = default);
}
