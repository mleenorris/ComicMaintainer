namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// One process-wide lock for work that creates, fills or removes library
/// folders, so one service never removes a folder while another is moving a
/// file into it. The Suwayomi importer holds it while it stages and publishes a
/// chapter, and folder combine holds it while it removes emptied source
/// folders.
/// </summary>
/// <remarks>
/// Registered as a singleton. The lock is not reentrant: code that already
/// holds it must not take it again (directly or from work it waits on), and an
/// attempt to do so throws <see cref="InvalidOperationException"/> instead of
/// deadlocking. Keep the locked sections short; they block every other library
/// mutation.
/// </remarks>
public interface ILibraryMutationLock
{
    /// <summary>
    /// Runs <paramref name="action"/> while holding the lock, blocking the
    /// calling thread until the lock is free.
    /// </summary>
    T Execute<T>(Func<T> action, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs <paramref name="action"/> while holding the lock, blocking the
    /// calling thread until the lock is free.
    /// </summary>
    void Execute(Action action, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs <paramref name="action"/> while holding the lock, waiting for the
    /// lock asynchronously so no thread is blocked while another mutation runs.
    /// </summary>
    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default);
}
