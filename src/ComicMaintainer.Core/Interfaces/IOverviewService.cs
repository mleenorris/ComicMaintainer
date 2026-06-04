using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Builds the overview/home page: three per-user series rows
/// (Continue Reading, Series Updates, Newly Added Series).
/// </summary>
public interface IOverviewService
{
    /// <summary>
    /// Returns the overview rows for a specific user. Continue Reading is
    /// sourced from that user's durable reading progress; the other two rows
    /// are library-wide but scoped to a recency window.
    /// </summary>
    Task<OverviewResult> GetOverviewAsync(string userId, CancellationToken cancellationToken = default);
}
