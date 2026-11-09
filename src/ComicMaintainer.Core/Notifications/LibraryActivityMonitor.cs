using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ComicMaintainer.Core.Notifications;

/// <summary>Tells whether the library is being changed right now.</summary>
public interface ILibraryActivityMonitor
{
    /// <summary>True while a file is being rewritten or moved, or is queued to be.</summary>
    Task<bool> IsBusyAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Default <see cref="ILibraryActivityMonitor"/>, consulted just before a media server is asked
/// to scan. Every archive rewrite holds a file lock, every folder change holds the library lock,
/// every batch job shows in the job registry, and files the watcher has queued but not processed
/// are flagged in the database. When none of those hold, nothing is being rewritten.
/// </summary>
public sealed class LibraryActivityMonitor : ILibraryActivityMonitor
{
    private readonly IFileOperationLock _fileLock;
    private readonly ILibraryMutationLock _mutationLock;
    private readonly IBatchJobRegistry _jobs;
    private readonly IFileWatcherService _watcher;
    private readonly IDbContextFactory<ComicMaintainerDbContext> _dbFactory;

    public LibraryActivityMonitor(
        IFileOperationLock fileLock,
        ILibraryMutationLock mutationLock,
        IBatchJobRegistry jobs,
        IFileWatcherService watcher,
        IDbContextFactory<ComicMaintainerDbContext> dbFactory)
    {
        _fileLock = fileLock;
        _mutationLock = mutationLock;
        _jobs = jobs;
        _watcher = watcher;
        _dbFactory = dbFactory;
    }

    public async Task<bool> IsBusyAsync(CancellationToken cancellationToken)
    {
        if (_fileLock.HasActiveOperations || _mutationLock.IsHeld || _jobs.GetActiveJob() is not null)
        {
            return true;
        }

        // A stopped watcher leaves its flags set until it next starts, and nothing
        // is working on those files in the meantime.
        if (!_watcher.IsRunning)
        {
            return false;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.ComicFiles.AnyAsync(file => file.IsProcessingPending, cancellationToken);
    }
}
