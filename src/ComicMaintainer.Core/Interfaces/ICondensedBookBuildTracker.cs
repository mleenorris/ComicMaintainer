using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Runs condensed-EPUB downloads as tracked background builds.
/// </summary>
/// <remarks>
/// Condensing a large selection takes minutes, which is far longer than a
/// browser or a reverse proxy will hold a request open. Building inside the
/// request therefore leaves the user with a spinner that may silently die, and
/// a failure that is only ever visible in the server log. A build is instead
/// started here, detached from the request that asked for it, and its progress,
/// outcome and error are kept so the caller can poll them and download the file
/// once it is ready.
/// </remarks>
public interface ICondensedBookBuildTracker
{
    /// <summary>
    /// Validates the request, registers a build and starts it in the
    /// background. Throws the same argument exceptions a synchronous build
    /// would, so an impossible request still fails fast.
    /// </summary>
    Task<CondensedBookBuildDto> StartAsync(
        CondensedBookBuildRequest request,
        string? ownerUserId,
        CancellationToken cancellationToken = default);

    /// <summary>The build with this id, or null when it is unknown or not the caller's.</summary>
    CondensedBookBuildDto? Get(Guid buildId, string? ownerUserId);

    /// <summary>The caller's builds, newest first.</summary>
    IReadOnlyList<CondensedBookBuildDto> List(string? ownerUserId);

    /// <summary>
    /// Requests cancellation of a running build. Returns false when the build
    /// is unknown, not the caller's, or already finished.
    /// </summary>
    bool Cancel(Guid buildId, string? ownerUserId);

    /// <summary>
    /// Forgets a build and deletes any file it produced. Returns false when the
    /// build is unknown or not the caller's.
    /// </summary>
    bool Discard(Guid buildId, string? ownerUserId);

    /// <summary>
    /// The finished file of a completed build, or null when it is unknown, not
    /// the caller's, or not finished. The file stays on disk until the build
    /// expires or is discarded, so an interrupted download can be retried.
    /// </summary>
    CondensedBookFile? GetCompletedFile(Guid buildId, string? ownerUserId);
}
