using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Runs processing previews as batch jobs and keeps their results: what processing would do to
/// each file (see <see cref="IComicProcessorService.PreviewProcessingAsync"/>), with clashes
/// between the files of one preview, until the user decides what to process.
/// </summary>
public interface IProcessingPreviewService
{
    /// <summary>
    /// Starts a job that previews processing of <paramref name="filePaths"/> and returns its id;
    /// blank and repeated paths are left out. Its progress is followed like any batch job's, and
    /// its results are read with <see cref="GetResults"/>.
    /// </summary>
    Guid StartPreview(IEnumerable<string> filePaths);

    /// <summary>
    /// One page of a preview's results: the files the <paramref name="filter"/> selects, sorted by
    /// series then path, from <paramref name="offset"/>, at most <paramref name="limit"/> of them.
    /// While the job runs the page holds the files checked so far. Null when no such preview is
    /// kept: an unknown id, a preview from before a restart, or one dropped for newer ones.
    /// </summary>
    ProcessingPreviewResults? GetResults(Guid jobId, ProcessingPreviewFilter filter, int offset, int limit);
}
