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
    /// <param name="seriesTitles">The series titles to refresh.</param>
    /// <param name="cancellationToken">
    /// Cancels the job itself, not just the queuing: the job stops before its
    /// next title, ends cancelled and skips re-tagging the files. Pass a token
    /// that lives as long as the job may run, such as
    /// <c>IHostApplicationLifetime.ApplicationStopping</c>, never an HTTP
    /// request's token: the server reuses that for later requests on the same
    /// connection and cancels it when the connection closes.
    /// </param>
    Task<Guid> StartAsync(IEnumerable<string> seriesTitles, CancellationToken cancellationToken = default);

    /// <summary>
    /// A snapshot of the job, or <c>null</c> when it is unknown. Finished jobs are
    /// forgotten after a while, so an old finished job is unknown too.
    /// </summary>
    MetadataRefreshJob? GetJob(Guid jobId);
    IEnumerable<MetadataRefreshJob> GetAllJobs();
}
