using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// What the file watcher needs from the store to catch up with changes it could not see: the
/// durable record of the directory it last scanned completely, and holding off the stale-row
/// cleanup while it works out which missing files were moved.
/// </summary>
public partial class FileStoreService
{
    private readonly object _staleCleanupLock = new();
    private int _staleCleanupSuspensions;

    /// <summary>Completed when the last suspension ends; null while none is active.</summary>
    private TaskCompletionSource? _staleCleanupResumed;

    /// <inheritdoc />
    public async Task<bool> IsLibraryScannedAsync(string directory, CancellationToken cancellationToken = default)
    {
        try
        {
            var path = NormalizeScannedDirectory(directory);
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            var scanned = await dbContext.LibraryScans
                .AsNoTracking()
                .Select(s => s.Directory)
                .ToListAsync(cancellationToken);

            return scanned.Any(root => LibraryPathValidator.IsSameOrUnder(path, root));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not OperationCanceledException)
        {
            // Unknown means "not scanned": the worst case is files added without being processed.
            _logger.LogError(ex, "Error reading whether {Directory} has been scanned", SanitizeForLogging(directory));
            return false;
        }
    }

    /// <inheritdoc />
    public async Task RecordLibraryScannedAsync(string directory, CancellationToken cancellationToken = default)
    {
        try
        {
            var path = NormalizeScannedDirectory(directory);
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            var rows = await dbContext.LibraryScans.ToListAsync(cancellationToken);

            var row = rows.FirstOrDefault(r => string.Equals(r.Directory, path, StringComparison.Ordinal));
            if (row is null)
            {
                dbContext.LibraryScans.RemoveRange(rows);
                dbContext.LibraryScans.Add(new LibraryScanEntity { Directory = path, CompletedAt = DateTime.UtcNow });
            }
            else
            {
                dbContext.LibraryScans.RemoveRange(rows.Where(r => r != row));
                row.CompletedAt = DateTime.UtcNow;
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error recording that {Directory} has been scanned", SanitizeForLogging(directory));
        }
    }

    /// <inheritdoc />
    public async Task ForgetLibraryScanAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            var rows = await dbContext.LibraryScans.ToListAsync(cancellationToken);
            if (rows.Count > 0)
            {
                dbContext.LibraryScans.RemoveRange(rows);
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error forgetting the last library scan");
        }
    }

    /// <inheritdoc />
    public bool IsStaleCleanupSuspended
    {
        get
        {
            lock (_staleCleanupLock)
            {
                return _staleCleanupSuspensions > 0;
            }
        }
    }

    /// <inheritdoc />
    public IDisposable SuspendStaleCleanup()
    {
        lock (_staleCleanupLock)
        {
            _staleCleanupSuspensions++;
            _staleCleanupResumed ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        return new StaleCleanupSuspension(this);
    }

    private void ResumeStaleCleanup()
    {
        lock (_staleCleanupLock)
        {
            if (--_staleCleanupSuspensions == 0)
            {
                _staleCleanupResumed?.TrySetResult();
                _staleCleanupResumed = null;
            }
        }
    }

    /// <summary>
    /// Waits until no <see cref="SuspendStaleCleanup"/> lease is active, including one taken
    /// while an earlier one was being waited for.
    /// </summary>
    private async Task WaitForStaleCleanupToResumeAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task? resumed;
            lock (_staleCleanupLock)
            {
                resumed = _staleCleanupResumed?.Task;
            }

            if (resumed is null)
            {
                return;
            }

            _logger.LogInformation("Stale entry cleanup is waiting for the file watcher to finish catching up with the disk");
            await resumed.WaitAsync(cancellationToken);
        }
    }

    private static string NormalizeScannedDirectory(string directory) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));

    private sealed class StaleCleanupSuspension(FileStoreService store) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                store.ResumeStaleCleanup();
            }
        }
    }
}
