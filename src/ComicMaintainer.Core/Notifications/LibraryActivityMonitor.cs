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
/// Lets a long run that changes library files one at a time say it is still going. Such a run
/// holds a file lock only during each file and is not a batch job, so without this the library
/// looks idle between two of its files.
/// </summary>
public interface ILibraryActivityTracker
{
    /// <summary>True while at least one run is going.</summary>
    bool IsRunActive { get; }

    /// <summary>Marks a run as started; it counts until the returned scope is disposed.</summary>
    IDisposable BeginRun();
}

/// <summary>Default <see cref="ILibraryActivityTracker"/>: a count of the runs in progress.</summary>
public sealed class LibraryActivityTracker : ILibraryActivityTracker
{
    private int _activeRuns;

    public bool IsRunActive => Volatile.Read(ref _activeRuns) > 0;

    public IDisposable BeginRun()
    {
        Interlocked.Increment(ref _activeRuns);
        return new Run(this);
    }

    private sealed class Run(LibraryActivityTracker owner) : IDisposable
    {
        private int _ended;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0)
            {
                Interlocked.Decrement(ref owner._activeRuns);
            }
        }
    }
}

/// <summary>
/// Default <see cref="ILibraryActivityMonitor"/>, consulted just before a media server is asked
/// to scan. Every archive rewrite holds a file lock, every folder change holds the library lock,
/// every batch job shows in the job registry, every long run that goes file by file outside a
/// batch job is in <see cref="ILibraryActivityTracker"/>, and files the watcher has queued but
/// not processed are flagged in the database. When none of those hold, nothing is being
/// rewritten.
/// </summary>
public sealed class LibraryActivityMonitor : ILibraryActivityMonitor
{
    private readonly IFileOperationLock _fileLock;
    private readonly ILibraryMutationLock _mutationLock;
    private readonly IBatchJobRegistry _jobs;
    private readonly ILibraryActivityTracker _runs;
    private readonly IFileWatcherService _watcher;
    private readonly IDbContextFactory<ComicMaintainerDbContext> _dbFactory;

    public LibraryActivityMonitor(
        IFileOperationLock fileLock,
        ILibraryMutationLock mutationLock,
        IBatchJobRegistry jobs,
        ILibraryActivityTracker runs,
        IFileWatcherService watcher,
        IDbContextFactory<ComicMaintainerDbContext> dbFactory)
    {
        _fileLock = fileLock;
        _mutationLock = mutationLock;
        _jobs = jobs;
        _runs = runs;
        _watcher = watcher;
        _dbFactory = dbFactory;
    }

    public async Task<bool> IsBusyAsync(CancellationToken cancellationToken)
    {
        if (_fileLock.HasActiveOperations || _mutationLock.IsHeld || _runs.IsRunActive || _jobs.GetActiveJob() is not null)
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
