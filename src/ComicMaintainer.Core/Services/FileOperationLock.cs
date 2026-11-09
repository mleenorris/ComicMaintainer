using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Default <see cref="IFileOperationLock"/>: one <see cref="SemaphoreSlim"/> per file that is
/// locked or waited for. A file's entry is removed when its last holder or waiter leaves, so
/// the number of entries is bounded by the operations in progress rather than growing with
/// every file ever touched.
/// </summary>
public sealed class FileOperationLock : IFileOperationLock
{
    private readonly object _sync = new();

    // Case is ignored on every platform, see IFileOperationLock: sharing a lock between two
    // names that a case-sensitive file system keeps apart only serializes them.
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Number of files currently locked or waited for (for tests and diagnostics).</summary>
    internal int ActiveKeyCount
    {
        get
        {
            lock (_sync)
            {
                return _entries.Count;
            }
        }
    }

    /// <inheritdoc />
    public bool HasActiveOperations => ActiveKeyCount > 0;

    /// <inheritdoc />
    public async ValueTask<IDisposable> AcquireAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var key = GetKey(filePath);
        Entry entry;
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new Entry();
                _entries[key] = entry;
            }

            entry.References++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Leave(key, entry, held: false);
            throw;
        }

        return new Lease(this, key, entry);
    }

    /// <summary>
    /// The key two paths share exactly when they lead to the same file: the full path with
    /// links resolved and no trailing separator. A path whose links cannot be read keeps its
    /// lexical form, so the lock still works (at worst it misses an alias through a link).
    /// </summary>
    internal static string GetKey(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        try
        {
            fullPath = LibraryPathValidator.ResolveRealPath(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Too many links or an unreadable segment: fall back to the lexical path.
        }

        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    private void Leave(string key, Entry entry, bool held)
    {
        if (held)
        {
            entry.Semaphore.Release();
        }

        lock (_sync)
        {
            // Waiters register under _sync before they wait, so a count of zero means nobody
            // holds or waits for this entry and it can go.
            if (--entry.References == 0)
            {
                _entries.Remove(key);
                entry.Semaphore.Dispose();
            }
        }
    }

    private sealed class Entry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);

        /// <summary>Holders plus waiters, guarded by <see cref="FileOperationLock._sync"/>.</summary>
        public int References;
    }

    private sealed class Lease : IDisposable
    {
        private readonly FileOperationLock _owner;
        private readonly string _key;
        private readonly Entry _entry;
        private int _disposed;

        public Lease(FileOperationLock owner, string key, Entry entry)
        {
            _owner = owner;
            _key = key;
            _entry = entry;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.Leave(_key, _entry, held: true);
            }
        }
    }
}
