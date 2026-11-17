using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Matches library files to ComicVine issues and fills their tags with what ComicVine knows
/// (see <see cref="Utilities.ComicVineIssueFill"/>), and reads the story arcs they are part of.
/// </summary>
public interface IComicVineIssueDetailsService
{
    /// <summary>
    /// Fills the files at <paramref name="filePaths"/>, or every file when null, making at most
    /// <paramref name="maxRequests"/> ComicVine requests. Files whose details are already known
    /// are filled without asking ComicVine; the rest wait for the next run.
    /// </summary>
    Task<ComicVineDetailsRunSummary> RunAsync(
        IReadOnlyCollection<string>? filePaths,
        int maxRequests,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a background job that fills <paramref name="filePaths"/> one by one, with the
    /// progress shown like any processing job. Returns the job's id.
    /// </summary>
    Guid StartJob(IReadOnlyCollection<string> filePaths);

    /// <summary>
    /// The ComicVine covers the cover of the file at <paramref name="filePath"/> can be replaced
    /// with: its volume is matched, and its issues listed, as for filling details. Null when the
    /// file is not in the library.
    /// </summary>
    Task<CoverCandidates?> GetCoverCandidatesAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>The story arcs the files at <paramref name="filePaths"/> are part of, by name.</summary>
    Task<IReadOnlyList<SeriesStoryArc>> GetStoryArcsAsync(
        IReadOnlyCollection<string> filePaths,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// A story arc's issues in reading order, each with the library file that holds it; the arc
    /// is fetched from ComicVine the first time. Null when ComicVine has no such arc.
    /// </summary>
    Task<StoryArcReadingOrder?> GetStoryArcAsync(int storyArcId, CancellationToken cancellationToken = default);
}
