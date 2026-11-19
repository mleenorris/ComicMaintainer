using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// ComicVine's issue-level catalogue: the issues of a volume, an issue's credits and story
/// arcs, and a story arc's issues. Every call is one or more rate-limited requests; each
/// throws <see cref="ExternalMetadataUnavailableException"/> when ComicVine cannot answer.
/// </summary>
public interface IComicVineCatalogClient
{
    /// <summary>True when a ComicVine API key is set and external metadata is on.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// The issues of volume <paramref name="volumeId"/>, 100 to a request, reading at most
    /// <paramref name="maxRequests"/> pages.
    /// </summary>
    Task<ComicVineResult<IReadOnlyList<ComicVineIssueSummary>>> ListVolumeIssuesAsync(
        int volumeId,
        int maxRequests,
        CancellationToken cancellationToken = default);

    /// <summary>An issue's writers and story arcs; null when ComicVine has no such issue.</summary>
    Task<ComicVineIssueDetails?> GetIssueDetailsAsync(int issueId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The issue <paramref name="issueId"/> with its volume and details, in one request: what a
    /// file whose tags name the issue (see <see cref="Utilities.ComicVineTagId"/>) is filled from.
    /// Null when ComicVine has no such issue.
    /// </summary>
    Task<ComicVineIssue?> GetIssueAsync(int issueId, CancellationToken cancellationToken = default);

    /// <summary>A story arc and its issues in ComicVine's order; null when there is no such arc.</summary>
    Task<ComicVineStoryArc?> GetStoryArcAsync(int storyArcId, CancellationToken cancellationToken = default);
}
