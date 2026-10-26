using System.Threading.Channels;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Default <see cref="ISeriesArchiveCoverWriteQueue"/> implementation. Backs the
/// queue with an unbounded <see cref="Channel{T}"/> drained by a single
/// long-running consumer task so archive rewrites happen off the caller's
/// thread and never overlap each other.
/// </summary>
public class SeriesArchiveCoverWriteQueue : ISeriesArchiveCoverWriteQueue
{
    private readonly ISeriesArchiveCoverWriter _writer;
    private readonly ILogger<SeriesArchiveCoverWriteQueue> _logger;

    private readonly object _lifecycleLock = new();

    // Replaced on every stop, so requests queued while stopped wait for the next start.
    private volatile Channel<CoverWriteRequest> _channel = CreateChannel();

    // Set while the queue runs.
    private CancellationTokenSource? _stoppingCts;

    // The latest consumer, which may still be draining after a stop.
    private Task _consumer = Task.CompletedTask;

    public SeriesArchiveCoverWriteQueue(
        ISeriesArchiveCoverWriter writer,
        ILogger<SeriesArchiveCoverWriteQueue> logger)
    {
        _writer = writer;
        _logger = logger;
    }

    public void EnqueueWrite(
        string normalizedKey,
        string sourceFilePath,
        string contentType,
        bool force = false)
    {
        if (string.IsNullOrWhiteSpace(normalizedKey))
        {
            return;
        }

        _channel.Writer.TryWrite(CoverWriteRequest.ForWrite(
            normalizedKey, sourceFilePath, contentType, force));
    }

    public void EnqueueRemove(string normalizedKey)
    {
        if (string.IsNullOrWhiteSpace(normalizedKey))
        {
            return;
        }

        _channel.Writer.TryWrite(CoverWriteRequest.ForRemove(normalizedKey));
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_lifecycleLock)
        {
            if (_stoppingCts is not null)
            {
                return Task.CompletedTask;
            }

            // Each run works on its own token source and channel, so a stop or
            // restart replacing the fields cannot affect a run already going.
            var stoppingCts = new CancellationTokenSource();
            var reader = _channel.Reader;
            var previous = _consumer;
            _stoppingCts = stoppingCts;
            _consumer = Task.Run(() => ConsumeAsync(previous, reader, stoppingCts.Token), CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task consumer;
        CancellationTokenSource stoppingCts;
        Channel<CoverWriteRequest> channel;
        lock (_lifecycleLock)
        {
            if (_stoppingCts is null)
            {
                return;
            }

            consumer = _consumer;
            stoppingCts = _stoppingCts;
            channel = _channel;
            _stoppingCts = null;
            _channel = CreateChannel();
        }

        // Signal that no more work will be queued for this run. The consumer keeps
        // draining any already-queued items and then exits, so a graceful shutdown
        // does not silently drop pending cover writes.
        channel.Writer.TryComplete();

        try
        {
            // If the host's shutdown deadline elapses before the queue drains,
            // cancel the consumer so shutdown is not blocked indefinitely.
            using (cancellationToken.Register(() => stoppingCts.Cancel()))
            {
                await consumer;
            }
        }
        finally
        {
            stoppingCts.Dispose();
        }
    }

    private static Channel<CoverWriteRequest> CreateChannel() =>
        Channel.CreateUnbounded<CoverWriteRequest>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    private async Task ConsumeAsync(Task previous, ChannelReader<CoverWriteRequest> reader, CancellationToken cancellationToken)
    {
        // A restart can overlap the previous run still draining; wait for it so
        // archive rewrites never overlap. If this run is stopped meanwhile, the
        // read below ends it.
        await previous.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        try
        {
            await foreach (var request in reader.ReadAllAsync(cancellationToken))
            {
                await ProcessAsync(request, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Series archive cover write queue consumer terminated unexpectedly");
        }
    }

    private async Task ProcessAsync(CoverWriteRequest request, CancellationToken cancellationToken)
    {
        try
        {
            if (request.IsRemove)
            {
                await _writer.RemoveAsync(request.NormalizedKey, cancellationToken);
            }
            else
            {
                await _writer.WriteAsync(
                    request.NormalizedKey,
                    request.SourceFilePath!,
                    request.ContentType!,
                    request.Force,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Failed to {Operation} embedded first-archive series cover for {Key}",
                request.IsRemove ? "remove" : "write",
                LoggingHelper.SanitizeForLog(request.NormalizedKey));
        }
    }

    private sealed record CoverWriteRequest(
        bool IsRemove,
        string NormalizedKey,
        string? SourceFilePath,
        string? ContentType,
        bool Force)
    {
        public static CoverWriteRequest ForWrite(
            string normalizedKey, string sourceFilePath, string contentType, bool force) =>
            new(false, normalizedKey, sourceFilePath, contentType, force);

        public static CoverWriteRequest ForRemove(string normalizedKey) =>
            new(true, normalizedKey, null, null, false);
    }
}
