namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Per-file mutual exclusion for operations that rewrite, rename or delete a comic archive.
/// <para>
/// Each such operation streams the archive into a temporary file and then replaces or moves
/// the original, so two of them running on the same file at once lose an update: the last
/// replace wins, and a rename can move the file between another writer's read and its replace.
/// Every component that changes an archive on disk (processing, rename, normalize, metadata
/// edits and removal, deletion, and the embedded series cover) therefore holds the file's lock
/// from the moment it reads the file until it has finished writing, renaming and recording it.
/// </para>
/// </summary>
/// <remarks>
/// Paths that lead to the same file share one lock: they are compared after resolving links
/// and normalizing the path, ignoring case. Ignoring case is what makes the lock hold on
/// case-insensitive file systems (Windows, macOS, most network shares); on a case-sensitive
/// one, two files whose names differ only in case merely share a lock, which serializes them
/// without affecting correctness.
/// The lock is not reentrant: code that holds a file's lock must not wait for the same file's
/// lock again, for example by calling a public file operation of the processor on it.
/// </remarks>
public interface IFileOperationLock
{
    /// <summary>
    /// Waits until no other operation holds the lock of <paramref name="filePath"/>, then takes
    /// it. Dispose the returned lease to release it.
    /// </summary>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled before the lock was taken.
    /// </exception>
    ValueTask<IDisposable> AcquireAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>True while any file's lock is held or waited for.</summary>
    bool HasActiveOperations => false;
}
