namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Resolves the identity of the caller for the current unit of work.
/// </summary>
/// <remarks>
/// Per-user state (read/unread status, reading progress) has to be resolvable from
/// <c>Core</c> services that are also reachable from background jobs, where no request
/// and therefore no user exists. Rather than threading a user id through every
/// <see cref="IFileStoreService"/> signature — including the many that have nothing to do
/// with per-user state — services depend on this accessor and treat a <c>null</c>
/// <see cref="UserId"/> as "no per-user state available", which is the correct behaviour
/// for scheduled scans, watcher-driven processing and audits.
/// </remarks>
public interface IUserContextAccessor
{
    /// <summary>
    /// Identifier of the current user, or <c>null</c> when running outside a request
    /// (background jobs, hosted services, startup) or when the caller is unauthenticated.
    /// </summary>
    string? UserId { get; }
}
