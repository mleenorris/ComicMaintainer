using System.Threading.Channels;
using ComicMaintainer.Core.ErrorReporting.Models;

namespace ComicMaintainer.WebApi.Logging;

/// <summary>
/// A single error awaiting capture, handed from the logging pipeline — or from
/// the global exception handler — to the background dispatcher.
/// </summary>
/// <remarks>
/// <paramref name="Exception"/> is carried when the originating call site has
/// the exception object, so the dispatcher can capture the whole chain rather
/// than a pre-flattened copy of it. The queue never leaves the process, so
/// holding the instance costs nothing beyond the bounded queue itself.
/// </remarks>
public sealed record PendingErrorCapture(
    string ExceptionType,
    string? Message,
    string? StackTrace,
    ErrorReportSource Source,
    string? Origin,
    string? CorrelationId = null,
    Exception? Exception = null);

/// <summary>
/// Bounded hand-off between the Serilog sink and the dispatcher.
/// </summary>
/// <remarks>
/// Logging must never block on the database or on GitHub, and a failure storm
/// must not be able to exhaust memory. The queue is therefore bounded and drops
/// the <em>newest</em> item when full: during a storm the first errors are the
/// diagnostically useful ones, and everything after that is almost certainly
/// the same fingerprint, which deduplication would collapse anyway.
/// </remarks>
public sealed class ErrorReportQueue
{
    private const int Capacity = 128;

    private readonly Channel<PendingErrorCapture> _channel =
        Channel.CreateBounded<PendingErrorCapture>(new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });

    /// <summary>Enqueues without blocking; returns false when the queue is full.</summary>
    public bool TryEnqueue(PendingErrorCapture capture) => _channel.Writer.TryWrite(capture);

    /// <summary>Streams queued captures until cancellation.</summary>
    public IAsyncEnumerable<PendingErrorCapture> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
