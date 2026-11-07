namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Handed to a batch job's post-loop hook so it can add results for things
/// that were not tracked items, such as a folder it could not remove after
/// moving the folder's files out.
/// </summary>
/// <remarks>
/// A reported problem is stored in the job's errors under its subject, so it
/// is listed with the job's results (as not successful, with its message), is
/// kept when the job is persisted, and is shown live in the progress view. The
/// job's processed and failed counts stay those of its tracked items.
/// </remarks>
public interface IBatchJobReport
{
    /// <summary>
    /// Records <paramref name="message"/> against <paramref name="subject"/>
    /// (for example a folder path) in the job's results.
    /// </summary>
    Task ReportProblemAsync(string subject, string message);
}
