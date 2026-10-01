using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Default <see cref="ICondensedBookBuildTracker"/>: keeps condensed-book
/// builds alive independently of the request that started them and records
/// their progress and outcome so the UI can poll for a status.
/// </summary>
/// <remarks>
/// Builds are serialized — condensing is CPU and IO bound, and running several
/// at once only makes them all slower — so a build may sit in
/// <see cref="CondensedBookBuildStatus.Queued"/> first. Finished builds (and
/// the file a successful one produced) are kept for
/// <see cref="Retention"/> so the user can still read the error of a failed
/// build, or retry an interrupted download, and are then swept away. The sweep
/// runs on every public call rather than on a timer, which is enough because
/// the state only matters to a caller that is asking about it.
/// </remarks>
public sealed class CondensedBookBuildTracker : ICondensedBookBuildTracker, IDisposable
{
    /// <summary>How long a finished build (and its file) is kept.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromMinutes(30);

    /// <summary>Finished builds kept per user before the oldest are dropped.</summary>
    private const int MaxRetainedPerOwner = 20;

    private readonly IComicEmailService _email;
    private readonly ILogger<CondensedBookBuildTracker> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _retention;

    private readonly ConcurrentDictionary<Guid, BuildEntry> _builds = new();
    private readonly ConcurrentDictionary<Guid, Task> _workers = new();
    private readonly object _registrationSync = new();
    private bool _disposed;

    // Condensing is CPU/IO heavy, so only one book is built at a time and the
    // rest wait their turn with a visible "queued" status.
    private readonly SemaphoreSlim _buildSlot = new(1, 1);
    private readonly CancellationTokenSource _shutdownCts = new();

    public CondensedBookBuildTracker(
        IComicEmailService email,
        ILogger<CondensedBookBuildTracker> logger)
        : this(email, logger, TimeProvider.System, Retention)
    {
    }

    /// <summary>
    /// Test-only constructor that allows overriding the clock and how long a
    /// finished build is kept. Production callers should use the simpler
    /// overload.
    /// </summary>
    public CondensedBookBuildTracker(
        IComicEmailService email,
        ILogger<CondensedBookBuildTracker> logger,
        TimeProvider timeProvider,
        TimeSpan retention)
    {
        _email = email;
        _logger = logger;
        _timeProvider = timeProvider;
        _retention = retention;
    }

    public async Task<CondensedBookBuildDto> StartAsync(
        CondensedBookBuildRequest request,
        string? ownerUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        PruneExpired();

        // Resolve the format before registering the build so inherited device
        // defaults are reflected in queued/running snapshots and signatures.
        var format = await _email.ResolveCondensedFormatAsync(
            request.DeliveryFormat,
            request.DeviceId,
            cancellationToken);

        // Planning validates the selection and names the book, so an impossible
        // request fails while the caller is still waiting for a response rather
        // than minutes later inside a background build.
        var plan = await _email.PlanCondensedDeliveryAsync(
            request.FilePaths,
            request.CondenseMode,
            request.IssuesPerBook,
            request.PreserveIssueOrder,
            request.DeviceId,
            request.SkipAlreadyDelivered,
            cancellationToken);

        if (request.BookIndex < 0 || request.BookIndex >= plan.Books.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                $"Book {request.BookIndex + 1} does not exist; the selection condenses into {plan.Books.Count} book(s).");
        }

        var book = plan.Books[request.BookIndex];
        var signature = BuildSignature(request with { DeliveryFormat = format }, ownerUserId);

        BuildEntry entry;
        lock (_registrationSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Registration and the active-build lookup are one operation, so
            // simultaneous requests for the same book join the same build.
            var existing = _builds.Values.FirstOrDefault(e =>
                e.Signature == signature && !CondensedBookBuildStatus.IsTerminal(e.Snapshot().Status));
            if (existing is not null)
            {
                return existing.Snapshot();
            }

            entry = new BuildEntry(
                Guid.NewGuid(),
                ownerUserId,
                signature,
                book.DisplayName,
                book.Files.Count,
                _timeProvider.GetUtcNow().UtcDateTime,
                format);

            _builds[entry.BuildId] = entry;
            var worker = Task.Run(
                () => RunAsync(entry, request with { DeliveryFormat = format }),
                CancellationToken.None);
            _workers[entry.BuildId] = worker;
            _ = worker.ContinueWith(
                completedTask => _workers.TryRemove(entry.BuildId, out _),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        _logger.LogInformation(
            "Queued condensed {Format} build {BuildId} for {IssueCount} issue(s)",
            format,
            entry.BuildId,
            book.Files.Count);

        return entry.Snapshot();
    }

    public CondensedBookBuildDto? Get(Guid buildId, string? ownerUserId)
    {
        PruneExpired();
        return TryGetOwned(buildId, ownerUserId, out var entry) ? entry.Snapshot() : null;
    }

    public IReadOnlyList<CondensedBookBuildDto> List(string? ownerUserId)
    {
        PruneExpired();
        return _builds.Values
            .Where(e => IsOwnedBy(e, ownerUserId))
            .Select(e => e.Snapshot())
            .OrderByDescending(b => b.CreatedAt)
            .ToList();
    }

    public bool Cancel(Guid buildId, string? ownerUserId)
    {
        PruneExpired();

        if (!TryGetOwned(buildId, ownerUserId, out var entry))
        {
            return false;
        }

        // Cancelled outside the entry lock: cancellation callbacks run
        // synchronously on this thread and must not re-enter the entry state.
        return entry.RequestCancel();
    }

    public bool Discard(Guid buildId, string? ownerUserId)
    {
        PruneExpired();

        if (!TryGetOwned(buildId, ownerUserId, out var entry))
        {
            return false;
        }

        entry.RequestCancel();
        _builds.TryRemove(buildId, out _);
        Release(entry);
        return true;
    }

    public CondensedBookFile? GetCompletedFile(Guid buildId, string? ownerUserId)
    {
        PruneExpired();

        if (!TryGetOwned(buildId, ownerUserId, out var entry))
        {
            return null;
        }

        var file = entry.CompletedFile();
        if (file is null || !File.Exists(file.FilePath))
        {
            return null;
        }

        return file;
    }

    private async Task RunAsync(BuildEntry entry, CondensedBookBuildRequest request)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            entry.CancellationToken,
            _shutdownCts.Token);

        try
        {
            await _buildSlot.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            entry.MarkCancelled(_timeProvider.GetUtcNow().UtcDateTime, _retention);
            return;
        }

        try
        {
            entry.MarkRunning(_timeProvider.GetUtcNow().UtcDateTime);

            var book = await _email.CreateCondensedBookAsync(
                request.FilePaths,
                request.CondenseMode,
                request.IssuesPerBook,
                request.BookIndex,
                request.PreserveIssueOrder,
                request.DeviceId,
                request.SkipAlreadyDelivered,
                new ConversionProgressSink(entry),
                request.DeliveryFormat,
                linked.Token);

            var size = new FileInfo(book.FilePath).Length;
            entry.MarkCompleted(book, size, _timeProvider.GetUtcNow().UtcDateTime, _retention);

            _logger.LogInformation(
                "Condensed book build {BuildId} finished ({Bytes} bytes)",
                entry.BuildId,
                size);
        }
        catch (OperationCanceledException)
        {
            entry.MarkCancelled(_timeProvider.GetUtcNow().UtcDateTime, _retention);
            _logger.LogInformation("Condensed book build {BuildId} was cancelled", entry.BuildId);
        }
        catch (Exception ex)
        {
            entry.MarkFailed(ex.Message, _timeProvider.GetUtcNow().UtcDateTime, _retention);
            _logger.LogError(ex, "Condensed book build {BuildId} failed", entry.BuildId);
        }
        finally
        {
            _buildSlot.Release();

            // A build discarded while it was still running is no longer tracked,
            // so if it nevertheless produced a file just before it noticed the
            // cancellation, nothing else would ever delete it.
            if (!_builds.ContainsKey(entry.BuildId))
            {
                Release(entry);
            }
        }
    }

    private bool TryGetOwned(Guid buildId, string? ownerUserId, out BuildEntry entry)
    {
        if (_builds.TryGetValue(buildId, out var found) && IsOwnedBy(found, ownerUserId))
        {
            entry = found;
            return true;
        }

        entry = null!;
        return false;
    }

    private static bool IsOwnedBy(BuildEntry entry, string? ownerUserId) =>
        string.Equals(entry.OwnerUserId, ownerUserId, StringComparison.Ordinal);

    /// <summary>
    /// Drops finished builds past their retention, plus the oldest finished
    /// builds of a user who has accumulated too many, and deletes their files.
    /// </summary>
    private void PruneExpired()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        foreach (var entry in _builds.Values)
        {
            if (entry.IsExpired(now) && _builds.TryRemove(entry.BuildId, out _))
            {
                Release(entry);
            }
        }

        foreach (var group in _builds.Values.GroupBy(e => e.OwnerUserId, StringComparer.Ordinal))
        {
            var terminal = group
                .Where(e => CondensedBookBuildStatus.IsTerminal(e.Snapshot().Status))
                .OrderByDescending(e => e.CreatedAt)
                .Skip(MaxRetainedPerOwner)
                .ToList();

            foreach (var entry in terminal)
            {
                if (_builds.TryRemove(entry.BuildId, out _))
                {
                    Release(entry);
                }
            }
        }
    }

    /// <summary>Deletes the work directory of a build that is no longer tracked.</summary>
    private void Release(BuildEntry entry)
    {
        var file = entry.CompletedFile();
        entry.Dispose();

        if (file is null)
        {
            return;
        }

        // The path is server-generated: ComicEmailService writes every download
        // into its own temporary directory, which is what is removed here.
        var directory = Path.GetDirectoryName(file.FilePath);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex,
                "Failed to clean up the condensed book work directory {Directory}",
                LoggingHelper.SanitizePathForLog(directory));
        }
    }

    /// <summary>
    /// Identifies the book a request builds, so the same request arriving twice
    /// joins the build already running instead of starting a second one.
    /// </summary>
    private static string BuildSignature(CondensedBookBuildRequest request, string? ownerUserId)
    {
        var builder = new StringBuilder();
        builder.Append(ownerUserId ?? string.Empty).Append('\n');
        builder.Append(request.CondenseMode ?? string.Empty).Append('\n');
        builder.Append(request.IssuesPerBook?.ToString() ?? string.Empty).Append('\n');
        builder.Append(request.BookIndex).Append('\n');
        builder.Append(request.PreserveIssueOrder).Append('\n');
        builder.Append(request.DeviceId?.ToString() ?? string.Empty).Append('\n');
        builder.Append(request.SkipAlreadyDelivered).Append('\n');
        builder.Append(request.DeliveryFormat ?? string.Empty).Append('\n');
        foreach (var path in request.FilePaths)
        {
            builder.Append(path).Append('\n');
        }

        return System.Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    public void Dispose()
    {
        Task[] workers;
        lock (_registrationSync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            workers = _workers.Values.ToArray();
        }

        try
        {
            _shutdownCts.Cancel();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "An error occurred while cancelling condensed book builds during shutdown");
        }

        try
        {
            Task.WhenAll(workers).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A condensed book build worker failed during shutdown");
        }
        finally
        {
            foreach (var entry in _builds.Values)
            {
                if (_builds.TryRemove(entry.BuildId, out _))
                {
                    Release(entry);
                }
            }

            _shutdownCts.Dispose();
            _buildSlot.Dispose();
        }
    }

    /// <summary>
    /// Forwards converter progress onto the tracked build. Deliberately
    /// allocation-light and non-throwing: it is called once per page.
    /// </summary>
    private sealed class ConversionProgressSink : IProgress<EpubConversionProgress>
    {
        private readonly BuildEntry _entry;

        public ConversionProgressSink(BuildEntry entry) => _entry = entry;

        public void Report(EpubConversionProgress value) => _entry.ReportProgress(value);
    }

    /// <summary>Mutable state of one tracked build.</summary>
    private sealed class BuildEntry : IDisposable
    {
        private readonly object _sync = new();
        private readonly CancellationTokenSource _cts = new();

        private string _status = CondensedBookBuildStatus.Queued;
        private CondensedBookBuildProgress _progress = CondensedBookBuildProgress.Empty;
        private DateTime? _startedAt;
        private DateTime? _completedAt;
        private DateTime? _expiresAt;
        private string? _error;
        private CondensedBookFile? _file;
        private long? _fileSizeBytes;
        private string _deliveryFormat;
        private bool _disposed;

        public BuildEntry(
            Guid buildId,
            string? ownerUserId,
            string signature,
            string displayName,
            int issueCount,
            DateTime createdAt,
            string deliveryFormat)
        {
            BuildId = buildId;
            OwnerUserId = ownerUserId;
            Signature = signature;
            DisplayName = displayName;
            IssueCount = issueCount;
            CreatedAt = createdAt;
            _deliveryFormat = deliveryFormat;
        }

        public Guid BuildId { get; }
        public string? OwnerUserId { get; }
        public string Signature { get; }
        public string DisplayName { get; }
        public int IssueCount { get; }
        public DateTime CreatedAt { get; }

        public CancellationToken CancellationToken => _cts.Token;

        public CondensedBookBuildDto Snapshot()
        {
            lock (_sync)
            {
                return new CondensedBookBuildDto(
                    BuildId,
                    _status,
                    DisplayName,
                    IssueCount,
                    _progress,
                    CreatedAt,
                    _startedAt,
                    _completedAt,
                    _error,
                    _fileSizeBytes,
                    _expiresAt,
                    _deliveryFormat);
            }
        }

        public CondensedBookFile? CompletedFile()
        {
            lock (_sync)
            {
                return _status == CondensedBookBuildStatus.Completed ? _file : null;
            }
        }

        public bool IsExpired(DateTime now)
        {
            lock (_sync)
            {
                return _expiresAt is DateTime expires && expires <= now;
            }
        }

        public void ReportProgress(EpubConversionProgress value)
        {
            var phase = value.Phase switch
            {
                EpubConversionPhase.Reading => "reading",
                EpubConversionPhase.Writing => "writing",
                EpubConversionPhase.Validating => "validating",
                EpubConversionPhase.Recompressing => "recompressing",
                _ => "writing"
            };

            lock (_sync)
            {
                if (CondensedBookBuildStatus.IsTerminal(_status))
                {
                    return;
                }

                _progress = new CondensedBookBuildProgress(
                    phase,
                    value.CompletedPages,
                    value.TotalPages,
                    value.CompletedIssues,
                    value.TotalIssues,
                    value.CurrentIssue,
                    value.Pass,
                    value.TotalPasses);
            }
        }

        public void MarkRunning(DateTime now)
        {
            lock (_sync)
            {
                if (CondensedBookBuildStatus.IsTerminal(_status))
                {
                    return;
                }

                _status = CondensedBookBuildStatus.Running;
                _startedAt ??= now;
            }
        }

        public void MarkCompleted(CondensedBookFile file, long sizeBytes, DateTime now, TimeSpan retention)
        {
            lock (_sync)
            {
                _status = CondensedBookBuildStatus.Completed;
                _file = file;
                // The builder resolves the format (a request can inherit the
                // device default), so the finished file is what the caller is
                // told it got.
                _deliveryFormat = string.Equals(
                    Path.GetExtension(file.FilePath),
                    ".azw3",
                    StringComparison.OrdinalIgnoreCase)
                    ? EmailDeliveryFormat.Azw3
                    : EmailDeliveryFormat.Epub;
                _fileSizeBytes = sizeBytes;
                _completedAt = now;
                _expiresAt = now.Add(retention);
                _progress = _progress with
                {
                    Phase = "completed",
                    CompletedPages = _progress.TotalPages
                };
            }
        }

        public void MarkFailed(string error, DateTime now, TimeSpan retention)
        {
            lock (_sync)
            {
                _status = CondensedBookBuildStatus.Failed;
                _error = error;
                _completedAt = now;
                _expiresAt = now.Add(retention);
            }
        }

        public void MarkCancelled(DateTime now, TimeSpan retention)
        {
            lock (_sync)
            {
                _status = CondensedBookBuildStatus.Cancelled;
                _completedAt = now;
                _expiresAt = now.Add(retention);
            }
        }

        /// <summary>
        /// Signals the build to stop. Returns false when it has already
        /// finished, so the caller can report that there was nothing to cancel.
        /// </summary>
        public bool RequestCancel()
        {
            lock (_sync)
            {
                if (_disposed || CondensedBookBuildStatus.IsTerminal(_status))
                {
                    return false;
                }
            }

            try
            {
                _cts.Cancel();
                return true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        public void Dispose()
        {
            bool disposeCts;
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;

                // A build that has not finished yet is still holding this token
                // through a linked source, so the source is left for the
                // garbage collector instead of being pulled out from under it.
                disposeCts = CondensedBookBuildStatus.IsTerminal(_status);
            }

            if (disposeCts)
            {
                _cts.Dispose();
            }
        }
    }
}
