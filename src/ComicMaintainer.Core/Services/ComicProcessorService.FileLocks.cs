namespace ComicMaintainer.Core.Services;

public partial class ComicProcessorService
{
    /// <summary>
    /// Runs a file operation while holding <paramref name="filePath"/>'s
    /// <see cref="Interfaces.IFileOperationLock"/> and one of the <see cref="_processingSemaphore"/>
    /// slots shared by the watcher, the API and every batch job.
    /// <para>
    /// Every public operation that rewrites, renames or deletes an archive, and every per-file
    /// step of a built-in batch job, goes through here, and the *CoreAsync methods it runs read
    /// the file only after the lock is taken. So the same archive is never read, rewritten and
    /// renamed by two operations at once, and the total number of archive operations in flight
    /// stays bounded by <see cref="Configuration.AppSettings.MaxWorkers"/> however many jobs run.
    /// </para>
    /// <para>
    /// The file lock is taken before the slot: a waiter for a busy file then holds no slot, so
    /// files that are free keep being processed, and since nothing that holds a slot waits for a
    /// file lock, the two cannot deadlock. Code running here must not call a public operation of
    /// this service on the same file (the file lock is not reentrant); the *CoreAsync methods
    /// call each other directly instead.
    /// </para>
    /// </summary>
    private async Task<T> RunExclusiveAsync<T>(
        string filePath,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        using (await _fileLock.AcquireAsync(filePath, cancellationToken))
        {
            await _processingSemaphore.WaitAsync(cancellationToken);
            try
            {
                return await operation(cancellationToken);
            }
            finally
            {
                _processingSemaphore.Release();
            }
        }
    }
}
