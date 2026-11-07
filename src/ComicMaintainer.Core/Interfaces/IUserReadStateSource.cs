namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Implemented by stores that keep per-user read state separately from the shared file
/// list, so derived views can be cached once for every user and have each user's read
/// state applied when they are projected.
/// </summary>
/// <remarks>
/// <see cref="IChangeVersionSource.ChangeVersion"/> of such a store does not change when
/// only read state changes; <see cref="GetReadStateVersion"/> does, for that user alone.
/// One user marking an issue read therefore leaves every other user's cached library and
/// ETag valid. Like <see cref="IChangeVersionSource"/> this is opt-in, so test doubles
/// that do not implement it keep per-user, uncached behaviour.
/// </remarks>
public interface IUserReadStateSource
{
    /// <summary>
    /// A counter that changes whenever <paramref name="userId"/>'s read state changes.
    /// Values are only comparable for the same user within one process.
    /// </summary>
    long GetReadStateVersion(string userId);

    /// <summary>The paths <paramref name="userId"/> has marked read.</summary>
    Task<IReadOnlySet<string>> GetReadFilePathsAsync(string userId, CancellationToken cancellationToken = default);
}
