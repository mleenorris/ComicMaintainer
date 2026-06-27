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

    private readonly Channel<CoverWriteRequest> _channel =
        Channel.CreateUnbounded<CoverWriteRequest>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    private readonly object _lifecycleLock = new();
    private CancellationTokenSource? _stoppingCts;
    private Task? _consumer;

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
            if (_consumer is not null)
            {
                return Task.CompletedTask;
            }

            _stoppingCts = new CancellationTokenSource();
            _consumer = Task.Run(() => ConsumeAsync(_stoppingCts.Token), CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? consumer;
        CancellationTokenSource? stoppingCts;
        lock (_lifecycleLock)
        {
            if (_consumer is null)
            {
                return;
            }

            consumer = _consumer;
            stoppingCts = _stoppingCts;
            _consumer = null;
        }

        // Signal that no more work will be queued. The consumer keeps draining
        // any already-queued items and then exits, so a graceful shutdown does
        // not silently drop pending cover writes.
        _channel.Writer.TryComplete();

        // If the host's shutdown deadline elapses before the queue drains,
        // cancel the consumer so shutdown is not blocked indefinitely.
        using var registration = cancellationToken.Register(() => stoppingCts?.Cancel());

        try
        {
            await consumer;
        }
        catch (OperationCanceledException)
        {
            // Shutdown deadline fired before the consumer finished draining.
        }
        finally
        {
            stoppingCts?.Dispose();
            lock (_lifecycleLock)
            {
                _stoppingCts = null;
            }
        }
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var request in _channel.Reader.ReadAllAsync(cancellationToken))
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
