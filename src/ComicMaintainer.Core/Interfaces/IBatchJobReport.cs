namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Handed to a batch job's post-loop hook so it can report problems with
/// things that were not tracked items, such as a folder it could not remove
/// after moving the folder's files out.
/// </summary>
/// <remarks>
/// A reported problem is stored in the job's errors under its subject, so it
/// is listed in the job status's <c>reported_problems</c>, is kept when the
/// job is persisted, and is shown live in the progress view. The job's
/// results and its processed and failed counts stay those of its tracked
/// items.
/// </remarks>
public interface IBatchJobReport
{
    /// <summary>
    /// Records <paramref name="message"/> against <paramref name="subject"/>
    /// (for example a folder path) in the job's reported problems.
    /// </summary>
    Task ReportProblemAsync(string subject, string message);
}
