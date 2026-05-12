using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Manages background refresh jobs for the external series metadata cache.
/// </summary>
public interface ISeriesMetadataRefreshJobService
{
    /// <summary>
    /// Queue a background job that refreshes external metadata for the
    /// supplied series titles. Returns the new job's id.
    /// </summary>
    Task<Guid> StartAsync(IEnumerable<string> seriesTitles, CancellationToken cancellationToken = default);

    MetadataRefreshJob? GetJob(Guid jobId);
    IEnumerable<MetadataRefreshJob> GetAllJobs();
}
