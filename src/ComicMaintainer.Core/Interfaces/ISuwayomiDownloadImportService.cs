using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Moves chapters Suwayomi has finished downloading out of its downloads
/// folder and into the library, one folder per series.
/// </summary>
public interface ISuwayomiDownloadImportService
{
    /// <summary>
    /// Runs one import pass. Configuration problems (no folder configured, a
    /// missing or overlapping folder) are reported through
    /// <see cref="SuwayomiImportResult.SkippedReason"/> rather than thrown.
    /// </summary>
    Task<SuwayomiImportResult> ImportAsync(CancellationToken cancellationToken = default);
}
