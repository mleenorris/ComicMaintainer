namespace ComicMaintainer.Core.Utilities;

/// <summary>
/// Keeps forward passes over solid RAR archives open between page requests, so a reader
/// turning the pages of a solid CBR decompresses the archive once instead of from the start
/// for every page.
/// </summary>
/// <remarks>
/// <para>
/// A page of a solid RAR can only be decompressed after every entry before it. Opened afresh
/// per request, page N costs N entries, and a reader that prefetches the pages around the one
/// it shows asks for many at once, each redoing the same prefix with its own window. Here one
/// pass per file version (path, length and last-write time) is kept, and requests for the same
/// archive take turns on it: the next to go is the nearest page after the pass's position, so a
/// prefetch burst walks the archive forward once. A request for a page at or before the position
/// starts a new pass.
/// </para>
/// <para>
/// Every pass is a new <see cref="ComicArchiveReader.SolidRarCursor"/>, with all the archive's
/// headers checked and its own decompression budget, so every read limit applies per pass exactly
/// as it does to a rewrite. A pass that fails, is cancelled, or finds the file changed is closed,
/// never reused.
/// </para>
/// <para>
/// The pool is bounded: at most <c>maxCursors</c> idle passes, holding at most <c>maxBytes</c>
/// of estimated memory (mostly the decompression window each keeps). A pass that does not fit
/// once the least recently used idle ones have been closed is closed after its read. A pass
/// left idle for <c>idleTimeout</c> is closed, which also lets go of the file.
/// </para>
/// </remarks>
internal sealed class SolidRarCursorPool : IDisposable
{
    /// <summary>Default number of idle passes kept.</summary>
    public const int DefaultMaxCursors = 4;

    /// <summary>Default memory the kept passes may hold: 256 MiB.</summary>
    public const long DefaultMaxBytes = 256L * 1024 * 1024;

    /// <summary>Default time an unused pass is kept open.</summary>
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(60);

    private readonly object _lock = new();
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private readonly int _maxCursors;
    private readonly long _maxBytes;
    private readonly TimeSpan _idleTimeout;
    private readonly TimeProvider _time;
    private readonly ITimer _sweeper;
    private readonly Func<string, ComicArchiveReader.SolidRarCursor> _open;
    private int _cursorsOpened;
    private int _entriesDecompressed;
    private bool _disposed;

    public SolidRarCursorPool()
        : this(DefaultMaxCursors, DefaultMaxBytes, DefaultIdleTimeout, TimeProvider.System)
    {
    }

    internal SolidRarCursorPool(
        int maxCursors,
        long maxBytes,
        TimeSpan idleTimeout,
        TimeProvider time,
        Func<string, ComicArchiveReader.SolidRarCursor>? open = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxCursors);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        _maxCursors = maxCursors;
        _maxBytes = maxBytes;
        _idleTimeout = idleTimeout;
        _time = time;
        _open = open ?? ComicArchiveReader.OpenSolidCursor;
        var sweepEvery = TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerSecond, idleTimeout.Ticks / 4));
        _sweeper = time.CreateTimer(_ => CloseIdle(), null, sweepEvery, sweepEvery);
    }

    /// <summary>How many passes have been opened (for tests).</summary>
    internal int CursorsOpened => Volatile.Read(ref _cursorsOpened);

    /// <summary>How many entries all passes have decompressed, read or passed over (for tests).</summary>
    internal int EntriesDecompressed => Volatile.Read(ref _entriesDecompressed);

    /// <summary>How many passes are open right now, in use or idle (for tests).</summary>
    internal int OpenCursorCount
    {
        get
        {
            lock (_lock)
            {
                return _slots.Values.Count(s => s.Cursor is not null);
            }
        }
    }

    /// <summary>How many requests for <paramref name="path"/> are waiting for their turn (for tests).</summary>
    internal int WaitingCount(string path)
    {
        lock (_lock)
        {
            return _slots.TryGetValue(path, out var slot) ? slot.Waiters.Count : 0;
        }
    }

    /// <summary>
    /// Reads the page at entry <paramref name="position"/> of the solid RAR at
    /// <paramref name="path"/>, whose length and last-write time are those given, through the
    /// pass kept for it. Waits for any other request on the same archive first. Returns
    /// <c>null</c> when the archive does not hold <paramref name="expectedName"/> there (it
    /// changed since its pages were listed).
    /// </summary>
    /// <exception cref="ArchiveLimitExceededException">The page or an entry before it exceeds a limit.</exception>
    /// <exception cref="OperationCanceledException">The request was cancelled, waiting or reading.</exception>
    public async Task<RentedBufferStream?> ReadPageAsync(
        string path,
        long length,
        long lastWriteUtcTicks,
        int position,
        string expectedName,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        Slot slot;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_slots.TryGetValue(path, out slot!))
            {
                slot = new Slot(path);
                _slots.Add(path, slot);
            }

            slot.Users++;
        }

        try
        {
            await AcquireAsync(slot, position, cancellationToken).ConfigureAwait(false);
            try
            {
                return await Task.Run(
                    () => ReadOnTurn(slot, (length, lastWriteUtcTicks), position, expectedName, maxBytes, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Release(slot);
            }
        }
        finally
        {
            lock (_lock)
            {
                slot.Users--;
                RemoveIfUnused(slot);
            }
        }
    }

    /// <summary>The read itself, by the one request whose turn it is on <paramref name="slot"/>.</summary>
    private RentedBufferStream? ReadOnTurn(
        Slot slot,
        (long Length, long LastWriteUtcTicks) version,
        int position,
        string expectedName,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        var cursor = TakeCursor(slot, version, position);
        if (cursor is null)
        {
            cursor = _open(slot.Path);
            Interlocked.Increment(ref _cursorsOpened);
        }

        var before = cursor.EntriesDecompressed;
        RentedBufferStream? page = null;
        try
        {
            page = cursor.ReadPage(position, expectedName, maxBytes, cancellationToken);
        }
        finally
        {
            Interlocked.Add(ref _entriesDecompressed, cursor.EntriesDecompressed - before);
            if (page is null)
            {
                // Failed, cancelled or out of date: where the pass stands is unknown or useless.
                cursor.Dispose();
            }
        }

        if (page is not null)
        {
            Keep(slot, version, cursor);
        }

        return page;
    }

    /// <summary>
    /// Takes the slot's pass for a read at <paramref name="position"/> when it is for this version
    /// of the file and has not passed that position yet; closes it otherwise.
    /// </summary>
    private ComicArchiveReader.SolidRarCursor? TakeCursor(Slot slot, (long, long) version, int position)
    {
        ComicArchiveReader.SolidRarCursor? cursor;
        lock (_lock)
        {
            cursor = slot.Cursor;
            slot.Cursor = null;
        }

        if (cursor is not null && (slot.Version != version || cursor.Position >= position))
        {
            cursor.Dispose();
            cursor = null;
        }

        return cursor;
    }

    /// <summary>
    /// Puts a pass back on its slot for the next read, closing the least recently used idle passes
    /// that no longer fit, or this one when it does not fit even so.
    /// </summary>
    private void Keep(Slot slot, (long, long) version, ComicArchiveReader.SolidRarCursor cursor)
    {
        var toClose = new List<ComicArchiveReader.SolidRarCursor>();
        lock (_lock)
        {
            var others = _slots.Values.Where(s => s != slot && s.Cursor is not null).ToList();
            var count = others.Count + 1;
            var bytes = others.Sum(s => s.Cursor!.EstimatedBytes) + cursor.EstimatedBytes;
            foreach (var idle in others.Where(s => !s.Busy).OrderBy(s => s.LastUsedTicks))
            {
                if (count <= _maxCursors && bytes <= _maxBytes)
                {
                    break;
                }

                toClose.Add(idle.Cursor!);
                bytes -= idle.Cursor!.EstimatedBytes;
                count--;
                idle.Cursor = null;
                RemoveIfUnused(idle);
            }

            if (_disposed || count > _maxCursors || bytes > _maxBytes)
            {
                toClose.Add(cursor);
            }
            else
            {
                slot.Cursor = cursor;
                slot.Version = version;
                slot.LastUsedTicks = _time.GetTimestamp();
            }
        }

        foreach (var closing in toClose)
        {
            closing.Dispose();
        }
    }

    /// <summary>Waits for the turn on <paramref name="slot"/>.</summary>
    private async Task AcquireAsync(Slot slot, int position, CancellationToken cancellationToken)
    {
        Waiter waiter;
        lock (_lock)
        {
            if (!slot.Busy)
            {
                slot.Busy = true;
                return;
            }

            waiter = new Waiter(position);
            slot.Waiters.Add(waiter);
        }

        using (cancellationToken.Register(() =>
        {
            lock (_lock)
            {
                // Only a request still waiting gives up its place; one handed the turn already
                // owns it, and its read stops on the same token.
                if (slot.Waiters.Remove(waiter))
                {
                    waiter.Turn.TrySetCanceled(cancellationToken);
                }
            }
        }))
        {
            await waiter.Turn.Task.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Hands the turn on <paramref name="slot"/> to the waiting request whose page comes next after
    /// the pass's position, so the pass keeps moving forward; failing that, the lowest page, which
    /// starts the next pass from as early as anyone needs.
    /// </summary>
    private void Release(Slot slot)
    {
        lock (_lock)
        {
            var reached = slot.Cursor?.Position ?? -1;
            var next = slot.Waiters.Where(w => w.Position > reached).MinBy(w => w.Position)
                ?? slot.Waiters.MinBy(w => w.Position);
            if (next is null)
            {
                slot.Busy = false;
                return;
            }

            slot.Waiters.Remove(next);
            next.Turn.TrySetResult();
        }
    }

    /// <summary>Closes the passes nobody has used for the idle timeout.</summary>
    internal void CloseIdle()
    {
        var toClose = new List<ComicArchiveReader.SolidRarCursor>();
        lock (_lock)
        {
            foreach (var slot in _slots.Values
                .Where(s => !s.Busy && s.Cursor is not null && _time.GetElapsedTime(s.LastUsedTicks) >= _idleTimeout)
                .ToList())
            {
                toClose.Add(slot.Cursor!);
                slot.Cursor = null;
                RemoveIfUnused(slot);
            }
        }

        foreach (var cursor in toClose)
        {
            cursor.Dispose();
        }
    }

    /// <summary>Drops a slot with no pass, no turn taken and nobody waiting. Called under the lock.</summary>
    private void RemoveIfUnused(Slot slot)
    {
        if (slot.Cursor is null && !slot.Busy && slot.Users == 0 && slot.Waiters.Count == 0)
        {
            _slots.Remove(slot.Path);
        }
    }

    public void Dispose()
    {
        List<ComicArchiveReader.SolidRarCursor> toClose;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            toClose = _slots.Values.Where(s => s.Cursor is not null).Select(s => s.Cursor!).ToList();
            foreach (var slot in _slots.Values)
            {
                slot.Cursor = null;
            }
        }

        _sweeper.Dispose();
        foreach (var cursor in toClose)
        {
            cursor.Dispose();
        }
    }

    /// <summary>One archive's pass and the requests taking turns on it.</summary>
    private sealed class Slot(string path)
    {
        public string Path { get; } = path;

        public ComicArchiveReader.SolidRarCursor? Cursor { get; set; }

        public (long Length, long LastWriteUtcTicks) Version { get; set; }

        public long LastUsedTicks { get; set; }

        /// <summary>True while a request has the turn.</summary>
        public bool Busy { get; set; }

        /// <summary>Requests in <see cref="ReadPageAsync"/> for this archive, waiting or not.</summary>
        public int Users { get; set; }

        public List<Waiter> Waiters { get; } = new();
    }

    private sealed class Waiter(int position)
    {
        public int Position { get; } = position;

        public TaskCompletionSource Turn { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
